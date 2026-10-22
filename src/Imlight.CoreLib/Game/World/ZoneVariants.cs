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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The ZoneObjects variant each character's zone was loaded with (an added sigil's Variant, such as RavenwoodMalistaire),
/// kept in a file so logging in again, even after the server restarted, lands in the same kind of copy. Without it the
/// game came back to the plain zone: the zone's own Malistaire, with his minions standing on the circle (Niduus,
/// 2026-10-22, "reconnected into the dungeon").
/// </summary>
internal static class ZoneVariants {

    private const string FILE = "zone-variants.json";

    private static readonly object s_lock = new();
    private static Dictionary<ulong, Entry> s_entries;

    internal sealed record Entry(string Zone, string Variant);

    /// <summary>Notes the variant a character's zone was loaded with; an empty one forgets it.</summary>
    internal static void Remember(ulong charId, string zone, string variant) {
        lock (s_lock) {
            var entries = Load();
            var entry = string.IsNullOrEmpty(variant) ? null : new Entry(zone, variant);
            entries.TryGetValue(charId, out var old);
            if (entry == old) {
                return;
            }

            if (entry is null) {
                entries.Remove(charId);
            }
            else {
                entries[charId] = entry;
            }

            Save();
        }
    }

    /// <summary>
    /// The variant to load a character's zone with when they attach to it, or empty. Attaching to any other zone
    /// forgets the noted one, so a later visit to the plain zone stays plain.
    /// </summary>
    internal static string ForAttach(ulong charId, string zone) {
        lock (s_lock) {
            var entries = Load();
            if (!entries.TryGetValue(charId, out var entry)) {
                return "";
            }

            if (string.Equals(entry.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
                return entry.Variant;
            }

            entries.Remove(charId);
            Save();

            return "";
        }
    }

    private static Dictionary<ulong, Entry> Load() {
        if (s_entries is not null) {
            return s_entries;
        }

        s_entries = [];
        try {
            if (File.Exists(FILE)) {
                s_entries = JsonSerializer.Deserialize<Dictionary<ulong, Entry>>(File.ReadAllText(FILE)) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_entries;
    }

    private static void Save() {
        try {
            File.WriteAllText(FILE, JsonSerializer.Serialize(s_entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
