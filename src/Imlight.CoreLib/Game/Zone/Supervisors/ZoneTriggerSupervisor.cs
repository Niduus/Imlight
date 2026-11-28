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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/// <summary>
/// Exists as a child actor of a <see cref="Zone"/> and is the supervisor
/// for any triggers that may happen with the zone.
/// </summary>
/// <param name="zone">The zone that this supervisor is responsible for.</param>
/// <remarks>Triggers are any event that can happen within a zone. Zone transfers, POI
/// text, etc.</remarks>
internal sealed partial class ZoneTriggerSupervisor(Core.Zone zone) : ZoneEntitySupervisor(zone) {

    private static bool s_randomizeGateways => ConfigurationManager.Settings["April Fools.RandomizeGateways"].AsBool();

    // [World] OpenAllDoors: doors and other teleports work without their quest, level or event requirements.
    // [World] AccessAllWorlds does the same for every teleport (Imlight.CoreLib.Game.World.WorldAccess.AllOpen).
    private static bool s_openAllDoors => Imlight.CoreLib.Game.World.WorldAccess.AllOpen;

    // A trigger's own object is hidden with this state when the trigger is deactivated.
    private const string DEACTIVATED_OBJECT_STATE = "Off";

    private readonly List<(Trigger Trigger, IActorRef Actor)> _orderedTriggers = [];
    private readonly HashSet<string> _undecodableLogged = [];
    private readonly HashSet<string> _deadTeleportsLogged = [];
    private readonly HashSet<string> _chainedEvents = [];
    private readonly HashSet<string> _cinematicEndsPosted = [];
    private readonly HashSet<string> _unenforceableTriggers = [];
    // EnterZone triggers a player was refused on entry, checked again once the quest has updated its goals.
    private readonly Dictionary<IActorRef, HashSet<string>> _refusedOnEntry = [];

    // The objects that enabled triggers own (walls, gates, collision), by trigger name.
    private readonly Dictionary<string, (IActorRef Actor, CoreObject Object, string Tag)> _triggerObjects = new(StringComparer.OrdinalIgnoreCase);

    // The activate event a trigger lists to start the zone with it enabled.
    private const string START_ZONE_EVENT = "StartZone";
    private const string START_ZONE_ENTER_EVENT = "EnterZone";

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS))]
    public override void ReceiveZoneLoadResults(ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS message) {
        // Triggers are stored in the client data, except for zone transfers.
        // Our QA team has manually recreated this trigger data, and is available within the database.
        // Words cannot describe how thankful I am for QA. They are the unsung heroes of the development team.
        var replacedTriggers = ReplaceTriggerDataWithDatabase(message.TriggerData);
        var spawners = message.SpawnData.m_spawners;
        UpdateSpawnResultTriggers(ref replacedTriggers, spawners, message.PathData.m_pathList, message.NodeData.m_nodeList);

        _chainedEvents.Clear();
        foreach (var chained in replacedTriggers
                     .Where(t => t?.m_results?.m_results is not null)
                     .SelectMany(t => t.m_results.m_results)
                     .OfType<ResPostEvent>()) {
            _chainedEvents.Add(chained.m_eventName.ToString());
        }

        SeedTriggerStates(replacedTriggers, message.TriggerData.m_triggers);
        NoteSleepingSpawners(spawners, replacedTriggers);
        NoteEventPeopleSpawners(spawners);
        NoteWalkInSpawners(spawners, replacedTriggers, message.PathData.m_pathList, message.NodeData.m_nodeList);

        var byName = new Dictionary<string, Trigger>(StringComparer.Ordinal);
        foreach (var trigger in replacedTriggers) {
            if (!string.IsNullOrEmpty(trigger?.m_triggerName)) {
                byName.TryAdd(trigger.m_triggerName, trigger);
            }
        }
        Zone.TriggersByName = byName;
        NoteTransportSpots(replacedTriggers, message.VolumeData?.m_volumes, message.ZoneData?.m_objectList);

        // The state sets of the triggers' own objects (doors, gates), so a change plays its animation (StateTransitions).
        foreach (var info in replacedTriggers.Select(t => t?.m_triggerObjInfo).Where(i => i is not null && i.m_templateID != 0)) {
            try {
                Imlight.CoreLib.Game.States.StateTransitions.Note(Zone.ZonePath, info.m_zoneTag,
                    CoreObjectFactory.GetCoreTemplate(info.m_templateID));
            }
            catch (Exception ex) {
                Logger.Debug("Zone {0}: no state set for trigger object {1}: {2}", Logger.Args(Zone.ZonePath, info.m_zoneTag, ex.Message));
            }
        }

        _orderedTriggers.Clear();
        foreach (var trigger in replacedTriggers) {
            var triggerActor = Context.ActorOf(Props.Create(() => new ZoneTrigger(ZoneRef, Zone, trigger)));
            BeginEntityLoad(triggerActor, trigger?.m_triggerName);
            _orderedTriggers.Add((trigger, triggerActor));
        }

        // Triggers that start enabled bring their own objects with them.
        _triggerObjects.Clear();
        foreach (var (trigger, _) in _orderedTriggers) {
            if (trigger is not null && Zone.ScriptState.IsTriggerEnabled(trigger.m_triggerName)) {
                SpawnTriggerObject(trigger);
            }
        }

        ReportLoadedWhenEntitiesLoad();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_POSTEVENT))]
    private void ReceivePostEvent(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // An event that comes round far too often is a script loop; its extra posts are dropped (LoopGuard).
        if (IsRunawayEvent(message.EventName)) {
            return;
        }

        // A wizard walking into any spot of the room may be near a boss that only a walk-in spot brings out (WalkInBosses).
        if (message.PlayerActor is not null && HoldsWalkInSpawners() && ((string) message.EventName)?.StartsWith("Enter_", StringComparison.Ordinal) == true) {
            ScheduleWaitingSpawnCheck(message.PlayerActor, message.PlayerGameObject);
        }

        // Client wads pair multiple triggers on the same event; every passing trigger fires
        // (their results are independent), but paired teleporter triggers must not both win:
        // only one ResTeleport trigger may execute (see PickTeleportTrigger).
        // A boss that only a live cutscene would bring out is brought out for a wizard who comes to fight it.
        if (message.EventName == START_ZONE_ENTER_EVENT && message.PlayerActor is not null) {
            ScheduleWaitingSpawnCheck(message.PlayerActor, message.PlayerGameObject);
            // A door a quest's dialog opens for good (Bartleby's mouth), for a wizard who may go through but never heard it.
            OpenStoryDoors(message);
            // In the Well of Spirits or one of its tombs: past the storm obelisks for good (StormObelisks).
            if (StormObelisks.IsInside(Zone.ZonePath)) {
                StormObelisks.BeenInside(ZoneTrigger.QueryWizard(message.PlayerActor), Zone.ZonePath);
            }
        }

        var passing = new List<(Trigger Trigger, IActorRef Actor)>();
        // With OpenAllDoors a door goes through without its quest; these are the passing doors whose quest needs are met too.
        HashSet<Trigger> doorsWithNeedsMet = null;
        HashSet<string> retry = null;
        if (message.RetryRefused && (message.PlayerActor is null || !_refusedOnEntry.TryGetValue(message.PlayerActor, out retry))) {
            return;
        }

        // A fresh walk into the zone checks every entry trigger again, so what was refused on an earlier visit is dropped.
        if (!message.RetryRefused && message.EventName == START_ZONE_ENTER_EVENT && message.PlayerActor is not null) {
            _refusedOnEntry.Remove(message.PlayerActor);
        }

        foreach (var (trigger, triggerActor) in _orderedTriggers) {
            if (trigger.m_fireEvents is null || !trigger.m_fireEvents.Any(x => x == message.EventName)) {
                continue;
            }

            if (retry is not null && !retry.Contains(trigger.m_triggerName)) {
                continue;
            }

            // The Tomb of Storms' way into the Well of Spirits stays shut until the wizard lit the three storm obelisks.
            if (StormSealed(trigger, message)) {
                continue;
            }

            // Haggerty Greycap's "secret path" greeting in Northguard is only for a wizard Grizzleheim does not know yet.
            if (IsStaleSecretPathWarning(trigger, message)) {
                continue;
            }

            // With OpenAllDoors, a door goes through as long as its fire limit allows.
            if (s_openAllDoors && HasTeleportResult(trigger)) {
                // A door the game opens only for a step of a quest the wizard already finished stays shut (FinishedQuestDoors).
                if (IsFinishedQuestDoor(trigger, message)) {
                    continue;
                }

                if (Zone.ScriptState.HasTriggerFiresLeft(trigger.m_triggerName, trigger.m_triggerMax)) {
                    passing.Add((trigger, triggerActor));
                    if (!ZoneTrigger.HasUndecodableRequirement(trigger.m_requirements) && EvaluateRequirements(trigger, message)) {
                        (doorsWithNeedsMet ??= []).Add(trigger);
                    }
                }

                continue;
            }

            if (!_chainedEvents.Contains(message.EventName) && !IsEnforcedEnabled(trigger)) {
                continue;
            }

            if (ZoneTrigger.HasUndecodableRequirement(trigger.m_requirements) && !IsLongRaisedEvent(message.EventName)) {
                if (_undecodableLogged.Add(trigger.m_triggerName)) {
                    Logger.Warning("Trigger {0} in {1} has a requirement class Imcodec cannot decode; it will not fire.",
                        Logger.Args(trigger.m_triggerName, Zone.ZonePath));
                }

                continue;
            }

            // A trigger with a fire limit (m_triggerMax) that used them all in this zone instance stays quiet.
            if (!Zone.ScriptState.HasTriggerFiresLeft(trigger.m_triggerName, trigger.m_triggerMax)) {
                continue;
            }

            // A state trigger (a door that opens) only fires while its object is in the state it starts from.
            if (!IsInRequiredState(trigger)) {
                continue;
            }

            if (!EvaluateRequirements(trigger, message)) {
                // A door that stays shut is otherwise silent ("friend can not enter Hedge Maze", 2026-10-22).
                if (HasTeleportResult(trigger)) {
                    Logger.Debug("Zone {0}: teleport trigger {1} on event {2} refused {3}: requirements not met ({4}).",
                        Logger.Args(Zone.ZonePath, trigger.m_triggerName, message.EventName, message.PlayerActor?.Path.Name,
                            Imlight.CoreLib.Game.Sigils.SigilRequirements.Describe(trigger.m_requirements)));
                    // The bots learn it at once: this way opens with the quests its needs name (BotGates).
                    if (message.PlayerActor is not null && BotDoor.Side is { } bots && bots.GateNamesQuests(trigger)) {
                        bots.GateRefused(ZoneTrigger.QueryWizard(message.PlayerActor), Zone.ZonePath, trigger);
                    }
                }
                else if (retry is null && message.EventName == START_ZONE_ENTER_EVENT && message.PlayerActor is not null) {
                    if (!_refusedOnEntry.TryGetValue(message.PlayerActor, out var refused)) {
                        _refusedOnEntry[message.PlayerActor] = refused = [];
                    }
                    refused.Add(trigger.m_triggerName);
                }

                continue;
            }

            passing.Add((trigger, triggerActor));
        }

        if (retry is not null) {
            foreach (var (trigger, _) in passing) {
                Logger.Information("Zone {0}: trigger {1} fires after the quest updated on entry.",
                    Logger.Args(Zone.ZonePath, trigger.m_triggerName));
                retry.Remove(trigger.m_triggerName);
            }

            // The ones still refused wait for the wizard's next quest step in this zone (Belloq's tent: his fight starts
            // on entry only for a wizard on the "defeat Belloq" step, and that step starts after talking to him inside).
            if (retry.Count == 0) {
                _refusedOnEntry.Remove(message.PlayerActor);
            }
        }

        var teleportWinner = PickTeleportTrigger(passing, message, doorsWithNeedsMet);
        if (teleportWinner is not null) {
            Logger.Debug("Zone {0}: event {1} takes the player through teleport trigger {2}.",
                Logger.Args(Zone.ZonePath, message.EventName, teleportWinner.m_triggerName));
        }
        foreach (var (trigger, triggerActor) in passing) {
            // Two passing events in one message cannot both take the last fire.
            if (!Zone.ScriptState.HasTriggerFiresLeft(trigger.m_triggerName, trigger.m_triggerMax)) {
                continue;
            }

            Zone.ScriptState.RecordTriggerFire(trigger.m_triggerName);
            var hasTeleportResult = HasTeleportResult(trigger);
            triggerActor.Forward(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = message.EventName,
                PlayerActor = message.PlayerActor,
                PlayerGameObject = message.PlayerGameObject,
                SuppressTeleportResults = hasTeleportResult && !ReferenceEquals(trigger, teleportWinner),
                PlayerSpawned = message.PlayerSpawned,
                Adjectives = message.Adjectives,
                RequirementsChecked = true,
            });

            if (trigger is StateTrigger stateTrigger) {
                FireStateTrigger(stateTrigger, message);
            }
        }

        // Triggers that fired saw the state from before this event; now the event's own enables and disables land.
        if (retry is not null) {
            return;
        }

        ApplyTriggerStateEvents(message.EventName, message.PlayerActor, message.PlayerGameObject);
    }

    private void SeedTriggerStates(List<Trigger> triggers, IEnumerable<Trigger> zoneFileTriggers) {
        // A trigger whose activate events nothing in the zone posts stays enabled, since nothing could ever enable it.
        // A trigger in the zone files with no activate event at all is one the game's designers switched off: they take
        // StartZone out of an old copy instead of deleting it. Lord Obsidian's Tomb top floor (Necropolis 5Room3_5) keeps
        // such copies of Pyromancer's Tomb's Drusilla taunt and forced fight; the live game never plays them, the server
        // did (Niduus, 2026-11-25). Triggers the server adds itself (ZoneObjects event trips) have none and stay on.
        var fromZoneFiles = new HashSet<Trigger>(zoneFileTriggers?.Where(t => t is not null) ?? [],
            ReferenceEqualityComparer.Instance);
        _unenforceableTriggers.Clear();
        foreach (var trigger in triggers.Where(t => t is not null)) {
            var activate = trigger.m_activateEvents?.Select(e => e.ToString()).ToList() ?? [];
            var switchedOff = activate.Count == 0 && fromZoneFiles.Contains(trigger) && !HasTeleportResult(trigger);
            var startsEnabled = !switchedOff && (activate.Count == 0 || activate.Contains(START_ZONE_EVENT));
            string name = trigger.m_triggerName;
            Zone.ScriptState.SetTriggerEnabled(name, startsEnabled);

            if (switchedOff) {
                Logger.Debug("Zone {0}: trigger {1} has no activate event in the zone files; it stays off as in the game.",
                    Logger.Args(Zone.ZonePath, name));
                continue;
            }

            if (!startsEnabled && !activate.Any(_chainedEvents.Contains)) {
                _unenforceableTriggers.Add(name);
            }
        }
    }

    private bool IsEnforcedEnabled(Trigger trigger) {
        // Only events raised since object, kill and interaction events existed are enforced; older ones fire as they always did.
        string name = trigger.m_triggerName;

        return _unenforceableTriggers.Contains(name) || Zone.ScriptState.IsTriggerEnabled(name);
    }

    /*
     * STATE TRIGGERS
     * A StateTrigger moves its own object from m_requiredState to m_nextState when it fires; the game files never put a
     * result on it for that. Krokotopia's Throne Room of Fire shortcut is one: "Trigger Door1" fires on Krokenkahmen's
     * defeat and its portcullis goes Idle_Closed -> Idle_Open, while "Trigger DoorCol1" (m_flagB, no states) fires on the
     * same chain and its collision wall goes away. The server never read these fields, so the gate stayed shut (Niduus,
     * 2026-11-04).
     */
    private string CurrentObjectState(Trigger trigger) {
        var info = trigger.m_triggerObjInfo;
        string tag = info?.m_zoneTag;
        if (string.IsNullOrEmpty(tag)) {
            return null;
        }

        return Zone.ScriptState.GetObjectState(tag) ?? info.m_startState.ToString();
    }

    private bool IsInRequiredState(Trigger trigger) {
        if (trigger is not StateTrigger stateTrigger || string.IsNullOrEmpty(stateTrigger.m_requiredState)) {
            return true;
        }

        var current = CurrentObjectState(trigger);

        // An object whose state nobody knows counts as in it, so the door is not stuck shut.
        return string.IsNullOrEmpty(current)
            || string.Equals(current, stateTrigger.m_requiredState.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void FireStateTrigger(StateTrigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        try {
            string objectTag = trigger.m_triggerObjInfo?.m_zoneTag;
            string nextState = trigger.m_nextState;
            if (!string.IsNullOrEmpty(objectTag) && !string.IsNullOrEmpty(nextState)) {
                Logger.Information("Zone {0}: state trigger {1} moves {2} from {3} to {4}.",
                    Logger.Args(Zone.ZonePath, trigger.m_triggerName, objectTag, CurrentObjectState(trigger), nextState));
                ApplyObjectState(objectTag, nextState, message.PlayerActor, message.PlayerGameObject, message.PlayerSpawned);

                return;
            }

            // No state to go to, only the flag: the trigger is used up and its object (a collision wall) goes away.
            if (trigger.m_flagB && !string.IsNullOrEmpty(objectTag)) {
                Logger.Information("Zone {0}: state trigger {1} fired, its object {2} goes away.",
                    Logger.Args(Zone.ZonePath, trigger.m_triggerName, objectTag));
                Zone.ScriptState.SetTriggerEnabled(trigger.m_triggerName, false);
                ApplyObjectState(objectTag, DEACTIVATED_OBJECT_STATE, message.PlayerActor, message.PlayerGameObject);
                DespawnTriggerObject(trigger);
            }
        }
        catch (Exception ex) {
            Logger.Error("Zone {0}: state trigger {1} failed: {2}", Logger.Args(Zone.ZonePath, trigger.m_triggerName, ex));
        }
    }

    private void ApplyTriggerStateEvents(string eventName, IActorRef playerActor, CoreObject playerObject) {
        foreach (var (trigger, _) in _orderedTriggers) {
            string name = trigger?.m_triggerName;
            if (string.IsNullOrEmpty(name)) {
                continue;
            }

            if (trigger.m_activateEvents?.Any(e => e.ToString() == eventName) == true) {
                Zone.ScriptState.SetTriggerEnabled(name, true);
                SpawnTriggerObject(trigger);
            }

            if (trigger.m_deactivateEvents?.Any(e => e.ToString() == eventName) == true) {
                Zone.ScriptState.SetTriggerEnabled(name, false);

                // A trigger's own object (a collision volume, a door) goes away when one of the trigger's
                // deactivate events is posted.
                var objectTag = trigger.m_triggerObjInfo?.m_zoneTag;
                if (!string.IsNullOrEmpty(objectTag)) {
                    ApplyObjectState(objectTag, DEACTIVATED_OBJECT_STATE, playerActor, playerObject);
                }

                DespawnTriggerObject(trigger);
            }
        }
    }

    private void NoteTransportSpots(IEnumerable<Trigger> triggers, IEnumerable<Volume> volumes, IEnumerable<CoreObjectInfo> placed) {
        var spots = new List<TransportSpot>();
        var volumeList = volumes?.Where(volume => volume is not null).ToList() ?? [];
        var triggerList = triggers?.Where(trigger => trigger is not null).ToList() ?? [];
        foreach (var trigger in triggerList.Where(InteractDoorTeleportComponent.IsTransportTrigger)) {
            var info = trigger.m_triggerObjInfo;
            var volume = volumeList.FirstOrDefault(item => VolumeNamed(item, trigger))
                ?? volumeList.FirstOrDefault(item => VolumeFires(item, trigger))
                ?? volumeList
                    .Select(item => (Volume: item, Distance: TriggerDistance(item, info)))
                    .Where(pair => info is not null && pair.Distance <= TransportReach(pair.Volume) * TransportReach(pair.Volume))
                    .OrderBy(pair => pair.Distance)
                    .Select(pair => pair.Volume)
                    .FirstOrDefault();
            if (volume is null) {
                Logger.Information("Transport trigger {0} in {1} has no volume. Object {2} at {3},{4}.",
                    Logger.Args(trigger.m_triggerName, Zone.ZonePath, (string)info?.m_zoneTag, info?.m_locationX, info?.m_locationY));
                continue;
            }

            var reach = TransportReach(volume);
            spots.Add(new TransportSpot {
                TriggerName = trigger.m_triggerName,
                ObjectTag = (string)info?.m_zoneTag,
                VolumeName = volume.m_volumeName,
                X = volume.m_locationX,
                Y = volume.m_locationY,
                RadiusSq = reach * reach,
            });
            Logger.Information("Transport spot {0} uses volume {1} at {2},{3} radius {4}.",
                Logger.Args(trigger.m_triggerName, volume.m_volumeName, volume.m_locationX, volume.m_locationY, reach));
        }

        foreach (var info in placed ?? []) {
            var tag = (string)info?.m_zoneTag;
            if (info is null || !InteractDoorTeleportComponent.HasTransportName(tag)) {
                continue;
            }

            var at = info.m_location;

            // A second copy of a ladder end (The Hive's two KR-Hive-BottomPAFladder) gets its own spot around itself.
            if (spots.Any(spot => !spot.UsesHeight
                    && string.Equals(StripTransportName(spot.ObjectTag), StripTransportName(tag), StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            var named = volumeList.FirstOrDefault(volume => VolumeNamed(volume, tag))
                ?? volumeList.FirstOrDefault(volume => triggerList.Any(item =>
                    VolumeFires(volume, item) && TriggerOwnsObject(item, tag)));
            if (named is not null) {
                AddObjectSpot(spots, named, triggerList.FirstOrDefault(item => TriggerOwnsObject(item, tag))
                    ?? triggerList.FirstOrDefault(item => VolumeFires(named, item)), tag);
            }

            var near = triggerList
                .Select(item => (Trigger: item, Distance: TriggerDistance(at.X, at.Y, item.m_triggerObjInfo)))
                .Where(pair => pair.Distance <= 400f * 400f)
                .OrderBy(pair => pair.Distance)
                .Take(3);
            var logged = false;
            foreach (var pair in near) {
                logged = true;
                var owned = pair.Trigger.m_triggerObjInfo;
                var (ox, oy) = TriggerLocation(owned);
                Logger.Information("Transport object {0} at {1},{2} near trigger {3} tag {4} template {5} load {6} at {7},{8}.",
                    Logger.Args(tag, at.X, at.Y, pair.Trigger.m_triggerName, (string)owned?.m_zoneTag,
                        owned?.m_templateID, owned?.m_loadingType, ox, oy));
            }

            if (named is null && TryBehaviorSpot(spots, info, tag, at.X, at.Y)) {
                continue;
            }

            if (named is null) {
                var pad = volumeList
                    .Select(volume => {
                        var dx = at.X - volume.m_locationX;
                        var dy = at.Y - volume.m_locationY;
                        return (Volume: volume, Reach: TransportReach(volume), Distance: MathF.Sqrt((dx * dx) + (dy * dy)));
                    })
                    .Where(pair => pair.Reach is > 1f and <= 250f && pair.Distance <= pair.Reach + 200f)
                    .OrderBy(pair => pair.Distance)
                    .FirstOrDefault();
                if (pad.Volume is not null) {
                    var padTrigger = triggerList.FirstOrDefault(item => VolumeFires(pad.Volume, item))
                        ?? triggerList.FirstOrDefault(item => TriggerOwnsObject(item, tag));
                    AddObjectSpot(spots, pad.Volume, padTrigger, tag);
                }
                else if (TryOwnSpot(spots, info, tag)) {
                    continue;
                }
                else if (!logged) {
                    Logger.Information("Transport object {0} in {1} at {2},{3} has no nearby trigger.",
                        Logger.Args(tag, Zone.ZonePath, at.X, at.Y));
                }
            }
        }

        // A ladder end around itself is only one with another end over or under it (a quest's web ladder that is used for
        // its step, not climbed, keeps its own use).
        var ownEnds = spots.Where(spot => spot.UsesHeight).ToList();
        foreach (var end in ownEnds.Where(end => !ownEnds.Any(other => !ReferenceEquals(other, end)
                     && MathF.Abs(other.Z - end.Z) >= 150f
                     && ((other.X - end.X) * (other.X - end.X)) + ((other.Y - end.Y) * (other.Y - end.Y)) <= 900f * 900f))) {
            spots.Remove(end);
            Logger.Information("Transport object {0} in {1} at {2:0},{3:0} has no other ladder end over or under it.",
                Logger.Args(end.ObjectTag, Zone.ZonePath, end.X, end.Y));
        }

        Logger.Information("Transport spots in {0}: {1}.", Logger.Args(Zone.ZonePath, spots.Count));
        Zone.SetTransportSpots(spots);
    }

    // How near a ladder end with nothing else to go by (no trigger, volume, click box or behavior radius) a wizard stands
    // to be offered it.
    private const float OWN_SPOT_RADIUS = 200f;

    // A ladder end that is only an interactable placed in the zone (all of The Hive's): its press-X spot is around itself,
    // on its own level, and its other end is the nearest ladder end over or under it (Zone.TryNearestTransportEnd).
    private bool TryOwnSpot(List<TransportSpot> spots, CoreObjectInfo info, string tag) {
        if (CoreObjectFactory.GetCoreTemplate(info.m_templateID) is not GameObjectTemplate template
            || template.m_behaviors?.OfType<InteractableBehaviorTemplate>().Any() != true) {
            return false;
        }

        var at = info.m_location;
        spots.Add(new TransportSpot {
            TriggerName = StripTransportName(tag),
            ObjectTag = tag,
            VolumeName = "own",
            X = at.X,
            Y = at.Y,
            Z = at.Z,
            RadiusSq = OWN_SPOT_RADIUS * OWN_SPOT_RADIUS,
            UsesHeight = true,
        });
        Logger.Information("Transport spot {0} in {1}: around itself at {2:0},{3:0},{4:0} (no trigger, volume or click box).",
            Logger.Args(tag, Zone.ZonePath, at.X, at.Y, at.Z));

        return true;
    }

    private bool TryBehaviorSpot(List<TransportSpot> spots, CoreObjectInfo info, string tag, float x, float y) {
        var template = CoreObjectFactory.GetCoreTemplate(info.m_templateID) as GameObjectTemplate;
        var behaviors = template?.m_behaviors;
        var collision = behaviors?.OfType<CollisionBehaviorTemplate>().FirstOrDefault();
        if (collision?.m_bAutoClickBox == true && string.IsNullOrEmpty(collision.m_clickableCollisionFilename)) {
            const float clickRadius = 200f;
            spots.Add(new TransportSpot {
                TriggerName = StripTransportName(tag),
                ObjectTag = tag,
                VolumeName = "auto-click",
                X = x,
                Y = y,
                Z = info.m_location.Z,
                RadiusSq = clickRadius * clickRadius,
            });
            Logger.Information("Transport spot {0} uses its auto click box, radius {1} at {2},{3}.",
                Logger.Args(tag, clickRadius, x, y));
            return true;
        }

        var prox = behaviors?.OfType<TeleportProximityBehaviorTemplate>().FirstOrDefault(item => item.m_radius > 1f);
        var area = behaviors?.OfType<AreaBehaviorTemplate>().FirstOrDefault(item => item.m_radius > 1f);
        if (prox is null && area is null) {
            var described = behaviors?.Select(item => $"{item?.GetType().Name}:{item?.m_behaviorName}").ToList() ?? [];
            Logger.Information("Transport object {0} template {1} behaviors {2}; click {3}; auto box {4}; client only {5}.",
                Logger.Args(tag, info.m_templateID.Full, described.Count == 0 ? "none" : string.Join(", ", described),
                    collision?.m_clickableCollisionFilename, collision?.m_bAutoClickBox, collision?.m_bClientOnly));
            return false;
        }

        var radius = prox?.m_radius ?? area.m_radius;
        spots.Add(new TransportSpot {
            TriggerName = string.IsNullOrEmpty(prox?.m_locationName) ? StripTransportName(tag) : prox.m_locationName,
            ObjectTag = tag,
            VolumeName = prox?.m_behaviorName ?? area?.m_behaviorName ?? "behavior",
            X = x + (prox?.m_offsetX ?? 0f),
            Y = y + (prox?.m_offsetY ?? 0f),
            RadiusSq = radius * radius,
        });
        Logger.Information("Transport spot {0} object {1} uses behavior radius {2} at {3},{4}.",
            Logger.Args(prox?.m_locationName ?? tag, tag, radius, x + (prox?.m_offsetX ?? 0f), y + (prox?.m_offsetY ?? 0f)));
        return true;
    }

    private void AddObjectSpot(List<TransportSpot> spots, Volume volume, Trigger trigger, string tag) {
        var reach = TransportReach(volume);
        var box = volume.m_radius <= 0f && volume.m_length > 0f && volume.m_width > 0f;
        spots.Add(new TransportSpot {
            TriggerName = trigger?.m_triggerName ?? StripTransportName(tag),
            ObjectTag = tag,
            VolumeName = volume.m_volumeName,
            X = volume.m_locationX,
            Y = volume.m_locationY,
            RadiusSq = box ? 0f : reach * reach,
            Length = box ? volume.m_length : 0f,
            Width = box ? volume.m_width : 0f,
            Yaw = box ? volume.m_orientation.Z : 0f,
        });
        Logger.Information("Transport spot {0} object {1} uses volume {2} at {3},{4} radius {5}.",
            Logger.Args(trigger?.m_triggerName ?? tag, tag, volume.m_volumeName, volume.m_locationX, volume.m_locationY, reach));
    }

    private static bool VolumeNamed(Volume volume, string tag) {
        var volumeName = StripTransportName(volume.m_volumeName);
        var name = StripTransportName(tag);
        return !string.IsNullOrEmpty(volumeName) && !string.IsNullOrEmpty(name)
            && (volumeName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || volumeName.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(volumeName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TriggerOwnsObject(Trigger trigger, string tag) {
        var name = StripTransportName(tag);
        return string.Equals(StripTransportName(trigger?.m_triggerName), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripTransportName((string)trigger?.m_triggerObjInfo?.m_zoneTag), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripTransportName((string)trigger?.m_triggerObjInfo?.m_triggerObjName), name, StringComparison.OrdinalIgnoreCase);
    }

    private static float TriggerDistance(float x, float y, TriggerObjectInfo info) {
        if (info is null) {
            return float.MaxValue;
        }

        var (ox, oy) = TriggerLocation(info);
        var dx = x - ox;
        var dy = y - oy;
        return (dx * dx) + (dy * dy);
    }

    private static (float X, float Y) TriggerLocation(TriggerObjectInfo info) {
        if (info is null) {
            return (0f, 0f);
        }

        if (MathF.Abs(info.m_locationX) > 1f || MathF.Abs(info.m_locationY) > 1f) {
            return (info.m_locationX, info.m_locationY);
        }

        return (info.m_location.X, info.m_location.Y);
    }

    private static bool VolumeContains(Volume volume, float x, float y) {
        var dx = x - volume.m_locationX;
        var dy = y - volume.m_locationY;
        if (volume.m_radius <= 0f && volume.m_length > 0f && volume.m_width > 0f) {
            var yaw = volume.m_orientation.Z;
            var localX = (dx * MathF.Cos(yaw)) + (dy * MathF.Sin(yaw));
            var localY = (dy * MathF.Cos(yaw)) - (dx * MathF.Sin(yaw));
            return MathF.Abs(localX) <= volume.m_length / 2f && MathF.Abs(localY) <= volume.m_width / 2f;
        }

        var reach = TransportReach(volume);
        return (dx * dx) + (dy * dy) <= reach * reach;
    }

    private static bool VolumeNamed(Volume volume, Trigger trigger) {
        var volumeName = StripTransportName(volume.m_volumeName);
        foreach (var raw in new string[] {
            trigger.m_triggerName,
            (string)trigger.m_triggerObjInfo?.m_zoneTag,
            (string)trigger.m_triggerObjInfo?.m_triggerObjName,
        }) {
            var name = StripTransportName(raw);
            if (string.IsNullOrEmpty(volumeName) || string.IsNullOrEmpty(name)) {
                continue;
            }

            if (volumeName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || volumeName.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(volumeName, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            if (volumeName.Length >= 6 && name.Length >= 6
                && (volumeName.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(volumeName, StringComparison.OrdinalIgnoreCase))) {
                return true;
            }
        }

        return false;
    }

    private static bool VolumeFires(Volume volume, Trigger trigger)
        => trigger.m_fireEvents is { Count: > 0 } fires
           && volume.m_enterEvents?.Any(enter => fires.Any(fire =>
               string.Equals((string)enter, (string)fire, StringComparison.OrdinalIgnoreCase))) == true;

    private static float TriggerDistance(Volume volume, TriggerObjectInfo info) {
        if (volume is null || info is null) {
            return float.MaxValue;
        }

        var dx = volume.m_locationX - info.m_locationX;
        var dy = volume.m_locationY - info.m_locationY;
        return (dx * dx) + (dy * dy);
    }

    private static float TransportReach(Volume volume) {
        var reach = MathF.Max(volume.m_radius, MathF.Max(volume.m_length, volume.m_width) / 2f);
        return reach > 1f ? reach : 200f;
    }

    private static string StripTransportName(string name) {
        const string suffix = " instance";
        return !string.IsNullOrEmpty(name) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    private void SpawnTriggerObject(Trigger trigger) {
        // The object is a DYNAMIC_SERVER one, so the client only has it once MSG_NEWOBJECT arrives.
        var info = trigger.m_triggerObjInfo;
        string name = trigger.m_triggerName;
        if (info is null) {
            return;
        }

        if (info.m_templateID == 0) {
            return;
        }

        // A STATIC_CLIENT object is built by the client from its own zone data; the server only changes its state.
        if (info.m_loadingType == LoadingType.STATIC_CLIENT) {
            return;
        }

        if (string.IsNullOrEmpty(name)) {
            Logger.Debug("Trigger in {0}: object {1} skipped, the trigger has no name.",
                Logger.Args(Zone.ZonePath, info.m_zoneTag));

            return;
        }

        if (_triggerObjects.ContainsKey(name)) {
            Logger.Debug("Trigger {0} in {1}: object {2} skipped, the trigger already has its object.",
                Logger.Args(name, Zone.ZonePath, info.m_zoneTag));

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(info.m_templateID);
        if (template is null) {
            Logger.Warning("Trigger {0} in {1} owns object {2} with template ID {3}, which was not found; it is not spawned.",
                Logger.Args(name, Zone.ZonePath, info.m_zoneTag, info.m_templateID));

            return;
        }

        var coreObject = CoreObjectFactory.FinalizeCoreObject(info, template);
        if (coreObject is null) {
            Logger.Debug("Trigger {0} in {1}: object {2} skipped, the core object could not be finalized.",
                Logger.Args(name, Zone.ZonePath, info.m_zoneTag));

            return;
        }

        // TriggerObjectInfo hides the template id with its own property, so the base one the factory copies is 0.
        coreObject.m_templateID = info.m_templateID;

        // The trigger's own object comes back in its start state, not the "Off" it was hidden with.
        var objectTag = info.m_zoneTag;
        if (Zone.ScriptState.IsObjectIn(objectTag, DEACTIVATED_OBJECT_STATE) && !string.IsNullOrEmpty(info.m_startState)) {
            Zone.ScriptState.SetObjectState(objectTag, info.m_startState);
        }

        var actor = CreateEntityActor(coreObject, template, info);
        _triggerObjects[name] = (actor, coreObject, objectTag);
        Zone.NoteActor(name, actor);
        Zone.NoteActor(objectTag, actor);
        Logger.Debug("Trigger {0} in {1}: object {2} created, template {3} ({4}), location {5}, start state {6}, drawn by the client: {7}.",
            Logger.Args(name, Zone.ZonePath, objectTag, info.m_templateID, template.GetType().Name, info.m_location,
                info.m_startState, RenderComponent.ShouldAttachToEntity(template)));
    }

    private void DespawnTriggerObject(Trigger trigger) {
        // The object comes back when the trigger is enabled again.
        string name = trigger.m_triggerName;
        if (string.IsNullOrEmpty(name) || !_triggerObjects.Remove(name, out var owned)) {
            return;
        }

        StopTriggerObject(owned.Actor, owned.Object);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT))]
    private void ReceiveRemoveTriggerObject(ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT message) {
        var match = _triggerObjects.FirstOrDefault(x => string.Equals(x.Value.Tag, message.ObjectName, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null || !_triggerObjects.Remove(match.Key, out var owned)) {
            Logger.Debug("Zone {0}: no trigger object {1} to remove.", Logger.Args(Zone.ZonePath, message.ObjectName));

            return;
        }

        StopTriggerObject(owned.Actor, owned.Object);
    }

    private void StopTriggerObject(IActorRef actor, CoreObject triggerObject) {
        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT { GameObjectID = triggerObject.m_globalID },
            Targets = ZoneBroadcastTarget.Players,
        });
        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID { MobileId = triggerObject.m_nMobileID });

        EntityActors.Remove(actor);
        Context.Stop(actor);
    }

    private static bool HasTeleportResult(Trigger trigger)
        => trigger.m_results?.m_results?.Any(result => result is ResTeleport) == true;

    // The teleport handler sends a player with no destination zone out of a house zone only; anywhere else it does nothing.
    private bool LeadsSomewhere(Trigger trigger)
        => NamesDestination(trigger) || (HasTeleportResult(trigger) && HouseExits.TryResolve(Zone.ZonePath, out _, out _));

    private static bool NamesDestination(Trigger trigger)
        => trigger.m_results?.m_results?.Any(result => result is ResTeleport teleport
            && !string.IsNullOrEmpty(teleport.m_destinationZone)) == true;

    private Trigger PickTeleportTrigger(List<(Trigger Trigger, IActorRef Actor)> passing, ZONE_102_PROTOCOL.MSG_POSTEVENT message,
        HashSet<Trigger> doorsWithNeedsMet = null) {
        // ReqHasQuest also passes for a completed quest: prefer the trigger naming a currently active quest, else the last passing one.
        var teleports = passing.Where(x => HasTeleportResult(x.Trigger)).Select(x => x.Trigger).ToList();

        // A door the game ties to the very step the wizard is on wins over its everyday twin. The Commons' tunnel pairs
        // "Trigger-ToRavenwoodLite" (to the quest's Ravenwood, where Mr. Lincoln waits, while "To Ravenwood!" Goal 1 is
        // under way) with "TeleportToRavenwoodTrigger", whose "not on Goal 1 of any school's version" list also passes for a
        // wizard on it (the other schools' versions it doesn't hold). The everyday door won, so the step's NPC was never
        // reached and Ultimus went back and forth through the tunnel without end (Niduus, 2026-11-13: "I legit just don't see
        // Ultimus anymore").
        if (teleports.Count > 1 && ZoneTrigger.QueryWizard(message.PlayerActor) is { } onWizard) {
            var onStep = teleports.Where(t => NamesOpenStep(t.m_requirements, onWizard)).ToList();
            if (onStep.Count > 0 && onStep.Count < teleports.Count) {
                Logger.Information("Zone {0}: {1} doors passed; {2} is tied to the step the wizard is on, so it takes them.",
                    Logger.Args(Zone.ZonePath, teleports.Count, onStep[^1].m_triggerName));
                teleports = onStep;
                doorsWithNeedsMet = null;
            }
        }

        // With OpenAllDoors every door on the event passes, also the ones the game pairs by quest: Regent's Square's museum
        // door has "Trigger" (to the quest's Royal Museum, while A Mystery in Marleybone is on) and "Trigger 0" (to the
        // everyday museum, once a later quest is done). The last one always won, so the first Marleybone quest sent the
        // wizard to the wrong museum and they were stuck (Niduus 2026-11-09). The doors whose needs are really met come first.
        if (doorsWithNeedsMet is { Count: > 0 } && teleports.Count > 1) {
            var met = teleports.Where(doorsWithNeedsMet.Contains).ToList();
            if (met.Count > 0 && met.Count < teleports.Count) {
                Logger.Information("Zone {0}: doors are open to all, so {1} doors passed; {2} is the one whose quest needs are met.",
                    Logger.Args(Zone.ZonePath, teleports.Count, met[^1].m_triggerName));
                teleports = met;
            }
        }

        // A paired trigger whose teleport names no destination (the client data leaves it blank and spiraldb has none for it)
        // would win and take nobody anywhere, suppressing the one that does lead somewhere. Unicorn Way pairs the Hedge Maze
        // entrance with "And After Hedgemaze Quest" this way. Those only win when no passing teleport has a destination.
        var leading = teleports.Where(LeadsSomewhere).ToList();
        if (leading.Count > 0 && leading.Count < teleports.Count) {
            foreach (var dead in teleports.Except(leading)) {
                if (_deadTeleportsLogged.Add(dead.m_triggerName)) {
                    Logger.Information("Trigger {0} in {1} teleports with no destination; {2} takes the player instead.",
                        Logger.Args(dead.m_triggerName, Zone.ZonePath, leading[^1].m_triggerName));
                }
            }

            teleports = leading;
        }

        // In a house zone a trigger naming its own destination beats one that only falls back to the world's hub. The
        // dorm's exit pairs the boys' and girls' door triggers (each to its Ravenwood tower) with the first quest's
        // Trigger-WC-COMMONS-MAIN-002, which has none and would send the wizard to the Commons (Niduus, 2026-10-25).
        var named = teleports.Where(NamesDestination).ToList();
        if (named.Count > 0 && named.Count < teleports.Count) {
            foreach (var fallback in teleports.Except(named)) {
                if (_deadTeleportsLogged.Add(fallback.m_triggerName)) {
                    Logger.Information("Trigger {0} in {1} only leads to the world's hub; {2} names its destination and takes the player instead.",
                        Logger.Args(fallback.m_triggerName, Zone.ZonePath, named[^1].m_triggerName));
                }
            }

            teleports = named;
        }

        if (teleports.Count <= 1) {
            return teleports.FirstOrDefault();
        }

        HashSet<string> activeQuests = [];
        if (ZoneTrigger.QueryWizard(message.PlayerActor) is { } wizard) {
            activeQuests = wizard.QuestBehavior.CurrentQuestInstances
                .Select(q => q.QuestName).ToHashSet();
        }

        var qualifying = teleports
            .Where(t => CollectRequiredQuests(t.m_requirements).Any(activeQuests.Contains))
            .ToList();

        return qualifying.Count == 1 ? qualifying[0] : teleports[^1];
    }

    /// <summary>Whether a door's needs ask (not negated) for a step the wizard has open right now.</summary>
    private static bool NamesOpenStep(RequirementList list, WizardData.Models.Player.Wizard wizard) {
        if (list?.m_requirements is null) {
            return false;
        }

        foreach (var requirement in list.m_requirements) {
            if (requirement is RequirementList nested && !nested.m_applyNOT && NamesOpenStep(nested, wizard)) {
                return true;
            }

            if (requirement is not ReqHasGoal { m_applyNOT: false } goal || goal.m_requiredStatus == GoalStatusRequirement.Complete) {
                continue;
            }

            var questName = WizardData.QuestCodeAliases.Resolve(goal.m_questName);
            var held = wizard.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null && q.QuestName == questName);
            if (held is null || string.IsNullOrEmpty(goal.m_goalName)) {
                continue;
            }

            string goalName = goal.m_goalName;
            if (held.IsGoalActive(goalName)
                || (Quests.VanillaGoalNames.OurName(questName, goalName) is { } ours && held.IsGoalActive(ours))) {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CollectRequiredQuests(RequirementList list) {
        if (list?.m_requirements is null) {
            yield break;
        }

        foreach (var requirement in list.m_requirements) {
            if (requirement is RequirementList nested) {
                foreach (var name in CollectRequiredQuests(nested)) {
                    yield return name;
                }
            }
            else if (requirement is ReqHasQuest hasQuest && !hasQuest.m_applyNOT && !string.IsNullOrEmpty(hasQuest.m_questName)) {
                yield return hasQuest.m_questName;
            }
        }
    }

    // Events raised before object and kill events existed: zone entry, volumes and chained events.
    private bool IsLongRaisedEvent(string eventName)
        => eventName == START_ZONE_ENTER_EVENT
        || eventName.StartsWith("Enter_", StringComparison.Ordinal)
        || eventName.StartsWith("Exit_", StringComparison.Ordinal)
        || _chainedEvents.Contains(eventName);

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTSTAGEDCINEMATIC))]
    private void ReceiveStartStagedCinematic(ZONE_102_PROTOCOL.MSG_STARTSTAGEDCINEMATIC message) {
        // The end event is the waiting trigger's fire event that nothing in the zone posts; posted once per instance, as if the cutscene had played.
        var posted = _orderedTriggers
            .Where(x => x.Trigger?.m_results?.m_results is not null)
            .SelectMany(x => x.Trigger.m_results.m_results)
            .OfType<ResPostEvent>()
            .Select(x => x.m_eventName.ToString())
            .ToHashSet();

        var endEvents = _orderedTriggers
            .Where(x => x.Trigger?.m_fireEvents is not null)
            .SelectMany(x => x.Trigger.m_fireEvents)
            .Select(x => x.ToString())
            .Where(x => x.Contains("Cinematic", StringComparison.OrdinalIgnoreCase)
                && x.Contains("End", StringComparison.OrdinalIgnoreCase)
                && !x.EndsWith(".EnterState", StringComparison.Ordinal)
                && !x.StartsWith("Enter_", StringComparison.Ordinal)
                && !x.StartsWith("Exit_", StringComparison.Ordinal)
                && !posted.Contains(x))
            .Distinct()
            .Where(_cinematicEndsPosted.Add)
            .ToList();

        foreach (var endEvent in endEvents) {
            Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = endEvent,
                PlayerActor = message.PlayerActor,
                PlayerGameObject = message.PlayerGameObject,
            });
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT))]
    private void ReceiveModifyTriggerObject(ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT message) {
        var holdsObject = HoldsObject(message.ObjectName);
        if (message.Relayed && !holdsObject) {
            return;
        }

        ApplyObjectState(message.ObjectName, message.StateName, message.PlayerActor, message.PlayerGameObject, message.PlayerSpawned);

        // A goal result runs in the zone the player stands in; an object this zone does not hold may be in another zone of the instance.
        if (!holdsObject && !message.Relayed && Zone.IsInstance) {
            Context.ActorSelection(Context.Parent.Path.Parent).Tell(message, ZoneRef);
        }
    }

    private bool HoldsObject(string objectName)
        => _triggerObjects.Values.Any(x => string.Equals(x.Tag, objectName, StringComparison.OrdinalIgnoreCase))
           || Zone.ZoneData.m_objectList.Any(x => string.Equals(x.m_zoneTag, objectName, StringComparison.OrdinalIgnoreCase));

    private void ApplyObjectState(string objectName, string stateName, IActorRef playerActor, CoreObject playerObject, bool playerSpawned = false) {
        if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(stateName)) {
            return;
        }

        var previousState = Zone.ScriptState.GetObjectState(objectName);
        // Already in that state: nothing moves and nothing is raised (Katzenstein's Lab plaque resets fed on each other, LoopGuard).
        if (IsSameStateEcho(objectName, previousState, stateName)) {
            return;
        }

        Zone.ScriptState.SetObjectState(objectName, stateName);
        NoteLift(objectName, stateName);

        // A gate that opens plays its opening on the way (Closed to Open through Opening): the in-between state goes out
        // first and the end state once it has played (StateTransitions).
        if (Imlight.CoreLib.Game.States.StateTransitions.TryFind(Zone.ZonePath, objectName, previousState, stateName,
                out var passState, out var wait)) {
            Logger.Debug("Zone {0}: {1} plays {2} on its way to {3} ({4:0.#} s).",
                Logger.Args(Zone.ZonePath, objectName, passState, stateName, wait.TotalSeconds));
            SendObjectState(objectName, passState);
            Timers.StartSingleTimer($"state-pass-{objectName}", new ObjectStatePassDone(objectName, stateName), wait);
        }
        else {
            Timers.Cancel($"state-pass-{objectName}");
            SendObjectState(objectName, stateName);
        }

        Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = $"{objectName}.{stateName}.EnterState",
            PlayerActor = playerActor,
            PlayerGameObject = playerObject,
            PlayerSpawned = playerSpawned,
        });
    }

    // A lift or moving floor that moves (Big Ben's counterweight plays Raise2 on its way to IdleLevel2): the bots' way finder
    // walks onto it where the map shows no fixed floor (OverlayQuestPath.NoteLift).
    private void NoteLift(string objectName, string stateName) {
        try {
            if (!Imlight.CoreLib.Game.Tools.OverlayQuestPath.IsLiftState(stateName)) {
                return;
            }

            var held = _triggerObjects.Values.FirstOrDefault(x => string.Equals(x.Tag, objectName, StringComparison.OrdinalIgnoreCase)).Object;
            var spot = held?.m_location
                       ?? Zone.ZoneData.m_objectList.FirstOrDefault(x => string.Equals(x.m_zoneTag, objectName, StringComparison.OrdinalIgnoreCase))?.m_location;
            if (spot is { } at) {
                Imlight.CoreLib.Game.Tools.OverlayQuestPath.NoteLift(Zone.ZonePath, objectName, at.X, at.Y);
            }
        }
        catch (Exception) {
            // Only a help for the bots' way finder.
        }
    }

    private sealed record ObjectStatePassDone(string ObjectName, string StateName);

    // The animation on the way has played: the object settles in its end state, unless something moved it on since.
    [InternalMessageHandler(typeof(ObjectStatePassDone))]
    private void ReceiveObjectStatePassDone(ObjectStatePassDone message) {
        var current = Zone.ScriptState.GetObjectState(message.ObjectName);
        if (!string.Equals(current, message.StateName, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        SendObjectState(message.ObjectName, message.StateName);
    }

    private void SendObjectState(string objectName, string stateName) {
        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
                ObjectName = objectName,
                StateName = stateName,
                ExclusiveToSender = false,
            }],
            // Zone objects and the trigger-owned (server-spawned) ones live under different supervisors.
            Targets = ZoneBroadcastTarget.Objects | ZoneBroadcastTarget.Triggers,
        });
        // Client-only objects take the state through a dynamic mod.
        ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new CHARACTER_103_PROTOCOL.MSG_SENDDYNAMODSTATE {
                ObjectName = objectName,
                StateName = stateName,
            }],
            Targets = ZoneBroadcastTarget.Players,
        });
    }

    private readonly Dictionary<IActorRef, DateTime> _stormToldAt = [];

    // True when this is the Tomb of Storms' way into the Well of Spirits and the wizard still has storm obelisks to light.
    private bool StormSealed(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // OpenAllDoors opens this way too, as it does every door.
        if (s_openAllDoors || message.PlayerActor is null || !StormObelisks.IsCrypt(Zone.ZonePath)
            || !string.Equals(trigger.m_triggerName, StormObelisks.ENTRANCE_TRIGGER, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var wizard = ZoneTrigger.QueryWizard(message.PlayerActor);
        if (!StormObelisks.Shut(wizard)) {
            return false;
        }

        var left = StormObelisks.Unlit(wizard).Count();
        if (!_stormToldAt.TryGetValue(message.PlayerActor, out var told) || DateTime.UtcNow - told > TimeSpan.FromSeconds(15)) {
            _stormToldAt[message.PlayerActor] = DateTime.UtcNow;
            Logger.Information("Zone {0}: the way into the Well of Spirits stays shut for wizard {1}: {2} of the 3 storm obelisks still to light.",
                Logger.Args(Zone.ZonePath, wizard?.CharId, left));
            message.PlayerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = left == 1
                    ? "The way is sealed by storm magic. Light the last storm obelisk in the Tomb of Storms to open it."
                    : $"The way is sealed by storm magic. Light the {left} storm obelisks in the Tomb of Storms to open it.",
                Modal = 0,
            });
        }

        return true;
    }

    private bool EvaluateRequirements(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (trigger.m_requirements?.m_requirements is null || trigger.m_requirements.m_requirements.Count == 0) {
            return true;
        }

        return RequirementDispatcher.EvaluateRequirements(
            requirements: trigger.m_requirements,
            context: new ZoneRequirementContext(
                trigger.m_requirements,
                message.PlayerActor,
                message.PlayerGameObject,
                ZoneTrigger.QueryWizard(message.PlayerActor),
                ZoneRef,
                trigger.m_triggerName) {
                ScriptState = Zone.ScriptState,
                EventAdjectives = message.Adjectives,
                TriggerZonePath = Zone.ZonePath,
                InstanceQuestCompleted = Zone.IsInstance ? CopyJudgesQuest : null,
            });
    }

    /*
     * A dungeon copy runs its quests afresh each time (QuestService.TryGrantDungeonQuests), but the copy's doors asked the
     * wizard's registry whether a quest was done, and that remembers every earlier run. The Temple of Storms' way down to
     * the boss waits for the Test of Aptitude (KT-CRY5-C01-002): a wizard who passed it on an earlier run went down before
     * passing either test this time, though the pad stayed dark (Niduus 2026-11-19). A quest this copy gave out is now
     * judged by this copy; any other one still by the wizard's registry.
     */
    private bool? CopyJudgesQuest(string questName) {
        if (string.IsNullOrEmpty(questName) || QuestOptions.IsOff(questName)) {
            return null;
        }

        var progress = Zone.ScriptState.SnapshotQuestProgress();
        if (progress?.StartedQuests is not { } started || !started.Contains(questName, StringComparer.OrdinalIgnoreCase)) {
            return null;
        }

        return progress.CompletedQuests?.Contains(questName, StringComparer.OrdinalIgnoreCase) == true;
    }

    private void UpdateSpawnResultTriggers(ref List<Trigger> clientTriggers, List<SpawnObject> spawners, List<PathObjectTemplate> paths, List<NodeObject> nodes) {
        foreach (var trigger in clientTriggers) {
            if (trigger.m_results == null || trigger.m_results.m_results == null) {
                continue;
            }

            foreach (var result in trigger.m_results.m_results) {
                if (result is ResSpawn resSpawn) {
                    var spawnObjectList = spawners.Find(x => x.m_id == resSpawn.m_spawnID);
                    if (spawnObjectList != null) {
                        var spawnObject = spawnObjectList.m_spawnList[0];
                        var objectPath = paths.Find(x => x.m_id.Full == spawnObject.m_objectInfo.m_pathID.Full);
                        resSpawn.nodes = nodes.FindAll(x => objectPath.m_nodeIDs.Contains(x.m_id));
                        resSpawn.templateID = spawnObject.m_objectInfo.m_templateID;
                    }
                }
                else if (result is ResDespawn resDespawn) {
                    // The wad data only names the spawner; resolve the template so the removal can match an entity.
                    var spawnObjectList = spawners.Find(x => x.m_id == resDespawn.m_spawnID);
                    if (spawnObjectList != null) {
                        resDespawn.m_templateID = spawnObjectList.m_spawnList[0].m_objectInfo.m_templateID;
                    }
                }
            }
        }
    }

    private List<Trigger> ReplaceTriggerDataWithDatabase(WizZoneTriggers clientTriggers) {
        var triggers = new List<Trigger>(clientTriggers.m_triggers);
        var zoneName = Zone.ZonePath;

        // One single database query: great!
        var databaseTriggers = ZoneDataCollection.GetZoneData(zoneName);

        foreach (var trigger in triggers) {
            if (trigger is null) {
                continue;
            }

            // If there's persistent data associated with this trigger, load it.
            var persistentTriggerData = databaseTriggers?.Teleports
                .FirstOrDefault(x => x.TriggerName == trigger.m_triggerName);

            if (persistentTriggerData is not null) {
                // April Fools: if we've confirmed this is a zone transfer, we'll instead
                // grab a random zone transfer from the database. Then, we'll set the trigger
                // results to the random zone transfer.
                if (s_randomizeGateways) {
                    var randomZoneData = ZoneDataCollection.GetAprilFoolsRandomZoneData();
                    var randomIdx = new Random().Next(randomZoneData.Teleports.Count - 1);
                    var randomZoneTransfer = randomZoneData.Teleports[randomIdx];

                    persistentTriggerData.Teleport = randomZoneTransfer.Teleport;
                }

                // A trigger that already teleports (a ride waits, then teleports) keeps its other results and takes
                // the stored destination; any other trigger's results become the stored teleport.
                var existing = trigger.m_results?.m_results;
                var resultList = new ResultList {
                    m_results = existing is not null && existing.Any(r => r is ResTeleport)
                        ? [.. existing.Select(r => r is ResTeleport ? persistentTriggerData.Teleport : r)]
                        : [persistentTriggerData.Teleport]
                };
                trigger.m_results = resultList;
            }

            UseZoneObjectsExit(trigger);
        }

        // A trip the live server starts on a zone event that no trigger in the zone files answers (ZoneObjects
        // EventTeleports): the Young Battledrake's DS_BattleDrakeUse flies the wizard from the Dragonspyre Academy.
        foreach (var trip in Zone.Extras.EventTeleports) {
            var teleport = new ResTeleport { m_destinationZone = trip.Zone, m_destinationLoc = trip.Loc ?? "" };
            if (!string.IsNullOrEmpty(trip.WhileOnQuest)) {
                teleport = teleport with { m_requirements = OnQuest(trip.WhileOnQuest, trip.WhileOnGoal, not: false) };
            }

            triggers.Add(new Trigger {
                m_triggerName = $"ZoneObjects {trip.Event} teleport",
                m_fireEvents = [trip.Event],
                m_results = new ResultList { m_results = [teleport] },
            });
            Logger.Information("Zone {0}: event {1} now teleports to {2}.", Logger.Args(Zone.ZonePath, trip.Event, trip.Zone));
        }

        return triggers;
    }

    // A copy of the zone whose spiraldb-extra/ZoneObjects file gives it an exit (an added sigil's instance) sends its
    // teleports out there. The teleports are copied, so the stored ones and the zone's other copies are untouched.
    private void UseZoneObjectsExit(Trigger trigger) {
        var results = trigger.m_results?.m_results;
        if (results is null || !results.Any(r => r is ResTeleport)) {
            return;
        }

        var changed = false;
        var exits = results.SelectMany(r => {
            if (r is not ResTeleport teleport
                || !Zone.Extras.TryGetExit(trigger.m_triggerName, teleport.m_destinationZone, out var zone, out var loc,
                                           out var quest, out var goal)) {
                return new[] { r };
            }

            changed = true;
            var exit = teleport with { m_destinationZone = zone, m_destinationLoc = loc };
            if (string.IsNullOrEmpty(quest)) {
                return new Result[] { exit };
            }

            // One doorway, two trips: the exit while the player has the quest, the game's own teleport otherwise
            // (Dragonspyre Academy: the Fire School for Forged in Fire, else the dragon flight to Dragon's Roost).
            return new Result[] {
                exit with { m_requirements = OnQuest(quest, goal, not: false) },
                teleport with { m_requirements = OnQuest(quest, goal, not: true) },
            };
        }).ToList();

        if (changed) {
            trigger.m_results = new ResultList { m_results = exits };
            Logger.Information("Zone {0}: trigger {1} now leads to the ZoneObjects exit.",
                Logger.Args(Zone.ZonePath, trigger.m_triggerName));
        }
    }

    // On the step (ReqHasGoal, still to do), or with no step named the quest taken at all (ReqHasQuest counts a done
    // quest too, so the Academy names its step: Ultimus, done with Forged in Fire, was still sent into the school).
    private static RequirementList OnQuest(string quest, string goal, bool not)
        => new() {
            m_applyNOT = false, m_operator = Operator.ROP_AND,
            m_requirements = [
                string.IsNullOrEmpty(goal)
                    ? new ReqHasQuest { m_questName = quest, m_applyNOT = not, m_operator = Operator.ROP_AND }
                    : new ReqHasGoal {
                        m_questName = quest, m_goalName = goal, m_requiredStatus = GoalStatusRequirement.Incomplete,
                        m_applyNOT = not, m_operator = Operator.ROP_AND,
                    },
            ],
        };

}
