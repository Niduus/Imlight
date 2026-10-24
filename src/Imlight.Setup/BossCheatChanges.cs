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
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>The kinds of box a cheat block's sentence can hold (CheatSpecs).</summary>
internal enum CheatFieldType {
    Number, Percent, Words, Choice, Toggle, Spell, Creature, Line, Cards, Rounds, Health, School, Category, Target, HealAmount, Sign,
    // "any version of" / "exactly" (a yes/no value); the live "matches 7 spells" count (no value of its own); the player
    // cheats' targets (me, p1-p4, e1-e4, all).
    VersionChoice, MatchCount, PlayerTarget,
    // The enchantments picked for an enchanted free cast (a list of names), shown while its Enchant box is ticked.
    Enchants,
}

/// <summary>A box of a cheat block: the cheat file's name for it, its type, its first value and, for a Choice, its choices.</summary>
internal sealed record CheatField(string Key, CheatFieldType Type, JsonNode? Default = null, (string Value, string Label)[]? Options = null,
                                  string Label = "", int Max = 100_000_000, int Min = 0);

/// <summary>
/// A kind of cheat block: a When (what sets the cheat off), an If (only when) or a Do (what it does), with the words of
/// its sentence ("Cast {Spell} on {Target}": each {Key} is one of its Fields) and what it does in plain words.
/// </summary>
/// Scope: "both" (the boss and custom editors and Player cheats alike: Niduus, 2026-10-23, "ALL CHEATS are to be
/// compatible for PLAYERS and CREATURES"), or "alias": an old block that is the same as another, kept to read old files
/// and never offered.
internal sealed record CheatSpec(string Id, BlockKind Kind, string Icon, string Palette, string Help, string Sentence, CheatField[] Fields,
                                 Func<CheatBlock, string> Summary, string Scope = "both") {

    internal bool For(bool player) => Scope != "alias";

}

/// <summary>One block of a cheat: its kind (a CheatSpec) and its values, by the cheat file's names.</summary>
internal sealed class CheatBlock {

    internal required string Spec { get; init; }
    internal JsonObject Values { get; init; } = [];
    internal bool On { get; set; } = true;

    internal CheatSpec Kind => CheatSpecs.Get(Spec);

    internal CheatBlock Copy() => new() { Spec = Spec, Values = (JsonObject) Values.DeepClone(), On = On };

    internal string Text(string key) => BossChanges.FindIn(Values, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    internal int Number(string key)
        => BossChanges.FindIn(Values, key) is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? (int) Math.Clamp(n, int.MinValue, int.MaxValue) : 0;

    internal uint Id(string key)
        => BossChanges.FindIn(Values, key) is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? (uint) Math.Clamp(n, 0, uint.MaxValue) : 0;

    internal bool Flag(string key) => BossChanges.FindIn(Values, key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal List<string> List(string key) => BossChanges.FindIn(Values, key) is JsonArray a
        ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0)] : [];

    internal void Set(string key, JsonNode? value) {
        var existing = Values.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) {
            Values.Remove(existing);
        }

        Values[key] = value;
    }

    /// <summary>A new block of a kind, with its boxes at their first values.</summary>
    internal static CheatBlock New(CheatSpec spec, JsonObject? values = null) {
        var block = new CheatBlock { Spec = spec.Id };
        foreach (var field in spec.Fields) {
            foreach (var (key, value) in Defaults(field)) {
                block.Set(key, value?.DeepClone());
            }
        }

        foreach (var (key, value) in values ?? []) {
            block.Set(key, value?.DeepClone());
        }

        return block;
    }

    private static IEnumerable<(string, JsonNode?)> Defaults(CheatField field) => field.Type switch {
        CheatFieldType.Rounds => [("Round", 0), ("Every", 0)],
        CheatFieldType.Health => [("Percent", 50), ("Health", 0)],
        CheatFieldType.HealAmount => [("Amount", 500), ("Percent", false)],
        CheatFieldType.Spell => [("Spell", ""), ("SpellID", 0)],
        CheatFieldType.Creature => [("Creature", 0), ("Custom", 0)],
        CheatFieldType.Cards or CheatFieldType.Enchants => [(field.Key, new JsonArray())],
        CheatFieldType.MatchCount => [],
        _ => [(field.Key, field.Default?.DeepClone() ?? field.Type switch {
            CheatFieldType.Number or CheatFieldType.Percent or CheatFieldType.Sign => JsonValue.Create(0),
            CheatFieldType.Toggle => JsonValue.Create(false),
            _ => JsonValue.Create(""),
        })],
    };

}

/// <summary>
/// One cheat as the app shows it: a When block, its If blocks and its Do blocks (Niduus, 2026-10-22: Scratch-like
/// blocks). Index is its place in the boss's cheat file, -1 for one added in the app; Game is that file's cheat.
/// </summary>
internal sealed class CheatRule {

    internal int Index { get; init; } = -1;
    internal JsonObject Game { get; init; } = [];
    internal required CheatBlock When { get; set; }
    internal List<CheatBlock> Ifs { get; } = [];
    internal List<CheatBlock> Dos { get; } = [];
    internal bool Off { get; set; }
    internal string Notes { get; set; } = "";

    // The cheat file's cheat as a full cheat, to see whether this one still is the same.
    internal string GameCanonical { get; init; } = "";

    internal bool Added => Index < 0;

    internal bool IsChanged => Added || Off || Canonical().ToJsonString() != GameCanonical;

    internal CheatRule Copy() {
        var copy = new CheatRule { When = When.Copy(), Notes = Notes, Off = Off };
        copy.Ifs.AddRange(Ifs.Select(b => b.Copy()));
        copy.Dos.AddRange(Dos.Select(b => b.Copy()));

        return copy;
    }

    /// <summary>As the server's BossOverrides.Fingerprint: When|Cast|Line of the cheat file's cheat, lower case.</summary>
    internal string Match => string.Join('|', new[] { "When", "Cast", "Line" }
        .Select(k => BossChanges.FindIn(Game, k) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")).ToLowerInvariant();

    /// <summary>
    /// The whole cheat in the cheat file's format, every field written, so it can be laid over the file's cheat as it
    /// is: the old one-spell fields are emptied and everything it does goes in Actions.
    /// </summary>
    internal JsonObject Canonical() {
        var json = new JsonObject {
            ["When"] = When.Spec == "W.Other" ? When.Text("When") : CheatSpecs.WhenOf(When.Spec),
            ["Round"] = 0, ["Every"] = 0, ["Health"] = 0, ["Percent"] = 0, ["School"] = "", ["Kind"] = "", ["Category"] = "",
            ["Spells"] = new JsonArray(), ["Pips"] = 0, ["PipsOf"] = "", ["OnBoss"] = false, ["SingleTarget"] = false, ["NoAllies"] = false,
            ["CasterSchool"] = "", ["Phase"] = 0, ["Chance"] = 100, ["Once"] = false, ["MaxTimes"] = 0, ["Cooldown"] = 0,
            ["HealthAbove"] = 0, ["HealthUnder"] = 0, ["Random"] = false, ["Target"] = "", ["Cast"] = "", ["Casts"] = new JsonArray(),
            ["Line"] = "", ["Silent"] = false, ["SetHealth"] = 0, ["SetPhase"] = 0, ["Defeat"] = false, ["Spawn"] = 0,
            ["AnyVersion"] = false, ["CostMin"] = 0, ["CostMax"] = 0, ["PlayersMin"] = 0, ["PlayersMax"] = 0, ["FromRound"] = 0,
            ["ToRound"] = 0, ["HasAllies"] = false, ["Fizzle"] = "", ["Creature"] = 0, ["NotCreature"] = false, ["Minions"] = false,
            ["DispelThen"] = "", ["DispelRetries"] = 0, ["RetryUntilCast"] = false,
            ["Passive"] = CheatSpecs.PassiveOf(When.Spec), ["Circle"] = 0,
            ["Brings"] = new JsonArray(), ["BringsRandom"] = new JsonArray(), ["BringsCount"] = 0,
            ["HasWho"] = "", ["HasKind"] = "", ["HasSpell"] = "", ["HasNot"] = false, ["AlliesUp"] = new JsonArray(), ["AlliesDown"] = new JsonArray(),
            ["AlliesMin"] = 0, ["AlliesMax"] = 0, ["Mark"] = "", ["MarkWho"] = "", ["NotMarked"] = false, ["MultiTarget"] = false,
        };
        void Put(string key, JsonNode? value) => json[key] = value?.DeepClone();

        foreach (var key in new[] { "Round", "Every", "Health", "Percent", "School", "Category", "Pips", "PipsOf", "Minions", "Circle", "Brings", "BringsRandom", "BringsCount" }) {
            if (BossChanges.FindIn(When.Values, key) is { } value) {
                Put(key, value);
            }
        }

        // A When that does not use health keeps none, so a cheat file's leftover does not show as a change.
        if (When.Spec is not ("W.HealthBelow" or "W.PlayerHealthBelow" or "W.MyHealthBelow" or "W.AllyHealthBelow")) {
            json["Health"] = 0;
            json["Percent"] = 0;
        }

        foreach (var block in Ifs.Where(b => b.On)) {
            switch (block.Spec) {
                case "I.Once": json["Once"] = true; break;
                case "I.NoAllies": json["NoAllies"] = true; break;
                case "I.OnBoss": json["OnBoss"] = true; break;
                case "I.SingleTarget": json["SingleTarget"] = true; break;
                case "I.HasAllies": json["HasAllies"] = true; break;
                case "I.MultiTarget": json["MultiTarget"] = true; break;
                default:
                    foreach (var field in block.Kind.Fields.Where(f => f.Type != CheatFieldType.MatchCount)) {
                        Put(field.Key, BossChanges.FindIn(block.Values, field.Key));
                    }

                    break;
            }
        }

        var actions = new JsonArray();
        foreach (var block in Dos) {
            if (block.Spec == "D.RandomOne") {
                json["Random"] = block.On;

                continue;
            }

            var action = new JsonObject { ["Do"] = CheatSpecs.DoOf(block.Spec) };
            foreach (var (key, value) in block.Values) {
                action[key] = value?.DeepClone();
            }

            // Dispel-proof is gone (Niduus, 2026-10-23): a cast can be dispelled, and the "if dispelled" block retries it.
            if (block.Spec == "D.Cast") {
                action["DispelImmune"] = false;
            }

            if (!block.On) {
                action["Off"] = true;
            }

            actions.Add(action);
        }

        json["Actions"] = actions;
        json["Notes"] = Notes;

        return json;
    }

    /// <summary>A cheat of a cheat file (or a saved one), as blocks.</summary>
    internal static CheatRule FromCheat(JsonObject cheat, int index = -1, JsonObject? game = null, string gameCanonical = "", bool player = false) {
        string Text(string key) => BossChanges.FindIn(cheat, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        int Number(string key) => BossChanges.FindIn(cheat, key) is JsonValue v
                                  && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (int) n : key == "Chance" ? 100 : 0;
        bool Flag(string key) => BossChanges.FindIn(cheat, key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        JsonNode? Node(string key) => BossChanges.FindIn(cheat, key)?.DeepClone();

        var when = Text("When");
        var whenSpec = when.Equals("Passive", StringComparison.OrdinalIgnoreCase)
            ? CheatSpecs.All.FirstOrDefault(s => s.Kind == BlockKind.Passive && CheatSpecs.PassiveOf(s.Id).Equals(Text("Passive"), StringComparison.OrdinalIgnoreCase))
            : CheatSpecs.All.FirstOrDefault(s => s.Kind == BlockKind.When && s.For(player)
                                                 && CheatSpecs.WhenOf(s.Id).Equals(when, StringComparison.OrdinalIgnoreCase));
        var off = when.Equals("Off", StringComparison.OrdinalIgnoreCase) || Flag("Off");
        var whenBlock = whenSpec is null
            ? CheatBlock.New(CheatSpecs.Get("W.Other"), new JsonObject { ["When"] = when })
            : CheatBlock.New(whenSpec);
        foreach (var key in new[] { "Round", "Every", "Health", "Percent", "School", "Category", "Pips", "PipsOf", "Minions", "Circle", "Brings", "BringsRandom", "BringsCount" }) {
            if (whenBlock.Values.Any(p => p.Key == key) && Node(key) is { } value) {
                whenBlock.Set(key, value);
            }
        }

        if (whenBlock.Spec is "W.HealthBelow" or "W.PlayerHealthBelow" or "W.MyHealthBelow" or "W.AllyHealthBelow" && Number("Health") > 0) {
            whenBlock.Set("Percent", 0);
        }

        var rule = new CheatRule { Index = index, Game = game ?? [], GameCanonical = gameCanonical, When = whenBlock, Off = off, Notes = Text("Notes") };
        void If(string spec, JsonObject? values = null) => rule.Ifs.Add(CheatBlock.New(CheatSpecs.Get(spec), values));

        if (Number("Phase") > 0) {
            If("I.Phase", new JsonObject { ["Phase"] = Number("Phase") });
        }

        if (Number("Chance") < 100) {
            If("I.Chance", new JsonObject { ["Chance"] = Number("Chance") });
        }

        if (Flag("Once")) {
            If("I.Once");
        }

        foreach (var (key, spec) in new[] { ("MaxTimes", "I.MaxTimes"), ("Cooldown", "I.Cooldown"), ("HealthAbove", "I.HealthAbove"), ("HealthUnder", "I.HealthUnder") }) {
            if (Number(key) > 0) {
                If(spec, new JsonObject { [key] = Number(key) });
            }
        }

        foreach (var (key, spec) in new[] { ("NoAllies", "I.NoAllies"), ("OnBoss", "I.OnBoss"), ("SingleTarget", "I.SingleTarget") }) {
            if (Flag(key)) {
                If(spec);
            }
        }

        if (Text("CasterSchool").Length > 0) {
            If("I.CasterSchool", new JsonObject { ["CasterSchool"] = Text("CasterSchool") });
        }

        if (Text("Fizzle").Length > 0) {
            If("I.Fizzle", new JsonObject { ["Fizzle"] = Text("Fizzle") });
        }

        if (Text("DispelThen").Length > 0) {
            If("I.Dispelled", new JsonObject {
                ["DispelThen"] = Text("DispelThen"), ["DispelRetries"] = Number("DispelRetries"), ["RetryUntilCast"] = Flag("RetryUntilCast"),
            });
        }

        if (Text("Kind").Length > 0) {
            If("I.CardType", new JsonObject { ["Kind"] = Text("Kind") });
        }

        if (Node("Spells") is JsonArray { Count: > 0 } spells) {
            // An old file's names are exact names; AnyVersion makes each stand for every version of that spell.
            If("I.Spells", new JsonObject { ["Spells"] = spells, ["AnyVersion"] = Flag("AnyVersion") });
        }

        if (Number("CostMin") > 0 || Number("CostMax") > 0) {
            If("I.Cost", new JsonObject { ["CostMin"] = Number("CostMin"), ["CostMax"] = Number("CostMax") });
        }

        if (Number("PlayersMin") > 0 || Number("PlayersMax") > 0) {
            If("I.Players", new JsonObject { ["PlayersMin"] = Number("PlayersMin"), ["PlayersMax"] = Number("PlayersMax") });
        }

        if (Number("FromRound") > 0 || Number("ToRound") > 0) {
            If("I.Rounds", new JsonObject { ["FromRound"] = Number("FromRound"), ["ToRound"] = Number("ToRound") });
        }

        if (Flag("HasAllies")) {
            If("I.HasAllies");
        }

        if (Number("Creature") > 0) {
            If("I.Creature", new JsonObject { ["Creature"] = Number("Creature"), ["NotCreature"] = Flag("NotCreature") });
        }

        // The gauntlets' Ifs (Niduus, 2026-10-24).
        if (Text("HasKind").Length > 0 || Text("HasSpell").Length > 0 || Flag("HasNot")) {
            If("I.Has", new JsonObject {
                ["HasWho"] = Text("HasWho").Length > 0 ? Text("HasWho") : "Caster", ["HasKind"] = Text("HasKind"), ["HasSpell"] = Text("HasSpell"),
                ["HasNot"] = Flag("HasNot"),
            });
        }

        if (Number("AlliesMin") != 0 || Number("AlliesMax") != 0) {
            If("I.Allies", new JsonObject { ["AlliesMin"] = Number("AlliesMin"), ["AlliesMax"] = Number("AlliesMax") });
        }

        if (Node("AlliesUp") is JsonArray { Count: > 0 } || Node("AlliesDown") is JsonArray { Count: > 0 }) {
            If("I.AlliesUp", new JsonObject { ["AlliesUp"] = Node("AlliesUp") ?? new JsonArray(), ["AlliesDown"] = Node("AlliesDown") ?? new JsonArray() });
        }

        if (Text("Mark").Length > 0) {
            If("I.Marked", new JsonObject { ["MarkWho"] = Text("MarkWho").Length > 0 ? Text("MarkWho") : "Caster", ["Mark"] = Text("Mark"), ["NotMarked"] = Flag("NotMarked") });
        }

        if (Flag("MultiTarget")) {
            If("I.MultiTarget");
        }

        // A file's School and Category on a When that has no box for them stay as If blocks would not: keep them on it.
        void Do(string spec, JsonObject? values = null, bool on = true) {
            var block = CheatBlock.New(CheatSpecs.Get(spec), values);
            block.On = on;
            rule.Dos.Add(block);
        }

        var target = Text("Target");
        var silent = Flag("Silent");
        if (Number("Spawn") > 0) {
            Do("D.Form", new JsonObject { ["Creature"] = Number("Spawn"), ["ReplaceBoss"] = Flag("Defeat") });
        }

        if (Number("SetHealth") > 0) {
            Do("D.SetHealth", new JsonObject { ["Amount"] = Number("SetHealth") });
        }

        if (Number("SetPhase") > 0) {
            Do("D.SetPhase", new JsonObject { ["Phase"] = Number("SetPhase") });
        }

        if (Text("Line").Length > 0) {
            Do("D.Say", new JsonObject { ["Line"] = Text("Line") });
        }

        var casts = (Node("Casts") as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (casts.Count == 0 && Text("Cast").Length > 0) {
            casts.Add(new JsonObject { ["Spell"] = Text("Cast") });
        }

        // A cheat whose spells were dispel-proof (the old default) now retries a dispelled spell until it is cast.
        var dispelProof = false;
        foreach (var cast in casts) {
            var values = new JsonObject {
                ["Spell"] = BossChanges.FindIn(cast, "Spell")?.DeepClone() ?? "",
                ["SpellID"] = BossChanges.FindIn(cast, "SpellID")?.DeepClone() ?? 0,
                ["Target"] = BossChanges.FindIn(cast, "Target") is JsonValue t && t.ToString().Length > 0 ? t.ToString() : target,
                ["Silent"] = silent || (BossChanges.FindIn(cast, "Silent") is JsonValue s && s.TryGetValue<bool>(out var b) && b),
            };
            dispelProof |= BossChanges.FindIn(cast, "DispelImmune") is not JsonValue d || !d.TryGetValue<bool>(out var immune) || immune;
            Do("D.Cast", values);
        }

        if (Flag("Defeat") && Number("Spawn") == 0) {
            Do("D.Defeat");
        }

        foreach (var action in (Node("Actions") as JsonArray ?? []).OfType<JsonObject>()) {
            var what = BossChanges.FindIn(action, "Do") is JsonValue w ? w.ToString() : "";
            var values = new JsonObject();
            foreach (var (key, value) in action) {
                if (!key.Equals("Do", StringComparison.OrdinalIgnoreCase) && !key.Equals("Off", StringComparison.OrdinalIgnoreCase)) {
                    values[key] = value?.DeepClone();
                }
            }

            var actionOff = BossChanges.FindIn(action, "Off") is JsonValue o && o.TryGetValue<bool>(out var isOff) && isOff;
            var hasLine = BossChanges.FindIn(action, "Line") is JsonValue l && l.ToString().Length > 0;
            // The player cheats' old "me" and "all" targets are the same as "itself" and "every player".
            if (BossChanges.FindIn(values, "Target") is JsonValue aim && aim.TryGetValue<string>(out var code)
                && code.ToLowerInvariant() is "me" or "all") {
                values.Remove(values.First(p => p.Key.Equals("Target", StringComparison.OrdinalIgnoreCase)).Key);
                values["Target"] = code.Equals("me", StringComparison.OrdinalIgnoreCase) ? "Self" : "AllEnemies";
            }

            var spec = what.Equals("Say", StringComparison.OrdinalIgnoreCase)
                ? hasLine ? "D.Say" : "D.Chat"
                : CheatSpecs.All.FirstOrDefault(s => s.Kind == BlockKind.Do && s.For(player)
                                                     && CheatSpecs.DoOf(s.Id).Equals(what, StringComparison.OrdinalIgnoreCase))?.Id;
            if (spec == "D.Cast") {
                dispelProof |= values["DispelImmune"] is not JsonValue d || !d.TryGetValue<bool>(out var immune) || immune;
                values.Remove("DispelImmune");
            }

            if (spec is null) {
                // An action this app does not know is kept as it is, under its own name.
                values["Do"] = what;
                spec = "D.Other";
            }

            Do(spec, values, !actionOff);
        }

        if (Flag("Random")) {
            Do("D.RandomOne");
        }

        if (dispelProof && !player && Text("DispelThen").Length == 0) {
            If("I.Dispelled", new JsonObject { ["DispelThen"] = "Retry", ["DispelRetries"] = 1, ["RetryUntilCast"] = true });
        }

        return rule;
    }

}

/// <summary>
/// The cheats of a boss as the app shows them: its cheat file (spiraldb-extra/BossCheats/&lt;ID&gt;.json, never
/// changed) with the changes saved in its boss file (Cheats) laid over it, as the server's BossOverrides.CheatsOf does.
/// </summary>
internal sealed class BossCheatList {

    internal List<CheatRule> Cheats { get; } = [];

    /// <summary>Whether the boss has a cheat file.</summary>
    internal bool HasFile { get; private set; }

    /// <summary>How many saved changes found no cheat in the file (it lost that cheat since).</summary>
    internal int Lost { get; private set; }

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static string FileOf(string extraFolder, string id) => Path.Combine(extraFolder, "BossCheats", $"{id}.json");

    internal static BossCheatList Load(string extraFolder, string id, JsonObject? saved) {
        var list = new BossCheatList();
        var shipped = new List<JsonObject>();
        var file = FileOf(extraFolder, id);
        if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is JsonObject json) {
            list.HasFile = true;
            shipped.AddRange((BossChanges.FindIn(json, "Cheats") as JsonArray ?? []).OfType<JsonObject>());
        }

        var rules = shipped.Select((cheat, i) => {
            var canonical = CheatRule.FromCheat(cheat).Canonical().ToJsonString();

            return CheatRule.FromCheat(cheat, i, (JsonObject) cheat.DeepClone(), canonical);
        }).ToList();
        list.Cheats.AddRange(rules);
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
            var rule = index >= 0 && index < rules.Count && (match is not { Length: > 0 } || rules[index].Match == match)
                ? rules[index]
                : rules.FirstOrDefault(c => !taken.Contains(c.Index) && c.Match == match);
            if (rule is null || !taken.Add(rule.Index)) {
                list.Lost++;

                continue;
            }

            // As the server does: the change's fields over the file's cheat.
            var merged = (JsonObject) rule.Game.DeepClone();
            foreach (var (field, value) in change) {
                if (field.Equals("Match", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var existing = merged.Select(p => p.Key).FirstOrDefault(k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) {
                    merged.Remove(existing);
                }

                merged[field] = value?.DeepClone();
            }

            list.Cheats[list.Cheats.IndexOf(rule)] = CheatRule.FromCheat(merged, rule.Index, rule.Game, rule.GameCanonical);
        }

        foreach (var added in (BossChanges.FindIn(saved, "Added") as JsonArray ?? []).OfType<JsonObject>()) {
            list.Cheats.Add(CheatRule.FromCheat(added));
        }

        return list;
    }

    /// <summary>The Cheats section of the boss file, or null when no cheat is changed.</summary>
    internal JsonObject? ToJson() {
        var changed = new JsonObject();
        foreach (var rule in Cheats.Where(c => !c.Added && c.IsChanged)) {
            var json = new JsonObject { ["Match"] = rule.Match };
            if (rule.Off) {
                json["Off"] = true;
            }

            foreach (var (key, value) in rule.Canonical()) {
                json[key] = value?.DeepClone();
            }

            changed[rule.Index.ToString(CultureInfo.InvariantCulture)] = json;
        }

        var added = new JsonArray();
        foreach (var rule in Cheats.Where(c => c.Added)) {
            var json = rule.Canonical();
            if (rule.Off) {
                json["Off"] = true;
            }

            added.Add(json);
        }

        if (changed.Count == 0 && added.Count == 0) {
            return null;
        }

        return new JsonObject { ["Changed"] = changed, ["Added"] = added };
    }

}

/// <summary>
/// Player cheats (Niduus, 2026-10-22: "add PLAYER cheats for the local player to do"): spiraldb-extra/PlayerCheats.json,
/// which the server reads (PlayerCheats.cs) for the wizard played on the server's own PC only. Every cheat in it is the
/// player's own, so each is "added" (Index -1).
/// </summary>
internal static class PlayerCheatFile {

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static string FileOf(string extraFolder) => Path.Combine(extraFolder, "PlayerCheats.json");

    /// <summary>The cheats and the master switch (on when there is no file yet).</summary>
    internal static (BossCheatList List, bool Enabled) Load(string extraFolder) {
        var list = new BossCheatList();
        var file = FileOf(extraFolder);
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is not JsonObject json) {
            return (list, true);
        }

        foreach (var cheat in (BossChanges.FindIn(json, "Cheats") as JsonArray ?? []).OfType<JsonObject>()) {
            list.Cheats.Add(CheatRule.FromCheat(cheat, player: true));
        }

        var enabled = BossChanges.FindIn(json, "Enabled") is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;

        return (list, enabled);
    }

    internal static void Save(string extraFolder, BossCheatList list, bool enabled) {
        var cheats = new JsonArray();
        foreach (var rule in list.Cheats) {
            var json = rule.Canonical();
            if (rule.Off) {
                json["Off"] = true;
            }

            cheats.Add(json);
        }

        Directory.CreateDirectory(extraFolder);
        var text = new JsonObject { ["Enabled"] = enabled, ["Cheats"] = cheats }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var file = FileOf(extraFolder);
        File.WriteAllText(file + ".tmp", text);
        File.Move(file + ".tmp", file, overwrite: true);
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

    /// <summary>Game lines holding all the words, for the line picker.</summary>
    internal List<(int Number, string Text)> Search(string words, int max) {
        var parts = words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 0 ? []
            : [.. _phrases.Where(p => parts.All(w => p.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(max).Select(p => (p.Number, p.Text))];
    }

}
