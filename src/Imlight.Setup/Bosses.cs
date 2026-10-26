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
using Imlight.CoreLib.Game.Combat;

namespace Imlight.Setup;

/// <summary>
/// A creature as the game makes it, from the server's creatures.csv.
/// </summary>
/// <param name="Stats">Its other battle stats in words, from its gear and the game, or empty.</param>
/// <param name="Grid">The game's value of each school grid row (BossChanges.GRID_ROWS), one per school of
/// BossChanges.SCHOOLS; empty when the server's list is from before the grid.</param>
internal sealed record BossInfo(string Id, string Name, string School, int Level, int Health, bool IsBoss, List<uint> Spells, string Stats,
                                Dictionary<string, float[]> Grid) {

    internal string Haystack { get; } = SearchText.Normalize($"{Name} {Id} {School}");

}

/// <summary>A spell the Bosses tab can put in a deck, from the server's ids.csv.</summary>
internal sealed record BossSpell(uint Id, string Name, string School) {

    internal string Haystack { get; } = SearchText.Normalize($"{Name} {Id} {School}");

}

/// <summary>A spell in a boss's deck. Copies 0: it never runs out.</summary>
internal sealed class BossCard {
    internal uint SpellId { get; init; }
    internal string Name { get; init; } = "";
    internal string School { get; init; } = "";
    internal int Copies { get; set; }
}

/// <summary>
/// What the Bosses tab changes for one creature, saved as spiraldb-extra/BossOverrides/&lt;ID&gt;.json in the server
/// folder (the server's BossOverrides.cs reads it). Null keeps the game's value.
/// </summary>
internal sealed class BossChanges {

    /// <summary>The boxes of the Bosses tab, in order: the file's key, the label and what it means.</summary>
    internal static readonly (string Key, string Label, string Help)[] STATS = [
        ("FlatDamage", "Flat damage", "Added to every hit it makes."),
        ("FlatResist", "Flat resist", "Taken off every hit it takes."),
        ("Accuracy", "Accuracy %", "Added to its spells' chance to work."),
        ("OutgoingHealing", "Outgoing healing %", "More healing from its heal spells."),
        ("IncomingHealing", "Incoming healing %", "More healing when it is healed."),
        ("PowerPipChance", "Power pip chance %", "Chance a new pip is a power pip."),
        ("StartingPips", "Starting pips", "Pips it has when the battle starts (0 to 7)."),
        ("StartingPowerPips", "Starting power pips", "Power pips it has when the battle starts (0 to 7)."),
        ("ShadowPips", "Most shadow pips", "Shadow pips it can hold. 0 means none. Wins over its boss cheat file."),
        ("ShadowPipChance", "Shadow pip chance %", "Chance each round to gain a shadow pip, until it holds the most."),
    ];

    /// <summary>The boxes that take whole numbers only.</summary>
    internal static bool IsWhole(string key) => key is "StartingPips" or "StartingPowerPips" or "ShadowPips" or "ShadowPipChance";

    /// <summary>
    /// The rows of the school grid, in order: the file's key (saved as &lt;Key&gt;BySchool), the label and what it means.
    /// </summary>
    internal static readonly (string Key, string Label, string Help)[] GRID_ROWS = [
        ("Damage", "Damage %", "Extra damage it does with spells of that school. 25 means 25% more."),
        ("Resist", "Resist %", "Less damage it takes from that school. 60 means 60% less."),
        ("IncomingBoost", "Incoming boost %", "A weakness: more damage it takes from that school. 40 means 40% more."),
        ("Pierce", "Pierce %", "Goes through its target's shields, then its resist, by up to this much."),
        ("Critical", "Critical rating", "Higher means more critical hits with that school."),
        ("Block", "Block rating", "Higher means it blocks more critical hits from that school."),
    ];

    internal const string ALL = "All";

    /// <summary>The school columns of the grid, after All, as the server names them.</summary>
    internal static readonly string[] SCHOOLS = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow"];

    /// <summary>Each grid row's numbers: by school name, or All for the schools left empty.</summary>
    internal Dictionary<string, Dictionary<string, float>> Grid { get; } = [];

    /// <summary>Null keeps the game's value.</summary>
    internal bool? Stunable { get; set; }
    internal bool? Beguilable { get; set; }

    internal string Name { get; set; } = "";
    internal int? Health { get; set; }
    internal int? Level { get; set; }
    internal string? School { get; set; }
    internal Dictionary<string, float> Stats { get; } = [];
    internal List<BossCard>? Deck { get; set; }

    /// <summary>Changes to its cheats (BossCheatList.ToJson), or null to keep its cheat file as it is.</summary>
    internal JsonObject? Cheats { get; set; }

    /// <summary>
    /// The file's other fields, written back as they are: a custom's CustomID, BaseTemplateID, Drops and Forms
    /// (the Customs tab sets them).
    /// </summary>
    internal JsonObject Extra { get; set; } = [];

    /// <summary>A custom's file (Customs tab), which keeps its file even with nothing changed.</summary>
    internal bool IsCustom => Find(Extra, "BaseTemplateID") is not null;

    /// <summary>
    /// The changes without a custom's own fields (to see whether anything else is changed), or with clear, a custom
    /// file that keeps only those: its name, number, creature, drops and forms.
    /// </summary>
    internal BossChanges WithoutCustom(bool clear = false) {
        if (clear) {
            return new BossChanges { Name = Name, Extra = (JsonObject) Extra.DeepClone() };
        }

        var copy = (BossChanges) MemberwiseClone();
        copy.Extra = [];

        return copy;
    }

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    internal static string FileOf(string folder, string id) => Path.Combine(folder, $"{id}.json");

    /// <summary>The changes saved for a creature, or null when it has none.</summary>
    internal static BossChanges? Load(string folder, string id, IReadOnlyDictionary<uint, BossSpell> spells) {
        var file = FileOf(folder, id);
        if (!File.Exists(file)) {
            return null;
        }

        var json = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        }) as JsonObject ?? throw new PanelException($"{file} is not a boss file.");
        var changes = new BossChanges {
            Name = Text(json, "Name") ?? "",
            Health = (int?) Number(json, "Health"),
            Level = (int?) Number(json, "Level"),
            School = Text(json, "School") is { Length: > 0 } school ? school : null,
        };
        foreach (var (key, _, _) in STATS) {
            if (Number(json, key) is { } value) {
                changes.Stats[key] = (float) value;
            }
        }

        foreach (var (key, _, _) in GRID_ROWS) {
            var row = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (Find(json, key + "BySchool") is JsonObject bySchool) {
                foreach (var (column, node) in bySchool) {
                    if (node is JsonValue value && value.TryGetValue<double>(out var number)) {
                        row[column] = (float) number;
                    }
                }
            }

            // A v0.10.96 file kept one number for Damage, Resist, Critical and Block: it counts for every school.
            if (!row.ContainsKey(ALL) && Number(json, key) is { } single) {
                row[ALL] = (float) single;
            }

            if (row.Count > 0) {
                changes.Grid[key] = row;
            }
        }

        changes.Stunable = Flag(json, "Stunable");
        changes.Beguilable = Flag(json, "Beguilable");

        if (Find(json, "Deck") is JsonArray deck) {
            changes.Deck = [];
            foreach (var card in deck.OfType<JsonObject>()) {
                if (Number(card, "SpellID") is not { } spellId || spellId <= 0) {
                    continue;
                }

                var known = spells.GetValueOrDefault((uint) spellId);
                changes.Deck.Add(new BossCard {
                    SpellId = (uint) spellId,
                    Name = known?.Name ?? Text(card, "Name") ?? $"Spell {spellId}",
                    School = known?.School ?? "",
                    Copies = (int) (Number(card, "Copies") ?? 0),
                });
            }
        }

        changes.Cheats = Find(json, "Cheats") is JsonObject cheats ? (JsonObject) cheats.DeepClone() : null;

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "TemplateID", "Name", "Notes", "Health", "Level", "School", "Stunable", "Beguilable", "Deck", "Cheats",
        };
        handled.UnionWith(STATS.Select(s => s.Key));
        handled.UnionWith(GRID_ROWS.SelectMany(r => new[] { r.Key, r.Key + "BySchool" }));
        foreach (var (key, node) in json.Where(p => !handled.Contains(p.Key))) {
            changes.Extra[key] = node?.DeepClone();
        }

        return changes;
    }

    /// <summary>
    /// Writes the file the server reads. It is written beside the old one and then moved over it, so a battle that
    /// starts at that moment never reads half a file.
    /// </summary>
    internal void Save(string folder, string id) {
        var json = IsCustom
            ? new JsonObject {
                ["Name"] = Name,
                ["Notes"] = "A custom made in the app's Customs tab. Left out = the game's own value.",
            }
            : new JsonObject {
                ["TemplateID"] = ulong.Parse(id, CultureInfo.InvariantCulture),
                ["Name"] = Name,
                ["Notes"] = "Made in the app's Bosses tab. Left out = the game's own value.",
            };
        foreach (var (key, node) in Extra) {
            json[key] = node?.DeepClone();
        }

        if (Health is { } health) {
            json["Health"] = health;
        }

        if (Level is { } level) {
            json["Level"] = level;
        }

        if (School is { } school) {
            json["School"] = school;
        }

        foreach (var (key, _, _) in STATS) {
            if (Stats.TryGetValue(key, out var value)) {
                json[key] = IsWhole(key) ? (int) value : value;
            }
        }

        foreach (var (key, row) in Grid.Where(g => g.Value.Count > 0)) {
            var bySchool = new JsonObject();
            foreach (var column in new[] { ALL }.Concat(SCHOOLS).Where(row.ContainsKey)) {
                bySchool[column] = row[column];
            }

            json[key + "BySchool"] = bySchool;
        }

        if (Stunable is { } stunable) {
            json["Stunable"] = stunable;
        }

        if (Beguilable is { } beguilable) {
            json["Beguilable"] = beguilable;
        }

        if (Deck is not null) {
            json["Deck"] = new JsonArray([.. Deck.Select(c => (JsonNode) new JsonObject {
                ["SpellID"] = c.SpellId, ["Name"] = c.Name, ["Copies"] = c.Copies,
            })]);
        }

        if (Cheats is not null) {
            json["Cheats"] = Cheats.DeepClone();
        }

        Directory.CreateDirectory(folder);
        var file = FileOf(folder, id);
        var temp = file + ".saving";
        File.WriteAllText(temp, json.ToJsonString(s_write));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>True when nothing is changed, so there is no need for a file.</summary>
    internal bool IsEmpty => Health is null && Level is null && School is null && Stats.Count == 0 && Deck is null
                             && Grid.Values.All(r => r.Count == 0) && Stunable is null && Beguilable is null
                             && Cheats is null && Extra.Count == 0;

    /// <summary>A field of a JSON object, its name in any case.</summary>
    internal static JsonNode? FindIn(JsonObject json, string key) => Find(json, key);

    private static JsonNode? Find(JsonObject json, string key)
        => json.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool? Flag(JsonObject json, string key)
        => Find(json, key) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static string? Text(JsonObject json, string key)
        => Find(json, key) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // A double, so a spell ID (up to 4,294,967,295) keeps every digit.
    private static double? Number(JsonObject json, string key)
        => Find(json, key) is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

}

/// <summary>
/// Every creature and spell the server listed for the Bosses tab.
/// </summary>
internal sealed class BossCatalog {

    internal const string CREATURES_FILE = "creatures.csv";

    /// <summary>The schools a boss can have, as the server names them.</summary>
    internal static readonly string[] SCHOOLS = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];

    internal List<BossInfo> Creatures { get; }
    internal Dictionary<uint, BossSpell> Spells { get; }

    /// <summary>Each creature's template file in the game (ids.csv), by ID: its world is in it ("ObjectData/DM/...").</summary>
    internal Dictionary<string, string> CreaturePaths { get; } = [];

    /// <summary>Why the creature list is not here yet, or null when it is.</summary>
    internal string? Missing { get; }

    private BossCatalog(List<BossInfo> creatures, Dictionary<uint, BossSpell> spells, string? missing, Dictionary<string, string>? paths = null) {
        Creatures = creatures;
        Spells = spells;
        Missing = missing;
        CreaturePaths = paths ?? [];
    }

    /// <summary>Reads creatures.csv and the spells of ids.csv from the server's ID lookup folder.</summary>
    internal static BossCatalog Load(string folder) {
        var spells = new Dictionary<uint, BossSpell>();
        var paths = new Dictionary<string, string>();
        var ids = Path.Combine(folder, "ids.csv");
        if (File.Exists(ids)) {
            foreach (var fields in Lookup.ReadCsv(ids)) {
                if (fields.Count >= 4 && fields[1] == "Spell" && uint.TryParse(fields[0], out var id)) {
                    spells[id] = new BossSpell(id, fields[2], fields[3]);
                }
                else if (fields.Count >= 6 && fields[1] == "Creature") {
                    paths[fields[0]] = fields[5];
                }
            }
        }

        var creatures = new List<BossInfo>();
        var file = Path.Combine(folder, CREATURES_FILE);
        if (!File.Exists(file)) {
            return new BossCatalog(creatures, spells, "The boss list shows up here once the server has made it. "
                + "Start the server and give it a few minutes (the first start after an update takes longest).", paths);
        }

        foreach (var f in Lookup.ReadCsv(file)) {
            if (f.Count < 8 || f[0].Length == 0) {
                continue;
            }

            creatures.Add(new BossInfo(f[0], f[1], f[2], Int(f[3]), Int(f[4]), f[5] == "1",
                [.. f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => uint.TryParse(s, out var id) ? id : 0).Where(id => id != 0)],
                f[7], f.Count > 8 ? ParseGrid(f[8]) : []));
        }

        return new BossCatalog(creatures, spells, null, paths);
    }

    /// <summary>
    /// Creatures that hold every word of the query, bosses and exact names first. A name typed partly or with a slip
    /// finds it too when nothing holds every word as typed (CheatSearch). Filter: only these (the filters typed in the
    /// box: world, has cheats...). An empty query lists everything allowed, the changed ones first (Niduus, 2026-10-25:
    /// "have them all generate search results if left empty"). Also: another way a creature fits the query (its world,
    /// its cheats).
    /// </summary>
    internal List<BossInfo> Search(string query, bool bossesOnly, ISet<string> changed, int max, out int total,
                                   Func<BossInfo, bool>? filter = null, Func<BossInfo, string, bool>? also = null) {
        var words = SearchText.Words(query);
        // A creature's template ID typed whole is that creature and no other (Niduus, 2026-10-26).
        if (SpellNameFamily.IsSpellId(query, out _) && Creatures.FirstOrDefault(c => c.Id == query.Trim()) is { } byId) {
            total = 1;

            return [byId];
        }

        var allowed = Creatures.Where(c => (!bossesOnly || c.IsBoss || changed.Contains(c.Id)) && (filter is null || filter(c)));
        var pool = words.Length == 0
            ? allowed
            : allowed.Where(c => words.All(c.Haystack.Contains) || (also?.Invoke(c, query) ?? false));
        var matches = pool.ToList();
        if (matches.Count == 0 && words.Length > 0) {
            matches = [.. allowed.Where(c => CheatSearch.Matches($"{c.Name} {c.Id} {c.School}", query))];
        }

        total = matches.Count;
        var exact = query.Trim();

        return [.. matches
            .OrderBy(c => !(c.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || c.Id == exact))
            .ThenByDescending(c => CheatSearch.NameScore(c.Name, query))
            .ThenBy(c => !changed.Contains(c.Id))
            .ThenBy(c => !c.IsBoss)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Level)
            .Take(max)];
    }

    /// <summary>Spells that hold every word of the query, exact and shorter names first.</summary>
    internal List<BossSpell> SearchSpells(string query, int max) {
        var words = SearchText.Words(query);
        if (words.Length == 0) {
            return [];
        }

        var exact = query.Trim();
        // A template ID typed whole is that spell and no other.
        if (SpellNameFamily.IsSpellId(exact, out var typedId) && Spells.Values.FirstOrDefault(s => s.Id == typedId) is { } byId) {
            return [byId];
        }

        var found = Spells.Values.Where(s => words.All(s.Haystack.Contains)).ToList();
        // "supnova", "lord winter": partly or with a slip, when nothing holds every word as typed.
        if (found.Count == 0) {
            found = [.. Spells.Values.Where(s => CheatSearch.NameScore(s.Name, query) > 0)];
        }

        return [.. found
            .OrderBy(s => !(s.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || s.Id.ToString() == exact))
            .ThenByDescending(s => CheatSearch.NameScore(s.Name, query))
            .ThenBy(s => !words.All(s.Name.ToLowerInvariant().Contains))
            .ThenBy(s => s.Name.Length)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)];
    }

    /// <summary>The game's own deck of a creature, each spell never running out.</summary>
    internal List<BossCard> GameDeck(BossInfo boss)
        => [.. boss.Spells.Select(id => Spells.GetValueOrDefault(id) is { } s
            ? new BossCard { SpellId = id, Name = s.Name, School = s.School }
            : new BossCard { SpellId = id, Name = $"Spell {id}" })];

    // "Damage=0 0 25 ...;Resist=..." as the server's BossOverrides.GameGrid writes it.
    private static Dictionary<string, float[]> ParseGrid(string text) {
        var grid = new Dictionary<string, float[]>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var (key, values) = (part.Split('=')[0], part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : "");
            grid[key] = [.. values.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0f)];
        }

        return grid;
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

}
