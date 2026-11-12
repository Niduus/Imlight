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
 * GROUND CONTOURS
 *
 * Smooth outlines of one kind of ground (paths, grass or water) for the overlay's minimap (Niduus, 2026-11-12: "the
 * corners are still there ... I want them smooth"). The game's collision ground is a rough mesh whose edges zig-zag in
 * little teeth. Its triangles are filled into a fine grid, the grid is blurred by about Smoothing game units, and the
 * outline is traced where it is half full (marching squares). Teeth smaller than the blur melt into smooth curves; the
 * shape keeps its place and size. Each outline is then thinned to the points it needs.
 */

using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class GroundContours {

    private const int MAX_SIDE = 3000;
    private const float MIN_CELL = 6f;

    /// <summary>
    /// The outlines (closed, as x, y pairs in world units) of the area the triangles cover (x1, y1, x2, y2, x3, y3 each),
    /// smoothed by about <paramref name="smoothing"/> game units. Holes are outlines too: fill them even-odd.
    /// <paramref name="level"/> under a half grows the shape a little, so kinds drawn side by side overlap and leave no
    /// crack between them.
    /// </summary>
    internal static List<float[]> Trace(IReadOnlyList<float[]> triangles, float smoothing, float level = 0.5f) {
        var outlines = new List<float[]>();
        if (triangles.Count == 0) {
            return outlines;
        }

        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        foreach (var t in triangles) {
            for (var i = 0; i < 6; i += 2) {
                (left, right) = (Math.Min(left, t[i]), Math.Max(right, t[i]));
                (bottom, top) = (Math.Min(bottom, t[i + 1]), Math.Max(top, t[i + 1]));
            }
        }

        if (!(left < right) || !(bottom < top)) {
            return outlines;
        }

        var cell = Math.Max(MIN_CELL, Math.Max(Math.Max(right - left, top - bottom) / MAX_SIDE, smoothing / 5f));
        var sigma = Math.Max(0.6f, smoothing / cell);
        var pad = (int) MathF.Ceiling(sigma * 3) + 2;
        var x0 = left - (pad * cell);
        var y0 = bottom - (pad * cell);
        var width = (int) MathF.Ceiling((right - left) / cell) + (2 * pad) + 1;
        var height = (int) MathF.Ceiling((top - bottom) / cell) + (2 * pad) + 1;
        var grid = new float[width * height];

        // Grid points inside a triangle are full.
        foreach (var t in triangles) {
            var (ax, ay, bx, by, cx, cy) = ((t[0] - x0) / cell, (t[1] - y0) / cell, (t[2] - x0) / cell, (t[3] - y0) / cell, (t[4] - x0) / cell, (t[5] - y0) / cell);
            var area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (MathF.Abs(area) < 1e-6f) {
                continue;
            }

            var gx0 = Math.Max(0, (int) MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
            var gx1 = Math.Min(width - 1, (int) MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            var gy0 = Math.Max(0, (int) MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
            var gy1 = Math.Min(height - 1, (int) MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            for (var y = gy0; y <= gy1; y++) {
                for (var x = gx0; x <= gx1; x++) {
                    var w0 = ((bx - x) * (cy - y)) - ((by - y) * (cx - x));
                    var w1 = ((cx - x) * (ay - y)) - ((cy - y) * (ax - x));
                    var w2 = ((ax - x) * (by - y)) - ((ay - y) * (bx - x));
                    if (area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0) {
                        grid[(y * width) + x] = 1;
                    }
                }
            }
        }

        Blur(grid, width, height, sigma);
        foreach (var loop in March(grid, width, height, level)) {
            var thin = Thin(loop, 0.35f);
            if (thin.Count < 3 || MathF.Abs(Area(thin)) < 4) {
                continue;
            }

            var points = new float[thin.Count * 2];
            for (var i = 0; i < thin.Count; i++) {
                points[i * 2] = x0 + (thin[i].X * cell);
                points[(i * 2) + 1] = y0 + (thin[i].Y * cell);
            }

            outlines.Add(points);
        }

        return outlines;
    }

    // A Gaussian blur, rows then columns.
    private static void Blur(float[] grid, int width, int height, float sigma) {
        var radius = (int) MathF.Ceiling(sigma * 3);
        var kernel = new float[(radius * 2) + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++) {
            kernel[i + radius] = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }

        for (var i = 0; i < kernel.Length; i++) {
            kernel[i] /= sum;
        }

        var line = new float[Math.Max(width, height)];
        for (var y = 0; y < height; y++) {
            var row = y * width;
            for (var x = 0; x < width; x++) {
                var v = 0f;
                for (var k = -radius; k <= radius; k++) {
                    var at = x + k;
                    if (at >= 0 && at < width) {
                        v += grid[row + at] * kernel[k + radius];
                    }
                }

                line[x] = v;
            }

            Array.Copy(line, 0, grid, row, width);
        }

        for (var x = 0; x < width; x++) {
            for (var y = 0; y < height; y++) {
                var v = 0f;
                for (var k = -radius; k <= radius; k++) {
                    var at = y + k;
                    if (at >= 0 && at < height) {
                        v += grid[(at * width) + x] * kernel[k + radius];
                    }
                }

                line[y] = v;
            }

            for (var y = 0; y < height; y++) {
                grid[(y * width) + x] = line[y];
            }
        }
    }

    // The closed outlines where the grid is half full, in grid units.
    private static List<List<(float X, float Y)>> March(float[] grid, int width, int height, float level) {
        var LEVEL = Math.Clamp(level, 0.05f, 0.95f);
        var links = new Dictionary<long, (long A, long B)>();
        void Link(long from, long to) {
            links[from] = links.TryGetValue(from, out var known) ? (known.A, to) : (to, -1);
            links[to] = links.TryGetValue(to, out var other) ? (other.A, from) : (from, -1);
        }

        float V(int x, int y) => grid[(y * width) + x];
        // An edge's key: across from (x, y) to the right (even) or up (odd).
        long Across(int x, int y) => ((long) ((y * width) + x)) * 2;
        long Up(int x, int y) => (((long) ((y * width) + x)) * 2) + 1;

        for (var y = 0; y + 1 < height; y++) {
            for (var x = 0; x + 1 < width; x++) {
                var (a, b, c, d) = (V(x, y), V(x + 1, y), V(x + 1, y + 1), V(x, y + 1));
                var shape = (a >= LEVEL ? 1 : 0) | (b >= LEVEL ? 2 : 0) | (c >= LEVEL ? 4 : 0) | (d >= LEVEL ? 8 : 0);
                if (shape is 0 or 15) {
                    continue;
                }

                var (e0, e1, e2, e3) = (Across(x, y), Up(x + 1, y), Across(x, y + 1), Up(x, y));
                var middle = (a + b + c + d) / 4 >= LEVEL;
                switch (shape) {
                    case 1 or 14: Link(e3, e0); break;
                    case 2 or 13: Link(e0, e1); break;
                    case 3 or 12: Link(e3, e1); break;
                    case 4 or 11: Link(e1, e2); break;
                    case 6 or 9: Link(e0, e2); break;
                    case 7 or 8: Link(e3, e2); break;
                    case 5:
                        if (middle) { Link(e0, e1); Link(e2, e3); } else { Link(e3, e0); Link(e1, e2); }
                        break;
                    case 10:
                        if (middle) { Link(e3, e0); Link(e1, e2); } else { Link(e0, e1); Link(e2, e3); }
                        break;
                }
            }
        }

        (float X, float Y) Point(long key) {
            var cellIndex = (int) (key / 2);
            var (x, y) = (cellIndex % width, cellIndex / width);
            var from = V(x, y);
            if (key % 2 == 0) {
                var to = V(x + 1, y);
                var t = Math.Abs(to - from) < 1e-9f ? 0.5f : Math.Clamp((LEVEL - from) / (to - from), 0, 1);
                return (x + t, y);
            }
            else {
                var to = V(x, y + 1);
                var t = Math.Abs(to - from) < 1e-9f ? 0.5f : Math.Clamp((LEVEL - from) / (to - from), 0, 1);
                return (x, y + t);
            }
        }

        var loops = new List<List<(float X, float Y)>>();
        var done = new HashSet<long>();
        foreach (var start in links.Keys) {
            if (done.Contains(start)) {
                continue;
            }

            var loop = new List<(float X, float Y)>();
            var (previous, at) = (-1L, start);
            while (at >= 0 && done.Add(at)) {
                loop.Add(Point(at));
                var (a, b) = links[at];
                var next = a != previous ? a : b;
                (previous, at) = (at, next);
            }

            if (loop.Count >= 3) {
                loops.Add(loop);
            }
        }

        return loops;
    }

    // Fewer points, none more than tolerance (grid units) off the outline (Douglas-Peucker, on a closed loop).
    private static List<(float X, float Y)> Thin(List<(float X, float Y)> loop, float tolerance) {
        if (loop.Count < 8) {
            return loop;
        }

        // Split at the point farthest from the first, so both halves are open lines.
        var far = 0;
        var best = -1f;
        for (var i = 1; i < loop.Count; i++) {
            var d = ((loop[i].X - loop[0].X) * (loop[i].X - loop[0].X)) + ((loop[i].Y - loop[0].Y) * (loop[i].Y - loop[0].Y));
            if (d > best) {
                (best, far) = (d, i);
            }
        }

        var keep = new bool[loop.Count];
        keep[0] = keep[far] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, far));
        stack.Push((far, loop.Count));
        while (stack.Count > 0) {
            var (from, to) = stack.Pop();
            var (ax, ay) = loop[from];
            var (bx, by) = loop[to % loop.Count];
            var (dx, dy) = (bx - ax, by - ay);
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            var worst = -1f;
            var worstAt = -1;
            for (var i = from + 1; i < to; i++) {
                var (px, py) = loop[i];
                var off = length < 1e-6f ? MathF.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay))) : MathF.Abs((dx * (ay - py)) - ((ax - px) * dy)) / length;
                if (off > worst) {
                    (worst, worstAt) = (off, i);
                }
            }

            if (worst > tolerance && worstAt > 0) {
                keep[worstAt] = true;
                stack.Push((from, worstAt));
                stack.Push((worstAt, to));
            }
        }

        var thin = new List<(float X, float Y)>();
        for (var i = 0; i < loop.Count; i++) {
            if (keep[i]) {
                thin.Add(loop[i]);
            }
        }

        return thin;
    }

    private static float Area(List<(float X, float Y)> loop) {
        var area = 0f;
        for (var i = 0; i < loop.Count; i++) {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2;
    }

}
