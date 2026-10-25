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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Phrases tab (Niduus, 2026-10-22: "make Phrases their own tab and list ALL of them as compact and optimized as
/// possible"): every line of the game's text (phrases.csv of the ID lookup folder, about 125,000), one short row each,
/// in a list that only draws the rows on screen. Search by words, number or key, narrow it to one group of the game's
/// text, and click a row to copy its .ca command.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ALL_PHRASES = "All groups";

    private sealed record PhraseRow(int Number, string Text, string Key, string Group, string Haystack);

    private List<PhraseRow>? _phrases;
    private readonly TextBox _phraseSearch = new() { Watermark = "Search by words, number or key, such as cannot be stopped, 588 or Malistaire" };
    private readonly ComboBox _phraseGroup = new() { MinWidth = 240 };
    private readonly TextBlock _phraseCount = Sub("");
    private readonly ListBox _phraseList = new() { Background = Brushes.Transparent };
    private readonly DispatcherTimer _phraseDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // Niduus, 2026-10-25: every detail of a line to filter by, like the Gear tab's filters.
    private static readonly string[] s_phraseLengths = [ANY_FILTER, "Short (up to 40 letters)", "Medium (41 to 120)", "Long (over 120)"];
    private static readonly string[] s_phraseSorts = ["Number", "Number (high first)", "Words (A to Z)", "Shortest first", "Longest first", "Key"];
    private readonly NumericUpDown _phraseFrom = FilterNumber(100, 130);
    private readonly NumericUpDown _phraseTo = FilterNumber(100, 130);
    private readonly ComboBox _phraseLength = FilterCombo(s_phraseLengths, 190);
    private readonly ComboBox _phraseSort = FilterCombo(s_phraseSorts, 170);
    private readonly TextBox _phraseKey = new() { Watermark = "Such as Malistaire", Width = 180 };
    private readonly CheckBox _phraseExact = new() { Content = "Words in this exact order" };
    private readonly CheckBox _phraseFilled = new() { Content = "Has words the game fills in (such as %s)" };
    private readonly CheckBox _phraseLines = new() { Content = "More than one line" };

    private Control PhrasesTab() {
        _phraseDelay.Tick += (_, _) => {
            _phraseDelay.Stop();
            RenderPhrases();
        };
        _phraseSearch.TextChanged += (_, _) => {
            _phraseDelay.Stop();
            _phraseDelay.Start();
        };
        _phraseGroup.SelectionChanged += (_, e) => {
            if (e.Source == _phraseGroup) {
                RenderPhrases();
            }
        };
        WhenFiltersChange(RenderPhrases, _phraseFrom, _phraseTo, _phraseLength, _phraseSort, _phraseKey, _phraseExact, _phraseFilled, _phraseLines);
        // Short rows: a ListBoxItem's own padding and height would make each row twice as tall.
        _phraseList.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) {
            Setters = {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)),
                new Setter(ListBoxItem.MinHeightProperty, 0.0),
            },
        });
        _phraseList.ItemTemplate = new FuncDataTemplate<PhraseRow>((row, _) => row is null ? new Panel() : PhraseRowView(row));
        _phraseList.SelectionChanged += (_, e) => {
            if (e.Source == _phraseList && _phraseList.SelectedItem is PhraseRow row) {
                _ = Guard(() => CopyAsync($".ca {row.Number}"));
                // Unpicked again, so a second click on the same line copies it again.
                Dispatcher.UIThread.Post(() => _phraseList.SelectedItem = null);
            }
        };
        _phraseCount.Text = "Reading the game's lines…";
        _ = LoadPhrasesAsync();

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(WithMargin(MissingNote("it doesn't write the list of the game's lines this page shows.", () => !_manager.HasIdLookup),
                    new Thickness(0, 0, 0, 10)), Dock.Top),
                WithDock(Fold("quote", "Phrases", "Every game line",
                    "Click a line to copy its .ca command. Boss cheats and the show a banner block take its words or number too.",
                    WithHint(new DockPanel {
                        Children = { WithDock(WithMargin(_phraseGroup, new Thickness(8, 0, 0, 0)), Dock.Right), _phraseSearch },
                    }, "Every line of the game's text, by number. Type words from a line, its number or its key (such as " +
                       "CombatMessages) to find it, or pick a group on the right. Battle lines are in the CombatMessages group. " +
                       "In battle, .ca and its number shows the line over you like a boss's cheat line. The filters narrow it by number, " +
                       "key, length, and lines with words the game fills in (a name or number)."),
                    SearchFilters(
                        SearchFilter("Number from", _phraseFrom),
                        SearchFilter("Number to", _phraseTo),
                        SearchFilter("Key has", _phraseKey),
                        SearchFilter("Length", _phraseLength),
                        SearchFilter("Sort by", _phraseSort),
                        SearchTick(_phraseExact),
                        SearchTick(_phraseFilled),
                        SearchTick(_phraseLines)),
                    _phraseCount), Dock.Top),
                WithMargin(Card(_phraseList), new Thickness(0, 14, 0, 0)),
            },
        };
    }

    private static Control PhraseRowView(PhraseRow row) {
        var text = new TextBlock { Text = row.Text.Replace('\n', ' '), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(text, row.Text);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto") };
        grid.Children.Add(new TextBlock {
            Text = row.Number.ToString(CultureInfo.InvariantCulture), FontFamily = MONO, FontSize = 12, Foreground = MUTED,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var key = new TextBlock { Text = row.Key, FontSize = 11, Foreground = MUTED, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        return grid;
    }

    private async Task LoadPhrasesAsync() {
        var file = Path.Combine(_manager.IdLookupFolder, "phrases.csv");
        try {
            _phrases = await Task.Run(() => {
                var rows = new List<PhraseRow>();
                if (!File.Exists(file)) {
                    return rows;
                }

                foreach (var f in Lookup.ReadCsv(file)) {
                    if (f.Count >= 3 && int.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
                        var key = f[2];
                        var cut = key.IndexOf('_');
                        rows.Add(new PhraseRow(number, f[1], key, cut > 0 ? key[..cut] : key, SearchText.Normalize(f[1] + " " + key)));
                    }
                }

                return rows;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _phrases = [];
            _phraseCount.Text = $"The game's lines could not be read: {ex.Message}";

            return;
        }

        var groups = _phrases.GroupBy(p => p.Group).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({g.Count():N0})");
        _phraseGroup.ItemsSource = new[] { ALL_PHRASES }.Concat(groups).ToList();
        _phraseGroup.SelectedIndex = 0;
        RenderPhrases();
    }

    private void RenderPhrases() {
        if (_phrases is not { } phrases) {
            return;
        }

        if (phrases.Count == 0) {
            _phraseList.ItemsSource = null;
            _phraseCount.Text = "No game lines yet. Start the server once so it writes them.";

            return;
        }

        var query = (_phraseSearch.Text ?? "").Trim();
        var words = SearchText.Words(query);
        var number = int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?) null;
        var picked = _phraseGroup.SelectedItem as string ?? ALL_PHRASES;
        var group = picked == ALL_PHRASES ? null : picked[..picked.LastIndexOf(" (", StringComparison.Ordinal)];
        var inOrder = _phraseExact.IsChecked == true && words.Length > 1 ? string.Join(' ', words) : null;
        var key = SearchText.Words(_phraseKey.Text);
        var length = _phraseLength.SelectedIndex;
        var filled = _phraseFilled.IsChecked == true;
        var lines = _phraseLines.IsChecked == true;
        IEnumerable<PhraseRow> matched = phrases.Where(p => (group is null || p.Group == group)
                                                            && (p.Number == number || (inOrder is null ? words.All(p.Haystack.Contains)
                                                                : string.Join(' ', SearchText.Words(p.Text)).Contains(inOrder, StringComparison.Ordinal)))
                                                            && InFilterRange(p.Number, _phraseFrom, _phraseTo)
                                                            && (key.Length == 0 || key.All(SearchText.Normalize(p.Key).Contains))
                                                            && length switch {
                                                                1 => p.Text.Length <= 40,
                                                                2 => p.Text.Length is > 40 and <= 120,
                                                                3 => p.Text.Length > 120,
                                                                _ => true,
                                                            }
                                                            && (!filled || p.Text.Contains('%') || p.Text.Contains('{') || p.Text.Contains('<'))
                                                            && (!lines || p.Text.Contains('\n')));
        matched = _phraseSort.SelectedIndex switch {
            1 => matched.OrderByDescending(p => p.Number),
            2 => matched.OrderBy(p => p.Text, StringComparer.OrdinalIgnoreCase),
            3 => matched.OrderBy(p => p.Text.Length),
            4 => matched.OrderByDescending(p => p.Text.Length),
            5 => matched.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase),
            _ => matched,
        };
        List<PhraseRow> shown = [.. matched];
        // The line of that very number first.
        if (number is not null && shown.FindIndex(p => p.Number == number) is > 0 and var at) {
            var exact = shown[at];
            shown.RemoveAt(at);
            shown.Insert(0, exact);
        }

        _phraseList.ItemsSource = shown;
        _phraseCount.Text = shown.Count == phrases.Count
            ? $"{phrases.Count:N0} lines. Click one to copy its .ca command."
            : $"{shown.Count:N0} of {phrases.Count:N0} lines. Click one to copy its .ca command.";
    }

}
