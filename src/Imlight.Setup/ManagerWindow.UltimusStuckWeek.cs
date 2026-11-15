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
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Line = Avalonia.Controls.Shapes.Line;
using Polyline = Avalonia.Controls.Shapes.Polyline;
using Path = System.IO.Path;
using Avalonia.Layout;
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

    private readonly TextBlock _ultGates = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly StackPanel _ultGateRows = new() { Spacing = 4 };
    private string _ultGatesAll = "";

    // The last 14 days of stuck time as a line (Niduus, 2026-11-15, "all"): one point a day, minutes up the side.
    private readonly Canvas _ultStuckChart = new() { Height = 110, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private const double CHART_WIDTH = 420, CHART_HEIGHT = 80, CHART_LEFT = 40;
    private DateTime _ultGatesRead = DateTime.MinValue;

    private string GatesAheadPath => Path.Combine(_manager.LogFolder, "bots", "gates-ahead.txt");

    // Once a minute: the minutes he stood still or was getting unstuck over the last 7 days, and the 7 before.
    private void ShowUltimusStuckWeek() {
        if (DateTime.UtcNow - _ultStuckWeekRead < TimeSpan.FromMinutes(1) && _ultStuckWeek.Text?.Length > 0) {
            return;
        }

        _ultStuckWeekRead = DateTime.UtcNow;
        var (week, before) = StuckMinutesByWeek(Path.Combine(UltimusFolder, "stuck-days.json"));
        DrawStuckChart(StuckMinutesByDay(Path.Combine(UltimusFolder, "stuck-days.json")));
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
        if (StuckMinutesByDay(path) is not { } seconds) {
            return (null, null);
        }

        var now = DateTime.Today;
        double Sum(int from, int to) => seconds.Where(d => d.Key > now.AddDays(-to) && d.Key <= now.AddDays(-from)).Sum(d => d.Value);

        return (Sum(0, 7), seconds.Keys.Any(d => d <= now.AddDays(-7)) ? Sum(7, 14) : null);
    }

    // The minutes of each day written down (null when there is no file).
    private static Dictionary<DateTime, double>? StuckMinutesByDay(string path) {
        try {
            if (!File.Exists(path)) {
                return null;
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

            return seconds.ToDictionary(d => d.Key, d => d.Value / 60);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    private void DrawStuckChart(Dictionary<DateTime, double>? days) {
        _ultStuckChart.Children.Clear();
        _ultStuckChart.IsVisible = days is { Count: > 0 };
        if (days is not { Count: > 0 }) {
            return;
        }

        var today = DateTime.Today;
        var values = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).Select(d => (Day: d, Minutes: days.GetValueOrDefault(d))).ToList();
        var most = Math.Max(10, values.Max(v => v.Minutes));
        var step = CHART_WIDTH / (values.Count - 1);
        Point At(int i) => new(CHART_LEFT + (i * step), 10 + CHART_HEIGHT - (values[i].Minutes / most * CHART_HEIGHT));

        // The floor and the top, with the minutes they stand for.
        _ultStuckChart.Children.Add(new Line { StartPoint = new Point(CHART_LEFT, 10 + CHART_HEIGHT), EndPoint = new Point(CHART_LEFT + CHART_WIDTH, 10 + CHART_HEIGHT), Stroke = ManagerTheme.LINE_BRUSH, StrokeThickness = 1 });
        _ultStuckChart.Children.Add(new Line { StartPoint = new Point(CHART_LEFT, 10), EndPoint = new Point(CHART_LEFT + CHART_WIDTH, 10), Stroke = ManagerTheme.LINE_BRUSH, StrokeThickness = 1, StrokeDashArray = [2, 3] });
        Label($"{most:0} min", 0, 2);
        Label("0", 24, 2 + CHART_HEIGHT);

        var line = new Polyline { Stroke = ManagerTheme.Accent("good"), StrokeThickness = 2 };
        for (var i = 0; i < values.Count; i++) {
            line.Points.Add(At(i));
        }

        _ultStuckChart.Children.Add(line);
        for (var i = 0; i < values.Count; i++) {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = ManagerTheme.Accent("good") };
            ToolTip.SetTip(dot, $"{values[i].Day:ddd d MMM}: {values[i].Minutes:0} minutes stuck");
            Canvas.SetLeft(dot, At(i).X - 3);
            Canvas.SetTop(dot, At(i).Y - 3);
            _ultStuckChart.Children.Add(dot);
            if (i % 2 == 1 || i == values.Count - 1) {
                Label(i == values.Count - 1 ? "today" : values[i].Day.ToString("d MMM", CultureInfo.InvariantCulture), At(i).X - 14, 14 + CHART_HEIGHT);
            }
        }

        void Label(string text, double x, double y) {
            var block = new TextBlock { Text = text, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            _ultStuckChart.Children.Add(block);
        }
    }

    private Control UltimusGatesFold() => Fold("list", ULTIMUS_TAB, "Locked ways ahead",
        "Doors and teleporters in every story world that open only once a quest step is done, read from the game's own files " +
        "when the server starts. When Ultimus or the Quest Team need one of these ways and hold its quest, they do that quest " +
        "first (like the Temple of Storms' tests before its boss). Updates by itself.",
        Row(Action(Plain("Copy the list"), () => CopyAsync(_ultGatesAll)), Action(Plain("Open its folder"), () => {
            var folder = Path.GetDirectoryName(GatesAheadPath)!;
            Directory.CreateDirectory(folder);

            return Open(folder);
        })),
        _ultGates,
        _ultGateRows);

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
            _ultGatesAll = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
            ShowGateRows(_ultGatesAll);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            _ultGates.Text = "The list could not be read right now.";
        }
    }

    // One row per locked way: where it is, where it leads, the quest that opens it, and a button that sends your own
    // wizard to that area (.tp, Niduus, 2026-11-15) to look at it.
    private const int MOST_GATE_ROWS = 300;

    private void ShowGateRows(string text) {
        _ultGateRows.Children.Clear();
        var rows = new List<(string Zone, string Line)>();
        var zone = "";
        foreach (var raw in text.Split('\n')) {
            if (raw.StartsWith("    to ", StringComparison.Ordinal) && zone.Length > 0) {
                rows.Add((zone, raw.Trim()));
            }
            else if (raw.Length > 0 && !raw.StartsWith(' ') && raw.Contains('/') && !raw.Contains(' ')) {
                zone = raw.Trim();
            }
        }

        _ultGates.Text = rows.Count == 0 ? "No locked ways found yet." : $"{rows.Count} locked way(s) in the story worlds" +
            (rows.Count > MOST_GATE_ROWS ? $"; the first {MOST_GATE_ROWS} are shown (Copy the list has them all)." : ".");
        foreach (var (where, line) in rows.Take(MOST_GATE_ROWS)) {
            var go = Action(Plain("Go there"), () => {
                if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
                    throw new PanelException("Start the server and log in to your wizard first.");
                }

                _manager.SendCommand($".tp {where}");
                Say($"Sent: your wizard goes to {where}, where this locked way is.");

                return Task.CompletedTask;
            });
            ToolTip.SetTip(go, $"Your wizard (logged in on this PC) goes to this area to look at the way. In the game: .tp {where}");
            _ultGateRows.Children.Add(new DockPanel {
                Children = {
                    WithDock(WithCenter(go), Dock.Right),
                    WithCenter(new TextBlock { Text = $"{where}: {line}", TextWrapping = TextWrapping.Wrap }),
                },
            });
        }
    }

}
