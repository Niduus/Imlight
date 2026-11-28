/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * ZONE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages a game world zone including loading, player transfers, entity tracking, 
 * and communication between zone supervisors.
 * 
 * USAGE EXAMPLE:
 * var zoneActor = Context.ActorOf(Zone.Props("MyWorld/Hub", 12345, false));
 * 
 * NOTE:
 * Uses Akka actor model for asynchronous communication and supervisor pattern.
 * Zone loading is handled asynchronously with a timeout mechanism.
 * Mobile ID allocation is thread-safe using locks.
 *
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Supervisors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Represents a zone in the game world.
/// </summary>
public partial class Zone : ReceiveProtocolDispatcher, IWithTimers {

    private const ushort RESERVED_MOBILE_ID_MAX = ushort.MaxValue / 20; // 5% of the maximum mobile ID count is reserved for special objects.
    private static readonly Vector4 s_locationFailedGiveaway = new(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);

    /// <summary>
    /// The zone data as loaded from game client data.
    /// </summary>
    public WizZoneData ZoneData { get; private set; }

    /// <summary>
    /// What spiraldb-extra/ZoneObjects gave this copy of the zone (its encounters and exit); set before the
    /// supervisors load.
    /// </summary>
    internal ZoneObjects.Applied Extras { get; private set; } = ZoneObjects.Applied.None;

    /// <summary>
    /// Whether this zone is a private, per-owner instance (a dungeon/raid loaded under an
    /// <see cref="World.InstanceContainer"/>) rather than a shared public zone.
    /// </summary>
    public bool IsInstance { get; }

    /// <summary>
    /// The owner of the instance container this zone is in, or 0 for a shared zone.
    /// </summary>
    public ulong ContainerOwnerId { get; }

    /// <summary>
    /// Whether the zone has battle circles of its own; an instance with battles counts for Dungeon Recall.
    /// </summary>
    public bool HasBattles { get; private set; }

    /// <summary>
    /// How many battles against a ZoneObjects encounter's boss run in this zone now. A distinct spawner (the boss's
    /// minions) does not spawn while one runs, so the room's helpers are not doubled by spawned ones.
    /// </summary>
    internal int EncounterBattles;

    // Spawners a trigger of this zone switches off (ResSpawn with activate false): the game means them to keep coming back
    // on their timer until then, even in a dungeon copy (ZonePath.CanSpawn). Read when the zone's triggers load.
    internal HashSet<ulong> SpawnersSwitchedOffByTriggers;

    /// <summary>
    /// The zone path, formatted as it would be in the access pass.
    /// </summary>
    public string ZonePath { get; init; }

    /// <summary>
    /// The pretty name of the zone, as displayed in the game client.
    /// If the zone data is not loaded, this will be the same as <see cref="ZonePath"/>.
    /// </summary>
    public string ZoneName {
        get {
            if (ZoneData is null) {
                return ZonePath;
            }

            return ZoneData.m_zoneDisplayName;
        }
    }

    /// <summary>
    /// The state of every named object in this zone instance, read by trigger requirements.
    /// </summary>

    /// <summary>
    /// The tokens, counters and puzzle variables of this zone instance, read by trigger requirements.
    /// </summary>
    public ZoneScriptState ScriptState { get; }

    /// <summary>
    /// The zone's triggers by name, set when the zone loads: a Press X teleporter checks the same quest steps its
    /// trigger in the game's zone files asks for.
    /// </summary>
    public IReadOnlyDictionary<string, Trigger> TriggersByName { get; set; } = new Dictionary<string, Trigger>();

    // Spawned trigger objects, by trigger name and zone tag, so a volume can address that object. Every zone object's actor
    // notes itself here from its own thread while the zone loads, so a plain dictionary was corrupted ("Operations that
    // change non-concurrent collections must have exclusive access", ZoneEntity.ReceiveObjectLoadBegin, 2026-11-26).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IActorRef> _namedActors =
        new(StringComparer.OrdinalIgnoreCase);

    internal void NoteActor(string name, IActorRef actor) {
        if (string.IsNullOrEmpty(name) || actor is null) {
            return;
        }

        _namedActors[name] = actor;
        const string instanceSuffix = " instance";
        if (name.EndsWith(instanceSuffix, StringComparison.OrdinalIgnoreCase)) {
            _namedActors[name[..^instanceSuffix.Length]] = actor;
        }
    }

    // Set once by the trigger supervisor and read by the zone and its ladders from their own threads: a whole new list is
    // swapped in, never one filled in place.
    private volatile Imlight.CoreLib.Game.Zone.Components.TransportSpot[] _transportSpots = [];
    private readonly Dictionary<IActorRef, string> _transportPrompt = [];
    private readonly Dictionary<IActorRef, long> _transportPromptAt = [];
    // Written by the zone, read by a ladder's own actor (TryPairedTransportEnd).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Numerics.Vector3> _transportApproach =
        new(StringComparer.OrdinalIgnoreCase);

    // How far past the other ladder end's mesh to land when nobody has stood there yet (away from this end, off the cliff).
    private const float TRANSPORT_LANDING_STEP = 150f;

    internal void SetTransportSpots(IReadOnlyList<Imlight.CoreLib.Game.Zone.Components.TransportSpot> spots) {
        _transportSpots = [.. spots ?? []];
    }

    internal void OfferTransportAt(CoreObject player, IActorRef playerActor) {
        if (playerActor is null) {
            return;
        }

        if (player is null) {
            if (_transportPrompt.Remove(playerActor, out var leaving)) {
                _transportPromptAt.Remove(playerActor);
                ActorFor(leaving)?.Tell(new Imlight.CoreLib.Game.Zone.Components.TransportVolumePrompt {
                    Show = false,
                    PlayerActor = playerActor,
                    TriggerName = leaving,
                });
            }

            return;
        }

        var x = player.m_location.X;
        var y = player.m_location.Y;
        Imlight.CoreLib.Game.Zone.Components.TransportSpot best = null;
        var bestDistance = float.MaxValue;
        foreach (var spot in _transportSpots) {
            var dx = x - spot.X;
            var dy = y - spot.Y;
            var inside = spot.Length > 0f && spot.Width > 0f && spot.RadiusSq <= 0f
                ? InsideSpotBox(dx, dy, spot)
                : (dx * dx) + (dy * dy) <= spot.RadiusSq;
            if (inside && spot.UsesHeight && MathF.Abs(player.m_location.Z - spot.Z) > TRANSPORT_END_HEIGHT) {
                inside = false;
            }
            var distance = (dx * dx) + (dy * dy);
            if (inside && distance < bestDistance) {
                best = spot;
                bestDistance = distance;
            }
        }

        _transportPrompt.TryGetValue(playerActor, out var current);
        var same = string.Equals(current, best?.TriggerName, StringComparison.OrdinalIgnoreCase);
        var now = Environment.TickCount64;
        if (same && best is not null
            && _transportPromptAt.TryGetValue(playerActor, out var sentAt)
            && now - sentAt < 3000) {
            return;
        }

        if (same && best is not null) {
            var again = ActorFor(best.TriggerName, best.ObjectTag);
            again?.Tell(new Imlight.CoreLib.Game.Zone.Components.TransportVolumePrompt {
                Show = true,
                PlayerActor = playerActor,
                TriggerName = best.TriggerName,
            });
            _transportPromptAt[playerActor] = now;
            return;
        }

        if (!string.IsNullOrEmpty(current)) {
            ActorFor(current)?.Tell(new Imlight.CoreLib.Game.Zone.Components.TransportVolumePrompt {
                Show = false,
                PlayerActor = playerActor,
                TriggerName = current,
            });
            _transportPrompt.Remove(playerActor);
            _transportPromptAt.Remove(playerActor);
        }

        if (best is null) {
            return;
        }

        // Where a wizard really stands at this ladder end: the landing spot for anyone coming from the other end.
        // Only on that end's own level: the prompt check looks at x, y alone, and the ends sit over each other.
        if (MathF.Abs(player.m_location.Z - best.Z) < 300f) {
            _transportApproach[StripTransportSpotInstance(best.ObjectTag ?? best.TriggerName)] =
                new System.Numerics.Vector3(player.m_location.X, player.m_location.Y, player.m_location.Z);
        }

        var target = ActorFor(best.TriggerName, best.ObjectTag);
        if (target is null) {
            Logger.Information("Transport spot {0} ({1}) has no object yet.",
                Logger.Args(best.TriggerName, best.VolumeName));
            return;
        }

        target.Tell(new Imlight.CoreLib.Game.Zone.Components.TransportVolumePrompt {
            Show = true,
            PlayerActor = playerActor,
            TriggerName = best.TriggerName,
        });
        _transportPrompt[playerActor] = best.TriggerName;
        _transportPromptAt[playerActor] = Environment.TickCount64;
        Logger.Information("Transport prompt {0} for player at {1},{2}.",
            Logger.Args(best.ObjectTag ?? best.TriggerName, x, y));
    }

    // How far above or below a ladder end a wizard may stand and still be at it, and how far apart (across) and how far
    // apart in height the two ends of one ladder are at most and at least.
    private const float TRANSPORT_END_HEIGHT = 250f;
    private const float TRANSPORT_PAIR_ACROSS = 900f;
    private const float TRANSPORT_PAIR_RISE = 150f;

    /// <summary>
    /// The other end of the ladder the wizard at <paramref name="at"/> stands at, when its ends are not named as a pair
    /// (The Hive: KR-Hive-StartLadder-1 and KR-Hive-Startbottomladder, HOMToTopladder and HOMToBottomladder): the nearest
    /// other ladder end across that is well above or below this one. One ladder's ends stand nearly over each other.
    /// </summary>
    internal bool TryNearestTransportEnd(System.Numerics.Vector3 at, out string location) {
        location = null;
        var ends = _transportSpots.Where(spot => spot.UsesHeight).ToArray();
        var self = ends
            .Where(spot => MathF.Abs(at.Z - spot.Z) <= TRANSPORT_END_HEIGHT
                           && Square(at.X - spot.X) + Square(at.Y - spot.Y) <= spot.RadiusSq)
            .OrderBy(spot => Square(at.X - spot.X) + Square(at.Y - spot.Y))
            .FirstOrDefault();
        if (self is null) {
            return false;
        }

        var partner = ends
            .Where(spot => !ReferenceEquals(spot, self) && MathF.Abs(spot.Z - self.Z) >= TRANSPORT_PAIR_RISE
                           && Square(spot.X - self.X) + Square(spot.Y - self.Y) <= Square(TRANSPORT_PAIR_ACROSS))
            .OrderBy(spot => Square(spot.X - self.X) + Square(spot.Y - self.Y))
            .FirstOrDefault();
        if (partner is null) {
            return false;
        }

        // Land where wizards stand at that end, else a step out from its mesh (away from this end).
        var (x, y, z) = (partner.X, partner.Y, partner.Z);
        var partnerName = StripTransportSpotInstance(partner.ObjectTag ?? partner.TriggerName);
        if (_transportApproach.TryGetValue(partnerName, out var stood)
            && Square(stood.X - partner.X) + Square(stood.Y - partner.Y) <= partner.RadiusSq
            && MathF.Abs(stood.Z - partner.Z) <= TRANSPORT_END_HEIGHT) {
            (x, y, z) = (stood.X, stood.Y, stood.Z);
        }
        else {
            var (dx, dy) = (partner.X - self.X, partner.Y - self.Y);
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length > 1f) {
                x += dx / length * TRANSPORT_LANDING_STEP;
                y += dy / length * TRANSPORT_LANDING_STEP;
            }
        }

        location = string.Join(",", new[] { x, y, z, 0f }
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Logger.Information("Ladder end {0} in {1}: its other end is {2} (nearest end {3:0} across, {4:0} {5}).",
            Logger.Args(self.ObjectTag, ZonePath, partner.ObjectTag, MathF.Sqrt(Square(partner.X - self.X) + Square(partner.Y - self.Y)),
                MathF.Abs(partner.Z - self.Z), partner.Z > self.Z ? "up" : "down"));

        return true;
    }

    private static float Square(float value) => value * value;

    internal bool TryPairedTransportEnd(string objectTag, out string location) {
        location = null;
        var name = StripTransportSpotInstance(objectTag);
        var partnerName = PairedTransportName(name);
        if (string.IsNullOrEmpty(partnerName)) {
            return false;
        }

        var partner = _transportSpots.FirstOrDefault(spot =>
            string.Equals(StripTransportSpotInstance(spot.ObjectTag), partnerName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripTransportSpotInstance(spot.TriggerName), partnerName, StringComparison.OrdinalIgnoreCase));
        if (partner is null) {
            return false;
        }

        // The ladder end's own mesh sits in the cliff (Tyrian Gorge LadderE 2026-11-25: the landing check found it inside
        // a wall and moved the wizard back up top). Land where wizards stand at that end, else a step out from its mesh.
        var (x, y, z) = (partner.X, partner.Y, partner.Z);
        if (_transportApproach.TryGetValue(partnerName, out var stood)) {
            (x, y, z) = (stood.X, stood.Y, stood.Z);
        }
        else {
            var self = _transportSpots.FirstOrDefault(spot =>
                string.Equals(StripTransportSpotInstance(spot.ObjectTag), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(StripTransportSpotInstance(spot.TriggerName), name, StringComparison.OrdinalIgnoreCase));
            if (self is not null) {
                var (dx, dy) = (partner.X - self.X, partner.Y - self.Y);
                var length = MathF.Sqrt((dx * dx) + (dy * dy));
                if (length > 1f) {
                    x += dx / length * TRANSPORT_LANDING_STEP;
                    y += dy / length * TRANSPORT_LANDING_STEP;
                }
            }
        }

        location = string.Join(",", new[] { x, y, z, 0f }
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return true;
    }

    private static string StripTransportSpotInstance(string name) {
        const string suffix = " instance";
        return !string.IsNullOrEmpty(name) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    private static string PairedTransportName(string name) {
        name = StripTransportSpotInstance(name);
        if (string.IsNullOrEmpty(name) || name.Length < 2) {
            return null;
        }

        var prefix = name[..^1];
        var suffix = name[^1];
        if (char.IsLetter(suffix)) {
            var upper = char.ToUpperInvariant(suffix);
            char partner = (upper - 'A') % 2 == 0
                ? (char) (upper + 1)
                : (char) (upper - 1);
            if (char.IsLower(suffix)) {
                partner = char.ToLowerInvariant(partner);
            }
            return prefix + partner;
        }

        if (char.IsDigit(suffix)) {
            var digit = suffix - '0';
            var partner = (char) ('0' + (digit % 2 == 1 ? digit + 1 : digit - 1));
            return prefix + partner;
        }

        return null;
    }

    private static bool InsideSpotBox(float dx, float dy, Imlight.CoreLib.Game.Zone.Components.TransportSpot spot) {
        var yaw = spot.Yaw;
        var localX = (dx * MathF.Cos(yaw)) + (dy * MathF.Sin(yaw));
        var localY = (dy * MathF.Cos(yaw)) - (dx * MathF.Sin(yaw));
        return MathF.Abs(localX) <= spot.Length / 2f && MathF.Abs(localY) <= spot.Width / 2f;
    }

    internal IActorRef ActorFor(params string[] names) {
        foreach (var name in names) {
            if (string.IsNullOrEmpty(name)) {
                continue;
            }

            if (_namedActors.TryGetValue(name, out var actor)) {
                return actor;
            }

            const string instanceSuffix = " instance";
            if (name.EndsWith(instanceSuffix, StringComparison.OrdinalIgnoreCase)
                && _namedActors.TryGetValue(name[..^instanceSuffix.Length], out actor)) {
                return actor;
            }
        }

        return null;
    }

    public ITimerScheduler Timers { get; set; }

    private readonly uint _dynamicZoneId;

    /// <summary>The zone's dynamic id, as the game is sent it (it builds the zone file's objects with ids made from it).</summary>
    internal uint DynamicZoneId => _dynamicZoneId;
    private readonly Lock _mobileIdLock = new();
    private readonly List<IActorRef> _supervisors = [];
    private readonly IActorRef _sigilSupervisor;
    private readonly IActorRef _playerSupervisor;
    private readonly IActorRef _objectSupervisor;
    private readonly IActorRef _volumeSupervisor;
    private readonly IActorRef _triggerSupervisor;
    private readonly IActorRef _pathSupervisor;
    private readonly Stopwatch _zoneLoadTimer;
    private readonly Dictionary<IActorRef, IServerMessage> _pendingPlayerEvents = [];
    private readonly HashSet<ushort> _mobileIdMap = [];
    private readonly Dictionary<IActorRef, bool> _supervisorLoadResults = [];
    private readonly HashSet<GID> _criticalObjectIds = [];
    private bool _isLoading;
    private readonly HashSet<IActorRef> _players = [];
    private const string IDLE_EXPIRE_TIMER = "instance-idle-expire";
    private const int DEFAULT_INSTANCE_IDLE_MINUTES = 10;
    private const int RESET_WHEN_EMPTY_SECONDS = 5;
    // A fresh copy waits this long for its first wizard: one sent in while the server is busy can take more than
    // RESET_WHEN_EMPTY_SECONDS to arrive, and the copy was dropped just before (a Beastmoon arena, 2026-11-14).
    private const int FIRST_ARRIVAL_SECONDS = 60;
    private bool _everHadPlayer;

    // How often an empty room kept for the rest of its dungeon (KeepRoomsWhileInside) looks again.
    private const int HELD_ROOM_RECHECK_SECONDS = 10;
    private readonly List<ZONE_102_PROTOCOL.MSG_PLAYERMOVE> _pendingPlayerMoves = [];
    private readonly List<ZONE_102_PROTOCOL.MSG_CREATUREMOVE> _pendingCreatureMoves = [];

    /// <summary>
    /// Creates a new zone from the path of the zone, formatted as it would be in the access pass.
    /// </summary>
    /// <param name="zonePath">The path of the zone, formatted as it would be in the access pass.</param>
    /// <param name="dynamicZoneId">The dynamic zone ID of the zone.</param>
    /// <param name="isInstance">Whether this zone is a private, per-owner instance.</param>
    /// <param name="questProgress">The quest progress shared by the zones of one instance container; null for a zone of its own.</param>
    /// <param name="containerOwnerId">The owner of the instance container the zone is in; 0 for a shared zone.</param>
    public Zone(string zonePath, uint dynamicZoneId, bool isInstance, InstanceQuestProgress questProgress = null,
                ulong containerOwnerId = 0) {
        this.ScriptState = new ZoneScriptState(questProgress);
        this.ZonePath = zonePath;
        this.ContainerOwnerId = containerOwnerId;
        World.DungeonRecall.ZoneOpened(containerOwnerId, zonePath);
        this._dynamicZoneId = dynamicZoneId;
        this.IsInstance = isInstance;
        this._isLoading = true;
        this._zoneLoadTimer = new Stopwatch();

        _objectSupervisor = CreateSupervisor<ZoneObjectSupervisor>();
        _supervisors.Add(_objectSupervisor);
        _volumeSupervisor = CreateSupervisor<ZoneVolumeSupervisor>();
        _supervisors.Add(_volumeSupervisor);
        _triggerSupervisor = CreateSupervisor<ZoneTriggerSupervisor>();
        _supervisors.Add(_triggerSupervisor);
        _playerSupervisor = CreateSupervisor<ZonePlayerSupervisor>();
        _supervisors.Add(_playerSupervisor);
        _pathSupervisor = CreateSupervisor<ZonePathSupervisor>();
        _supervisors.Add(_pathSupervisor);
        _sigilSupervisor = CreateSupervisor<ZoneSigilSupervisor>();
        _supervisors.Add(_sigilSupervisor);

        Timers.StartPeriodicTimer("flush-moves", new ZONE_102_PROTOCOL.MSG_FLUSHMOVES(),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        _zoneLoadTimer.Restart();
        _isLoading = true;

        Logger.Debug("Zone {ZoneName} begins load.", Logger.Args(ZoneName));
    }

    // Props
    public static Props Props(string zonePath, uint dynamicZoneId, bool isInstance, InstanceQuestProgress questProgress = null,
                              ulong containerOwnerId = 0)
        => Akka.Actor.Props.Create(() => new Zone(zonePath, dynamicZoneId, isInstance, questProgress, containerOwnerId))
            .WithMailbox("akka.actor.mailbox.zone-priority");

    protected override void PreRestart(Exception reason, object message) {
        Logger.Error("Zone {ZoneName} restarts for: {Exception}", Logger.Args(ZoneName, reason));
        base.PreRestart(reason, message);
    }

    /// <summary>
    /// Closes the zone and stops all child actors.
    /// </summary>
    protected void CloseZone() {
        var msg = new ZONE_102_PROTOCOL.MSG_ZONECLOSED() {
            DynamicZoneId = _dynamicZoneId
        };

        foreach (var supervisor in _supervisors) {
            supervisor.Tell(msg);
        }

        // Inform any pending player events that the zone is closing.
        foreach (var (player, _) in _pendingPlayerEvents) {
            var rsp = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
                ZoneActorRef = Self,
                DynamicZoneId = _dynamicZoneId,
                ErrorCode = 1,
                MobileId = 0,
                ZoneDisplayName = ZoneName
            };

            player.Tell(rsp);
        }

        // Inform the supervisor (GameWorld) that the zone is closing.
        Context.Parent.Tell(msg);
        Context.Stop(Self);
    }

    #region Handlers

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    protected virtual void ReceiveZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        // A wizard is on the way in: the copy must not close under them while their game loads it (NoteArriving).
        NoteArriving();
        if (_isLoading) {
            _pendingPlayerEvents[Sender] = message;

            return;
        }

        ProcessZoneTransfer(message, Sender);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYER))]
    protected virtual void ReceiveAddPlayer(ZONE_102_PROTOCOL.MSG_ADDPLAYER message) {
        if (_isLoading) {
            _pendingPlayerEvents[message.PlayerActor] = message;

            return;
        }

        _players.Add(message.PlayerActor);
        _everHadPlayer = true;
        ScriptState.PlayerCount = _players.Count;
        NoteInstancedPlayer(message.PlayerActor, message.Wizard);
        NoteBattlegroundPlayerJoined(message.PlayerActor, message.Wizard);
        Timers.Cancel(IDLE_EXPIRE_TIMER);
        _heldForOtherRooms = false;
        InformZoneSupervisors(message.PlayerActor, message);
        RestoreRememberedSpawns(message.Wizard);
        OfferTransportAt(message.PlayerObject, message.PlayerActor);
        
        // Send response to confirm player was added
        var response = new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP {
            WizardGameObject = message.PlayerObject
        };
        Sender.Tell(response);
    }

    // The characters in this zone when it is an instance, so a teleport to them is refused (InstancedPlayers).
    private readonly Dictionary<IActorRef, ulong> _instancedCharIds = [];

    private void NoteInstancedPlayer(IActorRef playerActor, Wizard wizard) {
        if (!IsInstance || wizard is null) {
            return;
        }

        _instancedCharIds[playerActor] = wizard.CharId;
        World.InstancedPlayers.Enter(wizard.CharId, this);
        World.DungeonRecall.Entered(ContainerOwnerId, wizard.CharId, ZonePath, HasBattles);
    }

    protected override void PostStop() {
        ZoneObjects.Unregister(Self);
        ZoneStates.Unregister(Self);
        foreach (var charId in _instancedCharIds.Values) {
            World.InstancedPlayers.Leave(charId, this);
            World.DungeonRecall.Left(ContainerOwnerId, charId);
        }
        World.DungeonRecall.ZoneClosed(ContainerOwnerId, ZonePath);

        base.PostStop();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED))]
    private void ReceiveZoneObjectsChanged(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REMOVEPLAYER))]
    protected virtual void ReceiveRemovePlayer(ZONE_102_PROTOCOL.MSG_REMOVEPLAYER message) {
        if (_isLoading) {
            _pendingPlayerEvents.Remove(message.PlayerActor);
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());

            return;
        }

        if (!_players.Contains(message.PlayerActor)) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());

            return;
        }

        // The leaver is counted until the supervisors are told: the Players target is gated on a non-empty set.
        OfferTransportAt(null, message.PlayerActor);
        ForgetClientBuiltRemovals(message.PlayerActor);
        InformZoneSupervisors(message.PlayerActor, message);
        NoteBattlegroundPlayerLeft(message.PlayerActor);
        _players.Remove(message.PlayerActor);
        ScriptState.PlayerCount = _players.Count;
        if (_instancedCharIds.Remove(message.PlayerActor, out var charId)) {
            World.InstancedPlayers.Leave(charId, this);
            World.DungeonRecall.Left(ContainerOwnerId, charId);
        }
        ReleaseObjectIdentifier(message.MobileId);
        ScheduleIdleExpiryIfEmpty();
        Sender.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP());
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERMOVE))]
    protected virtual void ReceivePlayerMove(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        if (_isLoading) {
            _pendingPlayerEvents[message.PlayerActor] = message;
            return;
        }

        if (!IsMobileIdInUse(message.PlayerObject.m_nMobileID)) {
            return;
        }

        _pendingPlayerMoves.Add(message);
        NoteBattlegroundPlayerMoved(message);
        QuestSpotGoals.OnPlayerMoved(ZonePath, message);
        OfferTransportAt(message.PlayerObject, message.PlayerActor);
        RemoveClientBuiltObjects(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CREATUREMOVE))]
    protected virtual void ReceiveCreatureMove(ZONE_102_PROTOCOL.MSG_CREATUREMOVE message) {
        if (_players.Count == 0) {
            return;
        }

        if (!IsMobileIdInUse(message.CreatureObject.m_nMobileID)) {
            return;
        }

        _pendingCreatureMoves.Add(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_FLUSHMOVES))]
    private void ReceiveFlushMoves(ZONE_102_PROTOCOL.MSG_FLUSHMOVES _) {
        if (_pendingPlayerMoves.Count > 0) {
            var moves = _pendingPlayerMoves.ToArray();
            _pendingPlayerMoves.Clear();
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = moves,
                Targets = ZoneBroadcastTarget.Objects
                        | ZoneBroadcastTarget.Volumes
                        | ZoneBroadcastTarget.Sigils
                        | ZoneBroadcastTarget.Paths,
            });
        }

        if (_pendingCreatureMoves.Count > 0) {
            var moves = _pendingCreatureMoves.ToArray();
            _pendingCreatureMoves.Clear();
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = moves,
                Targets = ZoneBroadcastTarget.Sigils,
            });
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    private void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        Logger.Debug("Zone {ZoneName} client data gathered.", Logger.Args(ZoneName));

        if (message.Error) {
            Logger.Error("Zone {ZoneName} failed to load because {ErrorMessage}", 
                Logger.Args(ZoneName, message.ErrorMessage));

            CloseZone();

            return;
        }

        _zoneLoadTimer.Restart();

        // Objects and creatures added from spiraldb-extra/ZoneObjects join the zone's own before the supervisors make
        // them; a copy loaded for a variant (an added sigil's instance) takes that variant's file too.
        Extras = ZoneObjects.AddTo(ZonePath, message.Variant, message);
        // A change to those files (the Customs tab moving an object) reaches this copy while it runs.
        ZoneObjects.Register(Self, ZonePath, message.Variant);
        // Its live state, for the bots' floor and puzzle duties (BotShared/BotFloors).
        ZoneStates.Register(Self, ScriptState);
        ZoneData = message.ZoneData;
        HasBattles = ZoneData?.m_objectList?.Any(o => o is CombatSigilObjectInfo) == true;
        SetUpRaid(message.TriggerData);
        SetUpBattleground(message);

        // Inform each supervisor of the loaded zone data. They are expected to give a reply
        // to inform the zone that they have loaded their data.
        _supervisorLoadResults.Clear();
        foreach (var supervisor in _supervisors) {
            _supervisorLoadResults[supervisor] = false;
            supervisor.Tell(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS))]
    private void ReceiveSupervisorLoadComplete(ZONE_102_PROTOCOL.MSG_ZONESUPERVISORLOADRESULTS message) {
        _supervisorLoadResults[Sender] = true;

        Logger.Debug("Zone {ZoneName} supervisor {SupervisorName} loaded.", Logger.Args(ZoneName, message.SupervisorName));

        // If the supervisor load results are all true, then the zone is fully loaded.
        if (_supervisorLoadResults.All(x => x.Value)) {
            // Finally process any pending player events that couldn't occur because the zone was still loading.
            ProcessQueue();

            _zoneLoadTimer.Stop();
            Logger.Information("Zone {ZoneName} loaded in {Time}ms.", Logger.Args(ZoneName, _zoneLoadTimer.ElapsedMilliseconds));
            _isLoading = false;
            ScheduleIdleExpiryIfEmpty();
            Context.Parent.Tell(new World.GameWorld.ZoneReady { ZonePath = ZonePath });

            var startMsg = new ZONE_102_PROTOCOL.MSG_ZONESTART();

            foreach (var supervisor in _supervisors) {
                supervisor.Tell(startMsg);
            }
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADTIMER))]
    private void ReceiveZoneTimerEnd() {
        if (_isLoading) {
            Logger.Error("Zone {ZoneName} failed to load within the timeout.", Logger.Args(ZoneName));
            CloseZone();
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT))]
    private void ReceiverRegisterCriticalObject(ZONE_102_PROTOCOL.MSG_REGISTERCRITICALOBJECT message) 
        => _criticalObjectIds.Add(message.ObjectID);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEID))]
    private void ReceiveGetMobileId() {
        var rsp = new ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEIDRSP {
            MobileID = GenerateReservedObjectIdentifier()
        };
        Sender.Tell(rsp);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEDATA))]
    private void ReceiveQueryZoneData() {
        Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYZONEDATARSP {
            ZoneData = ZoneData
        });
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_POSTEVENT))]
    private void ReceiveTriggerPost(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        Logger.Verbose("Zone {ZoneName} received post event {EventName}.", Logger.Args(ZoneName, message.EventName));

        if (message.RetryRefused) {
            if (message.PlayerActor is not null && _players.Contains(message.PlayerActor)) {
                _triggerSupervisor.Tell(message);
            }

            return;
        }

        // A result list that waited can outlive its player; once the player has left, the event is the zone's own.
        if (message.PlayerActor is not null && !_players.Contains(message.PlayerActor)) {
            message = new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = message.EventName,
                PlayerSpawned = message.PlayerSpawned,
                Adjectives = message.Adjectives,
            };
        }

        foreach (var supervisor in _supervisors) {
            supervisor.Tell(message);
        }

        // A Beastmoon arena's control points follow who walks in and out of them.
        NoteBattlegroundEvent(message);

        // Goals that list the event complete for every player in this zone instance.
        DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_ZONEEVENTFORQUESTS { EventName = message.EventName.ToString() }],
            Targets = ZoneBroadcastTarget.Players,
        });
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT))]
    private void ReceiveModifyTriggerObject(ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT message)
        => _triggerSupervisor.Forward(message);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT))]
    private void ReceiveRemoveTriggerObject(ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT message) {
        _triggerSupervisor.Forward(message);
        _objectSupervisor.Forward(message);
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER))]
    private void ReceiveSpawnHouseTrainer(SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER message)
        => _objectSupervisor.Forward(message);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDTRIGGEROBJECT))]
    private void ReceiveAddTriggerObject(ZONE_102_PROTOCOL.MSG_ADDTRIGGEROBJECT message)
        => _objectSupervisor.Forward(message);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYINSTANCEQUESTS))]
    private void ReceiveQueryInstanceQuests(ZONE_102_PROTOCOL.MSG_QUERYINSTANCEQUESTS message) {
        if (!IsInstance) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYINSTANCEQUESTSRSP());

            return;
        }

        var progress = ScriptState.SnapshotQuestProgress();
        progress.Zones = InstanceQuestProgress.ZonesSharingQuests(ZonePath, progress.Zones);
        Sender.Tell(progress);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CHECKSTEPSPAWNS))]
    private void ReceiveCheckStepSpawns(ZONE_102_PROTOCOL.MSG_CHECKSTEPSPAWNS message)
        => _triggerSupervisor.Forward(message);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_NOTEINSTANCEGOALCOUNT))]
    private void ReceiveNoteInstanceGoalCount(ZONE_102_PROTOCOL.MSG_NOTEINSTANCEGOALCOUNT message) {
        if (IsInstance) {
            ScriptState.NoteQuestGoalCount(message.CharId, message.QuestName, message.GoalName, message.Count);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CLAIMINSTANCEQUEST))]
    private void ReceiveClaimInstanceQuest(ZONE_102_PROTOCOL.MSG_CLAIMINSTANCEQUEST message) {
        if (!IsInstance) {
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_CLAIMINSTANCEQUESTRSP { First = true });

            return;
        }

        var first = ScriptState.TryClaimQuestStep(message.Kind, message.QuestName, message.GoalName);
        Sender.Tell(new ZONE_102_PROTOCOL.MSG_CLAIMINSTANCEQUESTRSP { First = first });

        if (first && message.Kind == InstanceQuestClaimKind.GoalComplete) {
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_INSTANCEGOALCOMPLETED {
                    Origin = message.Origin,
                    QuestName = message.QuestName,
                    GoalName = message.GoalName,
                }],
                Targets = ZoneBroadcastTarget.Players,
            });
        }

        // Everyone in the instance, in this room or another of the dungeon's rooms, is brought to the same quests and
        // steps (Niduus, 2026-11-04: "everyone is to be on the same step at all times").
        if (first && message.Kind != InstanceQuestClaimKind.GoalStart) {
            var changed = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_INSTANCEQUESTSCHANGED()],
                Targets = ZoneBroadcastTarget.Players,
            };
            DispatchBroadcast(changed);
            Context.Parent.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_INSTANCEQUESTSCHANGED()],
                Targets = ZoneBroadcastTarget.Players,
            }, Self);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONESCRIPTRESULT))]
    private void ReceiveZoneScriptResult(ZONE_102_PROTOCOL.MSG_ZONESCRIPTRESULT message)
        => ScriptState.Apply(message.Result, message.PlayerGameObject?.m_globalID.Full ?? 0);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTSTAGEDCINEMATIC))]
    private void ReceiveStartStagedCinematic(ZONE_102_PROTOCOL.MSG_STARTSTAGEDCINEMATIC message)
        => _triggerSupervisor.Forward(message);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY))]
    private void ReceiveQueryEntityObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET))]
    private void ReceiveQueryNearestDuelTarget(ZONE_102_PROTOCOL.MSG_QUERYNEARESTDUELTARGET message) {
        foreach (var supervisor in _supervisors) {
            supervisor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONS))]
    private void ReceiveQueryEncounterMinions(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONS message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENCOUNTERBATTLEENDED))]
    private void ReceiveEncounterBattleEnded(ZONE_102_PROTOCOL.MSG_ENCOUNTERBATTLEENDED message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        DispatchBroadcast(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL))]
    private void ReceiveRequestCombatSigil(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTPVPMATCH))]
    private void ReceiveStartPvPMatch(ZONE_102_PROTOCOL.MSG_STARTPVPMATCH message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_FORFEITPVPPARTICIPANT))]
    private void ReceiveForfeitPvPParticipant(ZONE_102_PROTOCOL.MSG_FORFEITPVPPARTICIPANT message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SPAWNENTITY))]
    private void ReceiveSpawnEntity(ZONE_102_PROTOCOL.MSG_SPAWNENTITY message)
        => _supervisors.ForEach(supervisor => supervisor.Forward(message));

    #endregion

    private void DispatchBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if ((message.Targets & ZoneBroadcastTarget.Players) != 0) {
            if (_players.Count > 0) _playerSupervisor.Forward(message);
        }
        if ((message.Targets & ZoneBroadcastTarget.Objects)  != 0) _objectSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Volumes)  != 0) _volumeSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Triggers) != 0) _triggerSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Paths)    != 0) _pathSupervisor.Forward(message);
        if ((message.Targets & ZoneBroadcastTarget.Sigils)   != 0) _sigilSupervisor.Forward(message);
    }

    private IActorRef CreateSupervisor<T>() where T : ActorBase {
        var props = Akka.Actor.Props.Create(() => (T) Activator.CreateInstance(typeof(T), this));
        
        return Context.ActorOf(props, typeof(T).Name);
    }

    private void RestoreRememberedSpawns(Wizard wizard) {
        foreach (var spawnId in RememberedSpawns.Get(wizard, ZonePath)) {
            DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Messages = [new ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN {
                    SpawnObjectID = (uint) spawnId,
                    OnlyIfAbsent = true,
                }],
                Targets = ZoneBroadcastTarget.Paths,
            });
        }
    }

    private void InformZoneSupervisors(IActorRef player, IServerMessage message) {
        DispatchBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Sender = player,
            Messages = [message],
            Targets = ZoneBroadcastTarget.All,
        });
    }

    private Vector4 GetLocationFromString(ByteString location) {
        var actualLocation = Vector4.Zero;

        var parsedLoc = Util.GetVectorFromCompactString(location);
        if (parsedLoc.X != 0 || parsedLoc.Y != 0 || parsedLoc.Z != 0) {
            actualLocation = parsedLoc;

            // A spot given by x, y, z (TP buttons, teleports to a wizard, going back after a battle) is checked so the
            // wizard never lands inside a wall, a pillar or furniture (Niduus 2026-11-12, see SafeLanding).
            if (SafeLanding.Enabled) {
                var wanted = new System.Numerics.Vector3(parsedLoc.X, parsedLoc.Y, parsedLoc.Z);
                var landing = SafeLanding.Check(ZonePath, wanted);
                if (landing is { Moved: true } moved) {
                    Logger.Information("Safe landing: {0} at {1} is {2}; the wizard lands at {3} instead.",
                        Logger.Args(ZonePath, wanted, moved.Why, moved.Spot));
                    actualLocation = new Vector4(moved.Spot.X, moved.Spot.Y, moved.Spot.Z, parsedLoc.W);
                }
                else if (landing is null && ZoneData.m_locationList.FirstOrDefault(x => x.m_locName == "Start") is { } start) {
                    Logger.Information("Safe landing: {0} at {1} has no open floor near it; the wizard goes to the area's start.",
                        Logger.Args(ZonePath, wanted));
                    actualLocation = (Vector4) start.m_location;
                    actualLocation.W = start.m_direction;
                }
            }
        }
        else {
            var searchedLoc = ZoneData.m_locationList.FirstOrDefault(x => x.m_locName == location);
            if (searchedLoc is null) {
                var start = ZoneData.m_locationList.FirstOrDefault(x => x.m_locName == "Start");
                if (start is not null) {
                    actualLocation = (Vector4) start.m_location;
                    actualLocation.W = start.m_direction;

                    return actualLocation;
                }

                return s_locationFailedGiveaway;
            }

            actualLocation = (Vector4) searchedLoc.m_location;
            actualLocation.W = searchedLoc.m_direction;
        }

        return actualLocation;
    }

    private void ProcessZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message, IActorRef sender) {
        var rsp = new ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP {
            ZoneActorRef = Self,
            DynamicZoneId = _dynamicZoneId,
            ErrorCode = 0,
            MobileId = GenerateObjectIdentifier(),
            ZoneDisplayName = ZoneName,
            CriticalObjects = [.. _criticalObjectIds],
            IsInstance = IsInstance
        };

        var actualLocation = GetLocationFromString(message.DestinationLocation);

        rsp.Location = (Vector3) actualLocation;
        rsp.Orientation = actualLocation.W;
        sender.Tell(rsp);
    }

    private void ProcessQueue() {
        foreach (var (playerActor, pendingEvent) in _pendingPlayerEvents) {
            if (pendingEvent is ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer) {
                ProcessZoneTransfer(transfer, playerActor);
            }
            else if (pendingEvent is ZONE_102_PROTOCOL.MSG_ADDPLAYER addPlayer) {
                _players.Add(playerActor);
                _everHadPlayer = true;
                ScriptState.PlayerCount = _players.Count;
                NoteInstancedPlayer(playerActor, addPlayer.Wizard);
                NoteBattlegroundPlayerJoined(playerActor, addPlayer.Wizard);
                Timers.Cancel(IDLE_EXPIRE_TIMER);
                _heldForOtherRooms = false;
                InformZoneSupervisors(playerActor, addPlayer);
                RestoreRememberedSpawns(addPlayer.Wizard);
                OfferTransportAt(addPlayer.PlayerObject, playerActor);
                
                // Send response to confirm player was added
                var response = new ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP {
                    WizardGameObject = addPlayer.PlayerObject
                };
                playerActor.Tell(response);
            }
            else {
                InformZoneSupervisors(playerActor, pendingEvent);
            }
        }
        
        _pendingPlayerEvents.Clear();
    }

    // Kept because someone was in another room of the dungeon (ReceiveInstanceIdleExpire).
    private bool _heldForOtherRooms;

    // A wizard was sent here and has not been added yet (between the transfer and the game's attach). The empty-room
    // timer of the last visit could fire in that gap and close the room under them: Idunn walked out of the Robe Shop
    // and straight back in, the 5-second timer of her leaving fired 65 ms after her attach, and she stood in a closed
    // room for 18 minutes where no exit worked and her friends saw her offline (Niduus, 2026-11-17: "one of TCO7 is
    // offline?").
    private const int ARRIVING_SECONDS = 20;
    private DateTime _arrivingUntil = DateTime.MinValue;

    private void NoteArriving() {
        if (!IsInstance) {
            return;
        }

        _arrivingUntil = DateTime.UtcNow.AddSeconds(ARRIVING_SECONDS);
        if (_players.Count == 0 && !_isLoading) {
            Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
                TimeSpan.FromSeconds(ARRIVING_SECONDS + 1));
        }
    }

    private void ScheduleIdleExpiryIfEmpty() {
        // Instance.IdleMinutes of 0 or less disables the expiry; a join cancels the timer.
        if (!IsInstance || _players.Count > 0 || _isLoading) {
            return;
        }

        // A wizard's Dungeon Recall into this copy keeps it until the recall runs out (DungeonRecall), then this asks again.
        var heldFor = (long) World.DungeonRecall.HeldUntil(ContainerOwnerId) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (heldFor > 0) {
            Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
                TimeSpan.FromSeconds(heldFor + 2));

            return;
        }

        // [Instance] ResetWhenEmpty: a dungeon starts fresh once everyone has left, after a few seconds so a
        // player still loading back in is not caught by it. A copy no one has entered yet waits for its first wizard.
        if (ConfigurationManager.GetValue("Instance.ResetWhenEmpty", true)) {
            Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
                TimeSpan.FromSeconds(_everHadPlayer ? RESET_WHEN_EMPTY_SECONDS : FIRST_ARRIVAL_SECONDS));

            return;
        }

        var minutes = ConfigurationManager.GetValue("Instance.IdleMinutes", DEFAULT_INSTANCE_IDLE_MINUTES);
        if (minutes <= 0) {
            return;
        }

        Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
            TimeSpan.FromMinutes(minutes));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE))]
    private void ReceiveInstanceIdleExpire(ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE message) {
        if (!IsInstance || _players.Count > 0) {
            return;
        }

        // Someone is still loading in (NoteArriving): wait for them, then look again.
        var arriving = _arrivingUntil - DateTime.UtcNow;
        if (!message.Force && arriving > TimeSpan.Zero) {
            Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
                arriving + TimeSpan.FromSeconds(1));

            return;
        }

        // [Instance] KeepRoomsWhileInside: a room of a dungeon stays as it is while anyone is still in another room of the
        // same copy. The Emperor's Retreat was thrown away the moment a wizard walked into one of its towers, so the
        // lever the tower boss's quest step unlocks was unlocked in nothing, and the fresh Retreat they came back to had
        // it locked for good (Niduus, 2026-11-13: "not performing correct checks thus soft locking it"). Bosses beaten
        // in a tower came back too. Once everyone has left the whole dungeon, the usual reset below applies.
        if (!message.Force && ConfigurationManager.GetValue("Instance.KeepRoomsWhileInside", true)
            && World.DungeonRecall.AnyoneInside(ContainerOwnerId)) {
            if (!_heldForOtherRooms) {
                _heldForOtherRooms = true;
                Logger.Information("Instance zone {ZoneName} is kept: someone is still in another room of the same dungeon.",
                    Logger.Args(ZonePath));
            }

            Timers.StartSingleTimer(IDLE_EXPIRE_TIMER, new ZONE_102_PROTOCOL.MSG_INSTANCEIDLEEXPIRE(),
                TimeSpan.FromSeconds(HELD_ROOM_RECHECK_SECONDS));

            return;
        }

        // The rooms it waited for are empty now: the usual wait starts from here.
        if (_heldForOtherRooms && !message.Force) {
            _heldForOtherRooms = false;
            ScheduleIdleExpiryIfEmpty();

            return;
        }

        // A fresh run (Force: the owner walked in through the dungeon's front again) is not held by a Dungeon Recall into
        // the last run: the recall was for that run, and a room kept for it is what the fresh run walks into next. Cyndr,
        // 2026-11-22 08:06-08:16: he pressed the Commons button in the Plaza of Conquests gauntlet's third room (recall
        // for 10 minutes), walked in again at 08:10, and only the first room was new; rooms 2 and 3 were the old ones,
        // whose door stays shut for good once its Irate Banshee is beaten, so he stood by the same shut door again.
        if (!message.Force && World.DungeonRecall.HeldUntil(ContainerOwnerId) > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) {
            ScheduleIdleExpiryIfEmpty();

            return;
        }

        Logger.Information("Instance zone {ZoneName} idle with no players, dropping it.", Logger.Args(ZoneName));
        Context.Parent.Tell(new ZONE_102_PROTOCOL.MSG_DROPINSTANCEZONE { ZoneName = ZonePath });
    }

    private ushort GenerateObjectIdentifier() {
        lock (_mobileIdLock) {
            // Find first available ID.
            for (ushort i = RESERVED_MOBILE_ID_MAX + 1; i <= ushort.MaxValue; i++) {
                if (!_mobileIdMap.Contains(i)) {
                    _mobileIdMap.Add(i);
                    return i;
                }
            }

            throw new InvalidOperationException("Failed to generate a mobile ID.");
        }
    }

    internal ushort ReserveMobileId() => GenerateReservedObjectIdentifier();

    private bool IsMobileIdInUse(ushort mobileId) {
        lock (_mobileIdLock) {
            return _mobileIdMap.Contains(mobileId);
        }
    }

    private ushort GenerateReservedObjectIdentifier() {
        lock (_mobileIdLock) {
            // Find first available ID in reserved range.
            for (ushort i = 1; i <= RESERVED_MOBILE_ID_MAX; i++) {
                if (!_mobileIdMap.Contains(i)) {
                    _mobileIdMap.Add(i);
                    return i;
                }
            }

            throw new InvalidOperationException("Failed to generate a reserved mobile ID - all IDs in use.");
        }
    }

    private void ReleaseObjectIdentifier(ushort mobileId) {
        // Derfer the actual release on a short cooldown. This is a direct fix
        // for https://github.com/Revive101/Imlight/issues/131, I think.
        // Without this, a rapid leave+join cycle can cause the new player's
        // MSG_NEWOBJECT to race ahead of the old player's MSG_REMOVEOBJECT.
        var key = $"release-mobile-{mobileId}";
        Timers.StartSingleTimer(key, new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID {
            MobileId = mobileId
        }, TimeSpan.FromSeconds(2));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID))]
    private void ReceiveReleaseMobileId(ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID message) {
        lock (_mobileIdLock) {
            _mobileIdMap.Remove(message.MobileId);
        }
    }

}