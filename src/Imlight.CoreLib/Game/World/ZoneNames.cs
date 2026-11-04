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

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Every zone .tp can go to, with the name players know it by (spiraldb-extra/Zones/zone-details.json). Read again
/// whenever that file changes, so a fixed name works without a restart.
/// </summary>
internal static class ZoneNames {

    private static readonly object s_lock = new();
    private static IReadOnlyList<ZoneName> s_zones = [];
    private static DateTime s_readFrom = DateTime.MinValue;
    private static int s_zoneCount = -1;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "Zones", "zone-details.json");

    /// <summary>Every zone of the game's zone list, named where the names file knows it.</summary>
    internal static IReadOnlyList<ZoneName> All {
        get {
            var codes = AccessPassManager.Zones;
            var changed = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            lock (s_lock) {
                if (changed == s_readFrom && codes.Count == s_zoneCount) {
                    return s_zones;
                }

                var details = Read();
                s_zones = [.. codes.Select(code => details.TryGetValue(code, out var d)
                    ? new ZoneName(code, d.Length > 0 ? d[0] : "", d.Length > 2 ? d[2] : "", d.Length > 3 ? d[3] : "")
                    : new ZoneName(code, ""))];
                s_readFrom = changed;
                s_zoneCount = codes.Count;

                return s_zones;
            }
        }
    }

    /// <summary>The zone a typed name or code name means, or null when none is close.</summary>
    internal static ZoneMatch? Find(string? typed) => ZoneNameMatch.Find(All, typed);

    /// <summary>The name players know a zone by, or the code name when there is none.</summary>
    internal static string Shown(string code)
        => All.FirstOrDefault(z => z.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } zone ? zone.Name : code;

    private static Dictionary<string, string[]> Read() {
        var details = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!File.Exists(FilePath)) {
                return details;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!document.RootElement.TryGetProperty("Zones", out var zones) || zones.ValueKind != JsonValueKind.Object) {
                return details;
            }

            foreach (var zone in zones.EnumerateObject()) {
                if (zone.Value.ValueKind == JsonValueKind.Array) {
                    details[zone.Name] = [.. zone.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "")];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("The zone names file {0} could not be read: {1}", Logger.Args(FilePath, ex.Message));
        }

        return details;
    }

}
