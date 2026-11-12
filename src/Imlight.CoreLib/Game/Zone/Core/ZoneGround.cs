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

    private readonly record struct Triangle(float X1, float Y1, float X2, float Y2, float X3, float Y3, byte Kind);

    internal static Ground Build(byte[] data) {
        var triangles = new List<Triangle>();
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
            var rot = new float[9];
            for (var r = 0; r < 9; r++) {
                rot[r] = F();
            }

            var (mx, my) = (F(), F());
            F(); // middle height
            var scale = F();
            // A mesh's points are its own: turned, sized and moved by the shape like a box's corners (Niduus, 2026-11-12:
            // Unicorn Way's ground came out about 12000 to one side, so the map didn't match the street).
            if (points is not null) {
                var size = scale is > 0 and < 1000 ? scale : 1f;
                for (var p = 0; p + 2 < points.Length; p += 3) {
                    var (px, py, pz) = (points[p] * size, points[p + 1] * size, points[p + 2] * size);
                    points[p] = mx + (rot[0] * px) + (rot[3] * py) + (rot[6] * pz);
                    points[p + 1] = my + (rot[1] * px) + (rot[4] * py) + (rot[7] * pz);
                }
            }
            var material = Text();
            var sizeType = U();
            switch (sizeType) {
                case 0: {
                    // A box: a wall or fence, its four corners from above.
                    var (hx, hy) = (F() * scale / 2f, F() * scale / 2f);
                    F();
                    (float X, float Y) Corner(float sx, float sy) => (mx + (rot[0] * sx * hx) + (rot[3] * sy * hy), my + (rot[1] * sx * hx) + (rot[4] * sy * hy));
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
                case 3 or 4:
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

            // Ground meshes (category 2); the fishing spots' water is another category and is left out.
            if (points is not null && category == 2) {
                var kind = KindOf(material);
                for (var f = 0; f + 2 < faces.Length; f += 3) {
                    var (p1, p2, p3) = ((int) faces[f] * 3, (int) faces[f + 1] * 3, (int) faces[f + 2] * 3);
                    if (Math.Max(p1, Math.Max(p2, p3)) + 2 >= points.Length) {
                        continue;
                    }

                    triangles.Add(new Triangle(points[p1], points[p1 + 1], points[p2], points[p2 + 1], points[p3], points[p3 + 1], kind));
                }
            }
        }

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

        // Ground first (grass under paths under water), walls last, so each is drawn over the one before.
        foreach (var kind in new[] { GRASS, PATH, WATER, WALL }) {
            foreach (var t in triangles) {
                if (t.Kind == kind) {
                    Fill(grid, width, height, left, top, cell, t);
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
