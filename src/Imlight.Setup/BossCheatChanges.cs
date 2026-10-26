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
    // An aura's stats, picked one by one ("resist 20, accuracy 15"), shown while its kind is Aura.
    AuraStats,
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

    /// <summary>
    /// A block read from a cheat file: the boxes the file leaves out take the value the server reads for them ("",
    /// 0, false, none), not the box's first value for a new block (Niduus, 2026-10-26: "sometimes saving cheats modifies
    /// the cheat": a Heal or Pips with no Target was saved "on itself", an Immune with no School as Fire).
    /// </summary>
    internal static CheatBlock FromFile(CheatSpec spec, JsonObject values) {
        var block = New(spec, values);
        foreach (var field in spec.Fields) {
            foreach (var (key, first) in Defaults(field)) {
                if (!values.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) {
                    block.Set(key, Unset(first));
                }
            }
        }

        return block;
    }

    // What the server reads for a value a file leaves out, of the same kind as the box's own.
    internal static JsonNode? Unset(JsonNode? like) => like switch {
        JsonArray => new JsonArray(),
        JsonValue v when v.TryGetValue<bool>(out _) => JsonValue.Create(false),
        JsonValue v when v.TryGetValue<string>(out _) => JsonValue.Create(""),
        JsonValue => JsonValue.Create(0),
        _ => null,
    };

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
            ["NotOnCaster"] = false, ["NotCategory"] = "", ["HasCount"] = 0, ["PipsKind"] = "", ["CreatureWho"] = "",
            ["RemovedKind"] = "", ["RemovedSpell"] = "", ["RemovedFrom"] = "", ["SpellDoes"] = "", ["NotSpellDoes"] = false,
            ["Enchant"] = "", ["NotEnchant"] = false, ["Global"] = "", ["GlobalSchool"] = "", ["GlobalSpell"] = "", ["SchoolPips"] = 0,
            ["SchoolPipsSchool"] = "", ["SchoolPipsWho"] = "", ["SchoolPipsMost"] = false, ["PipsMin"] = 0, ["PipsMax"] = 0, ["PipsWho"] = "",
            ["Circles"] = "", ["CirclesWho"] = "", ["Conditions"] = new JsonArray(),
        };
        void Put(string key, JsonNode? value) => json[key] = value?.DeepClone();

        foreach (var key in new[] { "Round", "Every", "Health", "Percent", "School", "Category", "Pips", "PipsOf", "PipsKind", "Minions", "Circle", "Brings", "BringsRandom", "BringsCount",
                     "RemovedKind", "RemovedSpell", "RemovedFrom" }) {
            if (BossChanges.FindIn(When.Values, key) is { } value) {
                Put(key, value);
            }
        }

        // A When that does not use health keeps none, so a cheat file's leftover does not show as a change.
        if (When.Spec is not ("W.HealthBelow" or "W.PlayerHealthBelow" or "W.MyHealthBelow" or "W.AllyHealthBelow")) {
            json["Health"] = 0;
            json["Percent"] = 0;
        }

        // The conditions (Niduus, 2026-10-26): one entry each, with every value the block holds.
        var conditions = new JsonArray();
        foreach (var block in Ifs.Where(b => b.On && CheatSpecs.IsCondition(b.Spec))) {
            var entry = new JsonObject { ["If"] = CheatSpecs.ConditionOf(block) };
            // What the server reads for a value left out ("", 0, false) is left out, so the file stays short.
            foreach (var (key, value) in block.Values) {
                if (!key.Equals("If", StringComparison.OrdinalIgnoreCase)
                    && !(value is JsonValue && JsonNode.DeepEquals(value, CheatBlock.Unset(value)))) {
                    entry[key] = value?.DeepClone();
                }
            }

            conditions.Add(entry);
        }

        json["Conditions"] = conditions;

        foreach (var block in Ifs.Where(b => b.On && !CheatSpecs.IsCondition(b.Spec))) {
            switch (block.Spec) {
                case "I.Once": json["Once"] = true; break;
                case "I.NoAllies": json["NoAllies"] = true; break;
                case "I.OnBoss": json["OnBoss"] = true; break;
                case "I.SingleTarget": json["SingleTarget"] = true; break;
                case "I.HasAllies": json["HasAllies"] = true; break;
                case "I.MultiTarget": json["MultiTarget"] = true; break;
                case "I.NotOnCaster": json["NotOnCaster"] = true; break;
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
        foreach (var key in new[] { "Round", "Every", "Health", "Percent", "School", "Category", "Pips", "PipsOf", "PipsKind", "Minions", "Circle", "Brings", "BringsRandom", "BringsCount",
                     "RemovedKind", "RemovedSpell", "RemovedFrom" }) {
            var boxed = whenBlock.Values.Any(p => p.Key == key);
            if (Node(key) is { } value) {
                // A School or Category the When has no box for (on "boss defeated", "a player puts a charm") is kept on
                // it as it is, so a save writes it back; the server reads it.
                if (boxed || key is "School" or "Category" && value is JsonValue v && v.ToString().Length > 0) {
                    whenBlock.Set(key, value);
                }
            }
            else if (boxed && !(key is "Health" or "Percent")) {
                // Left out of the file: what the server reads, not the box's first value (FromRound's Every 1).
                whenBlock.Set(key, CheatBlock.Unset(whenBlock.Values[key]));
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

        foreach (var (key, spec) in new[] { ("NoAllies", "I.NoAllies"), ("OnBoss", "I.OnBoss"), ("SingleTarget", "I.SingleTarget"), ("NotOnCaster", "I.NotOnCaster") }) {
            if (Flag(key)) {
                If(spec);
            }
        }

        if (Text("CasterSchool").Length > 0) {
            If("I.CasterSchool", new JsonObject { ["CasterSchool"] = Text("CasterSchool") });
        }

        // Pips on a When with no box for them (a round's start too) are the "pips left" If, as on a round's end.
        if (Number("Pips") > 0 && !whenBlock.Values.Any(p => p.Key == "Pips")) {
            If("I.Pips", new JsonObject { ["PipsOf"] = Text("PipsOf"), ["Pips"] = Number("Pips"), ["PipsKind"] = Text("PipsKind") });
        }

        if (Text("NotCategory").Length > 0) {
            If("I.NotCategory", new JsonObject { ["NotCategory"] = Text("NotCategory") });
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

        if (Number("CostMin") > 0 || Number("CostMax") != 0) {
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
            If("I.Creature", new JsonObject { ["CreatureWho"] = Text("CreatureWho"), ["Creature"] = Number("Creature"), ["NotCreature"] = Flag("NotCreature") });
        }

        // The gauntlets' Ifs (Niduus, 2026-10-24).
        if (Text("HasKind").Length > 0 || Text("HasSpell").Length > 0 || Flag("HasNot")) {
            If("I.Has", new JsonObject {
                ["HasWho"] = Text("HasWho").Length > 0 ? Text("HasWho") : "Caster", ["HasCount"] = Number("HasCount"), ["HasKind"] = Text("HasKind"),
                ["HasSpell"] = Text("HasSpell"), ["HasNot"] = Flag("HasNot"),
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

        // Closer to the real bosses (Niduus, 2026-10-25).
        if (Text("SpellDoes").Length > 0) {
            If("I.SpellDoes", new JsonObject { ["SpellDoes"] = Text("SpellDoes"), ["NotSpellDoes"] = Flag("NotSpellDoes") });
        }

        if (Text("Enchant").Length > 0) {
            If("I.Enchanted", new JsonObject { ["Enchant"] = Text("Enchant"), ["NotEnchant"] = Flag("NotEnchant") });
        }

        if (Text("Global").Length > 0) {
            If("I.Global", new JsonObject { ["Global"] = Text("Global"), ["GlobalSchool"] = Text("GlobalSchool"), ["GlobalSpell"] = Text("GlobalSpell") });
        }

        if (Number("SchoolPips") > 0) {
            If("I.SchoolPips", new JsonObject {
                ["SchoolPipsWho"] = Text("SchoolPipsWho"), ["SchoolPips"] = Number("SchoolPips"), ["SchoolPipsSchool"] = Text("SchoolPipsSchool"),
                ["SchoolPipsMost"] = Flag("SchoolPipsMost"),
            });
        }

        if (Number("PipsMin") > 0 || Number("PipsMax") != 0) {
            If("I.PipsNow", new JsonObject { ["PipsWho"] = Text("PipsWho"), ["PipsMin"] = Number("PipsMin"), ["PipsMax"] = Number("PipsMax") });
        }

        if (Text("Circles").Length > 0) {
            If("I.Circle", new JsonObject { ["CirclesWho"] = Text("CirclesWho"), ["Circles"] = Text("Circles") });
        }

        // The conditions, one block each; one this app does not know is kept as it is.
        foreach (var entry in (Node("Conditions") as JsonArray ?? []).OfType<JsonObject>()) {
            var kind = BossChanges.FindIn(entry, "If") is JsonValue k && k.TryGetValue<string>(out var w) ? w : "";
            var values = new JsonObject();
            foreach (var (key, value) in entry) {
                if (!key.Equals("If", StringComparison.OrdinalIgnoreCase)) {
                    values[key] = value?.DeepClone();
                }
            }

            var spec = CheatSpecs.ConditionSpec(kind);
            if (spec.Id == "C.Other") {
                values["If"] = kind;
            }

            rule.Ifs.Add(CheatBlock.FromFile(spec, values));
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

            // An aura made with "add an effect of your own size" is now the aura block (Niduus, 2026-10-26: "Make them their
            // own blocks instead"); its amount was its damage.
            if (spec == "D.AddCharm" && BossChanges.FindIn(values, "Kind") is JsonValue kind && kind.ToString().Equals("Aura", StringComparison.OrdinalIgnoreCase)) {
                spec = "D.Aura";
                var stats = BossChanges.FindIn(values, "AuraStats") is JsonValue st ? st.ToString() : "";
                var amount = BossChanges.FindIn(values, "Amount") is JsonValue am && am.TryGetValue<int>(out var n) ? n : 0;
                var school = BossChanges.FindIn(values, "School") is JsonValue sc ? sc.ToString().Trim().ToLowerInvariant() : "";
                if (amount != 0 && !System.Text.RegularExpressions.Regex.IsMatch(stats, @"(^|,|\s)damage\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
                    var damage = $"{(school.Length > 0 ? school + " " : "")}damage {amount}";
                    stats = stats.Trim().Length > 0 ? $"{damage}, {stats.Trim()}" : damage;
                }

                foreach (var key in new[] { "Kind", "Amount", "School", "Look", "AuraStats" }) {
                    if (values.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key is { } k) {
                        values.Remove(k);
                    }
                }

                values["AuraStats"] = stats;
            }

            if (spec is null) {
                // An action this app does not know is kept as it is, under its own name.
                values["Do"] = what;
                spec = "D.Other";
            }

            var block = CheatBlock.FromFile(CheatSpecs.Get(spec), values);
            block.On = !actionOff;
            rule.Dos.Add(block);
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
        // An empty search lists the lines too (Niduus, 2026-10-25: "have them all generate search results if left empty"),
        // and words match as every search box reads them (SearchText).
        return [.. _phrases.Where(p => SearchText.Matches(p.Text, words) || p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == words.Trim())
            .Take(max).Select(p => (p.Number, p.Text))];
    }

}

/// <summary>An example cheat file (spiraldb-extra/CheatExamples): its name, what it shows and its cheats.</summary>
internal sealed record CheatExample(string File, string Name, string Notes, List<JsonObject> Cheats);

/// <summary>
/// The examples to copy (Niduus, 2026-10-26: "I really want people to look at the system and be like 'WOW! You can legit
/// do anything'"): cheat files in spiraldb-extra/CheatExamples, on no creature (the server never reads that folder), that
/// the cheat editor copies into a boss's or the player's cheats.
/// </summary>
internal static class CheatExamples {

    internal const string FOLDER = "CheatExamples";

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static List<CheatExample> Load(string extraFolder) {
        var folder = Path.Combine(extraFolder, FOLDER);
        if (!Directory.Exists(folder)) {
            return [];
        }

        var examples = new List<CheatExample>();
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            try {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) is not JsonObject json) {
                    continue;
                }

                string Text(string key) => BossChanges.FindIn(json, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                var cheats = (BossChanges.FindIn(json, "Cheats") as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (cheats.Count > 0) {
                    examples.Add(new CheatExample(Path.GetFileName(file), Text("Name") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file), Text("Notes"), cheats));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // A file that cannot be read is left out.
            }
        }

        return examples;
    }

    /// <summary>
    /// An example's cheats as new cheats at the end of a list that already has <paramref name="already"/>: the cheat
    /// numbers its blocks name (run cheat, turn a cheat off, a cheat went off) move along with them.
    /// </summary>
    internal static List<CheatRule> Copy(CheatExample example, int already, bool player) {
        var rules = new List<CheatRule>();
        foreach (var cheat in example.Cheats) {
            var copy = (JsonObject) cheat.DeepClone();
            var numbered = (BossChanges.FindIn(copy, "Actions") as JsonArray ?? []).OfType<JsonObject>()
                .Where(a => BossChanges.FindIn(a, "Do") is JsonValue d && d.ToString().ToLowerInvariant() is "runcheat" or "cheatonoff")
                .Concat((BossChanges.FindIn(copy, "Conditions") as JsonArray ?? []).OfType<JsonObject>()
                    .Where(c => BossChanges.FindIn(c, "If") is JsonValue i && i.ToString().Equals("Fired", StringComparison.OrdinalIgnoreCase)));
            foreach (var block in numbered) {
                var key = block.Select(p => p.Key).FirstOrDefault(k => k.Equals("Cheat", StringComparison.OrdinalIgnoreCase));
                if (key is not null && block[key] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0) {
                    block[key] = n + already;
                }
            }

            rules.Add(CheatRule.FromCheat(copy, player: player));
        }

        return rules;
    }

}
