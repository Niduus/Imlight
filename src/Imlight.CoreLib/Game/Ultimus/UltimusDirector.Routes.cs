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
    // The door or stone "another way" walked it to, pressed once it is there.
    private (UltimusPads.Pad Pad, string Zone, DateTime At)? _otherWayPad;

    /// <summary>The door or stone to press on the way now: the way's own, else the one "another way" walked it to.</summary>
    private UltimusPads.Pad PadToPress(UltimusMember.Status member) {
        if (_viaPad is { Click: not 0 }) {
            return _viaPad;
        }

        if (_otherWayPad is { } other && other.Zone == member.Zone && DateTime.UtcNow - other.At < s_padGiveUp) {
            return other.Pad;
        }

        _otherWayPad = null;

        return null;
    }
    private DateTime _viaPadSince;
    // When the way to the teleporter was last walked: time away from it (a battle that caught it on the way, a heal, a
    // talk) does not count toward giving up on the teleporter (TCO7 Ra, Triton Avenue 2026-11-16: a 3-minute battle on
    // the way made it give up on the right teleporter to Suzie Gryphonbane and wander for 30 minutes).
    private DateTime _viaPadSeen;
    // Standing on a walked-onto teleporter that did not fire: when it got there, where it came from, and until when it
    // steps back off to walk in again, as a player does when a pad does not take them (TCO7 Cyndr stood 30 seconds on
    // Triton Avenue's pad to the Tempest Nexus, 2026-11-16, then went all the way round by the Commons ten times).
    private DateTime _padStandSince;
    private DateTime _padStepOffUntil;
    private (float X, float Y) _padFrom;
    private int _padReentries;
    private const float PAD_ON = 90f;
    private const float PAD_STEP_OFF = 260f;

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
        // Cut off by walls or water: the teleporter that leads there. Else a teleporter that is much faster than the walk.
        var left = PadsLeft();
        var pad = UltimusPads.Toward(member.Zone, member.X, member.Y, target.X, target.Y, left)
                  ?? UltimusPads.Faster(member.Zone, member.X, member.Y, target.X, target.Y, left);
        if (pad is null) {
            _viaPad = null;

            return (target.X, target.Y, target.What);
        }

        var now = DateTime.UtcNow;
        if (Equals(pad, _viaPad) && now - _viaPadSeen > TimeSpan.FromSeconds(20)) {
            _viaPadSince += now - _viaPadSeen;
        }

        _viaPadSeen = now;
        if (!Equals(pad, _viaPad)) {
            _viaPad = pad;
            _viaPadSince = now;
            _padStandSince = DateTime.MinValue;
            _padReentries = 0;
            _padFrom = (member.X, member.Y);
            var cutOff = OverlayQuestPath.Apart(member.Zone, member.X, member.Y, target.X, target.Y);
            Note("Ultimus", $"{member.Name} " + (cutOff ? $"can't walk to \"{target.What}\" from here: walls or water are in the way. "
                                                         : $"finds a faster way to \"{target.What}\" than walking. ") +
                            $"Takes the teleporter at ({pad.X:0}, {pad.Y:0}), which lands at ({pad.ToX:0}, {pad.ToY:0}).");
        }
        else if (DateTime.UtcNow - _viaPadSince > s_padGiveUp) {
            // Walked there and nothing happened (a pad that needs a quest, or one that is not there any more).
            Note("Ultimus", $"{member.Name}: the teleporter at ({pad.X:0}, {pad.Y:0}) did not take it anywhere; tries another way.", UltimusLink.ABNORMAL);
            _padsLeft[pad] = DateTime.UtcNow;
            _viaPad = null;

            return WayTo(member, target);
        }

        if (pad.Click == 0 && StepOffPad(member, pad) is { } off) {
            return (off.X, off.Y, $"stepping off the teleporter to walk in again, toward {target.What}");
        }

        return (pad.X, pad.Y, $"teleporter toward {target.What}");
    }

    /// <summary>
    /// On a walked-onto teleporter that has not fired after a few seconds: the spot a few steps back the way it came, to walk
    /// in again (a pad fires when a wizard walks into it, not while one stands on it). Null while it should just go on.
    /// </summary>
    private (float X, float Y)? StepOffPad(UltimusMember.Status member, UltimusPads.Pad pad) {
        var now = DateTime.UtcNow;
        var dx = _padFrom.X - pad.X;
        var dy = _padFrom.Y - pad.Y;
        var far = MathF.Sqrt((dx * dx) + (dy * dy));
        var off = far < 1 ? (X: pad.X + PAD_STEP_OFF, Y: pad.Y) : (X: pad.X + (dx / far * PAD_STEP_OFF), Y: pad.Y + (dy / far * PAD_STEP_OFF));
        if (now < _padStepOffUntil) {
            return off;
        }

        if (UltimusPlanner.Far(member.X, member.Y, pad.X, pad.Y) > PAD_ON || member.Walking) {
            _padStandSince = DateTime.MinValue;

            return null;
        }

        if (_padStandSince == DateTime.MinValue) {
            _padStandSince = now;

            return null;
        }

        if (now - _padStandSince < TimeSpan.FromSeconds(5) || _padReentries >= 3) {
            return null;
        }

        _padReentries++;
        _padStandSince = DateTime.MinValue;
        _padStepOffUntil = now.AddSeconds(4);
        Note("Ultimus", $"{member.Name} stands on the teleporter at ({pad.X:0}, {pad.Y:0}) and it has not fired: steps back off and walks in again " +
                        $"(try {_padReentries} of 3).", UltimusLink.MINOR);

        return off;
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

    // How near a door or stone of the way it presses it (a door's own reach is 140).
    private const float PRESS_REACH = 160f;

    // How near a door that opens on a fight (on his own ground) counts as his, about one tower floor, and how long before that door is fought for again.
    private const float GATE_NEAR = 6500f;
    private static readonly TimeSpan s_gateAgain = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTime> _gateFought = new(StringComparer.OrdinalIgnoreCase);

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

        // A door on his ground that the area's files say opens on beating a creature: he beats the creatures by it first, as
        // a player clears the room to open its door. Where none stand yet, he steps on the room's battle circle, which
        // starts its fight (Big Ben: each floor's door by the teleporter up opens once the floor's elite is beaten, and the
        // elite only comes out on the floor's battle circle; he stood at the sixth floor's south wall for hours, Niduus's
        // log 2026-11-16 00:25, "check him again").
        // A door already open is not fought for again (he stepped back on the sixth floor's used circle after its door opened).
        if (UltimusAreaBook.OpensOnFight(member.Zone, member.X, member.Y, GATE_NEAR, sameGround: true) is { } gate
            && !BotShared.BotFloors.Opens(Zone.Core.ZoneStates.Of(member.ZoneActor)?.GetObjectState(gate.Object) ?? "")
            && _gateFought.GetValueOrDefault(member.Zone + "|" + gate.Object) < DateTime.UtcNow - s_gateAgain) {
            if (BotShared.BotUseGuards.Near(member.ZoneActor, gate.X, gate.Y).Any(m => BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full))) {
                _gateFought[member.Zone + "|" + gate.Object] = DateTime.UtcNow;
                _clearAround = (member.Zone ?? "", gate.X, gate.Y, DateTime.UtcNow.AddMinutes(10));
                Note("Ultimus", $"{member.Name} is stuck on the way to \"{goal.What}\" by \"{gate.Object}\", which the area's files say opens on {gate.By}: " +
                                "he beats the creatures there first.", UltimusLink.NOTABLE);
                Think($"The door \"{gate.Object}\" here opens on {gate.By}. I beat the creatures by it first, as a player clears the room, then go on.",
                    "gate-fight|" + gate.Object);

                return true;
            }

            if (UltimusAreaBook.BattleStartNear(member.Zone, member.X, member.Y, GATE_NEAR) is { } circle) {
                _gateFought[member.Zone + "|" + gate.Object] = DateTime.UtcNow;
                Note("Ultimus", $"{member.Name} is stuck on the way to \"{goal.What}\": \"{gate.Object}\" opens on {gate.By}, and no creature is out yet, " +
                                $"so he steps on the room's battle circle \"{circle.Name}\" at ({circle.X:0}, {circle.Y:0}) to start its fight.", UltimusLink.NOTABLE);
                Think($"The door \"{gate.Object}\" opens on {gate.By}, but nobody is here to fight. The room's fight starts on its battle circle, " +
                      "so I step on it, win, and the door opens.", "gate-circle|" + gate.Object);
                var toCircle = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, circle.X, circle.Y, body: true);
                _actors[index].Tell(new UltimusMember.WalkTo(toCircle, $"steps on the battle circle that opens {gate.Object}"));

                return true;
            }
        }

        // The step on ground no known teleporter reaches: a teleporter he has not used yet, before one that goes back.
        if (OverlayQuestPath.Apart(member.Zone, member.X, member.Y, goal.X, goal.Y)
            && UltimusPads.Toward(member.Zone, member.X, member.Y, goal.X, goal.Y, PadsLeft()) is null
            && TryUntried(index, member, goal, goal.What)) {
            return true;
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
        if (pad.Click != 0) {
            _otherWayPad = (pad, member.Zone, DateTime.UtcNow);
        }

        Note("Ultimus", $"{member.Name} is stuck on the way to \"{goal.What}\": tries another way, the teleporter at ({pad.X:0}, {pad.Y:0}) " +
                        $"that lands nearer, at ({pad.ToX:0}, {pad.ToY:0}).", UltimusLink.ABNORMAL);
        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, pad.X, pad.Y, body: true);
        _actors[index].Tell(new UltimusMember.WalkTo(points, $"another way toward {goal.What}"));

        return true;
    }

}
