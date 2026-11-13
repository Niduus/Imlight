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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The ways that worked, and the spots where a wizard got stuck (Niduus, 2026-11-13: "adapt to new paths to get to his
/// quest goal. Marking down working paths for data collection and longevity. Also learning"). Ultimus and the Quest Team
/// write down each walk that got them to their step (the street, where from, where to, the spots walked) and each spot
/// they were stuck at. The shared way finder (<see cref="OverlayQuestPath.Walk"/>) then takes a proven walk when one fits,
/// and bends round the stuck spots, for Ultimus, the Quest Team and the Bot Players alike. Kept in
/// logs/routes/learned-routes.json, so it survives restarts and updates.
/// </summary>
internal static class RouteMemory {

    private const string FOLDER = "logs/routes";
    private const string FILE = "learned-routes.json";

    // A walk counts once it is this long; shorter ones the way finder gets right anyway.
    private const float MIN_LEG = 600f;

    // A spot is written down after moving this far, and a move this big in a moment is a teleport (it ends the walk).
    private const float SAMPLE = 120f;
    private const float JUMP = 800f;

    // How near the start and the end of a proven walk the wizard and its step must be for the walk to be taken.
    private const float FROM_NEAR = 300f;
    private const float TO_NEAR = 250f;

    // How near a stuck spot a proven walk may pass before it no longer counts as proven.
    private const float STUCK_NEAR = 150f;

    // How many walks and stuck spots each street keeps (the oldest go first), and for how long a stuck spot is avoided.
    private const int MOST_LEGS = 300;
    private const int MOST_STUCK = 40;
    private static readonly TimeSpan s_stuckFor = TimeSpan.FromDays(14);
    private static readonly TimeSpan s_saveGap = TimeSpan.FromSeconds(30);

    internal sealed class Leg {
        public float FromX { get; set; }
        public float FromY { get; set; }
        public float ToX { get; set; }
        public float ToY { get; set; }
        public List<float[]> Points { get; set; } = [];
        public int Worked { get; set; }
        public int Failed { get; set; }
        public DateTime Last { get; set; }
        public string Who { get; set; } = "";
    }

    internal sealed class StuckSpot {
        public float X { get; set; }
        public float Y { get; set; }
        public int Times { get; set; }
        public DateTime Last { get; set; }
    }

    internal sealed class Street {
        public List<Leg> Legs { get; set; } = [];
        public List<StuckSpot> Stuck { get; set; } = [];
    }

    private sealed class Trail {
        public string Zone = "";
        public List<(float X, float Y)> Points = [];
        public string Who = "";
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, Street> s_streets;
    private static readonly Dictionary<ulong, Trail> s_trails = [];
    private static bool s_dirty;
    private static DateTime s_saved = DateTime.MinValue;

    private static string FilePath => Path.Combine(Path.GetFullPath(FOLDER), FILE);

    /// <summary>Where a wizard stands now (each look): the walk it is on is drawn from these.</summary>
    internal static void Track(string who, ulong charId, string zonePath, float x, float y) {
        if (string.IsNullOrEmpty(zonePath) || charId == 0) {
            return;
        }

        lock (s_lock) {
            if (s_dirty) {
                SaveSoon();
            }

            if (!s_trails.TryGetValue(charId, out var trail) || !string.Equals(trail.Zone, zonePath, StringComparison.OrdinalIgnoreCase)) {
                s_trails[charId] = new Trail { Zone = zonePath, Points = [(x, y)], Who = who };

                return;
            }

            var (lx, ly) = trail.Points[^1];
            var moved = Far(lx, ly, x, y);
            if (moved > JUMP) {
                // A teleport, a recall: a new walk starts here.
                trail.Points = [(x, y)];
            }
            else if (moved >= SAMPLE) {
                trail.Points.Add((x, y));
                if (trail.Points.Count > 400) {
                    trail.Points.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>
    /// The wizard reached its step: the walk that got it here is written down (or counted again), and a new one starts.
    /// True when it is a walk not known before.
    /// </summary>
    internal static bool Arrived(ulong charId, string zonePath, float x, float y) {
        lock (s_lock) {
            if (!s_trails.TryGetValue(charId, out var trail) || !string.Equals(trail.Zone, zonePath, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            var points = trail.Points;
            if (Far(points[^1].X, points[^1].Y, x, y) >= 1) {
                points.Add((x, y));
            }

            s_trails[charId] = new Trail { Zone = zonePath, Points = [(x, y)], Who = trail.Who };
            if (points.Count < 3 || Length(points) < MIN_LEG) {
                return false;
            }

            var street = StreetOf(zonePath, create: true);
            var (fx, fy) = points[0];
            var same = street.Legs.FirstOrDefault(l => Far(l.FromX, l.FromY, fx, fy) <= FROM_NEAR && Far(l.ToX, l.ToY, x, y) <= TO_NEAR);
            var walked = points.Select(p => new[] { MathF.Round(p.X), MathF.Round(p.Y) }).ToList();
            if (same is null) {
                street.Legs.Add(new Leg { FromX = fx, FromY = fy, ToX = x, ToY = y, Points = walked, Worked = 1, Last = DateTime.UtcNow, Who = trail.Who });
                if (street.Legs.Count > MOST_LEGS) {
                    street.Legs.Remove(street.Legs.OrderBy(l => l.Last).First());
                }
            }
            else {
                same.Worked++;
                same.Last = DateTime.UtcNow;
                // The shorter of the two walks is kept.
                if (Length(walked.Select(p => (p[0], p[1])).ToList()) < Length(same.Points.Select(p => (p[0], p[1])).ToList())) {
                    same.Points = walked;
                }
            }

            s_dirty = true;
            SaveSoon();

            return same is null;
        }
    }

    /// <summary>
    /// The wizard got stuck here: the spot is written down (the way finder bends round it from now on), walks through it
    /// count as failed, and the walk it was on is dropped.
    /// </summary>
    internal static void Stuck(ulong charId, string zonePath, float x, float y) {
        if (string.IsNullOrEmpty(zonePath)) {
            return;
        }

        lock (s_lock) {
            s_trails.Remove(charId);
            var street = StreetOf(zonePath, create: true);
            var spot = street.Stuck.FirstOrDefault(s => Far(s.X, s.Y, x, y) <= STUCK_NEAR);
            if (spot is null) {
                street.Stuck.Add(spot = new StuckSpot { X = x, Y = y });
                if (street.Stuck.Count > MOST_STUCK) {
                    street.Stuck.Remove(street.Stuck.OrderBy(s => s.Last).First());
                }
            }

            spot.Times++;
            spot.Last = DateTime.UtcNow;
            foreach (var leg in street.Legs.Where(l => l.Points.Any(p => Far(p[0], p[1], x, y) <= STUCK_NEAR))) {
                leg.Failed++;
            }

            s_dirty = true;
            SaveSoon();
        }
    }

    /// <summary>
    /// A walk that already got a wizard from about here to about there, as spots to walk (the last one the step itself);
    /// null when none is known, when it passes a creature to keep away from, or when it failed more than it worked.
    /// </summary>
    internal static List<(float X, float Y)> Proven(string zonePath, float fromX, float fromY, float toX, float toY,
        IReadOnlyList<(float X, float Y)> avoid) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        lock (s_lock) {
            if (StreetOf(zonePath, create: false) is not { } street) {
                return null;
            }

            var leg = street.Legs
                .Where(l => l.Worked > l.Failed * 2 && Far(l.FromX, l.FromY, fromX, fromY) <= FROM_NEAR && Far(l.ToX, l.ToY, toX, toY) <= TO_NEAR)
                .Where(l => avoid is null || !l.Points.Any(p => avoid.Any(a => Far(a.X, a.Y, p[0], p[1]) < 200)))
                .OrderByDescending(l => l.Worked - l.Failed)
                .FirstOrDefault();
            if (leg is null) {
                return null;
            }

            // From the nearest spot of the walk on (not back to its start), and to the step itself.
            var nearest = 0;
            for (var i = 1; i < leg.Points.Count; i++) {
                if (Far(leg.Points[i][0], leg.Points[i][1], fromX, fromY) < Far(leg.Points[nearest][0], leg.Points[nearest][1], fromX, fromY)) {
                    nearest = i;
                }
            }

            var points = leg.Points.Skip(nearest + 1).Select(p => (p[0], p[1])).ToList();
            points.Add((toX, toY));
            leg.Last = DateTime.UtcNow;

            return points;
        }
    }

    /// <summary>The spots where wizards got stuck on this street lately, added to the ones to keep away from (null when none).</summary>
    internal static IReadOnlyList<(float X, float Y)> WithStuckSpots(string zonePath, float toX, float toY, IReadOnlyList<(float X, float Y)> avoid) {
        if (string.IsNullOrEmpty(zonePath)) {
            return avoid;
        }

        lock (s_lock) {
            if (StreetOf(zonePath, create: false) is not { Stuck.Count: > 0 } street) {
                return avoid;
            }

            // Not the ones right by the step itself (it has to be reached).
            var spots = street.Stuck.Where(s => DateTime.UtcNow - s.Last < s_stuckFor && Far(s.X, s.Y, toX, toY) > 400)
                .Select(s => (s.X, s.Y)).ToList();
            if (spots.Count == 0) {
                return avoid;
            }

            return avoid is null ? spots : [.. avoid, .. spots];
        }
    }

    /// <summary>How many walks and stuck spots are known (for the log).</summary>
    internal static (int Legs, int Stuck) Count() {
        lock (s_lock) {
            var all = Streets();

            return (all.Values.Sum(s => s.Legs.Count), all.Values.Sum(s => s.Stuck.Count));
        }
    }

    /// <summary>Writes what is not written yet (the server closing).</summary>
    internal static void Flush() {
        lock (s_lock) {
            if (s_dirty) {
                Save();
            }
        }
    }

    // ---- The file. ----

    private static Street StreetOf(string zonePath, bool create) {
        var all = Streets();
        if (all.TryGetValue(zonePath, out var street)) {
            return street;
        }

        if (!create) {
            return null;
        }

        all[zonePath] = street = new Street();

        return street;
    }

    private static Dictionary<string, Street> Streets() {
        if (s_streets is not null) {
            return s_streets;
        }

        s_streets = new Dictionary<string, Street>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, Street>>(File.ReadAllText(FilePath)) is { } read) {
                foreach (var (zone, street) in read.Where(s => s.Value is not null)) {
                    street.Legs ??= [];
                    street.Stuck ??= [];
                    street.Legs.RemoveAll(l => l?.Points is not { Count: > 0 } || l.Points.Any(p => p is not { Length: >= 2 }));
                    street.Stuck.RemoveAll(s => s is null);
                    s_streets[zone] = street;
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Route memory: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_streets;
    }

    private static void SaveSoon() {
        if (DateTime.UtcNow - s_saved >= s_saveGap) {
            Save();
        }
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Streets()));
            File.Move(temp, FilePath, overwrite: true);
            s_dirty = false;
        }
        catch (Exception ex) {
            Logger.Debug("Route memory: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }

        s_saved = DateTime.UtcNow;
    }

    private static float Length(List<(float X, float Y)> points) {
        var total = 0f;
        for (var i = 1; i < points.Count; i++) {
            total += Far(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
        }

        return total;
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}
