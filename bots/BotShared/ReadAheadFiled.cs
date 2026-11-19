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
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The read-ahead issues already filed, kept in a small file next to the bot's issues, so a restart does not file the same
/// quest's read-ahead issue again (Niduus's log, 2026-11-14: "The Right Combination" and "Guardians at the Gate" were filed
/// at every one of 5 restarts in one day).
/// </summary>
internal static class ReadAheadFiled {

    internal const string FILE = "read-ahead-filed.txt";
    private static readonly object s_lock = new();

    /// <summary>True the first time <paramref name="quest"/> is filed from <paramref name="folder"/>, and it is written down.</summary>
    internal static bool FirstTime(string folder, string quest) {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(quest)) {
            return true;
        }

        lock (s_lock) {
            var path = Path.Combine(folder, FILE);
            try {
                var filed = File.Exists(path) ? new HashSet<string>(File.ReadAllLines(path), StringComparer.OrdinalIgnoreCase) : [];
                if (filed.Contains(quest)) {
                    return false;
                }

                Directory.CreateDirectory(folder);
                File.AppendAllText(path, quest + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("The read-ahead list {0} could not be read or written: {1}", Logger.Args(path, e.Message));
            }

            return true;
        }
    }

}
