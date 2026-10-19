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
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Harmony mode (`.harmony on`): the wizard starts every battle with all 7 pips as power pips and full shadow pips.
/// Kept in harmony.txt next to the server, one character id per line, so it stays on after a restart.
/// </summary>
internal static class HarmonyMode {

    private const string FILE = "harmony.txt";
    private static readonly object s_lock = new();
    private static HashSet<ulong> s_characters;

    internal static bool IsOn(ulong characterId) {
        lock (s_lock) {
            return Load().Contains(characterId);
        }
    }

    internal static void Set(ulong characterId, bool on) {
        lock (s_lock) {
            var characters = Load();
            if (on ? !characters.Add(characterId) : !characters.Remove(characterId)) {
                return;
            }

            try {
                File.WriteAllLines(FILE, characters.Select(id => id.ToString()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Could not save Harmony mode: {0}", Logger.Args(ex.Message));
            }
        }
    }

    private static HashSet<ulong> Load() {
        if (s_characters is not null) {
            return s_characters;
        }

        s_characters = [];
        try {
            if (File.Exists(FILE)) {
                foreach (var line in File.ReadAllLines(FILE)) {
                    if (ulong.TryParse(line.Trim(), out var id)) {
                        s_characters.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not read Harmony mode: {0}", Logger.Args(ex.Message));
        }

        return s_characters;
    }

}
