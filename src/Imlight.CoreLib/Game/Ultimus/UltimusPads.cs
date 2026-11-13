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
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's map of the teleporters inside an area (Niduus, 2026-11-13: "add more advance thinking, pathing and reasoning
/// to Ultimus ... emulate the abilities of a normal human being doing the main story"). A player knows that a pad on one
/// side of Triton Avenue lands on the Kraken isle and the pad there lands back on the street; Ultimus knows it two ways:
/// from the area's own files (each trigger that teleports to a spot in the same area, with the spot from the area's
/// location list), and from what happened to it (stood somewhere and was moved far within the same area in a moment). What
/// it learned is kept in logs/ultimus/pads.json, so it is not lost when the server restarts.
/// </summary>
internal static class UltimusPads {

    /// <param name="Click">
    /// The door or street teleporter stone to press there (its global id), 0 for a pad that is walked onto. A "To Teleporter
    /// Hub" stone or a "To Triton Avenue" door does nothing to a wizard who only stands by it (Niduus, 2026-11-13: "bot
    /// can't use"): it has to be pressed, as a player presses X.
    /// </param>
    internal sealed record Pad(float X, float Y, float ToX, float ToY, string Name, bool Learned = false, ulong Click = 0);

    private const string PADS_FILE = "pads.json";

    private static readonly ConcurrentDictionary<string, List<Pad>> s_files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_lock = new();

    // Same-area doors seen standing in each area (clickable "To Triton Avenue" pads): a door has no trigger volume, so
    // the files alone don't show it.
    private static readonly ConcurrentDictionary<string, List<Pad>> s_doors = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, List<Pad>> s_learned;

    /// <summary>Every teleporter of the area that lands in the same area: the area's own and the ones Ultimus learned.</summary>
    internal static List<Pad> In(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return [];
        }

        var pads = new List<Pad>(s_files.GetOrAdd(zonePath, FromFiles));
        if (s_doors.TryGetValue(zonePath, out var doors)) {
            pads.AddRange(doors);
        }

        lock (s_lock) {
            if (Learned().TryGetValue(zonePath, out var learned)) {
                // One it learned by being moved by a door or stone it knows now is that door or stone (which is pressed).
                pads.AddRange(learned.Where(l => doors is null || !doors.Any(d => Near(d.X, d.Y, l.X, l.Y, 300) && Near(d.ToX, d.ToY, l.ToX, l.ToY, 800))));
            }
        }

        return pads;
    }

    /// <summary>
    /// Ultimus was moved from one spot to another far away in the same area in a moment: a teleporter it stepped on.
    /// True when this is a new one (and it is remembered).
    /// </summary>
    internal static bool Learn(string zonePath, float fromX, float fromY, float toX, float toY) {
        if (string.IsNullOrEmpty(zonePath)) {
            return false;
        }

        // A teleporter the files already know (its pad near where it stood, its landing near where it came out).
        if (s_files.GetOrAdd(zonePath, FromFiles).Any(p => Near(p.X, p.Y, fromX, fromY, 300) && Near(p.ToX, p.ToY, toX, toY, 400))) {
            return false;
        }

        // A door or street teleporter stone it pressed.
        if (s_doors.TryGetValue(zonePath, out var doors) && doors.Any(p => Near(p.X, p.Y, fromX, fromY, 300) && Near(p.ToX, p.ToY, toX, toY, 800))) {
            return false;
        }

        lock (s_lock) {
            var all = Learned();
            if (!all.TryGetValue(zonePath, out var pads)) {
                all[zonePath] = pads = [];
            }

            if (pads.Any(p => Near(p.X, p.Y, fromX, fromY, 250) && Near(p.ToX, p.ToY, toX, toY, 400))) {
                return false;
            }

            pads.Add(new Pad(fromX, fromY, toX, toY, "a teleporter it found", Learned: true));
            Save(all);
        }

        return true;
    }

    /// <summary>
    /// The teleporter to walk to from (x, y) for a step at (toX, toY) that walls and water cut off: the first pad of the
    /// shortest chain of pads (at most four) from this island to the step's island. Null when the step is on the same
    /// island, when the ground is unknown, or when no chain of known pads leads there.
    /// </summary>
    internal static Pad Toward(string zonePath, float x, float y, float toX, float toY, ICollection<Pad> skip = null) {
        var from = OverlayQuestPath.IslandOf(zonePath, x, y);
        var goal = OverlayQuestPath.IslandOf(zonePath, toX, toY);
        if (from < 0 || goal < 0 || from == goal) {
            return null;
        }

        var pads = In(zonePath).Where(p => skip is null || !skip.Contains(p))
            .Select(p => (Pad: p, On: OverlayQuestPath.IslandOf(zonePath, p.X, p.Y), Lands: OverlayQuestPath.IslandOf(zonePath, p.ToX, p.ToY)))
            .Where(p => p.On >= 0 && p.Lands >= 0 && p.On != p.Lands)
            .ToList();

        // Island by island, the first pad taken to reach each.
        var firstPad = new Dictionary<int, Pad> { [from] = null };
        var frontier = new List<int> { from };
        for (var depth = 0; depth < 4 && frontier.Count > 0; depth++) {
            var next = new List<int>();
            foreach (var island in frontier) {
                foreach (var pad in pads.Where(p => p.On == island)
                             .OrderBy(p => island == from ? UltimusPlanner.Far(x, y, p.Pad.X, p.Pad.Y) : 0)) {
                    if (firstPad.ContainsKey(pad.Lands)) {
                        continue;
                    }

                    firstPad[pad.Lands] = firstPad[island] ?? pad.Pad;
                    if (pad.Lands == goal) {
                        return firstPad[goal];
                    }

                    next.Add(pad.Lands);
                }
            }

            frontier = next;
        }

        return null;
    }

    /// <summary>
    /// Another way when stuck (a player who can't get through tries the teleporter instead): a pad on this island whose
    /// landing is nearer the step than where it stands, the nearest first. Null when there is none.
    /// </summary>
    internal static Pad Another(string zonePath, float x, float y, float toX, float toY, ICollection<Pad> skip) {
        var here = OverlayQuestPath.IslandOf(zonePath, x, y);
        var now = UltimusPlanner.Far(x, y, toX, toY);

        return In(zonePath)
            .Where(p => !skip.Contains(p) && (here < 0 || OverlayQuestPath.IslandOf(zonePath, p.X, p.Y) == here)
                        && UltimusPlanner.Far(p.ToX, p.ToY, toX, toY) < now - 300)
            .OrderBy(p => UltimusPlanner.Far(x, y, p.X, p.Y))
            .FirstOrDefault();
    }

    /// <summary>The nearest teleporter within <paramref name="most"/> on the ground the spot is on (null when none).</summary>
    internal static Pad Nearest(string zonePath, float x, float y, ICollection<Pad> skip, float most) {
        var here = OverlayQuestPath.IslandOf(zonePath, x, y);

        return In(zonePath)
            .Where(p => !skip.Contains(p) && (here < 0 || OverlayQuestPath.IslandOf(zonePath, p.X, p.Y) == here)
                        && UltimusPlanner.Far(x, y, p.X, p.Y) <= most)
            .OrderBy(p => UltimusPlanner.Far(x, y, p.X, p.Y))
            .FirstOrDefault();
    }

    /// <summary>
    /// The door or stone to press when a wizard at (x, y) is by the first pad of the way to (toX, toY) and that pad has to
    /// be pressed (null when the way has no such pad here). The same for Ultimus and the Quest Team.
    /// </summary>
    internal static Pad PressHere(string zonePath, float x, float y, float toX, float toY, ICollection<Pad> skip = null, float reach = 160f)
        => Toward(zonePath, x, y, toX, toY, skip) is { Click: not 0 } pad && Near(x, y, pad.X, pad.Y, reach) ? pad : null;

    private static bool Near(float ax, float ay, float bx, float by, float most) => UltimusPlanner.Far(ax, ay, bx, by) <= most;

    /// <summary>
    /// The doors standing in a loaded area that lead to another spot of the same area (Niduus, 2026-11-13: the quest line
    /// "doesn't see or respect teleporters such as in Triton Avenue"): kept as its teleporters.
    /// </summary>
    internal static void NoteDoors(string zonePath, IEnumerable<MinimapIndex.Mark> live) {
        if (string.IsNullOrEmpty(zonePath) || live is null) {
            return;
        }

        try {
            var pads = new List<Pad>();
            foreach (var door in live.Where(m => m.Kind == MinimapIndex.DOOR && string.Equals(m.To?.Trim(), zonePath, StringComparison.OrdinalIgnoreCase))) {
                if (Landing(zonePath, door.ToLoc) is { } to
                    && !Near(door.Object.m_location.X, door.Object.m_location.Y, to.X, to.Y, 200)) {
                    pads.Add(new Pad(door.Object.m_location.X, door.Object.m_location.Y, to.X, to.Y, door.Name, Click: door.Object.m_globalID.Full));
                }
            }

            // The street teleporter stones ("To Teleporter Hub" and the hub's stones): each sends the wizard to its
            // partner stone in the same area, once pressed.
            foreach (var stone in live.Where(m => m.Kind == MinimapIndex.DOOR && m.Entity is not null)) {
                if (stone.Entity.GetComponentOfType<StreetTeleporterComponent>()?.PartnerSpot is { } lands
                    && !Near(stone.Object.m_location.X, stone.Object.m_location.Y, lands.X, lands.Y, 200)) {
                    pads.Add(new Pad(stone.Object.m_location.X, stone.Object.m_location.Y, lands.X, lands.Y, stone.Name, Click: stone.Object.m_globalID.Full));
                }
            }

            if (pads.Count > 0 || s_doors.ContainsKey(zonePath)) {
                s_doors[zonePath] = pads;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the doors of {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));
        }
    }

    // A teleport's landing: "x,y,z,yaw" as SpiralDB records most of them, else a named spot of the area.
    private static (float X, float Y)? Landing(string zonePath, string loc) {
        if (Coordinates(loc) is { } xy) {
            return xy;
        }

        return string.IsNullOrWhiteSpace(loc) || !Spots(zonePath).TryGetValue(loc.Trim(), out var spot) ? null : spot;
    }

    private static (float X, float Y)? Coordinates(string loc) {
        var parts = (loc ?? "").Split(',');
        return parts.Length >= 3
               && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
               && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? (x, y) : null;
    }

    private static readonly ConcurrentDictionary<string, Dictionary<string, (float X, float Y)>> s_spots = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, (float X, float Y)> Spots(string zonePath) => s_spots.GetOrAdd(zonePath, zone => {
        try {
            if (!ResourceManager.TryLoadArchive(zone, out var wad)) {
                return [];
            }

            return (Read<WizZoneData>(ResourceManager.OpenZoneData(wad)?.ToArray())?.m_locationList ?? [])
                .Where(l => l is not null && !l.m_locName.IsEmpty)
                .GroupBy(l => l.m_locName.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.First().m_location.X, g.First().m_location.Y), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) {
            return [];
        }
    });

    // ---- The area's files: a trigger that teleports within the area, its volume, and the named spot it lands on. ----

    private static List<Pad> FromFiles(string zonePath) {
        var pads = new List<Pad>();
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) {
                return pads;
            }

            var zoneData = Read<WizZoneData>(ResourceManager.OpenZoneData(wad)?.ToArray());
            var spots = (zoneData?.m_locationList ?? []).Where(l => l is not null && !l.m_locName.IsEmpty)
                .GroupBy(l => l.m_locName.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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

                string zone = teleport.m_destinationZone;
                string loc = teleport.m_destinationLoc;
                if (string.IsNullOrWhiteSpace(loc) || !(string.IsNullOrWhiteSpace(zone) || string.Equals(zone.Trim(), zonePath, StringComparison.OrdinalIgnoreCase))) {
                    continue;
                }

                // The landing: a named spot of the area, or "x,y,z,yaw" (how SpiralDB records Triton Avenue's pads; Niduus,
                // 2026-11-13: these were all missed, so no teleporter was ever known there).
                (float X, float Y) landing;
                if (spots.TryGetValue(loc.Trim(), out var named)) {
                    landing = (named.m_location.X, named.m_location.Y);
                }
                else if (Coordinates(loc) is { } xy) {
                    landing = xy;
                }
                else {
                    continue;
                }

                foreach (var fire in trigger.m_fireEvents.Select(e => e.ToString()).Where(e => !string.IsNullOrEmpty(e))) {
                    foreach (var volume in volumes.Where(v => v.m_enterEvents?.Any(e => (string) e == fire) == true
                                                              || "Enter_" + (string) v.m_volumeName == fire)) {
                        if (!Near(volume.m_locationX, volume.m_locationY, landing.X, landing.Y, 200)) {
                            pads.Add(new Pad(volume.m_locationX, volume.m_locationY, landing.X, landing.Y, name));
                        }
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the teleporters of {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));
        }

        return [.. pads.DistinctBy(p => ((int) (p.X / 100), (int) (p.Y / 100), (int) (p.ToX / 100), (int) (p.ToY / 100)))];
    }

    private static T Read<T>(byte[] data) where T : PropertyClass {
        if (data is null) {
            return null;
        }

        return new BindSerializer().Deserialize<T>(data, 1, out var result) ? result : null;
    }

    // ---- What Ultimus learned, kept between server starts. ----

    private static Dictionary<string, List<Pad>> Learned() {
        if (s_learned is not null) {
            return s_learned;
        }

        s_learned = new Dictionary<string, List<Pad>>(StringComparer.OrdinalIgnoreCase);
        try {
            var path = Path.Combine(UltimusWire.Folder, PADS_FILE);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, List<Pad>>>(File.ReadAllText(path)) is { } read) {
                foreach (var (zone, pads) in read) {
                    s_learned[zone] = [.. pads.Where(p => p is not null).Select(p => p with { Learned = true })];
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(PADS_FILE, ex.Message));
        }

        return s_learned;
    }

    private static void Save(Dictionary<string, List<Pad>> all) {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, PADS_FILE), UltimusWire.Write(all));
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(PADS_FILE, ex.Message));
        }
    }

}
