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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Core;

using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Dungeon rooms for the Quest Team, the same rule as Ultimus (UltimusDirector.Rooms.cs, BotShared/BotFloors): in a dungeon
/// or instance the leader steps on each room's battle circle and the team wins its fight, beats the creatures by a door
/// that opens on a fight, steps on the spots that open doors, and sets a puzzle's plates right before pulling its lever,
/// before going on to the step's teleporter.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    // Steps and clicks made per floor duty, for this copy of the area.
    private readonly Dictionary<string, int> _floorTries = new(StringComparer.Ordinal);
    private string _floorZone = "";
    // The spot the leader stands on now (stepped on once); it steps off before stepping on it again.
    private string _floorOn;
    private readonly HashSet<string> _floorSaid = new(StringComparer.Ordinal);

    private QuestTeamPlanner.Target FloorDuty(QuestTeamMember.Status leader, QuestTeamPlanner.Target target) {
        if (string.IsNullOrEmpty(leader?.Zone) || !(leader.Wizard?.ZoneIsInstance == true || OverlayQuestRoute.IsDungeon(leader.Zone))) {
            return null;
        }

        // A quest fight in this room goes first; one on another floor or further on waits until this floor is done (the
        // floor's creatures, its lever, then up: Niduus, 2026-11-16, Counterweight East).
        if (target?.Kind == QuestTeamPlanner.FIGHT && QuestTeamPlanner.Far(leader.X, leader.Y, target.X, target.Y) <= BotFloors.ROOM
            && BotFloors.OnFloor(leader.Z, FightZ(leader, target))) {
            return null;
        }

        var zoneKey = leader.Zone + "|" + (leader.ZoneActor?.Path.ToString() ?? "");
        if (zoneKey != _floorZone) {
            _floorZone = zoneKey;
            _floorTries.Clear();
            _floorOn = null;
            _floorSaid.Clear();
        }

        var book = BotFloors.BookOf(leader.Zone);
        if (book.IsEmpty) {
            return null;
        }

        var state = ZoneStates.Of(leader.ZoneActor);
        var live = new BotFloors.Live(
            name => state?.GetObjectState(name),
            trigger => state?.GetTriggerFires(trigger) ?? 0,
            trigger => state is null ? null : state.IsTriggerEnabled(trigger),
            (x, y) => BotUseGuards.Near(leader.ZoneActor, x, y).Any(m => DudCreatures.Fightable(m.Object.m_globalID.Full)
                                                                    && BotFloors.OnFloor(leader.Z, m.Object.m_location.Z)),
            (x, y) => OverlayQuestPath.IslandOf(leader.Zone, leader.X, leader.Y) is var a && OverlayQuestPath.IslandOf(leader.Zone, x, y) is var b
                      && (a < 0 || b < 0 || a == b)) {
            EncounterDone = BotFloors.EncounterDoneFor(leader.Wizard),
        };
        if (BotFloors.Next(book, live, leader.X, leader.Y, _floorTries, leader.Z) is not { } duty) {
            _floorOn = null;
            return null;
        }

        // Through an opened door only when the step is not in this room.
        if (duty.Key.StartsWith("through|", StringComparison.Ordinal) && target is not null
            && BotFloors.Far(leader.X, leader.Y, target.X, target.Y) <= BotFloors.ROOM && live.SameGround(target.X, target.Y)) {
            return null;
        }

        var far = QuestTeamPlanner.Far(leader.X, leader.Y, duty.X, duty.Y);
        if (_floorSaid.Add(duty.Key)) {
            Note(leader.Role, $"The team does this floor first, as players play it: {duty.What}, because {duty.Why}.", QuestTeamLink.NOTABLE);
        }

        switch (duty.Kind) {
            case BotFloors.FIGHT: {
                var foe = BotUseGuards.Near(leader.ZoneActor, duty.X, duty.Y)
                    .Where(m => DudCreatures.Fightable(m.Object.m_globalID.Full))
                    .OrderBy(m => QuestTeamPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
                if (foe is null) {
                    return null;
                }

                return new QuestTeamPlanner.Target(QuestTeamPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
                    $"{foe.Name}, {duty.What} (the floor's fight comes before the teleporter on)", _currentQuest ?? "", Template: foe.Template,
                    GlobalId: foe.Object.m_globalID.Full, Key: "floor|fight|" + foe.Object.m_globalID.Full);
            }
            case BotFloors.CLICK when far <= 220: {
                var lever = leader.Options.Where(kv => kv.Value.Any(o => o.Service == "Interact")).Select(kv => kv.Key).FirstOrDefault();
                if (lever != 0 && CanClick("floor|" + duty.Key)) {
                    Clicked("floor|" + duty.Key);
                    _floorTries[duty.Key] = _floorTries.GetValueOrDefault(duty.Key) + 1;
                    Note(leader.Role, $"{leader.Name} pulls {duty.What}: {duty.Why}.", QuestTeamLink.NOTABLE);
                    _actors[0].Tell(new QuestTeamMember.Interact(lever, "Interact", 0, duty.What));
                }
                else if (lever == 0 && CanClick("floor|" + duty.Key + "|none")) {
                    Clicked("floor|" + duty.Key + "|none");
                    _floorTries[duty.Key] = _floorTries.GetValueOrDefault(duty.Key) + 1;
                    Note(leader.Role, $"{leader.Name} is at {duty.What}, but it offers nothing to pull yet.", QuestTeamLink.ABNORMAL);
                }

                return new QuestTeamPlanner.Target(QuestTeamPlanner.SPOT, leader.X, leader.Y, $"at {duty.What}", Key: "floor|" + duty.Key);
            }
            case BotFloors.CLICK:
                return new QuestTeamPlanner.Target(QuestTeamPlanner.SPOT, duty.X + 60, duty.Y, $"to {duty.What}", Key: "floor|" + duty.Key);
        }

        // A spot to step on: counted once on arrival; stepped off first when it has to be stepped on again.
        if (far <= BotFloors.ON_SPOT && _floorOn != duty.Key) {
            _floorOn = duty.Key;
            _floorTries[duty.Key] = _floorTries.GetValueOrDefault(duty.Key) + 1;
            Note(leader.Role, $"{leader.Name} steps on {duty.What}: {duty.Why}.", QuestTeamLink.NOTABLE);
        }

        if (_floorOn == duty.Key && far <= 300) {
            if (_floorTries.GetValueOrDefault(duty.Key) >= BotFloors.TRIES || duty.Key.StartsWith("circle|", StringComparison.Ordinal)) {
                return null;
            }

            // Off the spot and back on, as a player re-steps a plate that did not turn.
            var (dx, dy) = (leader.X - duty.X, leader.Y - duty.Y);
            var len = MathF.Max(1, MathF.Sqrt((dx * dx) + (dy * dy)));
            return new QuestTeamPlanner.Target(QuestTeamPlanner.SPOT, duty.X + (dx / len * 380), duty.Y + (dy / len * 380),
                $"off {duty.What}, to step on it again", Key: "floor|off|" + duty.Key);
        }

        if (far > 300 && _floorOn == duty.Key) {
            _floorOn = null;
        }

        return new QuestTeamPlanner.Target(QuestTeamPlanner.SPOT, duty.X, duty.Y, $"onto {duty.What}", Key: "floor|" + duty.Key);
    }

    /// <summary>The height of a fight's creature (its target's own, else the creature's on the map), NaN when unknown.</summary>
    private static float FightZ(QuestTeamMember.Status leader, QuestTeamPlanner.Target target) {
        if (!float.IsNaN(target.Z)) {
            return target.Z;
        }

        try {
            return target.GlobalId != 0 && MinimapIndex.In(leader.ZoneActor).FirstOrDefault(m => m.Object.m_globalID.Full == target.GlobalId) is { } mark
                ? mark.Object.m_location.Z
                : float.NaN;
        }
        catch (Exception) {
            return float.NaN;
        }
    }

}
