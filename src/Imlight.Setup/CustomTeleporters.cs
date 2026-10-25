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
using System.Text.Json;

namespace Imlight.Setup;

/// <summary>
/// A teleporter hub of the Customs tab (the server's CustomTeleporters.cs): a ring of teleporter stones, each linked to
/// a street teleporter.
/// </summary>
internal sealed class TeleporterHub {

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Zone { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public List<TeleporterPad> Pads { get; set; } = [];

    /// <summary>How many stones a new hub has, as the game's own hub on Colossus Boulevard.</summary>
    internal const int PAD_COUNT = 3;

    // How far from the middle of a new hub its stones stand.
    private const double RING_RADIUS = 250;

    [System.Text.Json.Serialization.JsonIgnore]
    internal string Shown => Name.Trim().Length > 0 ? Name.Trim() : $"Teleporter Hub {Id}";

    /// <summary>Puts the hub's middle at a spot, with its stones in a ring round it (the first one in front).</summary>
    internal void PlaceAt(string zone, double x, double y, double z, double yaw) {
        (Zone, X, Y, Z, Yaw) = (zone, x, y, z, yaw);
        while (Pads.Count < PAD_COUNT) {
            Pads.Add(new TeleporterPad { Name = $"Teleporter {Pads.Count + 1}" });
        }

        for (var i = 0; i < Pads.Count; i++) {
            var angle = yaw + (i * 2 * Math.PI / Pads.Count);
            // The way a wizard with this yaw faces (the server's CustomTeleporters.InFront).
            Pads[i].X = Math.Round(x - (Math.Sin(angle) * RING_RADIUS), 2);
            Pads[i].Y = Math.Round(y - (Math.Cos(angle) * RING_RADIUS), 2);
            Pads[i].Z = z;
            Pads[i].Yaw = yaw;
        }
    }

    internal TeleporterHub Copy() {
        var copy = (TeleporterHub) MemberwiseClone();
        copy.Pads = [.. Pads.Select(p => p.Copy())];

        return copy;
    }

}

internal sealed class TeleporterPad {

    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public ulong Look { get; set; }
    public TeleporterStreet? Street { get; set; }

    internal TeleporterPad Copy() {
        var copy = (TeleporterPad) MemberwiseClone();
        copy.Street = Street?.Copy();

        return copy;
    }

}

internal sealed class TeleporterStreet {

    public string Zone { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public ulong Look { get; set; }

    internal TeleporterStreet Copy() => (TeleporterStreet) MemberwiseClone();

}

/// <summary>Reads and writes spiraldb-extra/Customs/Teleporters.json.</summary>
internal static class TeleporterHubs {

    internal const string FILE = "Teleporters.json";

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal static List<TeleporterHub> Load(string folder) {
        var file = Path.Combine(folder, FILE);

        return File.Exists(file) ? JsonSerializer.Deserialize<List<TeleporterHub>>(File.ReadAllText(file), s_options) ?? [] : [];
    }

    /// <summary>The lowest number no hub has (1, 2, 3...).</summary>
    internal static int NextId(List<TeleporterHub> hubs) {
        var id = 1;
        while (hubs.Any(h => h.Id == id)) {
            id++;
        }

        return id;
    }

    /// <summary>Saves one hub (a new one, or over the one with its number); null with an id deletes that one.</summary>
    internal static void Set(string folder, int id, TeleporterHub? hub) {
        var hubs = Load(folder);
        var at = hubs.FindIndex(h => h.Id == id);
        if (hub is null) {
            if (at >= 0) {
                hubs.RemoveAt(at);
            }
        }
        else if (at >= 0) {
            hubs[at] = hub;
        }
        else {
            hubs.Add(hub);
        }

        // Written beside the old file and then moved over it, so the server never reads half a file.
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FILE);
        File.WriteAllText(file + ".saving", JsonSerializer.Serialize(hubs.OrderBy(h => h.Id).ToList(), s_options));
        File.Move(file + ".saving", file, overwrite: true);
    }

}
