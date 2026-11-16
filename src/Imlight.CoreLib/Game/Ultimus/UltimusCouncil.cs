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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The TCO7 council (Niduus, 2026-11-16): "TCO7 is to also get slight updates for the overall Ultimus model if the members of
/// TCO7 all share the same issue. Analyzing them for efficiency in their tasks and noting them", and "it will document tasks
/// that completely get them stuck or when 4/7 (majority) members share the same problems that the minority have surpassed".
///
/// Every 10 minutes it reads the seven members' issues, quest times and lessons and writes, in Ultimus's folder:
/// tco7-council.txt (what got them completely stuck, and each problem a majority shares with what the ones who got past it
/// did), tco7-council.json (the same for the code) and tco7-efficiency.txt (each member's pace, and each quest's times
/// against Ultimus's). The findings go back to every seat, Ultimus first, as facts in their stuck thinking on that quest
/// (<see cref="FactsFor"/>): knowledge only, never a setting or a hard limit changed.
/// </summary>
internal static class UltimusCouncil {

    internal const string FILE = "tco7-council.txt";
    internal const string DATA_FILE = "tco7-council.json";
    internal const string EFFICIENCY_FILE = "tco7-efficiency.txt";

    /// <summary>A majority of the seven (Niduus: "4/7").</summary>
    internal const int MAJORITY = 4;

    private static readonly TimeSpan s_every = TimeSpan.FromMinutes(10);
    private static readonly object s_lock = new();
    private static DateTime s_next = DateTime.MinValue;
    private static Dictionary<string, List<Finding>> s_byQuest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One documented problem: a member completely stuck, or a problem the majority shares.</summary>
    internal sealed class Finding {
        public string Kind { get; set; } = "";            // "stuck" (completely stuck) or "majority"
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string Problem { get; set; } = "";          // the kind of problem, in plain words
        public List<string> Had { get; set; } = [];        // members who had it
        public List<string> Past { get; set; } = [];       // members who got past the quest (with their time)
        public List<string> Lessons { get; set; } = [];    // what those who got past it learned on that quest
        public string Example { get; set; } = "";          // one issue's words
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class IssueRow {
        public int Id { get; set; }
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public string Step { get; set; } = "";
    }

    private sealed class TimeRow {
        public string Title { get; set; } = "";
        public double Seconds { get; set; }
        public int Times { get; set; }
        public DateTime Last { get; set; }
    }

    // The kinds of problem, by the words of the issue (first match wins), and whether each means "completely stuck".
    private static readonly (string Words, string Problem, bool Stuck)[] s_problems = [
        ("needs help", "tried every fix he has and needs help", true),
        ("tried every way to get free", "stuck in a spot he could not get out of", true),
        ("watchdog", "no step done and no XP for a long time", true),
        ("is stuck again", "stuck again at the same spot", true),
        ("still stuck", "stuck in a spot he could not get out of", true),
        ("lost the fight", "kept losing the step's fight", false),
        ("never showed up", "the step's creature never showed up", false),
        ("does not take it anywhere", "a door or exit that did not take him", false),
        ("never takes ultimus on", "a door or exit that did not take him", false),
        ("goes round in a loop", "the way there went round in a loop", false),
        ("went in a loop", "went in a loop with no story progress", false),
        ("dialog never finishes", "a dialog that never finished", false),
        ("cutscene", "a cutscene that did not end", false),
        ("offers nothing to click", "the quest NPC offered nothing", false),
        ("only offers", "the quest NPC offered the wrong thing", false),
        ("did not finish", "the step did not finish after doing it", false),
        ("at the quest arrow's spot there is nothing", "nothing to use at the quest arrow", false),
        ("could not heal", "could not heal (no potions or wisps)", false),
        ("needs level", "too low a level and no side quest to level", false),
        ("time limit", "went past the quest's time limit", false),
        ("much longer than expected", "the quest took much longer than expected", false),
        ("battles won to finish", "far more battles than a player needs", false),
    ];

    /// <summary>Called by every seat's tick: looks again at most every 10 minutes, and only while the TCO7 are on.</summary>
    internal static void Tick() {
        lock (s_lock) {
            if (DateTime.UtcNow < s_next) {
                return;
            }

            s_next = DateTime.UtcNow + s_every;
        }

        if (!ConfigurationManager.GetValue("TCO7.Enabled", false)) {
            return;
        }

        try {
            Look();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("TCO7 council could not read the members' records: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// What the TCO7 found on this quest, for a seat's stuck thinking: the problems a majority shared, who got past them and
    /// how, and what got a member completely stuck here. Empty when there is nothing.
    /// </summary>
    internal static List<string> FactsFor(string quest) {
        if (string.IsNullOrEmpty(quest)) {
            return [];
        }

        List<Finding> found;
        lock (s_lock) {
            if (s_byQuest.Count == 0) {
                s_byQuest = Load();
            }

            found = s_byQuest.TryGetValue(quest, out var list) ? [.. list] : [];
        }

        var me = UltimusSeat.Current.Name;
        var facts = new List<string>();
        foreach (var f in found.OrderByDescending(f => f.Kind == "majority").Take(4)) {
            var others = f.Had.Where(h => !h.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList();
            facts.Add(f.Kind == "majority"
                ? $"TCO7 council: {f.Had.Count} of 7 had this problem on this quest: {f.Problem}." +
                  (f.Past.Count > 0 ? $" Got past it: {string.Join(", ", f.Past)}." : " Nobody has got past it yet.")
                : $"TCO7 council: {string.Join(", ", others.Count > 0 ? others : f.Had)} got completely stuck on this quest: {f.Problem}.");
            facts.AddRange(f.Lessons.Take(3).Select(l => "What worked for them: " + UltimusText.Short(l, 200)));
        }

        return facts;
    }

    // ---------------------------------------------------------------------

    private static void Look() {
        var issues = new Dictionary<string, List<IssueRow>>(StringComparer.OrdinalIgnoreCase);
        var times = new Dictionary<string, Dictionary<string, TimeRow>>(StringComparer.OrdinalIgnoreCase);
        var lessons = new Dictionary<string, List<(string Quest, string Lesson)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in UltimusSeat.Tco7) {
            issues[seat.Name] = Read<List<IssueRow>>(Path.Combine(seat.Folder, UltimusWire.ISSUES_FILE)) ?? [];
            times[seat.Name] = new Dictionary<string, TimeRow>(Read<Dictionary<string, TimeRow>>(Path.Combine(seat.Folder, UltimusWire.QUEST_TIMES_FILE)) ?? [],
                StringComparer.OrdinalIgnoreCase);
            lessons[seat.Name] = ReadLessons(Path.Combine(seat.Folder, UltimusWire.LEARNED_FILE));
        }

        var his = new Dictionary<string, TimeRow>(Read<Dictionary<string, TimeRow>>(Path.Combine(UltimusWire.SharedFolder, UltimusWire.QUEST_TIMES_FILE)) ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (issues.Values.All(l => l.Count == 0) && times.Values.All(t => t.Count == 0)) {
            return;
        }

        var findings = new List<Finding>();
        // Each member's problems, by quest and kind of problem (a problem counted once per member).
        var rows = issues.SelectMany(kv => kv.Value.Where(i => i.Kind is "quest" or "team" or "slow" && i.Quest.Length > 0)
                .Select(i => (Member: kv.Key, Issue: i, Kind: ProblemOf(i.Text))))
            .Where(r => r.Kind.Problem.Length > 0)
            .ToList();
        foreach (var group in rows.GroupBy(r => (r.Issue.Quest.ToLowerInvariant(), r.Kind.Problem))) {
            var quest = group.First().Issue.Quest;
            var had = group.Select(r => r.Member).Distinct().OrderBy(Order).ToList();
            var title = group.Select(r => r.Issue.Title).FirstOrDefault(t => t.Length > 0) ?? quest;
            // The ones who got past it: they finished the quest and never had this problem on it.
            var past = UltimusSeat.Tco7.Select(s => s.Name)
                .Where(n => !had.Contains(n) && times[n].TryGetValue(quest, out var t) && t.Times > 0)
                .Select(n => $"{n} ({Minutes(times[n][quest].Seconds)})").ToList();
            var pastNames = UltimusSeat.Tco7.Select(s => s.Name).Where(n => past.Any(p => p.StartsWith(n + " ", StringComparison.Ordinal))).ToList();
            var learned = pastNames.SelectMany(n => lessons[n].Where(l => l.Quest.Equals(quest, StringComparison.OrdinalIgnoreCase))
                    .Select(l => $"{n}: {l.Lesson}"))
                .Distinct().TakeLast(5).ToList();
            var first = group.Min(r => r.Issue.At);
            var last = group.Max(r => r.Issue.At);
            var example = UltimusText.Short(group.OrderByDescending(r => r.Issue.At).First().Issue.Text, 300);
            if (had.Count >= MAJORITY) {
                findings.Add(new Finding {
                    Kind = "majority", Quest = quest, Title = title, Problem = group.Key.Problem, Had = had, Past = past, Lessons = learned,
                    Example = example, First = first, Last = last,
                });
            }

            if (group.First().Kind.Stuck) {
                findings.Add(new Finding {
                    Kind = "stuck", Quest = quest, Title = title, Problem = group.Key.Problem, Had = had, Past = past, Lessons = learned,
                    Example = example, First = first, Last = last,
                });
            }
        }

        var byQuest = findings.GroupBy(f => f.Quest, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        List<Finding> before;
        lock (s_lock) {
            before = s_byQuest.Count == 0 ? Load().Values.SelectMany(l => l).ToList() : s_byQuest.Values.SelectMany(l => l).ToList();
            s_byQuest = byQuest;
        }

        Directory.CreateDirectory(UltimusWire.SharedFolder);
        Write(Path.Combine(UltimusWire.SharedFolder, DATA_FILE), JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
        Write(Path.Combine(UltimusWire.SharedFolder, FILE), Report(findings));
        Write(Path.Combine(UltimusWire.SharedFolder, EFFICIENCY_FILE), Efficiency(times, his));

        // Each new finding is noted once in the server log, so it is in front of Niduus too.
        foreach (var f in findings.Where(f => !before.Any(b => b.Kind == f.Kind && b.Quest.Equals(f.Quest, StringComparison.OrdinalIgnoreCase) && b.Problem == f.Problem))) {
            Logger.Information("TCO7 council: {0} on {1}: {2} ({3}){4}", Logger.Args(
                f.Kind == "majority" ? $"{f.Had.Count} of 7 share a problem" : "completely stuck", f.Title, f.Problem, string.Join(", ", f.Had),
                f.Past.Count > 0 ? $"; got past it: {string.Join(", ", f.Past)}" : ""));
        }
    }

    private static (string Problem, bool Stuck) ProblemOf(string text) {
        var words = (text ?? "").ToLowerInvariant();
        foreach (var (match, problem, stuck) in s_problems) {
            if (words.Contains(match, StringComparison.Ordinal)) {
                return (problem, stuck);
            }
        }

        return ("", false);
    }

    private static int Order(string name) => UltimusSeat.ByKey(name)?.Index ?? 99;

    private static string Report(List<Finding> findings) {
        var text = new StringBuilder();
        text.AppendLine($"TCO7 COUNCIL  (written {DateTime.Now:yyyy-MM-dd HH:mm})");
        text.AppendLine("What got the TCO7 completely stuck, and each problem 4 or more of the 7 share, with what the ones who got past it did.");
        text.AppendLine("Ultimus and every member read these on that quest when they get stuck.");
        text.AppendLine();
        var majority = findings.Where(f => f.Kind == "majority").OrderByDescending(f => f.Last).ToList();
        text.AppendLine($"SHARED BY THE MAJORITY ({majority.Count})");
        if (majority.Count == 0) {
            text.AppendLine("  None yet.");
        }

        foreach (var f in majority) {
            text.AppendLine($"  {f.Title}: {f.Problem}");
            text.AppendLine($"    Had it ({f.Had.Count} of 7): {string.Join(", ", f.Had)}");
            text.AppendLine($"    Got past it: {(f.Past.Count > 0 ? string.Join(", ", f.Past) : "nobody yet")}");
            foreach (var lesson in f.Lessons) {
                text.AppendLine($"    What worked: {lesson}");
            }

            text.AppendLine($"    Last time: {f.Last.ToLocalTime():yyyy-MM-dd HH:mm}. Example: {f.Example}");
            text.AppendLine();
        }

        var stuck = findings.Where(f => f.Kind == "stuck").OrderByDescending(f => f.Last).ToList();
        text.AppendLine($"COMPLETELY STUCK ({stuck.Count})");
        if (stuck.Count == 0) {
            text.AppendLine("  None yet.");
        }

        foreach (var f in stuck) {
            text.AppendLine($"  {f.Title}: {f.Problem}");
            text.AppendLine($"    Who: {string.Join(", ", f.Had)}. Got past it: {(f.Past.Count > 0 ? string.Join(", ", f.Past) : "nobody yet")}");
            text.AppendLine($"    Last time: {f.Last.ToLocalTime():yyyy-MM-dd HH:mm}. Example: {f.Example}");
            text.AppendLine();
        }

        return text.ToString();
    }

    // Each member's pace, and each quest three or more of them finished, against Ultimus's time.
    private static string Efficiency(Dictionary<string, Dictionary<string, TimeRow>> times, Dictionary<string, TimeRow> his) {
        var text = new StringBuilder();
        text.AppendLine($"TCO7 EFFICIENCY  (written {DateTime.Now:yyyy-MM-dd HH:mm})");
        text.AppendLine();
        text.AppendLine("EACH MEMBER (quests finished, average time a quest, quests faster than Ultimus / slower)");
        foreach (var seat in UltimusSeat.Tco7) {
            var mine = times[seat.Name].Where(t => t.Value.Times > 0 && t.Value.Seconds > 0).ToList();
            var faster = mine.Count(t => his.TryGetValue(t.Key, out var h) && h.Seconds > 0 && t.Value.Seconds < h.Seconds);
            var slower = mine.Count(t => his.TryGetValue(t.Key, out var h) && h.Seconds > 0 && t.Value.Seconds > h.Seconds);
            text.AppendLine(mine.Count == 0
                ? $"  {seat.Name} ({seat.Role}): no quest finished yet"
                : $"  {seat.Name} ({seat.Role}): {mine.Count} quest(s), {Minutes(mine.Average(t => t.Value.Seconds))} each, {faster} faster than Ultimus / {slower} slower");
        }

        var quests = times.SelectMany(kv => kv.Value.Where(t => t.Value.Times > 0 && t.Value.Seconds > 0).Select(t => (Member: kv.Key, Code: t.Key, Time: t.Value)))
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 3)
            .Select(g => (Code: g.Key, Title: g.First().Time.Title, Rows: g.OrderBy(r => r.Time.Seconds).ToList(),
                Average: g.Average(r => r.Time.Seconds), His: his.TryGetValue(g.Key, out var h) ? h.Seconds : 0))
            .ToList();
        text.AppendLine();
        text.AppendLine("WHERE THE TCO7 ARE CLEARLY FASTER THAN ULTIMUS (a quicker way exists)");
        var quicker = quests.Where(q => q.His > 0 && q.Average < q.His * 0.75).OrderBy(q => q.Average / q.His).Take(25).ToList();
        if (quicker.Count == 0) {
            text.AppendLine("  None yet.");
        }

        foreach (var q in quicker) {
            text.AppendLine($"  {Name(q.Title, q.Code)}: TCO7 {Minutes(q.Average)} on average, fastest {q.Rows[0].Member} {Minutes(q.Rows[0].Time.Seconds)}; Ultimus {Minutes(q.His)}");
        }

        text.AppendLine();
        text.AppendLine("WHERE THEY ARE ALL SLOW (each took over twice the fastest one's time, or over twice Ultimus's)");
        var slow = quests.Where(q => q.Rows.Count >= 3 && (q.Rows[1].Time.Seconds > q.Rows[0].Time.Seconds * 2 || (q.His > 0 && q.Average > q.His * 2)))
            .OrderByDescending(q => q.Average).Take(25).ToList();
        if (slow.Count == 0) {
            text.AppendLine("  None yet.");
        }

        foreach (var q in slow) {
            text.AppendLine($"  {Name(q.Title, q.Code)}: {string.Join(", ", q.Rows.Select(r => $"{r.Member} {Minutes(r.Time.Seconds)}"))}" +
                            (q.His > 0 ? $"; Ultimus {Minutes(q.His)}" : ""));
        }

        return text.ToString();
    }

    private static string Name(string title, string code) => title.Length > 0 ? title : code;

    private static string Minutes(double seconds) => seconds < 90 ? $"{Math.Max(1, Math.Round(seconds))}s"
        : seconds < 5400 ? $"{Math.Round(seconds / 60)} min" : $"{seconds / 3600:0.#} h";

    private static List<(string Quest, string Lesson)> ReadLessons(string path) {
        var found = new List<(string, string)>();
        if (!File.Exists(path)) {
            return found;
        }

        foreach (var line in ReadLines(path).TakeLast(400)) {
            try {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var quest = root.TryGetProperty("quest", out var q) ? q.GetString() ?? "" : "";
                var lesson = root.TryGetProperty("lesson", out var l) ? l.GetString() ?? "" : "";
                if (quest.Length > 0 && lesson.Length > 0) {
                    found.Add((quest, lesson));
                }
            }
            catch (JsonException) {
                // A half-written line: skipped.
            }
        }

        return found;
    }

    private static IEnumerable<string> ReadLines(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) {
            if (line.Length > 0) {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static T Read<T>(string path) where T : class {
        if (!File.Exists(path)) {
            return null;
        }

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (JsonException) {
            return null;
        }
    }

    private static Dictionary<string, List<Finding>> Load() {
        var list = Read<List<Finding>>(Path.Combine(UltimusWire.SharedFolder, DATA_FILE)) ?? [];

        return list.GroupBy(f => f.Quest, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static void Write(string path, string text) {
        try {
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("TCO7 council could not write {0}: {1}", Logger.Args(Path.GetFileName(path), e.Message));
        }
    }

}
