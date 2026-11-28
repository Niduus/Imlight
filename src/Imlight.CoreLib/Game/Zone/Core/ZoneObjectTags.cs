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
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// The tags a zone's placed objects really carry, so a quest's dynamod that names an object a little differently (with or
/// without " instance") still reaches it, and where a paired teleporter stands. Read once per zone from its zone file.
/// </summary>
internal static class ZoneObjectTags {

    private const string INSTANCE = " instance";

    private static readonly ConcurrentDictionary<string, HashSet<string>> s_tags = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, Imcodec.Math.Vector3>> s_spots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where an object of this tag (" instance" or not) stands in some zone of <paramref name="world"/> ("Khrysalis"),
    /// the zones nearest by name to <paramref name="nearZone"/> first; null when none does. Reads zone files as it goes.
    /// </summary>
    internal static (string Zone, Imcodec.Math.Vector3 Spot)? FindInWorld(string world, string nearZone, string tag) {
        if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(tag)) {
            return null;
        }

        var plain = tag.EndsWith(INSTANCE, StringComparison.OrdinalIgnoreCase) ? tag[..^INSTANCE.Length] : tag;
        return s_found.GetOrAdd($"{world}|{plain}", _ => Search(world, nearZone, plain));
    }

    private static readonly ConcurrentDictionary<string, (string Zone, Imcodec.Math.Vector3 Spot)?> s_found = new(StringComparer.OrdinalIgnoreCase);

    private static (string Zone, Imcodec.Math.Vector3 Spot)? Search(string world, string nearZone, string plain) {
        var area = AreaCode(nearZone);
        var zones = Imlight.CoreLib.Game.World.AccessPassManager.Zones
            .Where(z => z.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(z => !string.IsNullOrEmpty(area) && z.Contains(area, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        foreach (var zone in zones) {
            var spots = s_spots.GetOrAdd(zone, ReadSpots);
            if (spots.TryGetValue(plain, out var spot) || spots.TryGetValue(plain + INSTANCE, out spot)) {
                return (zone, spot);
            }
        }

        return null;
    }

    // "Khrysalis/KR_Z07_RuinedAlcazar" -> "_Z07_": the area code its interiors share.
    private static string AreaCode(string zone) {
        var match = System.Text.RegularExpressions.Regex.Match(zone ?? "", @"_Z\d+_");
        return match.Success ? match.Value : null;
    }

    private static Dictionary<string, Imcodec.Math.Vector3> ReadSpots(string zonePath) {
        var spots = new Dictionary<string, Imcodec.Math.Vector3>(StringComparer.OrdinalIgnoreCase);
        try {
            if (ResourceManager.TryLoadArchive(zonePath, out var wad)
                && ResourceManager.OpenZoneData(wad)?.ToArray() is { } bytes
                && new BindSerializer().Deserialize<WizZoneData>(bytes, 1u, out var data)
                && data?.m_objectList is { } objects) {
                foreach (var placed in objects) {
                    if ((string?) placed?.m_zoneTag is { Length: > 0 } name) {
                        spots.TryAdd(name, placed.m_location);
                    }
                }
            }
        }
        catch (Exception) {
            // A zone that can't be read has no spots.
        }

        return spots;
    }

    /// <summary>The tag the zone places for <paramref name="tag"/>: the tag itself when placed (or unknown), else the same
    /// name with or without " instance" when that one is placed.</summary>
    internal static string Placed(string zonePath, string tag) {
        if (string.IsNullOrEmpty(zonePath) || string.IsNullOrEmpty(tag)) {
            return tag;
        }

        var tags = s_tags.GetOrAdd(zonePath, Read);
        if (tags.Count == 0 || tags.Contains(tag)) {
            return tag;
        }

        var other = tag.EndsWith(INSTANCE, StringComparison.OrdinalIgnoreCase) ? tag[..^INSTANCE.Length] : tag + INSTANCE;
        if (tags.TryGetValue(other, out var placed)) {
            return placed;
        }

        return tag;
    }

    /// <summary>Whether some placed object of the zone carries exactly this tag (true when the zone file can't be read).</summary>
    internal static bool IsPlaced(string zonePath, string tag) {
        if (string.IsNullOrEmpty(zonePath) || string.IsNullOrEmpty(tag)) {
            return false;
        }

        var tags = s_tags.GetOrAdd(zonePath, Read);
        return tags.Count == 0 || tags.Contains(tag);
    }

    private static HashSet<string> Read(string zonePath) {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (ResourceManager.TryLoadArchive(zonePath, out var wad)
                && ResourceManager.OpenZoneData(wad)?.ToArray() is { } bytes
                && new BindSerializer().Deserialize<WizZoneData>(bytes, 1u, out var data)
                && data?.m_objectList is { } objects) {
                foreach (var placed in objects) {
                    if ((string?) placed?.m_zoneTag is { Length: > 0 } name) {
                        tags.Add(name);
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Zone object tags: could not read {0}: {1}", Logger.Args(zonePath, ex.Message));
        }

        return tags;
    }

}
