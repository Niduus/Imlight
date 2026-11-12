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
/// collision file): streets and grass are walked, water mostly gone around, walls never crossed. When no walk is found
/// (no ground, or an area with floors above each other), the line goes straight.
/// </summary>
internal static class OverlayQuestPath {

    // The ground is looked at on a grid no bigger than this each way (finer grids are made coarser first).
    private const int MAX_SIDE = 256;
    private const int MAX_POINTS = 60;

    private const byte BLOCKED = 255;

    private sealed record Grid(float Left, float Top, float Cell, int Width, int Height, byte[] Cost);

    private static readonly ConcurrentDictionary<string, Grid> s_grids = new(StringComparer.OrdinalIgnoreCase);

    // The last walk found for each wizard: found again only when they moved off its start or the step changed.
    private static readonly ConcurrentDictionary<ulong, (string Zone, int Goal, int Start, DateTime At, List<(float X, float Y)> Points)> s_walks = new();

    /// <summary>The points to walk through from (<paramref name="fromX"/>, <paramref name="fromY"/>) to the step, ending at the step; straight when unknown.</summary>
    internal static List<(float X, float Y)> Walk(ulong charId, string zonePath, float fromX, float fromY, float toX, float toY) {
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

            if (s_walks.TryGetValue(charId, out var last) && last.Zone == zonePath && last.Goal == goal && last.Start == start
                && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(20)) {
                return last.Points;
            }

            var cells = Search(grid, Open(grid, start), Open(grid, goal));
            var points = cells is null ? straight : Smooth(grid, cells, toX, toY);
            s_walks[charId] = (zonePath, goal, start, DateTime.UtcNow, points);

            return points;
        }
        catch (Exception) {
            return straight;
        }
    }

    private static Grid GridOf(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        if (s_grids.TryGetValue(zonePath, out var known)) {
            return known;
        }

        var ground = ZoneGround.Of(zonePath);
        var grid = ground.IsEmpty ? null : Coarse(ground);
        if (s_grids.Count > 24) {
            s_grids.Clear();
        }

        s_grids[zonePath] = grid;

        return grid;
    }

    // The ground's cells as walking costs, at most MAX_SIDE each way: a coarse cell can be walked when any of its
    // cells is a street or grass, and is a wall only when it has a wall and nothing to walk on.
    private static Grid Coarse(ZoneGround.Ground ground) {
        var cells = new byte[ground.Width * ground.Height];
        var runs = Convert.FromBase64String(ground.Runs);
        var at = 0;
        for (var i = 0; i + 2 < runs.Length && at < cells.Length; i += 3) {
            var count = Math.Min(runs[i + 1] | (runs[i + 2] << 8), cells.Length - at);
            Array.Fill(cells, runs[i], at, count);
            at += count;
        }

        var step = Math.Max(1, (int) Math.Ceiling(Math.Max(ground.Width, ground.Height) / (double) MAX_SIDE));
        var width = (ground.Width + step - 1) / step;
        var height = (ground.Height + step - 1) / step;
        var cost = new byte[width * height];
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                bool walk = false, wall = false, water = false;
                for (var dy = 0; dy < step && !walk; dy++) {
                    for (var dx = 0; dx < step; dx++) {
                        var (fx, fy) = ((x * step) + dx, (y * step) + dy);
                        if (fx >= ground.Width || fy >= ground.Height) {
                            continue;
                        }

                        switch (cells[(fy * ground.Width) + fx]) {
                            case ZoneGround.PATH or ZoneGround.GRASS:
                                walk = true;
                                break;
                            case ZoneGround.WALL:
                                wall = true;
                                break;
                            case ZoneGround.WATER:
                                water = true;
                                break;
                        }
                    }
                }

                // Streets and grass 1; nothing known (a bridge, a floor the file doesn't mark) 8; water 25; walls never.
                cost[(y * width) + x] = walk ? (byte) 1 : wall ? BLOCKED : water ? (byte) 25 : (byte) 8;
            }
        }

        return new Grid(ground.Left, ground.Top, ground.Cell * step, width, height, cost);
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

    // A* from start to goal over the costs; null when there is no way.
    private static List<int> Search(Grid grid, int start, int goal) {
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
        while (open.TryDequeue(out var cell, out var priority)) {
            if (cell == goal) {
                var path = new List<int>();
                for (var at = goal; at != -1; at = cameFrom[at]) {
                    path.Add(at);
                }

                path.Reverse();

                return path;
            }

            if (++looked > 120000) {
                return null;
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
                if (cost == BLOCKED || (dx != 0 && dy != 0 && (grid.Cost[(y * grid.Width) + nx] == BLOCKED || grid.Cost[(ny * grid.Width) + x] == BLOCKED))) {
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

        return null;
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
        while (true) {
            if (grid.Cost[(y0 * grid.Width) + x0] > most) {
                return false;
            }

            if (x0 == x1 && y0 == y1) {
                return true;
            }

            var e2 = 2 * err;
            if (e2 >= dy) {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx) {
                err += dx;
                y0 += sy;
            }
        }
    }

}
