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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Imlight.Setup;

/// <summary>
/// The look of the whole Manager in one place (Niduus, 2026-10-22: "make the whole manager nicer to compensate for the
/// new design in the 'block'-like programming"): its colours, fonts, sizes, corner radius and icons, and the styles
/// that give every tab's buttons, boxes and switches the same look as the block editor. AreaStyle (the Customs tab's
/// areas) and BlockColors (the block editor) read their colours from here.
/// </summary>
internal static class ManagerTheme {

    // ---------------------------------------------------------------------
    // Colours
    // ---------------------------------------------------------------------

    internal static readonly Color BACKGROUND = Color.FromRgb(19, 22, 28);
    internal static readonly Color SIDEBAR = Color.FromRgb(14, 17, 22);
    internal static readonly Color SURFACE = Color.FromRgb(27, 31, 39);
    internal static readonly Color SURFACE_HIGH = Color.FromRgb(36, 41, 51);
    internal static readonly Color SURFACE_HOVER = Color.FromRgb(44, 50, 62);
    internal static readonly Color FIELD = Color.FromRgb(17, 20, 26);
    internal static readonly Color LINE = Color.FromRgb(43, 49, 61);
    internal static readonly Color LINE_STRONG = Color.FromRgb(60, 67, 82);
    internal static readonly Color TEXT = Color.FromRgb(231, 234, 240);
    internal static readonly Color TEXT_SOFT = Color.FromRgb(176, 183, 196);
    internal static readonly Color MUTED = Color.FromRgb(142, 150, 166);

    /// <summary>The colour of primary buttons, ticks and the selected tab.</summary>
    internal static readonly Color ACCENT = Color.FromRgb(76, 125, 240);
    internal static readonly Color ACCENT_LIGHT = Color.FromRgb(104, 147, 246);
    internal static readonly Color ACCENT_DARK = Color.FromRgb(58, 103, 210);

    internal static readonly Color GOOD = Color.FromRgb(46, 170, 98);
    internal static readonly Color WARN = Color.FromRgb(221, 156, 44);
    internal static readonly Color BAD = Color.FromRgb(224, 80, 68);
    internal static readonly Color BAD_TEXT = Color.FromRgb(255, 150, 140);

    internal static readonly Color LOG_BACK = Color.FromRgb(13, 16, 21);
    internal static readonly Color LOG_TEXT = Color.FromRgb(216, 222, 233);

    // The three kinds of block (Scratch-like: When, If, Do) and their darker edges.
    internal static readonly Color BLOCK_WHEN = Color.FromRgb(224, 163, 35);
    internal static readonly Color BLOCK_WHEN_EDGE = Color.FromRgb(179, 127, 18);
    internal static readonly Color BLOCK_IF = Color.FromRgb(58, 143, 217);
    internal static readonly Color BLOCK_IF_EDGE = Color.FromRgb(36, 104, 166);
    internal static readonly Color BLOCK_DO = Color.FromRgb(139, 92, 214);
    internal static readonly Color BLOCK_DO_EDGE = Color.FromRgb(101, 56, 168);

    // The parts of a custom area (the Customs tab).
    internal static readonly Color AREA_ZONE = Color.FromRgb(64, 176, 168);
    internal static readonly Color AREA_GENERAL = Color.FromRgb(140, 150, 165);
    internal static readonly Color AREA_OBJECTS = Color.FromRgb(76, 141, 230);
    internal static readonly Color AREA_SIGIL = Color.FromRgb(226, 180, 60);
    internal static readonly Color AREA_COMBAT = Color.FromRgb(222, 92, 72);
    internal static readonly Color AREA_CREATURES = Color.FromRgb(86, 178, 98);
    internal static readonly Color AREA_REMOVED = Color.FromRgb(200, 84, 110);
    internal static readonly Color AREA_ENCOUNTERS = Color.FromRgb(232, 128, 52);
    internal static readonly Color AREA_DISTINCT = Color.FromRgb(150, 110, 220);
    internal static readonly Color AREA_EXIT = Color.FromRgb(56, 160, 220);
    internal static readonly Color AREA_ADVANCED = Color.FromRgb(120, 128, 140);

    // When a change counts.
    internal static readonly Color TIMING_LIVE = GOOD;
    internal static readonly Color TIMING_NEXT_USE = Color.FromRgb(70, 130, 220);
    internal static readonly Color TIMING_RELOAD = WARN;

    /// <summary>The colour of each tab and each kind of section, by name.</summary>
    private static readonly Dictionary<string, Color> s_accents = new() {
        // Tabs.
        ["Play"] = Color.FromRgb(46, 170, 98),
        ["Commands"] = BLOCK_WHEN,
        ["Lookup"] = BLOCK_IF,
        ["Spells"] = Color.FromRgb(196, 74, 112),
        ["Bosses"] = Color.FromRgb(217, 101, 59),
        ["Customs"] = BLOCK_DO,
        ["Guides"] = Color.FromRgb(46, 160, 140),
        ["Server"] = Color.FromRgb(63, 167, 160),
        ["Logs"] = Color.FromRgb(108, 122, 140),
        ["Settings"] = ACCENT,
        ["Accounts"] = Color.FromRgb(199, 92, 154),
        ["Game Files"] = Color.FromRgb(201, 162, 39),
        ["Tools"] = Color.FromRgb(140, 150, 165),
        // The boss editor's sections.
        ["identity"] = Color.FromRgb(63, 167, 160),
        ["stats"] = Color.FromRgb(217, 101, 59),
        ["deck"] = BLOCK_IF,
        ["drops"] = Color.FromRgb(201, 162, 39),
        ["rewards"] = Color.FromRgb(63, 166, 90),
        ["forms"] = Color.FromRgb(154, 92, 201),
        ["cheats"] = BLOCK_WHEN,
        // Other sections.
        ["good"] = GOOD,
        ["warn"] = WARN,
        ["bad"] = BAD,
        ["accent"] = ACCENT,
        ["zone"] = AREA_ZONE,
    };

    internal static Color AccentColor(string name) => s_accents.TryGetValue(name, out var color) ? color : Color.FromRgb(108, 122, 140);

    internal static IBrush Accent(string name) => Brush(AccentColor(name));

    internal static IBrush Brush(Color color, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

    internal static readonly IBrush BACKGROUND_BRUSH = Brush(BACKGROUND);
    internal static readonly IBrush SIDEBAR_BRUSH = Brush(SIDEBAR);
    internal static readonly IBrush SURFACE_BRUSH = Brush(SURFACE);
    internal static readonly IBrush SURFACE_HIGH_BRUSH = Brush(SURFACE_HIGH);
    internal static readonly IBrush LINE_BRUSH = Brush(LINE);
    internal static readonly IBrush TEXT_BRUSH = Brush(TEXT);
    internal static readonly IBrush MUTED_BRUSH = Brush(MUTED);

    // ---------------------------------------------------------------------
    // Fonts, sizes and corners
    // ---------------------------------------------------------------------

    internal static readonly FontFamily MONO = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    internal const double FONT_APP = 19;
    internal const double FONT_PAGE = 24;
    internal const double FONT_SECTION = 17;
    internal const double FONT_HEADING = 15;
    internal const double FONT_BODY = 14;
    internal const double FONT_SMALL = 12;

    internal const double RADIUS = 12;
    internal const double RADIUS_SMALL = 8;
    internal const double RADIUS_PILL = 999;

    internal const double GAP = 14;
    internal const double PAD = 18;
    internal const double CONTROL_HEIGHT = 34;
    internal const double SIDEBAR_WIDTH = 214;
    internal const double PAGE_MAX_WIDTH = 1060;

    // ---------------------------------------------------------------------
    // Icons (drawn on a 24 by 24 grid, so they show before the game files are found)
    // ---------------------------------------------------------------------

    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);

    // A circle and an ellipse as path data.
    private static string C(double x, double y, double r) => E(x, y, r, r);

    private static string E(double x, double y, double rx, double ry)
        => $"M{F(x - rx)} {F(y)}a{F(rx)} {F(ry)} 0 1 0 {F(2 * rx)} 0a{F(rx)} {F(ry)} 0 1 0 {F(-2 * rx)} 0z";

    // Name: (path data, filled). Filled icons are painted, the others drawn as lines.
    private static readonly Dictionary<string, (string Data, bool Filled)> s_icons = new() {
        // Blocks and the boss editor.
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
        // Toolbars and buttons.
        ["plus"] = ("M12 5v14 M5 12h14", false),
        ["copy"] = ("M8 8h12v12H8z M4 16V4h12", false),
        ["trash"] = ("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13", false),
        ["bin"] = ("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13 M10 11v6 M14 11v6", false),
        ["eye"] = ("M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z " + C(12, 12, 3), false),
        ["eyeoff"] = ("M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M3 3l18 18", false),
        ["grip"] = (C(9, 6, 1.6) + " " + C(15, 6, 1.6) + " " + C(9, 12, 1.6) + " " + C(15, 12, 1.6) + " " + C(9, 18, 1.6) + " " + C(15, 18, 1.6), true),
        ["up"] = ("M6 15l6-6 6 6", false),
        ["down"] = ("M6 9l6 6 6-6", false),
        ["right"] = ("M9 6l6 6-6 6", false),
        ["undo"] = ("M9 14L4 9l5-5 M4 9h10a6 6 0 0 1 0 12h-3", false),
        ["search"] = (C(10.5, 10.5, 6.5) + " M15.5 15.5L21 21", false),
        ["play"] = ("M7 4.5v15a.8.8 0 0 0 1.2.7l12-7.5a.8.8 0 0 0 0-1.4l-12-7.5A.8.8 0 0 0 7 4.5z", true),
        ["square"] = ("M6 5h12a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z", true),
        ["save"] = ("M5 3h11l4 4v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 1-2z M8 3v5h7V3 M7 21v-7h10v7", false),
        ["folder"] = ("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z", false),
        ["pencil"] = ("M15.5 4.5l4 4L8 20H4v-4z M13 7l4 4", false),
        ["coin"] = (C(12, 12, 9) + " M12 6.5v11 M14.8 8.8c-.6-.8-1.6-1.2-2.8-1.2-1.6 0-2.8.8-2.8 2s1.2 1.7 2.8 2 2.8.8 2.8 2-1.2 2-2.8 2c-1.3 0-2.4-.5-3-1.4", false),
        ["star"] = ("M12 2l3 6.3 6.9.9-5 4.8 1.2 6.9L12 17.6 5.9 20.9 7.1 14l-5-4.8 6.9-.9z", true),
        ["card"] = ("M6 2h12a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M8 6h8v7H8z M8 16h8 M8 18.5h5", false),
        ["refresh"] = ("M20 12a8 8 0 1 1-2.4-5.7 M20 4v4.5h-4.5", false),
        ["power"] = ("M12 3v9 M6.4 6.6a8 8 0 1 0 11.2 0", false),
        ["globe"] = (C(12, 12, 9) + " M3 12h18 M12 3c3 3.5 3 14.5 0 18 M12 3c-3 3.5-3 14.5 0 18", false),
        ["download"] = ("M12 3v12 M7 10l5 5 5-5 M4 20h16", false),
        ["check"] = ("M5 12.5l4.5 4.5L19 7.5", false),
        ["close"] = ("M6 6l12 12 M18 6L6 18", false),
        ["info"] = (C(12, 12, 9) + " M12 11v6 M12 7.5v.5", false),
        ["key"] = (C(8, 15, 4) + " M11 12l9-9 M16.5 6.5l3 3 M14 9l2 2", false),
        ["lock"] = ("M6 11h12v10H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3", false),
        ["unlock"] = ("M6 11h12v10H6z M8.5 11V8a3.5 3.5 0 0 1 6.8-1.2", false),
        ["gift"] = ("M4 9h16v4H4z M5.5 13h13v8h-13z M12 9v12 M12 9c-1.5-3.5-5.5-4-5.5-1.5S10 9 12 9zm0 0c1.5-3.5 5.5-4 5.5-1.5S14 9 12 9z", false),
        ["link"] = ("M10 14a4.5 4.5 0 0 0 6.4 0l3-3a4.5 4.5 0 0 0-6.4-6.4l-1 1 M14 10a4.5 4.5 0 0 0-6.4 0l-3 3a4.5 4.5 0 0 0 6.4 6.4l1-1", false),
        // Tabs.
        ["terminal"] = ("M4 4h16a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z M7 9l3 3-3 3 M12.5 15H17", false),
        ["server"] = ("M5 4h14a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z M5 14h14a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z M7.5 7h1 M7.5 17h1 M12 7h4 M12 17h4", false),
        ["log"] = ("M6 3h12a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M8.5 7.5h7 M8.5 11.5h7 M8.5 15.5h4", false),
        ["gear"] = (C(12, 12, 3.2) + " " + C(12, 12, 7.2) + " M12 2v2.8 M12 19.2V22 M2 12h2.8 M19.2 12H22 M4.9 4.9l2 2 M17.1 17.1l2 2 M4.9 19.1l2-2 M17.1 6.9l2-2", false),
        ["wrench"] = ("M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.4-3.4a6 6 0 0 1-7.9 7.9l-6.9 6.9a2.1 2.1 0 0 1-3-3l6.9-6.9a6 6 0 0 1 7.9-7.9z", false),
        ["home"] = ("M3 11l9-7 9 7 M5.5 9.5V20h13V9.5 M10 20v-6h4v6", false),
        // The parts of a custom area.
        ["pin"] = ("M12 21s-7-6.2-7-12a7 7 0 0 1 14 0c0 5.8-7 12-7 12z " + C(12, 9, 2.5), false),
        ["sliders"] = ("M4 6h9 M17 6h3 M15 4v4 M4 12h3 M11 12h9 M9 10v4 M4 18h11 M19 18h1 M17 16v4", false),
        ["cube"] = ("M12 2.5 20.5 7v10L12 21.5 3.5 17V7z M3.5 7 12 11.5 20.5 7 M12 11.5v10", false),
        ["sigil"] = (C(12, 12, 9) + " M12 5.5l1.9 4.2 4.6.4-3.5 3 1.1 4.5-4.1-2.4-4.1 2.4 1.1-4.5-3.5-3 4.6-.4z", false),
        ["ring"] = (C(12, 12, 9) + " " + C(12, 12, 4.5) + " M12 3v4.5 M12 16.5V21 M3 12h4.5 M16.5 12H21", false),
        ["paw"] = (E(5.8, 10, 1.8, 2.3) + " " + E(9.4, 5.8, 1.8, 2.3) + " " + E(14.6, 5.8, 1.8, 2.3) + " " + E(18.2, 10, 1.8, 2.3)
                   + " M12 11.5c-3 0-6 4-6 6.5 0 1.7 1.5 2.5 3 2.5 1.2 0 2-.6 3-.6s1.8.6 3 .6c1.5 0 3-.8 3-2.5 0-2.5-3-6.5-6-6.5z", true),
        ["arrows"] = ("M7 4 3 8l4 4 M3 8h14 M17 20l4-4-4-4 M21 16H7", false),
        ["ban"] = (C(12, 12, 9) + " M5.6 5.6l12.8 12.8", false),
        ["swords"] = ("M14.5 17.5 3 6V3h3l11.5 11.5 M13 19l6-6 M16 16l4 4 M19 21l2-2 M14.5 6.5 18 3h3v3l-3.5 3.5 M5 14l4 4 M7 17l-3 3 M3 19l2 2", false),
        ["crownline"] = ("M3 8l4.5 4L12 5l4.5 7L21 8l-2 11H5z", false),
        ["shuffle"] = ("M3 6h4l10 12h4 M17 15l3 3-3 3 M3 18h4l3-3.6 M14 9.6 17 6h4 M17 3l3 3-3 3", false),
        ["exit"] = ("M14 3h5a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1h-5 M10 17l5-5-5-5 M15 12H3", false),
        ["radius"] = (C(12, 12, 9) + " M12 12l6.4-6.4 M15 5.6h3.4V9", false),
        ["crosshair"] = (C(12, 12, 8) + " M12 2v4 M12 18v4 M2 12h4 M18 12h4 " + C(12, 12, 1), false),
        ["file"] = ("M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z M14 3v5h5 M8 13h8 M8 17h6", false),
        ["code"] = ("M8 7l-5 5 5 5 M16 7l5 5-5 5 M14 4l-4 16", false),
        ["branch"] = ("M6 3v12 M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 9a9 9 0 0 1-9 9", false),
        ["map"] = ("M3 7l6-3 6 3 6-3v13l-6 3-6-3-6 3z M9 4v13 M15 7v13", false),
    };

    internal static bool HasIcon(string name) => s_icons.ContainsKey(name);

    /// <summary>An icon as a control, in the given colour and size.</summary>
    internal static Control Icon(string name, double size, IBrush brush) {
        var path = IconPath(name);
        if (s_icons.TryGetValue(name, out var icon) && icon.Filled) {
            path.Fill = brush;
        }
        else {
            path.Stroke = brush;
        }

        return Boxed(path, size);
    }

    /// <summary>
    /// An icon in the colour of the text around it (a button's), so it follows the button when it is pointed at or
    /// switched off.
    /// </summary>
    internal static Control TextIcon(string name, double size) {
        var path = IconPath(name);
        var foreground = new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ContentPresenter) } };
        path.Bind(s_icons.TryGetValue(name, out var icon) && icon.Filled ? Shape.FillProperty : Shape.StrokeProperty, foreground);

        return Boxed(path, size);
    }

    // Each icon's shape is parsed once and shared: every button, tile and section header draws one.
    private static readonly Dictionary<string, Geometry> s_iconGeometry = [];

    private static Path IconPath(string name) {
        if (!s_icons.ContainsKey(name)) {
            name = "puzzle";
        }

        var (data, filled) = s_icons[name];
        if (!s_iconGeometry.TryGetValue(name, out var geometry)) {
            geometry = StreamGeometry.Parse(data);
            s_iconGeometry[name] = geometry;
        }

        var path = new Path { Data = geometry };
        if (!filled) {
            path.StrokeThickness = 2;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
        }

        return path;
    }

    // A line icon drawn into a box of its own keeps its stroke inside and its proportions.
    private static Control Boxed(Path path, double size)
        => new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };

    /// <summary>An icon on a rounded tile of its colour, as the blocks and section headers show them.</summary>
    internal static Border Tile(string name, IBrush background, double size = 34) => new() {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 3.5), Background = background,
        Child = new Border { Padding = new Thickness(size * 0.2), Child = Icon(name, size * 0.6, Brushes.White) },
    };

    // ---------------------------------------------------------------------
    // Buttons
    // ---------------------------------------------------------------------

    /// <summary>The icon each button label shows, so the same action looks the same on every tab.</summary>
    private static readonly Dictionary<string, string> s_buttonIcons = new() {
        ["Play Wizard101"] = "play", ["Play"] = "play", ["Run in my game"] = "play", ["Start server"] = "play",
        ["Stop server"] = "square", ["Stop"] = "square", ["Restart"] = "refresh", ["Refresh"] = "refresh",
        ["Update Imlight"] = "download", ["Uninstall Imlight"] = "bin",
        ["Save server"] = "save", ["Save changes"] = "save", ["Save shop research"] = "save",
        ["Save log to desktop"] = "download", ["Save game log to desktop"] = "download",
        ["Copy"] = "copy", ["Copy all"] = "copy", ["Copy address"] = "copy", ["Copy command"] = "copy", ["Copy ID"] = "copy",
        ["Copy .joinme"] = "copy", ["Copy X, Y, Z"] = "copy", ["Copy Rotation, Angle, Width"] = "copy", ["Duplicate"] = "copy",
        ["Copy my game files"] = "download", ["Read my copy"] = "file",
        ["Open log folder"] = "folder", ["Open settings folder"] = "folder", ["Open install folder"] = "folder",
        ["Open game log folder"] = "folder", ["Open game files folder"] = "folder", ["Open key details"] = "folder",
        ["Let friends connect"] = "globe", ["Create account"] = "plus", ["Make the custom"] = "plus", ["New cheat"] = "plus",
        ["Add"] = "plus", ["Add a form"] = "plus", ["Use"] = "check", ["OK"] = "check", ["Cancel"] = "close",
        ["Edit"] = "pencil", ["Edit blocks"] = "puzzle", ["Close"] = "up", ["Open all"] = "down", ["Close all"] = "up",
        ["Delete"] = "bin", ["Remove"] = "bin", ["Discard"] = "undo",
        ["Put back the game's values"] = "undo", ["Put back the game's deck"] = "undo", ["Put back the game's cheats"] = "undo",
        ["New password"] = "key", ["Lock"] = "lock", ["Unlock"] = "unlock",
        ["Learn on my wizard"] = "sparkle", ["Send to my gifts"] = "gift",
    };

    /// <summary>A button with its label's icon. Primary ones are filled with the accent colour.</summary>
    internal static Button MakeButton(string text, bool primary) {
        var button = new Button { Content = ButtonContent(text), Classes = { "ui" } };
        if (primary) {
            button.Classes.Add("accent");
        }

        return button;
    }

    /// <summary>A button's label with its icon in front, when the label has one.</summary>
    internal static object ButtonContent(string text) => s_buttonIcons.TryGetValue(text, out var icon)
        ? new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { TextIcon(icon, 15), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        }
        : text;

    // ---------------------------------------------------------------------
    // Pieces every tab uses
    // ---------------------------------------------------------------------

    /// <summary>A tab's header: its icon tile, its name and one line on what it is for.</summary>
    internal static Control PageHeader(string icon, IBrush accent, string title, string line) => new DockPanel {
        Margin = new Thickness(24, 20, 24, 2), MaxWidth = PAGE_MAX_WIDTH,
        Children = {
            Docked(new Border { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Child = Tile(icon, accent, 46) }, Dock.Left),
            new StackPanel {
                VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                Children = {
                    new TextBlock { Text = title, FontSize = FONT_PAGE, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = line, Foreground = MUTED_BRUSH, TextWrapping = TextWrapping.Wrap },
                },
            },
        },
    };

    /// <summary>A tab's name in the side bar, with its icon.</summary>
    internal static Control TabHeader(string icon, IBrush accent, string title) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 12,
        Children = {
            Tile(icon, accent, 28),
            new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    /// <summary>A pill with a dot, such as the server's state in the window's header.</summary>
    internal static Border Badge(Border dot, TextBlock text) => new() {
        CornerRadius = new CornerRadius(RADIUS_PILL), Padding = new Thickness(12, 5, 14, 5), BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dot, text } },
    };

    /// <summary>Colours a badge (and its text) after its dot.</summary>
    internal static void PaintBadge(Border badge, TextBlock text, IBrush? dot) {
        var color = (dot as ISolidColorBrush)?.Color ?? MUTED;
        color = Color.FromRgb(color.R, color.G, color.B);
        badge.Background = Brush(color, 38);
        badge.BorderBrush = Brush(color, 120);
        text.Foreground = Brush(Mix(color, Colors.White, 0.35));
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte) (a.R + ((b.R - a.R) * amount)), (byte) (a.G + ((b.G - a.G) * amount)), (byte) (a.B + ((b.B - a.B) * amount)));

    /// <summary>A row of a list inside a section: an account, a server, a custom.</summary>
    internal static Border Item(Control content, bool highlighted = false) => new() {
        Background = highlighted ? Brush(ACCENT, 34) : SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(RADIUS_SMALL),
        BorderBrush = highlighted ? Brush(ACCENT, 140) : LINE_BRUSH, BorderThickness = new Thickness(1), Padding = new Thickness(12, 8),
        Child = content,
    };

    private static T Docked<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    // ---------------------------------------------------------------------
    // Styles
    // ---------------------------------------------------------------------

    private static bool s_resourcesSet;

    /// <summary>
    /// Gives a window the Manager's look. The colours of the app's controls go into the application's resources (the
    /// built-in theme reads them there); the shapes go into the window's styles.
    /// </summary>
    internal static void Apply(Window window) {
        window.Background = BACKGROUND_BRUSH;
        window.FontSize = FONT_BODY;
        if (!s_resourcesSet && Application.Current is { } app) {
            s_resourcesSet = true;
            foreach (var (key, value) in Resources()) {
                app.Resources[key] = value;
            }
        }

        window.Styles.AddRange(Styles());
    }

    private static IEnumerable<(string Key, object Value)> Resources() {
        // The accent the built-in theme builds on (ticks, selections, focus).
        yield return ("SystemAccentColor", ACCENT);
        yield return ("SystemAccentColorDark1", ACCENT_DARK);
        yield return ("SystemAccentColorDark2", ACCENT_DARK);
        yield return ("SystemAccentColorDark3", ACCENT_DARK);
        yield return ("SystemAccentColorLight1", ACCENT_LIGHT);
        yield return ("SystemAccentColorLight2", ACCENT_LIGHT);
        yield return ("SystemAccentColorLight3", ACCENT_LIGHT);
        yield return ("ControlCornerRadius", new CornerRadius(RADIUS_SMALL));
        yield return ("OverlayCornerRadius", new CornerRadius(RADIUS));

        // Buttons: secondary ones are a raised grey, primary ones the accent.
        yield return ("ButtonPadding", new Thickness(14, 7));
        yield return ("ButtonBackground", Brush(SURFACE_HIGH));
        yield return ("ButtonBackgroundPointerOver", Brush(SURFACE_HOVER));
        yield return ("ButtonBackgroundPressed", Brush(SURFACE));
        yield return ("ButtonBackgroundDisabled", Brush(SURFACE_HIGH, 120));
        yield return ("ButtonBorderBrush", Brush(LINE_STRONG));
        yield return ("ButtonBorderBrushPointerOver", Brush(Mix(LINE_STRONG, Colors.White, 0.15)));
        yield return ("ButtonBorderBrushPressed", Brush(LINE_STRONG));
        yield return ("ButtonBorderBrushDisabled", Brush(LINE));
        yield return ("ButtonForeground", Brush(TEXT));
        yield return ("ButtonForegroundPointerOver", Brush(Colors.White));
        yield return ("ButtonForegroundPressed", Brush(TEXT_SOFT));
        yield return ("ButtonForegroundDisabled", Brush(MUTED, 150));
        yield return ("AccentButtonBackground", Brush(ACCENT));
        yield return ("AccentButtonBackgroundPointerOver", Brush(ACCENT_LIGHT));
        yield return ("AccentButtonBackgroundPressed", Brush(ACCENT_DARK));
        yield return ("AccentButtonBackgroundDisabled", Brush(ACCENT, 70));
        yield return ("AccentButtonBorderBrush", Brush(ACCENT_LIGHT, 120));
        yield return ("AccentButtonBorderBrushPointerOver", Brush(ACCENT_LIGHT));
        yield return ("AccentButtonBorderBrushPressed", Brush(ACCENT_DARK));
        yield return ("AccentButtonBorderBrushDisabled", Brush(ACCENT, 40));
        yield return ("AccentButtonForeground", Brush(Colors.White));
        yield return ("AccentButtonForegroundPointerOver", Brush(Colors.White));
        yield return ("AccentButtonForegroundPressed", Brush(Colors.White, 220));
        yield return ("AccentButtonForegroundDisabled", Brush(Colors.White, 130));

        // Text boxes and drop-downs: a dark well that lights up in the accent when typed in.
        foreach (var state in new[] { "", "PointerOver" }) {
            yield return ($"TextControlBackground{state}", Brush(FIELD));
            yield return ($"ComboBoxBackground{state}", Brush(FIELD));
        }

        yield return ("TextControlBackgroundFocused", Brush(Color.FromRgb(12, 14, 19)));
        yield return ("TextControlBorderBrush", Brush(LINE_STRONG));
        yield return ("TextControlBorderBrushPointerOver", Brush(Mix(LINE_STRONG, Colors.White, 0.15)));
        yield return ("TextControlBorderBrushFocused", Brush(ACCENT));
        yield return ("TextControlForegroundFocused", Brush(TEXT));
        yield return ("TextControlPlaceholderForeground", Brush(MUTED, 190));
        yield return ("TextControlPlaceholderForegroundPointerOver", Brush(MUTED, 220));
        yield return ("TextControlPlaceholderForegroundFocused", Brush(MUTED, 160));
        yield return ("TextControlThemePadding", new Thickness(10, 6));
        yield return ("ComboBoxBackgroundPressed", Brush(FIELD));
        yield return ("ComboBoxBorderBrush", Brush(LINE_STRONG));
        yield return ("ComboBoxBorderBrushPointerOver", Brush(Mix(LINE_STRONG, Colors.White, 0.15)));
        yield return ("ComboBoxDropDownBackground", Brush(SURFACE));
        yield return ("ComboBoxDropDownBorderBrush", Brush(LINE_STRONG));

        // Switches: green when on, so on and off are told apart at a glance.
        var on = GOOD;
        var onLight = Mix(GOOD, Colors.White, 0.15);
        yield return ("ToggleSwitchFillOn", Brush(on));
        yield return ("ToggleSwitchFillOnPointerOver", Brush(onLight));
        yield return ("ToggleSwitchFillOnPressed", Brush(on));
        yield return ("ToggleSwitchStrokeOn", Brush(on));
        yield return ("ToggleSwitchStrokeOnPointerOver", Brush(onLight));
        yield return ("ToggleSwitchStrokeOnPressed", Brush(on));
        yield return ("ToggleSwitchKnobFillOn", Brush(Colors.White));
        yield return ("ToggleSwitchKnobFillOnPointerOver", Brush(Colors.White));
        yield return ("ToggleSwitchKnobFillOnPressed", Brush(Colors.White));
        yield return ("ToggleSwitchFillOff", Brush(FIELD));
        yield return ("ToggleSwitchFillOffPointerOver", Brush(SURFACE_HIGH));
        yield return ("ToggleSwitchFillOffPressed", Brush(SURFACE_HIGH));
        yield return ("ToggleSwitchStrokeOff", Brush(Mix(LINE_STRONG, Colors.White, 0.2)));
        yield return ("ToggleSwitchStrokeOffPointerOver", Brush(Mix(LINE_STRONG, Colors.White, 0.35)));
        yield return ("ToggleSwitchKnobFillOff", Brush(TEXT_SOFT));
        yield return ("ToggleSwitchKnobFillOffPointerOver", Brush(TEXT));
        yield return ("ToggleSwitchContentForeground", Brush(TEXT_SOFT));

        // Check boxes.
        yield return ("CheckBoxCheckBackgroundFillChecked", Brush(ACCENT));
        yield return ("CheckBoxCheckBackgroundFillCheckedPointerOver", Brush(ACCENT_LIGHT));
        yield return ("CheckBoxCheckBackgroundFillCheckedPressed", Brush(ACCENT_DARK));
        yield return ("CheckBoxCheckBackgroundFillUnchecked", Brush(FIELD));
        yield return ("CheckBoxCheckBackgroundStrokeUnchecked", Brush(Mix(LINE_STRONG, Colors.White, 0.2)));

        // The side bar's tabs.
        yield return ("TabItemHeaderBackgroundUnselected", Brushes.Transparent);
        yield return ("TabItemHeaderBackgroundUnselectedPointerOver", Brush(SURFACE));
        yield return ("TabItemHeaderBackgroundUnselectedPressed", Brush(SURFACE));
        yield return ("TabItemHeaderBackgroundSelected", Brush(SURFACE_HIGH));
        yield return ("TabItemHeaderBackgroundSelectedPointerOver", Brush(SURFACE_HOVER));
        yield return ("TabItemHeaderBackgroundSelectedPressed", Brush(SURFACE_HIGH));
        yield return ("TabItemHeaderForegroundUnselected", Brush(TEXT_SOFT));
        yield return ("TabItemHeaderForegroundUnselectedPointerOver", Brush(TEXT));
        yield return ("TabItemHeaderForegroundUnselectedPressed", Brush(TEXT));
        yield return ("TabItemHeaderForegroundSelected", Brush(Colors.White));
        yield return ("TabItemHeaderForegroundSelectedPointerOver", Brush(Colors.White));
        yield return ("TabItemHeaderForegroundSelectedPressed", Brush(Colors.White));
        yield return ("TabItemHeaderSelectedPipeFill", Brush(ACCENT));

        // Folding panels (the Customs tab's areas).
        yield return ("ExpanderHeaderBackground", Brush(SURFACE));
        yield return ("ExpanderHeaderBackgroundPointerOver", Brush(SURFACE_HIGH));
        yield return ("ExpanderHeaderBackgroundPressed", Brush(SURFACE_HIGH));
        yield return ("ExpanderHeaderBorderBrush", Brush(LINE));
        yield return ("ExpanderHeaderBorderBrushPointerOver", Brush(LINE_STRONG));
        yield return ("ExpanderHeaderBorderBrushPressed", Brush(LINE_STRONG));
        yield return ("ExpanderContentBackground", Brush(Color.FromRgb(22, 26, 33)));
        yield return ("ExpanderContentBorderBrush", Brush(LINE));

        // Lists.
        yield return ("ScrollBarSize", 12.0);
    }

    private static IEnumerable<IStyle> Styles() {
        yield return new Style(x => x.OfType<Button>()) {
            Setters = { new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium) },
        };

        // Danger: a red-tinted button for what cannot be undone.
        var bad = BAD;
        yield return new Style(x => x.OfType<Button>().Class("danger")) {
            Setters = {
                new Setter(TemplatedControl.BackgroundProperty, Brush(bad, 36)),
                new Setter(TemplatedControl.BorderBrushProperty, Brush(bad, 130)),
                new Setter(TemplatedControl.ForegroundProperty, Brush(BAD_TEXT)),
            },
        };
        foreach (var (state, alpha) in new[] { (":pointerover", (byte) 80), (":pressed", (byte) 110) }) {
            yield return new Style(x => x.OfType<Button>().Class("danger").Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter")) {
                Setters = {
                    new Setter(ContentPresenter.BackgroundProperty, Brush(bad, alpha)),
                    new Setter(ContentPresenter.BorderBrushProperty, Brush(bad)),
                    new Setter(ContentPresenter.ForegroundProperty, Brush(Colors.White)),
                },
            };
        }

        // The side bar's tabs: rounded, full width, with the icon tile in front.
        yield return new Style(x => x.OfType<TabItem>().Class("nav")) {
            Setters = {
                new Setter(Layoutable.WidthProperty, SIDEBAR_WIDTH - 20),
                new Setter(Layoutable.MarginProperty, new Thickness(10, 2)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 0, 12, 0)),
                new Setter(Layoutable.MinHeightProperty, 44.0),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(TemplatedControl.FontSizeProperty, 15.0),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
            },
        };

        // Buttons, boxes and drop-downs share one height, so a row of them lines up exactly. (The blocks' own small
        // boxes and chips set their sizes themselves and keep them.)
        yield return new Style(x => x.OfType<Button>().Class("ui")) {
            Setters = {
                new Setter(Layoutable.MinHeightProperty, CONTROL_HEIGHT),
                new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            },
        };
        yield return new Style(x => x.OfType<TextBox>()) {
            Setters = {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(RADIUS_SMALL)),
                new Setter(Layoutable.MinHeightProperty, CONTROL_HEIGHT),
            },
        };
        yield return new Style(x => x.OfType<ComboBox>()) {
            Setters = { new Setter(Layoutable.MinHeightProperty, CONTROL_HEIGHT) },
        };
        yield return new Style(x => x.OfType<AutoCompleteBox>()) {
            Setters = { new Setter(Layoutable.MinHeightProperty, CONTROL_HEIGHT) },
        };
        yield return new Style(x => x.OfType<ToggleSwitch>()) {
            Setters = { new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium) },
        };
    }

}
