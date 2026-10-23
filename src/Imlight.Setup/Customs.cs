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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// A custom creature of the Customs tab: a game creature (BaseId) with changes of its own, saved as
/// spiraldb-extra/Customs/&lt;Id&gt;.json (the server's Customs.cs reads it). Its stats, deck and cheats are in the
/// Bosses tab's format (BossChanges); Name, Drops and Forms are its own.
/// </summary>
internal sealed record CustomMob(int Id, string Name, string BaseId, List<string> Drops, Dictionary<string, int> Forms) {

    /// <summary>Its name, or "Custom 1" when it has none.</summary>
    internal string Shown => Name.Length > 0 ? Name : $"Custom {Id}";

    /// <summary>The line that brings it into a battle.</summary>
    internal string JoinMe => $".joinme {BaseId} {Id}";

    internal static string FileOf(string folder, int id) => Path.Combine(folder, $"{id}.json");

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    internal static CustomMob? Load(string folder, int id) {
        var file = FileOf(folder, id);
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is not JsonObject json) {
            return null;
        }

        var baseId = BossChanges.FindIn(json, "BaseTemplateID") is JsonValue b && double.TryParse(b.ToJsonString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? ((ulong) n).ToString(CultureInfo.InvariantCulture) : "";
        var drops = (BossChanges.FindIn(json, "Drops") as JsonArray ?? [])
            .Select(d => (d as JsonValue)?.ToString() ?? "").Where(d => d.Length > 0).ToList();
        var forms = new Dictionary<string, int>();
        foreach (var (key, node) in BossChanges.FindIn(json, "Forms") as JsonObject ?? []) {
            if (node is JsonValue v && int.TryParse(v.ToJsonString(), out var form) && form > 0) {
                forms[key] = form;
            }
        }

        return new CustomMob(id, (BossChanges.FindIn(json, "Name") as JsonValue)?.ToString() ?? "", baseId, drops, forms);
    }

    /// <summary>Every custom in the folder, by number. A file that cannot be read is left out.</summary>
    internal static List<CustomMob> All(string folder) {
        if (!Directory.Exists(folder)) {
            return [];
        }

        var customs = new List<CustomMob>();
        foreach (var file in Directory.GetFiles(folder, "*.json")) {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var id) || id <= 0) {
                continue;
            }

            try {
                if (Load(folder, id) is { } custom) {
                    customs.Add(custom);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // A file being written or broken by hand is left out until it reads again.
            }
        }

        return [.. customs.OrderBy(c => c.Id)];
    }

    /// <summary>The lowest number no custom has (1, 2, 3...).</summary>
    internal static int NextId(string folder) {
        var taken = All(folder).Select(c => c.Id).ToHashSet();
        var id = 1;
        while (taken.Contains(id) || File.Exists(FileOf(folder, id))) {
            id++;
        }

        return id;
    }

    /// <summary>Makes a new custom of a creature, with no changes yet: it fights as the game's creature.</summary>
    internal static CustomMob Create(string folder, string baseId, string name) {
        var id = NextId(folder);
        Write(folder, id, new JsonObject {
            ["CustomID"] = id,
            ["BaseTemplateID"] = ulong.Parse(baseId, CultureInfo.InvariantCulture),
            ["Name"] = name,
            ["Notes"] = "A custom made in the app's Customs tab. Left out = the game's own value.",
        });

        return Load(folder, id)!;
    }

    /// <summary>A copy of a custom with the next free number and its name followed by "(copy)".</summary>
    internal static CustomMob Duplicate(string folder, CustomMob custom) {
        var json = JsonNode.Parse(File.ReadAllText(FileOf(folder, custom.Id)), documentOptions: s_read) as JsonObject
                   ?? throw new PanelException($"Custom {custom.Id} cannot be read.");
        var id = NextId(folder);
        json["CustomID"] = id;
        json["Name"] = $"{custom.Shown} (copy)";
        Write(folder, id, json);

        return Load(folder, id)!;
    }

    // Written beside the old file and then moved over it, so the server never reads half a file.
    private static void Write(string folder, int id, JsonObject json) {
        Directory.CreateDirectory(folder);
        var file = FileOf(folder, id);
        File.WriteAllText(file + ".saving", json.ToJsonString(s_write));
        File.Move(file + ".saving", file, overwrite: true);
    }

}

/// <summary>
/// The Customs tab's rooms (spiraldb-extra/Customs/Rooms.json): for a room (a zone, with @variant for a private copy),
/// the creature templates its spawners spawn as a custom instead, or -1 for the game's creature even where its
/// ZoneObjects file says a custom.
/// </summary>
internal static class CustomRooms {

    internal const string FILE = "Rooms.json";

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    internal static Dictionary<string, Dictionary<string, int>> Load(string folder) {
        var file = Path.Combine(folder, FILE);
        if (!File.Exists(file)) {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        var rooms = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(file),
            new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip }) ?? [];

        return new(rooms, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sets what a room spawns for a creature: a custom's number, -1 for the game's, 0 for what its file says.</summary>
    internal static void Set(string folder, string room, string templateId, int custom) {
        var rooms = Load(folder);
        if (!rooms.TryGetValue(room, out var swaps)) {
            rooms[room] = swaps = [];
        }

        if (custom == 0) {
            swaps.Remove(templateId);
        }
        else {
            swaps[templateId] = custom;
        }

        if (swaps.Count == 0) {
            rooms.Remove(room);
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FILE);
        File.WriteAllText(file + ".saving", JsonSerializer.Serialize(rooms, s_write));
        File.Move(file + ".saving", file, overwrite: true);
    }

}

/// <summary>
/// Where the Customs tab puts the added objects (spiraldb-extra/Customs/Placements.json, read by the server's
/// ZoneObjects.cs, which watches it and moves the objects at once): for a room (the zone, with @variant for a variant's
/// file) and an object name, its X, Y, Z, Yaw and Scale over the ZoneObjects file's, a TemplateID (an object of the
/// room's own, or one the file does not have), or Removed. Updates replace the ZoneObjects files but never this one.
/// </summary>
internal static class CustomPlacements {

    internal const string FILE = "Placements.json";

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    internal static JsonObject Load(string folder) {
        var file = Path.Combine(folder, FILE);

        return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is JsonObject json ? json : [];
    }

    /// <summary>The placement of an object in a room, or null.</summary>
    internal static JsonObject? Of(JsonObject placements, string room, string name)
        => BossChanges.FindIn(placements, room) is JsonObject objects ? BossChanges.FindIn(objects, name) as JsonObject : null;

    /// <summary>Saves an object's placement (null removes it, so the ZoneObjects file's values count again).</summary>
    internal static void Set(string folder, string room, string name, JsonObject? placement) {
        var placements = Load(folder);
        var key = placements.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, room, StringComparison.OrdinalIgnoreCase)) ?? room;
        if (placements[key] is not JsonObject objects) {
            placements[key] = objects = [];
        }

        var nameKey = objects.Select(o => o.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        objects.Remove(nameKey);
        if (placement is not null) {
            objects[nameKey] = placement;
        }

        if (objects.Count == 0) {
            placements.Remove(key);
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FILE);
        File.WriteAllText(file + ".saving", placements.ToJsonString(s_write));
        File.Move(file + ".saving", file, overwrite: true);
    }

}

/// <summary>A spiraldb-extra/ZoneObjects file, read for the Customs tab's areas.</summary>
internal sealed record CustomArea(string File, string Zone, string Variant, string Notes, JsonObject Json) {

    /// <summary>The room's name in Rooms.json: the zone, with @variant for a private copy.</summary>
    internal string Room => Variant.Length > 0 ? $"{Zone}@{Variant}" : Zone;

    internal static List<CustomArea> All(string folder) {
        if (!Directory.Exists(folder)) {
            return [];
        }

        var areas = new List<CustomArea>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            try {
                if (JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: new JsonDocumentOptions {
                        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
                    }) is JsonObject json) {
                    areas.Add(new CustomArea(file, Text(json, "Zone"), Text(json, "Variant"), Text(json, "Notes"), json));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // A file that cannot be read is left out.
            }
        }

        return areas;
    }

    internal static string Text(JsonObject json, string key) => (BossChanges.FindIn(json, key) as JsonValue)?.ToString() ?? "";

    internal static double Number(JsonObject json, string key)
        => BossChanges.FindIn(json, key) is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var n) ? n : 0;

    internal IEnumerable<JsonObject> List(string key) => (BossChanges.FindIn(Json, key) as JsonArray ?? []).OfType<JsonObject>();

}

/// <summary>
/// A vendor or trainer of the Customs tab (Niduus, 2026-10-23: "custom Vendors/Trainer in the Custom section"), one
/// entry of spiraldb-extra/Customs/VendorsTrainers.json. The server's CustomNpcs.cs reads the file, and ZoneObjects.cs
/// watches it and puts each NPC in its zone at once.
/// </summary>
internal sealed class CustomNpc {

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = VENDOR;
    public ulong TemplateID { get; set; } = DEFAULT_LOOK;
    public string Zone { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public string Window { get; set; } = AUTOMATIC;
    public List<CustomNpcEntry> Items { get; set; } = [];
    public List<CustomNpcEntry> Spells { get; set; } = [];

    internal const string VENDOR = "Vendor";
    internal const string TRAINER = "Trainer";
    internal const string AUTOMATIC = "Automatic";

    /// <summary>Elmer Meadowgrass, a Wizard City shopkeeper (the house sellers' look).</summary>
    internal const ulong DEFAULT_LOOK = 136831;

    /// <summary>The shop windows, as the file names them, and in plain words.</summary>
    internal static readonly (string Value, string Label)[] WINDOWS = [
        (AUTOMATIC, "Pick it from the items (equipment shop for a mix)"),
        ("Equipment", "Equipment shop (clothes, wands, most things)"),
        ("Furniture", "Furniture shop"),
        ("Snack", "Pet snack shop"),
        ("Reagent", "Reagent shop"),
        ("Jewel", "Jewel shop"),
        ("Recipe", "Recipe shop"),
    ];

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsTrainer => string.Equals(Kind, TRAINER, StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    internal string Shown => Name.Trim().Length > 0 ? Name.Trim() : (IsTrainer ? $"Trainer {Id}" : $"Vendor {Id}");

    internal CustomNpc Copy() {
        var copy = (CustomNpc) MemberwiseClone();
        copy.Items = [.. Items.Select(e => new CustomNpcEntry { ID = e.ID, Price = e.Price })];
        copy.Spells = [.. Spells.Select(e => new CustomNpcEntry { ID = e.ID, Price = e.Price })];

        return copy;
    }

}

internal sealed class CustomNpcEntry {

    public uint ID { get; set; }
    public int Price { get; set; } = 1;

}

/// <summary>Reads and writes spiraldb-extra/Customs/VendorsTrainers.json.</summary>
internal static class CustomNpcs {

    internal const string FILE = "VendorsTrainers.json";

    private static readonly JsonSerializerOptions s_options = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    internal static List<CustomNpc> Load(string folder) {
        var file = Path.Combine(folder, FILE);

        return File.Exists(file) ? JsonSerializer.Deserialize<List<CustomNpc>>(File.ReadAllText(file), s_options) ?? [] : [];
    }

    /// <summary>The lowest number no vendor or trainer has (1, 2, 3...).</summary>
    internal static int NextId(List<CustomNpc> npcs) {
        var id = 1;
        while (npcs.Any(n => n.Id == id)) {
            id++;
        }

        return id;
    }

    /// <summary>Saves one NPC (a new one, or over the one with its number); null with an id deletes that one.</summary>
    internal static void Set(string folder, int id, CustomNpc? npc) {
        var npcs = Load(folder);
        var at = npcs.FindIndex(n => n.Id == id);
        if (npc is null) {
            if (at >= 0) {
                npcs.RemoveAt(at);
            }
        }
        else if (at >= 0) {
            npcs[at] = npc;
        }
        else {
            npcs.Add(npc);
        }

        // Written beside the old file and then moved over it, so the server never reads half a file.
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FILE);
        File.WriteAllText(file + ".saving", JsonSerializer.Serialize(npcs.OrderBy(n => n.Id).ToList(), s_options));
        File.Move(file + ".saving", file, overwrite: true);
    }

}
