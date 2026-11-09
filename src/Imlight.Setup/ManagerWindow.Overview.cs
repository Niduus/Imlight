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
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Server > Overview (Niduus, 2026-11-09: "Need a overview tab to see who is online, on what character, at what location
/// and so on"): counts at the top, everyone online with their wizard, level, school, place, whether they are in a battle
/// and how long they have been on, and the accounts that were online last, with when, on which wizard and where.
/// It reads the same online-players.json as Home's "Who's online" card.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string OVERVIEW = "Overview";
    private const int OVERVIEW_RECENT = 20;

    private readonly TextBlock _ovLine = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _ovStats = new();
    private readonly StackPanel _ovRows = new() { Spacing = 8 };
    private readonly TextBlock _ovRecentNote = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _ovRecent = new() { Spacing = 8 };
    // The "online for" lines, brought up to date with every status check.
    private readonly List<(TextBlock Text, DateTime Since)> _ovTimes = [];
    private bool _ovLoadingRecent;
    private int _ovLastCount = -1;

    private Control OverviewTab() {
        _ovLine.Text = "Checking the server…";
        _ovRecentNote.Text = "Open this tab while the server is running to see who was online last.";

        return Page(
            Fold("eye", OVERVIEW, "Right now",
                "How many players are on your server and what they are doing. It updates by itself every few seconds.",
                _ovLine, _ovStats),
            Fold("users", OVERVIEW, "Who's online",
                "Everyone playing right now: the wizard they are on, its level and school, their account, where they are, " +
                "whether they are in a battle and how long they have been on.",
                _ovRows),
            Fold("clock", OVERVIEW, "Last online",
                "The accounts that logged in to your server, the most recent first: when they were last on, on which wizard and where.",
                new DockPanel {
                    Children = { WithDock(Action(Plain("Refresh"), LoadOverviewRecentAsync), Dock.Right), WithCenter(_ovRecentNote) },
                },
                _ovRecent));
    }

    /// <summary>Fills the Right now and Who's online cards from the list just read (null: the server wrote none).</summary>
    private void ShowOverview(List<OnlineRow>? rows, bool serverHasIt, bool running) {
        _ovRows.Children.Clear();
        _ovStats.Children.Clear();
        _ovTimes.Clear();
        _ovLine.Foreground = MUTED;
        if (!running) {
            _ovLine.Text = "● The server is off, so nobody is online. Start it on Home or Server > Server.";
            _ovLastCount = -1;

            return;
        }

        if (rows is null) {
            _ovLine.Text = serverHasIt
                ? "● The server is on. Nobody is online yet."
                : "● The server is on, but this server copy can't tell who is online. Click \"Update Ahziael\" at the top to get it.";
            rows = [];
        }
        else {
            _ovLine.Text = rows.Count switch {
                0 => "● The server is on. Nobody is online right now.",
                1 => "● The server is on. 1 player is online.",
                _ => $"● The server is on. {rows.Count} players are online.",
            };
            _ovLine.Foreground = rows.Count > 0 ? GOOD : MUTED;
        }

        var picking = rows.Count(r => r.Wizard.Length == 0);
        var battling = rows.Count(r => r.InBattle);
        _ovStats.Children.Add(OverviewStat(rows.Count.ToString(), "Online", "users"));
        _ovStats.Children.Add(OverviewStat(battling.ToString(), "In a battle", "swords"));
        _ovStats.Children.Add(OverviewStat((rows.Count - picking - battling).ToString(), "Exploring", "map"));
        _ovStats.Children.Add(OverviewStat(picking.ToString(), "Picking a wizard", "user"));

        if (rows.Count == 0) {
            _ovRows.Children.Add(new TextBlock { Text = "Nobody is online right now.", Foreground = MUTED });
        }

        foreach (var row in rows.OrderBy(r => r.Wizard.Length == 0).ThenBy(r => r.Wizard, StringComparer.OrdinalIgnoreCase)) {
            _ovRows.Children.Add(OverviewPlayerRow(row));
        }

        // Someone came or went: the Last online list changes too (the server saves it a moment later).
        if (_ovLastCount != rows.Count) {
            var first = _ovLastCount < 0;
            _ovLastCount = rows.Count;
            if (_shownPage == OVERVIEW && !first) {
                _ = ReloadRecentSoonAsync();
            }
        }

        TickOverview();
    }

    private async Task ReloadRecentSoonAsync() {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await LoadOverviewRecentAsync();
    }

    private static Control OverviewStat(string number, string label, string icon) => new Border {
        Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 10, 10),
        MinWidth = 150,
        Child = new DockPanel {
            Children = {
                WithDock(new Border {
                    Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = ManagerTheme.Tile(icon, ManagerTheme.Accent(OVERVIEW), 32),
                }, Dock.Left),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new TextBlock { Text = number, FontSize = ManagerTheme.FONT_SECTION + 4, FontWeight = FontWeight.Bold },
                        new TextBlock { Text = label, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED },
                    },
                },
            },
        },
    };

    private Control OverviewPlayerRow(OnlineRow row) {
        var picking = row.Wizard.Length == 0;
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock {
            Text = picking ? "Picking a wizard" : row.Wizard, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });

        var about = new List<string>();
        if (!picking && row.Level > 0) {
            about.Add(row.School.Length > 0 && row.School != "None" ? $"Level {row.Level} {row.School}" : $"Level {row.Level}");
        }

        about.Add("Account: " + (row.Account.Length > 0 ? row.Account : "(name unknown)"));
        lines.Children.Add(new TextBlock {
            Text = string.Join(" · ", about), FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextWrapping = TextWrapping.Wrap,
        });

        var place = new TextBlock {
            Text = picking ? "At the wizard select screen" : PlaceInWords(row.Zone),
            TextWrapping = TextWrapping.Wrap, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT),
        };
        if (!picking && row.Zone.Length > 0) {
            ToolTip.SetTip(place, $"Zone {row.Zone}");
        }

        lines.Children.Add(place);
        if (row.Since is { } since) {
            var time = new TextBlock { FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED };
            _ovTimes.Add((time, since));
            lines.Children.Add(time);
        }

        var state = picking ? "Picking" : row.InBattle ? "In a battle" : "Exploring";
        var pill = new Border {
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center,
            Background = ManagerTheme.Brush(ManagerTheme.AccentColor(row.InBattle ? "Boss Test" : OVERVIEW), 70),
            Child = new TextBlock { Text = state, FontSize = ManagerTheme.FONT_SMALL, FontWeight = FontWeight.SemiBold },
        };

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(12, 10),
            Child = new DockPanel {
                Children = {
                    WithDock(new Border {
                        Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                        Child = ManagerTheme.Tile(picking ? "users" : row.InBattle ? "swords" : "sparkle", ManagerTheme.Accent(OVERVIEW), 36),
                    }, Dock.Left),
                    WithDock(WithMargin(pill, new Thickness(12, 0, 0, 0)), Dock.Right),
                    lines,
                },
            },
        };
    }

    /// <summary>Brings the "online for" lines up to date (each status check).</summary>
    private void TickOverview() {
        var now = DateTime.Now;
        foreach (var (text, since) in _ovTimes) {
            text.Text = "Online for " + SpanInWords(now - since);
        }
    }

    /// <summary>The Last online card: the accounts by when they were last on, read from the server's database.</summary>
    private async Task LoadOverviewRecentAsync() {
        if (_ovLoadingRecent) {
            return;
        }

        _ovLoadingRecent = true;
        try {
            List<AccountSummary> list;
            try {
                using var accounts = _manager.Accounts();
                list = await accounts.ListAsync();
            }
            catch (Exception ex) when (ex is AccountException or PanelException) {
                _ovRecent.Children.Clear();
                _ovRecentNote.Text = ex.Message;

                return;
            }

            _accountList = list;
            _ovRecent.Children.Clear();
            var seen = list.Where(a => IsOnlineNow(a.Username) || LastOnline(a) is not null)
                .OrderByDescending(a => IsOnlineNow(a.Username) ? DateTime.MaxValue : LastOnline(a) ?? DateTime.MinValue)
                .ToList();
            var never = list.Count - seen.Count;
            _ovRecentNote.Text = seen.Count == 0
                ? "No account has logged in since this was added. When someone logs in, they show here."
                : $"{seen.Count} of {list.Count} account{(list.Count == 1 ? "" : "s")} logged in to this server" +
                  (seen.Count > OVERVIEW_RECENT ? $"; the {OVERVIEW_RECENT} most recent are shown." : ".") +
                  (never > 0 ? " Accounts not on since this was added say Never on the Accounts tab until their next login." : "");
            foreach (var account in seen.Take(OVERVIEW_RECENT)) {
                _ovRecent.Children.Add(ManagerTheme.Item(new DockPanel {
                    Children = {
                        WithDock(WithMargin(WithCenter(ManagerTheme.Tile("user", IsOnlineNow(account.Username) ? GOOD : ManagerTheme.Accent(OVERVIEW), 30)),
                            new Thickness(0, 0, 12, 0)), Dock.Left),
                        new StackPanel {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                new TextBlock { Text = account.Username, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = LastOnlineWords(account), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                            },
                        },
                    },
                }));
            }
        }
        finally {
            _ovLoadingRecent = false;
        }
    }

    private bool IsOnlineNow(string username)
        => _onlineServerRunning == true && _onlineLatest.Any(r => r.Account.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "Online now as Ember, at The Commons, Wizard City", or "Last online 9 Nov 14:05 (2 hours ago) as Ember, at …", or
    /// "Last online: Never".
    /// </summary>
    private string LastOnlineWords(AccountSummary account) {
        if (_onlineServerRunning == true
            && _onlineLatest.FirstOrDefault(r => r.Account.Equals(account.Username, StringComparison.OrdinalIgnoreCase)) is { } now) {
            return now.Wizard.Length == 0
                ? "Online now, picking a wizard"
                : $"Online now as {now.Wizard}, at {PlaceInWords(now.Zone)}";
        }

        if (LastOnline(account) is not { } time) {
            return "Last online: Never";
        }

        var words = $"Last online {time:g} ({SpanInWords(DateTime.Now - time)} ago)";
        if (account.LastWizard is { Length: > 0 } wizard) {
            words += $" as {wizard}";
            if (account.LastZone is { Length: > 0 } zone) {
                words += $", at {PlaceInWords(zone)}";
            }
        }

        return words;
    }

    // 42 seconds, 5 minutes, 1 hour 20 minutes, 3 days.
    private static string SpanInWords(TimeSpan span) {
        if (span < TimeSpan.Zero) {
            span = TimeSpan.Zero;
        }

        static string Count(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

        if (span.TotalMinutes < 1) {
            return Count((int) span.TotalSeconds, "second");
        }

        if (span.TotalHours < 1) {
            return Count((int) span.TotalMinutes, "minute");
        }

        if (span.TotalDays < 1) {
            return span.Minutes == 0 ? Count((int) span.TotalHours, "hour") : $"{Count((int) span.TotalHours, "hour")} {Count(span.Minutes, "minute")}";
        }

        return Count((int) span.TotalDays, "day");
    }

}
