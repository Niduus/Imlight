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
using Akka.Actor;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Never stuck in one place (Niduus, 2026-11-13: "Parameters should be in place to prevent that"), as the Quest Team: when
/// Ultimus should be getting somewhere and has stood still over a minute it steps aside and finds its way again, the next
/// time it goes further off with a fresh try at every click, and after that it is written up pink (ABNORMAL).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    /// <summary>How long a wizard that is stepping aside is left to it before orders come again.</summary>
    private static readonly TimeSpan UNSTICK_FOR = TimeSpan.FromSeconds(6);

    private readonly BotStuck _stuck = new();
    private readonly Dictionary<int, DateTime> _unstickUntil = [];

    // Stepping aside: the other orders wait a few seconds.
    private bool Unsticking(int index) => _unstickUntil.TryGetValue(index, out var until) && DateTime.UtcNow < until;

    private void LookForStuck() {
        var resting = _healWaiting;
        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            if (_stuck.Look(i, member.Zone, member.X, member.Y, resting) is var tries and > 0) {
                Unstick(i, tries);
            }
        }
    }

    private void Unstick(int index, int tries) {
        var member = _team[index];
        var where = $"{UltimusText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0})";
        var seconds = (int) BotStuck.STILL.TotalSeconds;
        var quest = _target?.Quest ?? _currentQuest;

        // A fresh look at the way: the old walk and the old order are forgotten.
        _sent.Remove(index);
        OverlayQuestPath.Forget(member.CharId);
        _target = null;

        if (tries >= 3) {
            Issue("team", quest,
                $"{member.Name} is stuck in {where} while {_doing}: it stood still over {seconds} seconds {tries} times and stepping aside did not free it.",
                key: $"unstick|{member.Zone}|{index}", skipStep: false, level: UltimusLink.ABNORMAL);
        }

        // The self-check: stepping aside twice did not free it, so the server takes it back to where the area starts (a
        // place cut off from the way on, such as the Kraken isle, is left that way), and the way is found again from there.
        if (tries >= 3) {
            Note("Ultimus", $"{member.Name} is still stuck in {where}: goes back to where {UltimusText.Zone(member.Zone)} starts and finds the way from there.",
                UltimusLink.ABNORMAL);
            _actors[index].Tell(new UltimusMember.Recall());
            _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;

            return;
        }

        if (tries >= 2) {
            _clicks.Clear();

            // Another way first, as a player who can't get through tries the teleporter (UltimusDirector.Routes.cs).
            if (TryAnotherWay(index)) {
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                OverlayQuestPath.Forget(member.CharId);

                return;
            }
        }

        var (near, far) = tries >= 2 ? (600f, 1600f) : (150f, 600f);
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far)
                   ?? (member.X + Random.Shared.Next(-200, 200), member.Y + Random.Shared.Next(-200, 200));

        Note("Ultimus", $"{member.Name} has stood still over {seconds} seconds in {where} while {_doing}: steps aside and finds the way again.",
            UltimusLink.ABNORMAL);
        var aside = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.X, spot.Y, body: true);
        _actors[index].Tell(new UltimusMember.WalkTo(aside, "steps aside to get unstuck"));
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(member.CharId);
    }

}
