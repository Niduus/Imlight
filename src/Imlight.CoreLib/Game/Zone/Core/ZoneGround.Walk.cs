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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static partial class ZoneGround {

    /// <summary>
    /// The ground as the Quest Tracker's walk needs it (Niduus, 2026-11-12: the line went straight across the Vault of
    /// Ice's sunken pool, "I can't past this"): what each cell is (a path, grass, water, a wall, or nothing known) and how
    /// high its ground is, so a drop or a ledge between two cells can't be walked over. Thin walls keep their cells.
    /// </summary>
    /// <remarks>Heights is each cell's highest ground and Lows its lowest (a hall under a balcony has both).</remarks>
    internal sealed record WalkGround(float Left, float Top, float Cell, int Width, int Height, byte[] Kinds, float[] Heights, float[] Lows);

    private const int WALK_CELLS = 480;

    private static readonly ConcurrentDictionary<string, Lazy<WalkGround>> s_walkGrounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This zone's ground for walking (read once), or null when it has none.</summary>
    internal static WalkGround WalkOf(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        if (s_walkGrounds.Count > 24) {
            s_walkGrounds.Clear();
        }

        return s_walkGrounds.GetOrAdd(zonePath, z => new Lazy<WalkGround>(() => LoadWalk(z))).Value;
    }

    private static WalkGround LoadWalk(string zonePath) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || wad.OpenFile("collision.bcd") is not { } memory) {
                return null;
            }

            return BuildWalk(memory.ToArray(), ZoneNav.Points(zonePath));
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: could not read its ground for the quest line ({1}).", Logger.Args(zonePath, ex.Message));

            return null;
        }
    }

    /// <summary>The walking ground of a collision file's bytes.</summary>
    internal static WalkGround BuildWalk(byte[] data, Vector3[] navPoints = null) {
        var triangles = Read(data, out var circles, forWalking: true);
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
            return null;
        }

        var cell = Math.Max(MIN_CELL, Math.Max(right - left, top - bottom) / WALK_CELLS);
        var width = Math.Clamp((int) MathF.Ceiling((right - left) / cell) + 1, 1, WALK_CELLS + 2);
        var height = Math.Clamp((int) MathF.Ceiling((top - bottom) / cell) + 1, 1, WALK_CELLS + 2);
        var kinds = new byte[width * height];
        var heights = new float[width * height];
        var lows = new float[width * height];
        Array.Fill(heights, float.NaN);
        Array.Fill(lows, float.NaN);

        // The ground first: each cell takes its highest ground (a bridge over a stream, the rim over a pit's floor), and
        // its kind from that ground. Water counts where it is the top. Walls last, over everything.
        foreach (var t in triangles) {
            if (t.Kind != WALL) {
                FillWalk(kinds, heights, lows, width, height, left, top, cell, t);
            }
        }

        FillHoles(kinds, heights, lows, width, height, cell);

        foreach (var t in triangles) {
            if (t.Kind == WALL) {
                FillWalk(kinds, heights, lows, width, height, left, top, cell, t);
            }
        }

        // Round posts, trees and pillars (a wizard walks round them, not through).
        foreach (var c in circles.Where(c => c.R is >= 10 and <= 400)) {
            var reach = (int) MathF.Ceiling(c.R / cell);
            var (gx, gy) = ((int) MathF.Floor((c.X - left) / cell), (int) MathF.Floor((top - c.Y) / cell));
            for (var y = gy - reach; y <= gy + reach; y++) {
                for (var x = gx - reach; x <= gx + reach; x++) {
                    if (x < 0 || y < 0 || x >= width || y >= height) {
                        continue;
                    }

                    var (cx, cy) = (left + ((x + 0.5f) * cell), top - ((y + 0.5f) * cell));
                    if (MathF.Sqrt(((cx - c.X) * (cx - c.X)) + ((cy - c.Y) * (cy - c.Y))) <= c.R + (cell * 0.35f)) {
                        kinds[(y * width) + x] = WALL;
                    }
                }
            }
        }

        // After the walls and posts, so a hole walled off from the open void outside counts as ringed.
        FillNavHoles(kinds, heights, lows, width, height, left, top, cell, navPoints);

        return new WalkGround(left, top, cell, width, height, kinds, heights, lows);
    }

    // The biggest hole in the ground (game units across, about) that is floor all the same.
    private const float HOLE_SPAN = 260f;
    // How much the floor round a hole may rise and fall for the hole to count as floor.
    private const float HOLE_RISE = 60f;

    /// <summary>
    /// Small holes in the ground (a gap between two ground pieces, a missing tile under a doorstep) closed up: a patch of
    /// no ground, wholly ringed by floor of about one height and no wider than <see cref="HOLE_SPAN"/>, is floor at that
    /// height. Such a hole drew black on the Minimap and was a wall to the walk (Niduus, 2026-11-13: black areas
    /// "accidentally impeding or blocking quest projection for QT and Ultimus"); the game's own walk map (zone.nav)
    /// walks over them. A gap that is open to the edge, or ringed by ground at different heights (a pit, a drop), stays.
    /// </summary>
    private static void FillHoles(byte[] kinds, float[] heights, float[] lows, int width, int height, float cell) {
        bool Void(int i) => kinds[i] == NONE && float.IsNaN(heights[i]);
        var most = Math.Max(1, (int) Math.Ceiling(HOLE_SPAN / cell));
        var limit = most * most;
        var seen = new bool[kinds.Length];
        var patch = new List<int>();
        var queue = new Queue<int>();
        for (var start = 0; start < kinds.Length; start++) {
            if (seen[start] || !Void(start)) {
                continue;
            }

            patch.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var (open, minX, maxX, minY, maxY) = (false, int.MaxValue, 0, int.MaxValue, 0);
            float low = float.MaxValue, high = float.MinValue, sum = 0;
            var ring = 0;
            var ringKinds = new int[8];
            while (queue.Count > 0) {
                var i = queue.Dequeue();
                patch.Add(i);
                var (x, y) = (i % width, i / width);
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }) {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) {
                        open = true;
                        continue;
                    }

                    var n = (ny * width) + nx;
                    if (Void(n)) {
                        if (!seen[n]) {
                            seen[n] = true;
                            queue.Enqueue(n);
                        }

                        continue;
                    }

                    if (!float.IsNaN(heights[n])) {
                        (low, high) = (Math.Min(low, heights[n]), Math.Max(high, heights[n]));
                        sum += heights[n];
                        ring++;
                        ringKinds[Math.Min(kinds[n], (byte) 7)]++;
                    }
                }
            }

            if (open || ring == 0 || patch.Count > limit || maxX - minX + 1 > most || maxY - minY + 1 > most || high - low > HOLE_RISE) {
                continue;
            }

            // The kind most of its rim is (path, grass or water).
            var kind = (byte) (ringKinds[GRASS] > ringKinds[PATH] && ringKinds[GRASS] >= ringKinds[WATER] ? GRASS
                : ringKinds[WATER] > ringKinds[PATH] ? WATER : PATH);
            var level = sum / ring;
            foreach (var i in patch) {
                kinds[i] = kind;
                heights[i] = level;
                lows[i] = level;
            }
        }
    }

    // The biggest hole (game units across, about) that the game's walk map can close up.
    private const float NAV_HOLE_SPAN = 6000f;
    // How far above or below a walk point the floor round its hole may be.
    private const float NAV_HOLE_RISE = 100f;
    // How far a walk point looks for the floor round it, each way.
    private const float NAV_HOLE_REACH = 480f;

    /// <summary>
    /// Bigger holes the game itself walks over: a patch of no ground, wholly ringed by floor, with one of the game's own
    /// walk points (zone.nav) in it at about the height of that floor, is floor (Niduus, 2026-11-13: "use the map data
    /// here to refine the mini map"). Checking every place against his game's files found 13 such spots in 9 places
    /// (the seams across Avalon's High Road, Khrysalis's Central Keep, three in Dragonspyre, two house interiors), each
    /// a wall to the quest line, Ultimus and the Quest Team. Each cell takes the height of the floor next to it, so a
    /// sloped road stays a slope. A patch open to the edge, too big, or with no walk point stays as it is.
    /// </summary>
    private static void FillNavHoles(byte[] kinds, float[] heights, float[] lows, int width, int height, float left, float top, float cell, Vector3[] navPoints) {
        if (navPoints is not { Length: > 0 }) {
            return;
        }

        bool Void(int i) => kinds[i] == NONE && float.IsNaN(heights[i]);

        // Looking out from the walk point every way (east, west, north, south), floor at about its height comes before
        // any wall and within NAV_HOLE_REACH. The game's walk map also has points inside buildings and walled-off
        // corners; those meet a wall first and stay shut.
        var reach = Math.Max(1, (int) Math.Ceiling(NAV_HOLE_REACH / cell));
        bool Ringed(int px, int py, float z) {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) {
                var found = false;
                for (var step = 1; step <= reach; step++) {
                    var (x, y) = (px + (dx * step), py + (dy * step));
                    if (x < 0 || y < 0 || x >= width || y >= height) {
                        break;
                    }

                    var i = (y * width) + x;
                    if (Void(i)) {
                        continue;
                    }

                    found = kinds[i] != WALL && MathF.Abs(heights[i] - z) <= NAV_HOLE_RISE;
                    break;
                }

                if (!found) {
                    return false;
                }
            }

            return true;
        }

        var most = Math.Max(1, (int) Math.Ceiling(NAV_HOLE_SPAN / cell));
        var seen = new bool[kinds.Length];
        var patch = new List<int>();
        var queue = new Queue<int>();
        foreach (var point in navPoints) {
            var (px, py) = ((int) MathF.Floor((point.X - left) / cell), (int) MathF.Floor((top - point.Y) / cell));
            if (px < 0 || py < 0 || px >= width || py >= height) {
                continue;
            }

            var start = (py * width) + px;
            if (seen[start] || !Void(start) || !Ringed(px, py, point.Z)) {
                continue;
            }

            patch.Clear();
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var (open, minX, maxX, minY, maxY) = (false, px, px, py, py);
            int ring = 0, near = 0;
            var ringKinds = new int[8];
            while (queue.Count > 0 && !open) {
                var i = queue.Dequeue();
                patch.Add(i);
                var (x, y) = (i % width, i / width);
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
                if (maxX - minX + 1 > most || maxY - minY + 1 > most) {
                    open = true;
                    break;
                }

                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }) {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) {
                        open = true;
                        break;
                    }

                    var n = (ny * width) + nx;
                    if (Void(n)) {
                        if (!seen[n]) {
                            seen[n] = true;
                            queue.Enqueue(n);
                        }

                        continue;
                    }

                    if (!float.IsNaN(heights[n])) {
                        ring++;
                        ringKinds[Math.Min(kinds[n], (byte) 7)]++;
                        if (MathF.Abs(heights[n] - point.Z) <= NAV_HOLE_RISE) {
                            near++;
                        }
                    }
                }
            }

            // Leave the rest of an open or too-big patch marked seen: no other walk point in it can close it either.
            if (open || ring == 0 || near * 2 < ring) {
                while (queue.Count > 0) {
                    queue.Dequeue();
                }

                continue;
            }

            var kind = (byte) (ringKinds[GRASS] > ringKinds[PATH] && ringKinds[GRASS] >= ringKinds[WATER] ? GRASS
                : ringKinds[WATER] > ringKinds[PATH] ? WATER : PATH);

            // Fill from the rim inward, each cell at the mean height of its filled neighbours.
            var left2 = new HashSet<int>(patch);
            while (left2.Count > 0) {
                var filled = new List<(int Cell, float Height)>();
                foreach (var i in left2) {
                    var (x, y) = (i % width, i / width);
                    float sum = 0;
                    var count = 0;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }) {
                        var n = (ny * width) + nx;
                        if (!left2.Contains(n) && !float.IsNaN(heights[n])) {
                            sum += heights[n];
                            count++;
                        }
                    }

                    if (count > 0) {
                        filled.Add((i, sum / count));
                    }
                }

                if (filled.Count == 0) {
                    break;
                }

                foreach (var (i, h) in filled) {
                    kinds[i] = kind;
                    heights[i] = h;
                    lows[i] = h;
                    left2.Remove(i);
                }
            }
        }
    }

    // A wizard is about this tall; a wall whose bottom is higher than that over every floor of the cell is walked under.
    private const float HEAD_ROOM = 70f;

    private static bool Blocks(Triangle wall, float high, float low) {
        if (float.IsNaN(high) || float.IsNaN(wall.Z1) || !float.IsFinite(wall.Z) || !float.IsFinite(wall.Z1)) {
            return true;
        }

        bool BlocksAt(float floor) => float.IsNaN(floor) || (wall.Z < floor + HEAD_ROOM && wall.Z1 > floor + 5f);

        return BlocksAt(high) || (!float.IsNaN(low) && BlocksAt(low));
    }

    // Every cell a wall's edge (in grid terms) runs through is a wall, unless walked under or wholly under ground.
    private static void WallEdge(byte[] kinds, float[] heights, float[] lows, int width, int height, Triangle t, float x0, float y0, float x1, float y1) {
        var steps = (int) MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)) * 2) + 1;
        if (steps > 20000 || !float.IsFinite(x0 + y0 + x1 + y1)) {
            return;
        }

        for (var i = 0; i <= steps; i++) {
            var f = (float) i / steps;
            var (x, y) = ((int) MathF.Floor(x0 + ((x1 - x0) * f)), (int) MathF.Floor(y0 + ((y1 - y0) * f)));
            if (x < 0 || y < 0 || x >= width || y >= height) {
                continue;
            }

            var at = (y * width) + x;
            if (Blocks(t, heights[at], lows[at])) {
                kinds[at] = WALL;
            }
        }
    }

    private static void FillWalk(byte[] kinds, float[] heights, float[] lows, int width, int height, float left, float top, float cell, Triangle t) {
        float Gx(float x) => (x - left) / cell;
        float Gy(float y) => (top - y) / cell;
        var (ax, ay, bx, by, cx, cy) = (Gx(t.X1), Gy(t.Y1), Gx(t.X2), Gy(t.Y2), Gx(t.X3), Gy(t.Y3));
        var area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (t.Kind == WALL) {
            // Its edges first: a wall standing straight up is only a line from above, and a line has no inside
            // (Niduus, 2026-11-13: the team walked through such walls).
            WallEdge(kinds, heights, lows, width, height, t, ax, ay, bx, by);
            WallEdge(kinds, heights, lows, width, height, t, bx, by, cx, cy);
            WallEdge(kinds, heights, lows, width, height, t, cx, cy, ax, ay);
        }

        if (MathF.Abs(area) < 1e-6f) {
            return;
        }

        var kind = t.Kind switch { PATH_OVER or WOOD => PATH, GRASS_OVER => GRASS, _ => t.Kind };
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
                var at = (y * width) + x;
                if (kind == WALL) {
                    // A sliver smaller than a cell (a thin fence) still marks its cell; but not a wall wholly over a
                    // wizard's head (an arch, a roof walked under) or wholly under the ground (Niduus, 2026-11-12: the
                    // line went the long way round a street's archway).
                    if ((inside || small) && Blocks(t, heights[at], lows[at])) {
                        kinds[at] = WALL;
                    }

                    continue;
                }

                if (!inside) {
                    continue;
                }

                // The ground's height in the cell's middle (from its corners).
                var z = float.IsNaN(t.Z1) ? t.Z : ((w0 * t.Z1) + (w1 * t.Z2) + (w2 * t.Z3)) / area;
                if (!float.IsFinite(z)) {
                    z = t.Z;
                }

                if (kinds[at] == WALL) {
                    continue;
                }

                if (float.IsNaN(lows[at]) || z < lows[at]) {
                    lows[at] = z;
                }

                if (float.IsNaN(heights[at]) || z > heights[at]) {
                    heights[at] = z;
                    kinds[at] = kind;
                }
            }
        }
    }

}
