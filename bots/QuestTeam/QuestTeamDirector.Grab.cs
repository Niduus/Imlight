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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Timing a grab, as Ultimus does (UltimusDirector.Grab.cs; Niduus, 2026-11-15: the team caught up on everything Ultimus
/// can do). A quest thing to click with creatures walking about by it: the team waits out of their reach and goes for it
/// when none of them can reach it before it is grabbed. Creatures the step needs are no reason to wait. After 2 minutes
/// with no opening it goes anyway, as players would, and fights if it must.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private const float GRAB_AGGRO = 200f;
    private const float CREATURE_SPEED = 60f;
    private const float WALK_SPEED = 350f;
    private const float GRAB_SECONDS = 2f;
    private const float GRAB_DECIDE = 1000f;
    private const float GRAB_WAIT_AT = 650f;
    private const float GRAB_COMMIT = 350f;
    private static readonly TimeSpan s_grabPatience = TimeSpan.FromMinutes(2);

    private string _grabKey;
    private DateTime _grabSince = DateTime.MinValue;
    private bool _grabWaiting;
    private bool _grabGoing;

    /// <summary>True while the team waits at a safe spot for an opening to grab a quest thing (the leader has been sent there).</summary>
    private bool WaitForOpening(QuestTeamMember.Status leader, QuestTeamPlanner.Target target, float distance) {
        if (target.Kind != QuestTeamPlanner.USE || leader.ZoneActor is null || distance > GRAB_DECIDE) {
            return GrabDone(target);
        }

        var grabKey = $"{target.Key}|{(int) (target.X / 50)}|{(int) (target.Y / 50)}";
        if (_grabKey != grabKey) {
            (_grabKey, _grabSince, _grabWaiting, _grabGoing) = (grabKey, DateTime.UtcNow, false, false);
        }

        if (_grabGoing && distance < GRAB_COMMIT) {
            return GrabDone(target);
        }

        var clear = GRAB_AGGRO + (CREATURE_SPEED * ((distance / WALK_SPEED) + GRAB_SECONDS));
        var needed = NeededCreatures(target);
        var hot = MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && m.Object.m_globalID.Full != target.GlobalId
                        && !needed.Contains(m.Template) && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .Select(m => (m.Name, X: m.Object.m_location.X, Y: m.Object.m_location.Y,
                Far: QuestTeamPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, target.X, target.Y)))
            .Where(c => c.Far < clear)
            .OrderBy(c => c.Far).ToList();
        if (hot.Count == 0) {
            if (_grabWaiting) {
                Note(leader.Role, $"{leader.Name} sees an opening: no creature near \"{target.What}\" can reach it in time, so the team goes for it now.");
            }

            _grabGoing = true;
            return GrabDone(target);
        }

        if (DateTime.UtcNow - _grabSince > s_grabPatience) {
            if (_grabWaiting) {
                Note(leader.Role, $"The team waited {s_grabPatience.TotalMinutes:0} minutes for {hot[0].Name} to walk away from \"{target.What}\"; " +
                                  "it never did, so it goes for it anyway and fights if it must.");
            }

            return GrabDone(target);
        }

        if (!_grabWaiting) {
            _grabWaiting = true;
            Note(leader.Role, $"{leader.Name} holds back from \"{target.What}\": {hot[0].Name} is {hot[0].Far:0} from it" +
                              (hot.Count > 1 ? $" (and {hot.Count - 1} more creature(s))" : "") +
                              ". The team waits out of reach and watches for an opening, so it doesn't get pulled into a fight it doesn't need.");
        }

        _doing = $"waiting for an opening to grab \"{QuestTeamText.Short(target.What, 60)}\" ({hot[0].Name} is near it)";
        _lastProgress = DateTime.UtcNow;
        var nearest = hot.Min(c => QuestTeamPlanner.Far(leader.X, leader.Y, c.X, c.Y));
        if (nearest >= 450f && distance >= GRAB_WAIT_AT * 0.8f) {
            StopWalking(0);
            return true;
        }

        var (dx, dy) = (leader.X - target.X, leader.Y - target.Y);
        var len = MathF.Max(1, MathF.Sqrt((dx * dx) + (dy * dy)));
        WalkTo(0, new QuestTeamPlanner.Target(QuestTeamPlanner.SPOT, target.X + (dx / len * GRAB_WAIT_AT), target.Y + (dy / len * GRAB_WAIT_AT),
            $"a spot out of reach, to wait for an opening to grab {target.What}", target.Quest, target.Goal, Key: "grab-wait|" + target.Key));
        return true;
    }

    private bool GrabDone(QuestTeamPlanner.Target target) {
        _grabWaiting = false;
        if (target is null) {
            (_grabKey, _grabGoing) = (null, false);
        }

        return false;
    }

    // The creatures this quest still needs (the step's own, and ones that counted for its open steps before): no reason to wait.
    private HashSet<ulong> NeededCreatures(QuestTeamPlanner.Target target) {
        var needed = new HashSet<ulong>();
        if (target.Template != 0) {
            needed.Add(target.Template);
        }

        if (string.IsNullOrEmpty(target.Quest) || _team.Count == 0) {
            return needed;
        }

        foreach (var member in _team) {
            var held = member.Wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
                && string.Equals(q.QuestName, target.Quest, StringComparison.OrdinalIgnoreCase));
            foreach (var goal in held?.GoalProgress?.Where(g => g is not null && !g.IsGoalCompleted()) ?? []) {
                needed.UnionWith(UltimusDrops.Givers(target.Quest, goal.GoalName));
            }
        }

        return needed;
    }

}
