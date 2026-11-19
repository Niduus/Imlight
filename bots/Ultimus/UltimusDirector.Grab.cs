/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Timing a grab (Niduus, 2026-11-14: "better detection for environment interactables for quests. He's just getting into
/// fights because of their placement, which he should still go and get them when mobs are not around long enough to
/// catch him in a fight, unless he needs them"). A quest thing to click (a gemstone, a crate) with creatures walking about
/// near it: he waits at a spot out of their reach and watches them, and goes for it when none of them can reach it before
/// he has grabbed it and walked off. Creatures the step needs (the one it fights, or ones that counted for it before) are
/// no reason to wait. After 2 minutes of no opening he goes anyway, as a player would, and fights if he must.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // A creature starts a battle within about 175; a wandering one walks about 60 a second; he walks about 350 a second
    // and a grab takes about 2 seconds.
    private const float GRAB_AGGRO = 200f;
    private const float CREATURE_SPEED = 60f;
    private const float WALK_SPEED = 350f;
    private const float GRAB_SECONDS = 2f;

    // Nearer than this to the thing, he decides whether to go now or wait; farther, he keeps walking up.
    private const float GRAB_DECIDE = 1000f;

    // Where he waits: this far from the thing, on his side of it.
    private const float GRAB_WAIT_AT = 650f;
    private static readonly TimeSpan s_grabPatience = TimeSpan.FromMinutes(2);

    private string _grabKey;
    private DateTime _grabSince = DateTime.MinValue;
    private bool _grabWaiting;
    private bool _grabGoing;

    // This near the thing on his way in, he doesn't turn back.
    private const float GRAB_COMMIT = 350f;

    /// <summary>
    /// A thing to click with creatures near it: true while he waits at a safe spot for an opening (he has been sent
    /// there). False when he should walk up and grab it now.
    /// </summary>
    private bool WaitForOpening(UltimusMember.Status leader, UltimusPlanner.Target target, float distance) {
        if (target.Kind != UltimusPlanner.USE || leader.ZoneActor is null || distance > GRAB_DECIDE || SeekFights(leader)) {
            return GrabDone(target, false);
        }

        // One wait per thing (the gemstones of one step share the step's key, so its spot is part of it).
        var grabKey = GrabKey(target);
        if (_grabKey != grabKey) {
            (_grabKey, _grabSince, _grabWaiting, _grabGoing) = (grabKey, DateTime.UtcNow, false, false);
        }

        // Already on his way in and nearly there: he finishes the grab rather than turning back halfway.
        if (_grabGoing && distance < GRAB_COMMIT) {
            return GrabDone(target, false);
        }

        // How far a creature may walk while he gets there, grabs it and leaves: it must stay out of reach of the thing.
        var clear = GRAB_AGGRO + (CREATURE_SPEED * ((distance / WALK_SPEED) + GRAB_SECONDS));
        var needed = NeededCreatures(target);
        var hot = MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && m.Object.m_globalID.Full != target.GlobalId
                        && !needed.Contains(m.Template) && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .Select(m => (m.Name, X: m.Object.m_location.X, Y: m.Object.m_location.Y,
                Far: UltimusPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, target.X, target.Y)))
            .Where(c => c.Far < clear)
            .OrderBy(c => c.Far).ToList();
        if (hot.Count == 0) {
            if (_grabWaiting) {
                Note("Ultimus", $"{leader.Name} sees an opening: no creature near \"{target.What}\" can reach it in time, so he goes for it now.");
                Think($"The creatures by \"{UltimusText.Short(target.What, 60)}\" walked off. Now's my chance: I grab it and get out.", "grab-go|" + target.Key);
            }

            _grabGoing = true;
            return GrabDone(target, false);
        }

        if (DateTime.UtcNow - _grabSince > s_grabPatience) {
            if (_grabWaiting) {
                Note("Ultimus", $"{leader.Name} waited {s_grabPatience.TotalMinutes:0} minutes for {hot[0].Name} to walk away from \"{target.What}\"; " +
                                "it never did, so he goes for it anyway and fights if he must.");
                Think($"{hot[0].Name} won't leave \"{UltimusText.Short(target.What, 60)}\" alone. I've waited long enough: I go for it, and fight if I have to.",
                    "grab-anyway|" + target.Key);
            }

            return GrabDone(target, false);
        }

        if (!_grabWaiting) {
            _grabWaiting = true;
            Note("Ultimus", $"{leader.Name} holds back from \"{target.What}\": {hot[0].Name} is {hot[0].Far:0} from it" +
                            (hot.Count > 1 ? $" (and {hot.Count - 1} more creature(s))" : "") +
                            ". He waits out of reach and watches for an opening, so he doesn't get pulled into a fight he doesn't need.");
            Think($"{hot[0].Name} is walking around right by \"{UltimusText.Short(target.What, 60)}\". If I run in now it catches me, " +
                  "so I wait out of its reach and go when it walks off.", "grab-wait|" + target.Key);
        }

        _doing = $"waiting for an opening to grab \"{UltimusText.Short(target.What, 60)}\" ({hot[0].Name} is near it)";
        _lastProgress = DateTime.UtcNow;

        // Where he waits: on his side of the thing, out of reach of every creature near it; where he stands when that is
        // already so.
        var nearest = hot.Min(c => UltimusPlanner.Far(leader.X, leader.Y, c.X, c.Y));
        if (nearest >= MOB_WATCH && distance >= GRAB_WAIT_AT * 0.8f) {
            StopWalking(0);
            return true;
        }

        var (dx, dy) = (leader.X - target.X, leader.Y - target.Y);
        var len = MathF.Max(1, MathF.Sqrt((dx * dx) + (dy * dy)));
        var spot = new UltimusPlanner.Target(UltimusPlanner.SPOT, target.X + (dx / len * GRAB_WAIT_AT), target.Y + (dy / len * GRAB_WAIT_AT),
            $"a spot out of reach, to wait for an opening to grab {target.What}", target.Quest, target.Goal, Key: "grab-wait|" + target.Key);
        WalkTo(0, spot);
        return true;
    }

    private bool GrabDone(UltimusPlanner.Target target, bool waiting) {
        if (_grabKey is not null && (target is null || _grabKey != GrabKey(target))) {
            (_grabKey, _grabWaiting, _grabGoing) = (null, false, false);
        }

        _grabWaiting &= waiting;
        return waiting;
    }

    private static string GrabKey(UltimusPlanner.Target target) => $"{target.Key}|{(int) (target.X / 50)}|{(int) (target.Y / 50)}";

    // The creatures this quest still needs: the step's own one, and ones that counted for any of its open steps before
    // (UltimusDrops). Those are no reason to wait.
    private HashSet<ulong> NeededCreatures(UltimusPlanner.Target target) {
        var needed = new HashSet<ulong>();
        if (target.Template != 0) {
            needed.Add(target.Template);
        }

        if (string.IsNullOrEmpty(target.Quest) || _team.Count == 0) {
            return needed;
        }

        var held = _team[0].Wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
            && string.Equals(q.QuestName, target.Quest, StringComparison.OrdinalIgnoreCase));
        foreach (var goal in held?.GoalProgress?.Where(g => g is not null && !g.IsGoalCompleted()) ?? []) {
            needed.UnionWith(UltimusDrops.Givers(target.Quest, goal.GoalName));
        }

        return needed;
    }

}
