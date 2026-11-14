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
using System.Linq;
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
        /// The costs with the cells that show no floor counted as an unmarked floor (walls still walls): the last try of a
        /// wizard's own walk when the floor the file shows leaves it no way at all (Niduus's log, 2026-11-13: Ultimus stood
        /// on The Oasis's Spiral Door platform for over an hour; the stairs down show no floor in the file).
        /// </summary>
        internal byte[] Loose { get; init; }

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
        if (body) {
            // A walk that already got a wizard from about here to about there (RouteMemory): taken as it is. Else the way
            // finder's own, bent round the spots where wizards got stuck on this street.
            try {
                if (RouteMemory.Proven(zonePath, fromX, fromY, toX, toY, avoid) is { Count: > 0 } proven) {
                    return proven;
                }

                avoid = RouteMemory.WithStuckSpots(zonePath, toX, toY, avoid);
            }
            catch (Exception) {
                // The way finder's own below.
            }
        }

        return WalkCore(charId, zonePath, fromX, fromY, toX, toY, body, avoid, remember: true);
    }

    // Whether this thread's last WalkCore found a way on the floor (not the straight fallback).
    [ThreadStatic]
    private static bool t_found;

    private static List<(float X, float Y)> WalkCore(ulong charId, string zonePath, float fromX, float fromY, float toX, float toY, bool body,
        IReadOnlyList<(float X, float Y)> avoid, bool remember) {
        var straight = new List<(float X, float Y)> { (toX, toY) };
        // Ground not known (no collision file, a spot off the map): the straight line is all there is.
        t_found = true;
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
            else if (remember && s_walks.TryGetValue(charId, out var last) && last.Zone == zonePath && last.Goal == goal && last.Start == start
                && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(20)) {
                return last.Points;
            }

            // A step standing in a wall cell (an NPC in an alcove, a door in its wall) opens onto the floor on the wizard's
            // side, not the first floor cell found (that could be behind the wall, and the way then went all round it;
            // Niduus, 2026-11-13: "This needs better intel, logic and behavior").
            var from = Open(grid, start);
            var to = Open(grid, goal, IslandLabel(zonePath, grid, from));
            var cells = Search(grid, from, to);
            // No way without a ledge (a ramp the file doesn't show as one): the way past walls only.
            if (cells is null) {
                grid = grid with { Heights = null };
                cells = Search(grid, from, to, partial: body);
            }

            // A wizard's own walk that gets nowhere near the step on the floor the file shows (a stair or ramp the file
            // shows no floor on cuts it off): over the unshown floor too, walls still walls. Only when that reaches the step.
            if (body && grid.Loose is { } looseCost && (cells is null || cells.Count == 0 || cells[^1] != to)) {
                var looseGrid = grid with { Cost = avoid is { Count: > 0 } ? Avoiding(grid with { Cost = looseCost }, avoid).Cost : looseCost, Heights = null };
                var looseTo = Open(looseGrid, goal);
                if (Search(looseGrid, Open(looseGrid, start), looseTo) is { Count: > 0 } looseCells) {
                    grid = looseGrid;
                    to = looseTo;
                    cells = looseCells;
                }
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

            t_found = cells is not null && (!body || cells.Count == 0 || cells[^1] == to);
            var points = cells is null ? straight : Smooth(grid, cells, endX, endY);
            if (remember) {
                s_walks[charId] = (zonePath, goal, start, DateTime.UtcNow, points);
            }

            return points;
        }
        catch (Exception) {
            return straight;
        }
    }

    /// <summary>One spot of a <see cref="Route"/>: walk to it; Pad means it is a teleporter, and the next spot is where it lands.</summary>
    internal readonly record struct Spot(float X, float Y, bool Pad = false);

    // The last route found for each wizard (found again when they moved, the step changed, or it is a few seconds old).
    private static readonly ConcurrentDictionary<ulong, (string Zone, int From, int To, DateTime At, List<Spot> Spots)> s_routes = new();

    /// <summary>
    /// The legal way from (<paramref name="fromX"/>, <paramref name="fromY"/>) to the step, the one a player can follow
    /// exactly (Niduus, 2026-11-13: "I want the draws on the mini map to be of legal routes, that way not only can the
    /// player follow it completely but the bots (QT and Ultimus) can learn off the data"): on floor only (never over the
    /// Minimap's black, through a wall or off a ledge), and, when the step is on ground walls, water or a ledge cut off
    /// (Triton Avenue's Kraken isle, its raised lawn), through the area's teleporters (<see cref="Ultimus.UltimusPads"/>:
    /// the area's own files and the ones the bots found), at most four. The Minimap's quest line, the Quest Team and
    /// Ultimus all take their way from here.
    /// </summary>
    /// <param name="avoid">Spots to keep away from (the street's creature walks, <see cref="RouteMobs.Avoid"/>).</param>
    internal static List<Spot> Route(ulong charId, string zonePath, float fromX, float fromY, float toX, float toY,
        IReadOnlyList<(float X, float Y)> avoid = null) {
        var key = (Zone: zonePath ?? string.Empty, From: Rough(fromX, fromY), To: Rough(toX, toY));
        if (s_routes.TryGetValue(charId, out var last) && last.Zone == key.Zone && last.From == key.From && last.To == key.To
            && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(10)) {
            return last.Spots;
        }

        var spots = new List<Spot>();
        try {
            var (x, y) = (fromX, fromY);
            var taken = new List<Ultimus.UltimusPads.Pad>();
            for (var hop = 0; hop < 4 && Apart(zonePath, x, y, toX, toY); hop++) {
                // The pads that chain to the step's ground; else, as a player would try, a pad here that lands nearer it.
                if ((Ultimus.UltimusPads.Toward(zonePath, x, y, toX, toY, taken)
                     ?? Ultimus.UltimusPads.Another(zonePath, x, y, toX, toY, taken)) is not { } pad) {
                    break;
                }

                taken.Add(pad);
                foreach (var (px, py) in WalkCore(charId, zonePath, x, y, pad.X, pad.Y, false, avoid, remember: false)) {
                    spots.Add(new Spot(px, py));
                }

                spots.Add(new Spot(pad.X, pad.Y, Pad: true));
                (x, y) = (pad.ToX, pad.ToY);
                spots.Add(new Spot(x, y));
            }

            // The last walk, only when it is a real one: never a straight line over the Minimap's black, through a wall or
            // off a ledge (Niduus, 2026-11-13: "it still draws through"). With no legal way, the line stops where the
            // known way stops (at the wizard when there is none): the ring still shows the step.
            if (!Apart(zonePath, x, y, toX, toY)) {
                var final = WalkCore(charId, zonePath, x, y, toX, toY, false, avoid, remember: false);
                if (t_found) {
                    spots.AddRange(final.Select(p => new Spot(p.X, p.Y)));
                }
            }

            if (spots.Count == 0) {
                spots.Add(new Spot(fromX, fromY));
            }
        }
        catch (Exception) {
            spots = [new Spot(toX, toY)];
        }

        if (s_routes.Count > 200) {
            s_routes.Clear();
        }

        s_routes[charId] = (key.Zone, key.From, key.To, DateTime.UtcNow, spots);

        return spots;
    }

    /// <summary>
    /// A server-played wizard's walk toward the step the legal way (the Quest Team): the same as <see cref="Walk"/>, except
    /// that when the step is on ground cut off from where it stands, it walks onto the first teleporter of the
    /// <see cref="Route"/> instead (after it lands, the next walk goes on from there).
    /// </summary>
    internal static List<(float X, float Y)> WalkLegal(ulong charId, string zonePath, float fromX, float fromY, float toX, float toY,
        IReadOnlyList<(float X, float Y)> avoid = null) {
        try {
            if (Apart(zonePath, fromX, fromY, toX, toY)
                && Ultimus.UltimusPads.Toward(zonePath, fromX, fromY, toX, toY) is { } pad) {
                return Walk(charId, zonePath, fromX, fromY, pad.X, pad.Y, body: true, avoid: avoid);
            }

            // A teleporter that gets there much faster than walking (one any bot found counts for all).
            if (Ultimus.UltimusPads.Faster(zonePath, fromX, fromY, toX, toY) is { } faster) {
                return Walk(charId, zonePath, fromX, fromY, faster.X, faster.Y, body: true, avoid: avoid);
            }
        }
        catch (Exception) {
            // The plain walk below.
        }

        return Walk(charId, zonePath, fromX, fromY, toX, toY, body: true, avoid: avoid);
    }

    // A spot rounded to 60 units, so a wizard standing still keeps the route it has.
    private static int Rough(float x, float y) => HashCode.Combine((int) MathF.Floor(x / 60), (int) MathF.Floor(y / 60));

    private static readonly ConcurrentDictionary<string, int[]> s_islands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which island of dry floor the spot is on: floor that walls and water cut off from the rest of the area (the Kraken
    /// isle in Triton Avenue, left only by its teleporter) is an island of its own. -1 when the area's ground is unknown.
    /// </summary>
    internal static int IslandOf(string zonePath, float x, float y) {
        try {
            if (GridOf(zonePath) is not { } grid) {
                return -1;
            }

            var cell = Cell(grid, x, y);
            if (cell < 0) {
                return -1;
            }

            if (s_islands.Count > 24) {
                s_islands.Clear();
            }

            var labels = s_islands.GetOrAdd(zonePath, _ => Islands(grid));
            if (labels[cell] >= 0) {
                return labels[cell];
            }

            // Standing in a wall or water cell (the grid is coarse): the island of the nearest dry floor.
            var (cx, cy) = (cell % grid.Width, cell / grid.Width);
            for (var r = 1; r < 6; r++) {
                for (var dy = -r; dy <= r; dy++) {
                    for (var dx = -r; dx <= r; dx++) {
                        var (nx, ny) = (cx + dx, cy + dy);
                        if (nx >= 0 && ny >= 0 && nx < grid.Width && ny < grid.Height && labels[(ny * grid.Width) + nx] >= 0) {
                            return labels[(ny * grid.Width) + nx];
                        }
                    }
                }
            }

            return -1;
        }
        catch (Exception) {
            return -1;
        }
    }

    /// <summary>Whether two spots of an area are on different islands (false when unknown, so the walk is tried as before).</summary>
    internal static bool Apart(string zonePath, float ax, float ay, float bx, float by) {
        var (a, b) = (IslandOf(zonePath, ax, ay), IslandOf(zonePath, bx, by));

        return a >= 0 && b >= 0 && a != b;
    }

    /// <summary>
    /// How big the island the spot is on is, against the biggest island of the area (0 to 1; -1 when unknown). A small patch
    /// cut off by a fence or a ledge (a grass strip behind a low wall) is a tiny share.
    /// </summary>
    internal static double IslandShare(string zonePath, float x, float y) {
        try {
            var island = IslandOf(zonePath, x, y);
            if (island < 0 || !s_islands.TryGetValue(zonePath, out var labels)) {
                return -1;
            }

            var sizes = new Dictionary<int, int>();
            foreach (var label in labels) {
                if (label >= 0) {
                    sizes[label] = sizes.GetValueOrDefault(label) + 1;
                }
            }

            var biggest = 0;
            foreach (var size in sizes.Values) {
                biggest = Math.Max(biggest, size);
            }

            return biggest <= 0 ? -1 : sizes.GetValueOrDefault(island) / (double) biggest;
        }
        catch (Exception) {
            return -1;
        }
    }

    private const byte WATER_COST = 25;

    // Each dry floor cell's island number (walls and water are -1), found by flooding out from every unnumbered cell.
    private static int[] Islands(Grid grid) {
        var labels = new int[grid.Cost.Length];
        Array.Fill(labels, -1);
        var next = 0;
        var queue = new Queue<int>();
        for (var i = 0; i < labels.Length; i++) {
            if (labels[i] >= 0 || grid.Cost[i] is BLOCKED or WATER_COST) {
                continue;
            }

            labels[i] = next;
            queue.Enqueue(i);
            while (queue.TryDequeue(out var cell)) {
                var (x, y) = (cell % grid.Width, cell / grid.Width);
                foreach (var (dx, dy, len) in s_moves) {
                    var (nx, ny) = (x + dx, y + dy);
                    if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height) {
                        continue;
                    }

                    // A ledge too high to climb or drop parts islands too (Niduus, 2026-11-13: Ultimus stood at the edge
                    // of the raised lawn by "To Lower Triton", the way down being that teleporter, not the cliff).
                    var n = (ny * grid.Width) + nx;
                    if (labels[n] < 0 && grid.Cost[n] is not (BLOCKED or WATER_COST) && !grid.TooSteep(cell, n, len)) {
                        labels[n] = next;
                        queue.Enqueue(n);
                    }
                }
            }

            next++;
        }

        return labels;
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

    // Each cell's walking cost: streets and grass 1; a floor the file doesn't mark 20; water 25; walls never; and never where
    // there is no floor at all, the black of the Minimap (Niduus, 2026-11-13: "the black zones are barrier ... Can we have
    // Ultimus train off that intel so it knows where the valid zone walks are?"). Such void was costed as an unmarked
    // floor, so a way could cut across it, off the edge of a raised lawn or into the gap behind a fence.
    private static Grid Costs(ZoneGround.WalkGround ground) {
        var cost = new byte[ground.Kinds.Length];
        var loose = new byte[ground.Kinds.Length];
        for (var i = 0; i < cost.Length; i++) {
            var noFloor = Void(ground, i) && !Between(ground, i);
            // The loose way (a stair or ramp the file shows no floor on) crosses only a narrow gap of the black, floor on both
            // sides within a few cells, never open void (Niduus, 2026-11-14: "MAke sure he respects the world/games
            // boundaries": he walked out of bounds in the Bazaar).
            loose[i] = noFloor && !NarrowGap(ground, i) ? BLOCKED : (byte) (ground.Kinds[i] switch {
                ZoneGround.PATH or ZoneGround.GRASS => 1,
                ZoneGround.WALL => BLOCKED,
                ZoneGround.WATER => 25,
                _ => 20,
            });
            cost[i] = noFloor ? BLOCKED : loose[i];
        }

        return new Grid(ground.Left, ground.Top, ground.Cell, ground.Width, ground.Height, cost, ground.Heights, ground.Lows) { Loose = loose };
    }

    // How many cells of black a stair or ramp may span on the loose way.
    private const int GAP_MOST = 3;

    // A void cell with floor within GAP_MOST cells on both sides (left and right, or up and down).
    private static bool NarrowGap(ZoneGround.WalkGround ground, int i) {
        var (x, y, w, h) = (i % ground.Width, i / ground.Width, ground.Width, ground.Height);
        bool Floor(int nx, int ny) => nx >= 0 && ny >= 0 && nx < w && ny < h && ground.Kinds[(ny * w) + nx] != ZoneGround.WALL
                                      && !Void(ground, (ny * w) + nx);
        bool Side(int dx, int dy) {
            for (var k = 1; k <= GAP_MOST; k++) {
                if (Floor(x + (dx * k), y + (dy * k))) {
                    return true;
                }
            }

            return false;
        }

        return (Side(-1, 0) && Side(1, 0)) || (Side(0, -1) && Side(0, 1));
    }

    /// <summary>
    /// Whether (x, y) is on ground a wizard may stand on in this area: floor, grass, water or a narrow gap a stair spans, not
    /// a wall and not the Minimap's black. True when the area's ground is not known (nothing to check against).
    /// </summary>
    internal static bool OnFloor(string zonePath, float x, float y) {
        try {
            if (GridOf(zonePath) is not { } grid) {
                return true;
            }

            var cell = Cell(grid, x, y);

            return cell >= 0 && (grid.Loose ?? grid.Cost)[cell] != BLOCKED;
        }
        catch (Exception) {
            return true;
        }
    }

    /// <summary>The nearest spot on the floor to (x, y) within a few cells, or null.</summary>
    internal static (float X, float Y)? NearestFloor(string zonePath, float x, float y) {
        try {
            if (GridOf(zonePath) is not { } grid) {
                return null;
            }

            var cell = Cell(grid, x, y);
            if (cell < 0) {
                return null;
            }

            var open = Open(grid, cell);

            return grid.Cost[open] == BLOCKED ? null : Center(grid, open);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// A walk kept on the legal ground (Niduus, 2026-11-14: bounds rule): each leg is checked along its length, and the walk
    /// stops at the last spot on the floor before a leg goes over a wall or the black. The first few steps may start in a
    /// wall cell (a wizard in a doorway, the grid is coarse) and the last spot may sit just inside a wall (a door, an NPC
    /// behind a counter's edge). <paramref name="cut"/> is true when it was cut short.
    /// </summary>
    internal static List<(float X, float Y)> Legal(string zonePath, float fromX, float fromY, List<(float X, float Y)> points, out bool cut) {
        cut = false;
        if (points is null || points.Count == 0) {
            return points ?? [];
        }

        try {
            if (GridOf(zonePath) is not { } grid) {
                return points;
            }

            var cost = grid.Loose ?? grid.Cost;
            var kept = new List<(float X, float Y)>(points.Count);
            var (ax, ay) = (fromX, fromY);
            var lastGood = (X: fromX, Y: fromY);
            var started = false;
            for (var p = 0; p < points.Count; p++) {
                var (bx, by) = points[p];
                var length = MathF.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
                var step = MathF.Max(4f, grid.Cell / 2);
                var count = Math.Max(1, (int) MathF.Ceiling(length / step));
                for (var i = 1; i <= count; i++) {
                    var t = i / (float) count;
                    var (x, y) = (ax + ((bx - ax) * t), ay + ((by - ay) * t));
                    var cell = Cell(grid, x, y);
                    var ok = cell >= 0 && cost[cell] != BLOCKED;
                    if (ok) {
                        started = true;
                        lastGood = (x, y);
                        continue;
                    }

                    // Leaving a doorway or a wall cell it stands in: fine while it has not reached floor yet and is near.
                    var nearStart = !started && MathF.Sqrt(((x - fromX) * (x - fromX)) + ((y - fromY) * (y - fromY))) <= grid.Cell * 2;
                    // The end, just inside a wall (a door, an exit's trigger at the edge of the map, a counter's edge): fine
                    // when it is by the floor (the last two legs, within a few cells of the last floor).
                    var gone = MathF.Sqrt(((x - lastGood.X) * (x - lastGood.X)) + ((y - lastGood.Y) * (y - lastGood.Y)));
                    var nearEnd = p >= points.Count - 2 && gone <= grid.Cell * 3;
                    // A corner the walk's smoothing cuts (less than a cell of wall between floor): fine.
                    var corner = gone <= grid.Cell;
                    if (nearStart || nearEnd || corner) {
                        continue;
                    }

                    cut = true;
                    if (MathF.Sqrt(((lastGood.X - ax) * (lastGood.X - ax)) + ((lastGood.Y - ay) * (lastGood.Y - ay))) > 1f) {
                        kept.Add(lastGood);
                    }

                    return kept;
                }

                kept.Add((bx, by));
                (ax, ay) = (bx, by);
            }

            return kept;
        }
        catch (Exception) {
            return points;
        }
    }

    private static bool Void(ZoneGround.WalkGround ground, int i)
        => ground.Kinds[i] == ZoneGround.NONE && (ground.Heights is null || float.IsNaN(ground.Heights[i]));

    // A one-cell crack with floor on both sides (a plank or ledge thinner than a cell missed the cell's middle): floor too.
    private static bool Between(ZoneGround.WalkGround ground, int i) {
        var (x, y, w, h) = (i % ground.Width, i / ground.Width, ground.Width, ground.Height);
        bool Floor(int nx, int ny) => nx >= 0 && ny >= 0 && nx < w && ny < h && ground.Kinds[(ny * w) + nx] != ZoneGround.WALL
                                      && !Void(ground, (ny * w) + nx);

        return (Floor(x - 1, y) && Floor(x + 1, y)) || (Floor(x, y - 1) && Floor(x, y + 1));
    }

    private static int Cell(Grid grid, float x, float y) {
        var cx = (int) MathF.Floor((x - grid.Left) / grid.Cell);
        var cy = (int) MathF.Floor((grid.Top - y) / grid.Cell);
        if (cx < 0 || cy < 0 || cx >= grid.Width || cy >= grid.Height) {
            return -1;
        }

        return (cy * grid.Width) + cx;
    }

    // A wizard (or step) standing in a wall cell (the grid is coarse): the nearest cell that can be walked, one on the
    // island <paramref name="island"/> (the wizard's own floor) when there is one that near.
    private static int Open(Grid grid, int cell, (int[] Labels, int Island) island = default) {
        if (grid.Cost[cell] != BLOCKED) {
            return cell;
        }

        var (x, y) = (cell % grid.Width, cell / grid.Width);
        var (best, bestFar, mine, mineFar) = (-1, int.MaxValue, -1, int.MaxValue);
        for (var dy = -OPEN_REACH; dy <= OPEN_REACH; dy++) {
            for (var dx = -OPEN_REACH; dx <= OPEN_REACH; dx++) {
                var (nx, ny) = (x + dx, y + dy);
                if (nx < 0 || ny < 0 || nx >= grid.Width || ny >= grid.Height || grid.Cost[(ny * grid.Width) + nx] == BLOCKED) {
                    continue;
                }

                var (at, far) = ((ny * grid.Width) + nx, (dx * dx) + (dy * dy));
                if (far < bestFar) {
                    (best, bestFar) = (at, far);
                }

                if (island.Labels is not null && island.Island >= 0 && island.Labels[at] == island.Island && far < mineFar) {
                    (mine, mineFar) = (at, far);
                }
            }
        }

        if (mine >= 0) {
            return mine;
        }

        if (best >= 0) {
            return best;
        }

        return cell;
    }

    // How far (in cells) a spot in a wall looks for floor.
    private const int OPEN_REACH = 7;

    // The island labels of the area and the island of the cell (-1 when not known).
    private static (int[] Labels, int Island) IslandLabel(string zonePath, Grid grid, int cell) {
        try {
            if (s_islands.Count > 24) {
                s_islands.Clear();
            }

            var labels = s_islands.GetOrAdd(zonePath, _ => Islands(grid));

            return labels.Length == grid.Cost.Length ? (labels, labels[cell]) : default;
        }
        catch (Exception) {
            return default;
        }
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
