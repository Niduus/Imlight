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
using System.Linq;
using System.Text.Json.Nodes;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// Every block a boss cheat can be built from (Niduus, 2026-10-22: "cheats can have their conditions and such able to
/// be modified and created"), with the words it shows and what it means. The server reads them as the cheat fields of
/// spiraldb-extra/BossCheats (see the top of the server's BossCheats.cs): When blocks set When and its numbers, If
/// blocks set a condition field each, and Do blocks are the cheat's Actions.
/// </summary>
internal static class CheatSpecs {

    internal static readonly (string Value, string Label)[] TARGETS = [
        ("", "where the spell aims"),
        ("Self", "itself"),
        ("Caster", "the attacker"),
        ("Receiver", "whoever that spell was aimed at"),
        ("Enemy", "a random player"),
        ("FirstEnemy", "the first player"),
        ("WeakestEnemy", "the weakest player"),
        ("AllEnemies", "every player"),
        ("Ally", "a random ally"),
        ("AllAllies", "its whole side"),
    ];

    internal static readonly (string Value, string Label)[] SCHOOLS = [
        ("", "any school"), ("Fire", "Fire"), ("Ice", "Ice"), ("Storm", "Storm"), ("Myth", "Myth"), ("Life", "Life"),
        ("Death", "Death"), ("Balance", "Balance"), ("Star", "Star"), ("Sun", "Sun"), ("Moon", "Moon"), ("Shadow", "Shadow"),
    ];

    internal static readonly (string Value, string Label)[] CATEGORIES = [
        ("", "any"), ("Damage", "damage"), ("Heal", "heal"), ("Shield", "shield"), ("Trap", "trap"), ("Blade", "blade"),
        ("Weakness", "weakness"), ("Aura", "aura"), ("Global", "global (bubble)"), ("Minion", "minion"), ("Stun", "stun"),
        ("Steal", "steal"), ("Dispel", "dispel"), ("Polymorph", "polymorph"),
    ];

    private static readonly (string, string)[] s_charmKinds = [("All", "of any"), ("Good", "good"), ("Bad", "bad")];
    private static readonly (string, string)[] s_pipKinds = [("Normal", "normal"), ("Power", "power"), ("Shadow", "shadow")];
    private static readonly (string, string)[] s_pipsOf = [("", "it"), ("Enemy", "a player")];

    private static CheatField F(string key, CheatFieldType type, JsonNode? first = null, (string, string)[]? options = null, int max = 100_000_000)
        => new(key, type, first, options, Max: max);

    internal static string LabelOf((string Value, string Label)[] options, string value)
        => options.FirstOrDefault(o => o.Value.Equals(value ?? "", StringComparison.OrdinalIgnoreCase)).Label ?? value ?? "";

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Rounds(CheatBlock b) {
        var round = b.Number("Round");
        var every = b.Number("Every");

        return round > 0 && every > 0 ? $"round {round}, then every {every} rounds"
            : round > 0 ? $"round {round}" : every > 1 ? $"every {every} rounds" : "every round";
    }

    private static string Target(CheatBlock b, string fallback = "") {
        var target = b.Text("Target");

        return target.Length == 0 ? fallback : " on " + LabelOf(TARGETS, target);
    }

    private static string SpellName(CheatBlock b) => b.Text("Spell") is { Length: > 0 } s ? s : b.Id("SpellID") is var id and > 0 ? $"spell {id}" : "(no spell picked)";

    /// <summary>The names of creatures and customs, for the words of a block (set by the app once it has them).</summary>
    internal static Func<uint, string?>? CreatureNames { get; set; }

    internal static Func<int, string?>? CustomNames { get; set; }

    private static string Creature(CheatBlock b) => b.Number("Custom") > 0
        ? CustomNames?.Invoke(b.Number("Custom")) is { } custom ? custom : $"custom {b.Number("Custom")}"
        : b.Id("Creature") is var id and > 0 ? CreatureNames?.Invoke(id) ?? $"creature {id}" : "(no creature picked)";

    private static string Spell(CheatBlock b, string what) {
        var school = b.Text("School");
        var category = LabelOf(CATEGORIES, b.Text("Category"));
        var words = string.Join(' ', new[] { school, b.Text("Category").Length > 0 ? category : "" }.Where(w => w.Length > 0));

        return words.Length == 0 ? what : $"a {words} spell";
    }

    internal static readonly CheatSpec[] All = [
        // When...
        new("W.BattleStart", BlockKind.When, "flag", "the battle starts", "Its first round. A boss brought in later counts from its own first round.",
            "When the battle starts", [], _ => "When the battle starts"),
        new("W.RoundStart", BlockKind.When, "clock", "a round starts", "The start of a round, before anyone moves. Pick the round, every few rounds, or both.",
            "At the start of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the start of {Rounds(b)}"),
        new("W.RoundEnd", BlockKind.When, "clockend", "a round ends", "The end of a round, after everyone has moved.",
            "At the end of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the end of {Rounds(b)}"),
        new("W.HealthBelow", BlockKind.When, "heart", "its health drops", "The first time its health falls under the mark (once each phase). Works even after it is defeated, for a second life.",
            "When its health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When its health drops below {N(b.Number("Health"))}" : $"When its health drops below {b.Number("Percent")}%"),
        new("W.PlayerCasts", BlockKind.When, "sparkle", "a player casts", "A player's spell lands: pick what kind of card (heal, shield, blade, trap, aura, global, minion...) and its school.",
            "When a player casts a {Category} spell of {School}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School)],
            b => $"When a player casts {Spell(b, "a spell")}"),
        new("W.BossHit", BlockKind.When, "hit", "it is hit", "A spell from the other side hurts it. Pick the school and the kind of card, or any.",
            "When it is hit by a {School} {Category} spell", [F("School", CheatFieldType.School), F("Category", CheatFieldType.Category)],
            b => b.Text("Category").Length == 0 ? $"When hit by {(b.Text("School").Length > 0 ? b.Text("School") : "anything")}" : $"When hit by {Spell(b, "")}"),
        new("W.PlayerPasses", BlockKind.When, "hourglass", "a player passes", "A player passes their turn.",
            "When a player passes", [], _ => "When a player passes"),
        new("W.MinionDefeated", BlockKind.When, "skull", "a minion falls", "A creature on its side (a minion or a helper) is defeated.",
            "When a creature on its side is defeated", [], _ => "When one of its minions is defeated"),
        new("W.Pips", BlockKind.When, "pips", "pips reach", "At the start of a round, when it (or any player) has at least this many pips. A power pip counts as one.",
            "When {PipsOf} has at least {Pips} pips", [F("PipsOf", CheatFieldType.Choice, "", s_pipsOf), F("Pips", CheatFieldType.Number, 5, max: 14)],
            b => $"When {LabelOf(s_pipsOf, b.Text("PipsOf"))} has {b.Number("Pips")} or more pips"),
        new("W.Other", BlockKind.When, "bolt", "something else", "A When this app does not know, kept as the file has it.",
            "When {When}", [F("When", CheatFieldType.Words)], b => $"When {b.Text("When")}"),

        // If...
        new("I.Chance", BlockKind.If, "dice", "chance", "It goes off only this often. 50 means half the time.",
            "If a {Chance} chance comes up", [F("Chance", CheatFieldType.Percent, 50, max: 100)], b => $"{b.Number("Chance")}% chance"),
        new("I.Once", BlockKind.If, "once", "only the first time", "Only the first time in a battle.",
            "Only the first time", [], _ => "only the first time"),
        new("I.MaxTimes", BlockKind.If, "list", "at most … times", "No more than this many times in one battle.",
            "At most {MaxTimes} times a battle", [F("MaxTimes", CheatFieldType.Number, 3, max: 999)], b => $"at most {b.Number("MaxTimes")} times"),
        new("I.Cooldown", BlockKind.If, "hourglass", "wait between", "After it goes off, it waits this many rounds before it can go off again.",
            "Then wait {Cooldown} rounds before again", [F("Cooldown", CheatFieldType.Number, 2, max: 99)], b => $"then waits {b.Number("Cooldown")} rounds"),
        new("I.Phase", BlockKind.If, "layers", "in phase", "Only in this phase of the fight. A boss starts in phase 1; a Do block moves it on.",
            "Only in phase {Phase}", [F("Phase", CheatFieldType.Number, 1, max: 99)], b => $"in phase {b.Number("Phase")}"),
        new("I.HealthAbove", BlockKind.If, "heart", "health above", "Only while its health is above this share of its most.",
            "Only while its health is above {HealthAbove}", [F("HealthAbove", CheatFieldType.Percent, 50, max: 100)], b => $"while above {b.Number("HealthAbove")}% health"),
        new("I.HealthUnder", BlockKind.If, "heart", "health under", "Only while its health is under this share of its most.",
            "Only while its health is under {HealthUnder}", [F("HealthUnder", CheatFieldType.Percent, 50, max: 100)], b => $"while under {b.Number("HealthUnder")}% health"),
        new("I.NoAllies", BlockKind.If, "user", "fighting alone", "Only when no one else on its side is still standing.",
            "Only when it fights alone", [], _ => "only when alone"),
        new("I.OnBoss", BlockKind.If, "target", "aimed at it", "Only a spell aimed at the boss (for When a player casts).",
            "Only if the spell is aimed at it", [], _ => "only spells aimed at it"),
        new("I.SingleTarget", BlockKind.If, "target", "one target", "Only a spell that hits a single target, not a whole side.",
            "Only if the spell hits one target", [], _ => "only single-target spells"),
        new("I.CasterSchool", BlockKind.If, "school", "player's school", "Only when the player who set it off is a wizard of this school.",
            "Only if the player is a {CasterSchool} wizard", [F("CasterSchool", CheatFieldType.Choice, "Fire", SCHOOLS[1..])], b => $"only {b.Text("CasterSchool")} wizards"),
        new("I.CardType", BlockKind.If, "cards", "card type", "Only cards of this type, as the game names it: Damage, Heal, Ward, Charm, Aura, Global, Steal, Manipulation...",
            "Only cards of type {Kind}", [F("Kind", CheatFieldType.Words, "Damage")], b => $"only {b.Text("Kind")} cards"),
        new("I.Cards", BlockKind.If, "cards", "these cards", "Only these cards, by name, with commas between.",
            "Only these cards: {Spells}", [F("Spells", CheatFieldType.Cards)], b => $"only {string.Join(", ", b.List("Spells"))}"),

        // Do...
        new("D.Cast", BlockKind.Do, "sparkle", "cast a spell", "A free spell: no pips, it never fizzles and works through stun. Silent: no casting animation. Dispel-proof: a Dispel on the boss does not stop it.",
            "Cast {Spell} on {Target} {Silent} {DispelImmune}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), new("Silent", CheatFieldType.Toggle, false, Label: "silent"),
             new("DispelImmune", CheatFieldType.Toggle, true, Label: "dispel-proof")],
            b => $"cast {SpellName(b)}{Target(b)}{(b.Flag("Silent") ? " (silent)" : "")}{(b.Flag("DispelImmune") ? "" : " (can be dispelled)")}"),
        new("D.Polymorph", BlockKind.Do, "swap", "polymorph", "Casts a polymorph spell. Polymorph - Dragon NA: it turns into the Black Dragon, attacks (535 Moon damage, destroys the aura) and turns back. Afterlife - Malistaire 02: it becomes Haunted Malistaire for the rest of the battle. \"Where the spell aims\" sends a lasting one on itself.",
            "Polymorph with {Spell} on {Target}", [F("Spell", CheatFieldType.Spell, "Polymorph - Dragon NA"), F("Target", CheatFieldType.Target, "")],
            b => $"polymorph with {SpellName(b)}{Target(b)}"),
        new("D.Interrupt", BlockKind.Do, "stop", "interrupt", "The player's spell that set it off fizzles. A cheat with this block goes off before that spell, not after.",
            "Interrupt the player's spell", [], _ => "interrupt the spell"),
        new("D.RemoveCharms", BlockKind.Do, "broom", "remove charms", "Takes charms (blades, weaknesses, accuracy...) off, newest first. 0 means all of them.",
            "Remove {Count} {Kind} charms from {Target}", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "All", s_charmKinds), F("Target", CheatFieldType.Target, "AllEnemies")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {LabelOf(s_charmKinds, b.Text("Kind")).Replace("of any", "")} charms{Target(b).Replace(" on ", " from ")}".Replace("  ", " ")),
        new("D.RemoveWards", BlockKind.Do, "broom", "remove wards", "Takes wards (shields, traps, absorbs...) off, newest first. 0 means all of them.",
            "Remove {Count} {Kind} wards from {Target}", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "All", s_charmKinds), F("Target", CheatFieldType.Target, "AllEnemies")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {LabelOf(s_charmKinds, b.Text("Kind")).Replace("of any", "")} wards{Target(b).Replace(" on ", " from ")}".Replace("  ", " ")),
        new("D.AddEffects", BlockKind.Do, "shield", "add charms or wards", "The charms and wards of a spell (Tower Shield, Fire Blade, Feint...) go on at once, with no casting.",
            "Put the charms and wards of {Spell} on {Target}", [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target, "Self")],
            b => $"put the effects of {SpellName(b)}{Target(b)}"),
        new("D.Heal", BlockKind.Do, "heal", "heal", "Heals by hit points, or by a share of the most health.",
            "Heal {Target} by {Amount}", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.HealAmount)],
            b => $"heal{Target(b)} by {(b.Flag("Percent") ? $"{b.Number("Amount")}%" : N(b.Number("Amount")))}"),
        new("D.Minion", BlockKind.Do, "users", "bring in a minion", "A creature (or one of your customs) joins its side in a free circle and fights from the next round.",
            "Bring in {Creature} as a minion", [F("Creature", CheatFieldType.Creature)], b => $"bring in {Creature(b)}"),
        new("D.Form", BlockKind.Do, "swap", "change form", "A second form: the creature takes a free circle. With leave on, the boss leaves the battle as it comes.",
            "Come back as {Creature} {ReplaceBoss}", [F("Creature", CheatFieldType.Creature), new("ReplaceBoss", CheatFieldType.Toggle, true, Label: "and leave the battle")],
            b => $"come back as {Creature(b)}"),
        new("D.SetHealth", BlockKind.Do, "heart", "set health", "Its most and current health become this, even after it was defeated (a second life).",
            "Set its health to {Amount}", [F("Amount", CheatFieldType.Number, 10000)], b => $"its health becomes {N(b.Number("Amount"))}"),
        new("D.SetPhase", BlockKind.Do, "layers", "next phase", "The fight moves on to this phase from the next round. Use In phase blocks to give each phase its own cheats.",
            "Move on to phase {Phase}", [F("Phase", CheatFieldType.Number, 2, max: 99)], b => $"move on to phase {b.Number("Phase")}"),
        new("D.Say", BlockKind.Do, "quote", "say a line", "A line from the game shown over the boss in battle. Type a few of its words or its number (Lookup tab, Phrase).",
            "Say {Line}", [F("Line", CheatFieldType.Line)], b => $"say \"{b.Text("Line")}\""),
        new("D.Chat", BlockKind.Do, "chat", "chat message", "Any words you like, sent to every player's chat window.",
            "Tell every player in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell everyone \"{b.Text("Text")}\""),
        new("D.Pips", BlockKind.Do, "pips", "give or take pips", "Gives pips (to fill empty slots first) or takes them away.",
            "{Amount} {Kind} pips, for {Target}", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Self")],
            b => $"{(b.Number("Amount") < 0 ? "take" : "give")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", b.Number("Amount") < 0 ? " from " : " to ")}"),
        new("D.Defeat", BlockKind.Do, "skull", "defeat itself", "The boss is defeated after the cheat (its drops still come).",
            "Defeat itself", [], _ => "defeat itself"),
        new("D.RandomOne", BlockKind.Do, "dice", "one spell at random", "Of all its Cast blocks, only one is cast each time, picked at random.",
            "Cast only one of these spells, picked at random", [], _ => "(only one spell, at random)"),
        new("D.Other", BlockKind.Do, "puzzle", "something else", "An action this app does not know, kept as the file has it.",
            "Do {Do}", [F("Do", CheatFieldType.Words)], b => $"do {b.Text("Do")}"),
    ];

    private static readonly Dictionary<string, CheatSpec> s_byId = All.ToDictionary(s => s.Id);

    internal static CheatSpec Get(string id) => s_byId.TryGetValue(id, out var spec) ? spec : s_byId["D.Other"];

    /// <summary>The server's Do for a Do block.</summary>
    internal static string DoOf(string id) => id switch { "D.Say" or "D.Chat" => "Say", _ => id[2..] };

    /// <summary>A cheat in one line, such as "When hit by Fire: cast Supernova Sun on the attacker, 50% chance."</summary>
    internal static string Summary(CheatRule rule) {
        var when = rule.When.Kind.Summary(rule.When);
        var dos = rule.Dos.Where(d => d.On).Select(d => d.Kind.Summary(d)).ToList();
        var ifs = rule.Ifs.Where(i => i.On).Select(i => i.Kind.Summary(i)).ToList();
        var what = dos.Count == 0 ? "does nothing yet" : string.Join(", then ", dos);
        var text = $"{when}: {what}" + (ifs.Count > 0 ? ", " + string.Join(", ", ifs) : "");

        return text.EndsWith('"') && text.Length > 1 && ".!?".Contains(text[^2]) ? text : text + ".";
    }

}
