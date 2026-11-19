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
using System.Linq;
using System.Numerics;
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Keeps a server-played wizard (the Quest Team and Ultimus) from standing in one place forever (Niduus, 2026-11-13:
/// "Parameters should be in place to prevent that"). Each look says whether a wizard that should be getting somewhere has
/// stood still too long, and how many times it already tried to free itself there; the director picks what to do.
/// A wizard is only looked at while nothing excuses it (a battle, a dialog, a pause, a rest): a gap between looks starts
/// the count again, so a long battle is never taken for being stuck.
/// </summary>
internal sealed class BotStuck {

    /// <summary>How long a wizard may stand within <see cref="MOVED"/> of one place before it is stuck.</summary>
    internal static readonly TimeSpan STILL = TimeSpan.FromSeconds(75);

    /// <summary>Moving this far counts as getting somewhere.</summary>
    private const float MOVED = 40f;

    /// <summary>Being this far from where it was first caught counts as free: the tries start again.</summary>
    private const float FREED = 500f;

    private sealed class Seen {
        public string Zone = "";
        public float X, Y;
        public DateTime Since;
        public DateTime LastLook;
        public float SpotX, SpotY;
        public int Tries;
        public DateTime LastTry = DateTime.MinValue;
    }

    private readonly Dictionary<int, Seen> _seen = [];

    /// <summary>How long this team's wizards may stand still before they are stuck (<see cref="STILL"/> unless set).</summary>
    internal TimeSpan Still { get; }

    internal BotStuck(TimeSpan? still = null) => Still = still ?? STILL;

    // Places a wizard was stuck at lately, with how many tries it made there.
    private readonly List<(int Index, string Zone, float X, float Y, int Tries, DateTime When)> _deadEnds = [];
    private static readonly TimeSpan DEAD_END_MEMORY = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Looks at wizard <paramref name="index"/>. Returns 0 while it is fine, or the try (1, 2, 3, ...) it should make now
    /// to free itself. <paramref name="excused"/>: it stands still for a reason this moment.
    /// </summary>
    internal int Look(int index, string zone, float x, float y, bool excused) {
        var now = DateTime.UtcNow;
        if (!_seen.TryGetValue(index, out var seen)) {
            _seen[index] = seen = new Seen { Zone = zone ?? "", X = x, Y = y, Since = now, LastLook = now, SpotX = x, SpotY = y };

            return 0;
        }

        var newZone = !string.Equals(seen.Zone, zone ?? "", StringComparison.OrdinalIgnoreCase);
        if (newZone || Far(seen.SpotX, seen.SpotY, x, y) > FREED) {
            // Remembered: a wizard that steps aside and walks straight back to the same dead end (Ultimus on the Kraken
            // isle, stepping aside 600 away and back, 1, 2, 1, 2 for hours) carries on counting there.
            if (seen.Tries > 0) {
                _deadEnds.RemoveAll(d => now - d.When > DEAD_END_MEMORY);
                _deadEnds.Add((index, seen.Zone, seen.SpotX, seen.SpotY, seen.Tries, now));
            }

            seen.Tries = 0;
            seen.SpotX = x;
            seen.SpotY = y;
        }

        var gap = now - seen.LastLook > TimeSpan.FromSeconds(3);
        seen.LastLook = now;
        if (newZone || excused || gap || Far(seen.X, seen.Y, x, y) > MOVED) {
            seen.Zone = zone ?? "";
            seen.X = x;
            seen.Y = y;
            seen.Since = now;

            return 0;
        }

        if (now - seen.Since < Still || now - seen.LastTry < Still) {
            return 0;
        }

        if (seen.Tries == 0) {
            seen.SpotX = x;
            seen.SpotY = y;
            var known = _deadEnds.Where(d => d.Index == index && now - d.When <= DEAD_END_MEMORY
                                             && string.Equals(d.Zone, zone ?? "", StringComparison.OrdinalIgnoreCase)
                                             && Far(d.X, d.Y, x, y) <= FREED)
                .Select(d => d.Tries).DefaultIfEmpty(0).Max();
            seen.Tries = known;
        }

        seen.Tries++;
        seen.LastTry = now;
        seen.Since = now;

        return seen.Tries;
    }

    /// <summary>How long wizard <paramref name="index"/> has stood still (zero when unknown).</summary>
    internal TimeSpan StillFor(int index) => _seen.TryGetValue(index, out var seen) ? DateTime.UtcNow - seen.Since : TimeSpan.Zero;

    /// <summary>Starts every wizard's count again (a restart, the quest moved on).</summary>
    internal void Clear() {
        _seen.Clear();
        _deadEnds.Clear();
    }

    /// <summary>
    /// A walk point to step to, between <paramref name="near"/> and <paramref name="far"/> away on about the same floor,
    /// a path and not the water's edge; the further tries go further. Null when the area has no walk points.
    /// </summary>
    internal static (float X, float Y)? StepAside(string zonePath, float x, float y, float z, float near, float far) {
        var here = new Vector3(x, y, z);
        var points = ZoneNav.Points(zonePath)
            .Where(p => MathF.Abs(p.Z - z) < 300)
            .Select(p => (Point: p, Distance: Vector3.Distance(p, here)))
            .Where(p => p.Distance >= near && p.Distance <= far)
            .ToList();
        if (points.Count == 0) {
            return null;
        }

        var good = points.Where(p => BotPlayers.BotGround.GoodSpot(zonePath, p.Point)).ToList();
        var pick = (good.Count > 0 ? good : points)[Random.Shared.Next(good.Count > 0 ? good.Count : points.Count)];

        return (pick.Point.X, pick.Point.Y);
    }

    /// <summary>
    /// Reads an area's walls, ground and walk points before a wizard that just arrived takes a step there (Niduus,
    /// 2026-11-13: "take a moment after each zone transfer for those collisions to be recognized"). Safe to call from any
    /// thread; each area is read once and kept.
    /// </summary>
    internal static void ReadArea(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return;
        }

        try {
            _ = ZoneGround.WalkOf(zonePath);
            _ = ZoneNav.Points(zonePath);
            Tools.OverlayQuestPath.Warm(zonePath);
        }
        catch (Exception) {
            // Read again on the first walk.
        }
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

}
