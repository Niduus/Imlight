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
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// What each game window's minimap shows (Niduus, 2026-11-12: "mini map that is very detailed"), as the server last sent
/// it over the overlay link (<see cref="OverlayHost.Link"/>, op "map", about twice a second while a minimap shows). Kept by
/// game window; the designers (no game window) get a made-up map so the look can be set up before playing.
/// </summary>
internal static class OverlayMinimapData {

    internal sealed record Mark(string Kind, double X, double Y, string Name, string Quest, int Level);

    internal sealed record Quest(string Title, bool Main, string Step, string Where, bool Here);

    internal sealed record Snapshot(string Place, double X, double Y, double Yaw, IReadOnlyList<Mark> Marks, IReadOnlyList<Quest> Quests, string Note,
                                    DateTime At) {

        internal bool Stale => DateTime.UtcNow - At > s_staleAfter;

    }

    private static readonly TimeSpan s_staleAfter = TimeSpan.FromSeconds(6);
    private static readonly object s_lock = new();
    private static readonly Dictionary<long, Snapshot> s_maps = [];
    private static readonly Dictionary<long, int> s_versions = [];
    private static readonly HashSet<long> s_staleShown = [];
    private static long[] s_wanted = [];

    /// <summary>The game windows (as numbers) whose overlay shows a minimap now; set by the overlay, read by the link.</summary>
    internal static long[] Wanted {
        get => System.Threading.Volatile.Read(ref s_wanted);
        set => System.Threading.Volatile.Write(ref s_wanted, value);
    }

    /// <summary>Changes each time this window's map changes (the overlay draws again then).</summary>
    internal static int VersionOf(long window) {
        lock (s_lock) {
            return s_versions.GetValueOrDefault(window);
        }
    }

    /// <summary>The map for this game window; for the designers (0), a made-up one.</summary>
    internal static Snapshot? For(long window) {
        if (window == 0) {
            return Sample;
        }

        lock (s_lock) {
            return s_maps.GetValueOrDefault(window);
        }
    }

    /// <summary>The server's answer to op "map" for one window.</summary>
    internal static void Heard(long window, JsonObject answer) {
        Snapshot? snapshot;
        if (answer["ok"]?.GetValue<bool>() == true && answer["map"] is JsonObject map) {
            snapshot = Read(map);
        }
        else {
            var error = ((string?) answer["error"] ?? "").Trim();
            // Asked too often: the last map stays.
            if (error == "busy") {
                return;
            }

            snapshot = new Snapshot("", 0, 0, 0, [], [], error.Length > 200 ? error[..200] : error, DateTime.UtcNow);
        }

        lock (s_lock) {
            s_maps[window] = snapshot;
            s_staleShown.Remove(window);
            s_versions[window] = s_versions.GetValueOrDefault(window) + 1;
        }
    }

    /// <summary>Once a second: a map that stopped coming is drawn again once, with a note.</summary>
    internal static void Tick() {
        lock (s_lock) {
            foreach (var window in Wanted) {
                var stale = !s_maps.TryGetValue(window, out var map) || map.Stale;
                if (stale && s_staleShown.Add(window)) {
                    s_versions[window] = s_versions.GetValueOrDefault(window) + 1;
                }
            }

            foreach (var gone in s_maps.Keys.Where(w => !Wanted.Contains(w)).ToList()) {
                s_maps.Remove(gone);
                s_staleShown.Remove(gone);
            }
        }
    }

    private static Snapshot Read(JsonObject map) {
        var marks = new List<Mark>();
        if (map["marks"] is JsonArray list) {
            foreach (var item in list.OfType<JsonObject>().Take(500)) {
                marks.Add(new Mark(Short((string?) item["k"], 12), Number(item["x"]), Number(item["y"]), Short((string?) item["n"], 60),
                    Short((string?) item["q"], 8), (int) Number(item["l"])));
            }
        }

        var quests = new List<Quest>();
        if (map["quests"] is JsonArray held) {
            foreach (var item in held.OfType<JsonObject>().Take(30)) {
                quests.Add(new Quest(Short((string?) item["t"], 120), (bool?) item["main"] ?? false, Short((string?) item["step"], 120),
                    Short((string?) item["where"], 80), (bool?) item["here"] ?? false));
            }
        }

        return new Snapshot(Short((string?) map["place"], 80), Number(map["x"]), Number(map["y"]), Number(map["yaw"]), marks, quests, "", DateTime.UtcNow);
    }

    private static double Number(JsonNode? node) {
        try {
            var value = node?.GetValue<double>() ?? 0;

            return double.IsFinite(value) ? value : 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
            return 0;
        }
    }

    private static string Short(string? text, int max) {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

        return text.Length > max ? text[..max] : text;
    }

    // A made-up map for the designers: a bit of everything, around a wizard facing north-east.
    private static readonly Snapshot Sample = new("Unicorn Way", 0, 0, -3 * Math.PI / 4, [
        new Mark("main", 900, 1300, "Talk to Ambrose", "main", 0),
        new Mark("side", -1500, -500, "Defeat 3 Lost Souls", "side", 0),
        new Mark("giver", 300, -700, "Lady Oriel", "", 0),
        new Mark("npc", -400, 600, "Private Stillson", "", 0),
        new Mark("boss", 1200, -1100, "Rattlebones", "", 10),
        new Mark("mob", -900, -300, "Lost Soul", "side", 2),
        new Mark("mob", -1100, -700, "Lost Soul", "side", 2),
        new Mark("mob", 500, 400, "Dark Fairy", "", 3),
        new Mark("sigil", -300, 1500, "Rattlebones's Tower", "", 0),
        new Mark("door", 0, -1800, "To Ravenwood", "", 0),
        new Mark("chest", 700, 900, "Chest", "", 0),
        new Mark("team", -250, -250, "Friend", "", 0),
        new Mark("wizard", 400, 100, "Wizard", "", 0),
    ], [
        new Quest("A Familiar Face", true, "Talk to Ambrose", "", true),
        new Quest("Lost Souls", false, "Defeat 3 Lost Souls", "", true),
        new Quest("Trouble in the Commons", false, "Talk to Mindy Pixiecrumb", "The Commons", false),
    ], "", DateTime.MaxValue);

}

/// <summary>
/// Draws one minimap: the map (round or square, turning with the wizard or north up), its icons, the place's name, and
/// the quest lines under it. Only drawing: everything comes from the <see cref="OverlayMap"/> choices and the server.
/// </summary>
internal sealed class MinimapView : Control {

    private readonly OverlayElement _element;
    private readonly OverlayMap _map;
    private readonly OverlayMinimapData.Snapshot? _data;
    private readonly double _mapW;
    private readonly double _mapH;

    internal MinimapView(OverlayElement element, OverlayMinimapData.Snapshot? data) {
        _element = element;
        _map = element.Map ?? new OverlayMap();
        _data = data;
        _mapW = element.Width > 0 ? element.Width : 200;
        _mapH = element.Height > 0 ? element.Height : 200;
        if (_map.Round) {
            _mapW = _mapH = Math.Min(_mapW, _mapH);
        }

        Width = _mapW;
        Height = _mapH + QuestHeight;
        IsHitTestVisible = false;
    }

    private int QuestCount => Math.Min(_map.QuestLines, _data?.Quests.Count ?? 0);

    private double LineHeight => _map.NameSize + 5;

    private double QuestHeight => QuestCount == 0 ? 0 : 6 + QuestCount * LineHeight + 6;

    public override void Render(DrawingContext context) {
        var area = new Rect(0, 0, _mapW, _mapH);
        var center = area.Center;
        var radiusX = _mapW / 2;
        var radiusY = _mapH / 2;
        var fill = OverlayRenderer.Brush(_element.Background) ?? new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x14, 0x24));
        var edge = _element.BorderWidth > 0 && OverlayRenderer.Parse(_element.BorderColor) is { } edgeColor
            ? new Pen(new SolidColorBrush(edgeColor), _element.BorderWidth) : null;
        var corner = Math.Min(_element.CornerRadius, Math.Min(_mapW, _mapH) / 2);
        Geometry shape = _map.Round ? new EllipseGeometry(area) : new RectangleGeometry(area, corner, corner);
        context.DrawGeometry(fill, null, shape);

        var data = _data;
        var stale = data is null || data.Stale;
        using (context.PushGeometryClip(shape)) {
            // A faint ring half way out, so distances read at a glance.
            var faint = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
            if (_map.Round) {
                context.DrawEllipse(null, faint, center, radiusX / 2, radiusY / 2);
            }
            else {
                context.DrawRectangle(null, faint, new Rect(center.X - radiusX / 2, center.Y - radiusY / 2, radiusX, radiusY));
            }

            // The last map stays under a note while a new one doesn't come.
            if (data is { Note.Length: 0 }) {
                DrawMarks(context, data, center, radiusX, radiusY);
            }
        }

        if (edge is not null) {
            context.DrawGeometry(null, edge, shape);
        }

        if (data is not null && data.Note.Length == 0 && _map.Place && data.Place.Length > 0) {
            Label(context, data.Place, new Point(center.X, _mapH - _map.NameSize - 12), _map.NameSize + 1, Brush(_map.TextColor), centered: true, bold: true, pill: true);
        }

        var note = stale
            ? data is { Note.Length: > 0 } ? data.Note : OverlayHost.Link.Connected ? OverlayHost.Link.SendsMap
                ? "Waiting for the map..." : "This server's Imlight is too old for the minimap. The host needs to update it."
                : "Not linked to a server yet. Start the game with Play in Ahziael."
            : data!.Note;
        if (note.Length > 0) {
            Note(context, note, area);
        }
        else if (QuestCount > 0) {
            DrawQuests(context, data!);
        }
    }

    // Where a spot of the world is drawn: from the wizard, scaled so Zoom reaches the edge, turned with the wizard.
    private Func<double, double, Point> Projector(OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY, out double turn) {
        var scale = Math.Min(radiusX, radiusY) / _map.Zoom;
        var mirror = _map.Mirror ? -1 : 1;
        // A wizard with yaw w faces (-sin w, -cos w) in the world (as the server's street teleporters set it).
        var faceX = -Math.Sin(data.Yaw) * mirror;
        var faceY = Math.Cos(data.Yaw);
        // On the screen, north (+Y) is up; turning with the wizard brings the way they face up.
        turn = _map.Turn ? -Math.PI / 2 - Math.Atan2(faceY, faceX) : 0;
        var (sin, cos) = Math.SinCos(turn);

        return (x, y) => {
            var dx = (x - data.X) * scale * mirror;
            var dy = -(y - data.Y) * scale;

            return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
        };
    }

    private void DrawMarks(DrawingContext context, OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY) {
        var project = Projector(data, center, radiusX, radiusY, out var turn);
        var size = _map.IconSize;

        // The least important first, so quest steps and bosses are on top.
        foreach (var mark in data.Marks.OrderBy(Order)) {
            if (!Shows(mark)) {
                continue;
            }

            var at = project(mark.X, mark.Y);
            var (ox, oy) = ((at.X - center.X) / radiusX, (at.Y - center.Y) / radiusY);
            var outside = _map.Round ? ox * ox + oy * oy > 1 : Math.Abs(ox) > 1 || Math.Abs(oy) > 1;
            var pinned = mark.Kind is "main" or "side" or "team";
            if (outside && !pinned) {
                continue;
            }

            if (outside) {
                // Off the map: an arrow on the edge pointing its way.
                var length = _map.Round ? Math.Sqrt(ox * ox + oy * oy) : Math.Max(Math.Abs(ox), Math.Abs(oy));
                var edgeAt = new Point(center.X + ox / length * (radiusX - size), center.Y + oy / length * (radiusY - size));
                Arrow(context, edgeAt, Math.Atan2(oy, ox) + Math.PI / 2, size * 1.1, Brush(ColorOf(mark)), Outline);
                continue;
            }

            Icon(context, mark, at, size);
            if (_map.Names && mark.Name.Length > 0 && mark.Kind is "main" or "side" or "boss" or "sigil" or "door" or "giver") {
                Label(context, mark.Name, new Point(at.X, at.Y + size * 0.9), _map.NameSize, Brush(_map.TextColor), centered: true);
            }
        }

        // North, on the edge, over the icons.
        var (northSin, northCos) = Math.SinCos(turn);
        var northR = _map.NameSize * 0.85;
        var north = new Point(center.X + (radiusX - northR - 2) * northSin, center.Y - (radiusY - northR - 2) * northCos);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x0A, 0x14)), new Pen(Brush("#FFFF6B6B"), 1), north, northR, northR);
        var n = Text("N", _map.NameSize, Brush("#FFFF6B6B"), true);
        context.DrawText(n, new Point(north.X - n.Width / 2, north.Y - n.Height / 2));

        // You, in the middle: pointing up when the map turns with you, else the way you face.
        var faceX = -Math.Sin(data.Yaw) * (_map.Mirror ? -1 : 1);
        var faceY = Math.Cos(data.Yaw);
        Arrow(context, center, _map.Turn ? 0 : Math.Atan2(faceX, -faceY), size * 1.5, Brush(_map.YouColor), new Pen(Brushes.Black, 1.2));
    }

    private static int Order(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "npc" => 0, "chest" => 1, "door" => 2, "mob" => mark.Quest.Length > 0 ? 4 : 3, "wizard" => 5, "team" => 6, "sigil" => 7, "giver" => 8,
        "boss" => 9, "side" => 10, "main" => 11, _ => 0,
    };

    private bool Shows(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "main" or "side" => _map.Quests,
        "giver" => _map.Givers,
        "npc" => _map.Npcs || (_map.Quests && mark.Quest.Length > 0),
        "mob" => _map.Mobs,
        "boss" => _map.Bosses,
        "sigil" => _map.Sigils,
        "door" => _map.Doors,
        "chest" => _map.Chests,
        "wizard" or "team" => _map.Wizards,
        _ => false,
    };

    private string ColorOf(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "main" => _map.MainColor, "side" => _map.SideColor, "giver" => _map.GiverColor, "npc" => _map.NpcColor, "mob" => _map.MobColor,
        "boss" => _map.BossColor, "sigil" => _map.SigilColor, "door" => _map.DoorColor, "chest" => _map.ChestColor,
        "wizard" => _map.WizardColor, "team" => _map.TeamColor, _ => _map.TextColor,
    };

    private static readonly Pen Outline = new(new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0)), 1);

    private void Icon(DrawingContext context, OverlayMinimapData.Mark mark, Point at, double size) {
        var brush = Brush(ColorOf(mark));
        var r = size / 2;
        switch (mark.Kind) {
            case "main":
                Star(context, at, r * 1.5, brush);
                break;
            case "side":
                Diamond(context, at, r * 1.25, brush);
                break;
            case "giver":
                context.DrawEllipse(brush, Outline, at, r * 1.1, r * 1.1);
                Label(context, "!", new Point(at.X, at.Y - r * 1.05), size * 1.05, Brushes.Black, centered: true, bold: true);
                break;
            case "boss":
                Skull(context, at, r * 1.4, brush);
                break;
            case "mob":
                // A creature a quest wants has a ring in that quest's colour.
                context.DrawEllipse(brush, Outline, at, r * 0.75, r * 0.75);
                if (mark.Quest.Length > 0) {
                    context.DrawEllipse(null, new Pen(Brush(mark.Quest == "main" ? _map.MainColor : _map.SideColor), 1.6), at, r * 1.25, r * 1.25);
                }

                break;
            case "npc":
                context.DrawEllipse(brush, Outline, at, r * 0.55, r * 0.55);
                if (mark.Quest.Length > 0) {
                    context.DrawEllipse(null, new Pen(Brush(mark.Quest == "main" ? _map.MainColor : _map.SideColor), 1.6), at, r * 1.1, r * 1.1);
                }

                break;
            case "sigil":
                context.DrawEllipse(null, new Pen(brush, Math.Max(1.5, size / 5)), at, r * 1.1, r * 1.1);
                context.DrawEllipse(brush, null, at, r * 0.4, r * 0.4);
                break;
            case "door":
                context.DrawRectangle(brush, Outline, new Rect(at.X - r * 0.7, at.Y - r, r * 1.4, r * 2), r * 0.7, r * 0.7);
                break;
            case "chest":
                context.DrawRectangle(brush, Outline, new Rect(at.X - r * 0.9, at.Y - r * 0.6, r * 1.8, r * 1.2), 1.5, 1.5);
                break;
            default:
                // Wizards: a dot with a white ring.
                context.DrawEllipse(brush, new Pen(Brushes.White, 1.2), at, r * 0.75, r * 0.75);
                break;
        }
    }

    private static void Star(DrawingContext context, Point at, double r, IBrush brush) {
        var star = new StreamGeometry();
        using (var g = star.Open()) {
            for (var i = 0; i < 10; i++) {
                var angle = -Math.PI / 2 + i * Math.PI / 5;
                var radius = i % 2 == 0 ? r : r * 0.45;
                var point = new Point(at.X + radius * Math.Cos(angle), at.Y + radius * Math.Sin(angle));
                if (i == 0) {
                    g.BeginFigure(point, true);
                }
                else {
                    g.LineTo(point);
                }
            }

            g.EndFigure(true);
        }

        context.DrawGeometry(brush, Outline, star);
    }

    private static void Diamond(DrawingContext context, Point at, double r, IBrush brush) {
        var diamond = new StreamGeometry();
        using (var g = diamond.Open()) {
            g.BeginFigure(new Point(at.X, at.Y - r), true);
            g.LineTo(new Point(at.X + r * 0.8, at.Y));
            g.LineTo(new Point(at.X, at.Y + r));
            g.LineTo(new Point(at.X - r * 0.8, at.Y));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, Outline, diamond);
    }

    private static void Skull(DrawingContext context, Point at, double r, IBrush brush) {
        // A round head over a jaw, with two dark eyes.
        context.DrawRectangle(brush, Outline, new Rect(at.X - r * 0.5, at.Y + r * 0.1, r, r * 0.75), r * 0.2, r * 0.2);
        context.DrawEllipse(brush, Outline, new Point(at.X, at.Y - r * 0.1), r * 0.85, r * 0.75);
        context.DrawRectangle(brush, null, new Rect(at.X - r * 0.45, at.Y + r * 0.1, r * 0.9, r * 0.45));
        var dark = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x08, 0x10));
        context.DrawEllipse(dark, null, new Point(at.X - r * 0.33, at.Y - r * 0.08), r * 0.22, r * 0.26);
        context.DrawEllipse(dark, null, new Point(at.X + r * 0.33, at.Y - r * 0.08), r * 0.22, r * 0.26);
    }

    // A pointer at a spot, its tip towards angle (0: up), as for you in the middle or a quest off the map.
    private static void Arrow(DrawingContext context, Point at, double angle, double size, IBrush brush, IPen outline) {
        var (sin, cos) = Math.SinCos(angle);
        Point Turned(double x, double y) => new(at.X + x * cos - y * sin, at.Y + x * sin + y * cos);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open()) {
            g.BeginFigure(Turned(0, -size * 0.75), true);
            g.LineTo(Turned(size * 0.55, size * 0.6));
            g.LineTo(Turned(0, size * 0.3));
            g.LineTo(Turned(-size * 0.55, size * 0.6));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, outline, arrow);
    }

    private void DrawQuests(DrawingContext context, OverlayMinimapData.Snapshot data) {
        var top = _mapH + 4;
        var box = new Rect(0, top, _mapW, QuestHeight - 4);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x14, 0x24)), null, box, 6, 6);
        var y = top + 6;
        foreach (var quest in data.Quests.Take(QuestCount)) {
            var color = Brush(quest.Main ? _map.MainColor : _map.SideColor);
            var at = new Point(8, y + LineHeight / 2 - 1);
            if (quest.Main) {
                Star(context, at, _map.NameSize * 0.45, color);
            }
            else {
                Diamond(context, at, _map.NameSize * 0.45, color);
            }

            var words = quest.Step.Length > 0 ? quest.Step : quest.Title;
            if (!quest.Here && quest.Where.Length > 0) {
                words += " (" + quest.Where + ")";
            }

            var text = Text(words, _map.NameSize, Brush(_map.TextColor), false);
            text.MaxTextWidth = Math.Max(10, _mapW - 22);
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(text, new Point(16, y));
            y += LineHeight;
        }
    }

    private void Note(DrawingContext context, string note, Rect area) {
        var text = Text(note, _map.NameSize + 1, Brush(_map.TextColor), true);
        text.MaxTextWidth = Math.Max(20, area.Width * 0.72);
        text.TextAlignment = TextAlignment.Center;
        var at = new Point(area.Center.X - text.MaxTextWidth / 2, area.Center.Y - text.Height / 2);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0x0A, 0x0A, 0x14)), null,
            new Rect(at.X - 6, at.Y - 4, text.MaxTextWidth + 12, text.Height + 8), 6, 6);
        context.DrawText(text, at);
    }

    private void Label(DrawingContext context, string words, Point at, double size, IBrush brush, bool centered = false, bool bold = false, bool pill = false) {
        var text = Text(words, size, brush, bold);
        text.MaxTextWidth = Math.Max(20, _mapW * 0.8);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        // Kept inside the map, so a name near the edge isn't cut off.
        var x = Math.Clamp(centered ? at.X - text.Width / 2 : at.X, 3, Math.Max(3, _mapW - text.Width - 3));
        if (pill) {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB0, 0x0A, 0x0A, 0x14)), null, new Rect(x - 6, at.Y - 2, text.Width + 12, text.Height + 4), 6, 6);
        }
        else {
            // A dark copy under the words, so they read on any background.
            var shadow = Text(words, size, Brushes.Black, bold);
            shadow.MaxTextWidth = text.MaxTextWidth;
            shadow.MaxLineCount = 1;
            shadow.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(shadow, new Point(x + 1, at.Y + 1));
        }

        context.DrawText(text, new Point(x, at.Y));
    }

    private FormattedText Text(string words, double size, IBrush brush, bool bold) {
        var family = string.IsNullOrWhiteSpace(_element.Font) ? FontFamily.Default : new FontFamily(_element.Font);

        return new FormattedText(words, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.SemiBold), Math.Max(4, size), brush);
    }

    private static IBrush Brush(string color) => OverlayRenderer.Parse(color) is { } c ? new SolidColorBrush(c) : Brushes.White;

}
