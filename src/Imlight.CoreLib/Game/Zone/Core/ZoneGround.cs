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

/*
 * ZONE GROUND
 *
 * The ground of a zone seen from above, for the overlay's minimap (Niduus, 2026-11-12: "I want the street actually
 * mapped out in the minimap"). Read only from collision.bcd in the zone's .wad (the same file ZoneCollision reads):
 * its ground meshes carry a material (Stone, Cobbles, Dirt, Grass, Water...), and its boxes are walls and fences. They
 * are filled into a grid of at most 1024 x 1024 cells, one kind per cell: 0 nothing, 1 path, 2 grass, 3 water, 4 wall.
 * The grid goes to the overlay once per zone, packed as runs. Nothing in the game's files is changed.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class ZoneGround {

    internal const byte NONE = 0;
    internal const byte PATH = 1;
    internal const byte GRASS = 2;
    internal const byte WATER = 3;
    internal const byte WALL = 4;

    private const int MAX_CELLS = 1024;
    private const float MIN_CELL = 8f;

    /// <summary>
    /// A zone's ground: the world spot of the grid's top left corner (X smallest, Y largest), the size of a cell in game
    /// units, the grid's size, and its cells packed as runs (kind, then a 16-bit count), base64.
    /// </summary>
    internal sealed record Ground(float Left, float Top, float Cell, int Width, int Height, string Runs) {

        internal static readonly Ground Empty = new(0, 0, 0, 0, 0, "");

        internal bool IsEmpty => Width == 0;

    }

    /// <summary>
    /// A zone's ground as exact shapes (Niduus, 2026-11-12: "Make sure they are fully accurate"): every ground triangle,
    /// wall and round thing as the game's collision has it, with no grid. Points are whole steps of <c>Unit</c> game
    /// units from (Left, Bottom). Packed, base64: blocks of a kind (1 path, 2 grass, 3 water, 4 wall, 6 path and 7 grass above water: triangles of six
    /// 16-bit numbers; 5 round things: circles of three: x, y, radius), each block a kind byte and a 32-bit count.
    /// </summary>
    internal sealed record Outline(float Left, float Bottom, float Unit, string Data) {

        internal static readonly Outline Empty = new(0, 0, 0, "");

        internal bool IsEmpty => Data.Length == 0;

    }

    internal const byte ROUND = 5;

    /// <summary>A path above water (a bridge or dock), drawn over the water.</summary>
    internal const byte PATH_OVER = 6;

    /// <summary>Grass above water, drawn over the water.</summary>
    internal const byte GRASS_OVER = 7;

    private static readonly ConcurrentDictionary<(string Zone, float Smoothing), Lazy<Outline>> s_outlines = new();

    /// <summary>
    /// The shapes of this zone's ground (made once for each smoothing), or <see cref="Outline.Empty"/> when it has none.
    /// <paramref name="smoothing"/> (game units, 0 = the exact triangles) rounds off the rough edges of paths, grass and water.
    /// </summary>
    internal static Outline ShapesOf(string zonePath, float smoothing = 0) {
        if (string.IsNullOrEmpty(zonePath)) {
            return Outline.Empty;
        }

        smoothing = Math.Clamp(MathF.Round(smoothing), 0, 400);
        if (s_outlines.Count > 64) {
            s_outlines.Clear();
        }

        return s_outlines.GetOrAdd((zonePath.ToLowerInvariant(), smoothing), key => new Lazy<Outline>(() => LoadShapes(key.Zone, key.Smoothing))).Value;
    }

    private static Outline LoadShapes(string zonePath, float smoothing) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || wad.OpenFile("collision.bcd") is not { } memory) {
                return Outline.Empty;
            }

            return Shapes(memory.ToArray(), smoothing);
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: could not draw its ground shapes for the minimap ({1}).", Logger.Args(zonePath, ex.Message));

            return Outline.Empty;
        }
    }

    internal static Outline Shapes(byte[] data, float smoothing = 0) {
        var triangles = Read(data, out var circles);
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        void Take(float x, float y) {
            if (float.IsFinite(x) && float.IsFinite(y)) {
                (left, right, bottom, top) = (Math.Min(left, x), Math.Max(right, x), Math.Min(bottom, y), Math.Max(top, y));
            }
        }

        foreach (var t in triangles) {
            Take(t.X1, t.Y1);
            Take(t.X2, t.Y2);
            Take(t.X3, t.Y3);
        }

        foreach (var c in circles) {
            Take(c.X - c.R, c.Y - c.R);
            Take(c.X + c.R, c.Y + c.R);
        }

        if (triangles.Count == 0 || left >= right || bottom >= top) {
            return Outline.Empty;
        }

        var unit = Math.Max(1f, Math.Max(right - left, top - bottom) / 65000f);
        ushort Q(float v, float from) => (ushort) Math.Clamp(MathF.Round((v - from) / unit), 0, 65535);
        bool Finite(Triangle t) => float.IsFinite(t.X1 + t.Y1 + t.X2 + t.Y2 + t.X3 + t.Y3);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var kind in new[] { GRASS, PATH, WATER, WALL, PATH_OVER, GRASS_OVER }) {
            var mine = triangles.Where(t => t.Kind == kind && Finite(t)).ToList();
            if (mine.Count == 0) {
                continue;
            }

            // Smoothed: the kind's outlines (kind + 16, a count, then each with its own count of points), filled even-odd.
            // Walls are only rounded a little (thin fences would melt away), so their corners don't stick out.
            if (smoothing > 0) {
                var outlines = GroundContours.Trace([.. mine.Select(t => new[] { t.X1, t.Y1, t.X2, t.Y2, t.X3, t.Y3 })],
                        kind == WALL ? Math.Min(smoothing * 0.3f, 15f) : smoothing)
                    .Where(o => o.Length / 2 <= ushort.MaxValue).ToList();
                writer.Write((byte) (kind + 16));
                writer.Write((uint) outlines.Count);
                foreach (var outline in outlines) {
                    writer.Write((ushort) (outline.Length / 2));
                    for (var i = 0; i + 1 < outline.Length; i += 2) {
                        writer.Write(Q(outline[i], left));
                        writer.Write(Q(outline[i + 1], bottom));
                    }
                }

                continue;
            }

            writer.Write(kind);
            writer.Write((uint) mine.Count);
            foreach (var t in mine) {
                writer.Write(Q(t.X1, left));
                writer.Write(Q(t.Y1, bottom));
                writer.Write(Q(t.X2, left));
                writer.Write(Q(t.Y2, bottom));
                writer.Write(Q(t.X3, left));
                writer.Write(Q(t.Y3, bottom));
            }
        }

        if (circles.Count > 0) {
            writer.Write(ROUND);
            writer.Write((uint) circles.Count);
            foreach (var c in circles) {
                writer.Write(Q(c.X, left));
                writer.Write(Q(c.Y, bottom));
                writer.Write((ushort) Math.Clamp(MathF.Round(c.R / unit), 1, 65535));
            }
        }

        writer.Flush();

        return new Outline(left, bottom, unit, Convert.ToBase64String(stream.ToArray()));
    }

    private static readonly ConcurrentDictionary<string, Lazy<Ground>> s_grounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ground of this zone (read once), or <see cref="Ground.Empty"/> when it has none.</summary>
    internal static Ground Of(string zonePath)
        => string.IsNullOrEmpty(zonePath) ? Ground.Empty : s_grounds.GetOrAdd(zonePath, z => new Lazy<Ground>(() => Load(z))).Value;

    private static Ground Load(string zonePath) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || wad.OpenFile("collision.bcd") is not { } memory) {
                return Ground.Empty;
            }

            return Build(memory.ToArray());
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: could not draw its ground for the minimap ({1}).", Logger.Args(zonePath, ex.Message));

            return Ground.Empty;
        }
    }

    private readonly record struct Triangle(float X1, float Y1, float X2, float Y2, float X3, float Y3, byte Kind, float Z = 0);

    private readonly record struct Circle(float X, float Y, float R);

    internal static Ground Build(byte[] data) => Grid(Read(data, out _));

    // Every ground triangle and wall (two triangles a box) from above, and the round things (trees, posts, pillars).
    private static List<Triangle> Read(byte[] data, out List<Circle> circles) {
        var triangles = new List<Triangle>();
        circles = [];
        var at = 0;
        uint U() { var v = BitConverter.ToUInt32(data, at); at += 4; return v; }
        float F() { var v = BitConverter.ToSingle(data, at); at += 4; return v; }
        string Text() { var length = (int) U(); var text = System.Text.Encoding.Latin1.GetString(data, at, length); at += length; return text; }

        var count = U();
        for (var i = 0; i < count && at < data.Length; i++) {
            var type = U();
            var category = U();
            U(); // collide flags
            float[] points = null;
            uint[] faces = null;
            if (type == 6) {
                var pointCount = (int) U();
                var faceCount = (int) U();
                points = new float[pointCount * 3];
                for (var p = 0; p < points.Length; p++) {
                    points[p] = F();
                }

                faces = new uint[faceCount * 3];
                for (var f = 0; f < faceCount; f++) {
                    faces[f * 3] = U();
                    faces[(f * 3) + 1] = U();
                    faces[(f * 3) + 2] = U();
                    at += 12; // the face's normal
                }
            }

            Text(); // name
            // The turn is stored row by row: world x = row 0 . point (Niduus, 2026-11-12: walls and bridges stuck out at
            // odd angles, turned the wrong way round).
            var rot = new float[9];
            for (var r = 0; r < 9; r++) {
                rot[r] = F();
            }

            var (mx, my) = (F(), F());
            var mz = F();
            var scale = F();
            // A mesh's points are its own: turned, sized and moved by the shape like a box's corners (Niduus, 2026-11-12:
            // Unicorn Way's ground came out about 12000 to one side, so the map didn't match the street).
            if (points is not null) {
                var size = scale is > 0 and < 1000 ? scale : 1f;
                for (var p = 0; p + 2 < points.Length; p += 3) {
                    var (px, py, pz) = (points[p] * size, points[p + 1] * size, points[p + 2] * size);
                    points[p] = mx + (rot[0] * px) + (rot[1] * py) + (rot[2] * pz);
                    points[p + 1] = my + (rot[3] * px) + (rot[4] * py) + (rot[5] * pz);
                    points[p + 2] = mz + (rot[6] * px) + (rot[7] * py) + (rot[8] * pz);
                }
            }
            var material = Text();
            var sizeType = U();
            switch (sizeType) {
                case 0: {
                    // A box: a wall or fence, its four corners from above.
                    var (hx, hy) = (F() * scale / 2f, F() * scale / 2f);
                    F();
                    (float X, float Y) Corner(float sx, float sy) => (mx + (rot[0] * sx * hx) + (rot[1] * sy * hy), my + (rot[3] * sx * hx) + (rot[4] * sy * hy));
                    var (a, b, c, d) = (Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1));
                    triangles.Add(new Triangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, WALL));
                    triangles.Add(new Triangle(a.X, a.Y, c.X, c.Y, d.X, d.Y, WALL));
                    break;
                }

                case 1:
                    at += 8;
                    break;
                case 2:
                    at += 4;
                    break;
                case 3: {
                    // A cylinder standing up (a tree, a post, a pillar): its length, then its radius.
                    F();
                    var radius = F() * (scale is > 0 and < 1000 ? scale : 1f);
                    if (category == 1 && MathF.Abs(rot[8]) > 0.9f && radius is > 5 and < 3000) {
                        circles.Add(new Circle(mx, my, radius));
                    }

                    break;
                }

                case 4:
                    at += 8;
                    break;
                case 5:
                    at += 16;
                    break;
                case 6:
                    break;
                default:
                    i = (int) count;
                    break;
            }

            // Ground meshes (category 2), and the water of lakes and fishing spots (category 64, Water).
            var lake = category == 64 && string.Equals((material ?? "").Trim(), "Water", StringComparison.OrdinalIgnoreCase);
            if (points is not null && (category == 2 || lake)) {
                var kind = lake ? WATER : KindOf(material);
                for (var f = 0; f + 2 < faces.Length; f += 3) {
                    var (p1, p2, p3) = ((int) faces[f] * 3, (int) faces[f + 1] * 3, (int) faces[f + 2] * 3);
                    if (Math.Max(p1, Math.Max(p2, p3)) + 2 >= points.Length) {
                        continue;
                    }

                    triangles.Add(new Triangle(points[p1], points[p1 + 1], points[p2], points[p2 + 1], points[p3], points[p3 + 1], kind,
                        (points[p1 + 2] + points[p2 + 2] + points[p3 + 2]) / 3));
                }
            }
        }

        // Water lies over the ground under it (the Commons' pond has a walkable floor); ground well above the water (a
        // bridge, a dock, a bank) goes over the water.
        var waters = triangles.Where(t => t.Kind == WATER).ToList();
        if (waters.Count > 0) {
            for (var i = 0; i < triangles.Count; i++) {
                var t = triangles[i];
                if (t.Kind is not (PATH or GRASS)) {
                    continue;
                }

                var (x0, x1) = (Math.Min(t.X1, Math.Min(t.X2, t.X3)), Math.Max(t.X1, Math.Max(t.X2, t.X3)));
                var (y0, y1) = (Math.Min(t.Y1, Math.Min(t.Y2, t.Y3)), Math.Max(t.Y1, Math.Max(t.Y2, t.Y3)));
                var under = waters.Where(w => Math.Max(w.X1, Math.Max(w.X2, w.X3)) >= x0 && Math.Min(w.X1, Math.Min(w.X2, w.X3)) <= x1
                                              && Math.Max(w.Y1, Math.Max(w.Y2, w.Y3)) >= y0 && Math.Min(w.Y1, Math.Min(w.Y2, w.Y3)) <= y1).ToList();
                if (under.Count > 0 && t.Z > under.Max(w => w.Z) + 15) {
                    triangles[i] = t with { Kind = t.Kind == PATH ? PATH_OVER : GRASS_OVER };
                }
            }
        }

        return triangles;
    }

    private static Ground Grid(List<Triangle> triangles) {
        // The grid covers the ground (walls alone don't set it).
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        foreach (var t in triangles) {
            if (t.Kind == WALL) {
                continue;
            }

            left = Math.Min(left, Math.Min(t.X1, Math.Min(t.X2, t.X3)));
            right = Math.Max(right, Math.Max(t.X1, Math.Max(t.X2, t.X3)));
            bottom = Math.Min(bottom, Math.Min(t.Y1, Math.Min(t.Y2, t.Y3)));
            top = Math.Max(top, Math.Max(t.Y1, Math.Max(t.Y2, t.Y3)));
        }

        if (left >= right || bottom >= top || !float.IsFinite(right - left) || !float.IsFinite(top - bottom)) {
            return Ground.Empty;
        }

        var cell = Math.Max(MIN_CELL, Math.Max(right - left, top - bottom) / MAX_CELLS);
        var width = Math.Clamp((int) MathF.Ceiling((right - left) / cell) + 1, 1, MAX_CELLS + 2);
        var height = Math.Clamp((int) MathF.Ceiling((top - bottom) / cell) + 1, 1, MAX_CELLS + 2);
        var grid = new byte[width * height];

        // Grass, paths, water over them, ground above the water over that, and walls last, each drawn over the one before.
        foreach (var kind in new[] { GRASS, PATH, WATER, GRASS_OVER, PATH_OVER, WALL }) {
            foreach (var t in triangles) {
                if (t.Kind == kind) {
                    Fill(grid, width, height, left, top, cell, t with { Kind = kind switch { PATH_OVER => PATH, GRASS_OVER => GRASS, _ => kind } });
                }
            }
        }

        return new Ground(left, top, cell, width, height, Pack(grid));
    }

    private static byte KindOf(string material) => (material ?? "").Trim().ToLowerInvariant() switch {
        "grass" or "leaves" or "moss" or "hay" or "flowers" => GRASS,
        "water" or "lava" or "goo" or "ooze" => WATER,
        _ => PATH,
    };

    // Every cell whose middle is inside the triangle (seen from above).
    private static void Fill(byte[] grid, int width, int height, float left, float top, float cell, Triangle t) {
        float Gx(float x) => (x - left) / cell;
        float Gy(float y) => (top - y) / cell;
        var (ax, ay, bx, by, cx, cy) = (Gx(t.X1), Gy(t.Y1), Gx(t.X2), Gy(t.Y2), Gx(t.X3), Gy(t.Y3));
        var area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (MathF.Abs(area) < 1e-6f) {
            return;
        }

        var x0 = Math.Max(0, (int) MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
        var x1 = Math.Min(width - 1, (int) MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        var y0 = Math.Max(0, (int) MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
        var y1 = Math.Min(height - 1, (int) MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        var small = x1 - x0 <= 1 && y1 - y0 <= 1;
        for (var y = y0; y <= y1; y++) {
            for (var x = x0; x <= x1; x++) {
                var (px, py) = (x + 0.5f, y + 0.5f);
                var w0 = ((bx - px) * (cy - py)) - ((by - py) * (cx - px));
                var w1 = ((cx - px) * (ay - py)) - ((cy - py) * (ax - px));
                var w2 = ((ax - px) * (by - py)) - ((ay - py) * (bx - px));
                var inside = area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0;
                // A sliver smaller than a cell (a thin fence) still marks its cell.
                if (inside || small) {
                    grid[(y * width) + x] = t.Kind;
                }
            }
        }
    }

    private static string Pack(byte[] grid) {
        using var stream = new MemoryStream();
        var i = 0;
        while (i < grid.Length) {
            var kind = grid[i];
            var run = 1;
            while (i + run < grid.Length && grid[i + run] == kind && run < ushort.MaxValue) {
                run++;
            }

            stream.WriteByte(kind);
            stream.WriteByte((byte) (run & 0xFF));
            stream.WriteByte((byte) (run >> 8));
            i += run;
        }

        return Convert.ToBase64String(stream.ToArray());
    }

}
