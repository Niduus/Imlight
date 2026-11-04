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
/// A portal of the Customs tab (the server's CustomTeleporters portals, Portals.json): one teleporter with its own
/// interaction prompt, sent by pressing X in it or by walking in, to a spot picked with Teleport here.
/// </summary>
internal sealed class CustomPortal {

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "Teleport";
    public string Text { get; set; } = "";
    public bool Walk { get; set; }
    public double Reach { get; set; }
    public string Zone { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public ulong Look { get; set; }
    public string ToZone { get; set; } = "";
    public double ToX { get; set; }
    public double ToY { get; set; }
    public double ToZ { get; set; }
    public double ToYaw { get; set; }

    /// <summary>The Shadow Portal house teleporter, a new portal's look (the server's CustomTeleporters.SHADOW_PORTAL).</summary>
    internal const ulong SHADOW_PORTAL = 1719467;

    /// <summary>How near a player has to be when Reach is left at 0 (the server's PORTAL_REACH).</summary>
    internal const double DEFAULT_REACH = 120;

    /// <summary>The prompt's second lines (the server's prompts): the game's own lines, then Custom (Text as written).</summary>
    internal static readonly (string Value, string Label)[] PROMPTS = [
        ("Teleport", "Press X to Teleport"),
        ("Travel", "Press X to Travel"),
        ("Enter", "Press X to Enter"),
        ("Exit", "Press X to Exit"),
        ("Interact", "Press X to Interact"),
        ("Custom", "My own words (below)"),
    ];

    /// <summary>The game's house teleporters (their HousingTeleporterBehavior), the looks a portal can have.</summary>
    internal static readonly (ulong Id, string Label)[] LOOKS = [
        (SHADOW_PORTAL, "Shadow Portal"),
        (1719468, "Star Portal"),
        (1719469, "Sun Portal"),
        (1719471, "Moon Portal"),
        (1695007, "Balance Portal"),
        (1695008, "Death Portal"),
        (1695009, "Life Portal"),
        (1695010, "Myth Portal"),
        (1695011, "Fire Portal"),
        (1695012, "Ice Portal"),
        (1695013, "Storm Portal"),
        (1603095, "Leyline Teleporter"),
        (1603096, "Basic Teleporter"),
        (1603097, "Blue Avalon Teleporter"),
        (1603098, "White Avalon Teleporter"),
        (469781, "Blue Teleporter"),
        (469782, "Cyan Teleporter"),
        (469783, "Green Teleporter"),
        (469784, "Orange Teleporter"),
        (469785, "Purple Teleporter"),
        (469786, "Red Teleporter"),
        (469787, "White Teleporter"),
        (469788, "Yellow Teleporter"),
        (512071, "Teleporter (house)"),
        (1488125, "Colossus Boulevard stone A"),
        (1488127, "Colossus Boulevard stone B"),
        (1488130, "Colossus Boulevard stone C"),
    ];

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool HasDestination => ToZone.Trim().Length > 0;

    internal CustomPortal Copy() => (CustomPortal) MemberwiseClone();

}

/// <summary>Reads and writes spiraldb-extra/Customs/Portals.json.</summary>
internal static class CustomPortals {

    internal const string FILE = "Portals.json";

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal static List<CustomPortal> Load(string folder) {
        var file = Path.Combine(folder, FILE);

        return File.Exists(file) ? JsonSerializer.Deserialize<List<CustomPortal>>(File.ReadAllText(file), s_options) ?? [] : [];
    }

    /// <summary>The lowest number no portal has (1, 2, 3...).</summary>
    internal static int NextId(List<CustomPortal> portals) {
        var id = 1;
        while (portals.Any(p => p.Id == id)) {
            id++;
        }

        return id;
    }

    /// <summary>Saves one portal (a new one, or over the one with its number); null with an id deletes that one.</summary>
    internal static void Set(string folder, int id, CustomPortal? portal) {
        var portals = Load(folder);
        var at = portals.FindIndex(p => p.Id == id);
        if (portal is null) {
            if (at >= 0) {
                portals.RemoveAt(at);
            }
        }
        else if (at >= 0) {
            portals[at] = portal;
        }
        else {
            portals.Add(portal);
        }

        // Written beside the old file and then moved over it, so the server never reads half a file.
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FILE);
        File.WriteAllText(file + ".saving", JsonSerializer.Serialize(portals.OrderBy(p => p.Id).ToList(), s_options));
        File.Move(file + ".saving", file, overwrite: true);
    }

    /// <summary>A step in front of a spot, the way a wizard standing there with that yaw faces (the server's InFront).</summary>
    internal static (double X, double Y) InFront(double x, double y, double yaw, double distance)
        => (Math.Round(x - (Math.Sin(yaw) * distance), 2), Math.Round(y - (Math.Cos(yaw) * distance), 2));

}
