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
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Where a player arrives when a way into a zone names no spot of it. Zones give each way in a spot named after the place
/// it comes from (Yakhal Mountain has From_PrincessHovel, From_RebelHQ, From_DeepFreeze ...), but many doors and teleporters
/// send only "Start" or nothing, so the player landed at the zone's start instead of outside the place they left (MR-YAKH-MAIN-005).
/// </summary>
internal static class ArrivalSpots {

    // Zone path -> the zone's location names (empty when the zone file cannot be read).
    private static readonly ConcurrentDictionary<string, string[]> s_locations = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_prefixes = ["LocationFrom_", "LocationFrom", "From_", "From"];

    /// <summary>The location names of a zone, read from its file once.</summary>
    internal static string[] Locations(string zone) => s_locations.GetOrAdd(zone ?? "", name => {
        try {
            if (name.Length == 0 || !ResourceManager.TryLoadArchive(name, out var wad) || ResourceManager.OpenZoneData(wad)?.ToArray() is not { } data
                || !new BindSerializer().Deserialize<WizZoneData>(data, 1, out var zoneData) || zoneData?.m_locationList is null) {
                return [];
            }

            return [.. zoneData.m_locationList.Where(l => l is not null && !string.IsNullOrEmpty(l.m_locName)).Select(l => (string) l.m_locName)];
        }
        catch (Exception ex) {
            Logger.Debug("Arrival spots: the locations of {0} could not be read: {1}", Logger.Args(name, ex.Message));
            return [];
        }
    });

    /// <summary>
    /// The spot of <paramref name="toZone"/> named after <paramref name="fromZone"/> ("From_&lt;place&gt;"), or null when
    /// there is none or more than one fits equally well.
    /// </summary>
    internal static string ComingFrom(string toZone, string fromZone) {
        if (string.IsNullOrEmpty(toZone) || string.IsNullOrEmpty(fromZone)
            || string.Equals(toZone, fromZone, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var leaf = Normalize(fromZone[(fromZone.LastIndexOfAny(['/', '\\']) + 1)..]);
        string best = null;
        var bestLength = 0;
        var tie = false;
        foreach (var location in Locations(toZone)) {
            var prefix = s_prefixes.FirstOrDefault(p => location.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null) {
                continue;
            }

            var key = Normalize(location[prefix.Length..]);
            if (key.Length < 4 || !leaf.Contains(key, StringComparison.Ordinal)) {
                continue;
            }

            if (key.Length > bestLength) {
                best = location;
                bestLength = key.Length;
                tie = false;
            }
            else if (key.Length == bestLength) {
                tie = true;
            }
        }

        return tie ? null : best;
    }

    private static string Normalize(string name)
        => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
