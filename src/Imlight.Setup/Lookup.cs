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
using System.Text;
using Imlight.CoreLib.Game.Commands;

namespace Imlight.Setup;

/// <summary>
/// One thing the Lookup tab can find: a command, or something in the game with its ID or name.
/// </summary>
/// <param name="Kind">Command, Item, Spell, Creature, NPC, Zone or Quest.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Id">Its template ID, when it has one.</param>
/// <param name="Detail">A short extra line, such as a school, a level or who may use a command.</param>
/// <param name="Typed">What to type in the game's chat to use it, when there is something to type.</param>
/// <param name="Help">What it does, in plain words.</param>
internal sealed record LookupEntry(string Kind, string Name, string Id, string Detail, string? Typed, string Help, bool Custom = false) {

    /// <summary>All of the text a search looks through, in lower case.</summary>
    internal string Haystack { get; } = SearchText.Normalize($"{Kind} {Name} {Id} {Detail} {Typed} {Help}");

    /// <summary>The school of an item, spell or creature ("" for the rest).</summary>
    internal string School => Kind switch {
        "Item" or "Spell" => Detail,
        "Creature" when Detail.StartsWith("Level ", StringComparison.Ordinal) => string.Join(' ', Detail.Split(' ').Skip(2)),
        "Creature" => Detail,
        _ => "",
    };

    /// <summary>A creature's level, or 0.</summary>
    internal int Level => Kind == "Creature" && Detail.StartsWith("Level ", StringComparison.Ordinal)
                          && int.TryParse(Detail.Split(' ')[1], out var level) ? level : 0;

    /// <summary>The template ID as a number, or 0.</summary>
    internal long Number => long.TryParse(Id, out var number) ? number : 0;

    /// <summary>Who can use a command: its Detail ("Every player", "Administrator only"...), "" for the rest.</summary>
    internal string Who => Kind == "Command" ? Detail : "";

    /// <summary>A teleport area's zone (what .tp takes), world, kind of area and access; "" for the rest.</summary>
    internal string Zone { get; init; } = "";
    internal string World { get; init; } = "";
    internal string Area { get; init; } = "";
    internal string Access { get; init; } = "";
    internal bool OldOrTest { get; init; }

}

/// <summary>
/// Every in-game command, and every item, spell, creature, NPC, zone and quest the server listed,
/// searchable by name or ID.
/// </summary>
internal sealed class Lookup {

    internal const string EVERYTHING = "Everything";

    /// <summary>The filters the tab offers, in order.</summary>
    internal static readonly string[] KINDS = [EVERYTHING, "Command", "Bundle", "Item", "Spell", "Creature", "NPC", TELEPORT_AREA, "Quest", "Phrase"];

    /// <summary>
    /// A zone .tp takes you to (Niduus, 2026-10-26: "add teleport area list to manager in look up"), named as the game
    /// names it, with its world, kind of area and access (spiraldb-extra/Zones/zone-details.json, from Wizard404).
    /// </summary>
    internal const string TELEPORT_AREA = "Teleport area";

    /// <summary>
    /// Every command this server build understands. Type them in the game's chat, starting with a dot. The list lives in
    /// CommandGuide.cs (shared with the server's .help).
    /// </summary>
    internal static readonly LookupEntry[] COMMANDS
        = [.. CommandGuide.All.Select(c => new LookupEntry("Command", c.Name, "", c.Who, c.Typed, c.Help, c.Custom))];

    private readonly List<LookupEntry> _entries;

    private Lookup(List<LookupEntry> entries, string? missing) {
        _entries = entries;
        Missing = missing;
    }

    /// <summary>
    /// Why the game lists are not here yet, or null when they are.
    /// </summary>
    internal string? Missing { get; }

    internal int Count => _entries.Count;

    private Dictionary<(string, string), LookupEntry>? _byId;

    /// <summary>The entry of a kind (Item, Spell, Creature, NPC) with this ID, or null.</summary>
    internal LookupEntry? ById(string kind, string id) {
        _byId ??= _entries.Where(e => e.Id.Length > 0).GroupBy(e => (e.Kind, e.Id)).ToDictionary(g => g.Key, g => g.First());

        return _byId.TryGetValue((kind, id), out var entry) ? entry : null;
    }

    /// <summary>
    /// Reads the commands and the lists the server wrote to its ID lookup folder.
    /// </summary>
    /// <param name="zoneDetails">The zones' names and kinds (zone-details.json), or null.</param>
    internal static Lookup Load(string folder, string? zoneDetails = null) {
        var entries = new List<LookupEntry>(COMMANDS);
        var ids = Path.Combine(folder, "ids.csv");
        string? missing = null;

        if (File.Exists(ids)) {
            foreach (var fields in ReadCsv(ids)) {
                if (fields.Count < 5) {
                    continue;
                }

                var (id, kind, name, school, level) = (fields[0], fields[1], fields[2], fields[3], fields[4]);
                entries.Add(kind switch {
                    "Item" => new LookupEntry("Item", name, id, school, $".mod additem {id}",
                        "Pet snacks use .mod addsnack and reagents use .mod addreagent with the same ID."),
                    "Spell" => new LookupEntry("Spell", name, id, school, $".sb learn {id}", "Adds the spell to your spellbook."),
                    "Creature" => new LookupEntry("Creature", name, id, level is "" or "0" ? school : $"Level {level} {school}".Trim(), null, ""),
                    _ => new LookupEntry("NPC", name, id, "", null, ""),
                });
            }
        }
        else {
            missing = "Items, spells, creatures and NPCs show up here once the server has made its ID list. "
                + "Start the server and give it a few minutes the first time.";
        }

        var zones = Path.Combine(folder, "zones.txt");
        if (File.Exists(zones)) {
            var details = ReadZoneDetails(zoneDetails);
            entries.AddRange(File.ReadLines(zones)
                .Select(z => z.Trim())
                .Where(z => z.Length > 0)
                .Select(z => TeleportArea(z, details.TryGetValue(z, out var d) ? d : null)));
        }

        var quests = Path.Combine(folder, "quests.csv");
        if (File.Exists(quests)) {
            foreach (var fields in ReadCsv(quests)) {
                if (fields.Count < 2 || fields[0].Length == 0) {
                    continue;
                }

                var (name, title) = (fields[0], fields[1]);
                entries.Add(new LookupEntry("Quest", title.Length > 0 ? title : name, "", title.Length > 0 ? name : "",
                    $".quest grant {name}", "Adds the quest to your quest book. .quest complete with the same name finishes it."));
            }
        }

        // The game's bundles, written by the server (the list moved out of the game's chat, Niduus 2026-10-21).
        var bundles = Path.Combine(folder, "bundles.csv");
        if (File.Exists(bundles)) {
            foreach (var fields in ReadCsv(bundles)) {
                if (fields.Count < 2 || fields[0].Length == 0) {
                    continue;
                }

                entries.Add(new LookupEntry("Bundle", fields[0], "", fields[1] == "1" ? "1 gift" : $"{fields[1]} gifts",
                    $".bundle {fields[0]}", "Puts this bundle in your gifts (the gift button). Claim it there and pick the tier you want."));
            }
        }

        // The game's own lines for .ca, by number.
        var phrases = Path.Combine(folder, "phrases.csv");
        if (File.Exists(phrases)) {
            foreach (var fields in ReadCsv(phrases)) {
                if (fields.Count < 3 || !int.TryParse(fields[0], out _)) {
                    continue;
                }

                entries.Add(new LookupEntry("Phrase", fields[1], fields[0], "", $".ca {fields[0]}",
                    "In battle, shows this line like a boss's cheat line before the next round's first spell."));
            }
        }

        return new Lookup(entries, missing);
    }

    /// <summary>
    /// Entries of a kind that hold every word of the query, best matches first.
    /// </summary>
    internal List<LookupEntry> Search(string kind, string query, int max, out int total, Func<LookupEntry, bool>? filter = null,
                                      string sort = SORT_BEST, Func<LookupEntry, bool>? favorite = null) {
        var words = SearchText.Words(query);
        // An empty search lists everything of the kind (Niduus, 2026-10-25: "have them all generate search results if left
        // empty"), commands first.
        var pool = kind == EVERYTHING ? _entries : _entries.Where(e => e.Kind == kind);
        var matches = pool.Where(e => words.All(e.Haystack.Contains) && (filter is null || filter(e))).ToList();
        total = matches.Count;
        var exact = query.Trim();

        IEnumerable<LookupEntry> sorted = sort switch {
            SORT_NAME => matches.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            SORT_NAME_DOWN => matches.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            SORT_ID => matches.OrderBy(e => e.Number == 0).ThenBy(e => e.Number),
            SORT_ID_DOWN => matches.OrderByDescending(e => e.Number),
            SORT_LEVEL => matches.OrderByDescending(e => e.Level).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            SORT_LEVEL_UP => matches.OrderBy(e => e.Level == 0).ThenBy(e => e.Level).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            SORT_KIND => matches.OrderBy(e => Array.IndexOf(KINDS, e.Kind)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => matches
                .OrderBy(e => !(e.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || e.Id == exact))
                .ThenBy(e => !e.Name.StartsWith(exact, StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => Array.IndexOf(KINDS, e.Kind))
                .ThenBy(e => words.Length == 0 ? 0 : e.Name.Length),
        };

        // Favorites first, before the list is cut, so a starred one always shows.
        if (favorite is not null) {
            sorted = sorted.OrderBy(e => !favorite(e));
        }

        return [.. sorted.Take(max)];
    }

    internal const string SORT_BEST = "Best match";
    internal const string SORT_NAME = "Name (A to Z)";
    internal const string SORT_NAME_DOWN = "Name (Z to A)";
    internal const string SORT_ID = "ID (low first)";
    internal const string SORT_ID_DOWN = "ID (high first)";
    internal const string SORT_LEVEL = "Level (high first)";
    internal const string SORT_LEVEL_UP = "Level (low first)";
    internal const string SORT_KIND = "Kind";

    /// <summary>The ways the Lookup tab can sort its list.</summary>
    internal static readonly string[] SORTS = [SORT_BEST, SORT_NAME, SORT_NAME_DOWN, SORT_KIND, SORT_ID, SORT_ID_DOWN, SORT_LEVEL, SORT_LEVEL_UP];

    /// <summary>Every entry, for the filters' choices.</summary>
    internal IReadOnlyList<LookupEntry> Entries => _entries;


    /// <summary>
    /// The rows of a CSV file after its header line, allowing quoted fields.
    /// </summary>
    internal static IEnumerable<List<string>> ReadCsv(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        reader.ReadLine();
        while (reader.ReadLine() is { } line) {
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++) {
                var c = line[i];
                if (quoted) {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"') {
                        quoted = false;
                    }
                    else {
                        field.Append(c);
                    }
                }
                else if (c == '"') {
                    quoted = true;
                }
                else if (c == ',') {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else {
                    field.Append(c);
                }
            }

            fields.Add(field.ToString());
            yield return fields;
        }
    }

    // A zone of zones.txt as a teleport area.
    private static LookupEntry TeleportArea(string zone, string[]? details) {
        var shown = details is { Length: > 0 } && details[0].Length > 0 ? details[0] : "";
        var world = ZoneWorld(zone);
        var area = zone.StartsWith("Housing", StringComparison.OrdinalIgnoreCase) ? "House"
            : details is { Length: > 2 } ? details[2] switch { "S" => "World area", "D" => "Instance (dungeon)", "T" => "Starting area", _ => "" }
            : "";
        var access = details is { Length: > 1 } ? details[1] switch { "F" => "Free", "P" => "Paid (members)", _ => "" } : "";
        var flags = details is { Length: > 3 } ? details[3] : "";
        var marks = (flags.Contains('r') ? " Old (retired) zone." : "") + (flags.Contains('d') ? " A test zone of the game's makers." : "");

        return new LookupEntry(TELEPORT_AREA, shown.Length > 0 ? shown : zone, "",
            string.Join(" · ", new[] { world, area, access }.Where(t => t.Length > 0)), $".tp {zone}",
            $"Zone {zone}. Teleport takes you there.{marks}") {
            Zone = zone, World = world, Area = area, Access = access, OldOrTest = flags.Length > 0,
        };
    }

    private static readonly (string Top, string World)[] s_zoneWorlds = [
        ("WizardCity", "Wizard City"), ("DragonSpire", "Dragonspyre"), ("MooShu", "Mooshu"), ("ThePhantomZoneWorld", "Phantom zones"),
        ("MonthlyEvents", "Events"), ("Holiday", "Events"), ("PetDerby", "Pet Derby"), ("Heroic_Dungeons_01", "Dungeons"),
    ];

    /// <summary>The world of a zone, by the start of its path (WizardCity/WC_Hub is Wizard City, Housing_... is Houses).</summary>
    internal static string ZoneWorld(string zone) {
        var top = zone.Split('/')[0];
        if (top.StartsWith("Housing", StringComparison.OrdinalIgnoreCase)) {
            return "Houses";
        }

        foreach (var (start, world) in s_zoneWorlds) {
            if (top.Equals(start, StringComparison.OrdinalIgnoreCase)) {
                return world;
            }
        }

        return top.Contains('_') ? "Other" : top;
    }

    // zone-details.json: zone -> [name, access, type, flags]. Empty when it is missing or cannot be read.
    internal static Dictionary<string, string[]> ReadZoneDetails(string? path) {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try {
            if (path is null || !File.Exists(path)) {
                return result;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var zone in document.RootElement.GetProperty("Zones").EnumerateObject()) {
                result[zone.Name] = [.. zone.Value.EnumerateArray().Select(v => v.GetString() ?? "")];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException
                                      or KeyNotFoundException) {
            // The zones show by their paths only.
        }

        return result;
    }

}
