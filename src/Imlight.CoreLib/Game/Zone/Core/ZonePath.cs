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
    // Spawners a ResSpawn switched off (m_activate false).
    private readonly HashSet<SpawnObject> _deactivated = new(ReferenceEqualityComparer.Instance);
    // Players that caused a spawn (quest or trigger result); the creature checks aggro against them once loaded.
    private readonly Dictionary<IActorRef, ZONE_102_PROTOCOL.MSG_PLAYERMOVE> _aggroOnLoad = [];
    // Players in the zone, so a creature spawned after they joined is told about them like one that was there.
    private readonly Dictionary<IActorRef, ZONE_102_PROTOCOL.MSG_ADDPLAYER> _players = [];
    private readonly bool _randomizeCreatures
        = ConfigurationManager.Settings["April Fools.RandomizeCreatures"].AsBool();

    // ctor
    public ZonePath(PathObjectTemplate template, List<NodeObject> nodes, List<SpawnObject> creatures, IActorRef zoneRef, Zone zone)
        : base(null, null, null, zoneRef, zone) {
        this._template = template;
        this._nodes = nodes;
        this._creatures = creatures;
        base.ZoneRef = zoneRef;
        base.Zone = zone;

        // Begin the creature spawn interval.
        for (var i = 0; i < creatures.Count; i++) {
            var spawnObject = creatures[i];
            _creatureCount.Add(spawnObject, 0);

            if (!spawnObject.m_active) {
                continue;
            }

            StartSpawnTimer(spawnObject, i);
        }
    }

    private void StartSpawnTimer(SpawnObject spawnObject, int index) {
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

        // Otherwise, start the interval. This still ticks in an instanced zone so a multi-creature
        // spawn point can populate up to its max on load; CanSpawn is what stops a defeated spawn
        // point from ever refilling there (see _defeatedInInstance).
        // The first spawn comes as the zone loads, as a spawner without an interval's does: a housing gauntlet's boss
        // (Rattlebones, whose spawner has an interval) came 5 seconds after the zone, while the Fairy Queen (none) stood
        // there from the start (Niduus, 2026-10-24: "Rattlebones does spawn but not immediate"). Later ones keep the interval.
        var delay = TimeSpan.Zero;
        Timers.StartPeriodicTimer(timerKey, msg, delay, interval);

        // A ZoneObjects distinct spawner (a boss's minions) is full from the first spawn, so they are all there
        // before anyone reaches the battle.
        if (Zone.Extras.IsDistinctSpawner(spawnObject.m_name)) {
            for (var i = 1; i < (int) spawnObject.m_maxNumberOfSpawns; i++) {
                Timers.StartSingleTimer($"{timerKey}_fill_{i}", msg, delay);
            }
        }
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

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT))]
    private void ReceiveCreatureLoadTimeout(ZONE_102_PROTOCOL.MSG_ENTITYLOADTIMEOUT message) {
        if (!_loadingCreatures.Remove(message.Entity, out var creature)) {
            return;
        }

        Logger.Error("Failed to create entity actor for {Kind} {Name} (no load reply within {Timeout} ms).",
            Logger.Args(nameof(CoreTemplate), creature.Name, OBJECT_CREATION_TIMEOUT_IN_MS));

        _creatureActors.Remove(message.Entity);
        _aggroOnLoad.Remove(message.Entity);
        _spawnObjectInfo.Remove(creature.ObjectId);
        _spawnedTemplate.Remove(creature.ObjectId);
        SetCreatureCount(creature.Spawner, CreatureCount(creature.Spawner) - 1);
        Context.Stop(message.Entity);
    }

    // A ZoneObjects creature (this path is its own) that was removed or changed while the zone runs: its creature
    // leaves the players and the path stops (ZonePathSupervisor makes the new one).
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED))]
    private void ReceiveZoneObjectsChanged(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED message) {
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

            _heldDistinct.Add(spawnObject);
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
    private void ReceiveCreatureSpawnInterval(ZONE_102_PROTOCOL.MSG_PATHSPAWNINTERVAL message) 
        => HandleCreatureSpawn(message.SpawnObject, null);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN))]
    private void ReceiveCreatureSpawn(ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN message) {
        var index = _creatures.FindIndex(x => x.m_id == message.SpawnObjectID);
        if (index < 0) {
            return;
        }

        var spawnObject = _creatures[index];
        if (message.OnlyIfAbsent && CreatureCount(spawnObject) > 0) {
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
    }

    private void HandleCreatureSpawn(SpawnObject spawnObject, ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN cause) {
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

        // Get the node to spawn the creature at.
        var spawnNode = GetRelevantNode(spawnItemInfo);

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
        var ownCustom = ZoneObjects.CustomOfSpawner(spawnObject.m_id.Full);
        if (!isWisp && (ownCustom > 0 ? ownCustom : Zone.Extras.CustomFor(templateId)) is var customId and > 0
            && Imlight.CoreLib.Game.Combat.Customs.Get(customId)?.BaseTemplateID == templateId) {
            Imlight.CoreLib.Game.Combat.Customs.Mark(creatureObj.m_globalID.Full, customId);
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

    private bool CanSpawn(SpawnObject spawnObject) {
        if (!_creatureCount.TryGetValue(spawnObject, out var count)) {
            throw new Exception("Somehow, this SpawnObject was not found in the creature count dictionary?");
        }

        // Instanced zones never repopulate a spawn point once something from it has died, even the
        // "at least one" guarantee below.
        if (Zone.IsInstance && _defeatedInInstance.Contains(spawnObject)) {
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
        if (!Zone.Extras.IsDistinctSpawner(spawnObject.m_name)) {
            return PickRandomSpawnObject(spawnObject.m_spawnList);
        }

        var living = _spawnObjectInfo.Where(x => ReferenceEquals(x.Value, spawnObject))
            .Select(x => _spawnedTemplate.GetValueOrDefault(x.Key)).ToHashSet();
        var left = spawnObject.m_spawnList.Where(i => i?.m_objectInfo is not null
                                                      && !living.Contains(i.m_objectInfo.m_templateID)).ToList();

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

    private static string CreateEntityActorName(CoreObject coreObject) {
        var actorName = $"{coreObject.m_debugName}_{coreObject.m_globalID.Full}";

        // Only alphanumeric characters and underscores are allowed in actor names.
        actorName = new string([.. actorName.Where(c => char.IsLetterOrDigit(c) || c == '_')]);

        return actorName;
    }

}