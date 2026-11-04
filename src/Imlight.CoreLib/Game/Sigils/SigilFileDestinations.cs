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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Sigils;

/// <summary>
/// Where a dungeon sigil pad leads, as the zone file itself says. A MinigameSigilInfo in a zone's gamedata.bin carries
/// the zone it sends to (property 0x5A66B8B0) and the spot to land on (0x8BAF14A0, a list of names), but Imcodec does
/// not decode those two fields, so the server only knew pads that a captured teleport or the tower naming explained.
/// The Grand Arena's "Trophy Room" pad (Oka's Chest, KT-SPH3-C04-002) and "HourGlass Chamber" pad matched neither and
/// gave no prompt at all (Niduus, 2026-11-04). This reads the two fields straight from the file's bytes, once a zone.
/// </summary>
internal static class SigilFileDestinations {

    internal sealed record Destination(string Zone, string Loc);

    // MinigameSigilInfo's class hash, then the hashes of its zone tag, destination zone and destination spot list.
    private const uint SIGIL_INFO_HASH = 234614075;
    private const uint ZONE_TAG_HASH = 0xADC3A56F;
    private const uint DESTINATION_ZONE_HASH = 0x5A66B8B0;
    private const uint DESTINATION_LOC_HASH = 0x8BAF14A0;

    // Zone path -> pad tag -> where it leads (null: two pads of that name lead to different places).
    private static readonly ConcurrentDictionary<string, Dictionary<string, Destination>> s_zones
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The destination the zone file gives the pad named <paramref name="tag"/> in <paramref name="zonePath"/>.</summary>
    internal static bool TryGet(string zonePath, string tag, out Destination destination) {
        destination = null;
        if (string.IsNullOrEmpty(zonePath) || string.IsNullOrEmpty(tag)) {
            return false;
        }

        var pads = s_zones.GetOrAdd(zonePath, Read);

        return pads.TryGetValue(tag, out destination) && destination is not null && !string.IsNullOrEmpty(destination.Zone);
    }

    private static Dictionary<string, Destination> Read(string zonePath) {
        var pads = new Dictionary<string, Destination>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || ResourceManager.OpenZoneData(wad)?.ToArray() is not { } bytes) {
                return pads;
            }

            foreach (var (tag, destination) in Scan(bytes)) {
                if (pads.TryGetValue(tag, out var known) && known is not null && known != destination) {
                    pads[tag] = null;
                    continue;
                }

                if (!pads.ContainsKey(tag)) {
                    pads[tag] = destination;
                }
            }

            Logger.Debug("Sigil pads of {0}: {1} name where they lead in the zone file.", Logger.Args(zonePath, pads.Count));
        }
        catch (Exception ex) {
            Logger.Warning("Sigil pads: could not read where the pads of {0} lead. {1}", Logger.Args(zonePath, ex.Message));
        }

        return pads;
    }

    /// <summary>Every sigil pad in a zone file's bytes with the destination it names.</summary>
    internal static IEnumerable<(string Tag, Destination Destination)> Scan(byte[] bytes) {
        var at = 0;
        while ((at = IndexOf(bytes, SIGIL_INFO_HASH, at, bytes.Length)) >= 0) {
            if (at + 8 > bytes.Length) {
                yield break;
            }

            // The object's size in bits follows its class hash; its fields lie within.
            var bits = BitConverter.ToUInt32(bytes, at + 4);
            var end = (int) Math.Min(bytes.Length, at + ((long) bits + 7) / 8);
            var tag = StringAfter(bytes, IndexOf(bytes, ZONE_TAG_HASH, at, end), end);
            var zone = StringAfter(bytes, IndexOf(bytes, DESTINATION_ZONE_HASH, at, end), end);
            var locAt = IndexOf(bytes, DESTINATION_LOC_HASH, at, end);
            var loc = "";
            if (locAt >= 0 && locAt + 8 <= end && BitConverter.ToUInt32(bytes, locAt + 4) > 0) {
                loc = StringAfter(bytes, locAt + 4, end) ?? "";
            }

            if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(zone)) {
                yield return (tag, new Destination(zone, loc));
            }

            at += 4;
        }
    }

    // The length-prefixed string after the 4 bytes at `at` (a field hash or a list count).
    private static string StringAfter(byte[] bytes, int at, int end) {
        if (at < 0 || at + 6 > end) {
            return null;
        }

        var length = BitConverter.ToUInt16(bytes, at + 4);
        if (at + 6 + length > end) {
            return null;
        }

        return Encoding.Latin1.GetString(bytes, at + 6, length);
    }

    private static int IndexOf(byte[] bytes, uint value, int from, int end) {
        if (from < 0) {
            return -1;
        }

        Span<byte> pattern = stackalloc byte[4];
        BitConverter.TryWriteBytes(pattern, value);
        var index = bytes.AsSpan(from, Math.Max(0, end - from)).IndexOf(pattern);

        return index < 0 ? -1 : from + index;
    }

}
