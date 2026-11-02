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

}
