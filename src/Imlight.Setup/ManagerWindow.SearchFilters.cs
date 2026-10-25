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
using Avalonia;
using Avalonia.Controls;

namespace Imlight.Setup;

/// <summary>
/// The filters under a search box, made the same way on every tab (Niduus, 2026-10-25: "have all searches be in detail
/// like the gear search and have them all generate search results if left empty as seen in gear and spells. Cover every
/// single detail possible to search for"): a small label over each drop-down, number box or tick, laid out in a row that
/// wraps, each one showing the list again as soon as it changes.
/// </summary>
internal sealed partial class ManagerWindow {

    /// <summary>The first choice of a filter's drop-down, which lets everything through.</summary>
    private const string ANY_FILTER = "Any";

    /// <summary>A filter with its label over it.</summary>
    private static Control SearchFilter(string label, Control box) => new StackPanel {
        Spacing = 2, Margin = new Thickness(0, 0, 10, 6),
        Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12 }, box },
    };

    /// <summary>A tick in a row of filters, lined up with the boxes beside it.</summary>
    private static Control SearchTick(CheckBox box) => WithMargin(box, new Thickness(4, 18, 10, 6));

    /// <summary>Filters in a row that wraps.</summary>
    private static WrapPanel SearchFilters(params Control[] filters) {
        var panel = new WrapPanel();
        panel.Children.AddRange(filters);

        return panel;
    }

    /// <summary>A drop-down of choices, the first picked.</summary>
    private static ComboBox FilterCombo(IEnumerable<string> choices, double minWidth = 140)
        => new() { ItemsSource = choices.ToList(), SelectedIndex = 0, MinWidth = minWidth };

    /// <summary>A drop-down's choices: Any (or the given first choice), then the values in the data, in their given order.</summary>
    private static void FillFilterCombo(ComboBox box, IEnumerable<string> values, string first = ANY_FILTER) {
        var picked = box.SelectedItem as string;
        var list = new[] { first }.Concat(values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
        if (box.ItemsSource is IEnumerable<string> had && had.SequenceEqual(list)) {
            return;
        }

        box.ItemsSource = list;
        box.SelectedItem = picked is not null && list.Contains(picked) ? picked : first;
    }

    /// <summary>A number box for a "from" or "to" filter, empty (any) at first.</summary>
    private static NumericUpDown FilterNumber(decimal step = 1, double width = 120) => new() {
        Minimum = 0, Maximum = 999_999_999, Increment = step, FormatString = "0", Width = width, Watermark = ANY_FILTER,
    };

    /// <summary>Whether a number is inside a from/to pair of number boxes (an empty box is no limit).</summary>
    private static bool InFilterRange(long value, NumericUpDown from, NumericUpDown to)
        => (from.Value is not { } min || value >= min) && (to.Value is not { } max || value <= max);

    /// <summary>A drop-down's picked choice, or null when it is the first one (Any).</summary>
    private static string? FilterPick(ComboBox box)
        => box.SelectedIndex > 0 && box.SelectedItem is string picked ? picked : null;

    /// <summary>
    /// Shows the list again when any of these filters changes. Drop-downs only count their own choice (not one from a
    /// list further in); number boxes and typing wait a moment after the last change, as the search boxes do.
    /// </summary>
    private static void WhenFiltersChange(Action render, params Control[] filters) {
        var delay = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        delay.Tick += (_, _) => {
            delay.Stop();
            render();
        };
        void Later() {
            delay.Stop();
            delay.Start();
        }

        foreach (var filter in filters) {
            switch (filter) {
                case ComboBox box:
                    box.SelectionChanged += (_, e) => {
                        if (e.Source == box) {
                            render();
                        }
                    };
                    break;
                case NumericUpDown number:
                    number.ValueChanged += (_, _) => Later();
                    break;
                case CheckBox tick:
                    tick.IsCheckedChanged += (_, _) => render();
                    break;
                case TextBox text:
                    text.TextChanged += (_, _) => Later();
                    break;
            }
        }
    }

    /// <summary>The count line under a list: how many show of how many, and that clearing the filters shows them all.</summary>
    private static string FilterCount(int shown, int total, string things, int showing = int.MaxValue) {
        if (total == 0) {
            return $"No {things} yet.";
        }

        if (shown == 0) {
            return "Nothing matches. Clear the search or set the filters back to Any.";
        }

        var head = shown == total ? $"All {total:N0} {things}." : $"{shown:N0} of {total:N0} {things}.";

        return showing < shown ? $"{head} Showing the first {showing:N0}; add words or filters to narrow it down." : head;
    }

}
