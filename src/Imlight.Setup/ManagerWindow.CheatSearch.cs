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
using System.Text.Json.Nodes;
using Imlight.CoreLib.Game.Combat;

namespace Imlight.Setup;

/// <summary>
/// The searches of the cheat editor's pickers and the Bosses tab (Niduus, 2026-10-25: "Expand on existing cheats and
/// search methods"): creatures by world, zone (their game file) and cheats; bosses whose cheats use a block, a spell or
/// a word; spells by school, kind of card and pip cost, typed partly or with a slip. The words are in CheatSearch.
/// </summary>
internal sealed partial class ManagerWindow {

    // Each creature's cheats in words (its cheat file and its saved cheat changes), the world its cheat file names, and
    // when they were read.
    private sealed record CheatIndexEntry(List<(string Summary, string Text)> Cheats, string World) {

        // Its cheats: the file's and the ones added in the app (a changed one stands for the file's, so it is not counted twice).
        internal int Count { get; set; }

    }

    private Dictionary<string, CheatIndexEntry> _cheatIndex = [];
    private DateTime _cheatIndexFrom = DateTime.MinValue;
    private DateTime _cheatIndexChecked = DateTime.MinValue;

    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private string CheatFilesFolder => Path.Combine(CheatExtraFolder, "BossCheats");

    // The newest time a cheat file or a saved change was written.
    private DateTime CheatFilesWritten()
        => new[] { CheatFilesFolder, _manager.BossChangesFolder }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.json"))
            .Select(File.GetLastWriteTimeUtc)
            .Append(Directory.Exists(CheatFilesFolder) ? Directory.GetLastWriteTimeUtc(CheatFilesFolder) : DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

    /// <summary>Each creature's cheats in words, read again when a cheat file or a saved change is newer.</summary>
    private Dictionary<string, CheatIndexEntry> CheatIndex() {
        // A search asks for it once per creature: the files are looked at again only every few seconds.
        if (DateTime.UtcNow - _cheatIndexChecked < TimeSpan.FromSeconds(3)) {
            return _cheatIndex;
        }

        _cheatIndexChecked = DateTime.UtcNow;
        var written = CheatFilesWritten();
        if (written <= _cheatIndexFrom && _cheatIndex.Count > 0) {
            return _cheatIndex;
        }

        var index = new Dictionary<string, CheatIndexEntry>();
        void Add(string id, IEnumerable<JsonNode?>? cheats, string world, bool counted) {
            var entry = index.TryGetValue(id, out var had) ? had : index[id] = new CheatIndexEntry([], world);
            foreach (var cheat in (cheats ?? []).OfType<JsonObject>()) {
                if (counted) {
                    entry.Count++;
                }

                try {
                    var rule = CheatRule.FromCheat(cheat);
                    var blocks = rule.Ifs.Concat(rule.Dos).Append(rule.When).Select(b => $"{b.Kind.Palette} {b.Spec}");
                    entry.Cheats.Add((CheatSpecs.Summary(rule), $"{CheatSpecs.Summary(rule)} {string.Join(' ', blocks)} {cheat.ToJsonString()}"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException) {
                    entry.Cheats.Add(("", cheat.ToJsonString()));
                }
            }
        }

        if (Directory.Exists(CheatFilesFolder)) {
            foreach (var file in Directory.EnumerateFiles(CheatFilesFolder, "*.json")) {
                try {
                    var text = File.ReadAllText(file);
                    if (JsonNode.Parse(text, null, s_jsonc) is JsonObject json) {
                        Add(Path.GetFileNameWithoutExtension(file), BossChanges.FindIn(json, "Cheats") as JsonArray, CheatSearch.WorldOfCheatFile(text), counted: true);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
                    // A file that cannot be read is left out of the search; the Bosses tab says so when it is opened.
                }
            }
        }

        // The cheats added or changed in the app are in the boss's saved changes.
        if (Directory.Exists(_manager.BossChangesFolder)) {
            foreach (var file in Directory.EnumerateFiles(_manager.BossChangesFolder, "*.json")) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(file), null, s_jsonc) is JsonObject json && BossChanges.FindIn(json, "Cheats") is JsonObject cheats) {
                        // Changed: { "3": the whole changed cheat }, Added: [ new cheats ] (BossCheatList.ToJson).
                        var id = Path.GetFileNameWithoutExtension(file);
                        var world = index.TryGetValue(id, out var had) ? had.World : "";
                        Add(id, (BossChanges.FindIn(cheats, "Changed") as JsonObject)?.Select(p => p.Value), world, counted: false);
                        Add(id, BossChanges.FindIn(cheats, "Added") as JsonArray, world, counted: true);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
                    // As above.
                }
            }
        }

        _cheatIndex = index;
        _cheatIndexFrom = written;

        return index;
    }

    /// <summary>A creature's world: as its cheat file names it, else from its game file ("ObjectData/DM/..." is Darkmoor).</summary>
    private string WorldOf(BossInfo creature)
        => CheatIndex().TryGetValue(creature.Id, out var entry) && entry.World.Length > 0 ? entry.World
            : CheatSearch.WorldOfPath(_bossCatalog?.CreaturePaths.GetValueOrDefault(creature.Id) ?? "");

    private int CheatCountOf(BossInfo creature) => CheatIndex().TryGetValue(creature.Id, out var entry) ? entry.Count : 0;

    // The first of its cheats that uses all of these words, blocks or spells (synonyms and slips count), or null.
    private string? CheatUsing(BossInfo creature, string words)
        => words.Trim().Length == 0 || !CheatIndex().TryGetValue(creature.Id, out var entry) ? null
            : entry.Cheats.FirstOrDefault(c => CheatSearch.Matches(c.Text, words)) is { Text.Length: > 0 } hit
                ? hit.Summary.Length > 0 ? hit.Summary : "a cheat" : null;

    /// <summary>
    /// Creatures for a search: its words (names and IDs, or with inCheats their cheats too) and its filters: world:Darkmoor
    /// (the world its cheat file or game file names), zone:DM_Player (words of its game file's name), has:cheats (or
    /// has:nocheats), uses:"cap damage" (a cheat of it uses that block, spell or word).
    /// </summary>
    private List<BossInfo> FindCreatures(string text, bool bossesOnly, ISet<string> changed, int max, out int total, bool inCheats = false,
                                         bool withCheats = false) {
        total = 0;
        if (_bossCatalog is null) {
            return [];
        }

        var query = CheatSearch.Parse(text);
        var world = query.Filter("world");
        var zone = query.Filter("zone");
        var has = query.Filter("has").ToLowerInvariant();
        var uses = query.Filter("uses");
        var needCheats = withCheats || has is "cheats" or "cheat";
        var noCheats = has is "nocheats" or "no-cheats" or "none";
        bool Filter(BossInfo c) => (world.Length == 0 || CheatSearch.Matches(WorldOf(c), world))
                                   && (zone.Length == 0 || CheatSearch.Matches(_bossCatalog.CreaturePaths.GetValueOrDefault(c.Id) ?? "", zone))
                                   && (!needCheats || CheatCountOf(c) > 0) && (!noCheats || CheatCountOf(c) == 0)
                                   && (uses.Length == 0 || CheatUsing(c, uses) is not null);
        var filtered = world.Length > 0 || zone.Length > 0 || needCheats || noCheats || uses.Length > 0;
        // Its world and, when asked, its cheats are words it is found by too ("winged darkmoor", "remove jinx").
        bool Also(BossInfo c, string words) => CheatSearch.Matches(
            $"{c.Name} {c.Id} {WorldOf(c)} {(inCheats && CheatIndex().TryGetValue(c.Id, out var e) ? string.Join(' ', e.Cheats.Select(x => x.Text)) : "")}", words);
        var found = _bossCatalog.Search(query.Words, bossesOnly, changed, max, out total, filtered ? Filter : null, Also);

        return found;
    }

    /// <summary>Words under a creature in a list: its world, how many cheats it has, and the cheat a search found.</summary>
    private string CreatureNote(BossInfo creature, string text, bool inCheats) {
        var query = CheatSearch.Parse(text);
        var hit = query.Filter("uses") is { Length: > 0 } uses ? CheatUsing(creature, uses)
            : inCheats && query.Words.Length > 0 && !CheatSearch.Matches($"{creature.Name} {creature.Id}", query.Words) ? CheatUsing(creature, query.Words) : null;
        var count = CheatCountOf(creature);

        return string.Join("  ·  ", new[] {
            WorldOf(creature), count > 0 ? count == 1 ? "1 cheat" : $"{count} cheats" : "", hit is null ? "" : $"cheat: {hit}",
        }.Where(t => t.Length > 0));
    }

    /// <summary>
    /// Spells for a picker: by name typed partly or with a slip, and by school, kind of card (Category's words, jinx and
    /// curse too) and pip cost ("4", "4-6", "4+", "x"), from the box's choices or typed (school:Fire kind:trap pips:4).
    /// The kind and cost need the game's spell files; without them only the name and school are looked at (note says so).
    /// </summary>
    private List<(BossSpell Spell, string Detail)> FindSpells(string text, string school, string kind, string pips, int max, out string note) {
        note = "";
        var query = CheatSearch.Parse(text);
        school = query.Filter("school") is { Length: > 0 } typedSchool ? typedSchool : school;
        kind = SpellNameFamily.KindSynonym(query.Filter("kind") is { Length: > 0 } typedKind ? typedKind : kind);
        var cost = CheatSearch.PipRange(query.Filter("pips") is { Length: > 0 } typedPips ? typedPips : pips);
        var words = query.Words;
        if (words.Length == 0 && school.Length == 0 && kind.Length == 0 && cost is null) {
            return [];
        }

        bool SchoolOk(string s) => school.Length == 0 || school.Equals(s, StringComparison.OrdinalIgnoreCase);
        if (_spellBook is { } book) {
            var cards = book.Spells.Where(c => SchoolOk(c.School) && CheatSearch.CostFits(cost, c.Pips, c.XPips)
                                               && (kind.Length == 0 || SpellNameFamily.CategoryMatches(kind, KindsOf(c))
                                                   || kind.Equals(c.Type, StringComparison.OrdinalIgnoreCase)))
                .Select(c => (Card: c, Score: words.Length == 0 ? 1 : Math.Max(CheatSearch.NameScore(c.Name, words), CheatSearch.NameScore(c.Code, words))))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score).ThenBy(m => m.Card.Hidden).ThenBy(m => m.Card.Name.Length).ThenBy(m => m.Card.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();

            return [.. cards.Select(m => (new BossSpell(m.Card.Id, m.Card.Name, m.Card.School),
                $"{m.Card.School}  ·  {m.Card.Type}  ·  {m.Card.Cost}  ·  ID {m.Card.Id}"))];
        }

        if (_bossCatalog is not { Spells.Count: > 0 } catalog) {
            return [];
        }

        if (kind.Length > 0 || cost is not null) {
            note = "The kind of card and pip cost are looked at once the game's spell files are read; this list is by name and school only.";
        }

        var spells = (words.Length == 0 ? catalog.Spells.Values.Where(s => SchoolOk(s.School)).OrderBy(s => s.Name).Take(max).ToList()
                : [.. catalog.SearchSpells(words, 400).Where(s => SchoolOk(s.School)).Take(max)]);

        return [.. spells.Select(s => (s, $"{s.School}  ·  ID {s.Id}"))];
    }

}
