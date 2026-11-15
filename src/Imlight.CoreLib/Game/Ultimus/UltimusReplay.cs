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
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The replay pack (approved 2026-11-14): every bug Niduus showed in a screenshot, as a check of the decision behind it,
/// run by Ultimus's self-test after each update. A failing check is written up pink and listed in replay-pack.txt, so a
/// fixed bug that comes back is seen at once. Add a line here for each new screenshot bug.
/// </summary>
internal static class UltimusReplay {

    private sealed record Case(string Bug, Func<string> Check);

    private static readonly Case[] s_cases = [
        new("Waited to heal at 875 of 1,099 health (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(onlyPrep: true, potion: false, wispHere: false, shopHops: 3, wispHops: 2) == "go-on" ? null : "he would still stop to heal"),
        new("Stood waiting to heal with wisps a few areas away (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: -1, wispHops: 2) == "wisp-area" ? null : "he would not go for the wisps"),
        new("Potions closer than wisps: he buys bottles (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: 1, wispHops: 3) == "shop" ? null : "he would not go to the closer shop"),
        new("Wisps and shop the same distance: free wisps first (2026-11-14)", () =>
            UltimusDirectorActor.HealChoice(false, false, false, shopHops: 2, wispHops: 2) == "wisp-area" ? null : "he would pay instead of free wisps"),
        new("Waited for wisps at 63% health (2026-11-14 \"oh boy\")", () =>
            !UltimusDirectorActor.HuntsAt(0.63) && UltimusDirectorActor.HuntsAt(0.2) ? null : "he would still hunt or wait above 25%"),
        new("Farmed Desert Golems for XP instead of questing (2026-11-14)", () =>
            UltimusPlaybook.Value("Push.SeekFights") < 0.5 && UltimusMission.HardLimit("Push.SeekFights") ? null : "seeking fights for XP is on or tunable"),
        new("Menu Chat \"Hi\" answered \"Hello?\" (2026-11-14)", () =>
            Enumerable.Range(0, 20).Select(i => MenuChatBook.Exact(new Random(i), "Hello", "Hi", "Hey", "Greetings")).Any(p => p?.Text.TrimEnd().EndsWith('?') == true)
                ? "a puzzled \"Hello?\" can still be picked" : null),
        new("\"Follow me\" answered \"Sorry, I am hatching with someone else\" (2026-11-14)", () =>
            Enumerable.Range(0, 20).Select(i => MenuChatBook.Exact(new Random(i), "No thanks", "No, thank you", "Sorry", "Not now", "No"))
                .Any(p => p is not null && (MenuChatBook.Says(p.Text, "hatching") || MenuChatBook.Says(p.Text, "busy"))) ? "an untrue Menu Chat line can still be picked" : null),
        new("Ironworks <-> Nail's Hideout 221 times in an hour: two rules took turns (2026-11-15)", () => {
            var t = DateTime.UtcNow.AddMinutes(-2);
            UltimusDirectorActor.GoalPick P(int s, string id, string src) => new(t.AddSeconds(s * 11), id, src, "", "", "", id);
            var loop = new[] { P(0, "q|Gate Crashers|g1", "dungeon-first"), P(1, "q|Internal Investigation|g2", "story"), P(2, "q|Gate Crashers|g1", "dungeon-first"),
                               P(3, "q|Internal Investigation|g2", "story"), P(4, "q|Gate Crashers|g1", "dungeon-first") };
            if (UltimusDirectorActor.FlipFlopIn(loop, DateTime.UtcNow, DateTime.MinValue, TimeSpan.FromMinutes(10), 4) is not var (a, b, _)) {
                return "the flip-flop would not be seen";
            }

            return UltimusDirectorActor.Winner(a, b).Win.Source == "dungeon-first" ? null : "he would hold to the story step, not the dungeon's quest that opens the way";
        }),
        new("Real progress between turns is not a flip-flop (2026-11-15)", () => {
            var t = DateTime.UtcNow.AddMinutes(-2);
            UltimusDirectorActor.GoalPick P(int s, string id) => new(t.AddSeconds(s * 11), id, "story", "", "", "", id);
            var picks = new[] { P(0, "a"), P(1, "b"), P(2, "a"), P(3, "b"), P(4, "a") };
            return UltimusDirectorActor.FlipFlopIn(picks, DateTime.UtcNow, t.AddSeconds(25), TimeSpan.FromMinutes(10), 4) is null
                ? null : "turns before his last step done would still count";
        }),
        new("Three different goals in a row are not a flip-flop (2026-11-15)", () => {
            var t = DateTime.UtcNow.AddMinutes(-2);
            UltimusDirectorActor.GoalPick P(int s, string id) => new(t.AddSeconds(s * 11), id, "story", "", "", "", id);
            var picks = new[] { P(0, "a"), P(1, "b"), P(2, "c"), P(3, "a"), P(4, "b") };
            return UltimusDirectorActor.FlipFlopIn(picks, DateTime.UtcNow, DateTime.MinValue, TimeSpan.FromMinutes(10), 4) is null
                ? null : "an ordinary run of steps would be taken for a flip-flop";
        }),
        new("A side trip never wins over a quest step in a flip-flop (2026-11-15)", () => {
            var t = DateTime.UtcNow;
            var side = new UltimusDirectorActor.GoalPick(t, "k|shop", "shopping", "", "", "", "shop");
            var step = new UltimusDirectorActor.GoalPick(t.AddSeconds(5), "q|Q|g", "story", "", "Q", "g", "step");
            return UltimusDirectorActor.Winner(side, step).Win.Source == "story" ? null : "he would hold to the shopping trip";
        }),
        new("A door leg and its step are one goal (2026-11-15)", () =>
            UltimusDirectorActor.GoalId(new UltimusPlanner.Target(UltimusPlanner.DOOR, 0, 0, "", Key: "Q|g|door|MB/x"))
            == UltimusDirectorActor.GoalId(new UltimusPlanner.Target(UltimusPlanner.EXIT, 0, 0, "", Key: "Q|g|exit|MB/y")) ? null : "each door on the way would look like a new goal"),
        new("The same line twice a second for minutes (2026-11-15, potion trip)", () => {
            var line = "replay check " + Guid.NewGuid();
            var said = Enumerable.Range(0, 9).Select(_ => UltimusLink.Repeats("check", line)).ToList();
            return said.Take(6).All(s => s.Count == 1 && s[0] == line) && said[6].Count == 1 && said[6][0].Contains("REPEAT", StringComparison.Ordinal)
                   && said.Skip(7).All(s => s.Count == 0) ? null : "a repeated line would still flood the log";
        }),
        new("Hand-set battle rules needed tuning each update (2026-11-15): he plays the style his own results say wins", () => {
            UltimusFightStyles.Score S(int fights, int wins, int rounds, double lost) => new() { Fights = fights, Wins = wins, Rounds = rounds, HealthLost = lost };
            var hitWins = new Dictionary<string, UltimusFightStyles.Score> { [UltimusFightStyles.BUILD_UP] = S(10, 6, 50, 4), [UltimusFightStyles.HIT_NOW] = S(8, 8, 24, 1.6) };
            var tooFew = new Dictionary<string, UltimusFightStyles.Score> { [UltimusFightStyles.BUILD_UP] = S(10, 6, 50, 4), [UltimusFightStyles.HIT_NOW] = S(3, 3, 6, 0.3) };
            var even = new Dictionary<string, UltimusFightStyles.Score> { [UltimusFightStyles.BUILD_UP] = S(10, 10, 40, 2), [UltimusFightStyles.HIT_NOW] = S(10, 10, 40, 2) };
            if (UltimusFightStyles.Best(hitWins) != UltimusFightStyles.HIT_NOW) {
                return "a style that clearly wins more is not picked";
            }

            if (UltimusFightStyles.Best(tooFew) is not null || UltimusFightStyles.Best(even) is not null) {
                return "a style is picked from too few fights or with no real difference";
            }

            return UltimusFightStyles.KindOf(1, 300, 1000) == "1 enemy, weaker" && UltimusFightStyles.KindOf(4, 5000, 1000) == "3+ enemies, stronger"
                ? null : "fight kinds are sorted wrong";
        }),
        new("Self-test said 8 of 8 passed while he looped for an hour (2026-11-15): lost time has a cause", () => {
            UltimusDirectorActor.CauseFacts F(bool hold = false, bool unstuck = false, double still = 0, double noProgress = 0, bool battle = false) =>
                new(battle, false, false, unstuck, hold, false, false, false, false, still, noProgress);
            var walkingInCircles = UltimusDirectorActor.CauseOf(F(noProgress: 45));
            var holding = UltimusDirectorActor.CauseOf(F(hold: true, noProgress: 45));
            var questing = UltimusDirectorActor.CauseOf(F(noProgress: 2));
            var fight = UltimusDirectorActor.CauseOf(F(battle: true, unstuck: true));
            if (!UltimusDirectorActor.IsLost(walkingInCircles) || !UltimusDirectorActor.IsLost(holding) || UltimusDirectorActor.IsLost(questing) || UltimusDirectorActor.IsLost(fight)) {
                return "an hour of looping would still count as questing";
            }

            var day = new UltimusDirectorActor.CauseDay { Seconds = { [walkingInCircles] = 3600, [questing] = 600 } };
            var report = UltimusDirectorActor.CauseReport(new() { ["2026-11-15"] = day }, DateTime.Now);
            return report.Contains("lost 1.0 h", StringComparison.Ordinal) && report.Contains(walkingInCircles, StringComparison.Ordinal) ? null : "the report does not name the lost hour";
        }),
    ];

    /// <summary>The failed checks (plain words), chat answers included; written to replay-pack.txt.</summary>
    internal static List<string> Run() {
        var failed = new List<string>();
        foreach (var c in s_cases) {
            try {
                if (c.Check() is { } why) {
                    failed.Add($"{c.Bug}: {why}");
                }
            }
            catch (Exception e) {
                failed.Add($"{c.Bug}: the check broke ({e.Message})");
            }
        }

        failed.AddRange(BotSocialChecks.Run().Select(f => "Chat: " + f));
        failed.AddRange(BotAiGuardChecks.Run());
        try {
            var total = Count;
            File.WriteAllLines(Path.Combine(UltimusWire.Folder, "replay-pack.txt"),
                [$"Replay pack {DateTime.Now:yyyy-MM-dd HH:mm}: {total - failed.Count} of {total} screenshot bugs stay fixed.", .. failed.Select(f => "FAILED " + f)]);
        }
        catch (IOException) {
            // The self-test still says it.
        }

        return failed;
    }

    internal static int Count => s_cases.Length + BotSocialChecks.All.Length + BotAiGuardChecks.Count;

}
