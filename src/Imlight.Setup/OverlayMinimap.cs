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

    /// <summary>One thing on the map; Id (when the server sends one) is which thing it is, so it glides from map to map.</summary>
    internal sealed record Mark(string Kind, double X, double Y, string Name, int Level, string Id = "");

    /// <summary>
    /// An area's ground from above (Niduus, 2026-11-12: "I want the street actually mapped out in the minimap"). Either
    /// its exact shapes (<see cref="Shapes"/>: the game's own ground triangles and walls, Niduus, 2026-11-12: "Make sure
    /// they are fully accurate") or, from an older server, a grid of cells (0 nothing, 1 path, 2 grass, 3 water, 4 wall)
    /// with its top left corner in the world and each cell's size. Left, Top, Width and Height bound it either way.
    /// </summary>
    internal sealed record Ground(string Zone, double Left, double Top, double Cell, int Width, int Height, byte[] Cells, Shapes? Exact = null) {

        internal bool IsEmpty => Exact is null ? Width == 0 || Cells.Length != Width * Height : Exact.IsEmpty;

        /// <summary>Spots on the ground (paths, grass, water; not walls), for fitting the game's map page to it.</summary>
        internal IEnumerable<(double X, double Y)> Spots() {
            if (Exact is not null) {
                foreach (var kind in GroundKinds) {
                    foreach (var points in Exact.Triangles[kind..(kind + 1)].Concat(Exact.Outlines[kind])) {
                        for (var i = 0; i + 1 < points.Length; i += 2) {
                            yield return (points[i], points[i + 1]);
                        }
                    }
                }

                yield break;
            }

            for (var row = 0; row < Height; row += 2) {
                for (var col = 0; col < Width; col += 2) {
                    if (Cells[(row * Width) + col] is 1 or 2 or 3) {
                        yield return (Left + (col * Cell), Top - (row * Cell));
                    }
                }
            }
        }

    }

    /// <summary>The kinds that are ground (paths, grass, water, and paths and grass above water).</summary>
    internal static readonly int[] GroundKinds = [1, 2, 3, 6, 7, 8];

    /// <summary>
    /// The shapes of an area's ground, in world units: for each kind (1 path, 2 grass, 3 water, 4 wall, 6 path and
    /// 7 grass above water such as bridges and banks) its triangles
    /// as x, y pairs, three a triangle, or its smoothed outlines (x, y pairs each, filled even-odd: holes are outlines
    /// too); and its round things (trees, posts, pillars) as x, y, radius.
    /// </summary>
    internal sealed record Shapes(float[][] Triangles, float[][][] Outlines, float[] Rounds) {

        internal bool IsEmpty => Triangles.All(t => t.Length == 0) && Outlines.All(o => o.Length == 0);

    }

    internal sealed record Snapshot(string Place, double X, double Y, double Yaw, IReadOnlyList<Mark> Marks, string Note,
                                    DateTime At, Ground? Ground = null, string Zone = "") {

        internal bool Stale => DateTime.UtcNow - At > s_staleAfter;

    }

    private static readonly TimeSpan s_staleAfter = TimeSpan.FromSeconds(6);
    private static readonly object s_lock = new();
    private static readonly Dictionary<long, Snapshot> s_maps = [];
    private static readonly Dictionary<long, Snapshot> s_previous = [];
    private static readonly Dictionary<long, Ground> s_grounds = [];
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

    /// <summary>The area whose streets this window has (sent with op "map", so the server sends them only once per area).</summary>
    internal static string GroundZone(long window) {
        lock (s_lock) {
            return s_grounds.TryGetValue(window, out var ground) ? ground.Zone : "";
        }
    }

    /// <summary>Which streets this window has (area and smoothing): the server sends them again when its smoothing changes.</summary>
    internal static string GroundKey(long window) {
        lock (s_lock) {
            return s_groundKeys.GetValueOrDefault(window, "");
        }
    }

    private static readonly Dictionary<long, string> s_groundKeys = [];

    /// <summary>The server's answer to op "map" for one window.</summary>
    internal static void Heard(long window, JsonObject answer) {
        Snapshot? snapshot;
        if (answer["ok"]?.GetValue<bool>() == true && answer["map"] is JsonObject map) {
            if (map["ground"] is JsonObject sent && ReadGround(sent) is { } ground) {
                lock (s_lock) {
                    s_grounds[window] = ground;
                    s_groundKeys[window] = Short((string?) sent["key"], 260) is { Length: > 0 } key ? key : ground.Zone;
                }
            }

            snapshot = Read(map);
            // The Quest Tracker's picked quest: its way comes with the map (drawn by OverlayQuestRouteDrawing).
            OverlayQuestData.HeardMap(window, map);
            var zone = Short((string?) map["zone"], 200);
            lock (s_lock) {
                if (s_grounds.TryGetValue(window, out var known) && string.Equals(known.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
                    snapshot = snapshot with { Ground = known };
                }
            }
        }
        else {
            var error = ((string?) answer["error"] ?? "").Trim();
            // Asked too often: the last map stays.
            if (error == "busy") {
                return;
            }

            snapshot = new Snapshot("", 0, 0, 0, [], error.Length > 200 ? error[..200] : error, DateTime.UtcNow);
        }

        lock (s_lock) {
            // The last map stays as where the glide starts from (OverlayMinimapData.Shown).
            if (s_maps.TryGetValue(window, out var before) && before.Note.Length == 0 && snapshot.Note.Length == 0) {
                s_previous[window] = before;
            }
            else {
                s_previous.Remove(window);
            }

            s_maps[window] = snapshot;
            s_staleShown.Remove(window);
            s_versions[window] = s_versions.GetValueOrDefault(window) + 1;
        }
    }

    /// <summary>
    /// The map as it is drawn right now: the wizard glides from the last map's spot and facing to the newest over the
    /// time between the two (Niduus, 2026-11-12: updates were "shotty"), so the map moves smoothly a little behind.
    /// </summary>
    internal static Snapshot? Shown(long window) {
        if (window == 0) {
            return Sample;
        }

        lock (s_lock) {
            if (!s_maps.TryGetValue(window, out var now)) {
                return null;
            }

            if (!s_previous.TryGetValue(window, out var before) || Glide(before, now) is not { } gap) {
                return now;
            }

            var t = Math.Clamp((DateTime.UtcNow - now.At).TotalSeconds / gap, 0, 1);
            var turn = Math.IEEERemainder(now.Yaw - before.Yaw, 2 * Math.PI);
            // Creatures, NPCs and other wizards glide too, each from where it was on the last map (Niduus, 2026-11-12:
            // "choppy live updates of mobs. I want them smooth"). One that jumped far (a teleport) is just put there.
            var was = new Dictionary<string, Mark>(before.Marks.Count);
            foreach (var mark in before.Marks) {
                if (mark.Id.Length > 0) {
                    was.TryAdd(mark.Id, mark);
                }
            }

            var marks = was.Count == 0 || t >= 1 ? now.Marks : now.Marks.Select(m =>
                m.Id.Length > 0 && was.TryGetValue(m.Id, out var from) && Math.Abs(m.X - from.X) + Math.Abs(m.Y - from.Y) < 1500
                    ? m with { X = from.X + ((m.X - from.X) * t), Y = from.Y + ((m.Y - from.Y) * t) } : m).ToList();

            return now with {
                X = before.X + ((now.X - before.X) * t), Y = before.Y + ((now.Y - before.Y) * t), Yaw = before.Yaw + (turn * t), Marks = marks,
            };
        }
    }

    /// <summary>Whether this window's minimap is still gliding to its newest map (it is drawn again often until then).</summary>
    internal static bool Moving(long window) {
        lock (s_lock) {
            return s_maps.TryGetValue(window, out var now) && s_previous.TryGetValue(window, out var before) && Glide(before, now) is { } gap
                   && (DateTime.UtcNow - now.At).TotalSeconds < gap + 0.05;
        }
    }

    /// <summary>What lays the overlay out again when it changes: whether the map is full screen, and whether it shows a note.</summary>
    internal static (bool Full, bool Note) LayoutOf(long window) {
        lock (s_lock) {
            return (s_full.ContainsKey(window), s_maps.TryGetValue(window, out var now) && now.Note.Length > 0);
        }
    }

    // The game windows whose map is full screen, with how far it reaches (Niduus, 2026-11-12: "Allow a full screen button").
    private static readonly Dictionary<long, double> s_full = [];

    /// <summary>How far (game units) this window's full screen map reaches from the wizard to its nearer edge, or null when it isn't full screen.</summary>
    internal static double? FullZoom(long window) {
        lock (s_lock) {
            return s_full.TryGetValue(window, out var zoom) ? zoom : null;
        }
    }

    /// <summary>The map of this game window goes full screen, or back to the corner.</summary>
    internal static void ToggleFull(long window, double startZoom) {
        lock (s_lock) {
            if (!s_full.Remove(window)) {
                s_full[window] = Math.Clamp(startZoom, MIN_ZOOM, MAX_ZOOM);
            }

            s_versions[window] = s_versions.GetValueOrDefault(window) + 1;
        }
    }

    /// <summary>The full screen map shows closer (in) or farther (out).</summary>
    internal static void ZoomFull(long window, bool closer, double step = 1.5, double min = MIN_ZOOM, double max = MAX_ZOOM) {
        lock (s_lock) {
            if (s_full.TryGetValue(window, out var zoom)) {
                s_full[window] = Math.Clamp(closer ? zoom / step : zoom * step, Math.Min(min, max), max);
                s_versions[window] = s_versions.GetValueOrDefault(window) + 1;
            }
        }
    }

    internal const double MIN_ZOOM = 300;
    internal const double MAX_ZOOM = 30000;

    // Seconds to glide over, or null when there is nothing to glide (no move, a jump, another area, a long gap).
    private static double? Glide(Snapshot before, Snapshot now) {
        var gap = (now.At - before.At).TotalSeconds;
        var far = Math.Sqrt(((now.X - before.X) * (now.X - before.X)) + ((now.Y - before.Y) * (now.Y - before.Y)));
        var turned = Math.Abs(Math.IEEERemainder(now.Yaw - before.Yaw, 2 * Math.PI));
        // Glides even when the wizard stands still: the creatures around may be walking.
        if (gap is <= 0.02 or > 1.5 || far > 1500 || turned > Math.PI * 2 || !string.Equals(before.Zone, now.Zone, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return gap;
    }

    /// <summary>Every window's minimap is drawn again (the game's map page of its area is ready).</summary>
    internal static void Redraw() {
        lock (s_lock) {
            foreach (var window in s_versions.Keys.ToList()) {
                s_versions[window]++;
            }
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
                s_previous.Remove(gone);
                s_grounds.Remove(gone);
                s_groundKeys.Remove(gone);
                s_staleShown.Remove(gone);
                s_full.Remove(gone);
            }
        }
    }

    // The streets as the server packs them: runs of (kind, 16-bit count), base64.
    private static Ground? ReadGround(JsonObject sent) {
        var zone = Short((string?) sent["zone"], 200);
        var (width, height) = ((int) Number(sent["w"]), (int) Number(sent["h"]));
        if (zone.Length == 0) {
            return null;
        }

        if (sent["shapes"] is not null) {
            return ReadShapes(zone, sent);
        }

        if (width <= 0 || height <= 0 || width > 1100 || height > 1100) {
            return new Ground(zone, 0, 0, 0, 0, 0, []);
        }

        try {
            var runs = Convert.FromBase64String((string?) sent["runs"] ?? "");
            var cells = new byte[width * height];
            var at = 0;
            for (var i = 0; i + 2 < runs.Length && at < cells.Length; i += 3) {
                var count = Math.Min(runs[i + 1] | (runs[i + 2] << 8), cells.Length - at);
                var kind = runs[i] <= 4 ? runs[i] : (byte) 0;
                Array.Fill(cells, kind, at, count);
                at += count;
            }

            var cell = Number(sent["cell"]);
            return cell > 0 ? new Ground(zone, Number(sent["left"]), Number(sent["top"]), cell, width, height, cells) : new Ground(zone, 0, 0, 0, 0, 0, []);
        }
        catch (FormatException) {
            return new Ground(zone, 0, 0, 0, 0, 0, []);
        }
    }

    // The exact shapes as the server packs them (ZoneGround.Outline): blocks of a kind byte and a 32-bit count, then
    // 16-bit steps of "unit" from (left, bottom): six a triangle, three a round thing.
    private static Ground ReadShapes(string zone, JsonObject sent) {
        var none = new Ground(zone, 0, 0, 0, 0, 0, []);
        try {
            var data = Convert.FromBase64String((string?) sent["shapes"] ?? "");
            var (left, bottom, unit) = (Number(sent["left"]), Number(sent["bottom"]), Number(sent["unit"]));
            if (unit <= 0 || data.Length < 5) {
                return none;
            }

            var triangles = new List<float>[10];
            var outlines = new List<float[]>[10];
            for (var k = 0; k < 10; k++) {
                triangles[k] = [];
                outlines[k] = [];
            }

            var rounds = new List<float>();
            var at = 0;
            float X(int i) => (float) (left + (BitConverter.ToUInt16(data, i) * unit));
            float Y(int i) => (float) (bottom + (BitConverter.ToUInt16(data, i) * unit));
            while (at + 5 <= data.Length) {
                var kind = data[at];
                var count = (int) Math.Min(BitConverter.ToUInt32(data, at + 1), 2_000_000u);
                at += 5;
                // Smoothed outlines (kind + 16): each its own count of points.
                if (kind is >= 17 and <= 25 and not 21) {
                    for (var n = 0; n < count && at + 2 <= data.Length; n++) {
                        var points = BitConverter.ToUInt16(data, at);
                        at += 2;
                        if (at + (points * 4) > data.Length) {
                            at = data.Length;
                            break;
                        }

                        var outline = new float[points * 2];
                        for (var p = 0; p < points; p++, at += 4) {
                            outline[p * 2] = X(at);
                            outline[(p * 2) + 1] = Y(at + 2);
                        }

                        if (points >= 3) {
                            outlines[kind - 16].Add(outline);
                        }
                    }

                    continue;
                }

                var each = kind == 5 ? 6 : 12;
                if (kind is < 1 or > 8 || at + ((long) count * each) > data.Length) {
                    break;
                }

                for (var n = 0; n < count; n++, at += each) {
                    if (kind == 5) {
                        rounds.Add(X(at));
                        rounds.Add(Y(at + 2));
                        rounds.Add((float) (BitConverter.ToUInt16(data, at + 4) * unit));
                    }
                    else {
                        for (var p = 0; p < 3; p++) {
                            triangles[kind].Add(X(at + (p * 4)));
                            triangles[kind].Add(Y(at + (p * 4) + 2));
                        }
                    }
                }
            }

            var shapes = new Shapes([.. triangles.Select(t => t.ToArray())], [.. outlines.Select(o => o.ToArray())], [.. rounds]);
            if (shapes.IsEmpty) {
                return none;
            }

            // Bounds of the ground (for the game's map page): paths, grass and water.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var points in GroundKinds.SelectMany(kind => shapes.Outlines[kind].Prepend(shapes.Triangles[kind]))) {
                for (var i = 0; i + 1 < points.Length; i += 2) {
                    (minX, maxX) = (Math.Min(minX, points[i]), Math.Max(maxX, points[i]));
                    (minY, maxY) = (Math.Min(minY, points[i + 1]), Math.Max(maxY, points[i + 1]));
                }
            }

            if (minX >= maxX || minY >= maxY) {
                (minX, maxX, minY, maxY) = (left, left + 1, bottom, bottom + 1);
            }

            return new Ground(zone, minX, maxY, 1, (int) Math.Ceiling(maxX - minX), (int) Math.Ceiling(maxY - minY), [], shapes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) {
            return none;
        }
    }

    private static Snapshot Read(JsonObject map) {
        var marks = new List<Mark>();
        if (map["marks"] is JsonArray list) {
            foreach (var item in list.OfType<JsonObject>().Take(500)) {
                marks.Add(new Mark(Short((string?) item["k"], 12), Number(item["x"]), Number(item["y"]), Short((string?) item["n"], 60),
                    (int) Number(item["l"]), Short((string?) item["i"], 40)));
            }
        }

        return new Snapshot(Short((string?) map["place"], 80), Number(map["x"]), Number(map["y"]), Number(map["yaw"]), marks, "", DateTime.UtcNow,
            Zone: Short((string?) map["zone"], 200));
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
        new Mark("npc", 300, -700, "Lady Oriel", 0),
        new Mark("npc", -400, 600, "Private Stillson", 0),
        new Mark("boss", 1200, -1100, "Rattlebones", 10),
        new Mark("mob", -900, -300, "Lost Soul", 2),
        new Mark("mob", -1100, -700, "Lost Soul", 2),
        new Mark("mob", 500, 400, "Dark Fairy", 3),
        new Mark("sigil", -300, 1500, "Rattlebones's Tower", 0),
        new Mark("door", 0, -1800, "To Ravenwood", 0),
        new Mark("chest", 700, 900, "Chest", 0),
        new Mark("team", -250, -250, "Friend", 0),
        new Mark("wizard", 400, 100, "Wizard", 0),
    ], "", DateTime.MaxValue, SampleGround());

    // A made-up street for the designers: a crossroads with a plaza, grass around, a pond and a few walls.
    private static Ground SampleGround() {
        const int size = 160;
        const double cell = 40;
        var cells = new byte[size * size];
        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var (wx, wy) = (-3200 + x * cell, 3200 - y * cell);
                byte kind = 2;
                if (Math.Abs(wx) < 260 || Math.Abs(wy + 200) < 260 || Math.Sqrt(wx * wx + wy * wy) < 700
                    || Math.Abs(wx - wy * 0.6 - 900) < 200 && wx > 0) {
                    kind = 1;
                }

                if (Math.Sqrt((wx + 1600) * (wx + 1600) + (wy - 1200) * (wy - 1200)) < 420) {
                    kind = 3;
                }

                if ((Math.Abs(wx) is > 330 and < 420 && Math.Abs(wy) is > 900 and < 2600) || (Math.Abs(wy - 1700) < 50 && wx is > 600 and < 2400)) {
                    kind = 4;
                }

                cells[y * size + x] = Math.Abs(wx) > 3000 || Math.Abs(wy) > 3000 ? (byte) 0 : kind;
            }
        }

        return new Ground("sample", -3200, 3200, cell, size, size, cells);
    }

}

/// <summary>
/// Draws one minimap: the map (round or square, turning with the wizard or north up), its icons and the place's name.
/// Only drawing: everything comes from the <see cref="OverlayMap"/> choices and the server. Quests are not shown here
/// (Niduus, 2026-11-12: "Do not track quests. That will be a different plugin"). Full screen (<paramref name="full"/>),
/// it fills the space it is given, square and north up, reaching as far as the window's full screen zoom.
/// </summary>
internal sealed class MinimapView : Control {

    private readonly OverlayElement _element;
    private readonly OverlayMap _map;
    private readonly OverlayMinimapData.Snapshot? _data;
    private readonly long _window;
    private readonly bool _full;
    private readonly double _fixedW;
    private readonly double _fixedH;

    internal MinimapView(OverlayElement element, OverlayMinimapData.Snapshot? data, long window = 0, bool full = false) {
        _element = element;
        _map = element.Map ?? new OverlayMap();
        _data = data;
        _window = window;
        _full = full;
        if (full) {
            _map = _map.Copy();
            _map.Round = false;
            _map.Turn = false;
        }

        _fixedW = element.Width > 0 ? element.Width : 200;
        _fixedH = element.Height > 0 ? element.Height : 200;
        if (_map.Round) {
            _fixedW = _fixedH = Math.Min(_fixedW, _fixedH);
        }

        if (!full) {
            Width = _fixedW;
            Height = _fixedH;
        }

        IsHitTestVisible = false;
    }

    private double _mapW => _full ? Math.Max(50, Bounds.Width) : _fixedW;

    private double _mapH => _full ? Math.Max(50, Bounds.Height) : _fixedH;

    // How far the map reaches from the wizard to its nearer edge: the minimap's Zoom, or the full screen map's own.
    private double Zoom => _full ? OverlayMinimapData.FullZoom(_window) ?? (_map.FullZoom > 0 ? _map.FullZoom : _map.Zoom * 2.5) : _map.Zoom;

    public override void Render(DrawingContext context) {
        var area = new Rect(0, 0, _mapW, _mapH);
        var center = area.Center;
        var radiusX = _mapW / 2;
        var radiusY = _mapH / 2;
        // Full screen it covers the game, so it gets a solid dark background.
        var fill = _full ? Brush(_map.FullBackground)
            : OverlayRenderer.Brush(_element.Background) ?? new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x14, 0x24));
        var edge = _element.BorderWidth > 0 && OverlayRenderer.Parse(_element.BorderColor) is { } edgeColor
            ? new Pen(new SolidColorBrush(edgeColor), _element.BorderWidth) : null;
        var corner = Math.Min(_element.CornerRadius, Math.Min(_mapW, _mapH) / 2);
        Geometry shape = _map.Round ? new EllipseGeometry(area) : new RectangleGeometry(area, corner, corner);
        context.DrawGeometry(fill, null, shape);

        // Read again each time it is drawn, so the overlay can draw just the minimap again while it glides.
        var data = _window != 0 ? (_map.Glide ? OverlayMinimapData.Shown(_window) : OverlayMinimapData.For(_window)) ?? _data : _data;
        var stale = data is null || data.Stale;
        using (context.PushGeometryClip(shape)) {
            // A faint ring half way out, so distances read at a glance.
            var faint = new Pen(Brush(_map.RingColor), 1);
            if (!_map.ShowRing) {
            }
            else if (_map.Round) {
                context.DrawEllipse(null, faint, center, radiusX / 2, radiusY / 2);
            }
            else if (!_full) {
                context.DrawRectangle(null, faint, new Rect(center.X - radiusX / 2, center.Y - radiusY / 2, radiusX, radiusY));
            }

            if (data is { Note.Length: 0, Zone.Length: > 0 } && _map.GamePicture) {
                DrawGamePicture(context, data, center, radiusX, radiusY);
            }

            if (data is { Note.Length: 0, Ground: { IsEmpty: false } ground } && _map.Streets) {
                DrawGround(context, data, ground, center, radiusX, radiusY);
            }

            // The quest picked in the Quest Tracker: its way, turned and zoomed with the map.
            if (data is { Note.Length: 0 } && _map.Grid) {
                DrawGrid(context, data, center, radiusX, radiusY);
            }

            if (data is { Note.Length: 0 }) {
                var project = Projector(data, center, radiusX, radiusY, out _);
                OverlayQuestRouteDrawing.Draw(context, _window, project, project(data.X, data.Y), _mapW, _mapH, _map.IconSize);
            }

            // The place's name at the bottom, under the icons (the N goes over it).
            if (data is { Note.Length: 0 } && _map.Place && data.Place.Length > 0) {
                var top = _map.PlaceAt == "Top" ? (_map.ShowNorth ? _map.NameSize * 2.2 + 6 : 8) : _mapH - _map.NameSize - 12;
                // The N marker comes round to the bottom as the map turns: the name moves up over it, not under it
                // (Niduus, 2026-11-12: "Northguard" was covered by the N).
                Projector(data, center, radiusX, radiusY, out var placeTurn);
                if (_map.PlaceAt != "Top" && _map.ShowNorth && Math.Cos(placeTurn) < -0.6) {
                    top -= _map.NameSize * 2.4;
                }
                else if (_map.PlaceAt == "Top" && _map.ShowNorth && Math.Cos(placeTurn) < 0.6) {
                    top = 8;
                }
                Label(context, data.Place, new Point(center.X, top), _map.NameSize + 1, Brush(_map.TextColor), centered: true, bold: true, pill: true);
            }

            // The last map stays under a note while a new one doesn't come.
            if (data is { Note.Length: 0 }) {
                DrawMarks(context, data, center, radiusX, radiusY);
            }
        }

        if (edge is not null) {
            context.DrawGeometry(null, edge, shape);
        }


        var note = stale
            ? data is { Note.Length: > 0 } ? data.Note : OverlayHost.Link.Connected ? OverlayHost.Link.SendsMap
                ? "Waiting for the map..." : "This server's Imlight is too old for the minimap. The host needs to update it."
                : "Not linked to a server yet. Start the game with Play in Ahziael."
            : data!.Note;
        if (note.Length > 0) {
            Note(context, note, area);
        }
    }

    // Where a spot of the world is drawn: from the wizard, scaled so Zoom reaches the edge, turned with the wizard.
    private Func<double, double, Point> Projector(OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY, out double turn) {
        var scale = Math.Min(radiusX, radiusY) / Zoom;
        var mirror = _map.Mirror ? -1 : 1;
        // A wizard with yaw w faces (-sin w, -cos w) in the world (as the server's street teleporters set it): on the
        // screen (y down) that is (-sin w, cos w), mirrored or not.
        var faceX = -Math.Sin(data.Yaw) * mirror;
        var faceY = Math.Cos(data.Yaw);
        // North (+Y) is up; turning with the wizard brings the way they face up.
        turn = _map.Turn ? -Math.PI / 2 - Math.Atan2(faceY, faceX) : 0;
        var (sin, cos) = Math.SinCos(turn);

        return (x, y) => {
            var dx = (x - data.X) * scale * mirror;
            var dy = -(y - data.Y) * scale;

            return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
        };
    }

    // The area's streets, grass, water and walls, placed, scaled and turned like the icons: the exact shapes when the
    // server sent them, else the grid as one picture.
    private void DrawGround(DrawingContext context, OverlayMinimapData.Snapshot data, OverlayMinimapData.Ground ground, Point center, double radiusX, double radiusY) {
        Projector(data, center, radiusX, radiusY, out var turn);
        var scale = Math.Min(radiusX, radiusY) / Zoom;
        var mirror = _map.Mirror ? -1 : 1;
        var placed = Matrix.CreateScale(scale * mirror, scale) * Matrix.CreateRotation(turn) * Matrix.CreateTranslation(center.X, center.Y);
        if (ground.Exact is { } exact) {
            // Shapes are kept as (x, -y): east right, north up. Moved so the wizard is at 0, 0.
            var matrix = Matrix.CreateTranslation(-data.X, data.Y) * placed;
            var colors = new[] { "", _map.PathColor, _map.GrassColor, _map.WaterColor, _map.WallColor, _map.TreeColor, _map.PathColor, _map.GrassColor, _map.WoodColor, _map.PathColor };
            var geometry = Outlines(exact);
            // Each kind is one filled outline, so the triangles in it leave no seams and nothing is drawn twice.
            using (context.PushTransform(matrix)) {
                // Grass, paths, water over them, bridges and banks above the water, wooden docks and decks, then walls,
                // trees and posts.
                // All the ground first, in the streets' colour, so no crack between two kinds shows through.
                foreach (var kind in new[] { 9, 2, 1, 3, 7, 6, 8, 4, 5 }) {
                    if ((kind == 4 && !_map.Walls) || (kind == 5 && !_map.Trees)) {
                        continue;
                    }

                    if (geometry[kind] is { } shape && OverlayRenderer.Parse(colors[kind]) is { A: > 0 } color) {
                        context.DrawGeometry(new SolidColorBrush(color, _map.StreetsOpacity), null, shape);
                    }
                }

                // A thin line round all the ground you can walk on.
                if (_map.GroundEdge && geometry[9] is { } edge && OverlayRenderer.Parse(_map.GroundEdgeColor) is { A: > 0 } edgeColor) {
                    context.DrawGeometry(null, new Pen(new SolidColorBrush(edgeColor), 1.5 / Math.Max(1e-6, scale)), edge);
                }
            }

            return;
        }

        if (GroundPicture(ground) is not { } picture) {
            return;
        }

        // In the wizard's frame (east right, north up), then sized, mirrored, turned and moved to the middle.
        var area = new Rect(ground.Left - data.X, -(ground.Top - data.Y), ground.Width * ground.Cell, ground.Height * ground.Cell);
        using (context.PushTransform(placed))
        using (context.PushOpacity(_map.StreetsOpacity)) {
            RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            context.DrawImage(picture, new Rect(picture.Size), area);
        }
    }

    // Each area's shapes as one outline a kind (1 path, 2 grass, 3 water, 4 walls, 5 round things), made once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OverlayMinimapData.Shapes, Geometry?[]> s_outlines = new();

    private static Geometry?[] Outlines(OverlayMinimapData.Shapes shapes) => s_outlines.GetValue(shapes, made => {
        var outlines = new Geometry?[10];
        foreach (var kind in new[] { 1, 2, 3, 4, 6, 7, 8, 9 }) {
            var points = made.Triangles[kind];
            var smooth = made.Outlines[kind];
            if (points.Length < 6 && smooth.Length == 0) {
                continue;
            }

            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                // Smoothed outlines: holes are outlines too, so even-odd. Triangles overlap: non-zero.
                g.SetFillRule(smooth.Length > 0 ? FillRule.EvenOdd : FillRule.NonZero);
                foreach (var outline in smooth) {
                    g.BeginFigure(new Point(outline[0], -outline[1]), true);
                    for (var i = 2; i + 1 < outline.Length; i += 2) {
                        g.LineTo(new Point(outline[i], -outline[i + 1]));
                    }

                    g.EndFigure(true);
                }

                for (var i = 0; i + 5 < points.Length; i += 6) {
                    g.BeginFigure(new Point(points[i], -points[i + 1]), true);
                    g.LineTo(new Point(points[i + 2], -points[i + 3]));
                    g.LineTo(new Point(points[i + 4], -points[i + 5]));
                    g.EndFigure(true);
                }
            }

            outlines[kind] = geometry;
        }

        if (made.Rounds.Length >= 3) {
            var group = new GeometryGroup { FillRule = FillRule.NonZero };
            for (var i = 0; i + 2 < made.Rounds.Length; i += 3) {
                var r = made.Rounds[i + 2];
                group.Children.Add(new EllipseGeometry(new Rect(made.Rounds[i] - r, -made.Rounds[i + 1] - r, r * 2, r * 2)));
            }

            outlines[5] = group;
        }

        return outlines;
    });

    // The game's own map page of the area (OverlayGameMap), turned and sized with the icons.
    private void DrawGamePicture(DrawingContext context, OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY) {
        if (OverlayGameMap.For(data.Zone, data.Ground) is not { } page) {
            return;
        }

        Projector(data, center, radiusX, radiusY, out var turn);
        var scale = Math.Min(radiusX, radiusY) / Zoom;
        var mirror = _map.Mirror ? -1 : 1;
        // Page pixel -> the wizard's frame (east right, north up: u = x - X, v = -(y - Y)), then as the streets.
        var toWizard = new Matrix(page.A, -page.D, page.B, -page.E, page.C - data.X, data.Y - page.F);
        var matrix = toWizard * Matrix.CreateScale(scale * mirror, scale) * Matrix.CreateRotation(turn) * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(matrix))
        using (context.PushOpacity(_map.GamePictureOpacity)) {
            RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            context.DrawImage(page.Bitmap, page.Inside, page.Inside);
        }
    }

    // Each area's picture in this map's colours, made once.
    private static readonly Dictionary<(OverlayMinimapData.Ground, string), Avalonia.Media.Imaging.WriteableBitmap> s_pictures = [];

    private Avalonia.Media.Imaging.WriteableBitmap? GroundPicture(OverlayMinimapData.Ground ground) {
        var colors = string.Join(",", _map.PathColor, _map.GrassColor, _map.WaterColor, _map.WallColor);
        lock (s_pictures) {
            if (s_pictures.TryGetValue((ground, colors), out var known)) {
                return known;
            }

            if (s_pictures.Count > 12) {
                foreach (var old in s_pictures.Values) {
                    old.Dispose();
                }

                s_pictures.Clear();
            }

            uint Pixel(string text) {
                if (OverlayRenderer.Parse(text) is not { } c) {
                    return 0;
                }

                // Premultiplied BGRA.
                var a = c.A / 255.0;
                return (uint) c.A << 24 | (uint) Math.Round(c.R * a) << 16 | (uint) Math.Round(c.G * a) << 8 | (uint) Math.Round(c.B * a);
            }

            var palette = new[] { 0u, Pixel(_map.PathColor), Pixel(_map.GrassColor), Pixel(_map.WaterColor), Pixel(_map.WallColor) };
            var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(ground.Width, ground.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var buffer = bitmap.Lock()) {
                var row = new int[ground.Width];
                for (var y = 0; y < ground.Height; y++) {
                    for (var x = 0; x < ground.Width; x++) {
                        row[x] = unchecked((int) palette[ground.Cells[y * ground.Width + x]]);
                    }

                    System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, ground.Width);
                }
            }

            s_pictures[(ground, colors)] = bitmap;

            return bitmap;
        }
    }

    private void DrawMarks(DrawingContext context, OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY) {
        var project = Projector(data, center, radiusX, radiusY, out var turn);
        var size = _map.IconSize;
        var unit = Math.Min(radiusX, radiusY) / Zoom;
        if (_map.MobAreas) {
            DrawMobAreas(context, data, project, center, radiusX, radiusY, unit);
        }

        var names = new List<(string Words, Point At, int Rank, IBrush Brush)>();

        // The least important first, so bosses and sigils are on top.
        foreach (var mark in data.Marks.OrderBy(Order)) {
            if (!Shows(mark)) {
                continue;
            }

            var at = project(mark.X, mark.Y);
            var (ox, oy) = ((at.X - center.X) / radiusX, (at.Y - center.Y) / radiusY);
            var outside = _map.Round ? ox * ox + oy * oy > 1 : Math.Abs(ox) > 1 || Math.Abs(oy) > 1;
            var pinned = mark.Kind is "team" && _map.EdgeArrows;
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

            var own = size * SizeOf(mark);
            Icon(context, mark, at, own);
            if (mark.Kind == "boss" && _map.BossGlow) {
                context.DrawEllipse(null, new Pen(Brush(_map.BossColor), Math.Max(1.2, size / 6)), at, own * 1.25, own * 1.25);
            }

            if (mark.Name.Length > 0 && Named(mark)) {
                var words = _map.Levels && mark.Level > 0 && mark.Kind is "mob" or "boss" ? $"{mark.Name} ({mark.Level})" : mark.Name;
                names.Add((words, new Point(at.X, at.Y + own * 0.9), Order(mark), _map.NamesInKindColor ? Brush(ColorOf(mark)) : Brush(_map.TextColor)));
            }
        }

        // Names last, the most important first; with Tidy names, one that would cover another is tried above its icon,
        // then left out (Niduus, 2026-11-12: names piled on top of each other).
        var taken = new List<Rect>();
        foreach (var (words, at, _, nameBrush) in names.OrderByDescending(n => n.Rank)) {
            var text = Text(words, _map.NameSize, Brush(_map.TextColor), false);
            var (w, h) = (Math.Min(text.Width, Math.Max(20, _mapW * 0.8)), text.Height);
            Rect Box(Point p) => new(p.X - w / 2 - 4, p.Y - 1, w + 8, h + 2);
            var place = at;
            if (_map.TidyNames) {
                var above = new Point(at.X, at.Y - h - size * 1.6);
                if (taken.Any(r => r.Intersects(Box(place)))) {
                    place = above;
                    if (taken.Any(r => r.Intersects(Box(place)))) {
                        continue;
                    }
                }
            }

            taken.Add(Box(place));
            Label(context, words, place, _map.NameSize, nameBrush, centered: true, pill: _map.NameBackground);
        }

        // North, on the edge, over the icons.
        if (_map.ShowNorth) {
            var (northSin, northCos) = Math.SinCos(turn);
            var northR = _map.NameSize * 0.85;
            var north = new Point(center.X + (radiusX - northR - 2) * northSin, center.Y - (radiusY - northR - 2) * northCos);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE0, 0x0A, 0x0A, 0x14)), new Pen(Brush(_map.NorthColor), 1), north, northR, northR);
            var n = Text("N", _map.NameSize, Brush(_map.NorthColor), true);
            Words(context, n, new Point(north.X - n.Width / 2, north.Y - n.Height / 2), Brush(_map.NorthColor));
        }

        // You, in the middle: pointing up when the map turns with you, else the way you face.
        var faceX = -Math.Sin(data.Yaw) * (_map.Mirror ? -1 : 1);
        var faceY = Math.Cos(data.Yaw);
        var facing = _map.Turn ? 0 : Math.Atan2(faceX, -faceY);
        if (_map.ViewCone) {
            // A soft wedge the way you face, 70 degrees wide.
            var reach = size * _map.YouSize * 5;
            var wedge = new StreamGeometry();
            using (var g = wedge.Open()) {
                g.BeginFigure(center, true);
                for (var k = 0; k <= 12; k++) {
                    var a = facing - (35 * Math.PI / 180) + (k * 70 * Math.PI / 180 / 12);
                    g.LineTo(new Point(center.X + reach * Math.Sin(a), center.Y - reach * Math.Cos(a)));
                }

                g.EndFigure(true);
            }

            context.DrawGeometry(Brush(_map.ViewConeColor), null, wedge);
        }

        Arrow(context, center, facing, size * _map.YouSize, Brush(_map.YouColor), new Pen(Brushes.Black, 1.2));
    }

    // Grid lines every GridSize game units, east-west and north-south, turned with the map.
    private void DrawGrid(DrawingContext context, OverlayMinimapData.Snapshot data, Point center, double radiusX, double radiusY) {
        var project = Projector(data, center, radiusX, radiusY, out _);
        var scale = Math.Min(radiusX, radiusY) / Zoom;
        var step = _map.GridSize;
        var reach = Math.Max(radiusX, radiusY) / scale * 1.5;
        if (reach / step > 120) {
            return;
        }

        var pen = new Pen(Brush(_map.GridColor), 1);
        var (x0, x1) = (Math.Floor((data.X - reach) / step) * step, data.X + reach);
        var (y0, y1) = (Math.Floor((data.Y - reach) / step) * step, data.Y + reach);
        for (var x = x0; x <= x1; x += step) {
            context.DrawLine(pen, project(x, data.Y - reach), project(x, data.Y + reach));
        }

        for (var y = y0; y <= y1; y += step) {
            context.DrawLine(pen, project(data.X - reach, y), project(data.X + reach, y));
        }
    }

    // Where mobs are: a soft patch round every creature, patches that touch joined into one area, and how many are in
    // each area (Niduus, 2026-11-12: "highlight areas of mobs").
    private void DrawMobAreas(DrawingContext context, OverlayMinimapData.Snapshot data, Func<double, double, Point> project, Point center,
                              double radiusX, double radiusY, double unit) {
        var r = Math.Max(3, _map.MobAreaSize * unit);
        var spots = data.Marks.Where(m => m.Kind is "mob" or "boss" && Shows(m)).Select(m => project(m.X, m.Y))
            .Where(p => Math.Abs(p.X - center.X) < radiusX + r && Math.Abs(p.Y - center.Y) < radiusY + r).ToList();
        if (spots.Count == 0) {
            return;
        }

        // One filled shape (non-zero), so where patches overlap the colour doesn't get darker.
        var patches = new GeometryGroup { FillRule = FillRule.NonZero };
        foreach (var p in spots) {
            patches.Children.Add(new EllipseGeometry(new Rect(p.X - r, p.Y - r, r * 2, r * 2)));
        }

        context.DrawGeometry(Brush(_map.MobAreaColor), null, patches);
        if (!_map.MobAreaCount) {
            return;
        }

        // Areas: spots closer than two patches apart are one.
        var group = Enumerable.Range(0, spots.Count).ToArray();
        int Root(int i) => group[i] == i ? i : group[i] = Root(group[i]);
        for (var i = 0; i < spots.Count; i++) {
            for (var j = i + 1; j < spots.Count; j++) {
                var (dx, dy) = (spots[i].X - spots[j].X, spots[i].Y - spots[j].Y);
                if (dx * dx + dy * dy < 4 * r * r) {
                    group[Root(i)] = Root(j);
                }
            }
        }

        foreach (var area in Enumerable.Range(0, spots.Count).GroupBy(Root).Where(g => g.Count() > 1)) {
            var top = area.Min(i => spots[i].Y) - r;
            var x = area.Average(i => spots[i].X);
            if (Math.Abs(x - center.X) > radiusX || Math.Abs(top - center.Y) > radiusY) {
                continue;
            }

            Label(context, $"{area.Count()} mobs", new Point(x, top - _map.NameSize * 0.2), _map.NameSize * 0.9, Brush(_map.MobColor), centered: true, bold: true);
        }
    }

    // Which names show: bosses, sigils and doors with Names; creatures and NPCs each by their own choice; NPCs on the full screen map.
    private bool Named(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "boss" or "sigil" or "door" => _map.Names,
        "chest" => _map.ChestNames,
        "mob" => _map.MobNames,
        "npc" => _map.NpcNames || (_full && _map.FullNames),
        _ => false,
    };

    private double SizeOf(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "mob" => _map.MobSize, "boss" => _map.BossSize, "npc" => _map.NpcSize, "sigil" => _map.SigilSize, "door" => _map.DoorSize,
        "chest" => _map.ChestSize, "wizard" or "team" => _map.WizardSize, _ => 1,
    };

    private static int Order(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "npc" => 0, "chest" => 1, "door" => 2, "mob" => 3, "wizard" => 5, "team" => 6, "sigil" => 7, "boss" => 9, _ => 0,
    };

    private bool Shows(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "npc" => _map.Npcs,
        "mob" => _map.Mobs,
        "boss" => _map.Bosses,
        "sigil" => _map.Sigils,
        "door" => _map.Doors,
        "chest" => _map.Chests,
        "wizard" or "team" => _map.Wizards,
        _ => false,
    };

    private string ColorOf(OverlayMinimapData.Mark mark) => mark.Kind switch {
        "npc" => _map.NpcColor, "mob" => _map.MobColor,
        "boss" => _map.BossColor, "sigil" => _map.SigilColor, "door" => _map.DoorColor, "chest" => _map.ChestColor,
        "wizard" => _map.WizardColor, "team" => _map.TeamColor, _ => _map.TextColor,
    };

    private Pen Outline => _outline ??= new Pen(Brush(_map.OutlineColor), 1);

    private Pen? _outline;

    private void Icon(DrawingContext context, OverlayMinimapData.Mark mark, Point at, double size) {
        var brush = Brush(ColorOf(mark));
        var r = size / 2;
        switch (mark.Kind) {
            case "boss":
                Skull(context, at, r * 1.4, brush);
                break;
            case "mob":
                context.DrawEllipse(brush, Outline, at, r * 0.75, r * 0.75);
                break;
            case "npc":
                context.DrawEllipse(brush, Outline, at, r * 0.55, r * 0.55);
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

    private void Skull(DrawingContext context, Point at, double r, IBrush brush) {
        // A round head over a jaw, with two dark eyes.
        context.DrawRectangle(brush, Outline, new Rect(at.X - r * 0.5, at.Y + r * 0.1, r, r * 0.75), r * 0.2, r * 0.2);
        context.DrawEllipse(brush, Outline, new Point(at.X, at.Y - r * 0.1), r * 0.85, r * 0.75);
        context.DrawRectangle(brush, null, new Rect(at.X - r * 0.45, at.Y + r * 0.1, r * 0.9, r * 0.45));
        var dark = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x08, 0x10));
        context.DrawEllipse(dark, null, new Point(at.X - r * 0.33, at.Y - r * 0.08), r * 0.22, r * 0.26);
        context.DrawEllipse(dark, null, new Point(at.X + r * 0.33, at.Y - r * 0.08), r * 0.22, r * 0.26);
    }

    // A pointer at a spot, its tip towards angle (0: up), as for you in the middle or a teammate off the map.
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

    private void Note(DrawingContext context, string note, Rect area) {
        var text = Text(note, _map.NameSize + 1, Brush(_map.TextColor), true);
        text.MaxTextWidth = Math.Max(20, area.Width * 0.72);
        text.TextAlignment = TextAlignment.Center;
        var at = new Point(area.Center.X - text.MaxTextWidth / 2, area.Center.Y - text.Height / 2);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0x0A, 0x0A, 0x14)), null,
            new Rect(at.X - 6, at.Y - 4, text.MaxTextWidth + 12, text.Height + 8), 6, 6);
        Words(context, text, at, Brush(_map.TextColor));
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
            Words(context, shadow, new Point(x + 1, at.Y + 1), Brushes.Black);
        }

        Words(context, text, new Point(x, at.Y), brush);
    }

    // Words drawn as their letters' outlines, like the map's other shapes. Drawn as text they came out blank on the
    // overlay over the game on Windows (Niduus, 2026-11-12: an empty dark bar where the note's words should be).
    private static void Words(DrawingContext context, FormattedText text, Point at, IBrush brush) {
        if (text.BuildGeometry(at) is { } outline) {
            context.DrawGeometry(brush, null, outline);
        }
    }

    private FormattedText Text(string words, double size, IBrush brush, bool bold) {
        var family = string.IsNullOrWhiteSpace(_element.Font) ? FontFamily.Default : new FontFamily(_element.Font);

        return new FormattedText(words, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.SemiBold), Math.Max(4, size), brush);
    }

    private static IBrush Brush(string color) => OverlayRenderer.Parse(color) is { } c ? new SolidColorBrush(c) : Brushes.White;

}
