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
using System.IO;
using System.Linq;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// One goal at a time (Niduus, 2026-11-15, "all" of the robustness pitches). His next move comes from a ladder of rules
/// (party, leave off-story, back out, wisps, training, shopping, selling, the Bazaar, treasure cards, quest-word hints,
/// the dungeon's own quest, a gate quest, levelling, while-here, top-up, the story step) asked again every second. Two of
/// those rules can give opposite answers in two places: in his log of 2026-11-15 he went The Ironworks -> Nail's Hideout ->
/// The Ironworks 221 times in one hour, because "the dungeon's quest first" won in one room and "the story step" in the
/// other. A fix for one pair of rooms leaves the next pair broken, so this is the general rule:
/// <list type="bullet">
/// <item>Every pick is written down with the rule that made it (its "source").</item>
/// <item>The same two goals taking turns (A, B, A, B) with no real progress in between is a flip-flop. He holds to one of
/// them (the one that opens the way for the other first, else the quest step over a side trip, else the one he had first)
/// for Guard.FlipFlopMinutes, and every pick of the other goal is turned into the held one.</item>
/// <item>The held goal done (its step finished or no longer his), or real progress made: the hold ends at once.</item>
/// <item>The time runs out with no real progress: round 2 holds the other goal instead. Round 2 failing too: a NEEDS HELP
/// issue naming the two rules that fought, and the hold ends.</item>
/// <item>Each flip-flop is saved in detail to logs/ultimus/flip-flops.jsonl (and the shared mistakes book), so the rule
/// pair behind it can be fixed for every world, and the Quest Team and later bots learn from the same data.</item>
/// </list>
/// Real progress is a step, a quest, a level, XP or a won quest fight; arriving in an area again does not count
/// (it did before, so his progress check said "yes" all through that loop).
/// Off switch: playbook Rules.FlipFlop = 0 (1 = watch only).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const string FLIP_FLOPS = "flip-flops.jsonl";

    // The rule that made the pick this second (set by PickLadder).
    private string _pickSource = "";

    // Each change of goal, newest last (20 minutes kept).
    private readonly List<GoalPick> _goalPicks = [];

    internal sealed record GoalPick(DateTime At, string Id, string Source, string Zone, string Quest, string Goal, string What);

    // The goal he holds to after a flip-flop.
    private GoalHold _hold;

    private sealed record GoalHold(GoalPick Win, GoalPick Lose, DateTime Since, DateTime Until, int Round, int Switches, DateTime ProgressAtStart) {
        internal int Turned { get; set; }
    }

    // Real progress only (a step, a quest, a level, XP or a won quest fight): never a room reached again.
    private DateTime _realProgressAt = DateTime.UtcNow;

    // Area arrivals by quest, for "a new area reached counts as progress, the same one again does not".
    private readonly Dictionary<string, DateTime> _arrivals = new(StringComparer.OrdinalIgnoreCase);

    private UltimusPlanner.Target Source(string source, UltimusPlanner.Target target) {
        if (target is not null) {
            _pickSource = source;
        }

        return target;
    }

    /// <summary>The goal a target serves: its quest and step, else its key without the door or exit of the way there.</summary>
    internal static string GoalId(UltimusPlanner.Target target) {
        if (target is null) {
            return "";
        }

        if (!string.IsNullOrEmpty(target.Quest)) {
            return "q|" + target.Quest + "|" + target.Goal;
        }

        var key = target.Key ?? "";
        foreach (var leg in new[] { "|door|", "|exit|", "|spiral", "|obelisk" }) {
            var at = key.IndexOf(leg, StringComparison.Ordinal);
            if (at >= 0) {
                key = key[..at];
            }
        }

        return "k|" + (key.Length > 0 ? key : target.Kind + "|" + target.What);
    }

    private static readonly HashSet<string> s_neverHeld = new(StringComparer.Ordinal) { "party", "top-up", "wisp-after-fight" };

    // Rules whose goal opens the way for another goal: they win a flip-flop.
    private static readonly HashSet<string> s_opensTheWay = new(StringComparer.Ordinal) { "dungeon-first", "gate-first", "leave-off-story", "back-out" };

    // Side trips: a quest step wins over them.
    private static readonly HashSet<string> s_sideTrips = new(StringComparer.Ordinal) {
        "training", "shopping", "bazaar-selling", "selling", "bazaar", "treasure-cards", "wisp-after-fight", "top-up", "while-here", "level-first",
    };

    /// <summary>The ladder's pick, held steady: a flip-flop between two goals is broken by holding to one of them.</summary>
    private UltimusPlanner.Target Steady(List<UltimusMember.Status> group, UltimusPlanner.Target picked) {
        var mode = UltimusRules.Mode(UltimusRules.FLIP_FLOP);
        if (picked is null || mode == UltimusRules.OFF) {
            _hold = null;
            return picked;
        }

        var now = DateTime.UtcNow;
        var leader = group[0];
        var id = GoalId(picked);
        var source = _pickSource.Length > 0 ? _pickSource : "story";
        // Going along with a real player's group is theirs to lead, and a wisp or top-up on the way is a few seconds:
        // neither is held against nor counted as a turn.
        if (s_neverHeld.Contains(source)) {
            return picked;
        }

        if (_goalPicks.Count == 0 || _goalPicks[^1].Id != id) {
            _goalPicks.Add(new GoalPick(now, id, source, leader.Zone ?? "", picked.Quest ?? "", picked.Goal ?? "", picked.What ?? ""));
        }

        _goalPicks.RemoveAll(p => now - p.At > TimeSpan.FromMinutes(20));

        if (_hold is { } hold) {
            var held = HeldTarget(group, hold, picked, id, now);
            if (held is not null) {
                return held;
            }
        }

        if (_hold is null && FlipFlop(now) is var (a, b, switches)) {
            if (mode == UltimusRules.WATCH) {
                UltimusRules.WouldHave(UltimusRules.FLIP_FLOP, $"held to \"{Winner(a, b).Win.What}\": my rules {a.Source} and {b.Source} took turns {switches} times " +
                    $"(\"{a.What}\" and \"{b.What}\") with no real progress.", a.Id + "|" + b.Id);
                return picked;
            }

            StartHold(leader, a, b, switches, round: 1);
            if (_hold is { } fresh && fresh.Win.Id != id) {
                return Turn(group, fresh, picked) ?? picked;
            }
        }

        return picked;
    }

    // Two goals taking turns in the last few minutes with no real progress since the first of those turns.
    private (GoalPick A, GoalPick B, int Switches)? FlipFlop(DateTime now, int need = 0) {
        var window = TimeSpan.FromMinutes(Math.Max(2, UltimusPlaybook.Value("Guard.FlipFlopWindowMinutes")));
        need = need > 0 ? need : Math.Max(3, (int) UltimusPlaybook.Value("Guard.FlipFlopSwitches"));

        return FlipFlopIn(_goalPicks, now, _realProgressAt, window, need);
    }

    /// <summary>
    /// Two goals taking turns (each pick the other one), at least <paramref name="need"/> turns within
    /// <paramref name="window"/>, all after the last real progress. Null when the picks show none.
    /// </summary>
    internal static (GoalPick A, GoalPick B, int Switches)? FlipFlopIn(IReadOnlyList<GoalPick> picks, DateTime now, DateTime realProgressAt,
                                                                         TimeSpan window, int need) {
        var recent = picks.Where(p => now - p.At <= window && p.At >= realProgressAt).ToList();
        if (recent.Count < need + 1) {
            return null;
        }

        var tail = recent.TakeLast(need + 1).ToList();
        var ids = tail.Select(p => p.Id).Distinct().ToList();
        if (ids.Count != 2) {
            return null;
        }

        for (var i = 1; i < tail.Count; i++) {
            if (tail[i].Id == tail[i - 1].Id) {
                return null;
            }
        }

        return (tail.First(p => p.Id == ids[0]), tail.First(p => p.Id == ids[1]), tail.Count - 1);
    }

    private void StartHold(UltimusMember.Status leader, GoalPick a, GoalPick b, int switches, int round) {
        var (win, lose) = Winner(a, b);
        if (round == 2) {
            (win, lose) = (lose, win);
        }

        var minutes = Math.Max(2, UltimusPlaybook.Value("Guard.FlipFlopMinutes"));
        var now = DateTime.UtcNow;
        _hold = new GoalHold(win, lose, now, now.AddMinutes(minutes), round, switches, _realProgressAt);
        var why = round == 2
            ? $"holding to \"{lose.What}\" for {minutes:0} minutes did not move me on, so now I hold to the other one"
            : s_opensTheWay.Contains(win.Source) ? "it opens the way for the other one"
            : s_sideTrips.Contains(lose.Source) && !s_sideTrips.Contains(win.Source) ? "a quest step comes before a side trip"
            : "it is the one I had first";
        Note("Ultimus", $"FLIP-FLOP: my rules \"{a.Source}\" and \"{b.Source}\" took turns {switches} times in a few minutes " +
                        $"(\"{a.What}\" and \"{b.What}\") with no real progress. Holds to \"{win.What}\" (round {round}, {minutes:0} minutes).", UltimusLink.ABNORMAL);
        Think($"I keep turning between \"{a.What}\" ({a.Source}) and \"{b.What}\" ({b.Source}): that is a flip-flop, not progress. " +
              $"I hold to \"{win.What}\" because {why}, and do it to the end before the other one.", "flipflop|" + win.Id + "|" + round);
        Count("flip-flops broken");
        BotShared.BotMistakes.Record("Ultimus", BotShared.BotMistakes.FLIP_FLOP, leader.Zone ?? "", leader.X, leader.Y,
            $"{win.Quest}|{win.Goal}|{win.What}", $"Rules {a.Source} and {b.Source} took turns {switches} times with no progress.", _lastReasoning,
            $"Held to {win.What} ({win.Source}) for {minutes:0} minutes, round {round}.", "hold one goal");
        SaveFlipFlop("start", leader, _hold, why);
    }

    // The winner of a flip-flop and the other one.
    internal static (GoalPick Win, GoalPick Lose) Winner(GoalPick a, GoalPick b) {
        var aOpens = s_opensTheWay.Contains(a.Source);
        var bOpens = s_opensTheWay.Contains(b.Source);
        if (aOpens != bOpens) {
            return aOpens ? (a, b) : (b, a);
        }

        var aSide = s_sideTrips.Contains(a.Source);
        var bSide = s_sideTrips.Contains(b.Source);
        if (aSide != bSide) {
            return aSide ? (b, a) : (a, b);
        }

        return a.At <= b.At ? (a, b) : (b, a);
    }

    // While holding: the held goal's move, a pick of the other goal turned into it, anything else let through.
    private UltimusPlanner.Target HeldTarget(List<UltimusMember.Status> group, GoalHold hold, UltimusPlanner.Target picked, string id, DateTime now) {
        var leader = group[0];
        // Real progress made: the hold did its job.
        if (_realProgressAt > hold.ProgressAtStart) {
            EndHold(leader, hold, "real progress was made");
            return null;
        }

        // The held goal is finished, or no longer his.
        if (hold.Win.Quest.Length > 0 && !group.Any(m => HoldsGoal(m, hold.Win.Quest, hold.Win.Goal))) {
            EndHold(leader, hold, $"\"{hold.Win.What}\" is done");
            return null;
        }

        if (now > hold.Until) {
            if (hold.Round == 1) {
                SaveFlipFlop("round 1 ran out", leader, hold, "no real progress while holding");
                StartHold(leader, hold.Win, hold.Lose, hold.Switches, round: 2);
                return null;
            }

            Issue("team", hold.Win.Quest.Length > 0 ? hold.Win.Quest : _currentQuest,
                $"FLIP-FLOP NOT BROKEN: my rules \"{hold.Win.Source}\" and \"{hold.Lose.Source}\" keep pulling me between \"{hold.Win.What}\" and " +
                $"\"{hold.Lose.What}\" in {UltimusText.Zone(leader.Zone)}. Holding to each for a while did not move me on. Those two rules need a look.",
                key: "flipflop|" + hold.Win.Id + "|" + hold.Lose.Id, broken: false, level: UltimusLink.ABNORMAL);
            EndHold(leader, hold, "both rounds ran out (filed for help)");
            return null;
        }

        if (id == hold.Win.Id) {
            return picked;
        }

        return id == hold.Lose.Id ? Turn(group, hold, picked) : null;
    }

    // A pick of the other goal turned into the held one (planned afresh from here, by its quest).
    private UltimusPlanner.Target Turn(List<UltimusMember.Status> group, GoalHold hold, UltimusPlanner.Target picked) {
        if (hold.Win.Quest.Length == 0 || UltimusPlanner.ForQuest(group, hold.Win.Quest) is not { } held
            || held.Problem.Length > 0 || held.Kind is UltimusPlanner.WAIT or UltimusPlanner.NONE) {
            // Nothing to hold to from here (not a quest step, or no way to it from this room): the other goal goes on.
            return null;
        }

        hold.Turned++;
        Think($"My rule \"{hold.Lose.Source}\" wants \"{picked.What}\" again, but I hold to \"{held.What}\" until it is done " +
              $"(flip-flop round {hold.Round}).", "flipflop-turn|" + hold.Win.Id + "|" + group[0].Zone);

        return held;
    }

    private void EndHold(UltimusMember.Status leader, GoalHold hold, string why) {
        Note("Ultimus", $"FLIP-FLOP over: stops holding to \"{hold.Win.What}\": {why} (after {(DateTime.UtcNow - hold.Since).TotalMinutes:0.#} minutes, " +
                        $"{hold.Turned} pick(s) turned).", UltimusLink.NOTABLE);
        SaveFlipFlop("end", leader, hold, why);
        _hold = null;
        _goalPicks.Clear();
    }

    // Every flip-flop, in full, for fixing the rule pair behind it once for every world.
    private void SaveFlipFlop(string what, UltimusMember.Status leader, GoalHold hold, string why) {
        try {
            var line = UltimusWire.Write(new Dictionary<string, object> {
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["event"] = what,
                ["why"] = why,
                ["round"] = hold.Round,
                ["switches"] = hold.Switches,
                ["turned"] = hold.Turned,
                ["minutes"] = Math.Round((DateTime.UtcNow - hold.Since).TotalMinutes, 1),
                ["zone"] = leader.Zone ?? "",
                ["x"] = Math.Round(leader.X),
                ["y"] = Math.Round(leader.Y),
                ["level"] = leader.Wizard?.MagicSchoolBehavior?.Level ?? 0,
                ["currentQuest"] = _currentQuest ?? "",
                ["win"] = new Dictionary<string, object> { ["source"] = hold.Win.Source, ["quest"] = hold.Win.Quest, ["goal"] = hold.Win.Goal, ["what"] = hold.Win.What, ["zone"] = hold.Win.Zone },
                ["lose"] = new Dictionary<string, object> { ["source"] = hold.Lose.Source, ["quest"] = hold.Lose.Quest, ["goal"] = hold.Lose.Goal, ["what"] = hold.Lose.What, ["zone"] = hold.Lose.Zone },
                ["picks"] = _goalPicks.TakeLast(12).Select(p => $"{p.At:HH:mm:ss} {p.Source} {p.Zone} {p.What}").ToList(),
            });
            File.AppendAllText(Path.Combine(UltimusWire.Folder, FLIP_FLOPS), line.Replace("\r", "").Replace("\n", "") + Environment.NewLine);
        }
        catch (IOException) {
            // The note in the log says it anyway.
        }
        catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>
    /// Arriving in an area counts as progress only the first time on this quest within 15 minutes: going back and forth
    /// between two areas is not progress (it was, so the off-track alarm and the progress check never saw the loop).
    /// </summary>
    private bool NewArrival(string zone) {
        var key = (_currentQuest ?? "") + "|" + (zone ?? "");
        var now = DateTime.UtcNow;
        var fresh = !_arrivals.TryGetValue(key, out var at) || now - at > TimeSpan.FromMinutes(15);
        _arrivals[key] = now;
        if (_arrivals.Count > 300) {
            foreach (var old in _arrivals.Where(a => now - a.Value > TimeSpan.FromMinutes(15)).Select(a => a.Key).ToList()) {
                _arrivals.Remove(old);
            }
        }

        return fresh;
    }

    // Moves the progress check saw lately: a move seen again is not "a new move" (it alternated in the loop).
    private readonly Dictionary<string, DateTime> _checkMovesSeen = new(StringComparer.Ordinal);

    private bool NewMoveForCheck(string key) {
        if (string.IsNullOrEmpty(key)) {
            return false;
        }

        var now = DateTime.UtcNow;
        var fresh = !_checkMovesSeen.TryGetValue(GoalIdOfKey(key), out var at) || now - at > TimeSpan.FromMinutes(15);
        _checkMovesSeen[GoalIdOfKey(key)] = now;

        return fresh;
    }

    private static string GoalIdOfKey(string key) => GoalId(new UltimusPlanner.Target(UltimusPlanner.NONE, 0, 0, "", Key: key));

    /// <summary>
    /// His own play, checked when the quest diagnosis finds nothing wrong with the quest: flip-flops, areas visited again
    /// and again, and how long since real progress. A flip-flop it finds is broken at once. Empty when nothing stands out.
    /// </summary>
    private string OwnPlayCheck(UltimusMember.Status leader) {
        var now = DateTime.UtcNow;
        var said = new List<string>();
        if (_hold is { } hold) {
            said.Add($"I am holding to \"{hold.Win.What}\" after my rules {hold.Win.Source} and {hold.Lose.Source} took turns (round {hold.Round}).");
        }
        else if (UltimusRules.On(UltimusRules.FLIP_FLOP) && FlipFlop(now, need: 3) is var (a, b, switches)) {
            said.Add($"my rules {a.Source} and {b.Source} took turns {switches} times between \"{a.What}\" and \"{b.What}\": a flip-flop, which I break now.");
            StartHold(leader, a, b, switches, round: 1);
        }

        var areas = _zoneTrail.GroupBy(z => z.Zone, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2)
            .Select(g => $"{UltimusText.Zone(g.Key)} x{g.Count()}").ToList();
        if (areas.Count > 0) {
            said.Add($"areas reached again in the last 15 minutes: {string.Join(", ", areas)}.");
        }

        var picks = _goalPicks.Where(p => now - p.At < TimeSpan.FromMinutes(10)).GroupBy(p => p.Source).Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (picks.Count > 1) {
            said.Add($"rules that picked my moves in the last 10 minutes: {string.Join(", ", picks)}.");
        }

        said.Add($"last real progress {(now - _realProgressAt).TotalMinutes:0} minute(s) ago.");

        return string.Join(" ", said);
    }
}
