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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// Reads and writes the custom area files (spiraldb-extra/ZoneObjects/*.json, read by the server's ZoneObjects.cs,
/// which watches them). The whole file is kept as JSON, so fields written by hand (Notes, and anything the app does
/// not know) stay as they are. A file the app saved carries "EditedInManager", so an update keeps it; the first save
/// keeps a copy of the file as it was in spiraldb-extra/Customs/Area originals, which "Go back to the original" puts
/// back.
/// </summary>
internal static class AreaFiles {

    internal const string FOLDER = "ZoneObjects";
    internal const string ORIGINALS = "Area originals";
    internal const string EDITED = "EditedInManager";

    private static readonly JsonSerializerOptions s_write = new() {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly JsonDocumentOptions READ = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The file name the server expects: the zone path with / as -, then @variant if it has one.</summary>
    internal static string FileNameOf(string zone, string variant)
        => zone.Trim().Replace('/', '-').Replace('\\', '-') + (variant.Trim().Length == 0 ? "" : "@" + variant.Trim()) + ".json";

    internal static string OriginalOf(string customsFolder, string file) => Path.Combine(customsFolder, ORIGINALS, Path.GetFileName(file));

    /// <summary>
    /// Saves an area: written beside the old file and moved over it, so the server never reads half a file. When the
    /// zone or variant changed, the file takes its new name and the old one goes. Returns the path written.
    /// </summary>
    internal static string Save(string folder, string customsFolder, string? oldFile, JsonObject json) {
        var zone = CustomArea.Text(json, "Zone").Trim();
        if (zone.Length == 0) {
            throw new PanelException("An area needs a zone, such as WizardCity/WC_Ravenwood.");
        }

        var variant = CustomArea.Text(json, "Variant").Trim();
        if (variant.IndexOfAny(['/', '\\', '@', ':', '*', '?', '"', '<', '>', '|']) >= 0) {
            throw new PanelException("A variant name is letters, numbers and spaces only, such as RavenwoodMalistaire.");
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FileNameOf(zone, variant));
        if (!string.Equals(Path.GetFullPath(file), oldFile is null ? null : Path.GetFullPath(oldFile), StringComparison.OrdinalIgnoreCase)
            && File.Exists(file)) {
            throw new PanelException($"There is already an area for {zone}{(variant.Length > 0 ? " (" + variant + ")" : "")} ({Path.GetFileName(file)}).");
        }

        // The file as it came, once, so it can be put back.
        if (oldFile is not null && File.Exists(oldFile)) {
            var original = OriginalOf(customsFolder, oldFile);
            if (!File.Exists(original) && !File.ReadAllText(oldFile).Contains($"\"{EDITED}\"", StringComparison.OrdinalIgnoreCase)) {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(oldFile, original);
            }
        }

        json[EDITED] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        File.WriteAllText(file + ".saving", json.ToJsonString(s_write));
        File.Move(file + ".saving", file, overwrite: true);
        if (oldFile is not null && File.Exists(oldFile) && !string.Equals(Path.GetFullPath(oldFile), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) {
            File.Delete(oldFile);
        }

        return file;
    }

    /// <summary>Puts back the file as it was before the app first saved it. Returns false when there is no copy.</summary>
    internal static bool RestoreOriginal(string customsFolder, string file) {
        var original = OriginalOf(customsFolder, file);
        if (!File.Exists(original)) {
            return false;
        }

        File.Copy(original, file + ".saving", overwrite: true);
        File.Move(file + ".saving", file, overwrite: true);
        File.Delete(original);

        return true;
    }

    /// <summary>A new area file for a zone (and variant), empty or copied from another area.</summary>
    internal static string Create(string folder, string customsFolder, string zone, string variant, JsonObject? from) {
        var json = from is null ? new JsonObject() : (JsonObject) from.DeepClone();
        json["Zone"] = zone.Trim();
        if (variant.Trim().Length > 0) {
            json["Variant"] = variant.Trim();
        }
        else {
            json.Remove("Variant");
        }

        json.Remove(EDITED);
        if (from is null) {
            json["Notes"] = "Made in the Manager's Customs tab.";
        }

        return Save(folder, customsFolder, null, json);
    }

    internal static string Pretty(JsonNode json) => json.ToJsonString(s_write);

}

/// <summary>
/// What the Customs tab's area pickers know about the game: zones, named places, the zone's own objects, spawners and
/// teleport triggers, and object templates. It reads the server's ID lookup lists (ids.csv, zones.txt) and, when the
/// Game Files tab has read the game files, its Key details tables. Anything missing is simply not offered.
/// </summary>
internal sealed class AreaCatalog {

    internal sealed record Template(ulong Id, string Name, string Shown, string Kind, string School) {

        internal string Haystack { get; } = $"{Id} {Name} {Shown} {Kind} {School}".ToLowerInvariant();

        internal string Label => Shown.Length > 0 && !Shown.Equals(Name, StringComparison.OrdinalIgnoreCase) ? $"{Shown} ({Name})" : Name;

    }

    internal sealed record Place(string Name, double X, double Y, double Z, double Facing) {

        internal string Loc => string.Join(",", new[] { X, Y, Z, Facing }.Select(n => Math.Round(n, 3).ToString(CultureInfo.InvariantCulture)));

    }

    internal sealed record ZoneObject(ulong TemplateId, string Name, double X, double Y, double Z);

    internal List<string> Zones { get; } = [];
    internal Dictionary<string, string> ZoneNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<Place>> Places { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<ZoneObject>> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<string>> Spawners { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<string>> Teleports { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<ulong, Template> Templates { get; } = [];

    /// <summary>True when the Game Files tab's tables were found (objects, places, spawners, teleports).</summary>
    internal bool HasGameTables { get; private set; }

    internal static readonly string[] SIGIL_TYPES = [
        "MinigameSemiCircleSigil4Actor", "MinigameSemiCircleSigil2Actor", "MinigameSemiCircleSigil1Actor",
        "MinigameCircleSigil4Actor", "MinigameSigil4Actor",
    ];

    internal static readonly string[] COMBAT_SIGIL_TYPES = [
        "CombatSigil_4EnemySlots", "CombatSigil_3EnemySlots", "CombatSigil_2EnemySlots", "CombatSigil_1EnemySlot",
        "CombatSigilDarkmoorMalistaireBoss", PVP_SIGIL_TYPE,
    ];

    /// <summary>The game's PvP circle: as a battle circle it is a duel circle, wizards on its two sides duel.</summary>
    internal const string PVP_SIGIL_TYPE = "PvPSigil8Actor2Sides";

    /// <summary>
    /// A sigil pad's looks, in plain words, with the state the server sends for each (MinigameSigilStatesNew in
    /// Root.wad). Red is the Advanced Mode look: the pad's On_Red animation, a red glow.
    /// </summary>
    internal static readonly (string Label, string State)[] SIGIL_LOOKS = [
        ("Lit: the usual blue glow", "Glowy"),
        ("Advanced Mode: a red glow", "Red"),
        ("Off: no glow", "Off"),
    ];

    /// <summary>The sigil templates whose animations have the red Advanced Mode glow (FX_BattleCircle_4X_New.xml).</summary>
    internal static readonly ulong[] ADVANCED_LOOK_TEMPLATES = [107081, 1313007, 1313009];

    // Friendly names for the sigil pads in the template picker (the game gives most of them none).
    private static readonly Dictionary<ulong, string> s_sigilNames = new() {
        [107081] = "Sigil pad, 4 players (blue, or red for Advanced Mode)",
        [1634569] = "Sigil pad, 2 players (blue only)",
        [1313007] = "Skeleton Key sigil, for looks (blue, or red for Advanced Mode)",
        [1313009] = "Skeleton Key sigil, invisible pad (no model)",
    };

    internal static AreaCatalog Load(string idLookupFolder, string keyDetailsFolder) {
        var catalog = new AreaCatalog();
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileToZone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Try(() => {
            var path = Path.Combine(idLookupFolder, "zones.txt");
            if (File.Exists(path)) {
                foreach (var z in File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0)) {
                    zones.Add(z);
                }
            }
        });
        Try(() => {
            var path = Path.Combine(idLookupFolder, "ids.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 4 && f[1] is "Creature" or "Npc" && ulong.TryParse(f[0], out var id)) {
                    catalog.Templates[id] = new Template(id, f[2], "", f[1] == "Npc" ? "NPC" : "Creature", f[3]);
                }
            }
        });

        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "Zones.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 2 && f[1].Length > 0) {
                    zones.Add(f[1]);
                    fileToZone[f[0]] = f[1];
                    if (f.Count >= 7 && f[6].IndexOf("zoneDisplayName=", StringComparison.Ordinal) is var at and >= 0) {
                        var open = f[6].IndexOf("(\"", at, StringComparison.Ordinal);
                        var close = open < 0 ? -1 : f[6].IndexOf("\")", open, StringComparison.Ordinal);
                        if (open >= 0 && close > open && close - open < 120) {
                            catalog.ZoneNames[f[1]] = f[6][(open + 2)..close];
                        }
                    }
                }
            }
        });

        string ZoneOf(string file) => fileToZone.TryGetValue(file, out var z) ? z : file.Replace('-', '/');

        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "Zone places.csv");
            if (!File.Exists(path)) {
                return;
            }

            catalog.HasGameTables = true;
            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 5 && Num(f[2]) is { } x && Num(f[3]) is { } y && Num(f[4]) is { } z) {
                    Add(catalog.Places, ZoneOf(f[0]), new Place(f[1], x, y, z, f.Count > 5 ? Num(f[5]) ?? 0 : 0));
                }
            }
        });
        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "NPCs and objects.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 7 && ulong.TryParse(f[2], out var id) && id > 0) {
                    Add(catalog.Objects, ZoneOf(f[0]), new ZoneObject(id, f[3], Num(f[4]) ?? 0, Num(f[5]) ?? 0, Num(f[6]) ?? 0));
                }
            }
        });
        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "Spawns.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 2 && f[1].Length > 0) {
                    Add(catalog.Spawners, ZoneOf(f[0]), f[1] + (f.Count > 6 && f[6].Length > 0 ? "\t" + f[6] : ""));
                }
            }
        });
        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "Triggers.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 9 && f[1].Length > 0 && f[8].Contains("Teleport", StringComparison.OrdinalIgnoreCase)) {
                    Add(catalog.Teleports, ZoneOf(f[0]), f[1]);
                }
            }
        });
        Try(() => {
            var path = Path.Combine(keyDetailsFolder, "Items and objects.csv");
            if (!File.Exists(path)) {
                return;
            }

            foreach (var f in Lookup.ReadCsv(path)) {
                if (f.Count >= 4 && ulong.TryParse(f[0], out var id) && !catalog.Templates.ContainsKey(id)) {
                    var kind = f[3] switch {
                        "WizItemTemplate" or "ItemTemplate" => "Item",
                        "DynamicTriggerTemplate" => "Sigil or trigger",
                        _ => "Object",
                    };
                    catalog.Templates[id] = new Template(id, f[1], f[2], kind, f.Count > 5 ? f[5] : "");
                }
                else if (f.Count >= 3 && ulong.TryParse(f[0], out var known) && catalog.Templates.TryGetValue(known, out var had) && had.Shown.Length == 0) {
                    catalog.Templates[known] = had with { Shown = f[2] };
                }
            }
        });

        foreach (var (id, shown) in s_sigilNames) {
            if (catalog.Templates.TryGetValue(id, out var sigil)) {
                catalog.Templates[id] = new Template(id, sigil.Name, shown, sigil.Kind, sigil.School);
            }
        }

        catalog.Zones.AddRange(zones.OrderBy(z => z, StringComparer.OrdinalIgnoreCase));
        foreach (var list in catalog.Spawners.Values) {
            var distinct = list.Distinct().ToList();
            list.Clear();
            list.AddRange(distinct);
        }

        return catalog;

        static void Try(Action read) {
            try {
                read();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A list that cannot be read is not offered.
            }
        }
    }

    private static List<T> Add<T>(Dictionary<string, List<T>> map, string key, T value) {
        if (!map.TryGetValue(key, out var list)) {
            map[key] = list = [];
        }

        list.Add(value);

        return list;
    }

    private static double? Num(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Templates that hold every word of the query (names, IDs, kinds), exact matches first.</summary>
    internal List<Template> Search(string query, int max, Func<Template, bool>? filter = null) {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            return [];
        }

        var exact = query.Trim();

        return [.. Templates.Values
            .Where(t => (filter is null || filter(t)) && words.All(t.Haystack.Contains))
            .OrderBy(t => !(t.Id.ToString(CultureInfo.InvariantCulture) == exact || t.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)
                            || t.Shown.Equals(exact, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(t => t.Kind == "Item")
            .ThenBy(t => t.Label.Length)
            .Take(max)];
    }

    /// <summary>A template's name, or "" when it is not known.</summary>
    internal string NameOf(ulong id) => Templates.TryGetValue(id, out var t) ? t.Label : "";

    internal string ZoneLabel(string zone) => ZoneNames.TryGetValue(zone, out var name) && name.Length > 0 ? $"{name} ({zone})" : zone;

    internal List<Place> PlacesIn(string zone) => Places.TryGetValue(zone, out var list) ? list : [];

    internal List<ZoneObject> ObjectsIn(string zone) => Objects.TryGetValue(zone, out var list) ? list : [];

    internal List<string> SpawnersIn(string zone) => Spawners.TryGetValue(zone, out var list) ? list : [];

    internal List<string> TeleportsIn(string zone) => Teleports.TryGetValue(zone, out var list) ? [.. list.Distinct()] : [];

}
