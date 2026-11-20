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
 *
 * ========================================================================
 * ZONE NAV
 * ========================================================================
 *
 * PURPOSE:
 * The game's own walk grid of a zone (zone.nav in the zone's .wad, read only): points on open ground, about 480
 * units apart, where creatures and wizards can stand. A teleport that lands on one of them never puts the wizard
 * inside a wall (Niduus 2026-11-02: the Firecat Alley teleporter put him in a wall, unable to move).
 *
 * The file: u16 last index, u16 point count, u16 (unused); per point x, y, z (float) and its u16 index; then a u32
 * edge count and the edges (u16 from, u16 to), which are not needed here.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class ZoneNav {

    private const string NAV_FILE_NAME = "zone.nav";

    private static readonly ConcurrentDictionary<string, Vector3[]> s_points = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The zone's walk points, or an empty list when its .wad has no zone.nav.</summary>
    public static Vector3[] Points(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return [];
        }

        return s_points.GetOrAdd(zonePath, Load);
    }

    /// <summary>
    /// The walk point nearest (x, y) on the ground, among those no more than <paramref name="maxDistance"/> away and
    /// no more than <paramref name="maxHeight"/> above or below z; null when there is none.
    /// </summary>
    public static Vector3? Nearest(string zonePath, float x, float y, float z, float maxDistance, float maxHeight) {
        Vector3? best = null;
        var bestDistance = maxDistance * maxDistance;
        foreach (var point in Points(zonePath)) {
            if (MathF.Abs(point.Z - z) > maxHeight) {
                continue;
            }

            var (dx, dy) = (point.X - x, point.Y - y);
            var distance = (dx * dx) + (dy * dy);
            if (distance <= bestDistance) {
                bestDistance = distance;
                best = point;
            }
        }

        return best;
    }

    private static Vector3[] Load(string zonePath) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || wad.OpenFile(NAV_FILE_NAME) is not { } memory) {
                return [];
            }

            return Parse(memory.ToArray());
        } catch (Exception ex) {
            Logger.Debug("Zone {0}: could not read its walk grid ({1}).", Logger.Args(zonePath, ex.Message));

            return [];
        }
    }

    // The game's own walk graph of the zone (zone.nav): its points and which point leads to which. The ground grid is
    // flat and coarse; this graph knows the floors of a tower and the narrow ledges and bridges between them (Niduus,
    // 2026-11-15, Big Ben's Counterweight East: the way to Sprockets goes round the top floor's west side, down onto a
    // bridge at height 4200 and across to the middle; the grid showed that ledge as no floor and the bridge not at all).
    internal sealed record NavGraph(Vector3[] Nodes, int[][] Next, (Vector2 A, Vector2 B)[] Links);

    private static readonly ConcurrentDictionary<string, NavGraph> s_graphs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The zone's walk graph, or null when its .wad has no zone.nav (or it has no links).</summary>
    public static NavGraph Graph(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        if (s_graphs.Count > 48) {
            s_graphs.Clear();
        }

        return s_graphs.GetOrAdd(zonePath, z => {
            try {
                return ResourceManager.TryLoadArchive(z, out var wad) && wad.OpenFile(NAV_FILE_NAME) is { } memory ? ParseGraph(memory.ToArray()) : null;
            }
            catch (Exception ex) {
                Logger.Debug("Zone {0}: could not read its walk graph ({1}).", Logger.Args(z, ex.Message));

                return null;
            }
        });
    }

    /// <summary>The walk graph of a zone.nav's bytes: points (x, y, z and a number), then a count and pairs of linked points.</summary>
    internal static NavGraph ParseGraph(byte[] data) {
        if (data is null || data.Length < 6) {
            return null;
        }

        var count = BitConverter.ToUInt16(data, 2);
        var offset = 6;
        if (offset + (count * 14) + 4 > data.Length) {
            return null;
        }

        var nodes = new Vector3[count];
        for (var i = 0; i < count; i++, offset += 14) {
            nodes[i] = new Vector3(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8));
        }

        var linkCount = BitConverter.ToUInt32(data, offset);
        offset += 4;
        var next = new HashSet<int>[count];
        for (var i = 0; i < count; i++) {
            next[i] = [];
        }

        var links = new List<(Vector2, Vector2)>();
        for (var i = 0; i < linkCount && offset + 4 <= data.Length; i++, offset += 4) {
            int a = BitConverter.ToUInt16(data, offset), b = BitConverter.ToUInt16(data, offset + 2);
            if (a >= count || b >= count || a == b || !Finite(nodes[a]) || !Finite(nodes[b])) {
                continue;
            }

            // A link across half the area (Counterweight East's long hall 20000 away, joined to the tower by its teleport)
            // is a teleport's, not a walk.
            if (Vector2.Distance(new Vector2(nodes[a].X, nodes[a].Y), new Vector2(nodes[b].X, nodes[b].Y)) > LONGEST_LINK) {
                continue;
            }

            // Both ways: a player walks a link back as well as on.
            if (next[a].Add(b) | next[b].Add(a)) {
                links.Add((new Vector2(nodes[a].X, nodes[a].Y), new Vector2(nodes[b].X, nodes[b].Y)));
            }
        }

        return links.Count == 0 ? null : new NavGraph(nodes, Array.ConvertAll(next, n => n.ToArray()), [.. links]);
    }

    // The longest link walked; Counterweight East's longest walk is about 1800.
    private const float LONGEST_LINK = 3000f;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // How far from a spot (on the ground, and in height) the walk graph's first and last point may be.
    private const float SNAP = 500f;
    private const float SNAP_HEIGHT = 300f;

    /// <summary>
    /// The game's own way from one spot to another over the zone's walk graph, floor by floor (heights known: the points
    /// nearest each spot on its own floor): the points to walk, ending at the step. Null when the graph has no way.
    /// </summary>
    public static List<Vector3> Route(string zonePath, Vector3 from, Vector3 to) => Graph(zonePath) is { } graph ? Route(graph, from, to) : null;

    internal static List<Vector3> Route(NavGraph graph, Vector3 from, Vector3 to, bool onFloors = false) {
        // Timed for the log's speed line (ServerHealth): path finding is the server's busiest maths, so it shows best
        // whether a server built for live patches runs slower.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try {
            return RouteCore(graph, from, to, onFloors);
        }
        finally {
            ServerHealth.CountRoute(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    private static List<Vector3> RouteCore(NavGraph graph, Vector3 from, Vector3 to, bool onFloors) {
        var starts = onFloors ? NearOnFloor(graph, from) : Near(graph, from);
        var goals = onFloors ? NearOnFloor(graph, to) : Near(graph, to);
        if (starts.Count == 0 || goals.Count == 0) {
            return null;
        }

        var goalSet = new HashSet<int>(goals);
        var best = new float[graph.Nodes.Length];
        Array.Fill(best, float.MaxValue);
        var came = new int[graph.Nodes.Length];
        Array.Fill(came, -1);
        var open = new PriorityQueue<int, float>();
        foreach (var s in starts) {
            best[s] = Flat(graph.Nodes[s], from);
            open.Enqueue(s, best[s] + Vector3.Distance(graph.Nodes[s], to));
        }

        while (open.TryDequeue(out var at, out _)) {
            if (goalSet.Contains(at)) {
                var path = new List<Vector3> { to };
                for (var n = at; n != -1; n = came[n]) {
                    path.Add(graph.Nodes[n]);
                }

                path.Reverse();

                return path;
            }

            foreach (var n in graph.Next[at]) {
                var through = best[at] + Vector3.Distance(graph.Nodes[at], graph.Nodes[n]);
                if (through < best[n]) {
                    best[n] = through;
                    came[n] = at;
                    open.Enqueue(n, through + Vector3.Distance(graph.Nodes[n], to));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The game's own way between two floors: its first point on the wizard's own floor and its last on the step's (both
    /// heights must be known), so it climbs by the stairs or ramp the walk map shows. Null when there is none. Where the
    /// nearest points on the right floor are a little further off (a wizard standing under a raised door, the stairs a
    /// way back), they are looked for further out. Niduus's log, 2026-11-17: Cyndr, Garm and Kurrent stood under the
    /// Oasis's door to the Altar of Kings (at 523) on the ground at 202 for over an hour; the plain way snapped onto the
    /// door's own point 294 above them and walked straight on.
    /// </summary>
    public static List<Vector3> RouteOnFloors(string zonePath, Vector3 from, Vector3 to)
        => float.IsFinite(from.Z) && float.IsFinite(to.Z) && Graph(zonePath) is { } graph ? Route(graph, from, to, onFloors: true) : null;

    // How far in height a point may be from a spot to stand on its floor, and how far out the right floor is looked for.
    private const float FLOOR_HEIGHT = 150f;
    private const float FLOOR_REACH = 1500f;

    // The graph's points on a spot's own floor, nearest first, at most four: within SNAP, else within FLOOR_REACH.
    private static List<int> NearOnFloor(NavGraph graph, Vector3 at) {
        var found = new List<(int I, float Far)>();
        for (var i = 0; i < graph.Nodes.Length; i++) {
            var n = graph.Nodes[i];
            if (graph.Next[i].Length == 0 || !Finite(n) || MathF.Abs(n.Z - at.Z) > FLOOR_HEIGHT) {
                continue;
            }

            var far = Flat(n, at);
            if (far <= FLOOR_REACH) {
                found.Add((i, far));
            }
        }

        found.Sort((a, b) => a.Far.CompareTo(b.Far));
        var close = found.Where(f => f.Far <= SNAP).ToList();

        return (close.Count > 0 ? close : found).Take(4).Select(f => f.I).ToList();
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    // The graph's points near a spot on its own floor (any floor when its height is not known), nearest first, at most four.
    private static List<int> Near(NavGraph graph, Vector3 at) {
        var found = new List<(int I, float Far)>();
        for (var i = 0; i < graph.Nodes.Length; i++) {
            var n = graph.Nodes[i];
            if (graph.Next[i].Length == 0 || !Finite(n) || (float.IsFinite(at.Z) && MathF.Abs(n.Z - at.Z) > SNAP_HEIGHT)) {
                continue;
            }

            var far = Flat(n, at);
            if (far <= SNAP) {
                found.Add((i, far));
            }
        }

        found.Sort((a, b) => a.Far.CompareTo(b.Far));

        return found.Take(4).Select(f => f.I).ToList();
    }

    /// <summary>Whether (x, y) is on or by one of the walk graph's links (the game's own walkable ways), within reach.</summary>
    public static bool OnLink(string zonePath, float x, float y, float reach = 90f) {
        if (Graph(zonePath) is not { } graph) {
            return false;
        }

        var p = new Vector2(x, y);
        foreach (var (a, b) in graph.Links) {
            var ab = b - a;
            var t = ab.LengthSquared() <= 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            if (Vector2.DistanceSquared(p, a + (ab * t)) <= reach * reach) {
                return true;
            }
        }

        return false;
    }

    internal static Vector3[] Parse(byte[] data) {
        if (data is null || data.Length < 6) {
            return [];
        }

        var count = BitConverter.ToUInt16(data, 2);
        var points = new List<Vector3>(count);
        var offset = 6;
        for (var i = 0; i < count && offset + 14 <= data.Length; i++, offset += 14) {
            var point = new Vector3(
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8));
            if (float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)) {
                points.Add(point);
            }
        }

        return [.. points];
    }


    // Heights closer than this are one floor of the layout.
    private const float LAYOUT_FLOOR = 150f;
    // A link that climbs more than this is a stair, ramp or drop between floors.
    private const float LAYOUT_CLIMB = 100f;

    /// <summary>
    /// The area laid out for the bots ahead of time (Niduus, 2026-11-15: "have them all layed out for him so he knows
    /// where"): every floor of the game's own walk map by height with how far it spans, and every stair, ramp or drop
    /// between floors with both ends. Plain text, one area per file.
    /// </summary>
    public static string Layout(string zonePath) => Graph(zonePath) is { } graph ? Layout(zonePath, graph) : string.Empty;

    /// <summary>How many floors the walk map stacks, and how many links climb between them.</summary>
    internal static (int Floors, int Climbs) FloorsOf(NavGraph graph) {
        var heights = Enumerable.Range(0, graph.Nodes.Length).Where(i => graph.Next[i].Length > 0).Select(i => graph.Nodes[i].Z).OrderBy(z => z).ToList();
        var floors = 0;
        var last = float.NegativeInfinity;
        foreach (var z in heights) {
            if (z - last > LAYOUT_FLOOR) {
                floors++;
            }

            last = z;
        }

        var climbs = 0;
        for (var a = 0; a < graph.Next.Length; a++) {
            climbs += graph.Next[a].Count(b => b > a && MathF.Abs(graph.Nodes[b].Z - graph.Nodes[a].Z) > LAYOUT_CLIMB);
        }

        return (floors, climbs);
    }

    internal static string Layout(string zonePath, NavGraph graph) {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Area: {zonePath}");
        var used = Enumerable.Range(0, graph.Nodes.Length).Where(i => graph.Next[i].Length > 0).ToList();
        text.AppendLine($"Walk map: {used.Count} points, {graph.Links.Length} links.");
        if (used.Count == 0) {
            return text.ToString();
        }

        // Floors: the heights the points stand at, in bands.
        var floors = new List<(float Z, List<int> Points)>();
        foreach (var i in used.OrderBy(i => graph.Nodes[i].Z)) {
            var z = graph.Nodes[i].Z;
            if (floors.Count == 0 || z - floors[^1].Points.Max(p => graph.Nodes[p].Z) > LAYOUT_FLOOR) {
                floors.Add((z, []));
            }

            floors[^1].Points.Add(i);
        }

        text.AppendLine();
        text.AppendLine("Floors (lowest first):");
        for (var f = 0; f < floors.Count; f++) {
            var pts = floors[f].Points.Select(p => graph.Nodes[p]).ToList();
            text.AppendLine($"  Floor {f + 1}: height {pts.Min(p => p.Z):0} to {pts.Max(p => p.Z):0}, {pts.Count} points, "
                + $"x {pts.Min(p => p.X):0} to {pts.Max(p => p.X):0}, y {pts.Min(p => p.Y):0} to {pts.Max(p => p.Y):0}.");
        }

        var floorOf = new Dictionary<int, int>();
        for (var f = 0; f < floors.Count; f++) {
            foreach (var p in floors[f].Points) {
                floorOf[p] = f + 1;
            }
        }

        int FloorOf(int node) => floorOf.GetValueOrDefault(node);

        text.AppendLine();
        text.AppendLine("Ways between floors (stairs, ramps, drops):");
        var any = false;
        for (var a = 0; a < graph.Next.Length; a++) {
            foreach (var b in graph.Next[a]) {
                if (b <= a) {
                    continue;
                }

                var (pa, pb) = (graph.Nodes[a], graph.Nodes[b]);
                if (MathF.Abs(pb.Z - pa.Z) <= LAYOUT_CLIMB) {
                    continue;
                }

                any = true;
                var (lo, hi) = pa.Z <= pb.Z ? (a, b) : (b, a);
                text.AppendLine($"  ({graph.Nodes[lo].X:0}, {graph.Nodes[lo].Y:0}, {graph.Nodes[lo].Z:0}) on floor {FloorOf(lo)} "
                    + $"<-> ({graph.Nodes[hi].X:0}, {graph.Nodes[hi].Y:0}, {graph.Nodes[hi].Z:0}) on floor {FloorOf(hi)}");
            }
        }

        if (!any) {
            text.AppendLine("  none: the area is one floor.");
        }

        text.AppendLine();
        text.AppendLine("Points (x, y, height: linked to):");
        foreach (var i in used) {
            var p = graph.Nodes[i];
            text.AppendLine($"  {i}: ({p.X:0}, {p.Y:0}, {p.Z:0}) floor {FloorOf(i)}: {string.Join(" ", graph.Next[i])}");
        }

        return text.ToString();
    }
}
