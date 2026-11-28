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

    /// <summary>A named location of a zone: where it is and which way the player faces there.</summary>
    internal sealed record Spot(string Name, float X, float Y, float Z, float Direction);

    // Zone path -> the zone's named locations (empty when the zone file cannot be read).
    private static readonly ConcurrentDictionary<string, Spot[]> s_spots = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_prefixes = ["LocationFrom_", "LocationFrom", "From_", "From"];
    private static readonly string[] s_teleportPrefixes = ["TeleportTo_", "TeleportTo", "TeleTo_", "TeleTo", "To_"];

    /// <summary>The named locations of a zone, read from its file once.</summary>
    internal static Spot[] Spots(string zone) => s_spots.GetOrAdd(zone ?? "", name => {
        try {
            if (name.Length == 0 || !ResourceManager.TryLoadArchive(name, out var wad) || ResourceManager.OpenZoneData(wad)?.ToArray() is not { } data
                || !new BindSerializer().Deserialize<WizZoneData>(data, 1, out var zoneData) || zoneData?.m_locationList is null) {
                return [];
            }

            return [.. zoneData.m_locationList.Where(l => l is not null && !string.IsNullOrEmpty(l.m_locName))
                .Select(l => new Spot((string) l.m_locName,l.m_location.X, l.m_location.Y, l.m_location.Z, l.m_direction))];
        }
        catch (Exception ex) {
            Logger.Debug("Arrival spots: the locations of {0} could not be read: {1}", Logger.Args(name, ex.Message));
            return [];
        }
    });

    /// <summary>The location names of a zone.</summary>
    internal static string[] Locations(string zone) => [.. Spots(zone).Select(s => s.Name)];

    /// <summary>
    /// The arrival spot named for a teleporter of the zone: "TeleToMid03" -> "LocationFromMid03", the spot beside that
    /// teleporter's own pad where the way back from Mid03 lands. Null when the zone has none.
    /// </summary>
    internal static Spot ArrivalBeside(string zone, string teleporter) {
        var key = TeleporterKey(teleporter);
        if (key is null) {
            return null;
        }

        return Spots(zone).FirstOrDefault(s => s_prefixes.FirstOrDefault(p => s.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix
            && Normalize(s.Name[prefix.Length..]) == key);
    }

    /// <summary>What a teleporter's name says it leads to ("TeleToMid03" -> "mid03"), or null for another kind of name.</summary>
    internal static string TeleporterKey(string teleporter) {
        if (string.IsNullOrEmpty(teleporter)) {
            return null;
        }

        var prefix = s_teleportPrefixes.FirstOrDefault(p => teleporter.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var key = prefix is null ? "" : Normalize(teleporter[prefix.Length..]);

        return key.Length >= 3 ? key : null;
    }

    internal static bool Near(Spot spot, float x, float y, float z, float range)
        => ((spot.X - x) * (spot.X - x)) + ((spot.Y - y) * (spot.Y - y)) + ((spot.Z - z) * (spot.Z - z)) <= range * range;

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
