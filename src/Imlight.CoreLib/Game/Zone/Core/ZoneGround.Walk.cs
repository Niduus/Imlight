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

            return BuildWalk(memory.ToArray());
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: could not read its ground for the quest line ({1}).", Logger.Args(zonePath, ex.Message));

            return null;
        }
    }

    /// <summary>The walking ground of a collision file's bytes.</summary>
    internal static WalkGround BuildWalk(byte[] data) {
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

        return new WalkGround(left, top, cell, width, height, kinds, heights, lows);
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
