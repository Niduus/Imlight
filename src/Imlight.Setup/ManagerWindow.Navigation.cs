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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Imlight.Setup;

/// <summary>
/// The side bar and the Home page (Niduus, 2026-10-25: "Clean this up to be more organize ... a new home tab page where
/// these existing tabs become sub tabs"). The side bar holds a few pages; each page holds the old tabs as tabs along its
/// top. Every tab keeps its name as its Tag, so SelectTab("Spells") still finds it. Home greets, runs the server, and
/// searches everything at once.
/// </summary>
internal sealed partial class ManagerWindow {

    /// <summary>The side bar's pages after Home: name, icon, the short line under it, what it is for, and its tabs.</summary>
    private static readonly (string Name, string Icon, string Line, string About, string[] Tabs)[] s_groups = [
        ("Play", "play", "Play, commands, quests",
            "Play Wizard101, find chat commands and follow your wizard's quests.", ["Play", "Commands", "Quests"]),
        // Niduus, 2026-10-25: "move Bosses to Database".
        ("Database", "search", "IDs, spells, gear, bosses, vendors",
            "Everything in the game, ready to search: IDs, spell cards, gear, bosses, recipes, vendors and trainers, and every line of text.",
            ["Lookup", "Spells", "Gear", "Bosses", "Recipes", "Vendors", "Phrases"]),
        ("Create", "puzzle", "Cheats, customs, quests",
            "Change quests, make cheats for yourself, build your own creatures and areas, and word what the server announces.",
            ["Player Cheats", "Customs", "Quest Editor", "Announcements", "Guides"]),
        ("Server", "server", "Settings, accounts, logs",
            "Run the server and set it up: settings, who can log in, logs, game files and updates.",
            ["Server", "Settings", "Accounts", "Logs", "Game Files", "Tools"]),
    ];

    private const string HOME = "Home";

    private TabControl? _nav;
    private readonly Dictionary<string, TabControl> _groupTabs = [];
    private string? _shownPage;

    // Home.
    private readonly TextBox _homeSearch = new() {
        Watermark = "Search everything: pages, settings, commands, items, spells, creatures, zones and quests",
    };
    private readonly StackPanel _homeResults = new() { Spacing = 6 };
    // Niduus, 2026-10-25: every search with filters like the Gear tab's, and results when the box is empty.
    private const string HOME_PAGES = "Pages";
    private const string HOME_SETTINGS = "Settings";
    private static readonly string[] s_homeLookIn = [Lookup.EVERYTHING, HOME_PAGES, HOME_SETTINGS, .. Lookup.KINDS.Skip(1)];
    private readonly ComboBox _homeLookIn = FilterCombo(s_homeLookIn, 150);
    private readonly ComboBox _homeHowMany = FilterCombo(["6", "25", "100"], 90);
    private readonly DispatcherTimer _homeSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _homeSearchVersion;
    private readonly Button _homePlay = Primary("Play Wizard101");
    private readonly Button _homeStart = Primary("Start server");
    private readonly Button _homeStop = Plain("Stop server");
    private readonly TextBlock _homeStatus = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };

    /// <summary>The side bar: Home, then a page for each group with its tabs along the top.</summary>
    private TabControl Navigation(Dictionary<string, TabItem> tabs) {
        var items = new List<TabItem> {
            NavItem(HOME, "home", "Start here, search it all", HomePage()),
            NavItem(WHATS_NEW, "sparkle", "Every version's changes", WhatsNewPage()),
        };
        foreach (var group in s_groups) {
            var inner = new TabControl {
                Classes = { "subnav" }, Padding = new Thickness(0), TabStripPlacement = Dock.Top,
                ItemsSource = group.Tabs.Select(name => tabs[name]).ToArray(),
            };
            inner.SelectionChanged += (_, e) => {
                // A list or drop-down inside a tab sends its own SelectionChanged up here; only a new tab counts.
                if (e.Source == inner) {
                    PageChanged();
                }
            };
            _groupTabs[group.Name] = inner;
            items.Add(NavItem(group.Name, group.Icon, group.Line, inner));
        }

        var nav = new TabControl { Padding = new Thickness(0), TabStripPlacement = Dock.Left, ItemsSource = items };
        nav.SelectionChanged += (_, e) => {
            if (e.Source == nav) {
                PageChanged();
            }
        };
        _nav = nav;

        return nav;
    }

    private static TabItem NavItem(string name, string icon, string line, Control content) => new() {
        Tag = name,
        Classes = { "nav" },
        Header = ManagerTheme.NavHeader(icon, ManagerTheme.Accent(name), name, line),
        Content = content,
    };

    /// <summary>The tab showing now (its name), or Home.</summary>
    private string? ShownPage() {
        if (_nav?.SelectedItem is not TabItem top || top.Tag is not string name) {
            return null;
        }

        return _groupTabs.TryGetValue(name, out var inner) ? (inner.SelectedItem as TabItem)?.Tag as string : name;
    }

    /// <summary>Shows the tab of that name (its Tag), or Home: its page in the side bar, and the tab on that page.</summary>
    private void SelectTab(string name) {
        if (_nav is null) {
            return;
        }

        var groupName = name is HOME or WHATS_NEW ? name : s_groups.FirstOrDefault(g => g.Tabs.Contains(name)).Name;
        if (groupName is null) {
            return;
        }

        if (_groupTabs.TryGetValue(groupName, out var inner)
            && inner.Items.OfType<TabItem>().FirstOrDefault(t => (t.Tag as string) == name) is { } tab) {
            inner.SelectedItem = tab;
        }

        if (_nav.Items.OfType<TabItem>().FirstOrDefault(t => (t.Tag as string) == groupName) is { } page) {
            _nav.SelectedItem = page;
        }
    }

    // ---------------------------------------------------------------------
    // Home
    // ---------------------------------------------------------------------

    private Control HomePage() {
        Action(_homePlay, async () => Say(await _manager.PlayHereAsync()));
        Action(_homeStart, () => Task.FromResult(Say(_manager.StartServer())));
        Action(_homeStop, async () => {
            if (await ConfirmAsync("Stop the server? Everyone playing is disconnected.")) {
                Say(await Task.Run(() => _manager.StopServer()));
            }
        });
        Danger(_homeStop);

        _homeSearchDelay.Tick += async (_, _) => {
            _homeSearchDelay.Stop();
            await Guard(RenderHomeSearchAsync);
        };
        _homeSearch.TextChanged += (_, _) => {
            _homeSearchDelay.Stop();
            _homeSearchDelay.Start();
        };
        _homeSearch.FontSize = 15;
        _homeSearch.MinHeight = 42;
        _homeResults.Children.Add(HomeSearchTip());
        WhenFiltersChange(() => _ = Guard(RenderHomeSearchAsync), _homeLookIn, _homeHowMany);
        // The empty search lists the pages (and with Look in, everything of that kind) from the start.
        Dispatcher.UIThread.Post(() => _ = Guard(RenderHomeSearchAsync), DispatcherPriority.Background);

        var hero = new Border {
            Background = ManagerTheme.Gradient(70), BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 90), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(26, 22),
            Child = new DockPanel {
                Children = {
                    WithDock(new Border {
                        Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top,
                        Child = ManagerTheme.Tile("sparkle", ManagerTheme.Gradient(), 64),
                    }, Dock.Left),
                    new StackPanel {
                        Spacing = 8,
                        Children = {
                            new TextBlock { Text = $"Welcome to {Edition.NAME}", FontSize = 28, FontWeight = FontWeight.Bold },
                            new TextBlock {
                                Text = "Your own Wizard101 server, all in one place. Pick a page on the left, or search for anything below.",
                                Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), TextWrapping = TextWrapping.Wrap,
                            },
                            _homeStatus,
                            new WrapPanel {
                                Margin = new Thickness(0, 6, 0, 0),
                                Children = {
                                    Spaced(_homePlay), Spaced(_homeStart), Spaced(_homeStop),
                                    Spaced(Action(Plain("What's new"), () => { SelectTab(WHATS_NEW); return Task.CompletedTask; })),
                                },
                            },
                        },
                    },
                },
            },
        };

        var cards = new List<Control> {
            hero,
            Fold("search", HOME, "Search everything",
                "Every page, setting, command, item, spell, creature, zone and quest at once.",
                _homeSearch,
                SearchFilters(SearchFilter("Look in", _homeLookIn), SearchFilter("How many of each", _homeHowMany)),
                _homeResults),
        };
        foreach (var group in s_groups) {
            var tiles = new WrapPanel();
            foreach (var name in group.Tabs) {
                tiles.Children.Add(PageTile(name));
            }

            cards.Add(Fold(group.Icon, group.Name, group.Name, group.About, tiles));
        }

        return Page([.. cards]);
    }

    /// <summary>A tab as a button on Home: its icon, name and what it is for. It opens the tab.</summary>
    private Button PageTile(string name) {
        var (icon, line) = s_tabs[name];
        var tile = new Button {
            Classes = { "ui" }, Width = 288, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new DockPanel {
                Children = {
                    WithDock(new Border { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = ManagerTheme.Tile(icon, ManagerTheme.Accent(name), 36) }, Dock.Left),
                    new StackPanel {
                        Spacing = 2,
                        Children = {
                            new TextBlock { Text = name, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = line, FontSize = ManagerTheme.FONT_SMALL, FontWeight = FontWeight.Normal, Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            },
        };
        tile.Click += (_, _) => SelectTab(name);

        return tile;
    }

    private static TextBlock HomeSearchTip() => Sub("Type a name, an ID or a few words, such as Fire Cat, Malistaire, unlimited mana or 1317127. " +
                                                    "Ctrl+K jumps to this box from anywhere; Ctrl+F searches the tab you are on.");

    /// <summary>
    /// Home's results: matching tabs, settings and Lookup entries (a group for each kind), and a quick search of each tab
    /// that has one. Look in keeps it to one kind; an empty search lists them all.
    /// </summary>
    private async Task RenderHomeSearchAsync() {
        var version = ++_homeSearchVersion;
        var query = (_homeSearch.Text ?? "").Trim();
        var lookIn = _homeLookIn.SelectedItem as string ?? Lookup.EVERYTHING;
        var most = int.TryParse(_homeHowMany.SelectedItem as string, out var n) ? n : 6;
        bool Wanted(string kind) => lookIn == Lookup.EVERYTHING || lookIn == kind;

        // The ID lists load once, the first time anything is searched.
        if (query.Length > 0 || (lookIn != Lookup.EVERYTHING && lookIn != HOME_PAGES)) {
            await LoadLookupAsync();
        }

        if (version != _homeSearchVersion) {
            return;
        }

        var rows = new List<Control>();
        if (query.Length == 0) {
            rows.Add(HomeSearchTip());
        }

        // Tabs.
        if (Wanted(HOME_PAGES)) {
            var tabRows = new List<Control>();
            foreach (var group in s_groups) {
                foreach (var name in group.Tabs) {
                    var (icon, line) = s_tabs[name];
                    if (SearchText.Matches($"{name} {group.Name} {line}", query)) {
                        var tab = name;
                        tabRows.Add(ResultRow(icon, name, name, $"{group.Name} page. {line}", () => SelectTab(tab)));
                    }
                }
            }

            if (SearchText.Matches($"{WHATS_NEW} patch notes update changes version", query)) {
                tabRows.Add(ResultRow("sparkle", WHATS_NEW, WHATS_NEW, "Every version's changes, with a search.", () => SelectTab(WHATS_NEW)));
            }

            AddGroup(rows, HOME_PAGES, tabRows);
        }

        // Settings: with an empty search only when asked for by Look in, as there are hundreds.
        if (lookIn == HOME_SETTINGS || (lookIn == Lookup.EVERYTHING && query.Length > 0)) {
            try {
                if (_settings.Count == 0) {
                    LoadSettings();
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or PanelException) {
                // No settings file yet (the server was never built); the other results still show.
            }

            var settings = _settings.SelectMany(g => g.Settings)
                .Where(s => SearchText.Matches($"{s.Section} {s.Key} {SearchText.SplitCamel(s.Key)} {s.Help}", query))
                .ToList();
            AddGroup(rows, settings.Count > most ? $"Settings ({settings.Count:N0}, showing {most})" : "Settings", [.. settings.Take(most).Select(s => ResultRow("gear", "Settings",
                $"{s.Section}: {SearchText.SplitCamel(s.Key)}", s.Help, () => {
                    SelectTab("Settings");
                    _settingSearch.Text = s.Key;
                }))]);
        }

        // Commands, items, spells, creatures, zones and quests (the Lookup tab's lists), a group for each kind.
        if (_lookup is not null && (query.Length > 0 || Lookup.KINDS.Contains(lookIn)) && lookIn is not (HOME_PAGES or HOME_SETTINGS)) {
            foreach (var kind in Lookup.KINDS.Skip(1).Where(Wanted)) {
                var found = _lookup.Search(kind, query, most, out var total);
                var kindRows = found.Select(entry => ResultRow(KindIcon(entry.Kind), "Lookup", entry.Name,
                    string.Join(" · ", new[] { entry.Kind, entry.Id.Length > 0 ? $"ID {entry.Id}" : "", entry.Typed ?? "", entry.Detail }.Where(t => t.Length > 0)),
                    () => OpenInLookup(kind, query), entry.Typed ?? (entry.Id.Length > 0 ? entry.Id : null))).ToList();
                if (total > most) {
                    kindRows.Add(ResultRow("search", "Lookup", $"See all {total:N0} in Lookup", "Opens the Lookup tab with this search.",
                        () => OpenInLookup(kind, query)));
                }

                AddGroup(rows, total > most ? $"{KindPlural(kind)} ({total:N0})" : KindPlural(kind), kindRows);
            }
        }

        // Each tab's own search, with this query typed in.
        if (query.Length > 0 && lookIn == Lookup.EVERYTHING) {
            AddGroup(rows, "Search a tab", [.. SearchableTabs().Select(t => ResultRow(s_tabs[t.Tab].Icon, t.Tab, $"Search {t.Tab} for \"{query}\"",
                $"Opens the {t.Tab} tab with this search.", () => {
                    SelectTab(t.Tab);
                    t.Box.Text = query;
                }))]);
        }

        if (query.Length > 0 && rows.Count == 0) {
            rows.Add(Sub("Nothing matches. Try fewer words, or set Look in back to Everything."));
        }

        _homeResults.Children.Clear();
        _homeResults.Children.AddRange(rows);
    }

    /// <summary>Opens the Lookup tab on one kind with this search typed in.</summary>
    private void OpenInLookup(string kind, string query) {
        SelectTab("Lookup");
        _lookupKind.SelectedItem = kind;
        _lookupSearch.Text = query;
    }

    private static string KindPlural(string kind) => kind switch {
        "NPC" => "NPCs",
        "Command" => "Commands",
        _ => kind + "s",
    };

    private static void AddGroup(List<Control> rows, string title, List<Control> found) {
        if (found.Count == 0) {
            return;
        }

        rows.Add(new TextBlock {
            Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED,
            Margin = new Thickness(2, rows.Count == 0 ? 2 : 10, 0, 0),
        });
        rows.AddRange(found);
    }

    /// <summary>The tabs with a search box of their own, for Home's "Search a tab" rows.</summary>
    private IEnumerable<(string Tab, TextBox Box)> SearchableTabs() => [
        ("Spells", _spellsSearch), ("Gear", _gearSearch), ("Recipes", _recipeSearch), ("Vendors", _vendorSearch), ("Phrases", _phraseSearch),
        ("Quests", _questSearch), ("Bosses", _bossSearch), ("Commands", _commandSearch), ("Announcements", _announcementSearch),
        ("Lookup", _lookupSearch), ("Settings", _settingSearch), ("Customs", _customListSearch), ("Accounts", _accountSearch),
        ("Logs", _logSearch),
    ];

    private static string KindIcon(string kind) => kind switch {
        "Command" => "terminal",
        "Bundle" => "gift",
        "Item" => "bag",
        "Spell" => "cards",
        "Creature" => "skull",
        "NPC" => "user",
        "Zone" => "map",
        "Quest" => "flag",
        "Phrase" => "quote",
        _ => "search",
    };

    /// <summary>One result on Home: an icon in its tab's colour, a name and a line. Clicking it opens where it lives.</summary>
    private Control ResultRow(string icon, string accent, string title, string line, Action open, string? copy = null) {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (copy is not null) {
            buttons.Children.Add(Action(Plain("Copy"), () => CopyAsync(copy)));
        }

        var item = ManagerTheme.Item(new DockPanel {
            Children = {
                WithDock(WithMargin(buttons, new Thickness(10, 0, 0, 0)), Dock.Right),
                WithDock(new Border { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = ManagerTheme.Tile(icon, ManagerTheme.Accent(accent), 30) }, Dock.Left),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center, Spacing = 1,
                    Children = {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = line, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap },
                    },
                },
            },
        });
        item.Cursor = new Cursor(StandardCursorType.Hand);
        item.PointerEntered += (_, _) => item.Background = ManagerTheme.Brush(ManagerTheme.SURFACE_HOVER);
        item.PointerExited += (_, _) => item.Background = ManagerTheme.SURFACE_HIGH_BRUSH;
        item.PointerReleased += (_, e) => {
            // A click on the row, not on its Copy button.
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is Visual source && !IsInside<Button>(source, item)) {
                open();
            }
        };

        return item;
    }

    private static bool IsInside<T>(Visual source, Visual stop) where T : Visual {
        for (Visual? at = source; at is not null && at != stop; at = at.GetVisualParent()) {
            if (at is T) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Home's server line and buttons, after the header's badge.</summary>
    private void ShowHomeStatus(ServerStatus status) {
        _homeStatus.Text = status.Ready ? "● The server is running and ready for players."
            : status.Running ? "● The server is starting…"
            : "● The server is stopped. Play Wizard101 starts it for you.";
        _homeStatus.Foreground = status.Ready ? GOOD : status.Running ? WARN : MUTED;
        _homeStart.IsEnabled = !status.Running;
        _homeStop.IsEnabled = status.Running;
        _homePlay.IsEnabled = status.ClientFound;
    }

    // ---------------------------------------------------------------------
    // Search boxes
    // ---------------------------------------------------------------------

    private static bool s_searchBoxesHooked;

    // Before any box is made: the window's own search boxes are made with it.
    static ManagerWindow() => HookSearchBoxes();

    /// <summary>
    /// Gives every search box in the app (any box whose hint starts with "Search") a magnifying glass, a clear button
    /// and Esc to clear it. It hooks the hint itself, so boxes made later (inside the block editor) get them too.
    /// </summary>
    private static void HookSearchBoxes() {
        if (s_searchBoxesHooked) {
            return;
        }

        s_searchBoxesHooked = true;
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((box, _) => DecorateSearchBox(box));
    }

    private static bool IsSearchBox(TextBox box) => box.Watermark?.StartsWith("Search", StringComparison.OrdinalIgnoreCase) == true;

    private static void DecorateSearchBox(TextBox box) {
        if (!IsSearchBox(box) || box.InnerLeftContent is not null) {
            return;
        }

        box.InnerLeftContent = new Border {
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Child = ManagerTheme.Icon("search", 15, MUTED),
        };
        var clear = new Button {
            Classes = { "clear" }, Content = ManagerTheme.Icon("close", 12, MUTED), Focusable = false,
            IsVisible = !string.IsNullOrEmpty(box.Text),
        };
        ToolTip.SetTip(clear, "Clear the search (Esc)");
        clear.Click += (_, _) => {
            box.Text = "";
            box.Focus();
        };
        box.InnerRightContent = clear;
        box.PropertyChanged += (_, e) => {
            if (e.Property == TextBox.TextProperty) {
                clear.IsVisible = !string.IsNullOrEmpty(box.Text);
            }
        };
        box.KeyDown += (_, e) => {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(box.Text)) {
                box.Text = "";
                e.Handled = true;
            }
        };
    }

    /// <summary>Ctrl+K: Home's search. Ctrl+F: the search box of the tab showing.</summary>
    private void OnShortcutKey(object? sender, KeyEventArgs e) {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            return;
        }

        if (e.Key == Key.K) {
            SelectTab(HOME);
            FocusLater(_homeSearch);
            e.Handled = true;
        }
        else if (e.Key == Key.F) {
            if (ShownSearchBox() is { } box) {
                FocusLater(box);
                e.Handled = true;
            }
        }
    }

    private TextBox? ShownSearchBox() {
        if (_nav?.SelectedItem is not TabItem top || top.Tag is not string name) {
            return null;
        }

        if (name == HOME) {
            return _homeSearch;
        }

        if (name == WHATS_NEW) {
            return _notesSearch;
        }

        var content = _groupTabs.TryGetValue(name, out var inner) ? (inner.SelectedItem as TabItem)?.Content as ILogical : null;

        return content?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => IsSearchBox(t) && t.IsEffectivelyVisible);
    }

    private static void FocusLater(TextBox box) => Dispatcher.UIThread.Post(() => {
        box.Focus();
        box.SelectAll();
    }, DispatcherPriority.Background);

}
