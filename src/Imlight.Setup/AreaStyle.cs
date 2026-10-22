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
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>The built-in line icons of the Customs tab's areas (drawn on a 24 by 24 grid).</summary>
internal enum AreaIcon {
    Zone, General, Objects, Sigil, CombatCircle, Creatures, Swap, Removed, Encounters, Boss, Distinct, Exit, Radius,
    Position, File, Add, Delete, Copy, Code, Variant, Back, Search, Place,
}

/// <summary>
/// Everything the Customs tab's area editor looks like, in one place: its colours, sizes, the icons and the small
/// building blocks (section headers, timing tags). A later restyle of the Manager changes this file only.
/// </summary>
internal static class AreaStyle {

    // ---------------------------------------------------------------------
    // Colours and sizes
    // ---------------------------------------------------------------------

    internal static readonly Color ZONE = Color.FromRgb(64, 176, 168);
    internal static readonly Color GENERAL = Color.FromRgb(140, 150, 165);
    internal static readonly Color OBJECTS = Color.FromRgb(76, 141, 230);
    internal static readonly Color SIGIL = Color.FromRgb(226, 180, 60);
    internal static readonly Color COMBAT = Color.FromRgb(222, 92, 72);
    internal static readonly Color CREATURES = Color.FromRgb(86, 178, 98);
    internal static readonly Color REMOVED = Color.FromRgb(200, 84, 110);
    internal static readonly Color ENCOUNTERS = Color.FromRgb(232, 128, 52);
    internal static readonly Color DISTINCT = Color.FromRgb(150, 110, 220);
    internal static readonly Color EXIT = Color.FromRgb(56, 160, 220);
    internal static readonly Color ADVANCED = Color.FromRgb(120, 128, 140);

    internal static readonly Color LIVE = Color.FromRgb(46, 160, 92);
    internal static readonly Color NEXT_USE = Color.FromRgb(70, 130, 220);
    internal static readonly Color RELOAD = Color.FromRgb(214, 150, 40);

    internal static readonly IBrush SECTION_BACK = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128));
    internal static readonly IBrush ITEM_BACK = new SolidColorBrush(Color.FromArgb(26, 128, 128, 128));
    internal static readonly IBrush MUTED = new SolidColorBrush(Color.FromArgb(170, 128, 128, 128));
    internal static readonly IBrush BAD = new SolidColorBrush(Color.FromRgb(220, 70, 60));
    internal static readonly IBrush DIRTY = new SolidColorBrush(RELOAD);

    internal const double LABEL_WIDTH = 170;
    internal const double ICON = 18;
    internal const double TILE = 30;
    internal const double SECTION_SPACING = 8;
    internal const double FIELD_SPACING = 6;

    internal static IBrush Brush(Color color, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

    // ---------------------------------------------------------------------
    // Icons
    // ---------------------------------------------------------------------

    private static string Ellipse(double cx, double cy, double rx, double ry)
        => $"M{cx - rx} {cy}a{rx} {ry} 0 1 0 {2 * rx} 0a{rx} {ry} 0 1 0 {-2 * rx} 0z";

    private static readonly Dictionary<AreaIcon, (string Data, bool Filled)> s_icons = new() {
        [AreaIcon.Zone] = ("M12 21s-7-6.2-7-12a7 7 0 0 1 14 0c0 5.8-7 12-7 12z " + Ellipse(12, 9, 2.5, 2.5), false),
        [AreaIcon.General] = ("M4 6h9 M17 6h3 M15 4v4 M4 12h3 M11 12h9 M9 10v4 M4 18h11 M19 18h1 M17 16v4", false),
        [AreaIcon.Objects] = ("M12 2.5 20.5 7v10L12 21.5 3.5 17V7z M3.5 7 12 11.5 20.5 7 M12 11.5v10", false),
        [AreaIcon.Sigil] = (Ellipse(12, 12, 9, 9) + " M12 5.5l1.9 4.2 4.6.4-3.5 3 1.1 4.5-4.1-2.4-4.1 2.4 1.1-4.5-3.5-3 4.6-.4z", false),
        [AreaIcon.CombatCircle] = (Ellipse(12, 12, 9, 9) + " " + Ellipse(12, 12, 4.5, 4.5) + " M12 3v4.5 M12 16.5V21 M3 12h4.5 M16.5 12H21", false),
        [AreaIcon.Creatures] = (Ellipse(5.8, 10, 1.8, 2.3) + " " + Ellipse(9.4, 5.8, 1.8, 2.3) + " " + Ellipse(14.6, 5.8, 1.8, 2.3) + " "
                                + Ellipse(18.2, 10, 1.8, 2.3) + " M12 11.5c-3 0-6 4-6 6.5 0 1.7 1.5 2.5 3 2.5 1.2 0 2-.6 3-.6s1.8.6 3 .6c1.5 0 3-.8 3-2.5 0-2.5-3-6.5-6-6.5z", true),
        [AreaIcon.Swap] = ("M7 4 3 8l4 4 M3 8h14 M17 20l4-4-4-4 M21 16H7", false),
        [AreaIcon.Removed] = (Ellipse(12, 12, 9, 9) + " M5.6 5.6l12.8 12.8", false),
        [AreaIcon.Encounters] = ("M14.5 17.5 3 6V3h3l11.5 11.5 M13 19l6-6 M16 16l4 4 M19 21l2-2 M14.5 6.5 18 3h3v3l-3.5 3.5 M5 14l4 4 M7 17l-3 3 M3 19l2 2", false),
        [AreaIcon.Boss] = ("M3 8l4.5 4L12 5l4.5 7L21 8l-2 11H5z", false),
        [AreaIcon.Distinct] = ("M3 6h4l10 12h4 M17 15l3 3-3 3 M3 18h4l3-3.6 M14 9.6 17 6h4 M17 3l3 3-3 3", false),
        [AreaIcon.Exit] = ("M14 3h5a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1h-5 M10 17l5-5-5-5 M15 12H3", false),
        [AreaIcon.Radius] = (Ellipse(12, 12, 9, 9) + " M12 12l6.4-6.4 M15 5.6h3.4V9", false),
        [AreaIcon.Position] = (Ellipse(12, 12, 8, 8) + " M12 2v4 M12 18v4 M2 12h4 M18 12h4 " + Ellipse(12, 12, 1, 1), false),
        [AreaIcon.File] = ("M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z M14 3v5h5 M8 13h8 M8 17h6", false),
        [AreaIcon.Add] = ("M12 5v14 M5 12h14", false),
        [AreaIcon.Delete] = ("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13 M10 11v6 M14 11v6", false),
        [AreaIcon.Copy] = ("M8 8h12v12H8z M16 8V4H4v12h4", false),
        [AreaIcon.Code] = ("M8 7l-5 5 5 5 M16 7l5 5-5 5 M14 4l-4 16", false),
        [AreaIcon.Variant] = ("M6 3v12 M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 9a9 9 0 0 1-9 9", false),
        [AreaIcon.Back] = ("M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11", false),
        [AreaIcon.Search] = (Ellipse(11, 11, 7, 7) + " M21 21l-4.3-4.3", false),
        [AreaIcon.Place] = ("M3 7l6-3 6 3 6-3v13l-6 3-6-3-6 3z M9 4v13 M15 7v13", false),
    };

    /// <summary>A built-in icon in a colour.</summary>
    internal static Control Glyph(AreaIcon icon, Color color, double size = ICON) {
        var (data, filled) = s_icons[icon];
        var path = new Path {
            Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = size, Height = size,
        };
        if (filled) {
            path.Fill = Brush(color);
        }
        else {
            path.Stroke = Brush(color);
            path.StrokeThickness = 2;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
        }

        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { SizeTo24(path) } } };
    }

    // The paths are drawn on a 24 grid, so they keep their proportions inside the Viewbox.
    private static Path SizeTo24(Path path) {
        path.Stretch = Stretch.None;
        path.Width = double.NaN;
        path.Height = double.NaN;

        return path;
    }

    /// <summary>An icon on a rounded tile of its colour, for section headers.</summary>
    internal static Border Tile(Control icon, Color color, double size = TILE) => new() {
        Width = size, Height = size, CornerRadius = new CornerRadius(8), Background = Brush(color, 46),
        BorderBrush = Brush(color, 120), BorderThickness = new Thickness(1),
        Child = new Panel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon } },
    };

    /// <summary>A small coloured dot for a school, when the game's own school icon cannot be shown.</summary>
    internal static Control SchoolDot(string school, double size = ICON) => new Border {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = new SolidColorBrush(Color.FromUInt32(CardPainter.SchoolColor(school))),
        Child = new TextBlock {
            Text = school.Length > 0 ? school[..1] : "?", FontSize = size * 0.6, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // ---------------------------------------------------------------------
    // When a change counts
    // ---------------------------------------------------------------------

    internal enum Timing { Live, NextUse, Reload }

    internal static string TimingText(Timing timing) => timing switch {
        Timing.Live => "Changes at once",
        Timing.NextUse => "Counts from its next use",
        _ => "Needs the zone to load again",
    };

    internal static string TimingHelp(Timing timing) => timing switch {
        Timing.Live => "The running server applies it as soon as you press Save, for everyone in the zone. No restart.",
        Timing.NextUse => "The running server reads it the next time it is needed (the next battle, spawn or exit). No restart.",
        _ => "Read when the zone loads. For a private copy (an area with a variant, or a dungeon) everyone leaves and goes in again; " +
             "for a shared zone such as Ravenwood, restart the server.",
    };

    /// <summary>A small tag that says when a section's changes count, with the details on hover.</summary>
    internal static Control TimingTag(Timing timing) {
        var color = timing switch { Timing.Live => LIVE, Timing.NextUse => NEXT_USE, _ => RELOAD };
        var tag = new Border {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1), Background = Brush(color, 40),
            BorderBrush = Brush(color, 150), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = TimingText(timing), FontSize = 12, Foreground = Brush(color) },
        };
        ToolTip.SetTip(tag, TimingHelp(timing));

        return tag;
    }

    /// <summary>A small grey count, such as "3".</summary>
    internal static Control Count(string text) => new Border {
        CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), Background = ITEM_BACK, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = MUTED },
    };

    /// <summary>The header of a section: its icon tile, title, a count and when its changes count.</summary>
    internal static Control SectionHeader(Control tile, string title, string? count, Timing? timing) {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(tile);
        row.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (count is not null) {
            row.Children.Add(Count(count));
        }

        if (timing is { } t) {
            row.Children.Add(TimingTag(t));
        }

        return row;
    }

    /// <summary>A collapsible section with a coloured left edge.</summary>
    internal static Expander Section(Control header, Control body, Color color, bool open) => new() {
        Header = header, Content = body, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        BorderBrush = Brush(color, 140), BorderThickness = new Thickness(3, 0, 0, 0), Background = SECTION_BACK,
    };

    /// <summary>One entry of a list in a section (an object, a creature, an encounter).</summary>
    internal static Border Item(Control content, Color color) => new() {
        Background = ITEM_BACK, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10),
        BorderBrush = Brush(color, 90), BorderThickness = new Thickness(1), Child = content,
    };

}
