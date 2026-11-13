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
using Akka.Actor;
using System.Linq;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Never stuck in one place (Niduus, 2026-11-13: "Parameters should be in place to prevent that"): a wizard that should be
/// getting somewhere and has stood still for over a minute steps aside and finds its way again; the next time a teammate
/// goes to the leader and the leader goes further off; after that it is written up pink (ABNORMAL) for Ahziael too.
/// The whole team standing still with nothing to excuse it counts as the leader being stuck.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private const int WHOLE_TEAM = 99;

    /// <summary>How long a wizard that is stepping aside is left to it before orders come again.</summary>
    private static readonly TimeSpan UNSTICK_FOR = TimeSpan.FromSeconds(6);

    private readonly BotStuck _stuck = new();
    private readonly Dictionary<int, DateTime> _unstickUntil = [];

    // Stepping aside: the other orders wait a few seconds.
    private bool Unsticking(int index) => _unstickUntil.TryGetValue(index, out var until) && DateTime.UtcNow < until;

    private void LookForStuck() {
        var resting = _restingSince != DateTime.MaxValue;
        var leader = _team[0];
        var apart = _team.Skip(1).Any(m => m.ZoneActor is null || !m.ZoneActor.Equals(leader.ZoneActor));

        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            bool excused;
            if (i == 0) {
                // The leader waits for the ones catching up, or for the line to close up: they move, not it.
                excused = resting || apart || _doing.StartsWith("waiting", StringComparison.Ordinal);
            }
            else {
                var ahead = _team[i - 1];
                var sameArea = member.ZoneActor is not null && member.ZoneActor.Equals(ahead.ZoneActor);
                // A follower standing behind the one ahead is where it should be.
                excused = resting || (sameArea && Far(member, ahead) < FOLLOW_GAP * 1.6f);
            }

            if (i == 0) {
                RouteMemory.Track("Quest Team", member.CharId, member.Zone, member.X, member.Y);
            }

            if (_stuck.Look(i, member.Zone, member.X, member.Y, excused) is var tries and > 0) {
                Unstick(i, tries);
            }
        }

        // All four still at once, nobody resting: the leader is stuck, whatever it says it waits for.
        var (sx, sy) = (_team.Sum(m => m.X), _team.Sum(m => m.Y));
        if (_stuck.Look(WHOLE_TEAM, leader.Zone, sx, sy, resting) is var teamTries and > 0) {
            Unstick(0, teamTries);
        }
    }

    private void Unstick(int index, int tries) {
        var member = _team[index];
        var where = $"{QuestTeamText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0})";
        var seconds = (int) BotStuck.STILL.TotalSeconds;
        var quest = _target?.Quest ?? _currentQuest;

        // A fresh look at the way: the old walk and the old order are forgotten.
        _sent.Remove(index);
        OverlayQuestPath.Forget(member.CharId);
        if (index == 0) {
            _target = null;
            // Written down for every bot (RouteMemory): the way finder bends round this spot from now on.
            RouteMemory.Stuck(member.CharId, member.Zone, member.X, member.Y);
        }

        if (tries >= 3) {
            Issue("team", quest,
                $"{member.Name} is stuck in {where} while {_doing}: it stood still over {seconds} seconds {tries} times and stepping aside did not free it.",
                key: $"unstick|{member.Zone}|{index}", skipStep: false, level: QuestTeamLink.ABNORMAL);
        }

        // The second time on: a teammate goes straight to the leader, and its click memory is cleared for a fresh try.
        if (tries >= 2 && index > 0 && _team[0].State == "playing") {
            Note(member.Role, $"{member.Name} has stood still over {seconds} seconds again in {where}: goes to {_team[0].Name}.", QuestTeamLink.ABNORMAL);
            _actors[index].Tell(new QuestTeamMember.GoToTeammate(_team[0].CharId));
            _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;

            return;
        }

        if (tries >= 2) {
            _clicks.Clear();
        }

        var (near, far) = tries >= 2 ? (600f, 1600f) : (150f, 600f);
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far);
        if (spot is null) {
            // No walk points here: a few steps back the way it faces is better than nothing.
            spot = (member.X + Random.Shared.Next(-200, 200), member.Y + Random.Shared.Next(-200, 200));
        }

        Note(member.Role, $"{member.Name} has stood still over {seconds} seconds in {where} while {_doing}: steps aside and finds the way again.",
            QuestTeamLink.ABNORMAL);
        var aside = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.Value.X, spot.Value.Y, body: true);
        _actors[index].Tell(new QuestTeamMember.WalkTo(aside, "steps aside to get unstuck"));
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(member.CharId);
    }

}
