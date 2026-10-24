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

/*
 * The one place drop chances are tuned (Niduus, 2026-10-24: the gauntlets' drops from Wizard404, which lists what each
 * creature drops but not how often). spiraldb-extra/DropOdds.json gives a percent for each rank of creature (Boss,
 * Elite, Normal) and kind of drop (Gear, Jewel, TreasureCard...). A drop table whose Odds names one ("Boss/Gear") rolls
 * at that percent instead of its own RollChance, which then only counts while the file has no such entry. The file is
 * read again whenever it changes, so a change counts from the next roll without a restart. Scale multiplies them all.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

public static class DropOdds {

    private static readonly TimeSpan RESCAN = TimeSpan.FromSeconds(3);
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_options = new() {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static OddsFile s_odds = new();
    private static DateTime s_written = DateTime.MinValue;
    private static DateTime s_scanned = DateTime.MinValue;

    private sealed class OddsFile {
        public double Scale { get; set; } = 1;
        public Dictionary<string, Dictionary<string, double>> Odds { get; set; } = [];
    }

    /// <summary>
    /// The chance (0 to 1) a drop table rolls: DropOdds.json's percent for its Odds ("Boss/Gear") times Scale, or else
    /// its own RollChance.
    /// </summary>
    public static double ChanceOf(DropTable table) {
        if (table is null) {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(table.Odds)) {
            return table.RollChance;
        }

        var odds = Current();
        var parts = table.Odds.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && Find(odds.Odds, parts[0]) is { } rank && Find(rank, parts[1]) is { } percent) {
            return Math.Clamp(percent / 100 * Math.Max(0, odds.Scale), 0, 1);
        }

        return table.RollChance;
    }

    private static Dictionary<string, double> Find(Dictionary<string, Dictionary<string, double>> map, string key) {
        foreach (var (k, v) in map ?? []) {
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return v;
            }
        }

        return null;
    }

    private static double? Find(Dictionary<string, double> map, string key) {
        foreach (var (k, v) in map ?? []) {
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return v;
            }
        }

        return null;
    }

    private static OddsFile Current() {
        if (DateTime.UtcNow - s_scanned < RESCAN) {
            return s_odds;
        }

        lock (s_lock) {
            if (DateTime.UtcNow - s_scanned < RESCAN) {
                return s_odds;
            }

            s_scanned = DateTime.UtcNow;
            var file = Path.Combine(SpiralDB.ExtraPath, "DropOdds.json");
            try {
                if (!File.Exists(file)) {
                    s_odds = new OddsFile();
                    s_written = DateTime.MinValue;

                    return s_odds;
                }

                var written = File.GetLastWriteTimeUtc(file);
                if (written != s_written) {
                    s_odds = JsonSerializer.Deserialize<OddsFile>(File.ReadAllText(file), s_options) ?? new OddsFile();
                    s_written = written;
                    Logger.Information("Drop odds read from {0}: {1} ranks, scale {2}.", Logger.Args(file, s_odds.Odds?.Count ?? 0, s_odds.Scale));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
                Logger.Warning("DropOdds.json could not be read, keeping the odds before: {0}", Logger.Args(ex.Message));
                s_written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            }

            return s_odds;
        }
    }

}
