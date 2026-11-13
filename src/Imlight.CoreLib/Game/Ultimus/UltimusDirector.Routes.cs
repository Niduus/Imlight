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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How Ultimus gets there like a player (Niduus, 2026-11-13: "more advance thinking, pathing and reasoning ... emulate the
/// abilities of a normal human being doing the main story quest lines"): before a walk it checks whether walls or water
/// cut the step off from where it stands, and if so it takes the area's teleporters (UltimusPads) instead of walking into
/// the water's edge; it walks round creatures it does not need to fight on the way; and when stuck it tries another way (a
/// teleporter whose landing is nearer the step) before it asks the server to send it back to the area's start.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // A teleporter it walked to that did not take it anywhere within this long is left alone for a while.
    private static readonly TimeSpan s_padGiveUp = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_padForget = TimeSpan.FromMinutes(10);

    private readonly Dictionary<UltimusPads.Pad, DateTime> _padsLeft = [];
    private UltimusPads.Pad _viaPad;
    private DateTime _viaPadSince;

    private HashSet<UltimusPads.Pad> PadsLeft() {
        foreach (var old in _padsLeft.Where(p => DateTime.UtcNow - p.Value > s_padForget).Select(p => p.Key).ToList()) {
            _padsLeft.Remove(old);
        }

        return [.. _padsLeft.Keys];
    }

    /// <summary>
    /// Where to walk for <paramref name="target"/>: the target itself, or the teleporter that leads toward it when walls or
    /// water cut it off. The words say which.
    /// </summary>
    private (float X, float Y, string What) WayTo(UltimusMember.Status member, UltimusPlanner.Target target) {
        var pad = UltimusPads.Toward(member.Zone, member.X, member.Y, target.X, target.Y, PadsLeft());
        if (pad is null) {
            _viaPad = null;

            return (target.X, target.Y, target.What);
        }

        if (!Equals(pad, _viaPad)) {
            _viaPad = pad;
            _viaPadSince = DateTime.UtcNow;
            Note("Ultimus", $"{member.Name} can't walk to \"{target.What}\" from here: walls or water are in the way. Takes the teleporter at " +
                            $"({pad.X:0}, {pad.Y:0}), which lands at ({pad.ToX:0}, {pad.ToY:0}).");
        }
        else if (DateTime.UtcNow - _viaPadSince > s_padGiveUp) {
            // Walked there and nothing happened (a pad that needs a quest, or one that is not there any more).
            Note("Ultimus", $"{member.Name}: the teleporter at ({pad.X:0}, {pad.Y:0}) did not take it anywhere; tries another way.", UltimusLink.ABNORMAL);
            _padsLeft[pad] = DateTime.UtcNow;
            _viaPad = null;

            return WayTo(member, target);
        }

        return (pad.X, pad.Y, $"teleporter toward {target.What}");
    }

    /// <summary>
    /// Creatures to walk round on the way: every free creature in the area except the one it is going to fight (a player
    /// running to the next step does not run through a pack of creatures).
    /// </summary>
    private static List<(float X, float Y)> CreaturesToAvoid(UltimusMember.Status member, UltimusPlanner.Target target) {
        if (target.Kind is UltimusPlanner.FIGHT) {
            return null;
        }

        var list = MinimapIndex.In(member.ZoneActor)
            .Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && m.Object.m_globalID.Full != target.GlobalId
                        && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            // The step itself may stand among them (a thing to click by a creature): those right by it are not avoided.
            .Where(m => UltimusPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, target.X, target.Y) > 250)
            .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();

        return list.Count > 0 ? list : null;
    }

    // How far the nearest teleporter may be when it can't move at all.
    private const float PAD_LAST_RESORT = 3000f;

    /// <summary>
    /// Stuck a second time: another way, as a player would try, before going back to the area's start. True when there is
    /// one (a teleporter here whose landing is nearer the step).
    /// </summary>
    private bool TryAnotherWay(int index) {
        var member = _team[index];
        if (_leaderLastTarget is not { } goal) {
            return false;
        }

        var left = PadsLeft();
        if (_viaPad is not null) {
            left.Add(_viaPad);
        }

        // A teleporter whose landing is nearer the step; else, when it can't move at all (stood still on the same spot each
        // time), the nearest teleporter on the ground it stands on, as a player who can't get off a ledge takes the pad.
        if ((UltimusPads.Another(member.Zone, member.X, member.Y, goal.X, goal.Y, left)
             ?? UltimusPads.Nearest(member.Zone, member.X, member.Y, left, PAD_LAST_RESORT)) is not { } pad) {
            return false;
        }

        _padsLeft[pad] = DateTime.UtcNow;
        Note("Ultimus", $"{member.Name} is stuck on the way to \"{goal.What}\": tries another way, the teleporter at ({pad.X:0}, {pad.Y:0}) " +
                        $"that lands nearer, at ({pad.ToX:0}, {pad.ToY:0}).", UltimusLink.ABNORMAL);
        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, pad.X, pad.Y, body: true);
        _actors[index].Tell(new UltimusMember.WalkTo(points, $"another way toward {goal.What}"));

        return true;
    }

}
