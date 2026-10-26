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
using Avalonia.LogicalTree;

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

    /// <summary>
    /// Filters tucked behind a Filters button (Niduus, 2026-10-26, mockup F: "Make this the default for all search
    /// areas"): the button says how many are set, each filter that is set shows as a chip with an x that sets it back,
    /// and Clear all sets them all back. The filters themselves show in a row that wraps when the button is clicked.
    /// </summary>
    private static Control SearchFilters(params Control[] filters) {
        var panel = new WrapPanel();
        panel.Children.AddRange(filters);

        return new FilterBar(panel);
    }

    /// <summary>Previous or Next in a list's pop-up: picks the row before or after the picked one, which shows it.</summary>
    private static void StepList(ListBox list, int by) {
        var at = list.SelectedIndex + by;
        if (list.SelectedIndex >= 0 && at >= 0 && at < list.ItemCount) {
            list.SelectedIndex = at;
            list.ScrollIntoView(at);
        }
    }

    /// <summary>
    /// The details of what was clicked, in a pop-up over the list (Niduus, 2026-10-26, mockup L): hidden until something
    /// is picked, with Previous and Next to step through the list, and a close button (or Esc, or a click beside it) that
    /// hides it again, so the list always has the whole page.
    /// </summary>
    private sealed class DetailsPopup : Panel {

        private readonly TextBlock _title = new() {
            FontSize = 18, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        private readonly Button _previous = new() { Classes = { "ui" }, Content = "‹ Previous" };
        private readonly Button _next = new() { Classes = { "ui" }, Content = "Next ›" };

        internal DetailsPopup(string title, Control details, Action close, Action<int> step) {
            _title.Text = title;
            IsVisible = false;
            Focusable = true;
            var shade = new Border { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(170, 5, 6, 13)) };
            // A click beside the panel closes it, as Esc does.
            shade.PointerPressed += (_, _) => close();
            var closeButton = new Button { Classes = { "ui" }, Padding = new Thickness(10, 4), Content = ManagerTheme.TextIcon("close", 14) };
            ToolTip.SetTip(closeButton, "Close (Esc)");
            closeButton.Click += (_, _) => close();
            ToolTip.SetTip(_previous, "The one before it in the list (Left arrow)");
            ToolTip.SetTip(_next, "The next one in the list (Right arrow)");
            _previous.Click += (_, _) => step(-1);
            _next.Click += (_, _) => step(1);
            details.Margin = new Thickness(0);
            var panel = new Border {
                MaxWidth = 960, Margin = new Thickness(40, 28), Padding = new Thickness(22, 18),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(244, 24, 26, 46)),
                BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 110), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18), Effect = ManagerTheme.Glow(Avalonia.Media.Colors.Black, 60, 0.7),
                Child = new DockPanel {
                    Children = {
                        WithDock(new DockPanel {
                            Margin = new Thickness(0, 0, 0, 12),
                            Children = {
                                WithDock(new StackPanel {
                                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0),
                                    Children = { _previous, _next, closeButton },
                                }, Avalonia.Controls.Dock.Right),
                                _title,
                            },
                        }, Avalonia.Controls.Dock.Top),
                        details,
                    },
                },
            };
            Children.Add(shade);
            Children.Add(panel);
            KeyDown += (_, e) => {
                switch (e.Key) {
                    case Avalonia.Input.Key.Escape: close(); break;
                    case Avalonia.Input.Key.Left: step(-1); break;
                    case Avalonia.Input.Key.Right: step(1); break;
                    default: return;
                }

                e.Handled = true;
            };
        }

        /// <summary>Opens it for an item of a list (closes it for none), with Previous and Next by where the item is in the list.</summary>
        internal void ShowFor(ListBox list, object? item) {
            var at = item is null ? -1 : list.Items.IndexOf(item);
            Show(item is not null, at > 0, at >= 0 && at < list.ItemCount - 1);
        }

        /// <summary>Opens it (or closes it, with nothing picked), and greys out Previous or Next at either end of the list.</summary>
        internal void Show(bool open, bool hasPrevious = true, bool hasNext = true) {
            var opening = open && !IsVisible;
            IsVisible = open;
            _previous.IsEnabled = hasPrevious;
            _next.IsEnabled = hasNext;
            if (opening) {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus(), Avalonia.Threading.DispatcherPriority.Background);
            }
        }

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

    /// <summary>The Filters button, the chips of the filters that are set, and the filters (shown when the button is clicked).</summary>
    private sealed class FilterBar : StackPanel {

        private readonly Panel _filters;
        private readonly Border _drawer;
        private readonly Button _toggle = new() { Classes = { "ui" } };
        private readonly TextBlock _toggleText = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly WrapPanel _chips = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly Button _clearAll = new() { Content = "Clear all", Classes = { "ui" } };
        // Each filter's value when the page was made, which its chip's x and Clear all put back.
        private readonly Dictionary<Control, object?> _defaults = [];
        private bool _resetting;

        internal FilterBar(Panel filters) {
            _filters = filters;
            Spacing = 8;
            _drawer = new Border {
                IsVisible = false, Child = filters, Padding = new Thickness(14, 12, 4, 6),
                Background = ManagerTheme.Glass(10), BorderBrush = ManagerTheme.Glass(30), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL + 2),
            };
            _toggle.Content = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7,
                Children = { ManagerTheme.TextIcon("sliders", 15), _toggleText },
            };
            ToolTip.SetTip(_toggle, "Show or hide every filter of this search");
            _toggle.Click += (_, _) => {
                _drawer.IsVisible = !_drawer.IsVisible;
                Refresh();
            };
            _clearAll.Click += (_, _) => Reset(Inputs().Select(i => i.Box).ToList());
            Children.Add(new DockPanel {
                Children = {
                    WithDock(WithMargin(_toggle, new Thickness(0, 0, 10, 0)), Avalonia.Controls.Dock.Left),
                    WithDock(_clearAll, Avalonia.Controls.Dock.Right),
                    _chips,
                },
            });
            Children.Add(_drawer);

            foreach (var (box, _) in Inputs()) {
                _defaults[box] = ValueOf(box);
            }

            // Every kind of filter tells the bar when it changes, including ones added later (the Gear tab's stat rows).
            AddHandler(Avalonia.Controls.Primitives.SelectingItemsControl.SelectionChangedEvent, (_, _) => Refresh());
            AddHandler(Avalonia.Controls.Primitives.ToggleButton.IsCheckedChangedEvent, (_, _) => Refresh());
            AddHandler(NumericUpDown.ValueChangedEvent, (_, _) => Refresh());
            AddHandler(TextBox.TextChangedEvent, (_, _) => Refresh());
            AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
        }

        /// <summary>The filters' boxes, drop-downs and ticks that show (a filter hidden by its page doesn't count), with their names.</summary>
        private List<(Control Box, string Name)> Inputs() {
            var found = new List<(Control, string)>();
            foreach (var control in _filters.GetLogicalDescendants().OfType<Control>()) {
                if (control is not (ComboBox or NumericUpDown or CheckBox or TextBox) || !ShowsInFilters(control)) {
                    continue;
                }

                // A text box inside a number box or a drop-down is part of it, not a filter of its own.
                if (control is TextBox && control.GetLogicalAncestors().Any(a => a is NumericUpDown or ComboBox)) {
                    continue;
                }

                found.Add((control, NameOf(control)));
            }

            return found;
        }

        private bool ShowsInFilters(Control control) {
            for (ILogical? at = control; at is not null && at != _filters; at = at.LogicalParent) {
                if (at is Control { IsVisible: false }) {
                    return false;
                }
            }

            return true;
        }

        private static string NameOf(Control control) {
            if (control is CheckBox { Content: string text }) {
                return text;
            }

            // A filter made by SearchFilter: its label is the text over it.
            if (control.Parent is StackPanel { Children: [TextBlock { Text: { } label }, ..] }) {
                return label;
            }

            return control is TextBox { Watermark: { } watermark } ? watermark : "Filter";
        }

        private static object? ValueOf(Control control) => control switch {
            // A drop-down filled later (from the game's data) has nothing picked yet: that counts as its first choice (Any or All).
            ComboBox box => Math.Max(0, box.SelectedIndex),
            NumericUpDown number => number.Value,
            CheckBox tick => tick.IsChecked,
            TextBox text => text.Text ?? "",
            _ => null,
        };

        private object? DefaultOf(Control control) => _defaults.TryGetValue(control, out var value) ? value : control switch {
            ComboBox => 0,
            NumericUpDown => null,
            CheckBox => false,
            TextBox => "",
            _ => null,
        };

        private bool IsSet(Control control) => !Equals(ValueOf(control), DefaultOf(control))
                                               && !(control is TextBox text && string.IsNullOrWhiteSpace(text.Text) && Equals(DefaultOf(control), ""));

        private static string Shown(Control control, string name) => control switch {
            ComboBox box => $"{name}: {box.SelectedItem}",
            NumericUpDown number => $"{name}: {number.Value?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? ANY_FILTER}",
            CheckBox { IsChecked: true } => name,
            CheckBox => $"Not: {name}",
            TextBox text => $"{name}: {text.Text}",
            _ => name,
        };

        private void Reset(List<Control> boxes) {
            _resetting = true;
            try {
                foreach (var box in boxes) {
                    var value = DefaultOf(box);
                    switch (box) {
                        case ComboBox combo: combo.SelectedIndex = value as int? ?? 0; break;
                        case NumericUpDown number: number.Value = value as decimal?; break;
                        case CheckBox tick: tick.IsChecked = value as bool?; break;
                        case TextBox text: text.Text = value as string ?? ""; break;
                    }
                }
            }
            finally {
                _resetting = false;
            }

            Refresh();
        }

        private void Refresh() {
            if (_resetting) {
                return;
            }

            var set = Inputs().Where(i => IsSet(i.Box)).ToList();
            _toggleText.Text = set.Count > 0 ? $"Filters · {set.Count}" : "Filters";
            _toggle.Classes.Set("accent", set.Count > 0 && !_drawer.IsVisible);
            _clearAll.IsVisible = set.Count > 0;
            _chips.Children.Clear();
            if (set.Count == 0) {
                _chips.Children.Add(new TextBlock {
                    Text = _drawer.IsVisible ? "Pick filters below to narrow the list." : "No filters set. Click Filters to narrow the list.",
                    Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });

                return;
            }

            foreach (var (box, name) in set) {
                var remove = new Button {
                    Padding = new Thickness(4), Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL),
                    Background = ManagerTheme.Glass(26), BorderThickness = new Thickness(0), MinHeight = 0,
                    Content = ManagerTheme.TextIcon("close", 10), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                ToolTip.SetTip(remove, "Set this filter back");
                var target = box;
                remove.Click += (_, _) => Reset([target]);
                _chips.Children.Add(new Border {
                    Margin = new Thickness(0, 2, 8, 2), Padding = new Thickness(12, 3, 4, 3), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL),
                    Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 50), BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 150),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Children = {
                            new TextBlock { Text = Shown(box, name), FontSize = ManagerTheme.FONT_SMALL, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                            remove,
                        },
                    },
                });
            }
        }

    }

}
