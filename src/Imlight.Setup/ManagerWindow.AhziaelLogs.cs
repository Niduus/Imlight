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
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Server > Logs > "Ahziael's crashes and refusals" (Niduus, 2026-11-12: "provide Ahziael logs section that log when the
/// program crashes or refuses"): every time Ahziael crashed, a tab could not open, something failed, Ahziael said no
/// (the red line at the bottom), or the server stopped by itself, newest first, with the full details a click away.
/// It fills in by itself while it is open.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ALL_KINDS = "Everything";
    private const int AHZIAEL_LOG_SHOWN = 300;

    private readonly ComboBox _alKind = FilterCombo([ALL_KINDS, AppLogs.CRASH, AppLogs.ERROR, AppLogs.REFUSED, AppLogs.SERVER_STOPPED], 160);
    private readonly TextBox _alSearch = new() { Watermark = "Search the messages, such as Monstrology, save or could not" };
    private readonly TextBlock _alSummary = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly StackPanel _alRows = new() { Spacing = 6 };
    private List<AppLogs.Event> _alEvents = [];
    private bool _alReading;
    private bool _alAgain;

    private Control AhziaelLogsCard() {
        WhenFiltersChange(RenderAhziaelLogs, _alKind, _alSearch);
        AppLogs.Logged += () => Dispatcher.UIThread.Post(() => _ = ReadAhziaelLogsAsync());
        _ = ReadAhziaelLogsAsync();

        return Fold("shield", "bad", "Ahziael's crashes and refusals",
            "Every time Ahziael crashed, failed or refused to do something, newest first. It fills in by itself.",
            Sub("Crash: Ahziael, its window or one of its tabs stopped. Error: something failed but Ahziael kept running. " +
                "Refused: Ahziael said no (the red line at the bottom of the window). Server stopped: the server closed by itself. " +
                "Click Details on a row to see everything that was written down. The last 30 days are kept."),
            SearchFilters(SearchFilter("Show", _alKind)),
            _alSearch,
            _alSummary,
            new ScrollViewer { MaxHeight = 520, Content = _alRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            Row(Action(Plain("Copy the list"), CopyAhziaelLogsAsync),
                Action(Primary("Make a zip to send"), MakeLogsZipAsync),
                Action(Danger(Plain("Clear the list")), ClearAhziaelLogsAsync)));
    }

    private async Task ReadAhziaelLogsAsync() {
        if (_alReading) {
            _alAgain = true;

            return;
        }

        _alReading = true;
        try {
            do {
                _alAgain = false;
                var install = _manager.InstallFolder;
                _alEvents = await Task.Run(() => AppLogs.ReadEvents(install));
            } while (_alAgain);

            RenderAhziaelLogs();
        }
        catch (Exception) {
            // The list stays as it was; the files are still there.
        }
        finally {
            _alReading = false;
        }
    }

    private List<AppLogs.Event> ShownAhziaelLogs() {
        var kind = FilterPick(_alKind);
        var words = (_alSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return _alEvents.Where(e => (kind is null || e.Kind == kind)
                                    && words.All(w => e.What.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                      || e.Details.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void RenderAhziaelLogs() {
        var shown = ShownAhziaelLogs();
        var counts = _alEvents.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        int Count(string kind) => counts.GetValueOrDefault(kind);
        static string Many(int n, string one, string more) => $"{n} {(n == 1 ? one : more)}";
        _alSummary.Text = _alEvents.Count == 0
            ? "Nothing went wrong yet. When Ahziael crashes, fails or refuses something, it shows up here."
            : $"{Many(Count(AppLogs.CRASH), "crash", "crashes")}, {Many(Count(AppLogs.ERROR), "error", "errors")}, " +
              $"{Many(Count(AppLogs.REFUSED), "refusal", "refusals")} and {Many(Count(AppLogs.SERVER_STOPPED), "server stop", "server stops")}." +
              (shown.Count != _alEvents.Count ? $" Showing {shown.Count} that match." : "") +
              (shown.Count > AHZIAEL_LOG_SHOWN ? $" The newest {AHZIAEL_LOG_SHOWN} are listed." : "");
        _alRows.Children.Clear();
        foreach (var e in shown.Take(AHZIAEL_LOG_SHOWN)) {
            _alRows.Children.Add(AhziaelLogRow(e));
        }
    }

    private static IBrush KindBrush(string kind) => kind switch {
        AppLogs.CRASH => BAD,
        AppLogs.ERROR => WARN,
        AppLogs.SERVER_STOPPED => BAD,
        _ => MUTED,
    };

    private static Control AhziaelLogRow(AppLogs.Event e) {
        var pill = new Border {
            Background = KindBrush(e.Kind), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = e.Kind, Foreground = Brushes.White, FontSize = ManagerTheme.FONT_SMALL, FontWeight = FontWeight.SemiBold },
        };
        var when = new TextBlock {
            Text = $"{e.Time:yyyy-MM-dd HH:mm:ss}" + (e.Version.Length > 0 ? $"  ·  version {e.Version}" : ""),
            Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, VerticalAlignment = VerticalAlignment.Center,
        };
        var body = new StackPanel {
            Spacing = 4,
            Children = {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { pill, when } },
                new SelectableTextBlock { Text = e.What, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (e.Details.Trim().Length > 0) {
            body.Children.Add(new Expander {
                Header = "Details", HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 0),
                Content = new SelectableTextBlock { Text = e.Details.Trim(), FontFamily = MONO, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = MUTED },
            });
        }

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL),
            BorderBrush = KindBrush(e.Kind), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8),
            Child = body,
        };
    }

    private Task CopyAhziaelLogsAsync() {
        var shown = ShownAhziaelLogs();
        if (shown.Count == 0) {
            Say("There is nothing in the list to copy.");

            return Task.CompletedTask;
        }

        var text = new StringBuilder();
        foreach (var e in shown) {
            text.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Kind}] {e.What} (version {e.Version})");
            if (e.Details.Trim().Length > 0) {
                text.AppendLine(e.Details.Trim());
            }

            text.AppendLine();
        }

        return CopyAsync(text.ToString(), $"Copied {shown.Count} entries. Paste them with Ctrl+V.");
    }

    private async Task ClearAhziaelLogsAsync() {
        if (_alEvents.Count == 0) {
            Say("The list is already empty.");

            return;
        }

        if (!await ConfirmAsync("Clear the list? The log files stay in the logs folder, so Make a zip to send still has them.")) {
            return;
        }

        var install = _manager.InstallFolder;
        await Task.Run(() => AppLogs.ClearList(install));
        Say("Cleared. New crashes and refusals show up here as they happen.");
    }

}
