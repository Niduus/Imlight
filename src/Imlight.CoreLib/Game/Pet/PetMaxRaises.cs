/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using Imlight.Common;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Pet stat maximums raised with `.pet stats` (Justin, 2026-10-19: Strength 220 to 260). Kept in pet-max-stats.txt
/// next to the server, one "pet id, stat, maximum" per line, so they stay after a restart. A raised maximum is a
/// floor: talents that give more still give more.
/// </summary>
internal static class PetMaxRaises {

    private const string FILE = "pet-max-stats.txt";
    private static readonly object s_lock = new();
    private static Dictionary<(ulong Pet, string Stat), int> s_raises;

    /// <summary>
    /// The raised maximum of a pet's stat, or 0 if it was not raised.
    /// </summary>
    internal static int Get(ulong petId, string stat) {
        lock (s_lock) {
            return Load().TryGetValue((petId, Key(stat)), out var value) ? value : 0;
        }
    }

    internal static void Set(ulong petId, string stat, int maximum) {
        lock (s_lock) {
            Load()[(petId, Key(stat))] = maximum;
            try {
                File.WriteAllLines(FILE, s_raises.Select(raise => $"{raise.Key.Pet} {raise.Key.Stat} {raise.Value}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
            }
        }
    }

    private static string Key(string stat) => (stat ?? "").Trim().ToLowerInvariant();

    private static Dictionary<(ulong, string), int> Load() {
        if (s_raises is not null) {
            return s_raises;
        }

        s_raises = [];
        try {
            if (File.Exists(FILE)) {
                foreach (var line in File.ReadAllLines(FILE)) {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && ulong.TryParse(parts[0], out var pet) && int.TryParse(parts[2], out var value)) {
                        s_raises[(pet, Key(parts[1]))] = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_raises;
    }

}
