/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Two things on the Ultimus pages (Niduus, 2026-11-15, "all"): the time he lost being stuck this week, next to last
/// week's, so it can be seen going down; and the ways that open only with a quest step (read ahead from the game's zone
/// files by the server, logs\bots\gates-ahead.txt), next to Read-ahead on the Ultimus Issues tab.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly TextBlock _ultStuckWeek = new() { TextWrapping = TextWrapping.Wrap };
    private DateTime _ultStuckWeekRead = DateTime.MinValue;

    private readonly TextBlock _ultGates = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
    private DateTime _ultGatesRead = DateTime.MinValue;

    private string GatesAheadPath => Path.Combine(_manager.LogFolder, "bots", "gates-ahead.txt");

    // Once a minute: the minutes he stood still or was getting unstuck over the last 7 days, and the 7 before.
    private void ShowUltimusStuckWeek() {
        if (DateTime.UtcNow - _ultStuckWeekRead < TimeSpan.FromMinutes(1) && _ultStuckWeek.Text?.Length > 0) {
            return;
        }

        _ultStuckWeekRead = DateTime.UtcNow;
        var (week, before) = StuckMinutesByWeek(Path.Combine(UltimusFolder, "stuck-days.json"));
        if (week is null) {
            _ultStuckWeek.Text = "Stuck time this week: nothing written down yet.";
            _ultStuckWeek.Foreground = MUTED;
            return;
        }

        var trend = before is not > 0 ? ""
            : week < before ? $", down from {Words(before.Value)} the week before"
            : week > before ? $", up from {Words(before.Value)} the week before"
            : ", the same as the week before";
        _ultStuckWeek.Text = $"Stuck time this week (the last 7 days): {Words(week.Value)}{trend}.";
        _ultStuckWeek.Foreground = before is > 0 && week > before ? ManagerTheme.Accent("Create") : ManagerTheme.TEXT_BRUSH;

        static string Words(double minutes) => minutes < 90 ? $"{minutes:0} minutes" : $"{minutes / 60:0.#} hours";
    }

    // stuck-days.json: each day's worst spots ("DaySpots") and today's ("Today"), summed by week. Null when there is no file.
    private static (double? Week, double? Before) StuckMinutesByWeek(string path) {
        try {
            if (!File.Exists(path)) {
                return (null, null);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var seconds = new Dictionary<DateTime, double>();
            void Add(string day, JsonElement spots) {
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || spots.ValueKind != JsonValueKind.Object) {
                    return;
                }

                // A day's spots, each counted once (today's live ones win over a copy written at the day's end).
                seconds[date] = spots.EnumerateObject()
                    .Sum(s => s.Value.ValueKind == JsonValueKind.Object && s.Value.TryGetProperty("Seconds", out var v) && v.TryGetDouble(out var n) ? n : 0);
            }

            if (root.TryGetProperty("DaySpots", out var days) && days.ValueKind == JsonValueKind.Object) {
                foreach (var day in days.EnumerateObject()) {
                    Add(day.Name, day.Value);
                }
            }

            if (root.TryGetProperty("Date", out var today) && today.ValueKind == JsonValueKind.String && root.TryGetProperty("Today", out var spots)) {
                Add(today.GetString() ?? "", spots);
            }

            var now = DateTime.Today;
            double Sum(int from, int to) => seconds.Where(d => d.Key > now.AddDays(-to) && d.Key <= now.AddDays(-from)).Sum(d => d.Value) / 60;

            return (Sum(0, 7), seconds.Keys.Any(d => d <= now.AddDays(-7)) ? Sum(7, 14) : null);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return (null, null);
        }
    }

    private Control UltimusGatesFold() => Fold("list", ULTIMUS_TAB, "Locked ways ahead",
        "Doors and teleporters in every story world that open only once a quest step is done, read from the game's own files " +
        "when the server starts. When Ultimus or the Quest Team need one of these ways and hold its quest, they do that quest " +
        "first (like the Temple of Storms' tests before its boss). Updates by itself.",
        Row(Action(Plain("Copy the list"), () => CopyAsync(_ultGates.Text ?? "")), Action(Plain("Open its folder"), () => {
            var folder = Path.GetDirectoryName(GatesAheadPath)!;
            Directory.CreateDirectory(folder);

            return Open(folder);
        })),
        _ultGates);

    private void ShowUltimusGates() {
        var changed = File.Exists(GatesAheadPath) ? File.GetLastWriteTimeUtc(GatesAheadPath) : DateTime.MinValue;
        if (changed == _ultGatesRead && _ultGates.Text?.Length > 0) {
            return;
        }

        _ultGatesRead = changed;
        try {
            if (changed == DateTime.MinValue) {
                _ultGates.Text = "Not read yet. The list is written a minute or so after the server starts.";
                return;
            }

            using var stream = new FileStream(GatesAheadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
            _ultGates.Text = text.Length > 64 * 1024 ? text[..(64 * 1024)] + "\n..." : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            _ultGates.Text = "The list could not be read right now.";
        }
    }

}
