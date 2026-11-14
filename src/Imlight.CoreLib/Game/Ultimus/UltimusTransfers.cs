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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Every zone transfer of the story's areas, read from the game's own zone files (Niduus, 2026-11-14: "Give Ultimus all the
/// data for expected zone transfers locations. (XYZ)"): each walk-through exit's trigger spot (x, y, z of its volume), where
/// it leads, and the spot it lands on in that area (the named spot of the area's location list, x, y, z). Ultimus walks to
/// the true trigger spot, and after each zone change checks that he landed where the book says (predict, then check).
/// The book is written to logs/ultimus/zone-transfers.json (read again at each server start, built again after 3 days).
/// Read-only: nothing in the game's files is changed.
/// </summary>
internal static class UltimusTransfers {

    internal sealed record Transfer(string To, float X, float Y, float Z, string Loc, float LandX, float LandY, float LandZ, bool Landing);

    private const string BOOK_FILE = "zone-transfers.json";
    private static readonly ConcurrentDictionary<string, List<Transfer>> s_book = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, (float X, float Y, float Z)>> s_spots = new(StringComparer.OrdinalIgnoreCase);
    private static int s_building;

    /// <summary>The transfers out of an area (read from its files the first time).</summary>
    internal static List<Transfer> In(string zonePath) {
        if (string.IsNullOrWhiteSpace(zonePath)) {
            return [];
        }

        return s_book.GetOrAdd(zonePath.Trim(), Read);
    }

    /// <summary>
    /// Where a wizard who left <paramref name="fromZone"/> near (<paramref name="x"/>, <paramref name="y"/>) for
    /// <paramref name="toZone"/> should land: the landing of that area's nearest transfer to it, or null when not known.
    /// </summary>
    internal static Transfer Expected(string fromZone, string toZone, float x, float y) {
        return In(fromZone).Where(t => t.Landing && (OverlayQuestTracker.SameZone(t.To, toZone) || OverlayQuestTracker.SameZone(toZone, t.To)))
            .OrderBy(t => UltimusPlanner.Far(x, y, t.X, t.Y)).FirstOrDefault();
    }

    /// <summary>The exact trigger spot of the exit to <paramref name="toZone"/> nearest (x, y), or null.</summary>
    internal static Transfer Exit(string fromZone, string toZone, float x, float y)
        => In(fromZone).Where(t => OverlayQuestTracker.SameZone(t.To, toZone) || OverlayQuestTracker.SameZone(toZone, t.To))
            .OrderBy(t => UltimusPlanner.Far(x, y, t.X, t.Y)).FirstOrDefault();

    /// <summary>How many areas and transfers the book holds now.</summary>
    internal static (int Areas, int Transfers, int Landings) Count()
        => (s_book.Count, s_book.Values.Sum(l => l.Count), s_book.Values.Sum(l => l.Count(t => t.Landing)));

    /// <summary>
    /// Builds the whole book for the story's areas on a background thread, one area at a time (a short rest between, so the
    /// server does not feel it), and writes it down. Read from the file when it is under 3 days old.
    /// </summary>
    internal static void BuildInBackground() {
        if (Interlocked.Exchange(ref s_building, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                var path = Path.Combine(UltimusWire.Folder, BOOK_FILE);
                if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(3)
                    && UltimusWire.Read<Dictionary<string, List<Transfer>>>(await File.ReadAllTextAsync(path)) is { Count: > 0 } saved) {
                    foreach (var (zone, list) in saved) {
                        s_book.TryAdd(zone, list ?? []);
                    }

                    return;
                }

                var zones = SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(z => !string.IsNullOrWhiteSpace(z) && OverlayQuestRoute.OnStory(z))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var zone in zones) {
                    In(zone);
                    await Task.Delay(25);
                }

                Directory.CreateDirectory(UltimusWire.Folder);
                await File.WriteAllTextAsync(path, UltimusWire.Write(s_book.Where(p => p.Value.Count > 0).OrderBy(p => p.Key)
                    .ToDictionary(p => p.Key, p => p.Value)));
                var (areas, transfers, landings) = Count();
                Logger.Information("Ultimus: the zone transfer book has {0} areas, {1} exits, {2} with their landing spot ({3}).",
                    Logger.Args(areas, transfers, landings, BOOK_FILE));
            }
            catch (Exception ex) {
                Logger.Debug("Ultimus: the zone transfer book could not be built: {0}", Logger.Args(ex.Message));
            }
        });
    }

    private static List<Transfer> Read(string zonePath) {
        var list = new List<Transfer>();
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) {
                return list;
            }

            var volumes = Read<WizZoneVolumes>(wad.OpenFile("volumes.xml")?.ToArray())?.m_volumes?.Where(v => v is not null).ToList() ?? [];
            var triggers = Read<WizZoneTriggers>(wad.OpenFile("triggers.xml")?.ToArray())?.m_triggers?.Where(t => t is not null).ToList() ?? [];
            var recorded = ZoneDataCollection.GetZoneData(zonePath)?.Teleports ?? [];
            foreach (var trigger in triggers) {
                string name = trigger.m_triggerName;
                var teleport = recorded.FirstOrDefault(t => t?.TriggerName == name)?.Teleport
                               ?? trigger.m_results?.m_results?.OfType<ResTeleport>().FirstOrDefault();
                if (teleport is null || trigger.m_fireEvents is null) {
                    continue;
                }

                string to = teleport.m_destinationZone;
                string loc = teleport.m_destinationLoc;
                to = string.IsNullOrWhiteSpace(to) ? zonePath : to.Trim();
                loc = string.IsNullOrWhiteSpace(loc) ? "Start" : loc.Trim();
                var land = Landing(to, loc);
                foreach (var fire in trigger.m_fireEvents.Select(e => e.ToString()).Where(e => !string.IsNullOrEmpty(e))) {
                    foreach (var volume in volumes.Where(v => v.m_enterEvents?.Any(e => (string) e == fire) == true
                                                              || "Enter_" + (string) v.m_volumeName == fire)) {
                        list.Add(new Transfer(to, volume.m_locationX, volume.m_locationY, volume.m_locationZ, loc,
                            land?.X ?? 0, land?.Y ?? 0, land?.Z ?? 0, land is not null));
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the zone transfers of {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));
        }

        return [.. list.DistinctBy(t => ((int) (t.X / 50), (int) (t.Y / 50), t.To.ToLowerInvariant()))];
    }

    // A landing: "x,y,z,yaw" as SpiralDB records some, else the named spot of the destination area.
    private static (float X, float Y, float Z)? Landing(string zonePath, string loc) {
        var parts = loc.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length >= 3 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var y)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var z)) {
            return (x, y, z);
        }

        return Spots(zonePath).TryGetValue(loc, out var spot) ? spot : null;
    }

    private static Dictionary<string, (float X, float Y, float Z)> Spots(string zonePath) => s_spots.GetOrAdd(zonePath, zone => {
        try {
            if (!ResourceManager.TryLoadArchive(zone, out var wad)) {
                return [];
            }

            return (Read<WizZoneData>(ResourceManager.OpenZoneData(wad)?.ToArray())?.m_locationList ?? [])
                .Where(l => l is not null && !l.m_locName.IsEmpty)
                .GroupBy(l => l.m_locName.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.First().m_location.X, g.First().m_location.Y, g.First().m_location.Z), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) {
            return [];
        }
    });

    private static T Read<T>(byte[] data) where T : PropertyClass {
        if (data is null) {
            return null;
        }

        return new BindSerializer().Deserialize<T>(data, 1, out var result) ? result : null;
    }

}
