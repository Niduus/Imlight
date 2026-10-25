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

/*
 * The drop tables of spiraldb-extra/DropTables as they are now (Niduus, 2026-10-22: "Include custom drop tables"). The app's
 * Drop tables editor saves there; SpiralDB reads the folder only when the server starts, so a table made or changed
 * since is read from its file here, at most every few seconds, and counts from the next roll without a restart.
 * A name not found here is SpiralDB's.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

public static class DropTableFiles {

    private static readonly TimeSpan RESCAN = TimeSpan.FromSeconds(3);
    private static readonly object s_lock = new();
    private static readonly ConcurrentDictionary<string, (DateTime Written, DropTable Table)> s_files = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, DropTable> s_byName = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_scanned = DateTime.MinValue;

    /// <summary>A drop table by its name: the extra folder's file as it is now, else SpiralDB's.</summary>
    public static DropTable Get(string name) {
        // A quest's own rewards from the Manager's Quest Editor.
        if (Imlight.CoreLib.Game.Quests.QuestOptions.RewardTable(name) is { } questRewards) {
            return questRewards;
        }

        Rescan();

        return s_byName.TryGetValue(name, out var table) ? table : SpiralDB.GetDropTable(name);
    }

    private static void Rescan() {
        if (DateTime.UtcNow - s_scanned < RESCAN) {
            return;
        }

        lock (s_lock) {
            if (DateTime.UtcNow - s_scanned < RESCAN) {
                return;
            }

            s_scanned = DateTime.UtcNow;
            var byName = new Dictionary<string, DropTable>(StringComparer.OrdinalIgnoreCase);
            try {
                var folder = Path.Combine(SpiralDB.ExtraPath, "DropTables");
                if (Directory.Exists(folder)) {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.json")) {
                        var written = File.GetLastWriteTimeUtc(file);
                        if (!s_files.TryGetValue(file, out var kept) || kept.Written != written) {
                            kept = (written, Read(file));
                            s_files[file] = kept;
                        }

                        if (kept.Table is { Name.Length: > 0 } table) {
                            byName[table.Name] = table;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Could not read the drop tables folder: {0}", Logger.Args(ex.Message));
            }

            s_byName = byName;
        }
    }

    private static DropTable Read(string file) {
        try {
            using var stream = File.OpenRead(file);

            return JsonSerializer.Deserialize<DropTable>(stream, SpiralJsonOptions.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Drop table {0} could not be read: {1}", Logger.Args(Path.GetFileName(file), ex.Message));

            return null;
        }
    }

}
