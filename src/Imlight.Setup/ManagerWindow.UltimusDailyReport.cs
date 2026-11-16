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
/// Ultimus's daily report (Niduus, 2026-11-16: "a plain summary in Ahziael of what he did, where he got stuck, and what he
/// learned"). One day at a time, picked from the last two weeks, in three parts: what he did (quests, levels, worlds, fights
/// and how they went, bosses he read up on, players he taught or helped), where he got stuck (the day's worst spots and
/// each stuck moment), and what he learned (the lessons from his mistakes and his own end-of-day review). Read only from the
/// files the server writes in his folder (journal.json, effort.txt, stuck-days.json, learned.jsonl, learned-today.txt), so
/// nothing in it is made up.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int REPORT_DAYS = 14;
    private TextBox? _ultReportBox;
    private readonly ComboBox _ultReportDay = new() { MinWidth = 260 };
    private string _ultReportText = "";
    private DateTime _ultReportRead = DateTime.MinValue;
    private List<DateTime> _ultReportDays = [];

    private Control UltimusDailyReportFold() {
        _ultReportBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
            Height = 340, Text = "Turn Ultimus on and start the server; his daily report is written here as he plays.",
        };
        _ultReportDays = [.. Enumerable.Range(0, REPORT_DAYS).Select(i => DateTime.Today.AddDays(-i))];
        _ultReportDay.ItemsSource = _ultReportDays.Select(ReportDayName).ToList();
        _ultReportDay.SelectedIndex = 0;
        _ultReportDay.SelectionChanged += (_, _) => {
            _ultReportRead = DateTime.MinValue;
            ShowUltimusDailyReport();
        };

        return Fold("book", ULTIMUS_TAB, "Daily report",
            "One day of Ultimus in three plain parts: what he did (quests, levels, worlds, fights won and lost, bosses he read up " +
            "on, players he walked through quests or helped in a fight), where he got stuck, and what he learned. Pick a day from " +
            $"the last {REPORT_DAYS}. Made only from the files he writes as he plays, so nothing in it is made up. Updated every 30 seconds.",
            Row(new TextBlock { Text = "Day:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, _ultReportDay),
            LogWell(_ultReportBox),
            Row(Action(Plain("Copy report"), () => CopyAsync(_ultReportText))));
    }

    private static string ReportDayName(DateTime day)
        => (day == DateTime.Today ? "Today, " : day == DateTime.Today.AddDays(-1) ? "Yesterday, " : "") +
           day.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

    private void ShowUltimusDailyReport() {
        if (_ultReportBox is null || DateTime.UtcNow - _ultReportRead < TimeSpan.FromSeconds(30)) {
            return;
        }

        _ultReportRead = DateTime.UtcNow;
        var index = Math.Clamp(_ultReportDay.SelectedIndex, 0, Math.Max(0, _ultReportDays.Count - 1));
        var day = _ultReportDays.Count > 0 ? _ultReportDays[index] : DateTime.Today;
        var text = UltimusDailyReportText(UltimusFolder, day);
        if (text != _ultReportText) {
            _ultReportText = text;
            _ultReportBox.Text = text;
        }
    }

    /// <summary>The report of one day (his PC's date) from the files in his folder.</summary>
    internal static string UltimusDailyReportText(string folder, DateTime day) {
        var date = day.Date;
        var journal = ReadJournal(Path.Combine(folder, "journal.json")).Where(e => e.At.ToLocalTime().Date == date).OrderBy(e => e.At).ToList();
        var effort = ReadDayLines(Path.Combine(folder, "effort.txt"), date);
        var stuckTop = StuckDays(Path.Combine(folder, "stuck-days.json")).GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) ?? [];
        var lessons = ReadLessons(Path.Combine(folder, "learned.jsonl"), date);
        var review = ReadReview(Path.Combine(folder, "learned-today.txt"), date);

        var text = new StringBuilder();
        text.AppendLine($"Daily report for {date.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}");
        text.AppendLine();

        // What he did.
        text.AppendLine("WHAT I DID");
        var did = new List<string>();
        List<DiaryEntry> Of(string kind) => [.. journal.Where(e => e.Kind == kind)];
        if (Of("quest") is { Count: > 0 } quests) {
            did.Add($"Finished {quests.Count} quest(s): {string.Join(", ", quests.Select(q => q.About.Length > 0 ? q.About : q.Text).Distinct().Take(12))}" +
                    (quests.Count > 12 ? " and more." : "."));
        }

        did.AddRange(Of("world").Select(w => w.Text));
        did.AddRange(Of("level").Select(l => l.Text));
        int Effort(string kind) => effort.Count(l => l.Contains($" {kind}:", StringComparison.Ordinal));
        var (won, lost, fled, dungeons) = (Effort("FightWon"), Effort("FightLost"), Effort("Fled"), Effort("DungeonLost"));
        if (won + lost + fled + dungeons > 0) {
            did.Add($"Fights: {won} won, {lost} lost" + (fled > 0 ? $", {fled} fled" : "") + (dungeons > 0 ? $", {dungeons} dungeon(s) lost and started over" : "") + ".");
        }

        if (Of("won").Count + Of("lost").Count > 0) {
            did.Add($"Tough fights: {Of("won").Count} won, {Of("lost").Count} lost" +
                    (Of("lost").Count > 0 ? $" (lost to {string.Join(", ", Of("lost").Select(e => e.About).Where(a => a.Length > 0).Distinct().Take(5))})" : "") + ".");
        }

        if (Of("boss") is { Count: > 0 } bosses) {
            did.Add($"Read up on {bosses.Count} boss(es)' cheats before fighting them: {string.Join(", ", bosses.Select(b => b.About).Distinct().Take(8))}.");
        }

        if (Of("taught") is { Count: > 0 } taught) {
            did.Add($"Walked {taught.Count} player(s) through a quest: {string.Join("; ", taught.Select(t => t.Text.TrimEnd('.')).Take(6))}.");
        }

        if (Of("helped") is { Count: > 0 } helped) {
            did.Add($"Helped {helped.Count} player(s) in a fight: {string.Join("; ", helped.Select(t => t.Text.TrimEnd('.')).Take(6))}.");
        }

        if (Of("party").Count > 0) {
            did.Add($"Went along with a player's group {Of("party").Count} time(s).");
        }

        var met = Of("met").Select(e => e.About).Where(a => a.Length > 0).Distinct().ToList();
        if (met.Count > 0) {
            did.Add($"Met {string.Join(", ", met.Take(10))}.");
        }

        if (Of("gear") is { Count: > 0 } gear) {
            did.Add($"Put on {gear.Count} better piece(s) of gear: {string.Join(", ", gear.Select(g => g.About).Where(a => a.Length > 0).Distinct().Take(6))}.");
        }

        if (effort.Count(l => l.Contains(" PotionUsedWell:", StringComparison.Ordinal)) is > 0 and var potions) {
            did.Add($"Saved {potions} potion(s) for right before a boss.");
        }

        AppendLines(text, did, "Nothing written down for this day.");

        // Where he got stuck.
        text.AppendLine("WHERE I GOT STUCK");
        var stuck = new List<string>();
        stuck.AddRange(stuckTop.Select((l, i) => $"Most time lost #{i + 1}: {l}"));
        stuck.AddRange(Of("stuck").Select(e => $"{e.At.ToLocalTime():HH:mm} {e.Text}"));
        stuck.AddRange(effort.Where(l => l.Contains(" StuckStep:", StringComparison.Ordinal))
            .Select(l => l.Length > 11 ? l[11..16] + " " + l[(l.IndexOf(" StuckStep:", StringComparison.Ordinal) + 11)..].Trim() : l));
        AppendLines(text, [.. stuck.Distinct().Take(25)], "Nowhere. A clean day.");

        // What he learned.
        text.AppendLine("WHAT I LEARNED");
        var learned = new List<string>();
        learned.AddRange(lessons);
        learned.AddRange(review);
        AppendLines(text, [.. learned.Distinct().Take(30)], "Nothing new written down for this day.");

        return text.ToString().TrimEnd();
    }

    private static void AppendLines(StringBuilder text, List<string> lines, string none) {
        if (lines.Count == 0) {
            text.AppendLine("  " + none);
        }

        foreach (var line in lines) {
            text.AppendLine("  - " + line);
        }

        text.AppendLine();
    }

    private static List<DiaryEntry> ReadJournal(string path) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return [.. (JsonSerializer.Deserialize<List<DiaryEntry>>(stream) ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Text))];
        }
        catch (Exception) {
            return [];
        }
    }

    private static List<string> ReadAllLinesShared(string path) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return [.. reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n')];
        }
        catch (Exception) {
            return [];
        }
    }

    // effort.txt lines of one day ("2026-11-16 08:01:02 GOOD FightWon: ...").
    private static List<string> ReadDayLines(string path, DateTime date) {
        var prefix = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ";
        return [.. ReadAllLinesShared(path).Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];
    }

    // learned.jsonl: each mistake's lesson, from the day (its time is UTC).
    private static List<string> ReadLessons(string path, DateTime date) {
        var lessons = new List<string>();
        foreach (var line in ReadAllLinesShared(path)) {
            if (line.Length < 2) {
                continue;
            }

            try {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("at", out var at) || !at.TryGetDateTime(out var when) || when.ToLocalTime().Date != date) {
                    continue;
                }

                var lesson = root.TryGetProperty("lesson", out var l) ? l.GetString() ?? "" : "";
                var mistake = root.TryGetProperty("mistake", out var m) ? m.GetString() ?? "" : "";
                if (lesson.Length == 0 && mistake.Length == 0) {
                    continue;
                }

                lessons.Add(lesson.Length > 0 ? $"{lesson.TrimEnd('.')}." + (mistake.Length > 0 ? $" (after: {Shorten(mistake, 120)})" : "") : Shorten(mistake, 160));
            }
            catch (JsonException) {
                // A half-written line.
            }
        }

        return lessons;
    }

    // learned-today.txt: his own end-of-day review. A review written after midnight (before 6 AM) is about the day before.
    private static List<string> ReadReview(string path, DateTime date) {
        var lines = new List<string>();
        var taking = false;
        foreach (var line in ReadAllLinesShared(path)) {
            if (line.StartsWith("=== ", StringComparison.Ordinal)) {
                taking = DateTime.TryParseExact(line.Length >= 20 ? line[4..20] : "", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                             DateTimeStyles.None, out var written)
                         && (written.Date == date && written.Hour >= 6 || written.Date == date.AddDays(1) && written.Hour < 6);
                continue;
            }

            if (taking && line.StartsWith("- ", StringComparison.Ordinal)) {
                lines.Add(line[2..].Trim());
            }
        }

        return lines;
    }

    private static string Shorten(string text, int most) => text.Length <= most ? text : text[..(most - 1)].TrimEnd() + "…";

}
