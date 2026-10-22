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

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// A section of an editor page (Niduus, 2026-10-22: "organized sections of what and each, using their icons"): a card
/// with a coloured icon tile, a title and a line on what it is for, which folds shut when its header is clicked.
/// </summary>
internal sealed class Section : Border {

    private readonly Border _body = new();
    private readonly Control _chevron;
    private readonly TextBlock _badge = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    internal string Title { get; }
    internal string Icon { get; }
    internal IBrush Accent { get; }

    internal Section(string icon, IBrush accent, string title, string help, Control content) {
        Title = title;
        Icon = icon;
        Accent = accent;
        // The look is the Manager's theme (ManagerTheme.cs): every tab's sections are cards like this one.
        Background = ManagerTheme.SURFACE_BRUSH;
        BorderBrush = ManagerTheme.LINE_BRUSH;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS);
        ClipToBounds = true;
        Padding = new Thickness(0);
        // A section with nothing under its header (just a title and its line) needs no room below.
        _body.Padding = content is Panel { Children.Count: 0 } ? new Thickness(0, 0, 0, 2) : new Thickness(18, 4, 18, 18);
        _body.Child = content;
        _chevron = BlockIcons.Icon("down", 16, ManagerTheme.MUTED_BRUSH);
        var header = new Border {
            Background = Brushes.Transparent, Padding = new Thickness(14, 12), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new DockPanel {
                Children = {
                    Docked(new Border { Margin = new Thickness(0, 0, 12, 0), Child = BlockIcons.Tile(icon, accent), VerticalAlignment = VerticalAlignment.Center }, Dock.Left),
                    Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _badge, _chevron } }, Dock.Right),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                        Children = {
                            new TextBlock { Text = title, FontSize = ManagerTheme.FONT_SECTION, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = ManagerTheme.MUTED_BRUSH, IsVisible = help.Length > 0 },
                        },
                    },
                },
            },
        };
        ToolTip.SetTip(header, "Click to fold this section shut or open it");
        header.PointerReleased += (_, _) => SetOpen(!_body.IsVisible);
        var stripe = new Border { Height = 3, Background = accent, Opacity = 0.9 };
        Child = new StackPanel { Children = { stripe, header, _body } };
    }

    private static T Docked<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    internal void SetOpen(bool open) {
        _body.IsVisible = open;
        _chevron.RenderTransform = open ? null : new RotateTransform(-90);
    }

    /// <summary>A short note in the header, such as "Changed" or "3 cheats".</summary>
    internal void SetBadge(string text, IBrush? brush = null) {
        _badge.Text = text;
        _badge.Foreground = brush ?? ManagerTheme.MUTED_BRUSH;
    }

    /// <summary>A row of chips, one for each section, that open it and scroll to it.</summary>
    internal static Control Nav(IEnumerable<Section> sections) {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var section in sections) {
            var chip = new Button {
                Padding = new Thickness(8, 4, 12, 4), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(16),
                Content = new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { BlockIcons.Tile(section.Icon, section.Accent, 22), new TextBlock { Text = section.Title, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            chip.Bind(IsVisibleProperty, section.GetObservable(IsVisibleProperty));
            chip.Click += (_, _) => {
                section.SetOpen(true);
                section.BringIntoView();
            };
            row.Children.Add(chip);
        }

        return row;
    }

}
