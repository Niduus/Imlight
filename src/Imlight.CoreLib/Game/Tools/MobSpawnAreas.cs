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
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Where each street's creatures spawn and how far they walk and pull (Niduus, 2026-11-13: "show highlights of where mob
/// spawns are on the street and where they end so bots, players, QT and Ultimus know where to avoid being pulled or
/// where to go for certain mobs they need"). Read from the zone's own spawn data when its creature paths load: each path
/// is the walk its creatures take (the game's path nodes) and which creatures spawn on it. The area a creature can pull
/// a wizard from is its walk widened by its reach ([Battle] EngageReach, or the game files' own). Kept while the zone
/// runs, so an area still shows while its creatures are dead and waiting to come back. Nothing here changes the game.
/// </summary>
internal static class MobSpawnAreas {

    /// <summary>How a path's creatures walk it: back and forth along it, round it, from node to random node, or standing.</summary>
    internal enum Walk { Chain, Loop, Random, Still }

    /// <summary>
    /// One spawn area: the path's id and name, its walk (x, y nodes in order), how it is walked, the creatures that can
    /// spawn on it (names, levels and templates), whether one is a boss, how many can be up at once, and the game
    /// files' pull reach (0 when unknown).
    /// </summary>
    internal sealed record Area(ulong Id, string PathName, IReadOnlyList<(float X, float Y)> Points, Walk How,
                                IReadOnlyList<string> Names, int MinLevel, int MaxLevel, IReadOnlyList<ulong> Templates,
                                bool Boss, int Count, float GameReach) {

        /// <summary>How far from its walk a creature here pulls a wizard in, as the server runs it now (live).</summary>
        internal float Reach => NpcComponent.EngageReach is > 0 and var own ? own : GameReach > 0 ? GameReach : 175f;

        /// <summary>The middle of the walk, for a label or for going there.</summary>
        internal (float X, float Y) Middle => (Points.Average(p => p.X), Points.Average(p => p.Y));

        /// <summary>How far (x, y) is from this area's walk (0 on it); its edge is at <see cref="Reach"/>.</summary>
        internal float DistanceTo(float x, float y) {
            if (Points.Count == 1) {
                return Far(x, y, Points[0].X, Points[0].Y);
            }

            // Inside a loop or a randomly walked area counts as on it: the creatures walk all round (Niduus's drawing).
            if ((How == Walk.Random && Inside(Hull, x, y)) || (How == Walk.Loop && Inside(Points, x, y))) {
                return 0;
            }

            var best = float.MaxValue;
            var line = How == Walk.Random ? Hull : Points;
            var closed = How is Walk.Loop or Walk.Random;
            for (var i = 0; i < line.Count; i++) {
                if (!closed && i == line.Count - 1) {
                    break;
                }

                var (a, b) = (line[i], line[(i + 1) % line.Count]);
                best = Math.Min(best, ToSegment(x, y, a.X, a.Y, b.X, b.Y));
            }

            return best;
        }

        /// <summary>Whether a wizard at (x, y) is inside the area (would be pulled by a creature walking it).</summary>
        internal bool Pulls(float x, float y) => DistanceTo(x, y) <= Reach;

        /// <summary>The outline of a randomly walked path's nodes (they go straight from any node to any other).</summary>
        internal IReadOnlyList<(float X, float Y)> Hull => _hull ??= ConvexHull(Points);

        private IReadOnlyList<(float X, float Y)> _hull;

        /// <summary>Spots along the walk at most <paramref name="every"/> apart, for a route finder that keeps away from spots.</summary>
        internal IEnumerable<(float X, float Y)> Spots(float every) {
            var line = How == Walk.Random ? Hull : Points;
            if (line.Count == 1) {
                yield return line[0];
                yield break;
            }

            var closed = How is Walk.Loop or Walk.Random;
            for (var i = 0; i < line.Count; i++) {
                if (!closed && i == line.Count - 1) {
                    yield return line[i];
                    break;
                }

                var (a, b) = (line[i], line[(i + 1) % line.Count]);
                var steps = Math.Max(1, (int) MathF.Ceiling(Far(a.X, a.Y, b.X, b.Y) / Math.Max(25f, every)));
                for (var s = 0; s < steps; s++) {
                    yield return (a.X + ((b.X - a.X) * s / steps), a.Y + ((b.Y - a.Y) * s / steps));
                }
            }

            // A randomly walked area's middle is walked too.
            if (How == Walk.Random) {
                yield return Middle;
            }
        }

    }

    private static readonly ConcurrentDictionary<IActorRef, ConcurrentDictionary<ulong, Area>> s_zones = new();

    /// <summary>
    /// A group of spawn areas whose pull reaches touch (Niduus, 2026-11-13, a red line drawn round all of a street's
    /// mob circles: "this is what I expect btw for mob areas in street locations"): one outline round all their walks
    /// widened by their reach, for the Minimap's highlight and for keeping a wizard out.
    /// </summary>
    internal sealed record Group(IReadOnlyList<Area> Areas, IReadOnlyList<float[]> Outline) {

        internal IEnumerable<string> Names => Areas.SelectMany(a => a.Names).Distinct(StringComparer.OrdinalIgnoreCase);

        internal int Count => Areas.Sum(a => a.Count);

        internal bool Boss => Areas.Any(a => a.Boss);

        /// <summary>Whether (x, y) is inside the group: a creature walking it can pull a wizard there into a battle.</summary>
        internal bool Contains(float x, float y) => Areas.Any(a => a.Pulls(x, y));

        /// <summary>How far (x, y) is from the group's edge (0 inside it).</summary>
        internal float DistanceTo(float x, float y) => Math.Max(0, Areas.Min(a => a.DistanceTo(x, y) - a.Reach));

    }

    private static readonly ConcurrentDictionary<IActorRef, (int Version, float Reach, List<Group> Groups)> s_groups = new();
    private static readonly ConcurrentDictionary<IActorRef, int> s_versions = new();

    /// <summary>The zone's spawn areas joined into groups, each with one outline (made again when the areas or the reach change).</summary>
    internal static List<Group> Groups(IActorRef zone) {
        if (zone is null) {
            return [];
        }

        var version = s_versions.GetValueOrDefault(zone);
        var reach = NpcComponent.EngageReach;
        if (s_groups.TryGetValue(zone, out var known) && known.Version == version && known.Reach == reach) {
            return known.Groups;
        }

        var groups = Join(In(zone));
        s_groups[zone] = (version, reach, groups);

        return groups;
    }

    // Areas whose widened walks touch are one group; the group's outline is the outline round every walk spot widened by
    // its reach (a ring of points round each).
    private static List<Group> Join(List<Area> areas) {
        var parent = Enumerable.Range(0, areas.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        var spots = areas.Select(a => a.Spots(Math.Max(60f, a.Reach * 0.5f)).ToList()).ToList();
        for (var i = 0; i < areas.Count; i++) {
            for (var j = i + 1; j < areas.Count; j++) {
                if (Root(i) == Root(j)) {
                    continue;
                }

                var touch = areas[i].Reach + areas[j].Reach + JOIN_GAP;
                if (spots[j].Any(p => areas[i].DistanceTo(p.X, p.Y) <= touch)) {
                    parent[Root(i)] = Root(j);
                }
            }
        }

        var groups = new List<Group>();
        foreach (var members in Enumerable.Range(0, areas.Count).GroupBy(Root)) {
            var band = new List<float[]>();
            foreach (var i in members) {
                Band(areas[i], band);
            }

            // Only the outer edges: a hole (the middle of a loop the creatures walk round) is part of the area.
            var loops = Imlight.CoreLib.Game.Zone.Core.GroundContours.Trace(band, OUTLINE_SMOOTHING, OUTLINE_LEVEL);
            var outer = loops.Where(loop => !loops.Any(other => !ReferenceEquals(other, loop) && other.Length >= 6 && loop.Length >= 2
                                                              && Inside(Pairs(other), loop[0], loop[1]))).ToList();
            groups.Add(new Group([.. members.Select(i => areas[i])], outer));
        }

        return groups;
    }


    /// <summary>[Overlay] MinimapSpawns: the minimap gets each street's spawn areas; live.</summary>
    internal static bool OnMinimap => ConfigurationManager.GetValue("Overlay.MinimapSpawns", true);

    /// <summary>The spawn areas in the zone run by <paramref name="zone"/> (a copy, safe to walk).</summary>
    internal static List<Area> In(IActorRef zone)
        => zone is not null && s_zones.TryGetValue(zone, out var areas) ? [.. areas.Values] : [];

    /// <summary>The groups whose outline holds (x, y): where standing there can start a battle once a creature walks by.</summary>
    internal static List<Group> PullingAt(IActorRef zone, float x, float y) => [.. Groups(zone).Where(g => g.Contains(x, y))];

    /// <summary>
    /// Spots to keep away from in a zone (every creature's walk, not just where it stands now), for
    /// <see cref="OverlayQuestPath.Walk"/>'s avoid list; areas of creatures in <paramref name="wanted"/> (templates the
    /// wizard is going to fight) are left out.
    /// </summary>
    internal static List<(float X, float Y)> AvoidSpots(IActorRef zone, IReadOnlyCollection<ulong> wanted = null, float every = 150f)
        => [.. In(zone).Where(a => wanted is null || !a.Templates.Any(wanted.Contains)).SelectMany(a => a.Spots(every))];

    /// <summary>
    /// The nearest area to (x, y) where a creature named <paramref name="name"/> (or of one of the templates) spawns,
    /// for a wizard that needs to fight it; null when none spawns in this zone.
    /// </summary>
    internal static Area Find(IActorRef zone, float x, float y, string name = null, IReadOnlyCollection<ulong> templates = null)
        => In(zone).Where(a => (templates is { Count: > 0 } && a.Templates.Any(templates.Contains))
                               || (!string.IsNullOrWhiteSpace(name) && a.Names.Any(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))))
            .OrderBy(a => a.DistanceTo(x, y)).FirstOrDefault();

    /// <summary>A creature path loaded (ZonePath): noted when creatures that fight spawn on it.</summary>
    internal static void Placed(IActorRef zone, PathObjectTemplate path, IReadOnlyList<NodeObject> nodes, IReadOnlyList<SpawnObject> spawners) {
        try {
            if (zone is null || path is null || nodes is null || nodes.Count == 0 || spawners is null || Describe(path, nodes, spawners) is not { } area) {
                return;
            }

            s_zones.GetOrAdd(zone, _ => new ConcurrentDictionary<ulong, Area>())[area.Id] = area;
            s_versions.AddOrUpdate(zone, 1, (_, v) => v + 1);
        }
        catch (Exception ex) {
            Logger.Debug("Spawn areas: could not note path {0}: {1}", Logger.Args(path is null ? "" : path.m_name.ToString(), ex.Message));
        }
    }

    /// <summary>A creature path stopped (its zone closed, or the Customs tab took it away): its area goes.</summary>
    internal static void Gone(IActorRef zone, PathObjectTemplate path) {
        if (zone is null || path is null || !s_zones.TryGetValue(zone, out var areas)) {
            return;
        }

        areas.TryRemove(path.m_id.Full, out _);
        s_versions.AddOrUpdate(zone, 1, (_, v) => v + 1);
        if (areas.IsEmpty) {
            s_zones.TryRemove(new KeyValuePair<IActorRef, ConcurrentDictionary<ulong, Area>>(zone, areas));
            s_groups.TryRemove(zone, out _);
            s_versions.TryRemove(zone, out _);
        }
    }

    private static Area Describe(PathObjectTemplate path, IReadOnlyList<NodeObject> nodes, IReadOnlyList<SpawnObject> spawners) {
        var names = new List<string>();
        var templates = new List<ulong>();
        var (minLevel, maxLevel, boss, count, reach) = (int.MaxValue, 0, false, 0, 0f);
        Walk? how = null;
        foreach (var spawner in spawners) {
            var fighters = 0;
            foreach (var item in spawner.m_spawnList ?? []) {
                var info = item?.m_objectInfo;
                if (info is null || info.m_pathID.Full != path.m_id.Full
                    || CoreObjectFactory.GetCoreTemplate(info.m_templateID) is not GameObjectTemplate template) {
                    continue;
                }

                var duelist = template.m_behaviors.OfType<DuelistBehaviorTemplate>().FirstOrDefault();
                var npc = template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
                if (duelist is null || npc is null) {
                    continue;
                }

                fighters++;
                var id = (ulong) info.m_templateID;
                if (!templates.Contains(id)) {
                    templates.Add(id);
                }

                var name = MinimapIndex.NameOf(template);
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                    names.Add(name);
                }

                minLevel = Math.Min(minLevel, npc.m_nLevel);
                maxLevel = Math.Max(maxLevel, npc.m_nLevel);
                boss |= npc.IsBoss();
                reach = Math.Max(reach, duelist.m_npcProximity);
                how ??= template.m_behaviors.OfType<PathBehaviorTemplate>().FirstOrDefault()?.m_kPathType switch {
                    PathType.PT_LOOP => Walk.Loop,
                    PathType.PT_RANDOM => Walk.Random,
                    PathType.PT_CHAIN => Walk.Chain,
                    _ => null,
                };
            }

            if (fighters > 0) {
                count += Math.Max(1, (int) spawner.m_maxNumberOfSpawns);
            }
        }

        if (templates.Count == 0) {
            return null;
        }

        var points = new List<(float X, float Y)>();
        foreach (var node in nodes) {
            var p = (node.m_location.X, node.m_location.Y);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) {
                continue;
            }

            if (points.Count == 0 || Far(points[^1].X, points[^1].Y, p.X, p.Y) > 1f) {
                points.Add(p);
            }
        }

        if (points.Count == 0) {
            return null;
        }

        var walk = points.Count == 1 ? Walk.Still : how ?? Walk.Chain;
        // A loop of two nodes is walked back and forth.
        if (walk == Walk.Loop && points.Count == 2) {
            walk = Walk.Chain;
        }

        return new Area(path.m_id.Full, path.m_name.ToString() ?? "", points, walk, names, minLevel == int.MaxValue ? 0 : minLevel, maxLevel,
            templates, boss, count, reach);
    }

    // How much each group's outline is rounded off (game units).
    private const float OUTLINE_SMOOTHING = 100f;
    // Traced a little under a half, so the rounding doesn't shrink a lone creature's patch inside its real reach.
    private const float OUTLINE_LEVEL = 0.35f;
    // Areas this much farther apart than their two reaches still make one group (a plaza's mobs are one area).
    private const float JOIN_GAP = 400f;

    // An area's walk widened by its reach, as triangles (x1, y1, x2, y2, x3, y3): a round patch at every node, a strip
    // along every leg, and a randomly walked area's whole inside.
    private static void Band(Area area, List<float[]> triangles) {
        const int SIDES = 16;
        var r = area.Reach;
        var line = area.How == Walk.Random ? area.Hull : area.Points;
        foreach (var (x, y) in line) {
            for (var k = 0; k < SIDES; k++) {
                var (s0, c0) = MathF.SinCos(k * MathF.PI * 2 / SIDES);
                var (s1, c1) = MathF.SinCos((k + 1) * MathF.PI * 2 / SIDES);
                triangles.Add([x, y, x + (c0 * r), y + (s0 * r), x + (c1 * r), y + (s1 * r)]);
            }
        }

        var closed = area.How is Walk.Loop or Walk.Random;
        for (var i = 0; i + 1 < line.Count || (closed && i < line.Count && line.Count > 2); i++) {
            var (a, b) = (line[i], line[(i + 1) % line.Count]);
            var length = Far(a.X, a.Y, b.X, b.Y);
            if (length < 1) {
                continue;
            }

            var (nx, ny) = (-(b.Y - a.Y) / length * r, (b.X - a.X) / length * r);
            triangles.Add([a.X + nx, a.Y + ny, b.X + nx, b.Y + ny, b.X - nx, b.Y - ny]);
            triangles.Add([a.X + nx, a.Y + ny, b.X - nx, b.Y - ny, a.X - nx, a.Y - ny]);
        }

        if (area.How == Walk.Random && line.Count >= 3) {
            for (var k = 1; k + 1 < line.Count; k++) {
                triangles.Add([line[0].X, line[0].Y, line[k].X, line[k].Y, line[k + 1].X, line[k + 1].Y]);
            }
        }
    }

    private static List<(float X, float Y)> Pairs(float[] loop) {
        var points = new List<(float X, float Y)>(loop.Length / 2);
        for (var i = 0; i + 1 < loop.Length; i += 2) {
            points.Add((loop[i], loop[i + 1]));
        }

        return points;
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    private static float ToSegment(float x, float y, float ax, float ay, float bx, float by) {
        var (dx, dy) = (bx - ax, by - ay);
        var length = (dx * dx) + (dy * dy);
        var t = length > 0 ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / length, 0f, 1f) : 0f;

        return Far(x, y, ax + (dx * t), ay + (dy * t));
    }

    private static bool Inside(IReadOnlyList<(float X, float Y)> polygon, float x, float y) {
        if (polygon.Count < 3) {
            return false;
        }

        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++) {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X) {
                inside = !inside;
            }
        }

        return inside;
    }

    // Monotone chain: the outline round the points, counter-clockwise.
    private static List<(float X, float Y)> ConvexHull(IReadOnlyList<(float X, float Y)> points) {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) {
            return sorted;
        }

        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b)
            => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

        var hull = new List<(float X, float Y)>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() }) {
            var start = hull.Count;
            foreach (var p in pass) {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

}
