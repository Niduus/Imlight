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
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// The boxes inside a block's sentence: light rounded chips with dark text, so they read as blanks to fill in on the
/// coloured block. Each calls back with the new value as it changes.
/// </summary>
internal static class BlockFields {

    private static readonly IBrush s_bad = new SolidColorBrush(Color.FromRgb(220, 70, 60));

    private static void Light(StyledElement control, params string[] prefixes) {
        foreach (var prefix in prefixes) {
            foreach (var state in new[] { "", "PointerOver", "Focused", "Pressed", "Disabled" }) {
                control.Resources[$"{prefix}Background{state}"] = BlockColors.FIELD;
                control.Resources[$"{prefix}Foreground{state}"] = BlockColors.FIELD_TEXT;
                control.Resources[$"{prefix}BorderBrush{state}"] = Brushes.Transparent;
            }
        }
    }

    /// <summary>A drop-down of choices (value, words), showing the words of the value picked.</summary>
    internal static ComboBox Choice(IReadOnlyList<(string Value, string Label)> options, string current, Action<string> changed,
                                    double width = double.NaN) {
        var list = options.ToList();
        var at = list.FindIndex(o => o.Value.Equals(current ?? "", StringComparison.OrdinalIgnoreCase));
        if (at < 0 && !string.IsNullOrEmpty(current)) {
            // A value a file has that the list does not (written by hand or by a newer server) stays pickable.
            list.Add((current, current));
            at = list.Count - 1;
        }

        var combo = new ComboBox {
            ItemsSource = list.Select(o => o.Label).ToList(), SelectedIndex = Math.Max(0, at), FontSize = 13, MinHeight = 26,
            Padding = new Thickness(10, 2, 4, 2), CornerRadius = new CornerRadius(13), Width = width,
            Background = BlockColors.FIELD, Foreground = BlockColors.FIELD_TEXT, BorderThickness = new Thickness(0),
        };
        Light(combo, "ComboBox");
        combo.Resources["ComboBoxDropDownGlyphForeground"] = BlockColors.FIELD_TEXT;
        combo.SelectionChanged += (_, _) => {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < list.Count) {
                changed(list[combo.SelectedIndex].Value);
            }
        };

        return combo;
    }

    /// <summary>A whole number box; a number it cannot read (or outside min to max) shows red and is not passed on.</summary>
    internal static TextBox Number(int value, Action<int> changed, int min = 0, int max = 100_000_000, double width = 64, string watermark = "") {
        var box = new TextBox {
            Text = value.ToString(CultureInfo.InvariantCulture), Width = width, MinWidth = 0, MinHeight = 26, FontSize = 13,
            Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(13), HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = BlockColors.FIELD, Foreground = BlockColors.FIELD_TEXT, BorderThickness = new Thickness(0), Watermark = watermark,
        };
        Light(box, "TextControl");
        box.TextChanged += (_, _) => {
            var text = (box.Text ?? "").Trim().Replace(",", "").TrimEnd('%');
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) {
                box.BorderThickness = new Thickness(0);
                changed(n);
            }
            else {
                box.BorderBrush = s_bad;
                box.BorderThickness = new Thickness(2);
                ToolTip.SetTip(box, $"A whole number from {min:N0} to {max:N0}.");
            }
        };

        return box;
    }

    /// <summary>A box of words.</summary>
    internal static TextBox Words(string value, Action<string> changed, string watermark = "", double width = 180) {
        var box = new TextBox {
            Text = value, Width = width, MinWidth = 0, MinHeight = 26, FontSize = 13, Padding = new Thickness(10, 3),
            CornerRadius = new CornerRadius(13), Background = BlockColors.FIELD, Foreground = BlockColors.FIELD_TEXT,
            BorderThickness = new Thickness(0), Watermark = watermark,
        };
        Light(box, "TextControl");
        box.TextChanged += (_, _) => changed(box.Text ?? "");

        return box;
    }

    /// <summary>A yes/no chip: a tick and its words.</summary>
    internal static CheckBox Toggle(string label, bool value, Action<bool> changed, string tip = "") {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = BlockColors.TEXT, FontSize = 13, MinHeight = 26 };
        if (tip.Length > 0) {
            ToolTip.SetTip(box, tip);
        }

        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);

        return box;
    }

    /// <summary>
    /// A chip that opens a pop-up to pick something (a spell, a creature): it shows the picture and words given, and
    /// the pop-up is made each time it opens.
    /// </summary>
    internal static Button Picker(Control? picture, string text, Func<Flyout, Control> popup, string tip = "") {
        var button = new Button {
            Padding = new Thickness(6, 2, 10, 2), MinHeight = 26, CornerRadius = new CornerRadius(13), FontSize = 13,
            Background = BlockColors.FIELD, Foreground = BlockColors.FIELD_TEXT, BorderThickness = new Thickness(0),
        };
        Light(button, "Button");
        SetPicker(button, picture, text);
        if (tip.Length > 0) {
            ToolTip.SetTip(button, tip);
        }

        button.Click += (_, _) => {
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
            flyout.Content = popup(flyout);
            flyout.ShowAt(button);
        };

        return button;
    }

    /// <summary>Changes what a picker chip shows.</summary>
    internal static void SetPicker(Button button, Control? picture, string text) {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (picture is not null) {
            picture.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(picture);
        }

        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = BlockColors.FIELD_TEXT, FontWeight = FontWeight.SemiBold });
        button.Content = row;
    }

}
