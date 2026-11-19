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

    // 45 seconds of standing still, not 75 (Niduus, 2026-11-15: get unstuck faster): Ultimus acts after 30 and a short
    // normal pause (a dialog, a battle, a rest) never counts, so the team can act sooner without fussing.
    private readonly BotStuck _stuck = new(TimeSpan.FromSeconds(45));
    private readonly HashSet<string> _tipsNoted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _solutionsNoted = new(StringComparer.OrdinalIgnoreCase);
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
                // Where the team is headed, so a walk that ends by going out through an exit is remembered too (RouteMemory).
                if (_target is { Problem.Length: 0 } headed) {
                    RouteMemory.Heading(member.CharId, member.Zone, headed.X, headed.Y,
                        headed.Kind is QuestTeamPlanner.EXIT or QuestTeamPlanner.DOOR);
                }

                CheckTeamTry(member);
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

    // The leader's last unstuck move, until it is known whether it got the team going (shared stuck memory, UltimusSolutions).
    private sealed class TeamTry {
        public Ultimus.UltimusSolutions.Solution Solution;
        public BotLessons.Situation Situation;
        public string Zone;
        public float X, Y;
        public DateTime At;
        public DateTime? AwaySince;
    }

    private TeamTry _teamTry;
    // The step the leader is stuck on again, waiting for the server-data check before the AI may think about it.
    private string _askStep;
    private QuestTeamPlanner.Target _leaderLastTargetForStuck;

    // Got going: well away for 45 seconds, or 5 minutes with no new alarm.
    private void CheckTeamTry(QuestTeamMember.Status leader) {
        if (_target is not null) {
            _leaderLastTargetForStuck = _target;
        }

        if (_teamTry is not { } t || string.IsNullOrEmpty(leader.Zone)) {
            return;
        }

        var away = !string.Equals(t.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)
                   || MathF.Sqrt(((t.X - leader.X) * (t.X - leader.X)) + ((t.Y - leader.Y) * (t.Y - leader.Y))) > 800;
        t.AwaySince = away ? t.AwaySince ?? DateTime.UtcNow : null;
        if (t.AwaySince is { } since && DateTime.UtcNow - since >= TimeSpan.FromSeconds(45) || DateTime.UtcNow - t.At >= TimeSpan.FromMinutes(5)) {
            _teamTry = null;
            Ultimus.UltimusSolutions.Result(t.Solution, true);
            // Also for the kind of place it was, in every world (BotLessons, shared with Ultimus).
            BotLessons.Learn(t.Situation, t.Solution.Move, true, QuestTeamText.Zone(t.Zone));
            Note(leader.Role, $"{leader.Name}: \"{Ultimus.UltimusReasonMoves.Words(t.Solution.Move)}\" got the team going; written down for Ultimus and the team.");
        }
    }

    private void Unstick(int index, int tries) {
        var member = _team[index];
        var where = $"{QuestTeamText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0})";
        var seconds = (int) _stuck.Still.TotalSeconds;
        var quest = _target?.Quest ?? _currentQuest;

        // A fresh look at the way: the old walk and the old order are forgotten.
        _sent.Remove(index);
        OverlayQuestPath.Forget(member.CharId);
        if (index == 0) {
            var goalNow = _target ?? _leaderLastTargetForStuck;
            var stepNow = goalNow is not null ? $"{goalNow.Quest}|{goalNow.Goal}|{goalNow.What}" : _doing;
            _target = null;
            // Written down for every bot (RouteMemory): the way finder bends round this spot from now on.
            RouteMemory.Stuck(member.CharId, member.Zone, member.X, member.Y);
            // Ten alarms on one step: a new approach, as Ultimus (QuestTeamDirector.Approach.cs).
            if (TenAlarms(member, stepNow, where)) {
                return;
            }

            // Stuck again on the step: thought through with the local AI in the background, but only once the team's own data
            // has nothing for the spot (server data first, BotDataFirst; asked below).
            _askStep = stepNow;
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

        // What players told Ultimus about this quest while he was stuck on it, shared with the team (Niduus, 2026-11-14):
        // written in the team's log once per quest, so Niduus sees it next to where the team got stuck.
        if (index == 0 && Ultimus.UltimusTips.For(quest) is { Count: > 0 } tips && _tipsNoted.Add(quest)) {
            Note(member.Role, $"{member.Name}: players told Ultimus about {QuestTeamText.Title(quest)}: " +
                              string.Join(" | ", tips.Select(t => $"\"{t}\"")), QuestTeamLink.NOTABLE);
        }

        // What Ultimus learned at this spot (the bots' mistakes book, Niduus, 2026-11-13: "other bots know what mistakes were
        // made and what to do to prevent them"): when stepping a little aside did not free him here, the team goes further.
        var lesson = BotMistakes.Near(BotMistakes.STUCK, member.Zone, member.X, member.Y);
        var goFar = tries >= 2;
        // Whether the bots' own records know a way out here (server data first, BotDataFirst).
        var dataKnowsAFix = lesson is { Worked.Length: > 0 };

        // What Ultimus worked out when he was stuck on this quest, and what got him going (UltimusSolutions, shared like his
        // drop memory and tips; Niduus, 2026-11-14): written in the team's log once per quest, and when going well away is
        // what worked for him, the team goes further at once.
        if (index == 0 && Ultimus.UltimusSolutions.ForTeam(quest, member.Zone, member.X, member.Y) is { } solved) {
            if (_solutionsNoted.Add(quest)) {
                Note(member.Role, $"{member.Name}: Ultimus was stuck on {QuestTeamText.Title(quest)} too. {solved.Line}", QuestTeamLink.NOTABLE);
            }

            goFar |= solved.GoFar;
            dataKnowsAFix = true;
        }
        if (lesson is not null && index == 0) {
            Note(member.Role, $"{member.Name}: {lesson.Bot} was stuck here too ({lesson.Times} time(s)); " +
                              (lesson.Worked.Length > 0 ? $"what freed it: {lesson.Worked}." : "nothing has freed it yet.") +
                              (lesson.Reasoning.Length > 0 ? $" Its reasoning: {lesson.Reasoning}" : ""));
            goFar |= lesson.Worked.Length > 0 && lesson.Worked != "step aside" || lesson.Tried.Contains("step aside") && lesson.Worked.Length == 0;
        }

        // Nothing known for this spot: what freed Ultimus or the team in situations like this one, in any world (BotLessons,
        // Niduus, 2026-11-15: every success future-proof). The team only steps aside or walks well away, so any longer
        // move that worked there (another route, the Commons button) means going well away.
        var situation = BotLessons.Of(member.Wizard?.ZoneIsInstance == true || OverlayQuestRoute.IsDungeon(member.Zone),
            (_target ?? _leaderLastTargetForStuck)?.Kind, "open ground", "stood still");
        if (index == 0 && !goFar && lesson is null
            && BotLessons.Best(situation, [BotLessons.ASIDE, BotLessons.AWAY, BotLessons.OTHER_WAY, BotLessons.COMMONS], null, out var learnedLine) is { } learned) {
            goFar = learned != BotLessons.ASIDE;
            dataKnowsAFix = true;
            Note(member.Role, $"{member.Name}: {learnedLine}");
        }

        // What got a bot going by this spot after its watchdog went off (BotWatchdogBook, Niduus 2026-11-19).
        var watchdogLine = "";
        var watchdogFix = index == 0 ? BotWatchdogBook.Advice(member.Zone, member.X, member.Y,
            [Ultimus.UltimusFixes.ASIDE, Ultimus.UltimusFixes.AWAY, Ultimus.UltimusFixes.OTHER_WAY, Ultimus.UltimusFixes.START], out watchdogLine) : null;
        if (watchdogFix is not null) {
            goFar |= watchdogFix != Ultimus.UltimusFixes.ASIDE;
            Note(member.Role, $"{member.Name}: {watchdogLine}");
        }

        // Server data first (BotDataFirst): the AI thinks only at a roadblock, when nothing the bots wrote down (Ultimus's
        // solutions, the mistakes book, lessons, the watchdog book) has a way out here, or the team keeps getting stuck here.
        if (index == 0 && _askStep is { } askStep) {
            _askStep = null;
            var usedUp = tries >= 3 || BotWatchdogBook.Trap(member.Zone, member.X, member.Y);
            if (BotDataFirst.Roadblock(dataKnowsAFix || watchdogFix is not null, usedUp, _alarms.GetValueOrDefault(AlarmKey(member, askStep)))) {
                AskAboutStuck(member, quest, askStep, where);
            }
            else {
                Note(member.Role, $"{member.Name}: the team's own records still have a way to try here, so it uses those before asking the AI.");
            }
        }

        // The team writes its own results into the shared stuck memory too (Niduus, 2026-11-15: idea 1): stuck again by the
        // spot of its last move, that move failed; the move it makes now is written down and checked (CheckTeamTry).
        if (index == 0) {
            if (_teamTry is { } last && string.Equals(last.Zone, member.Zone ?? "", StringComparison.OrdinalIgnoreCase)
                                     && MathF.Sqrt(((last.X - member.X) * (last.X - member.X)) + ((last.Y - member.Y) * (last.Y - member.Y))) <= 800) {
                Ultimus.UltimusSolutions.Result(last.Solution, false);
                BotLessons.Learn(last.Situation, last.Solution.Move, false, null);
            }

            var goal = _target ?? _leaderLastTargetForStuck;
            var step = goal is not null ? $"{goal.Quest}|{goal.Goal}|{goal.What}" : _doing;
            var move = goFar ? Ultimus.UltimusReasonMoves.AWAY : Ultimus.UltimusReasonMoves.ASIDE;
            _teamTry = new TeamTry {
                Solution = Ultimus.UltimusSolutions.Tried(quest ?? "", step ?? "", member.Zone, member.X, member.Y, "", move,
                    goFar ? "stepping a little aside did not free the team here" : "", "Quest Team"),
                Situation = situation, Zone = member.Zone ?? "", X = member.X, Y = member.Y, At = DateTime.UtcNow,
            };
        }

        var (near, far) = goFar ? (600f, 1600f) : (150f, 600f);
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far);
        if (spot is null) {
            // No walk points here: a few steps back the way it faces is better than nothing.
            spot = (member.X + Random.Shared.Next(-200, 200), member.Y + Random.Shared.Next(-200, 200));
        }

        if (index == 0) {
            SetStuckLine($"{member.Name} stood still over {seconds} s in {where} (height {member.Z:0}) while {_doing}; " +
                         $"{(goFar ? "walks well away and back" : "steps aside")} and finds the way again.");
        }

        Note(member.Role, $"{member.Name} has stood still over {seconds} seconds in {where} while {_doing}: steps aside and finds the way again.",
            QuestTeamLink.ABNORMAL);
        var aside = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.Value.X, spot.Value.Y, body: true);
        _actors[index].Tell(new QuestTeamMember.WalkTo(aside, "steps aside to get unstuck"));
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(member.CharId);
    }

}
