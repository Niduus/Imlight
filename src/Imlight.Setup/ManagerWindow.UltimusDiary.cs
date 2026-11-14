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
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Ultimus's daily diary (Niduus, 2026-11-14, approved): a plain summary of each of his days, read from his memory journal
/// (journal.json in his folder, which the server writes as things happen): quests finished, worlds, levels, tough fights won
/// and lost, players met, gear he put on and where he got stuck, then the day's moments with their times. Nothing in it is
/// made up. Ahziael only reads the file.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int DIARY_DAYS = 14;
    private TextBox? _ultDiaryBox;
    private string _ultDiaryText = "";
    private DateTime _ultDiaryRead = DateTime.MinValue;

    private sealed class DiaryEntry {
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string World { get; set; } = "";
        public string Area { get; set; } = "";
        public string About { get; set; } = "";
        public string Text { get; set; } = "";
        public int Feeling { get; set; }
    }

    private Control UltimusDiaryFold() {
        _ultDiaryBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
            Height = 320, Text = "Turn Ultimus on and start the server; his days are written here as he plays.",
        };

        return Fold("book", ULTIMUS_TAB, "Daily diary",
            "At the top, the places he got stuck most this week. Then Ultimus's day in plain words, newest day first: how many quests he finished, the worlds he started or finished, " +
            "the levels he reached, the tough fights he won and lost, the players he met, the gear he put on and where he got " +
            $"stuck, then each moment with its time. The last {DIARY_DAYS} days. Made from his memory journal, so nothing in it " +
            "is made up. Updated every 30 seconds.",
            LogWell(_ultDiaryBox),
            Row(Action(Plain("Copy diary"), () => CopyAsync(_ultDiaryText))));
    }

    private void ShowUltimusDiary() {
        if (_ultDiaryBox is null || DateTime.UtcNow - _ultDiaryRead < TimeSpan.FromSeconds(30)) {
            return;
        }

        _ultDiaryRead = DateTime.UtcNow;
        var text = UltimusDiaryText(Path.Combine(UltimusFolder, "journal.json"));
        if (text.Length > 0 && text != _ultDiaryText) {
            _ultDiaryText = text;
            _ultDiaryBox.Text = text;
        }
    }

    internal static string UltimusDiaryText(string journalPath) {
        List<DiaryEntry> entries;
        try {
            if (!File.Exists(journalPath)) {
                return "";
            }

            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            entries = JsonSerializer.Deserialize<List<DiaryEntry>>(stream) ?? [];
        }
        catch (Exception) {
            return "";
        }

        var days = entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Text))
            .GroupBy(e => e.At.ToLocalTime().Date).OrderByDescending(g => g.Key).Take(DIARY_DAYS).ToList();
        if (days.Count == 0) {
            return "";
        }

        var stuckPath = Path.Combine(Path.GetDirectoryName(journalPath) ?? "", "stuck-days.json");
        var stuck = StuckDays(stuckPath);
        var text = new StringBuilder();
        // The weekly stuck report (Niduus, 2026-11-14): the 5 places he lost the most time over the last 7 days.
        if (StuckWeek(stuckPath) is { Count: > 0 } week) {
            text.AppendLine("This week, most stuck (the last 7 days):");
            for (var i = 0; i < week.Count; i++) {
                text.AppendLine($"  {i + 1}. {week[i]}");
            }

            text.AppendLine();
        }

        foreach (var day in days) {
            var moments = day.OrderBy(e => e.At).ToList();
            var label = day.Key == DateTime.Today ? "Today" : day.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : "";
            var date = day.Key.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);
            text.AppendLine(label.Length > 0 ? $"{label}, {date}" : date);
            text.AppendLine(DiarySummary(moments));
            // Where he lost the most time that day, with the fix he would suggest (Niduus, 2026-11-14).
            if (stuck.TryGetValue(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var most) && most.Count > 0) {
                text.AppendLine("Most stuck:");
                for (var i = 0; i < most.Count; i++) {
                    text.AppendLine($"  {i + 1}. {most[i]}");
                }
            }

            foreach (var moment in moments) {
                text.AppendLine($"    {moment.At.ToLocalTime():HH:mm}  {moment.Text}");
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    // stuck-days.json's "Week": the last 7 days' worst spots as lines; empty when missing.
    private static List<string> StuckWeek(string path) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                return [];
            }

            foreach (var prop in doc.RootElement.EnumerateObject()) {
                if (prop.Name.Equals("Week", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array) {
                    return [.. prop.Value.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString() ?? "")];
                }
            }

            return [];
        }
        catch (Exception) {
            return [];
        }
    }

    // stuck-days.json (the server's "most stuck" top 3 per day): date -> lines; empty when missing.
    private static Dictionary<string, List<string>> StuckDays(string path) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) {
                foreach (var prop in doc.RootElement.EnumerateObject()) {
                    if (!prop.Name.Equals("Days", StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind != JsonValueKind.Object) {
                        continue;
                    }

                    foreach (var day in prop.Value.EnumerateObject().Where(d => d.Value.ValueKind == JsonValueKind.Array)) {
                        result[day.Name] = [.. day.Value.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString() ?? "")];
                    }
                }
            }

            return result;
        }
        catch (Exception) {
            return [];
        }
    }

    // "A good day. I finished 5 quests, reached level 12, won 2 tough fights and lost 1, met Ann, ..."
    private static string DiarySummary(List<DiaryEntry> day) {
        int Count(string kind) => day.Count(e => e.Kind == kind);
        static string Times(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        var parts = new List<string>();
        if (Count("quest") is > 0 and var quests) {
            parts.Add($"finished {Times(quests, "quest", "quests")}");
        }

        foreach (var world in day.Where(e => e.Kind == "world")) {
            parts.Add(char.ToLowerInvariant(world.Text[0]) + world.Text[1..].TrimEnd('.'));
        }

        var levels = day.Where(e => e.Kind == "level").Select(e => e.Text.Replace("Reached level ", "").Split(' ')[0]).ToList();
        if (levels.Count > 0) {
            parts.Add(levels.Count == 1 ? $"reached level {levels[0]}" : $"reached levels {string.Join(", ", levels.Take(levels.Count - 1))} and {levels[^1]}");
        }

        var (won, lost) = (Count("won"), Count("lost"));
        if (won + lost > 0) {
            parts.Add(won > 0 && lost > 0 ? $"won {Times(won, "tough fight", "tough fights")} and lost {lost}"
                : won > 0 ? $"won {Times(won, "tough fight", "tough fights")}" : $"lost {Times(lost, "tough fight", "tough fights")}");
        }

        var met = day.Where(e => e.Kind == "met" && e.About.Length > 0).Select(e => e.About).Distinct().ToList();
        if (met.Count > 0) {
            parts.Add("met " + (met.Count == 1 ? met[0] : string.Join(", ", met.Take(met.Count - 1)) + " and " + met[^1]));
        }

        if (Count("party") is > 0 and var party) {
            parts.Add($"went along with a group {Times(party, "time", "times")}");
        }

        if (Count("gear") is > 0 and var gear) {
            parts.Add($"put on {Times(gear, "better piece of gear", "better pieces of gear")}");
        }

        if (Count("stuck") is > 0 and var stuck) {
            parts.Add($"got stuck {(stuck == 1 ? "once" : $"{stuck} times")}");
        }

        var feeling = day.Sum(e => e.Feeling);
        var mood = feeling >= 2 ? "A good day." : feeling <= -2 ? "A rough day." : "An ordinary day.";
        if (parts.Count == 0) {
            return mood;
        }

        var said = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

        return $"{mood} I {said}.";
    }

}
