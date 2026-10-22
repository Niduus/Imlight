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
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// Small drawn icons (24 by 24) for blocks, palettes and section headers. They are drawn, not read from the game, so
/// they show even before the game files are found; the game's own pictures (school icons, spell cards) are used where
/// the app has them.
/// </summary>
internal static class BlockIcons {

    // A circle as path data.
    private static string C(double x, double y, double r)
        => string.Format(CultureInfo.InvariantCulture, "M{0} {1}a{2} {2} 0 1 0 {3} 0a{2} {2} 0 1 0 {4} 0z", x - r, y, r, 2 * r, -2 * r);

    // Name: (path data, filled). Filled icons are painted, the others drawn as lines.
    private static readonly Dictionary<string, (string Data, bool Filled)> s_icons = new() {
        ["flag"] = ("M5 3h2v18H5z M8 4h11l-2.5 4 2.5 4H8z", true),
        ["bolt"] = ("M13 2L4 14h7l-1 8 9-12h-7z", true),
        ["clock"] = (C(12, 12, 9) + " M12 7v5l3.5 2.5", false),
        ["clockend"] = (C(12, 12, 9) + " M12 7v5h-4", false),
        ["heart"] = ("M12 21s-7-4.6-9.3-9.1C1.2 8.9 3 5 6.6 5c2.1 0 3.4 1.2 4.4 2.6C12 6.2 13.3 5 15.4 5 19 5 20.8 8.9 19.3 11.9 17 16.4 12 21 12 21z", true),
        ["heal"] = ("M9 3h6v6h6v6h-6v6H9v-6H3V9h6z", true),
        ["shield"] = ("M12 2l8 3v6c0 5-3.5 9-8 11-4.5-2-8-6-8-11V5z", true),
        ["sword"] = ("M20 4L9.5 14.5 M6.5 12.5l5 5 M4 17l3 3 M20 4v4.5 M20 4h-4.5", false),
        ["skull"] = ("F0 M12 3c-4.4 0-8 3.1-8 7.5 0 2.4 1.1 4 2.5 5V19h3v-2h1v2h3v-2h1v2h3v-3.5c1.4-1 2.5-2.6 2.5-5C20 6.1 16.4 3 12 3z " + C(9, 10.5, 1.8) + " " + C(15, 10.5, 1.8), true),
        ["dice"] = ("F0 M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z " + C(8, 8, 1.6) + " " + C(12, 12, 1.6) + " " + C(16, 16, 1.6), true),
        ["chat"] = ("M4 4h16a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H9l-5 4v-4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z", true),
        ["quote"] = ("M3 5h18v11H10l-5 4v-4H3z M7 9h10 M7 12h6", false),
        ["sparkle"] = ("M12 2l2.4 6.6L21 11l-6.6 2.4L12 20l-2.4-6.6L3 11l6.6-2.4z M19 2l.8 2.2L22 5l-2.2.8L19 8l-.8-2.2L16 5l2.2-.8z", true),
        ["stop"] = ("M8 2h8l6 6v8l-6 6H8l-6-6V8z", true),
        ["repeat"] = ("M17 2l4 4-4 4 M3 11V9a3 3 0 0 1 3-3h15 M7 22l-4-4 4-4 M21 13v2a3 3 0 0 1-3 3H3", false),
        ["target"] = (C(12, 12, 8) + " " + C(12, 12, 3) + " M12 1v4 M12 19v4 M1 12h4 M19 12h4", false),
        ["pips"] = (C(5, 12, 3) + " " + C(12, 12, 3) + " " + C(19, 12, 3), true),
        ["user"] = (C(12, 7.5, 4) + " M4 21c0-4.4 3.6-7.5 8-7.5s8 3.1 8 7.5z", true),
        ["users"] = (C(9, 8, 3.5) + " M2 20c0-3.9 3.1-6.5 7-6.5s7 2.6 7 6.5z " + C(17, 7, 2.8) + " M17.5 12c2.6.2 4.5 2.4 4.5 5.5h-4.5", true),
        ["swap"] = ("M4 7h14l-3.5-3.5 M20 17H6l3.5 3.5", false),
        ["layers"] = ("M12 2l10 5-10 5L2 7z M2 12l10 5 10-5 M2 17l10 5 10-5", false),
        ["crown"] = ("M3 7l4.5 4L12 4l4.5 7L21 7l-2 12H5z", true),
        ["id"] = ("M3 5h18v14H3z M6.5 9h4v5h-4z M13 10h5 M13 14h4", false),
        ["stats"] = ("M4 20V10h4v10z M10 20V4h4v16z M16 20v-7h4v7z", true),
        ["cards"] = ("M8 3h11a2 2 0 0 1 2 2v12h-2V5H8z M5 7h10a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2z", true),
        ["bag"] = ("M4 9h16l-1.5 11a2 2 0 0 1-2 1.7h-9a2 2 0 0 1-2-1.7z M8 9V7a4 4 0 0 1 8 0v2h-2V7a2 2 0 0 0-4 0v2z", true),
        ["puzzle"] = ("M10 3a2 2 0 0 1 4 0v2h5v5h-2a2 2 0 0 0 0 4h2v5h-5v-2a2 2 0 0 0-4 0v2H5v-5h2a2 2 0 0 0 0-4H5V5h5z", true),
        ["broom"] = ("M20 3l-8 8 M7 10l7 7 M7 10c-2 2-4 3-5 8 5-1 6-3 8-5", false),
        ["hourglass"] = ("M6 3h12 M6 21h12 M7 3c0 6 10 5 10 9s-10 3-10 9 M17 3c0 6-10 5-10 9s10 3 10 9", false),
        ["once"] = (C(12, 12, 9) + " M10 8.5l2.5-1.5v10", false),
        ["list"] = ("M8 6h13 M8 12h13 M8 18h13 M3 6h1 M3 12h1 M3 18h1", false),
        ["hit"] = ("M12 2l1.8 5.5L19 5l-2.5 5.2L22 12l-5.5 1.8L19 19l-5.2-2.5L12 22l-1.8-5.5L5 19l2.5-5.2L2 12l5.5-1.8L5 5l5.2 2.5z", true),
        ["drop"] = ("M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z", true),
        ["school"] = ("M12 3l9 4.5-9 4.5-9-4.5z M6 10v5c0 1.7 2.7 3 6 3s6-1.3 6-3v-5", false),
        ["note"] = ("M5 3h10l4 4v14H5z M14 3v5h5 M8 12h8 M8 16h6", false),
        // Toolbar.
        ["plus"] = ("M12 5v14 M5 12h14", false),
        ["copy"] = ("M8 8h12v12H8z M4 16V4h12", false),
        ["trash"] = ("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13", false),
        ["eye"] = ("M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z " + C(12, 12, 3), false),
        ["eyeoff"] = ("M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M3 3l18 18", false),
        ["grip"] = (C(9, 6, 1.6) + " " + C(15, 6, 1.6) + " " + C(9, 12, 1.6) + " " + C(15, 12, 1.6) + " " + C(9, 18, 1.6) + " " + C(15, 18, 1.6), true),
        ["up"] = ("M6 15l6-6 6 6", false),
        ["down"] = ("M6 9l6 6 6-6", false),
        ["right"] = ("M9 6l6 6-6 6", false),
        ["undo"] = ("M9 14L4 9l5-5 M4 9h10a6 6 0 0 1 0 12h-3", false),
        ["search"] = (C(10.5, 10.5, 6.5) + " M15.5 15.5L21 21", false),
    };

    internal static bool Has(string name) => s_icons.ContainsKey(name);

    /// <summary>An icon as a control, in the given colour and size.</summary>
    internal static Control Icon(string name, double size, IBrush brush) {
        var (data, filled) = s_icons.TryGetValue(name, out var icon) ? icon : s_icons["puzzle"];
        var path = new Path {
            Data = StreamGeometry.Parse(data),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
        };
        if (filled) {
            path.Fill = brush;
        }
        else {
            path.Stroke = brush;
            path.StrokeThickness = 2;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
        }

        // A line icon drawn into a box of its own keeps its stroke inside.
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { Fixed(path) } } };
    }

    private static Path Fixed(Path path) {
        path.Stretch = Stretch.None;
        path.Width = double.NaN;
        path.Height = double.NaN;

        return path;
    }

    /// <summary>An icon on a rounded coloured tile, for section headers and palette groups.</summary>
    internal static Border Tile(string name, IBrush background, double size = 34) => new() {
        Width = size, Height = size, CornerRadius = new Avalonia.CornerRadius(size / 3.5), Background = background,
        Child = new Border { Padding = new Avalonia.Thickness(size * 0.2), Child = Icon(name, size * 0.6, Brushes.White) },
    };

}
