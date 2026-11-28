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
using System.Collections.Concurrent;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Game.Events;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Raids;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResTeleportHandler : BaseResultHandler<ResTeleport> {

    public override bool Execute(IResultContext context) {
        if (context.GetPlayerObj() is not WizClientObject playerObj) {
            return false;
        }

        // A player who spawned inside a door's volume would be sent straight back out of it.
        if (context.IsPlayerSpawn()) {
            return true;
        }

        var destinationZone = Result.m_destinationZone;
        var destinationLocation = Result.m_destinationLoc;

        if (context is GenericResultContext { TriggerName: { } triggerName }
            && context.GetZonePath() is { } forwardZone
            && forwardZone.Contains("/Interiors/", StringComparison.OrdinalIgnoreCase)
            && TryDungeonForwardGate(playerObj.m_characterId, forwardZone, triggerName)
            && ActiveDungeonRoom(context.GetPlayerRef(), forwardZone, playerObj.m_characterId) is { } activeRoom) {
            // Watched (Niduus 2026-11-28): this one replaces a destination the data already gives.
            Logger.Information("Dungeon room (A): trigger {0} in {1} goes forward to {2} ({3}) for {4}, in place of its own {5} ({6}).",
                Logger.Args(triggerName, forwardZone, activeRoom.Zone, activeRoom.Location, playerObj.m_characterId,
                    string.IsNullOrEmpty(destinationZone) ? "none" : destinationZone, destinationLocation));
            destinationZone = activeRoom.Zone;
            destinationLocation = activeRoom.Location;
        }

        // A clickable portal (Dragon's Roost's portal to the Academy) runs its own teleport, which names no destination:
        // the teleport stored under the portal's name (SpiralDB or spiraldb-extra/DungeonTeleports) says where it goes.
        if (string.IsNullOrEmpty(destinationZone) && StoredTeleport(context) is { } stored) {
            destinationZone = stored.m_destinationZone;
            destinationLocation = stored.m_destinationLoc;
        }

        // An airship flight (Marleybone's blimps to Chelsea Court and the Ironworks) teleports the player off at the end
        // of the trip, and names no destination either: it goes where the ride's own sign goes (RideZones). The players
        // sat on the blimp after it landed until they clicked the sign (Niduus 2026-11-19: zone transfers not working).
        if (string.IsNullOrEmpty(destinationZone)
            && Imlight.CoreLib.Game.World.RideZones.TryGetTripEnd(context.GetZonePath(), out var tripZone, out var tripLoc)) {
            destinationZone = tripZone;
            destinationLocation = tripLoc;
        }

        if (string.IsNullOrEmpty(destinationZone)) {
            if (context is GenericResultContext { TriggerName: { } roomTrigger }
                && !roomTrigger.Contains("Backdoor", StringComparison.OrdinalIgnoreCase)
                && !roomTrigger.Contains("Exit", StringComparison.OrdinalIgnoreCase)
                && IsForwardDungeonTrigger(roomTrigger)
                && ActiveDungeonRoom(context.GetPlayerRef(), context.GetZonePath(), playerObj.m_characterId) is { } roomTarget) {
                destinationZone = roomTarget.Zone;
                destinationLocation = roomTarget.Location;
                Logger.Information("Dungeon room (B): teleport trigger {0} in {1} names no destination; goes forward to {2} ({3}) for {4}.",
                    Logger.Args(roomTrigger, context.GetZonePath(), destinationZone, destinationLocation, playerObj.m_characterId));
            }

            // A raid's teleport pad names no destination either: it goes to the location at the pad's other end.
            if (context is GenericResultContext { TriggerName: { } padTriggerName }
                && Raids.Raids.TeleportTarget(context.GetZonePath(), padTriggerName) is { } padTarget) {
                return RaidResult.MoveTo(context, padTarget);
            }

            // So does a Beastmoon arena's (Game/Battlegrounds/BattlegroundTeleports.cs).
            if (context is GenericResultContext { TriggerName: { } bgTrigger }
                && Battlegrounds.BattlegroundTeleports.Target(context.GetZonePath(), bgTrigger) is { } bgTarget) {
                return RaidResult.MoveTo(context, bgTarget);
            }

            // The Deckathalon's tower floors name none either: their portals go up a floor or back to the arena.
            if (Events.Deckathalon.FloorTeleport(context.GetPlayerRef(), playerObj.m_characterId, context.GetZonePath(),
                    (context as GenericResultContext)?.TriggerName)) {
                return true;
            }

            // The exits of a house zone name no destination, so the player leaves to the hub of the house's world.
            if (!HouseExits.TryResolve(context.GetZonePath(), out destinationZone, out destinationLocation)) {
                // A dungeon's exit that names none takes the player back to where they were before the dungeon (their
                // session knows whether this zone is an instance and where that was). Some exits are not called Exit:
                // the Besieged Ship's is Teleport_2_Bale and the Hungry Lizard's "Teleport location", and they name no
                // spot either, so any such teleport inside an interior is its way out (Niduus, 2026-11-12: stuck there).
                var exitName = (context as GenericResultContext)?.TriggerName;
                var interiorWayOut = string.IsNullOrEmpty(Result.m_destinationLoc)
                    && (context.GetZonePath() ?? "").Contains("/Interiors/", StringComparison.OrdinalIgnoreCase);
                if (exitName is not null
                    && (exitName.Contains("Exit", StringComparison.OrdinalIgnoreCase) || interiorWayOut)) {
                    context.GetPlayerRef().Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                        LeaveInstance = true,
                        ByZoneDoor = true,
                        SendToClient = true,
                        OwnerCharId = playerObj.m_characterId,
                    });

                    return true;
                }

                Logger.Warning("A teleport in {0} names no destination zone; the player stays where they are.",
                    Logger.Args(context.GetZonePath()));

                return false;
            }
        }

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = destinationZone,
            DestinationLocation = destinationLocation,
            SendToClient = true,
            ByZoneDoor = true,
            OwnerCharId = playerObj.m_characterId
        };

        context.GetPlayerRef().Tell(msg);

        return true;
    }

    private sealed record DungeonRoomTarget(string Zone, string Location);

    private static readonly ConcurrentDictionary<(ulong CharacterId, string Zone, string Trigger), DateTime> s_forwardEvents = new();

    private static bool IsForwardDungeonTrigger(string triggerName)
        => triggerName?.Contains("(1)", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Trigger teleport", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Room", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Floor", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Level", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Next", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("Passage", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Contains("TeleportVol", StringComparison.OrdinalIgnoreCase) == true
           || triggerName?.Equals("To_Alto_Alto_Secret", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryDungeonForwardGate(ulong charId, string zone, string trigger) {
        var key = (charId, zone, trigger);
        var now = DateTime.UtcNow;
        if (s_forwardEvents.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(900)) {
            return false;
        }

        s_forwardEvents[key] = now;
        return true;
    }

    private static DungeonRoomTarget ActiveDungeonRoom(IActorRef playerActor, string currentZone, ulong charId) {
        if (playerActor is null
            || string.IsNullOrEmpty(currentZone)
            || !currentZone.Contains("/Interiors/", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        try {
            var character = playerActor
                .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(
                    new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                    TimeSpan.FromMilliseconds(750))
                .Result;
            var wizard = character?.Wizard;
            if (wizard?.QuestBehavior?.CurrentQuestInstances is null) {
                Logger.Information("Dungeon room: no active wizard quest list in {0}.", Logger.Args(currentZone));
                return null;
            }

            foreach (var instance in wizard.QuestBehavior.CurrentQuestInstances) {
                var quest = QuestTemplateCollection.GetQuestByName(instance.QuestName);
                if (quest?.m_goals is null) {
                    continue;
                }

                foreach (var progress in instance.GoalProgress ?? []) {
                    if (!instance.IsGoalActive(progress.GoalName)) {
                        continue;
                    }

                    var goal = quest.m_goals.FirstOrDefault(g =>
                        g is not null && g.m_goalName == progress.GoalName);
                    var destination = (string)(goal?.m_destinationZone ?? "");
                    if (string.IsNullOrEmpty(destination)
                        || string.Equals(destination, currentZone, StringComparison.OrdinalIgnoreCase)
                        || !destination.Contains("/Interiors/", StringComparison.OrdinalIgnoreCase)
                        || !SameDungeonFamily(currentZone, destination)) {
                        continue;
                    }

                    Logger.Information("Dungeon room: active goal {0}/{1} leads forward to {2}.",
                        Logger.Args(instance.QuestName, progress.GoalName, destination));
                    return new DungeonRoomTarget(destination, "Start");
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Could not resolve an active dungeon-room goal for character {0}: {1}",
                Logger.Args(charId, ex.Message));
        }

        return null;
    }

    private static bool SameDungeonFamily(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)
            || a.LastIndexOf('/') < 0
            || b.LastIndexOf('/') < 0) {
            return false;
        }

        var aParent = a[..a.LastIndexOf('/')];
        var bParent = b[..b.LastIndexOf('/')];
        return aParent.Equals(bParent, StringComparison.OrdinalIgnoreCase);
    }

    // The stored teleport of the trigger or object these results run for, when it names a destination.
    private static ResTeleport StoredTeleport(IResultContext context) {
        if (context is not GenericResultContext { TriggerName: { Length: > 0 } name } || context.GetZonePath() is not { Length: > 0 } zone) {
            return null;
        }

        var stored = Imlight.CoreLib.WizardData.Collections.ZoneDataCollection.GetZoneData(zone)?.Teleports?
            .FirstOrDefault(t => string.Equals(t?.TriggerName, name, StringComparison.Ordinal))?.Teleport;

        return string.IsNullOrEmpty(stored?.m_destinationZone) ? null : stored;
    }

}
