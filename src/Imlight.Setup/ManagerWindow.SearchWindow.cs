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
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The search window (Niduus, 2026-11-09, mockup "Search 2": "a general search bar at the top of the UI so players can
/// easily search for what they are looking for"). The search box at the top of every page (or Ctrl+K) opens it over the
/// page: what to look in on the left with how many match, the results in the middle, and on the right a preview of the
/// one picked with buttons that do the obvious thing. A setting can be changed right in the preview.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string SW_EVERYTHING = "Everything";
    private const string SW_PAGES = "Pages";
    private const string SW_SETTINGS = "Settings";
    private const string SW_NOTES = "Patch notes";
    private const int SW_EACH_IN_EVERYTHING = 5;
    private const int SW_MOST = 60;

    /// <summary>One result: what kind it is, its icon and colour, its name and line, what opening it does, and its preview.</summary>
    private sealed record SearchHit(string Kind, string Icon, string Accent, string Title, string Line, Action Open, Func<Control> Preview);

    private readonly Panel _swOverlay = new() { IsVisible = false };
    private readonly TextBox _swBox = new() { Watermark = "Search everything: pages, settings, commands, NPCs, quests, spells, places…", FontSize = 18 };
    private readonly TextBlock _swCount = new() { Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
    private readonly StackPanel _swKinds = new() { Spacing = 2 };
    private readonly StackPanel _swResults = new() { Spacing = 4 };
    private readonly ScrollViewer _swResultsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _swPreview = new() { Padding = new Thickness(22, 18) };
    private readonly DispatcherTimer _swDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string _swKind = SW_EVERYTHING;
    private int _swVersion;
    private List<SearchHit> _swHits = [];
    private readonly Dictionary<string, int> _swTotals = [];
    private int _swSelected = -1;
    private readonly List<Border> _swRows = [];

    /// <summary>The kinds down the left, in order: the Lookup tab's kinds by what players call them.</summary>
    private static readonly (string Kind, string? LookupKind, string Icon)[] s_swKinds = [
        (SW_EVERYTHING, null, "search"),
        (SW_PAGES, null, "home"),
        (SW_SETTINGS, null, "gear"),
        ("Commands", "Command", "terminal"),
        ("Creatures", "Creature", "skull"),
        ("NPCs", "NPC", "user"),
        ("Quests", "Quest", "flag"),
        ("Spells", "Spell", "cards"),
        ("Items", "Item", "bag"),
        ("Places", Lookup.TELEPORT_AREA, "map"),
        ("Bundles", "Bundle", "gift"),
        ("Phrases", "Phrase", "quote"),
        (SW_NOTES, null, "sparkle"),
    ];

    /// <summary>The search window over the whole app; it shows when opened.</summary>
    private Control SearchWindow() {
        _swDelay.Tick += async (_, _) => {
            _swDelay.Stop();
            await Guard(RenderSearchWindowAsync);
        };
        _swBox.TextChanged += (_, _) => {
            _swDelay.Stop();
            _swDelay.Start();
        };
        _swBox.MinHeight = 46;
        _swBox.KeyDown += SearchWindowKey;
        _swResultsScroll.Content = _swResults;

        var close = Plain("✕");
        close.Click += (_, _) => CloseSearchWindow();
        ToolTip.SetTip(close, "Close the search (Esc)");

        var dim = new Border { Background = ManagerTheme.Brush(Color.FromRgb(6, 6, 12), 165) };
        dim.PointerPressed += (_, _) => CloseSearchWindow();

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("210,2*,3*") };
        var kinds = new Border {
            BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10),
            Child = new ScrollViewer { Content = _swKinds },
        };
        var results = new Border { BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 10, 6, 10), Child = _swResultsScroll };
        Grid.SetColumn(results, 1);
        var preview = new ScrollViewer { Content = _swPreview };
        Grid.SetColumn(preview, 2);
        columns.Children.Add(kinds);
        columns.Children.Add(results);
        columns.Children.Add(preview);

        var keys = Sub("↑ ↓ pick   ·   Enter open   ·   Tab next kind   ·   Esc close");
        keys.FontSize = ManagerTheme.FONT_SMALL;
        var panel = new Border {
            Background = ManagerTheme.BACKGROUND_BRUSH, BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 120), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Margin = new Thickness(60, 40), MaxWidth = 1240, MaxHeight = 820,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 60, OffsetY = 20, Color = Color.FromArgb(150, 0, 0, 0) }),
            ClipToBounds = true,
            Child = new DockPanel {
                Children = {
                    WithDock(new Border {
                        BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 14),
                        Child = new DockPanel {
                            Children = { WithDock(close, Dock.Right), WithDock(_swCount, Dock.Right), _swBox },
                        },
                    }, Dock.Top),
                    WithDock(new Border { BorderBrush = LINE, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 8), Child = keys }, Dock.Bottom),
                    columns,
                },
            },
        };
        _swOverlay.Children.Add(dim);
        _swOverlay.Children.Add(panel);

        return _swOverlay;
    }

    /// <summary>Opens the search window with this typed in (Ctrl+K, or the search box at the top).</summary>
    private void OpenSearchWindow(string text = "") {
        _swOverlay.IsVisible = true;
        if (text.Length > 0 || !string.IsNullOrEmpty(_swBox.Text)) {
            _swBox.Text = text;
        }

        _ = Guard(RenderSearchWindowAsync);
        Dispatcher.UIThread.Post(() => {
            _swBox.Focus();
            _swBox.CaretIndex = _swBox.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void CloseSearchWindow() => _swOverlay.IsVisible = false;

    private void SearchWindowKey(object? sender, KeyEventArgs e) {
        switch (e.Key) {
            case Key.Escape:
                if (!string.IsNullOrEmpty(_swBox.Text)) {
                    _swBox.Text = "";
                }
                else {
                    CloseSearchWindow();
                }

                e.Handled = true;
                break;
            case Key.Down:
                PickHit(Math.Min(_swSelected + 1, _swHits.Count - 1));
                e.Handled = true;
                break;
            case Key.Up:
                PickHit(Math.Max(_swSelected - 1, 0));
                e.Handled = true;
                break;
            case Key.Enter:
                if (_swSelected >= 0 && _swSelected < _swHits.Count) {
                    OpenHit(_swHits[_swSelected]);
                }

                e.Handled = true;
                break;
            case Key.Tab:
                var shown = s_swKinds.Where(k => k.Kind == SW_EVERYTHING || _swTotals.GetValueOrDefault(k.Kind) > 0).Select(k => k.Kind).ToList();
                var at = shown.IndexOf(_swKind);
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                _swKind = shown[((at < 0 ? 0 : at) + step + shown.Count) % shown.Count];
                _ = Guard(RenderSearchWindowAsync);
                e.Handled = true;
                break;
        }
    }

    private void OpenHit(SearchHit hit) {
        CloseSearchWindow();
        hit.Open();
    }

    /// <summary>Works out every kind's matches, then draws the kinds, the results and the first one's preview.</summary>
    private async Task RenderSearchWindowAsync() {
        var version = ++_swVersion;
        var query = (_swBox.Text ?? "").Trim();
        if (query.Length > 0) {
            await LoadLookupAsync();
        }

        if (version != _swVersion) {
            return;
        }

        var byKind = new Dictionary<string, List<SearchHit>>();
        _swTotals.Clear();
        void Put(string kind, List<SearchHit> hits, int? total = null) {
            byKind[kind] = hits;
            _swTotals[kind] = total ?? hits.Count;
        }

        Put(SW_PAGES, PageHits(query));
        if (query.Length > 0) {
            Put(SW_SETTINGS, SettingHits(query));
            if (_lookup is not null) {
                foreach (var (kind, lookupKind, icon) in s_swKinds.Where(k => k.LookupKind is not null)) {
                    var found = _lookup.Search(lookupKind!, query, SW_MOST, out var total);
                    Put(kind, [.. found.Select(entry => LookupHit(kind, icon, entry, query))], total);
                }
            }

            Put(SW_NOTES, NoteHits(query));
        }

        _swTotals[SW_EVERYTHING] = _swTotals.Where(t => t.Key != SW_EVERYTHING).Sum(t => t.Value);
        if (_swKind != SW_EVERYTHING && _swTotals.GetValueOrDefault(_swKind) == 0 && query.Length > 0) {
            _swKind = SW_EVERYTHING;
        }

        // The kinds down the left, with their counts; kinds with nothing are dimmed.
        _swKinds.Children.Clear();
        foreach (var (kind, _, icon) in s_swKinds) {
            var count = _swTotals.GetValueOrDefault(kind);
            var lit = kind == _swKind;
            var pick = kind;
            var button = new Button {
                Classes = { "ui" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), BorderThickness = new Thickness(1),
                Background = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT, 70) : Brushes.Transparent,
                BorderBrush = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 150) : Brushes.Transparent,
                Opacity = count == 0 && kind != SW_EVERYTHING ? 0.5 : 1,
                Content = new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = count.ToString("N0"), Foreground = lit ? ManagerTheme.TEXT_BRUSH : MUTED, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, Dock.Right),
                        WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = ManagerTheme.Icon(icon, 14, lit ? ManagerTheme.TEXT_BRUSH : MUTED) }, Dock.Left),
                        new TextBlock { Text = kind, VerticalAlignment = VerticalAlignment.Center, FontWeight = lit ? FontWeight.SemiBold : FontWeight.Normal },
                    },
                },
            };
            button.Click += (_, _) => {
                _swKind = pick;
                _ = Guard(RenderSearchWindowAsync);
                _swBox.Focus();
            };
            _swKinds.Children.Add(button);
        }

        // The results: a few of each kind under Everything, else all of the kind picked.
        _swHits = [];
        _swRows.Clear();
        _swResults.Children.Clear();
        if (query.Length == 0) {
            _swResults.Children.Add(WithMargin(Sub("Type a few letters: a page, a setting, a command, a creature, an NPC, a quest, a spell, an item or a place. " +
                                                   "Every page is listed below to start with."), new Thickness(4, 2, 4, 8)));
        }

        foreach (var (kind, _, _) in s_swKinds.Where(k => k.Kind != SW_EVERYTHING)) {
            if ((_swKind != SW_EVERYTHING && kind != _swKind) || !byKind.TryGetValue(kind, out var hits) || hits.Count == 0) {
                continue;
            }

            var shown = _swKind == SW_EVERYTHING && query.Length > 0 ? hits.Take(SW_EACH_IN_EVERYTHING).ToList() : hits;
            var total = _swTotals.GetValueOrDefault(kind);
            _swResults.Children.Add(new TextBlock {
                Text = (total > shown.Count ? $"{kind}  ·  {shown.Count} of {total:N0}" : kind).ToUpperInvariant(),
                FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(4, _swResults.Children.Count == 0 ? 2 : 12, 0, 2),
            });
            foreach (var hit in shown) {
                var index = _swHits.Count;
                _swHits.Add(hit);
                var row = HitRow(hit, index);
                _swRows.Add(row);
                _swResults.Children.Add(row);
            }

            if (_swKind == SW_EVERYTHING && total > shown.Count) {
                var pick = kind;
                var more = new Button {
                    Classes = { "ui" }, Content = $"See all {total:N0} {kind.ToLowerInvariant()}", Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0), Foreground = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT), Padding = new Thickness(4, 2),
                };
                more.Click += (_, _) => {
                    _swKind = pick;
                    _ = Guard(RenderSearchWindowAsync);
                };
                _swResults.Children.Add(more);
            }
        }

        if (query.Length > 0 && _swHits.Count == 0) {
            _swResults.Children.Add(WithMargin(Sub("Nothing matches. Try fewer words or another spelling."), new Thickness(4)));
        }

        var all = _swTotals[SW_EVERYTHING];
        _swCount.Text = query.Length == 0 ? "" : $"{all:N0} result{(all == 1 ? "" : "s")}";
        _swResultsScroll.Offset = default;
        PickHit(_swHits.Count > 0 ? 0 : -1);
    }

    /// <summary>A result row: its icon tile, its name and line. Clicking picks it (its preview shows); a double click opens it.</summary>
    private Border HitRow(SearchHit hit, int index) {
        var row = new Border {
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(10, 7), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel {
                Children = {
                    WithDock(new Border { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = ManagerTheme.Tile(hit.Icon, ManagerTheme.Accent(hit.Accent), 30) }, Dock.Left),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center, Spacing = 1,
                        Children = {
                            new TextBlock { Text = hit.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = hit.Line, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                },
            },
        };
        row.PointerPressed += (_, e) => {
            if (e.ClickCount >= 2) {
                OpenHit(hit);
            }
            else {
                PickHit(index);
            }

            e.Handled = true;
        };

        return row;
    }

    /// <summary>Lights the result at that place, scrolls it into view and shows its preview.</summary>
    private void PickHit(int index) {
        if (_swSelected >= 0 && _swSelected < _swRows.Count) {
            _swRows[_swSelected].Background = Brushes.Transparent;
            _swRows[_swSelected].BorderBrush = Brushes.Transparent;
        }

        _swSelected = index;
        if (index < 0 || index >= _swHits.Count) {
            _swPreview.Child = Sub(string.IsNullOrWhiteSpace(_swBox.Text) ? "" : "Pick a result to see it here.");

            return;
        }

        var row = _swRows[index];
        row.Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 55);
        row.BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 120);
        row.BringIntoView();
        try {
            _swPreview.Child = _swHits[index].Preview();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException) {
            _swPreview.Child = Sub("This one has no preview.");
        }
    }

    /// <summary>The preview's shape: what kind it is, its name, what it is, extra rows, then its buttons.</summary>
    private static Control PreviewOf(string kind, string title, string about, IEnumerable<Control> extra, params Button[] buttons) {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = kind.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT) });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        if (about.Length > 0) {
            stack.Children.Add(new TextBlock { Text = about, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        }

        stack.Children.AddRange(extra);
        var row = new WrapPanel();
        foreach (var button in buttons) {
            row.Children.Add(Spaced(button));
        }

        stack.Children.Add(row);

        return stack;
    }

    private static Control PreviewLine(string label, string value) => new DockPanel {
        Children = {
            WithDock(new TextBlock { Text = label, Width = 110, Foreground = MUTED }, Dock.Left),
            new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
        },
    };

    // ---------------------------------------------------------------------
    // What each kind finds
    // ---------------------------------------------------------------------

    /// <summary>Every tab (and Home) whose name, page or line matches; with nothing typed, pinned and recent first, then all.</summary>
    private List<SearchHit> PageHits(string query) {
        var hits = new List<SearchHit>();
        if (SearchText.Matches($"{HOME} start welcome server play", query)) {
            hits.Add(new SearchHit(SW_PAGES, "home", HOME, HOME, "Start here: run the server, who is online, and what's new.", () => SelectTab(HOME),
                () => PreviewOf("Page", HOME, "Start here: run the server, who is online, and what's new.", [], OpenButton(HOME))));
        }

        IEnumerable<string> tabs = s_groups.SelectMany(g => g.Tabs);
        if (query.Length == 0) {
            tabs = _pinned.Concat(_recent).Concat(tabs).Distinct();
        }

        foreach (var tab in tabs.Where(s_tabs.ContainsKey)) {
            var group = GroupOf(tab) ?? "";
            var (icon, line) = s_tabs[tab];
            if (!SearchText.Matches($"{tab} {group} {line}", query)) {
                continue;
            }

            var name = tab;
            hits.Add(new SearchHit(SW_PAGES, icon, tab, tab, $"{group}  ›  {tab}", () => SelectTab(name), () => {
                var pin = Plain(_pinned.Contains(name) ? "Unpin" : "Pin to the side bar");
                pin.Click += (_, _) => {
                    TogglePin(name);
                    pin.Content = _pinned.Contains(name) ? "Unpin" : "Pin to the side bar";
                };

                return PreviewOf("Page", name, line, [PreviewLine("Where", $"{group}  ›  {name}")], OpenButton(name), pin);
            }));
        }

        return hits;
    }

    private Button OpenButton(string tab) {
        var open = Primary("Open this page");
        open.Click += (_, _) => {
            CloseSearchWindow();
            SelectTab(tab);
        };

        return open;
    }

    /// <summary>Every setting of the server's Imlight.ini that matches, with its editor in the preview.</summary>
    private List<SearchHit> SettingHits(string query) {
        try {
            if (_settings.Count == 0) {
                LoadSettings();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or PanelException) {
            // No settings file yet (the server was never built); the other kinds still search.
            return [];
        }

        return [.. _settings.SelectMany(g => g.Settings)
            .Where(s => SearchText.Matches($"{s.Section} {s.Key} {SearchText.SplitCamel(s.Key)} {s.Help}", query))
            .Take(SW_MOST)
            .Select(s => new SearchHit(SW_SETTINGS, "gear", "Settings", SearchText.SplitCamel(s.Key), $"Settings  ›  {s.Section}", () => {
                SelectTab("Settings");
                _settingSearch.Text = s.Key;
            }, () => {
                var save = Primary("Save changes");
                save.Click += async (_, _) => await Guard(SaveSettingsAsync);
                var open = Plain("Open on the Settings tab");
                open.Click += (_, _) => {
                    CloseSearchWindow();
                    SelectTab("Settings");
                    _settingSearch.Text = s.Key;
                };
                var setting = _settings.SelectMany(g => g.Settings).FirstOrDefault(x => x.Section == s.Section && x.Key == s.Key) ?? s;

                return PreviewOf("Setting", SearchText.SplitCamel(s.Key), "", [
                    PreviewLine("Where", $"Settings  ›  {s.Section}"),
                    Card(SettingRow(setting)),
                    Sub("Change it here, then press Save changes. It is saved to the server's Imlight.ini."),
                ], save, open);
            }))];
    }

    /// <summary>A command, creature, NPC, quest, spell, item, place, bundle or phrase from the Lookup lists.</summary>
    private SearchHit LookupHit(string kind, string icon, LookupEntry entry, string query) {
        var line = string.Join("  ·  ", new[] { entry.Typed ?? "", entry.Id.Length > 0 ? $"ID {entry.Id}" : "", entry.Detail }.Where(t => t.Length > 0));
        void Open() {
            if (entry.Kind is "Creature" or "Spell" or "Item" && entry.Id.Length > 0) {
                OpenInWiki(entry.Kind, entry.Id);
            }
            else {
                OpenInLookup(entry.Kind, entry.Name);
            }
        }

        return new SearchHit(kind, icon, "Lookup", entry.Name, line, Open, () => {
            var buttons = new List<Button>();
            if (entry.Kind == Lookup.TELEPORT_AREA && entry.Typed is { Length: > 0 } tp) {
                buttons.Add(Action(Primary("Teleport my wizard here"), () => {
                    _manager.SendCommand(tp);
                    Say($"Sent {tp}. Your wizard goes there in a few seconds.");

                    return Task.CompletedTask;
                }));
            }

            var open = entry.Kind is "Creature" or "Spell" or "Item" && entry.Id.Length > 0 ? "Open its Wiki page" : "Open in Lookup";
            buttons.Add(buttons.Count == 0 ? Primary(open) : Plain(open));
            buttons[^1].Click += (_, _) => {
                CloseSearchWindow();
                Open();
            };
            if (entry.Typed is { Length: > 0 } typed) {
                buttons.Add(Action(Plain(entry.Kind == "Command" ? "Copy the command" : "Copy"), () => CopyAsync(typed)));
            }
            else if (entry.Id.Length > 0) {
                buttons.Add(Action(Plain("Copy the ID"), () => CopyAsync(entry.Id)));
            }

            var extra = new List<Control>();
            if (entry.Typed is { Length: > 0 } command) {
                extra.Add(PreviewLine(entry.Kind == "Command" ? "Type in chat" : "Command", command));
            }

            if (entry.Id.Length > 0) {
                extra.Add(PreviewLine("ID", entry.Id));
            }

            if (entry.Detail.Length > 0) {
                extra.Add(PreviewLine("Details", entry.Detail));
            }

            return PreviewOf(kind.TrimEnd('s'), entry.Name, entry.Help, extra, [.. buttons]);
        });
    }

    /// <summary>The patch notes that match, newest first.</summary>
    private List<SearchHit> NoteHits(string query) {
        if (_notes.Count == 0) {
            _notes = Merge(BuiltInNotes(), ReadNotesCache(System.IO.Path.Combine(_manager.InstallFolder, NOTES_CACHE)));
        }

        return [.. _notes.SelectMany(r => r.Changes.Select(c => (r.Version, Note: PatchNote.Parse(r.Version, c))))
            .Where(n => SearchText.Matches($"{n.Note.Title} {n.Note.Detail} {n.Note.Where}", query))
            .Take(SW_MOST)
            .Select(n => new SearchHit(SW_NOTES, "sparkle", WHATS_NEW, n.Note.Title, $"{n.Note.Kind}  ·  version {n.Version}", () => OpenNotesAt(n.Version), () => {
                var open = Primary("Open in What's New");
                open.Click += (_, _) => {
                    CloseSearchWindow();
                    OpenNotesAt(n.Version);
                };
                var extra = new List<Control> { PreviewLine("Version", n.Version.ToString()), PreviewLine("Kind", n.Note.Kind) };
                if (n.Note.Where.Length > 0) {
                    extra.Add(PreviewLine("Where", n.Note.Where));
                }

                if (n.Note.Test.Length > 0) {
                    extra.Add(PreviewLine("How to test", n.Note.Test));
                }

                return PreviewOf("Patch note", n.Note.Title, n.Note.Detail, extra, open);
            }))];
    }

}
