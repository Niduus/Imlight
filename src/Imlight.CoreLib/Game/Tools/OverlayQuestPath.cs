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
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The walk from the wizard to a quest step along the area's streets, for the route line on the Minimap (the Quest
/// Tracker's picked quest). Found on the area's ground from above (<see cref="ZoneGround"/>, read only from the game's
/// collision file): streets and grass are walked, water mostly gone around, walls never crossed, and no ledge, drop or
/// sunken pool walked over (the ground's height between two cells; Niduus, 2026-11-12, the Vault of Ice's pool). When
/// no walk is found that way, heights are let go; when none is found at all, the line goes straight.
/// </summary>
internal static class OverlayQuestPath {

    private const int MAX_POINTS = 60;

    private const byte BLOCKED = 255;

    private sealed record Grid(float Left, float Top, float Cell, int Width, int Height, byte[] Cost, float[] Heights, float[] Lows = null) {

        /// <summary>
        /// Whether a step between two cells this far apart (in cells) climbs or drops more than a wizard can walk. A cell
        /// with ground at two heights (a hall under a balcony, a stream under a bridge) can be walked at either.
        /// </summary>
        internal bool TooSteep(int a, int b, float length) {
            if (Heights is null) {
                return false;
            }

            var most = 30f + (Cell * length);
            bool Near(float za, float zb) => float.IsNaN(za) || float.IsNaN(zb) || MathF.Abs(za - zb) <= most;
            var (ha, hb) = (Heights[a], Heights[b]);
            if (Near(ha, hb)) {
                return false;
            }

            if (Lows is null) {
                return true;
            }

            var (la, lb) = (Lows[a], Lows[b]);

            return !(Near(la, lb) || Near(ha, lb) || Near(la, hb));
        }

    }

    private static readonly ConcurrentDictionary<string, Grid> s_grids = new(StringComparer.OrdinalIgnoreCase);

    // The last walk found for each wizard: found again only when they moved off its start or the step changed.
    private static readonly ConcurrentDictionary<ulong, (string Zone, int Goal, int Start, DateTime At, List<(float X, float Y)> Points)> s_walks = new();

    /// <summary>The points to walk through from (<paramref name="fromX"/>, <paramref name="fromY"/>) to the step, ending at the step; straight when unknown.</summary>
    /// <remarks>
    /// <paramref name="body"/>: for a server-played wizard's own feet (the Quest Team, Ultimus), never through a wall
    /// (Niduus, 2026-11-13: "bots visually to players go through walls"): with no way all the way, it walks as far as
    /// the walls let it, and a step inside a wall ends on the nearest floor instead.
    /// </remarks>
    /// <param name="avoid">Creatures to keep away from (a wizard collecting quest objects goes around them, Niduus,
    /// 2026-11-13: "avoid battles as much as possible"): ground near them costs as much as water, so the way bends round.</param>
    internal static List<(float X, float Y)> Walk(ulong charId, string zonePath, float fromX, float fromY, float toX, float toY, bool body = false,
        IReadOnlyList<(float X, float Y)> avoid = null) {
        var straight = new List<(float X, float Y)> { (toX, toY) };
        try {
            if (GridOf(zonePath) is not { } grid) {
                return straight;
            }

            var start = Cell(grid, fromX, fromY);
            var goal = Cell(grid, toX, toY);
            if (start < 0 || goal < 0) {
                return straight;
            }

            if (avoid is { Count: > 0 }) {
                grid = Avoiding(grid, avoid);
            }
            else if (s_walks.TryGetValue(charId, out var last) && last.Zone == zonePath && last.Goal == goal && last.Start == start
                && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(20)) {
                return last.Points;
            }

            var (from, to) = (Open(grid, start), Open(grid, goal));
            var cells = Search(grid, from, to);
            // No way without a ledge (a ramp the file doesn't show as one): the way past walls only.
            if (cells is null) {
                grid = grid with { Heights = null };
                cells = Search(grid, from, to, partial: body);
            }

            // A wizard's step inside a wall (the grid is coarse, a door sits in its wall): kept when it is just by the
            // floor, otherwise it stops on the floor nearest it.
            var (endX, endY) = (toX, toY);
            if (body && cells is not null && cells.Count > 0) {
                var reached = cells[^1];
                var (cx, cy) = Center(grid, reached);
                if (grid.Cost[goal] == BLOCKED && MathF.Sqrt(((cx - toX) * (cx - toX)) + ((cy - toY) * (cy - toY))) > grid.Cell * 2) {
                    (endX, endY) = (cx, cy);
                }
                else if (reached != to && MathF.Sqrt(((cx - toX) * (cx - toX)) + ((cy - toY) * (cy - toY))) > 300) {
                    // Only part of the way: it ends where the walls let it (a door or exit just past its wall still counts).
                    (endX, endY) = (cx, cy);
                }
            }

            var points = cells is null ? straight : Smooth(grid, cells, endX, endY);
            s_walks[charId] = (zonePath, goal, start, DateTime.UtcNow, points);

            return points;
        }
        catch (Exception) {
            return straight;
        }
    }

    /// <summary>Reads an area's walk grid ahead of the first walk there (a server-played wizard that just arrived).</summary>
    internal static void Warm(string zonePath) => GridOf(zonePath);

    /// <summary>Forgets the last walk found for this wizard, so the next is found again from where it stands.</summary>
    internal static void Forget(ulong charId) => s_walks.TryRemove(charId, out _);

    private static Grid GridOf(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        if (s_grids.TryGetValue(zonePath, out var known)) {
            return known;
        }

        var ground = ZoneGround.WalkOf(zonePath);
        var grid = ground is null ? null : Costs(ground);
        if (s_grids.Count > 24) {
            s_grids.Clear();
        }

        s_grids[zonePath] = grid;

        return grid;
    }

    // Each cell's walking cost: streets and grass 1; nothing known (a floor the file doesn't mark) 20; water 25; walls never.
    private static Grid Costs(ZoneGround.WalkGround ground) {
        var cost = new byte[ground.Kinds.Length];
        for (var i = 0; i < cost.Length; i++) {
            cost[i] = ground.Kinds[i] switch {
                ZoneGround.PATH or ZoneGround.GRASS => 1,
                ZoneGround.WALL => BLOCKED,
                ZoneGround.WATER => 25,
                _ => 20,
            };
        }

        return new Grid(ground.Left, ground.Top, ground.Cell, ground.Width, ground.Height, cost, ground.Heights, ground.Lows);
    }

    private static int Cell(Grid grid, float x, float y) {
        var cx = (int) MathF.Floor((x - grid.Left) / grid.Cell);
        var cy = (int) MathF.Floor((grid.Top - y) / grid.Cell);
        if (cx < 0 || cy < 0 || cx >= grid.Width || cy >= grid.Height) {
            return -1;
        }

        return (cy * grid.Width) + cx;
    }

    // A wizard (or step) standing in a wall cell (the grid is coarse): the nearest cell that can be walked.
    private static int Open(Grid grid, int cell) {
        if (grid.Cost[cell] != BLOCKED) {
            return cell;
        }

        var (x, y) = (cell % grid.Width, cell / grid.Width);
        for (var r = 1; r < 6; r++) {
            for (var dy = -r; dy <= r; dy++) {
                for (var dx = -r; dx <= r; dx++) {
                    var (nx, ny) = (x + dx, y + dy);
                    if (nx >= 0 && ny >= 0 && nx < grid.Width && ny < grid.Height && grid.Cost[(ny * grid.Width) + nx] != BLOCKED) {
                        return (ny * grid.Width) + nx;
                    }
                }
            }
        }

        return cell;
    }

    private static readonly (int Dx, int Dy, float Len)[] s_moves = [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f),
    ];

    /// <summary>How far round a creature a collecting wizard keeps, in game units.</summary>
    private const float AVOID_RADIUS = 320f;

    // A copy of the grid where the ground near each creature costs as much as water (walls stay walls).
    private static Grid Avoiding(Grid grid, IReadOnlyList<(float X, float Y)> creatures) {
        var cost = (byte[]) grid.Cost.Clone();
        var reach = (int) MathF.Ceiling(AVOID_RADIUS / grid.Cell);
        foreach (var (x, y) in creatures) {
            var cx = (int) MathF.Floor((x - grid.Left) / grid.Cell);
            var cy = (int) MathF.Floor((grid.Top - y) / grid.Cell);
            for (var dy = -reach; dy <= reach; dy++) {
                for (var dx = -reach; dx <= reach; dx++) {
                    var (nx, ny) = (cx + dx, cy + dy);
                    if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height || (dx * dx) + (dy * dy) > reach * reach) {
                        continue;
                    }

                    var at = (ny * grid.Width) + nx;
                    if (cost[at] != BLOCKED && cost[at] < 30) {
                        cost[at] = 30;
                    }
                }
            }
        }

        return grid with { Cost = cost };
    }

    private static (float X, float Y) Center(Grid grid, int cell)
        => (grid.Left + ((cell % grid.Width) + 0.5f) * grid.Cell, grid.Top - ((cell / grid.Width) + 0.5f) * grid.Cell);

    // A* from start to goal over the costs; null when there is no way (with partial, the way to the cell it got nearest).
    private static List<int> Search(Grid grid, int start, int goal, bool partial = false) {
        if (start == goal) {
            return [goal];
        }

        var size = grid.Width * grid.Height;
        var best = new float[size];
        Array.Fill(best, float.MaxValue);
        var cameFrom = new int[size];
        var (gx, gy) = (goal % grid.Width, goal / grid.Width);
        float Guess(int cell) {
            var (x, y) = (cell % grid.Width, cell / grid.Width);
            var (ax, ay) = (Math.Abs(x - gx), Math.Abs(y - gy));

            return Math.Max(ax, ay) + (0.4142f * Math.Min(ax, ay));
        }

        var open = new PriorityQueue<int, float>();
        best[start] = 0;
        cameFrom[start] = -1;
        open.Enqueue(start, Guess(start));
        var looked = 0;
        var (nearest, nearestGuess) = (start, Guess(start));
        List<int> PathTo(int end) {
            var path = new List<int>();
            for (var at = end; at != -1; at = cameFrom[at]) {
                path.Add(at);
            }

            path.Reverse();

            return path;
        }

        while (open.TryDequeue(out var cell, out var priority)) {
            if (cell == goal) {
                return PathTo(goal);
            }

            if (++looked > 400000) {
                return partial ? PathTo(nearest) : null;
            }

            if (Guess(cell) < nearestGuess) {
                (nearest, nearestGuess) = (cell, Guess(cell));
            }

            // An old entry for a cell reached more cheaply since.
            if (priority - Guess(cell) > best[cell] + 0.001f) {
                continue;
            }

            var (x, y) = (cell % grid.Width, cell / grid.Width);
            foreach (var (dx, dy, len) in s_moves) {
                var (nx, ny) = (x + dx, y + dy);
                if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height) {
                    continue;
                }

                var next = (ny * grid.Width) + nx;
                var cost = grid.Cost[next];
                // No cutting a wall's corner on a slant.
                if (cost == BLOCKED || grid.TooSteep(cell, next, len)
                    || (dx != 0 && dy != 0 && (grid.Cost[(y * grid.Width) + nx] == BLOCKED || grid.Cost[(ny * grid.Width) + x] == BLOCKED))) {
                    continue;
                }

                var through = best[cell] + (len * cost);
                if (through < best[next]) {
                    best[next] = through;
                    cameFrom[next] = cell;
                    open.Enqueue(next, through + Guess(next));
                }
            }
        }

        return partial ? PathTo(nearest) : null;
    }

    // Straight runs where the cells between are as easy to walk, so the line has few bends; then the step itself.
    private static List<(float X, float Y)> Smooth(Grid grid, List<int> cells, float toX, float toY) {
        var kept = new List<int>();
        var from = 0;
        while (from < cells.Count - 1) {
            var to = cells.Count - 1;
            while (to > from + 1 && !Clear(grid, cells[from], cells[to])) {
                to--;
            }

            kept.Add(cells[to]);
            from = to;
        }

        var points = new List<(float X, float Y)>();
        foreach (var cell in kept) {
            points.Add((grid.Left + ((cell % grid.Width) + 0.5f) * grid.Cell, grid.Top - ((cell / grid.Width) + 0.5f) * grid.Cell));
        }

        if (points.Count > 0) {
            points[^1] = (toX, toY);
        }
        else {
            points.Add((toX, toY));
        }

        if (points.Count > MAX_POINTS) {
            points = [.. points.GetRange(0, MAX_POINTS - 1), (toX, toY)];
        }

        return points;
    }

    // Whether the straight line between two cells only crosses cells as cheap as the cheaper end (no wall, no water, no void).
    private static bool Clear(Grid grid, int a, int b) {
        var (x0, y0) = (a % grid.Width, a / grid.Width);
        var (x1, y1) = (b % grid.Width, b / grid.Width);
        var most = Math.Min(grid.Cost[a], grid.Cost[b]);
        var (dx, dy) = (Math.Abs(x1 - x0), -Math.Abs(y1 - y0));
        var (sx, sy) = (x0 < x1 ? 1 : -1, y0 < y1 ? 1 : -1);
        var err = dx + dy;
        var before = -1;
        while (true) {
            var here = (y0 * grid.Width) + x0;
            if (grid.Cost[here] > most || (before >= 0 && grid.TooSteep(before, here, 1.4142f))) {
                return false;
            }

            before = here;

            if (x0 == x1 && y0 == y1) {
                return true;
            }

            var e2 = 2 * err;
            var (stepX, stepY) = (e2 >= dy, e2 <= dx);
            // A slant between two wall cells (their corners touch) is a wall too: the line can't slip through it.
            if (stepX && stepY && (grid.Cost[(y0 * grid.Width) + x0 + sx] == BLOCKED || grid.Cost[((y0 + sy) * grid.Width) + x0] == BLOCKED)) {
                return false;
            }

            if (stepX) {
                err += dy;
                x0 += sx;
            }

            if (stepY) {
                err += dx;
                y0 += sy;
            }
        }
    }

}
