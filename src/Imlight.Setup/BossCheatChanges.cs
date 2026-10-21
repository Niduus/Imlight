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

/// <summary>One spell a cheat casts. SpellId 0: known by Name only, as a cheat file names it.</summary>
internal sealed record CheatSpell(uint SpellId, string Name, bool Silent, bool DispelImmune = true) {

    internal JsonObject ToJson() {
        var json = new JsonObject { ["Spell"] = Name };
        if (SpellId != 0) {
            json["SpellID"] = SpellId;
        }

        if (Silent) {
            json["Silent"] = true;
        }

        if (!DispelImmune) {
            json["DispelImmune"] = false;
        }

        return json;
    }

}

/// <summary>
/// A cheat of the Bosses tab: one of the boss's cheat file (Index), or one added in the tab (Index -1). Changes holds
/// the fields set in the tab, by the cheat file's names; Spells is null while the file's spells are kept.
/// </summary>
internal sealed class CheatEdit {

    internal int Index { get; init; } = -1;
    internal JsonObject Game { get; init; } = [];
    internal Dictionary<string, JsonNode?> Changes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<CheatSpell>? Spells { get; set; }
    internal bool Off { get; set; }

    internal bool Added => Index < 0;

    internal bool IsChanged => Added || Off || Changes.Count > 0 || Spells is not null;

    /// <summary>A field as the cheat now has it: the tab's change, else the file's value, else null.</summary>
    internal JsonNode? Get(string key) => Changes.TryGetValue(key, out var changed) ? changed : GameValue(key);

    /// <summary>A field as the cheat file has it, or null.</summary>
    internal JsonNode? GameValue(string key) => BossChanges.FindIn(Game, key);

    internal string Text(string key) => Get(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    internal int Number(string key) => NumberOf(Get(key)) is { } n ? (int) n : key == "Chance" ? 100 : 0;

    /// <summary>A number however it was made (read from a file, or set in the tab as an int or a long), or null.</summary>
    internal static double? NumberOf(JsonNode? node)
        => node is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    internal bool Flag(string key) => Get(key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal List<string> List(string key) => Get(key) is JsonArray a
        ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0)] : [];

    /// <summary>The spells it casts now, in order.</summary>
    internal List<CheatSpell> CurrentSpells => Spells ?? GameSpells;

    /// <summary>The spells the cheat file gives it: Casts, or Cast as one spell; the cheat's Silent counts for each.</summary>
    internal List<CheatSpell> GameSpells {
        get {
            var silent = BossChanges.FindIn(Game, "Silent") is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            if (BossChanges.FindIn(Game, "Casts") is JsonArray casts && casts.Count > 0) {
                return [.. casts.OfType<JsonObject>().Select(c => FromJson(c, silent))];
            }

            return BossChanges.FindIn(Game, "Cast") is JsonValue cast && cast.TryGetValue<string>(out var name) && name.Length > 0
                ? [new CheatSpell(0, name, silent)] : [];
        }
    }

    internal static CheatSpell FromJson(JsonObject json, bool silent = false) => new(
        NumberOf(BossChanges.FindIn(json, "SpellID")) is { } n ? (uint) n : 0,
        BossChanges.FindIn(json, "Spell") is JsonValue s && s.TryGetValue<string>(out var name) ? name : "",
        silent || (BossChanges.FindIn(json, "Silent") is JsonValue v && v.TryGetValue<bool>(out var b) && b),
        BossChanges.FindIn(json, "DispelImmune") is not JsonValue d || !d.TryGetValue<bool>(out var immune) || immune);

    /// <summary>As the server's BossOverrides.Fingerprint: When|Cast|Line of the cheat file's cheat, lower case.</summary>
    internal string Match => string.Join('|', new[] { "When", "Cast", "Line" }
        .Select(k => BossChanges.FindIn(Game, k) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")).ToLowerInvariant();

    /// <summary>What the boss file keeps of this cheat: the changed fields, or the whole cheat when it was added.</summary>
    internal JsonObject ToJson() {
        var json = new JsonObject();
        if (!Added) {
            json["Match"] = Match;
        }

        if (Off) {
            json["Off"] = true;
        }

        foreach (var (key, value) in Changes) {
            json[key] = value?.DeepClone();
        }

        if (Spells is not null) {
            json["Casts"] = new JsonArray([.. Spells.Select(s => (JsonNode) s.ToJson())]);
            json["Cast"] = "";
            // Each spell has its own Silent now; the file's Silent for the whole cheat would count for every one.
            if (!Added && BossChanges.FindIn(Game, "Silent") is JsonValue v && v.TryGetValue<bool>(out var silent) && silent) {
                json["Silent"] = false;
            }
        }

        return json;
    }

}

/// <summary>
/// The cheats of a boss as the Bosses tab shows them: its cheat file (spiraldb-extra/BossCheats/&lt;ID&gt;.json, never
/// changed) with the tab's changes saved in its boss file (Cheats) laid over it, as the server's BossOverrides.CheatsOf
/// does.
/// </summary>
internal sealed class BossCheatList {

    internal List<CheatEdit> Cheats { get; } = [];

    /// <summary>Whether the boss has a cheat file.</summary>
    internal bool HasFile { get; private set; }

    /// <summary>How many saved changes found no cheat in the file (it lost that cheat since).</summary>
    internal int Lost { get; private set; }

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static string FileOf(string extraFolder, string id) => Path.Combine(extraFolder, "BossCheats", $"{id}.json");

    internal static BossCheatList Load(string extraFolder, string id, JsonObject? saved) {
        var list = new BossCheatList();
        var file = FileOf(extraFolder, id);
        if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is JsonObject json) {
            list.HasFile = true;
            var index = 0;
            foreach (var cheat in (BossChanges.FindIn(json, "Cheats") as JsonArray ?? []).OfType<JsonObject>()) {
                list.Cheats.Add(new CheatEdit { Index = index++, Game = (JsonObject) cheat.DeepClone() });
            }
        }

        if (saved is null) {
            return list;
        }

        var taken = new HashSet<int>();
        var changed = (BossChanges.FindIn(saved, "Changed") as JsonObject ?? [])
            .OrderBy(c => int.TryParse(c.Key, out var n) ? n : int.MaxValue);
        foreach (var (key, node) in changed) {
            if (node is not JsonObject change) {
                continue;
            }

            var match = (BossChanges.FindIn(change, "Match") as JsonValue)?.ToString().ToLowerInvariant();
            var index = int.TryParse(key, out var n) ? n : -1;
            var edit = index >= 0 && index < list.Cheats.Count && (match is not { Length: > 0 } || list.Cheats[index].Match == match)
                ? list.Cheats[index]
                : list.Cheats.FirstOrDefault(c => !taken.Contains(c.Index) && c.Match == match);
            if (edit is null || !taken.Add(edit.Index)) {
                list.Lost++;

                continue;
            }

            Apply(edit, change);
        }

        foreach (var added in (BossChanges.FindIn(saved, "Added") as JsonArray ?? []).OfType<JsonObject>()) {
            var edit = new CheatEdit();
            Apply(edit, added);
            list.Cheats.Add(edit);
        }

        return list;
    }

    private static void Apply(CheatEdit edit, JsonObject change) {
        foreach (var (key, value) in change) {
            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (key.Equals("Off", StringComparison.OrdinalIgnoreCase)) {
                edit.Off = value is JsonValue v && v.TryGetValue<bool>(out var off) && off;
            }
            else if (key.Equals("Casts", StringComparison.OrdinalIgnoreCase)) {
                edit.Spells = [.. (value as JsonArray ?? []).OfType<JsonObject>().Select(c => CheatEdit.FromJson(c))];
            }
            else if (key.Equals("Cast", StringComparison.OrdinalIgnoreCase) && edit.Spells is not null) {
                // Written with Casts so the file's single spell is not cast too.
            }
            else if (key.Equals("Silent", StringComparison.OrdinalIgnoreCase) && change.Any(p => p.Key.Equals("Casts", StringComparison.OrdinalIgnoreCase))) {
                // Each spell keeps its own Silent.
            }
            else {
                edit.Changes[key] = value?.DeepClone();
            }
        }

        // An added cheat that names one spell with Cast (as a cheat file does) shows it in its list.
        if (edit.Added && edit.Spells is null && edit.Changes.Remove("Cast", out var cast)
            && cast is JsonValue c && c.TryGetValue<string>(out var name) && name.Length > 0) {
            edit.Spells = [new CheatSpell(0, name, edit.Changes.Remove("Silent", out var s) && s is JsonValue sv && sv.TryGetValue<bool>(out var silent) && silent)];
        }
    }

    /// <summary>The Cheats section of the boss file, or null when no cheat is changed.</summary>
    internal JsonObject? ToJson() {
        var changed = new JsonObject();
        foreach (var edit in Cheats.Where(c => !c.Added && c.IsChanged)) {
            changed[edit.Index.ToString(CultureInfo.InvariantCulture)] = edit.ToJson();
        }

        var added = new JsonArray([.. Cheats.Where(c => c.Added).Select(c => (JsonNode) c.ToJson())]);
        if (changed.Count == 0 && added.Count == 0) {
            return null;
        }

        return new JsonObject { ["Changed"] = changed, ["Added"] = added };
    }

}

/// <summary>The game's lines (phrases.csv of the ID lookup folder), to show a cheat's line as the battle shows it.</summary>
internal sealed class CheatLines {

    private readonly List<(int Number, string Text, string Key)> _phrases = [];

    internal static CheatLines Load(string idLookupFolder) {
        var lines = new CheatLines();
        var file = Path.Combine(idLookupFolder, "phrases.csv");
        if (File.Exists(file)) {
            foreach (var f in Lookup.ReadCsv(file)) {
                if (f.Count >= 3 && int.TryParse(f[0], out var number)) {
                    lines._phrases.Add((number, f[1], f[2]));
                }
            }
        }

        return lines;
    }

    internal bool Any => _phrases.Count > 0;

    /// <summary>
    /// The line a cheat's Line shows: the phrase of that number, the phrase of that key, or the first phrase holding
    /// the words. Null when none is found. The server's search (.ca) can pick a different phrase when several hold them.
    /// </summary>
    internal string? Resolve(string line) {
        line = line.Trim();
        if (line.Length == 0) {
            return null;
        }

        if (int.TryParse(line, out var number)) {
            return _phrases.FirstOrDefault(p => p.Number == number).Text;
        }

        return _phrases.FirstOrDefault(p => p.Key.Equals(line, StringComparison.OrdinalIgnoreCase)).Text
               ?? _phrases.FirstOrDefault(p => p.Text.Equals(line, StringComparison.OrdinalIgnoreCase)).Text
               ?? _phrases.FirstOrDefault(p => p.Text.Contains(line, StringComparison.OrdinalIgnoreCase)).Text;
    }

}
