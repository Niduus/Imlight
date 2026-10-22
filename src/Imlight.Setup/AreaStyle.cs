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

namespace Imlight.Setup;

/// <summary>The built-in line icons of the Customs tab's areas (drawn on a 24 by 24 grid).</summary>
internal enum AreaIcon {
    Zone, General, Objects, Sigil, CombatCircle, Creatures, Swap, Removed, Encounters, Boss, Distinct, Exit, Radius,
    Position, File, Add, Delete, Copy, Code, Variant, Back, Search, Place,
}

/// <summary>
/// Everything the Customs tab's area editor looks like, in one place: its colours, sizes, the icons and the small
/// building blocks (section headers, timing tags). Its colours and icons come from the Manager's theme (ManagerTheme.cs).
/// </summary>
internal static class AreaStyle {

    // ---------------------------------------------------------------------
    // Colours and sizes
    // ---------------------------------------------------------------------

    // The colours are the Manager's theme (ManagerTheme.cs), so the areas match the block editor and every other tab.
    internal static readonly Color ZONE = ManagerTheme.AREA_ZONE;
    internal static readonly Color GENERAL = ManagerTheme.AREA_GENERAL;
    internal static readonly Color OBJECTS = ManagerTheme.AREA_OBJECTS;
    internal static readonly Color SIGIL = ManagerTheme.AREA_SIGIL;
    internal static readonly Color COMBAT = ManagerTheme.AREA_COMBAT;
    internal static readonly Color CREATURES = ManagerTheme.AREA_CREATURES;
    internal static readonly Color REMOVED = ManagerTheme.AREA_REMOVED;
    internal static readonly Color ENCOUNTERS = ManagerTheme.AREA_ENCOUNTERS;
    internal static readonly Color DISTINCT = ManagerTheme.AREA_DISTINCT;
    internal static readonly Color EXIT = ManagerTheme.AREA_EXIT;
    internal static readonly Color ADVANCED = ManagerTheme.AREA_ADVANCED;

    internal static readonly Color LIVE = ManagerTheme.TIMING_LIVE;
    internal static readonly Color NEXT_USE = ManagerTheme.TIMING_NEXT_USE;
    internal static readonly Color RELOAD = ManagerTheme.TIMING_RELOAD;

    internal static readonly IBrush SECTION_BACK = ManagerTheme.SURFACE_BRUSH;
    internal static readonly IBrush ITEM_BACK = ManagerTheme.SURFACE_HIGH_BRUSH;
    internal static readonly IBrush MUTED = ManagerTheme.MUTED_BRUSH;
    internal static readonly IBrush BAD = ManagerTheme.Brush(ManagerTheme.BAD);
    internal static readonly IBrush DIRTY = new SolidColorBrush(RELOAD);

    internal const double LABEL_WIDTH = 170;
    internal const double ICON = 18;
    internal const double TILE = 30;
    internal const double SECTION_SPACING = 8;
    internal const double FIELD_SPACING = 6;

    internal static IBrush Brush(Color color, byte alpha = 255) => ManagerTheme.Brush(color, alpha);

    // ---------------------------------------------------------------------
    // Icons
    // ---------------------------------------------------------------------

    // The icons live in the theme with every other icon of the app; these are their names there.
    private static readonly Dictionary<AreaIcon, string> s_icons = new() {
        [AreaIcon.Zone] = "pin", [AreaIcon.General] = "sliders", [AreaIcon.Objects] = "cube", [AreaIcon.Sigil] = "sigil",
        [AreaIcon.CombatCircle] = "ring", [AreaIcon.Creatures] = "paw", [AreaIcon.Swap] = "arrows", [AreaIcon.Removed] = "ban",
        [AreaIcon.Encounters] = "swords", [AreaIcon.Boss] = "crownline", [AreaIcon.Distinct] = "shuffle", [AreaIcon.Exit] = "exit",
        [AreaIcon.Radius] = "radius", [AreaIcon.Position] = "crosshair", [AreaIcon.File] = "file", [AreaIcon.Add] = "plus",
        [AreaIcon.Delete] = "bin", [AreaIcon.Copy] = "copy", [AreaIcon.Code] = "code", [AreaIcon.Variant] = "branch",
        [AreaIcon.Back] = "undo", [AreaIcon.Search] = "search", [AreaIcon.Place] = "map",
    };

    /// <summary>A built-in icon in a colour.</summary>
    internal static Control Glyph(AreaIcon icon, Color color, double size = ICON) => ManagerTheme.Icon(s_icons[icon], size, Brush(color));

    /// <summary>An icon on a rounded tile of its colour, for section headers.</summary>
    internal static Border Tile(Control icon, Color color, double size = TILE) => new() {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 3.5), Background = Brush(color, 56),
        BorderBrush = Brush(color, 140), BorderThickness = new Thickness(1),
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

    /// <summary>A collapsible section with a coloured left edge, a card like every other tab's sections.</summary>
    internal static Expander Section(Control header, Control body, Color color, bool open) => new() {
        Header = header, Content = body, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL + 2),
        BorderBrush = Brush(color, 170), BorderThickness = new Thickness(3, 0, 0, 0), Background = SECTION_BACK,
    };

    /// <summary>One entry of a list in a section (an object, a creature, an encounter).</summary>
    internal static Border Item(Control content, Color color) => new() {
        Background = ITEM_BACK, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 10),
        BorderBrush = Brush(color, 90), BorderThickness = new Thickness(1), Child = content,
    };

}
