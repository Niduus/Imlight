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
using System.Text;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The ladder: Ultimus, the TCO7 and the Kitharas Family learning from each other's quests (Niduus, 2026-11-20: "I want the
/// Family to know Ultimus' success and failures, then compare them to TCO7's for the most optimal successes and failures for
/// them to have ... this doesn't mean that TCO7 can't learn from the Family or Ultimus can't learn from TCO7 and/or the
/// Family either. I want them to all work together to solidify the Hive Mind").
///
/// Every few minutes it reads every questing seat's quest times, issues and lessons and keeps, for each quest: the fastest
/// finish on the whole ladder and who did it, the others' times, the lessons of the ones who finished it, and what went
/// wrong for anyone there (so nobody repeats it). Any seat, whatever its rung, gets these on its quest: the best time on
/// the ladder is the one to beat (not only Ultimus's), and the successes and failures go into its stuck thinking and its
/// heads-up when it picks the quest. Knowledge only: no setting or hard limit changes. It also writes ladder-quests.txt in
/// Ultimus's folder, for the Hive mind and for Niduus.
/// </summary>
internal static class UltimusLadder {

    internal const string FILE = "ladder-quests.txt";

    private static readonly TimeSpan s_every = TimeSpan.FromMinutes(5);
    private static readonly object s_lock = new();
    private static DateTime s_read = DateTime.MinValue;
    private static Dictionary<string, Quest> s_quests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the ladder knows of one quest.</summary>
    internal sealed class Quest {
        internal string Title = "";
        internal readonly List<(string Who, double Seconds)> Times = [];
        internal readonly List<(string Who, string Lesson)> Lessons = [];
        internal readonly List<(string Who, string Problem, DateTime At)> Failures = [];
    }

    private sealed class TimeRow {
        public string Title { get; set; } = "";
        public double Seconds { get; set; }
        public int Times { get; set; }
    }

    private sealed class IssueRow {
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
    }

    // The seats that quest: Ultimus, the TCO7 and the Kitharas Family (True Form helps players and does not quest).
    private static IEnumerable<UltimusSeat> Questers => UltimusSeat.All.Where(s => s.IsUltimus || s.IsTeamBot);

    /// <summary>
    /// The fastest finish of this quest on the whole ladder by anyone but the seat working now: (seconds, who), or (0, "").
    /// </summary>
    internal static (double Seconds, string Who) Best(string quest) {
        var me = UltimusSeat.Current.Name;
        var found = Of(quest);
        if (found is null) {
            return (0, "");
        }

        var best = found.Times.Where(t => !t.Who.Equals(me, StringComparison.OrdinalIgnoreCase) && t.Seconds > 0).OrderBy(t => t.Seconds).FirstOrDefault();

        return best.Who is null ? (0, "") : (best.Seconds, best.Who);
    }

    /// <summary>
    /// What the rest of the ladder knows of this quest, for a seat's stuck thinking and heads-up: the fastest finish and who
    /// did it, what the ones who finished it learned there, and what went wrong for anyone there. Empty when nothing is known.
    /// </summary>
    internal static List<string> FactsFor(string quest) {
        var found = Of(quest);
        if (found is null) {
            return [];
        }

        var me = UltimusSeat.Current.Name;
        var facts = new List<string>();
        var times = found.Times.Where(t => !t.Who.Equals(me, StringComparison.OrdinalIgnoreCase) && t.Seconds > 0).OrderBy(t => t.Seconds).ToList();
        if (times.Count > 0) {
            facts.Add($"Ladder: {times.Count} other bot(s) finished this quest; fastest {times[0].Who} in {Minutes(times[0].Seconds)}" +
                      (times.Count > 1 ? $", then {string.Join(", ", times.Skip(1).Take(3).Select(t => $"{t.Who} {Minutes(t.Seconds)}"))}." : "."));
        }

        // Successes first: the lessons of the fastest ones.
        var order = times.Select(t => t.Who).ToList();
        foreach (var (who, lesson) in found.Lessons.Where(l => !l.Who.Equals(me, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(l => order.IndexOf(l.Who) is var i and >= 0 ? i : 99).DistinctBy(l => l.Lesson).Take(3)) {
            facts.Add($"Ladder success ({who}): {UltimusText.Short(lesson, 200)}");
        }

        // Failures to avoid: each kind of problem once, newest first, with who had it.
        foreach (var group in found.Failures.Where(f => !f.Who.Equals(me, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => Shape(f.Problem)).OrderByDescending(g => g.Max(f => f.At)).Take(3)) {
            var newest = group.OrderBy(f => f.At).Last().Problem;
            facts.Add($"Ladder failure to avoid ({string.Join(", ", group.Select(f => f.Who).Distinct().Take(4))}): {UltimusText.Short(newest, 200)}");
        }

        return facts;
    }

    private static Quest Of(string quest) {
        if (string.IsNullOrEmpty(quest)) {
            return null;
        }

        lock (s_lock) {
            if (DateTime.UtcNow - s_read > s_every) {
                s_read = DateTime.UtcNow;
                try {
                    s_quests = Load();
                    Write(Path.Combine(UltimusWire.SharedFolder, FILE), Report(s_quests));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
                    Logger.Debug("The ladder could not read the bots' records: {0}", Logger.Args(e.Message));
                }
            }

            return s_quests.GetValueOrDefault(quest);
        }
    }

    private static Dictionary<string, Quest> Load() {
        var quests = new Dictionary<string, Quest>(StringComparer.OrdinalIgnoreCase);
        Quest For(string code) => quests.TryGetValue(code, out var q) ? q : quests[code] = new Quest();

        foreach (var seat in Questers) {
            var folder = seat.IsUltimus ? UltimusWire.SharedFolder : seat.Folder;
            if (!Directory.Exists(folder)) {
                continue;
            }

            foreach (var (code, row) in Read<Dictionary<string, TimeRow>>(Path.Combine(folder, UltimusWire.QUEST_TIMES_FILE)) ?? []) {
                if (row.Times > 0 && row.Seconds > 0) {
                    var q = For(code);
                    q.Title = q.Title.Length > 0 ? q.Title : row.Title ?? "";
                    q.Times.Add((seat.Name, row.Seconds));
                }
            }

            foreach (var issue in (Read<List<IssueRow>>(Path.Combine(folder, UltimusWire.ISSUES_FILE)) ?? []).TakeLast(400)) {
                if (issue.Quest.Length > 0 && issue.Kind is "quest" or "team" or "slow" or "stuck" && issue.Text.Length > 0) {
                    var q = For(issue.Quest);
                    q.Title = q.Title.Length > 0 ? q.Title : issue.Title ?? "";
                    q.Failures.Add((seat.Name, seat.Own(Problem(issue.Text)), issue.At));
                }
            }

            foreach (var (quest, lesson) in ReadLessons(Path.Combine(folder, UltimusWire.LEARNED_FILE))) {
                For(quest).Lessons.Add((seat.Name, lesson));
            }
        }

        return quests;
    }

    // The part of an issue that says what went wrong (its first sentence; the diagnosis after it is the same for everyone).
    private static string Problem(string text) {
        var cut = text.IndexOf(" Diagnosis", StringComparison.Ordinal);
        text = cut > 0 ? text[..cut] : text;

        return UltimusText.Short(text.Trim(), 240);
    }

    // The same problem with other numbers (minutes, spots, times flagged) and another bot's name counts as one.
    private static string Shape(string problem) {
        var shape = System.Text.RegularExpressions.Regex.Replace(problem, @"-?\d+(\.\d+)?", "#");
        foreach (var seat in Questers) {
            shape = shape.Replace(seat.Name, "*", StringComparison.Ordinal);
        }

        return shape;
    }

    private static string Report(Dictionary<string, Quest> quests) {
        var text = new StringBuilder();
        text.AppendLine($"THE LADDER: QUESTS  (written {DateTime.Now:yyyy-MM-dd HH:mm})");
        text.AppendLine("Ultimus, the TCO7 and the Kitharas Family, each quest they finished or had trouble on: the fastest finish, every");
        text.AppendLine("bot's time, what the ones who finished it learned, and what went wrong (to avoid). Every bot reads this on its quest.");
        text.AppendLine();
        foreach (var (code, q) in quests.Where(kv => kv.Value.Times.Count > 0 || kv.Value.Failures.Count > 0)
                     .OrderByDescending(kv => kv.Value.Times.Count + kv.Value.Failures.Select(f => f.Who).Distinct().Count()).Take(400)) {
            var times = q.Times.OrderBy(t => t.Seconds).ToList();
            text.AppendLine($"{(q.Title.Length > 0 ? q.Title : code)}  [{code}]");
            text.AppendLine(times.Count > 0
                ? $"  Finished by {times.Count}: {string.Join(", ", times.Take(8).Select(t => $"{t.Who} {Minutes(t.Seconds)}"))}{(times.Count > 8 ? ", ..." : "")}"
                : "  Nobody finished it yet.");
            foreach (var (who, lesson) in q.Lessons.DistinctBy(l => l.Lesson).TakeLast(3)) {
                text.AppendLine($"  Success ({who}): {UltimusText.Short(lesson, 200)}");
            }

            foreach (var group in q.Failures.GroupBy(f => Shape(f.Problem)).OrderByDescending(g => g.Count()).Take(3)) {
                text.AppendLine($"  Avoid ({group.Count()}x, {string.Join(", ", group.Select(f => f.Who).Distinct().Take(5))}): {group.OrderBy(f => f.At).Last().Problem}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

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

    private static List<string> ReadLines(string path) {
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

    private static void Write(string path, string text) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("The ladder could not write {0}: {1}", Logger.Args(Path.GetFileName(path), e.Message));
        }
    }

}
