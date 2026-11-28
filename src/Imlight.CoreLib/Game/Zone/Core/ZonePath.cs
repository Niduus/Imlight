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
 * PATH AND SPAWN MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages entity spawn points and movement paths within a zone, handling
 * creature creation, path following, and respawn mechanics.
 * 
 * USAGE EXAMPLE:
 * // Created by the Zone system during zone loading
 * // PathObjectTemplate template = ...
 * // List<NodeObject> nodes = ...
 * // List<SpawnObject> creatures = ...
 * var pathActor = Context.ActorOf(Props.Create(() => 
 *     new ZonePath(template, nodes, creatures, zoneRef, zone)));
 * 
 * NOTE:
 * Handles timed creature spawning based on configuration in SpawnObjects.
 * Enforces spawn limits and ensures proper creature distribution.
 * Uses timer-based respawn intervals for continuous population management,
 * except in an instanced Zone, where a spawn point that has lost a creature
 * is never repopulated.
 *
 * TODO:
 *
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.MessageLayer.Generated;
using Imcodec.Types;
using Imlight.CoreLib.Game.Randomization;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Manages a number of <see cref="ZoneEntity"/>s that exist in the game world following a path.
/// </summary>
/// <param name="template">The template of the path.</param>
/// <param name="nodes">The nodes of the path.</param>
/// <param name="creatures">The creatures that follow the path.</param>
/// <param name="zoneRef">The reference to the zone actor.</param>
/// <param name="zone">The zone that the path is in.</param>
public sealed class ZonePath : ZoneEntity {

    private const string CREATURE_SPAWN_INTERVAL_LOCK = "CREATURE_SPAWN_INTERVAL_LOCK";
    private const uint MAX_SPAWNS_ALLOWED = 25;
    private const uint OBJECT_CREATION_TIMEOUT_IN_MS = 5000;
    private const int FILL_STAGGER_IN_MS = 250;

    private readonly PathObjectTemplate _template;
    private readonly List<NodeObject> _nodes;
    private readonly List<SpawnObject> _creatures;
    // SpawnObject is a record with value equality and m_active changes at runtime, so these are keyed by reference.
    private readonly Dictionary<SpawnObject, byte> _creatureCount = new(ReferenceEqualityComparer.Instance);
    private readonly List<IActorRef> _creatureActors = [];
    private readonly Dictionary<ulong, SpawnObject> _spawnObjectInfo = [];
    // The spawn list template each living creature was picked as, for a ZoneObjects distinct spawner.
    private readonly Dictionary<ulong, ulong> _spawnedTemplate = [];
    private readonly Dictionary<IActorRef, (SpawnObject Spawner, GID ObjectId, string Name)> _loadingCreatures = [];
    private readonly HashSet<SpawnObject> _defeatedInInstance = new(ReferenceEqualityComparer.Instance);
    // Which creature used up each spawn point in _defeatedInInstance (by global id), so a lost battle against an
    // encounter's boss can give the spawn point back (MSG_ENCOUNTERBATTLEENDED).
    private readonly Dictionary<ulong, SpawnObject> _defeatedIds = [];
    // ZoneObjects distinct spawners that wanted to spawn while a battle against the zone's encounter ran.
    private readonly HashSet<SpawnObject> _heldDistinct = new(ReferenceEqualityComparer.Instance);

    // Creatures that won their battle because everyone fled. They are deleted like defeated ones, but their
    // spawn point is not used up: it spawns again (a fresh roll with the randomizer) a few seconds later.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> Spared = new();
    private const int SPARED_RESPAWN_DELAY_IN_SECONDS = 10;
    // Creatures that joined a battle (CombatCreatureAIComponent), by global id: the On and Off tab leaves them in it.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> Fighting = new();
    // Each living creature's actor, object, the template it is and the custom it is (0 for none), for the On and Off tab.
    private readonly Dictionary<ulong, (IActorRef Actor, CoreObject Object, ulong Template, int Custom)> _living = [];
    // Which spawn items of each spawner were off last time, so a spawner whose creatures were turned on again fills up.
    private readonly Dictionary<SpawnObject, string> _offSign = new(ReferenceEqualityComparer.Instance);
    // Spawners a ResSpawn switched off (m_activate false).
    private readonly HashSet<SpawnObject> _deactivated = new(ReferenceEqualityComparer.Instance);
    // Players that caused a spawn (quest or trigger result); the creature checks aggro against them once loaded.
    private readonly Dictionary<IActorRef, ZONE_102_PROTOCOL.MSG_PLAYERMOVE> _aggroOnLoad = [];
    // Players in the zone, so a creature spawned after they joined is told about them like one that was there.
    private readonly Dictionary<IActorRef, ZONE_102_PROTOCOL.MSG_ADDPLAYER> _players = [];
    private bool _randomizeCreatures
        => ConfigurationManager.Settings["April Fools.RandomizeCreatures"].AsBool();

    // ctor
    public ZonePath(PathObjectTemplate template, List<NodeObject> nodes, List<SpawnObject> creatures, IActorRef zoneRef, Zone zone)
        : base(null, null, null, zoneRef, zone) {
        this._template = template;
        this._nodes = nodes;
        this._creatures = creatures;
        base.ZoneRef = zoneRef;
        base.Zone = zone;
        // Where its creatures spawn and walk, for the overlay's minimap and the route finder (Tools.MobSpawnAreas).
        Imlight.CoreLib.Game.Tools.MobSpawnAreas.Placed(zoneRef, template, nodes, creatures);

        // Begin the creature spawn interval.
        for (var i = 0; i < creatures.Count; i++) {
            var spawnObject = creatures[i];
            _creatureCount.Add(spawnObject, 0);
            _offSign[spawnObject] = OffSign(spawnObject);

            if (!spawnObject.m_active) {
                continue;
            }

            StartSpawnTimer(spawnObject, i, fillNow: true);
        }
    }

    private void StartSpawnTimer(SpawnObject spawnObject, int index, bool fillNow = false) {
        var timerKey = SpawnTimerKey(spawnObject, index);
        var msg = new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL { SpawnObject = spawnObject };
        var interval = TimeSpan.FromSeconds(spawnObject.m_spawnTime);

        // Without an interval the spawn point fills up to its maximum at once and never refills.
        if (interval <= TimeSpan.Zero) {
            for (var i = 0; i < Math.Max(1, (int) spawnObject.m_maxNumberOfSpawns); i++) {
                Timers.StartSingleTimer($"{timerKey}_{i}", msg, TimeSpan.Zero);
            }

            return;
        }

        // Otherwise, start the interval. Each tick brings up to the spawner's respawn rate (m_respawnRate) back, as the
        // game files mean it: a Dragonspire ore spawner with 5 at most and a 20 minute interval brings all 5 back at
        // once, Unicorn Way's ghosts 2 every 30 seconds. One per tick left busy streets thin (Niduus, 2026-10-29: "some
        // streets don't really have a lot of mob spawns"): each battle takes its monsters, and those that walk into a
        // full battle vanish, faster than one a tick brought back. It still ticks in an instanced zone; CanSpawn is what
        // stops a defeated spawn point from ever refilling there (see _defeatedInInstance).
        var wave = new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL {
            SpawnObject = spawnObject,
            Count = (int) Math.Clamp(spawnObject.m_respawnRate, 1u, MAX_SPAWNS_ALLOWED),
        };

        // The first spawn comes as the zone loads, as a spawner without an interval's does: a housing gauntlet's boss
        // (Rattlebones, whose spawner has an interval) came 5 seconds after the zone, while the Fairy Queen (none) stood
        // there from the start (Niduus, 2026-10-24: "Rattlebones does spawn but not immediate").
        if (!fillNow) {
            Timers.StartPeriodicTimer(timerKey, wave, TimeSpan.Zero, interval);

            return;
        }

        // As the zone loads, every spawner fills up to its maximum, as on live, instead of one creature an interval: a
        // street had a single monster per spawn point for minutes after the server started. They come a moment apart
        // so a big street does not make all of them in the same instant. Later ones keep the interval.
        var count = (int) Math.Clamp(spawnObject.m_maxNumberOfSpawns, 1u, MAX_SPAWNS_ALLOWED);
        for (var i = 0; i < count; i++) {
            Timers.StartSingleTimer($"{timerKey}_fill_{i}", msg, TimeSpan.FromMilliseconds(i * FILL_STAGGER_IN_MS));
        }

        Timers.StartPeriodicTimer(timerKey, wave, interval, interval);
    }

    private static string SpawnTimerKey(SpawnObject spawnObject, int index) {
        // Spawners with a zone level range are alternatives (one boss per level band) and share one timer, so
        // the last of them runs. todo: pick the band from the zone's level once zones carry one.
        var isLevelVariant = spawnObject.m_zoneLevelMin != 0 || spawnObject.m_zoneLevelMax != 0;

        return isLevelVariant ? CREATURE_SPAWN_INTERVAL_LOCK : $"{CREATURE_SPAWN_INTERVAL_LOCK}_{index}";
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN))]
    protected override void ReceiveObjectLoadBegin()
        => Sender.Tell(new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS());

    [MessageHandler(typeof(IServerMessage))]
    protected override void ReceiveElse(IServerMessage message) {
        if (message is ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS or ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT) {
            return;
        }

        if (message is ZONE_102_PROTOCOL.MSG_ADDPLAYER addPlayer) {
            _players[addPlayer.PlayerActor] = addPlayer;
        }
        else if (message is ZONE_102_PROTOCOL.MSG_REMOVEPLAYER removePlayer) {
            _players.Remove(removePlayer.PlayerActor);
        }

        // ZonePath does not have any components. Instead, it manages the creatures that follow the path.
        // Dispatch the message to all of the creatures that follow the path.
        var isPlayerMessage = message is ZONE_102_PROTOCOL.MSG_ADDPLAYER or ZONE_102_PROTOCOL.MSG_REMOVEPLAYER
                                      or ZONE_102_PROTOCOL.MSG_PLAYERMOVE;
        foreach (var actor in _creatureActors) {
            // A creature still loading gets the players from _players once it has loaded; a join sent now too would
            // be processed twice, and a move ahead of that join would reach a creature that does not know the player.
            if (isPlayerMessage && _loadingCreatures.ContainsKey(actor)) {
                continue;
            }

            actor.Forward(message);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADRESULTS))]
    private void ReceiveCreatureLoaded() {
        if (_loadingCreatures.Remove(Sender)) {
            Timers.Cancel(Sender);

            // The creature spawned after the players joined, so it never saw their join.
            foreach (var player in _players.Values) {
                Sender.Tell(player);
            }
        }

        if (_aggroOnLoad.Remove(Sender, out var playerMove)) {
            // Same check a player step would run: a monster whose aggro radius covers the player starts combat.
            Sender.Tell(playerMove);
        }
    }

    // A slow creature is kept: it still follows the path and meets the players once it has loaded
    // (ReceiveCreatureLoaded). Stopping it lost the zone's monsters whenever the server was busy.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT))]
    private void ReceiveCreatureLoadTimeout(ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT message) {
        if (_loadingCreatures.TryGetValue(message.Entity, out var creature)) {
            Logger.Warning("{Kind} {Name} is still loading after {Timeout} ms; it joins the zone once it is done.",
                Logger.Args(nameof(CoreTemplate), creature.Name, OBJECT_CREATION_TIMEOUT_IN_MS));
        }
    }

    // A ZoneObjects creature (this path is its own) that was removed or changed while the zone runs: its creature
    // leaves the players and the path stops (ZonePathSupervisor makes the new one).
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED))]
    private void ReceiveZoneObjectsChanged(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED message) {
        if (message.SwitchesChanged) {
            ApplySwitches();

            return;
        }

        if (!message.RemovedCreatures.Contains(_template.m_id.Full)) {
            foreach (var actor in _creatureActors) {
                actor.Forward(message);
            }

            return;
        }

        foreach (var id in _spawnedTemplate.Keys) {
            ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT { GameObjectID = id },
                Targets = ZoneBroadcastTarget.Players,
            });
        }

        Logger.Information("Zone {0}: creature {1} taken away ({2} spawned).", Logger.Args(Zone.ZonePath, _template.m_name, _spawnedTemplate.Count));
        Context.Stop(Self);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_DELETEOBJECT))]
    private void ReceiveZoneBroadcast(GAME_5_PROTOCOL.MSG_DELETEOBJECT message) {
        if (_spawnObjectInfo.TryGetValue(message.GameObjectID, out var spawnObject)) {
            var count = CreatureCount(spawnObject);
            SetCreatureCount(spawnObject, count - 1);

            // Live does not respawn a defeated mob inside an instance: once a spawn point has lost a
            // creature here, CanSpawn refuses it for the rest of this instance's lifetime.
            if (Spared.TryRemove(message.GameObjectID, out _)) {
                Timers.StartSingleTimer($"Spared_{message.GameObjectID}",
                    new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL { SpawnObject = spawnObject },
                    TimeSpan.FromSeconds(SPARED_RESPAWN_DELAY_IN_SECONDS));
            }
            else if (Zone.IsInstance) {
                _defeatedInInstance.Add(spawnObject);
                _defeatedIds[message.GameObjectID] = spawnObject;
            }

            _spawnObjectInfo.Remove(message.GameObjectID);
            _spawnedTemplate.Remove(message.GameObjectID);
            _living.Remove(message.GameObjectID);
            Fighting.TryRemove(message.GameObjectID, out _);
            _creatureActors.RemoveAll(x => x == Sender);
            if (_loadingCreatures.Remove(Sender)) {
                Timers.Cancel(Sender);
            }
        }
    }

    // A battle against a ZoneObjects encounter's boss ended. When the creatures won it (everyone fled or was defeated),
    // the room's minions that were defeated in it come back as the ones that lived do (Spared): the fight starts over,
    // as on live. Spawners that waited during the battle fill up again; CanSpawn keeps the limits.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENCOUNTERBATTLEENDED))]
    private void ReceiveEncounterBattleEnded(ZONE_102_PROTOCOL.MSG_ENCOUNTERBATTLEENDED message) {
        foreach (var id in message.ResetIds ?? []) {
            if (!_defeatedIds.Remove(id, out var spawnObject)) {
                continue;
            }

            // Its delete was already counted as a defeat, so the Spared mark the duel gave it is not needed.
            Spared.TryRemove(id, out _);
            if (!_defeatedIds.Values.Any(s => ReferenceEquals(s, spawnObject))) {
                _defeatedInInstance.Remove(spawnObject);
            }

            if (message.OnlyReset) {
                var index = _creatures.FindIndex(x => ReferenceEquals(x, spawnObject));
                Timers.StartSingleTimer($"LostBattleRefill_{index}_{id}",
                    new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL { SpawnObject = spawnObject },
                    TimeSpan.FromSeconds(SPARED_RESPAWN_DELAY_IN_SECONDS));
                continue;
            }

            _heldDistinct.Add(spawnObject);
        }

        if (message.OnlyReset) {
            return;
        }

        foreach (var spawnObject in _heldDistinct) {
            var index = _creatures.FindIndex(x => ReferenceEquals(x, spawnObject));
            for (var i = 0; i < Math.Max(1, (int) spawnObject.m_maxNumberOfSpawns); i++) {
                Timers.StartSingleTimer($"EncounterRefill_{index}_{i}",
                    new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL { SpawnObject = spawnObject },
                    TimeSpan.FromSeconds(SPARED_RESPAWN_DELAY_IN_SECONDS));
            }
        }

        _heldDistinct.Clear();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL))]
    private void ReceiveCreatureSpawnInterval(ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL message) {
        for (var i = 0; i < Math.Max(1, message.Count); i++) {
            HandleCreatureSpawn(message.SpawnObject, null);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN))]
    private void ReceiveCreatureSpawn(ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN message) {
        var index = _creatures.FindIndex(x => x.m_id == message.SpawnObjectID);
        if (index < 0) {
            return;
        }

        var spawnObject = _creatures[index];
        // A street creature whose actor stopped without its delete reaching the zone still held the spawn point, and the
        // check below sent every "only if nobody holds it" call away before HandleCreatureSpawn could free it. Kitharas Moon
        // waited over an hour in Plaza of Conquests for the Earth Elemental ("No Weaklings Allowed") after a bots update
        // logged him out in the round he beat it (2026-11-22 20:47). It is freed first now.
        if (message.OnlyIfAbsent && !Zone.IsInstance) {
            ForgetGone(spawnObject);
        }

        if (message.OnlyIfAbsent && CreatureCount(spawnObject) > 0) {
            if (message.PlayerWizard is not null) {
                TellWhatHoldsIt(spawnObject);
            }

            return;
        }

        if (!message.Activate) {
            // ResSpawn with m_activate false switches the spawner off: stop its timer, spawn nothing more.
            _deactivated.Add(spawnObject);
            spawnObject.m_active = false;
            Timers.Cancel(SpawnTimerKey(spawnObject, index));

            return;
        }

        // Switching a spawner on starts its cycle if it was not running, then spawns now.
        var wasRunning = spawnObject.m_active;
        _deactivated.Remove(spawnObject);
        spawnObject.m_active = true;
        if (!wasRunning && spawnObject.m_spawnTime > 0) {
            StartSpawnTimer(spawnObject, index);
        }

        HandleCreatureSpawn(spawnObject, message);

        // A spawner marked "activate at max" fills up to its maximum when switched on, as on live. Morganthe's fight at
        // Mirror Lake (Zafaria, "Spawn pointSpiders", 4 at most) brought one Starburst Spider instead of four (Niduus,
        // 2026-11-25). CanSpawn stops it at the maximum.
        if (spawnObject.m_activateAtMax) {
            var max = (int) Math.Clamp(spawnObject.m_maxNumberOfSpawns, 1u, MAX_SPAWNS_ALLOWED);
            for (var i = 1; i < max; i++) {
                var before = CreatureCount(spawnObject);
                HandleCreatureSpawn(spawnObject, message);
                if (CreatureCount(spawnObject) <= before) {
                    break;
                }
            }
        }
    }

    private void HandleCreatureSpawn(SpawnObject spawnObject, ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN cause) {
        // On the streets a creature that is gone without the zone hearing of it no longer holds its spawn point.
        if (!Zone.IsInstance) {
            ForgetGone(spawnObject);
        }

        // Determine if the conditions match to spawn the objects.
        if (!CanSpawn(spawnObject)) {
            return;
        }

        // A boss's minions (a ZoneObjects distinct spawner) wait while a battle against the boss runs: the missing ones
        // are in that battle already, spawned into its circle. They fill up again once it ends.
        if (Zone.Extras.IsDistinctSpawner(spawnObject.m_name)
            && System.Threading.Volatile.Read(ref Zone.EncounterBattles) > 0) {
            _heldDistinct.Add(spawnObject);

            return;
        }

        // We meet the conditions to spawn whatever is within this spawn object.
        // The name is confusing, but a SpawnObject may contain different creatures to spawn.
        // Choose a random creature to spawn from the SpawnObject.
        var spawnItem = PickSpawnItem(spawnObject);
        if (spawnItem is null) {
            return;
        }

        var spawnItemInfo = spawnItem.m_objectInfo;

        // Get the node to spawn the creature at. A reagent keeps clear of things players press X on (Firecat Alley's
        // teleporter stone had a Mist Wood spot on it: X picked the reagent instead of teleporting).
        var spawnNode = IsReagent(spawnItemInfo.m_templateID)
            ? ClearNode(spawnItemInfo) ?? GetRelevantNode(spawnItemInfo)
            : GetRelevantNode(spawnItemInfo);

        // If creatures are randomized, pick a random template ID. Wisps always stay wisps. The spawn data is
        // shared by every spawn of this spawner, so the swap must not stick to it or each spawn swaps the last swap.
        var originalTemplateId = spawnItemInfo.m_templateID;
        var isWisp = Supervisors.ZonePathSupervisor.IsWisp(spawnItemInfo);
        var templateId = originalTemplateId;
        if (_randomizeCreatures && !isWisp) {
            templateId = AprilFools.CreatureList.GetRandomCreatureTemplateID();
        }
        else if (Randomizer.RandomizeCreatures && !isWisp) {
            templateId = Randomizer.MapCreature(originalTemplateId);
        }

        // A creature the On and Off tab turned off (TurnedOff) is not spawned; a randomized one that became it is not either.
        var ownCustom = ZoneObjects.CustomOfSpawner(spawnObject.m_id.Full);
        if (Imlight.CoreLib.Game.World.TurnedOff.IsTemplateOff(templateId)
            || Imlight.CoreLib.Game.World.TurnedOff.IsCustomOff(ownCustom)) {
            return;
        }

        // Create the creature using the data we have.
        var template = CoreObjectFactory.GetCoreTemplate(templateId);
        if (template is null) {
            Logger.Error("Failed to get template for {0}.",
                Logger.Args(templateId));

            return;
        }

        spawnItemInfo.m_templateID = templateId;
        var creatureObj = CoreObjectFactory.FinalizeCoreObject(spawnItemInfo, template);
        spawnItemInfo.m_templateID = originalTemplateId;
        creatureObj = CoreObjectFactory.InitializeCoreObjectBehaviors(creatureObj, template);
        // A creature this copy of the zone spawns as a custom of the Customs tab (a ZoneObjects creature's own Custom,
        // ReplaceSpawns, the tab's rooms) is marked before its actor is made, so it has the custom's name and changes.
        // A custom turned off on the On and Off tab that a room swaps in leaves the game's own creature.
        var spawnedCustom = 0;
        if (!isWisp && (ownCustom > 0 ? ownCustom : Zone.Extras.CustomFor(templateId)) is var customId and > 0
            && !Imlight.CoreLib.Game.World.TurnedOff.IsCustomOff(customId)
            && Imlight.CoreLib.Game.Combat.Customs.Get(customId)?.BaseTemplateID == templateId) {
            Imlight.CoreLib.Game.Combat.Customs.Mark(creatureObj.m_globalID.Full, customId);
            spawnedCustom = customId;
        }

        if (templateId != originalTemplateId) {
            Randomizer.RememberReplacedCreature(creatureObj.m_globalID, originalTemplateId);
        }
        if (isWisp) {
            Logger.Debug("Wisp {0} spawned on path {1}.", Logger.Args((template as GameObjectTemplate)?.m_objectName, _template.m_name));
        }
        creatureObj.m_location = spawnNode.m_location;
        var Z = (float) (spawnNode.m_direction * (Math.PI / 180));
        creatureObj.m_orientation = new Imcodec.Math.Vector3(
            creatureObj.m_orientation.X,
            creatureObj.m_orientation.Y,
            Z
        );

        // Create the creature actor. It counts toward the spawner's limit while it loads; a load that never
        // answers gives the slot back.
        var actorName = CreateEntityActorName(creatureObj);
        var creatureActor = Context.ActorOf(Props.Create(() => new ZoneEntity(creatureObj, template, null, ZoneRef, Zone)), actorName);
        creatureActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEOBJECTLOADBEGIN());
        _loadingCreatures[creatureActor] = (spawnObject, creatureObj.m_globalID, (template as GameObjectTemplate)?.m_objectName);
        Timers.StartSingleTimer(creatureActor, new ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT { Entity = creatureActor },
            TimeSpan.FromMilliseconds(OBJECT_CREATION_TIMEOUT_IN_MS));

        _creatureActors.Add(creatureActor);
        _spawnObjectInfo.Add(creatureObj.m_globalID, spawnObject);
        _spawnedTemplate[creatureObj.m_globalID] = originalTemplateId;
        _living[creatureObj.m_globalID] = (creatureActor, creatureObj, templateId, spawnedCustom);
        if (cause?.PlayerObject is not null && cause.PlayerActor is not null) {
            _aggroOnLoad[creatureActor] = new ZONE_102_PROTOCOL.MSG_PLAYERMOVE {
                PlayerObject = cause.PlayerObject,
                PlayerActor = cause.PlayerActor,
                PlayerWizard = cause.PlayerWizard,
            };
        }

        // Inform the newly created creature actor about the nodes they must walk through,
        // if relevant.
        var msg = new ZONE_102_PROTOCOL.MSG_PATHDETAILS { NodeObjects = _nodes };
        creatureActor.Tell(msg);

        // Increment the creature count.
        SetCreatureCount(spawnObject, CreatureCount(spawnObject) + 1);
    }

    // A street creature whose actor stopped without its delete reaching the zone (a despawn that could not be written stops
    // the actor and sends nothing) kept counting toward its spawn point, so the point never filled again: King Uro Ahnic in
    // the Ahnic Family Tomb never came back after 2:48 AM on 2026-11-21 while four players waited for him ("De-Cipher the
    // Ahnics"), and the tomb's spirits kept coming back. The point is freed here, as the delete would have done.
    private void ForgetGone(SpawnObject spawnObject) {
        var gone = _living.Where(l => l.Value.Actor is IInternalActorRef { IsTerminated: true }
                                      && !_loadingCreatures.ContainsKey(l.Value.Actor)
                                      && _spawnObjectInfo.TryGetValue(l.Key, out var s) && ReferenceEquals(s, spawnObject))
            .ToList();
        foreach (var (id, creature) in gone) {
            Logger.Warning("Zone {0}: creature (template {1}) on spawner {2} was gone without the zone hearing of it; its spawn point fills again.",
                Logger.Args(Zone.ZonePath, creature.Template, spawnObject.m_name));
            SetCreatureCount(spawnObject, Math.Max(0, CreatureCount(spawnObject) - 1));
            _spawnObjectInfo.Remove(id);
            _spawnedTemplate.Remove(id);
            _living.Remove(id);
            Fighting.TryRemove(id, out _);
            _creatureActors.Remove(creature.Actor);
        }
    }

    // A quest step asked for this spawner's creature and something still holds the spawn point: what it is, so a creature
    // that never comes back can be told apart from one that is there (a living one, one in a battle, one still loading).
    private void TellWhatHoldsIt(SpawnObject spawnObject) {
        try {
            var held = _living.Where(l => _spawnObjectInfo.TryGetValue(l.Key, out var s) && ReferenceEquals(s, spawnObject))
                .Select(l => $"{l.Key} (template {l.Value.Template}"
                             + (l.Value.Actor is IInternalActorRef { IsTerminated: true } ? ", gone" : "")
                             + (Fighting.ContainsKey(l.Key) ? ", in a battle" : "")
                             + (_loadingCreatures.ContainsKey(l.Value.Actor) ? ", loading" : "") + ")")
                .ToList();
            Logger.Information("Zone {0}: spawner {1} is held ({2} counted): {3}.",
                Logger.Args(Zone.ZonePath, spawnObject.m_name, CreatureCount(spawnObject), held.Count > 0 ? string.Join(", ", held) : "nothing living is known"));

            // One "in a battle" may have been left behind by a battle that ended without it: it is asked, and goes if so.
            foreach (var (id, creature) in _living.Where(l => Fighting.ContainsKey(l.Key)
                         && _spawnObjectInfo.TryGetValue(l.Key, out var s) && ReferenceEquals(s, spawnObject)).ToList()) {
                creature.Actor?.Tell(new ZONE_102_PROTOCOL.MSG_CHECKSTALEBATTLE());
            }
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: what holds spawner {1} could not be read: {2}", Logger.Args(Zone.ZonePath, spawnObject.m_name, ex.Message));
        }
    }

    private static bool IsItemOff(SpawnItem item)
        => item?.m_objectInfo is not null && Imlight.CoreLib.Game.World.TurnedOff.IsTemplateOff(item.m_objectInfo.m_templateID);

    private string OffSign(SpawnObject spawnObject)
        => string.Concat(spawnObject.m_spawnList.Select(i => IsItemOff(i) ? '1' : '0'))
           + (Imlight.CoreLib.Game.World.TurnedOff.IsCustomOff(ZoneObjects.CustomOfSpawner(spawnObject.m_id.Full)) ? "c" : "");

    // The On and Off switches changed (TurnedOff): a living creature that is now off leaves (one in a battle finishes it),
    // and a spawner whose creatures are on again fills up.
    private void ApplySwitches() {
        var gone = 0;
        foreach (var (id, creature) in _living.ToList()) {
            var customOff = creature.Custom > 0 && Imlight.CoreLib.Game.World.TurnedOff.IsCustomOff(creature.Custom);
            var off = Imlight.CoreLib.Game.World.TurnedOff.IsTemplateOff(creature.Template)
                      || Imlight.CoreLib.Game.World.TurnedOff.IsTemplateOff(_spawnedTemplate.GetValueOrDefault(id))
                      || customOff;
            if (!off || Fighting.ContainsKey(id)) {
                continue;
            }

            // A swapped-in custom turned off comes back as the game's own creature, so its spawner fills up again below.
            ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT { GameObjectID = id },
                Targets = ZoneBroadcastTarget.Players,
            });
            ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID { MobileId = creature.Object.m_nMobileID });
            if (_spawnObjectInfo.Remove(id, out var spawner)) {
                SetCreatureCount(spawner, Math.Max(0, CreatureCount(spawner) - 1));
                _offSign.Remove(spawner);
            }

            _spawnedTemplate.Remove(id);
            _living.Remove(id);
            _creatureActors.Remove(creature.Actor);
            _aggroOnLoad.Remove(creature.Actor);
            if (_loadingCreatures.Remove(creature.Actor)) {
                Timers.Cancel(creature.Actor);
            }

            Context.Stop(creature.Actor);
            gone++;
        }

        var refilled = 0;
        for (var i = 0; i < _creatures.Count; i++) {
            var spawnObject = _creatures[i];
            var sign = OffSign(spawnObject);
            var was = _offSign.GetValueOrDefault(spawnObject);
            _offSign[spawnObject] = sign;
            if (was == sign || !spawnObject.m_active || _deactivated.Contains(spawnObject)
                || spawnObject.m_zoneLevelMin != 0 || spawnObject.m_zoneLevelMax != 0) {
                continue;
            }

            Timers.StartSingleTimer($"SwitchedOn_{i}", new ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL {
                SpawnObject = spawnObject,
                Count = (int) Math.Clamp(spawnObject.m_maxNumberOfSpawns, 1u, MAX_SPAWNS_ALLOWED),
            }, TimeSpan.FromMilliseconds(FILL_STAGGER_IN_MS));
            refilled++;
        }

        if (gone + refilled > 0) {
            Logger.Information("Zone {0}: path {1}: {2} creatures turned off, {3} spawners checked again.",
                Logger.Args(Zone.ZonePath, _template.m_name, gone, refilled));
        }
    }

    private bool CanSpawn(SpawnObject spawnObject) {
        if (!_creatureCount.TryGetValue(spawnObject, out var count)) {
            throw new Exception("Somehow, this SpawnObject was not found in the creature count dictionary?");
        }

        // Instanced zones never repopulate a spawn point once something from it has died, even the
        // "at least one" guarantee below.
        // Except a spawner with a timer that a trigger of this room switches off later: the game keeps it coming back until
        // then. Big Ben's Door43 opens on beating the Frog (Spawn point 8, every 5 minutes) only after an earlier door
        // switched its trigger on, and that trigger switches the Frog's spawner off; a Frog beaten before that never came
        // back, so the door stayed shut for good (TCO7 Idunn waited there from 3:12 to past 4:30 AM on 2026-11-21).
        if (Zone.IsInstance && _defeatedInInstance.Contains(spawnObject)
            && !(spawnObject.m_spawnTime > 0 && Zone.SpawnersSwitchedOffByTriggers?.Contains(spawnObject.m_id.Full) == true)) {
            return false;
        }

        if (_deactivated.Contains(spawnObject) || count >= MAX_SPAWNS_ALLOWED) {
            return false;
        }

        if (count <= 0 && spawnObject.m_atLeastOneSpawn) {
            return true;
        }

        if (count >= spawnObject.m_maxNumberOfSpawns) {
            return false;
        }

        var currentCount = CreatureCount(spawnObject);
        var spawnsAvailable = spawnObject.m_maxNumberOfSpawns - currentCount;
        if (spawnsAvailable <= 0) {
            return false;
        }

        return true;
    }

    private SpawnItem PickSpawnItem(SpawnObject spawnObject) {
        // A ZoneObjects distinct spawner (Niduus, 2026-10-22: Malistaire's 3 Draconians are 3 different ones) picks
        // by chance among the templates it has no living creature of; with none left it spawns nothing.
        // Creatures the On and Off tab turned off are left out; the others share their chance.
        var on = spawnObject.m_spawnList.Where(i => !IsItemOff(i)).ToList();
        if (on.Count == 0) {
            return null;
        }

        if (!Zone.Extras.IsDistinctSpawner(spawnObject.m_name)) {
            return on.Count == spawnObject.m_spawnList.Count
                ? PickRandomSpawnObject(spawnObject.m_spawnList)
                : PickRandomSpawnObject(on, Math.Max(1, on.Sum(i => i.m_percentChance)));
        }

        var living = _spawnObjectInfo.Where(x => ReferenceEquals(x.Value, spawnObject))
            .Select(x => _spawnedTemplate.GetValueOrDefault(x.Key)).ToHashSet();
        var left = on.Where(i => i?.m_objectInfo is not null && !living.Contains(i.m_objectInfo.m_templateID)).ToList();

        return left.Count == 0 ? null : PickRandomSpawnObject(left, Math.Max(1, left.Sum(i => i.m_percentChance)));
    }

    private static SpawnItem PickRandomSpawnObject(List<SpawnItem> spawnItems, int totalChance = 100) {
        var rng = new Random();
        var rngNum = rng.Next(0, totalChance);

        var cumulativePercentage = 0;
        foreach (var t in spawnItems) {
            cumulativePercentage += t.m_percentChance;
            if (rngNum < cumulativePercentage) {
                return t;
            }
        }

        return spawnItems[^1];
    }

    private int CreatureCount(SpawnObject spawnObject) {
        var count = _creatureCount[spawnObject];

        if (count >= MAX_SPAWNS_ALLOWED) {
            Logger.Error("Creature {0} has reached the maximum number of spawns allowed ({1}).",
                Logger.Args(spawnObject.m_name, MAX_SPAWNS_ALLOWED));
        }

        return _creatureCount[spawnObject];
    }

    private void SetCreatureCount(SpawnObject spawnObject, int count)
        => _creatureCount[spawnObject] = (byte) count;

    // How far (on the ground) a reagent stays from anything players press X on: both are reached from 300 away, so
    // standing at the thing must not also put the reagent in reach.
    private const float REAGENT_CLEAR = 450f;
    private const float REAGENT_CLEAR_HEIGHT = 300f;
    // Two reagents of one path are not put on the same spot.
    private const float REAGENT_APART = 150f;

    private List<Imcodec.Math.Vector3> _pressSpots;

    private static bool IsReagent(GID templateId) {
        try {
            return CoreObjectFactory.GetCoreTemplate(templateId) is { } template
                   && Imlight.CoreLib.Game.Zone.Components.InteractReagentComponent.ShouldAttachToEntity(template);
        }
        catch {
            return false;
        }
    }

    // Where the zone's own press-X things stand: teleporter stones, sigils, doors, people. Reagents are left out.
    private List<Imcodec.Math.Vector3> PressSpots() {
        if (_pressSpots is not null) {
            return _pressSpots;
        }

        var spots = new List<Imcodec.Math.Vector3>();
        try {
            foreach (var o in Zone?.ZoneData?.m_objectList ?? []) {
                if (o is null || o.m_templateID == 0) {
                    continue;
                }
                if (CoreObjectFactory.GetCoreTemplate(o.m_templateID) is not GameObjectTemplate t
                    || Imlight.CoreLib.Game.Zone.Components.InteractReagentComponent.ShouldAttachToEntity(t)) {
                    continue;
                }
                if (o is CombatSigilObjectInfo
                    || t.m_behaviors?.Any(b => b is InteractableBehaviorTemplate or NPCBehaviorTemplate) == true) {
                    spots.Add(o.m_location);
                }
            }
        }
        catch (Exception e) {
            Logger.Debug("Reagent spots: could not read the press-X things of path {0}: {1}", Logger.Args(_template?.m_name, e.Message));
        }

        return _pressSpots = spots;
    }

    // A node of this path away from press-X things and from the path's other living reagents; null when none is.
    private NodeObject ClearNode(SpawnObjectInfo spawnInfo) {
        if (_nodes.Count == 0 || spawnInfo.m_kStartNodeType is not (StartNodeType.SNT_RANDOM or StartNodeType.SNT_RANDOM_UNIQUE)) {
            return null;
        }

        var press = PressSpots();
        var taken = _living.Values.Where(l => l.Object is not null).Select(l => l.Object.m_location).ToList();
        var clear = _nodes.Where(n => !press.Any(p => Near(p, n.m_location, REAGENT_CLEAR))
                                      && !taken.Any(t => Near(t, n.m_location, REAGENT_APART))).ToList();
        if (clear.Count == 0) {
            clear = _nodes.Where(n => !press.Any(p => Near(p, n.m_location, REAGENT_CLEAR))).ToList();
        }
        return clear.Count == 0 ? null : clear[Random.Shared.Next(clear.Count)];
    }

    private static bool Near(Imcodec.Math.Vector3 a, Imcodec.Math.Vector3 b, float reach) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return dx * dx + dy * dy < reach * reach && Math.Abs(a.Z - b.Z) <= REAGENT_CLEAR_HEIGHT;
    }

    private NodeObject GetRelevantNode(SpawnObjectInfo spawnInfo) {
        switch (spawnInfo.m_kStartNodeType) {
            case StartNodeType.SNT_RANDOM:
                var rng = new Random();
                var rngIndex = rng.Next(0, _nodes.Count);
                return _nodes.ElementAt(rngIndex);
            case StartNodeType.SNT_RANDOM_UNIQUE:
                var rng2 = new Random();
                var rngIndex2 = rng2.Next(0, _nodes.Count);
                return _nodes.ElementAt(rngIndex2);
            case StartNodeType.SNT_FIRST:
                return _nodes.First();
            case StartNodeType.SNT_LAST:
                return _nodes.Last();
            case StartNodeType.SNT_SPECIFIC:
                return _nodes.FirstOrDefault();
            default:
                throw new ArgumentOutOfRangeException(nameof(spawnInfo.m_kStartNodeType),
                    spawnInfo.m_kStartNodeType,
                    "Invalid StartNodeType value");
        }
    }

    protected override void PostStop() {
        Imlight.CoreLib.Game.Tools.MobSpawnAreas.Gone(ZoneRef, _template);
        base.PostStop();
    }

    private static string CreateEntityActorName(CoreObject coreObject) {
        var actorName = $"{coreObject.m_debugName}_{coreObject.m_globalID.Full}";

        // Only alphanumeric characters and underscores are allowed in actor names.
        actorName = new string([.. actorName.Where(c => char.IsLetterOrDigit(c) || c == '_')]);

        return actorName;
    }

}