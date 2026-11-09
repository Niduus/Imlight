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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The side bar (Niduus, 2026-11-09, mockup "Navigation 1": "Begin making the UI easier to navigate"): every page and tab
/// is always listed on the left, so nothing hides behind a menu. Pages starred with the star over a tab are pinned at the
/// top, the last few tabs opened are under Recent, and the bar over each tab has Back and Forward and says where you are
/// ("Quests › My Quests"). The pages' own tab strips are hidden (ManagerTheme), but the tabs are the same TabControls, so
/// SelectTab and PageChanged work as before.
/// </summary>
internal sealed partial class ManagerWindow {

    // Kept next to the app on this PC, one tab name a line; updates never touch them.
    private const string PINNED_FILE = "pinned-pages.txt";
    private const string RECENT_FILE = "recent-pages.txt";
    private const int RECENT_COUNT = 5;
    private const double SIDE_WIDTH = 236;

    private readonly List<string> _pinned = [];
    private readonly List<string> _recent = [];
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];
    private readonly HashSet<string> _openGroups = [];
    private bool _historyMove;
    private string? _lastShown;

    private readonly StackPanel _sideList = new() { Spacing = 1 };
    private readonly Button _backButton = Plain("‹");
    private readonly Button _forwardButton = Plain("›");
    private readonly Button _pinButton = Plain("Pin");
    private readonly TextBlock _crumbGroup = new() { Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _crumbArrow = new() { Text = "›", Foreground = MUTED, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _crumbTab = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The page area: the side bar on the left, the bar with Back, Forward and where you are over the tabs.</summary>
    private Control SideBarLayout(Control nav) {
        _pinned.AddRange(ReadPageList(PINNED_FILE));
        _recent.AddRange(ReadPageList(RECENT_FILE).Take(RECENT_COUNT));

        ToolTip.SetTip(_backButton, "Back to the tab you were on before (Alt+Left)");
        ToolTip.SetTip(_forwardButton, "Forward again (Alt+Right)");
        _backButton.Click += (_, _) => GoBack();
        _forwardButton.Click += (_, _) => GoForward();
        _pinButton.Click += (_, _) => TogglePin(ShownPage());
        foreach (var button in new[] { _backButton, _forwardButton }) {
            button.FontSize = 18;
            button.Padding = new Thickness(12, 0, 12, 2);
        }

        var crumbs = new StackPanel {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0),
            Children = { _crumbGroup, _crumbArrow, _crumbTab },
        };
        var bar = new DockPanel {
            Margin = new Thickness(24, 10, 24, 0), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH + 2,
            Children = {
                WithDock(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _backButton, _forwardButton } }, Dock.Left),
                WithDock(crumbs, Dock.Left),
                WithDock(_pinButton, Dock.Left),
                new Panel(),
            },
        };

        var side = ManagerTheme.GlassPanel(new ScrollViewer {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8, 8, 8, 10), Content = _sideList,
        }, 16);
        side.Width = SIDE_WIDTH;
        side.Margin = new Thickness(12, 10, 0, 12);

        AddHandler(KeyDownEvent, (_, e) => {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left) {
                GoBack();
                e.Handled = true;
            }
            else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right) {
                GoForward();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        SideBarShown(ShownPage() ?? HOME);

        return new DockPanel {
            Children = {
                WithDock(side, Dock.Left),
                WithDock(bar, Dock.Top),
                nav,
            },
        };
    }

    /// <summary>A tab came into view: History, Recent, the bar over the tab and the side bar follow it.</summary>
    private void SideBarShown(string? shown) {
        shown ??= HOME;
        if (_lastShown is not null && _lastShown != shown && !_historyMove) {
            _back.Add(_lastShown);
            if (_back.Count > 50) {
                _back.RemoveAt(0);
            }

            _forward.Clear();
        }

        _historyMove = false;
        _lastShown = shown;
        if (shown != HOME) {
            _recent.Remove(shown);
            _recent.Insert(0, shown);
            if (_recent.Count > RECENT_COUNT) {
                _recent.RemoveRange(RECENT_COUNT, _recent.Count - RECENT_COUNT);
            }

            WritePageList(RECENT_FILE, _recent);
        }

        // Only the page you are on stays open (a page opened by hand stays open until you go to another tab).
        var group = GroupOf(shown);
        _openGroups.Clear();
        if (group is not null) {
            _openGroups.Add(group);
        }

        _crumbGroup.Text = group ?? "";
        _crumbArrow.IsVisible = group is not null;
        _crumbTab.Text = shown;
        _backButton.IsEnabled = _back.Count > 0;
        _forwardButton.IsEnabled = _forward.Count > 0;
        _pinButton.IsVisible = shown != HOME;
        _pinButton.Content = PinContent(_pinned.Contains(shown));
        ToolTip.SetTip(_pinButton, _pinned.Contains(shown) ? "Take this tab off Pinned in the side bar" : "Pin this tab to the top of the side bar");
        RenderSideBar();
    }

    private static object PinContent(bool pinned) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = {
            ManagerTheme.Icon(pinned ? "star" : "starline", 14, pinned ? ManagerTheme.Brush(ManagerTheme.BLOCK_WHEN) : MUTED),
            new TextBlock { Text = pinned ? "Pinned" : "Pin", VerticalAlignment = VerticalAlignment.Center },
        },
    };

    private static string? GroupOf(string tab) => tab is HOME or AI_GUIDE ? null : s_groups.FirstOrDefault(g => g.Tabs.Contains(tab)).Name;

    private void GoBack() {
        if (_back.Count == 0 || _lastShown is null) {
            return;
        }

        var to = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(_lastShown);
        _historyMove = true;
        SelectTab(to);
    }

    private void GoForward() {
        if (_forward.Count == 0 || _lastShown is null) {
            return;
        }

        var to = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_lastShown);
        _historyMove = true;
        SelectTab(to);
    }

    private void TogglePin(string? tab) {
        if (tab is null or HOME) {
            return;
        }

        if (!_pinned.Remove(tab)) {
            _pinned.Add(tab);
        }

        WritePageList(PINNED_FILE, _pinned);
        SideBarShown(_lastShown);
    }

    /// <summary>Pinned, Recent, then Home and every page with its tabs; the page you are on is open, the tab lit.</summary>
    private void RenderSideBar() {
        var shown = _lastShown ?? HOME;
        _sideList.Children.Clear();

        var pinned = _pinned.Where(s_tabs.ContainsKey).ToList();
        if (pinned.Count > 0) {
            _sideList.Children.Add(SideHeading("Pinned"));
            foreach (var tab in pinned) {
                _sideList.Children.Add(SideTab(tab, shown, pinnedRow: true));
            }
        }

        var recent = _recent.Where(t => s_tabs.ContainsKey(t) && !pinned.Contains(t)).Take(3).ToList();
        if (recent.Count > 0) {
            _sideList.Children.Add(SideHeading("Recent"));
            foreach (var tab in recent) {
                _sideList.Children.Add(SideTab(tab, shown, pinnedRow: false));
            }
        }

        _sideList.Children.Add(SideHeading("Everything"));
        _sideList.Children.Add(SidePage(HOME, "home", shown == HOME, open: null, () => SelectTab(HOME)));
        if (AI_GUIDE_ON) {
            _sideList.Children.Add(SidePage(AI_GUIDE, "chat", shown == AI_GUIDE, open: null, () => SelectTab(AI_GUIDE)));
        }

        foreach (var group in s_groups) {
            var name = group.Name;
            var isOpen = _openGroups.Contains(name);
            _sideList.Children.Add(SidePage(name, group.Icon, group.Tabs.Contains(shown), isOpen, () => {
                if (!_openGroups.Remove(name)) {
                    _openGroups.Add(name);
                }

                RenderSideBar();
            }));
            if (!isOpen) {
                continue;
            }

            foreach (var tab in group.Tabs) {
                _sideList.Children.Add(SideSubTab(tab, tab == shown));
            }
        }
    }

    private static TextBlock SideHeading(string text) => new() {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED,
        Margin = new Thickness(10, 12, 0, 4),
    };

    private static Button SideButton(Control content, bool lit, Action click, double left = 8) {
        var button = new Button {
            Classes = { "ui" }, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(left, 5, 8, 5),
            Background = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT, 70) : Brushes.Transparent,
            BorderBrush = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 150) : Brushes.Transparent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL),
            FontWeight = lit ? FontWeight.SemiBold : FontWeight.Normal,
        };
        button.Click += (_, _) => click();

        return button;
    }

    /// <summary>A page of the side bar: its icon tile and name; clicking it opens or folds its tabs.</summary>
    private static Button SidePage(string name, string icon, bool lit, bool? open, Action click) {
        var row = new DockPanel {
            Children = {
                WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), Child = ManagerTheme.Tile(icon, ManagerTheme.Accent(name), 24) }, Dock.Left),
            },
        };
        if (open is { } isOpen) {
            row.Children.Add(WithDock(new TextBlock { Text = isOpen ? "⌄" : "›", Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) }, Dock.Right));
        }

        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = SideButton(row, lit && open is null, click);
        ToolTip.SetTip(button, name == HOME ? "Start here: run the server and search everything." : s_groups.FirstOrDefault(g => g.Name == name).About);

        return button;
    }

    /// <summary>A tab under its page in the side bar.</summary>
    private Button SideSubTab(string tab, bool lit) {
        var (icon, line) = s_tabs[tab];
        var button = SideButton(new DockPanel {
            Children = {
                WithDock(new Border { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = ManagerTheme.Icon(icon, 13, ManagerTheme.Accent(tab)) }, Dock.Left),
                new TextBlock { Text = tab, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        }, lit, () => SelectTab(tab), left: 36);
        ToolTip.SetTip(button, line);

        return button;
    }

    /// <summary>A tab under Pinned or Recent: its icon and name, and which page it is on.</summary>
    private Button SideTab(string tab, string shown, bool pinnedRow) {
        var (icon, line) = s_tabs[tab];
        var button = SideButton(new DockPanel {
            Children = {
                WithDock(new Border {
                    Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = pinnedRow ? ManagerTheme.Icon("star", 13, ManagerTheme.Brush(ManagerTheme.BLOCK_WHEN)) : ManagerTheme.Icon(icon, 13, ManagerTheme.Accent(tab)),
                }, Dock.Left),
                new StackPanel {
                    Children = {
                        new TextBlock { Text = tab, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = GroupOf(tab) ?? "", FontSize = 11, Foreground = MUTED },
                    },
                },
            },
        }, tab == shown, () => SelectTab(tab));
        ToolTip.SetTip(button, line);

        return button;
    }

    private List<string> ReadPageList(string file) {
        try {
            var path = Path.Combine(_manager.InstallFolder, file);
            return File.Exists(path) ? [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct()] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private void WritePageList(string file, List<string> tabs) {
        try {
            File.WriteAllLines(Path.Combine(_manager.InstallFolder, file), tabs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Not kept this time; the side bar still shows it until the app closes.
        }
    }

}
