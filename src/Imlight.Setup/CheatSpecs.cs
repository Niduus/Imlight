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
/// Every block a boss cheat (or a player cheat) can be built from (Niduus, 2026-10-22: "cheats can have their
/// conditions and such able to be modified and created"; "Get more expansive with cheats"; "add PLAYER cheats"), with
/// the words it shows and what it means. The server reads them as the cheat fields of spiraldb-extra/BossCheats (see the
/// top of the server's BossCheats.cs and PlayerCheats.cs): When blocks set When and its numbers, If blocks set a
/// condition field each, and Do blocks are the cheat's Actions.
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

    /// <summary>The player cheats' targets, as the .c1 commands take them.</summary>
    internal static readonly (string Value, string Label)[] PLAYER_TARGETS = [
        ("", "where the spell aims"), ("me", "me"), ("p1", "my side's circle 1"), ("p2", "my side's circle 2"),
        ("p3", "my side's circle 3"), ("p4", "my side's circle 4"), ("e1", "enemy 1"), ("e2", "enemy 2"), ("e3", "enemy 3"),
        ("e4", "enemy 4"), ("all", "every target"),
    ];

    internal static readonly (string Value, string Label)[] SCHOOLS = [
        ("", "any school"), ("Fire", "Fire"), ("Ice", "Ice"), ("Storm", "Storm"), ("Myth", "Myth"), ("Life", "Life"),
        ("Death", "Death"), ("Balance", "Balance"), ("Star", "Star"), ("Sun", "Sun"), ("Moon", "Moon"), ("Shadow", "Shadow"),
    ];

    internal static readonly (string Value, string Label)[] CATEGORIES = [
        ("", "any"), ("Damage", "damage"), ("Heal", "heal"), ("Charm", "charm (any)"), ("Ward", "ward (any)"),
        ("Charm,Ward", "charm or ward"), ("Shield", "shield"), ("Trap", "trap"), ("Blade", "blade"), ("Weakness", "weakness"),
        ("Aura", "aura"), ("Global", "global (bubble)"), ("Aura,Global", "aura or global"), ("Minion", "minion"),
        ("Shadow", "shadow"), ("Enchantment", "enchantment"), ("Stun", "stun"), ("Steal", "steal"), ("Dispel", "dispel"),
        ("Polymorph", "polymorph"), ("Treasure", "treasure card"),
    ];

    private static readonly (string, string)[] s_charmKinds = [("All", "of any"), ("Good", "good"), ("Bad", "bad")];
    private static readonly (string, string)[] s_pipKinds = [("Normal", "normal"), ("Power", "power"), ("Shadow", "shadow")];
    private static readonly (string, string)[] s_pipsOf = [("", "it"), ("Enemy", "a player")];
    private static readonly (string, string)[] s_addKinds = [("Blade", "blade"), ("Weakness", "weakness"), ("Shield", "shield"), ("Trap", "trap"), ("Absorb", "absorb")];
    private static readonly (string, string)[] s_boostKinds = [("Damage", "damage"), ("Resist", "resist"), ("Heal", "healing"), ("Accuracy", "accuracy")];
    private static readonly (string, string)[] s_timings = [
        ("Now", "right away"), ("Before", "before my card"), ("After", "after my card"), ("First", "first in the round"), ("Last", "last in the round"),
    ];

    private static readonly (string, string)[] s_fizzles = [("Landed", "lands"), ("Fizzled", "fizzles"), ("Any", "lands or fizzles")];
    private static readonly (string, string)[] s_sides = [("Us", "we go first"), ("Them", "they go first")];
    private static readonly (string, string)[] s_anySchool = [("", "every school"), .. SCHOOLS[1..]];

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

    private static string RoundsFor(CheatBlock b) => b.Number("Rounds") <= 0 ? "the rest of the battle" : b.Number("Rounds") == 1 ? "1 round" : $"{b.Number("Rounds")} rounds";

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

    private static string SchoolWord(CheatBlock b) => b.Text("School").Length > 0 ? b.Text("School") + " " : "";

    /// <summary>The kind of card a When such as "a player heals" stands for (as the server's s_castWhens).</summary>
    internal static string CastWhenKind(string spec) => spec switch {
        "W.PlayerHeals" => "Heal", "W.PlayerCharms" => "Charm", "W.PlayerWards" => "Ward", "W.PlayerAuras" => "Aura",
        "W.PlayerGlobals" => "Global", "W.PlayerMinions" => "Minion", _ => "",
    };

    // A When for one kind of a player's card ("When a player heals"), with its school and the count of what it covers.
    private static CheatSpec CastWhen(string id, string icon, string palette, string verb)
        => new(id, BlockKind.When, icon, palette, $"When a player {verb}: the spell lands. Any school, or pick one. Add a \"these spells\" block to name spells.",
            $"When a player {verb} with a {{School}} spell {{Count}}", [F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When a player {verb}{(b.Text("School").Length > 0 ? $" ({b.Text("School")})" : "")}");

    internal static readonly CheatSpec[] All = [
        // When... (a boss's)
        new("W.BattleStart", BlockKind.When, "flag", "the battle starts", "Its first round. A boss brought in later counts from its own first round.",
            "When the battle starts", [], _ => "When the battle starts", "both"),
        new("W.RoundStart", BlockKind.When, "clock", "a round starts", "The start of a round, before anyone moves. Pick the round, every few rounds, or both.",
            "At the start of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the start of {Rounds(b)}", "both"),
        new("W.FromRound", BlockKind.When, "clock", "from round … on", "Every round from this round on, such as an enrage after round 10. \"Every\" above 1 skips rounds between.",
            "At the start of every round from round {Round} on, every {Every} rounds", [F("Round", CheatFieldType.Number, 10, max: 999), F("Every", CheatFieldType.Number, 1, max: 99)],
            b => $"Every {(b.Number("Every") > 1 ? $"{b.Number("Every")} rounds" : "round")} from round {b.Number("Round")} on", "both"),
        new("W.RoundEnd", BlockKind.When, "clockend", "a round ends", "The end of a round, after everyone has moved.",
            "At the end of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the end of {Rounds(b)}", "both"),
        new("W.HealthBelow", BlockKind.When, "heart", "its health drops", "The first time its health falls under the mark (once each phase). Works even after it is defeated, for a second life.",
            "When its health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When its health drops below {N(b.Number("Health"))}" : $"When its health drops below {b.Number("Percent")}%"),
        new("W.PlayerCasts", BlockKind.When, "sparkle", "a player casts", "A player's spell lands: pick what kind of card (heal, shield, blade, trap, aura, global, minion...) and its school. Add a \"these spells\" block to name spells.",
            "When a player casts a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When a player casts {Spell(b, "a spell")}"),
        CastWhen("W.PlayerHeals", "heal", "a player heals", "heals"),
        CastWhen("W.PlayerCharms", "sparkle", "a player charms", "uses a charm"),
        CastWhen("W.PlayerWards", "shield", "a player wards", "uses a ward"),
        CastWhen("W.PlayerAuras", "layers", "a player auras", "uses an aura"),
        CastWhen("W.PlayerGlobals", "bolt", "a player globals", "uses a global"),
        CastWhen("W.PlayerMinions", "users", "a player minions", "calls a minion"),
        new("W.PlayerCrits", BlockKind.When, "bolt", "a player crits", "A player's spell lands as a critical hit (or a critical heal).",
            "When a player lands a critical {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When a player crits with {Spell(b, "a spell")}"),
        new("W.BossHit", BlockKind.When, "hit", "it is hit", "A spell from the other side hurts it. Pick the school and the kind of card, or any.",
            "When it is hit by a {School} {Category} spell {Count}", [F("School", CheatFieldType.School), F("Category", CheatFieldType.Category), F("Count", CheatFieldType.MatchCount)],
            b => b.Text("Category").Length == 0 ? $"When hit by {(b.Text("School").Length > 0 ? b.Text("School") : "anything")}" : $"When hit by {Spell(b, "")}"),
        new("W.PlayerPasses", BlockKind.When, "hourglass", "a player passes", "A player passes their turn.",
            "When a player passes", [], _ => "When a player passes"),
        new("W.PlayerHealthBelow", BlockKind.When, "heart", "a player's health drops", "A player's health first falls under the mark (once for each player). \"The attacker\" is that player.",
            "When a player's health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When a player drops below {N(b.Number("Health"))} health" : $"When a player drops below {b.Number("Percent")}% health"),
        new("W.PlayerDefeated", BlockKind.When, "skull", "a player falls", "A player is defeated. \"Whoever that spell was aimed at\" is that player.",
            "When a player is defeated", [], _ => "When a player is defeated"),
        new("W.PlayerFlees", BlockKind.When, "stop", "a player flees", "A player runs from the battle. It goes off at the start of the next round.",
            "When a player flees", [], _ => "When a player flees"),
        new("W.MinionDefeated", BlockKind.When, "skull", "a minion falls", "A creature on its side (a minion or a helper) is defeated.",
            "When a creature on its side is defeated", [], _ => "When one of its minions is defeated"),
        new("W.BossDefeated", BlockKind.When, "skull", "it is defeated", "It is defeated. Add \"these spells\" (such as Incindiate) to go off only when one of those spells defeated it, and \"set health\" first to bring it back.",
            "When it is defeated", [], _ => "When it is defeated"),
        new("W.Pips", BlockKind.When, "pips", "pips reach", "At the start of a round, when it (or any player) has at least this many pips. A power pip counts as one. With \"a player\", it goes off for each player who has them, and \"the attacker\" is that player.",
            "When {PipsOf} has at least {Pips} pips", [F("PipsOf", CheatFieldType.Choice, "", s_pipsOf), F("Pips", CheatFieldType.Number, 5, max: 14)],
            b => $"When {LabelOf(s_pipsOf, b.Text("PipsOf"))} has {b.Number("Pips")} or more pips"),

        // When... (a player cheat's)
        new("W.ICast", BlockKind.When, "sparkle", "I cast", "Your spell, just before it lands. Pick the kind of card and school, and add a \"these spells\" block to name spells.",
            "When I cast a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When I cast {Spell(b, "a spell")}", "player"),
        new("W.ICrit", BlockKind.When, "bolt", "I crit", "Your spell lands as a critical hit (or a critical heal).",
            "When I land a critical {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When I crit with {Spell(b, "a spell")}", "player"),
        new("W.IPass", BlockKind.When, "hourglass", "I pass", "You pass your turn.", "When I pass", [], _ => "When I pass", "player"),
        new("W.EnemyCasts", BlockKind.When, "hit", "an enemy casts", "An enemy's spell, just before it lands. \"Right away\" casts yours before it.",
            "When an enemy casts a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When an enemy casts {Spell(b, "a spell")}", "player"),
        new("W.MyHealthBelow", BlockKind.When, "heart", "my health drops", "The first time in a battle your health falls under the mark.",
            "When my health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When my health drops below {N(b.Number("Health"))}" : $"When my health drops below {b.Number("Percent")}%", "player"),
        new("W.MyPips", BlockKind.When, "pips", "my pips reach", "At the start of a round, when you have at least this many pips. A power pip counts as one.",
            "When I have at least {Pips} pips", [F("Pips", CheatFieldType.Number, 7, max: 14)], b => $"When I have {b.Number("Pips")} or more pips", "player"),
        new("W.Other", BlockKind.When, "bolt", "something else", "A When this app does not know, kept as the file has it.",
            "When {When}", [F("When", CheatFieldType.Words)], b => $"When {b.Text("When")}", "both"),

        // If...
        new("I.Chance", BlockKind.If, "dice", "chance", "It goes off only this often. 50 means half the time.",
            "If a {Chance} chance comes up", [F("Chance", CheatFieldType.Percent, 50, max: 100)], b => $"{b.Number("Chance")}% chance", "both"),
        new("I.Once", BlockKind.If, "once", "only the first time", "Only the first time in a battle.",
            "Only the first time", [], _ => "only the first time", "both"),
        new("I.MaxTimes", BlockKind.If, "list", "at most … times", "No more than this many times in one battle.",
            "At most {MaxTimes} times a battle", [F("MaxTimes", CheatFieldType.Number, 3, max: 999)], b => $"at most {b.Number("MaxTimes")} times", "both"),
        new("I.Cooldown", BlockKind.If, "hourglass", "wait between", "After it goes off, it waits this many rounds before it can go off again.",
            "Then wait {Cooldown} rounds before again", [F("Cooldown", CheatFieldType.Number, 2, max: 99)], b => $"then waits {b.Number("Cooldown")} rounds", "both"),
        new("I.Phase", BlockKind.If, "layers", "in phase", "Only in this phase of the fight. A boss starts in phase 1; a Do block moves it on.",
            "Only in phase {Phase}", [F("Phase", CheatFieldType.Number, 1, max: 99)], b => $"in phase {b.Number("Phase")}"),
        new("I.HealthAbove", BlockKind.If, "heart", "health above", "Only while its health (for a player cheat, yours) is above this share of the most.",
            "Only while health is above {HealthAbove}", [F("HealthAbove", CheatFieldType.Percent, 50, max: 100)], b => $"while above {b.Number("HealthAbove")}% health", "both"),
        new("I.HealthUnder", BlockKind.If, "heart", "health under", "Only while its health (for a player cheat, yours) is under this share of the most.",
            "Only while health is under {HealthUnder}", [F("HealthUnder", CheatFieldType.Percent, 50, max: 100)], b => $"while under {b.Number("HealthUnder")}% health", "both"),
        new("I.Rounds", BlockKind.If, "clock", "in these rounds", "Only in these rounds of the battle. Leave the second box at 0 for no end.",
            "Only from round {FromRound} to round {ToRound}", [F("FromRound", CheatFieldType.Number, 5, max: 999), F("ToRound", CheatFieldType.Number, 0, max: 999)],
            b => b.Number("ToRound") > 0 ? $"in rounds {b.Number("FromRound")}-{b.Number("ToRound")}" : $"from round {b.Number("FromRound")} on", "both"),
        new("I.Players", BlockKind.If, "users", "how many players", "Only while this many players are still standing. Leave the top at 0 for no top.",
            "Only while {PlayersMin} to {PlayersMax} players are standing", [F("PlayersMin", CheatFieldType.Number, 1, max: 4), F("PlayersMax", CheatFieldType.Number, 0, max: 4)],
            b => b.Number("PlayersMax") > 0 ? $"with {b.Number("PlayersMin")}-{b.Number("PlayersMax")} players" : $"with {b.Number("PlayersMin")}+ players"),
        new("I.NoAllies", BlockKind.If, "user", "fighting alone", "Only when no one else on its side is still standing.",
            "Only when it fights alone", [], _ => "only when alone"),
        new("I.HasAllies", BlockKind.If, "users", "not alone", "Only while someone else on its side is still standing.",
            "Only while it has allies standing", [], _ => "only with allies"),
        new("I.OnBoss", BlockKind.If, "target", "aimed at it", "Only a spell aimed at the boss (for When a player casts).",
            "Only if the spell is aimed at it", [], _ => "only spells aimed at it"),
        new("I.SingleTarget", BlockKind.If, "target", "one target", "Only a spell that hits a single target, not a whole side.",
            "Only if the spell hits one target", [], _ => "only single-target spells", "both"),
        new("I.CasterSchool", BlockKind.If, "school", "player's school", "Only when the player who set it off is a wizard of this school.",
            "Only if the player is a {CasterSchool} wizard", [F("CasterSchool", CheatFieldType.Choice, "Fire", SCHOOLS[1..])], b => $"only {b.Text("CasterSchool")} wizards"),
        new("I.Fizzle", BlockKind.If, "stop", "lands or fizzles", "Whether the player's spell has to land, fizzle, or either, for a player-cast When. Without this block only a spell that lands counts.",
            "Only if the spell {Fizzle}", [F("Fizzle", CheatFieldType.Choice, "Fizzled", s_fizzles)],
            b => $"only if the spell {LabelOf(s_fizzles, b.Text("Fizzle"))}"),
        new("I.Spells", BlockKind.If, "cards", "these spells", "Only these spells, by name, with commas between. \"Any version of\" makes Scald mean every Scald: its treasure card, item card, a monster's copy and the rest. \"Exactly\" means only spells with that very name.",
            "Only {AnyVersion} these spells: {Spells} {Count}",
            [new("AnyVersion", CheatFieldType.VersionChoice, true), F("Spells", CheatFieldType.Cards), F("Count", CheatFieldType.MatchCount)],
            b => $"only {string.Join(", ", b.List("Spells"))}{(b.Flag("AnyVersion") ? " (any version)" : "")}", "both"),
        new("I.Cost", BlockKind.If, "pips", "pip cost", "Only spells that cost this many pips. Leave the top at 0 for no top.",
            "Only spells that cost {CostMin} to {CostMax} pips", [F("CostMin", CheatFieldType.Number, 4, max: 14), F("CostMax", CheatFieldType.Number, 0, max: 14)],
            b => b.Number("CostMax") > 0 ? $"only {b.Number("CostMin")}-{b.Number("CostMax")} pip spells" : $"only spells of {b.Number("CostMin")}+ pips", "both"),
        new("I.CardType", BlockKind.If, "cards", "card type", "Only cards of this type, as the game names it: Damage, Heal, Ward, Charm, Aura, Global, Steal, Manipulation...",
            "Only cards of type {Kind}", [F("Kind", CheatFieldType.Words, "Damage")], b => $"only {b.Text("Kind")} cards", "both"),

        // Do... (a boss's)
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
        new("D.Reflect", BlockKind.Do, "swap", "reflect the spell", "The player's spell fizzles and the boss casts it back: an attack hits that player, a heal or blade goes on the boss's side. Goes off before the spell, as interrupt does.",
            "Reflect the player's spell back", [], _ => "reflect the spell back"),
        new("D.RemoveCharms", BlockKind.Do, "broom", "remove charms", "Takes charms (blades, weaknesses, accuracy...) off, newest first. 0 means all of them. Name a spell to take off only its charms (any version).",
            "Remove {Count} {Kind} charms from {Target} made by {Spell}", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "All", s_charmKinds), F("Target", CheatFieldType.Target, "AllEnemies"), F("Spell", CheatFieldType.Words, "")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {LabelOf(s_charmKinds, b.Text("Kind")).Replace("of any", "")} {(b.Text("Spell").Length > 0 ? b.Text("Spell") + " " : "")}charms{Target(b).Replace(" on ", " from ")}".Replace("  ", " ")),
        new("D.RemoveWards", BlockKind.Do, "broom", "remove wards", "Takes wards (shields, traps, absorbs...) off, newest first. 0 means all of them. Name a spell to take off only its wards (any version).",
            "Remove {Count} {Kind} wards from {Target} made by {Spell}", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "All", s_charmKinds), F("Target", CheatFieldType.Target, "AllEnemies"), F("Spell", CheatFieldType.Words, "")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {LabelOf(s_charmKinds, b.Text("Kind")).Replace("of any", "")} {(b.Text("Spell").Length > 0 ? b.Text("Spell") + " " : "")}wards{Target(b).Replace(" on ", " from ")}".Replace("  ", " ")),
        new("D.RemoveAuras", BlockKind.Do, "broom", "remove auras", "Takes auras (and boosts from cheats) off.",
            "Remove the auras of {Target}", [F("Target", CheatFieldType.Target, "AllEnemies")], b => $"remove the auras{Target(b).Replace(" on ", " of ")}"),
        new("D.AddEffects", BlockKind.Do, "shield", "add a spell's charms", "The charms and wards of a spell (Tower Shield, Fire Blade, Feint...) go on at once, with no casting.",
            "Put the charms and wards of {Spell} on {Target}", [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target, "Self")],
            b => $"put the effects of {SpellName(b)}{Target(b)}"),
        new("D.AddCharm", BlockKind.Do, "shield", "add a charm or ward", "A blade, weakness, shield, trap or absorb of your own size goes on at once, and is used up like a real one. The amount is a percent (hit points for an absorb).",
            "Put a {Amount} {School} {Kind} on {Target}", [F("Amount", CheatFieldType.Percent, 50, max: 100_000), F("School", CheatFieldType.Choice, "", s_anySchool), F("Kind", CheatFieldType.Choice, "Blade", s_addKinds), F("Target", CheatFieldType.Target, "Self")],
            b => $"put a {b.Number("Amount")}{(b.Text("Kind") == "Absorb" ? "" : "%")} {SchoolWord(b)}{LabelOf(s_addKinds, b.Text("Kind"))}{Target(b)}"),
        new("D.Boost", BlockKind.Do, "bolt", "boost or enrage", "Raises (or with a minus number, lowers) its damage, resist, healing or accuracy for some rounds; 0 rounds is the rest of the battle. It is never used up and stacks: on a \"from round … on\" cheat it is an enrage.",
            "Change the {Kind} of {Target} by {Amount} for {School} spells for {Rounds} rounds", [F("Kind", CheatFieldType.Choice, "Damage", s_boostKinds), F("Target", CheatFieldType.Target, "Self"), new("Amount", CheatFieldType.Number, 10, Max: 1000, Min: -100), F("School", CheatFieldType.Choice, "", s_anySchool), F("Rounds", CheatFieldType.Number, 0, max: 99)],
            b => $"{LabelOf(s_boostKinds, b.Text("Kind"))} {(b.Number("Amount") >= 0 ? "+" : "")}{b.Number("Amount")}%{(b.Text("School").Length > 0 ? $" ({b.Text("School")})" : "")}{Target(b)} for {RoundsFor(b)}"),
        new("D.Immune", BlockKind.Do, "shield", "become immune", "No damage from that school (or any) for some rounds; 0 rounds is the rest of the battle. Pierce still gets through, as against a shield.",
            "Make {Target} immune to {School} damage for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Self"), F("School", CheatFieldType.Choice, "Fire", s_anySchool), F("Rounds", CheatFieldType.Number, 2, max: 99)],
            b => $"immune to {SchoolWord(b)}damage{Target(b)} for {RoundsFor(b)}"),
        new("D.Stun", BlockKind.Do, "stop", "stun", "The target misses this many turns, as from a stun spell (with no stun animation; cast a stun spell for that).",
            "Stun {Target} for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Caster"), F("Rounds", CheatFieldType.Number, 1, max: 9)],
            b => $"stun{Target(b)} for {(b.Number("Rounds") > 1 ? $"{b.Number("Rounds")} rounds" : "1 round")}"),
        new("D.Heal", BlockKind.Do, "heal", "heal", "Heals by hit points, or by a share of the most health.",
            "Heal {Target} by {Amount}", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.HealAmount)],
            b => $"heal{Target(b)} by {(b.Flag("Percent") ? $"{b.Number("Amount")}%" : N(b.Number("Amount")))}"),
        new("D.HealthTo", BlockKind.Do, "heart", "set health to %", "Health becomes this share of the most (never below 1), up or down.",
            "Set the health of {Target} to {Amount}", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.Percent, 50, max: 100)],
            b => $"health{Target(b).Replace(" on ", " of ")} becomes {b.Number("Amount")}%"),
        new("D.SwapHealth", BlockKind.Do, "swap", "swap health", "It and the target swap their share of health: at 10% it takes a player's 90% and gives them its 10%.",
            "Swap health with {Target}", [F("Target", CheatFieldType.Target, "Caster")], b => $"swap health{Target(b).Replace(" on ", " with ")}"),
        new("D.Minion", BlockKind.Do, "users", "bring in a minion", "A creature (or one of your customs) joins its side in a free circle and fights from the next round.",
            "Bring in {Creature} as a minion", [F("Creature", CheatFieldType.Creature)], b => $"bring in {Creature(b)}"),
        new("D.Form", BlockKind.Do, "swap", "change form", "A second form: the creature takes a free circle. With leave on, the boss leaves the battle as it comes.",
            "Come back as {Creature} {ReplaceBoss}", [F("Creature", CheatFieldType.Creature), new("ReplaceBoss", CheatFieldType.Toggle, true, Label: "and leave the battle")],
            b => $"come back as {Creature(b)}"),
        new("D.SetHealth", BlockKind.Do, "heart", "set health", "Its most and current health become this, even after it was defeated (a second life).",
            "Set its health to {Amount}", [F("Amount", CheatFieldType.Number, 10000)], b => $"its health becomes {N(b.Number("Amount"))}"),
        new("D.FirstTurn", BlockKind.Do, "clock", "turn order", "Which side acts first, as .wegofirst and .theygofirst do. \"We\" is the cheat's own side (the boss's, or yours in a player cheat). It counts from the next round.",
            "Turn order: {Side}", [F("Side", CheatFieldType.Choice, "Us", s_sides)], b => LabelOf(s_sides, b.Text("Side")), "both"),
        new("D.SetPhase", BlockKind.Do, "layers", "next phase", "The fight moves on to this phase from the next round. Use In phase blocks to give each phase its own cheats.",
            "Move on to phase {Phase}", [F("Phase", CheatFieldType.Number, 2, max: 99)], b => $"move on to phase {b.Number("Phase")}"),
        new("D.Say", BlockKind.Do, "quote", "say a line", "A line from the game shown over the boss in battle. Type a few of its words or its number (Phrases tab).",
            "Say {Line}", [F("Line", CheatFieldType.Line)], b => $"say \"{b.Text("Line")}\""),
        new("D.Chat", BlockKind.Do, "chat", "chat message", "Any words you like, sent to every player's chat window.",
            "Tell every player in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell everyone \"{b.Text("Text")}\""),
        new("D.Pips", BlockKind.Do, "pips", "give or take pips", "Gives pips (to fill empty slots first) or takes them away.",
            "{Amount} {Kind} pips, for {Target}", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Self")],
            b => $"{(b.Number("Amount") < 0 ? "take" : "give")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", b.Number("Amount") < 0 ? " from " : " to ")}"),
        new("D.StealPips", BlockKind.Do, "pips", "steal pips", "Takes pips from the target and gives them to the boss.",
            "Steal {Amount} {Kind} pips from {Target}", [F("Amount", CheatFieldType.Number, 2, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Caster")],
            b => $"steal {b.Number("Amount")} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", " from ")}"),
        new("D.Defeat", BlockKind.Do, "skull", "defeat itself", "The boss is defeated after the cheat (its drops still come).",
            "Defeat itself", [], _ => "defeat itself"),
        new("D.RandomOne", BlockKind.Do, "dice", "one spell at random", "Of all its Cast blocks, only one is cast each time, picked at random.",
            "Cast only one of these spells, picked at random", [], _ => "(only one spell, at random)"),

        // Do... (a player cheat's)
        new("D.FreeCast", BlockKind.Do, "sparkle", "cast for free", "A free spell, as the .c1 and .c2 commands cast it. Pick when: right away, before or after your card, or first or last in the round. Silent skips the casting animation; enchanted adds the enchantments you pick (several stack), or the best one that fits when none is picked.",
            "Cast {Spell} on {Target} {Timing} {Times} times {Silent} {Enchant} {Enchants}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.PlayerTarget), F("Timing", CheatFieldType.Choice, "Now", s_timings),
             F("Times", CheatFieldType.Number, 1, max: 10), new("Silent", CheatFieldType.Toggle, false, Label: "silent"), new("Enchant", CheatFieldType.Toggle, false, Label: "enchanted"),
             F("Enchants", CheatFieldType.Enchants)],
            b => $"cast {SpellName(b)}{(b.Text("Target").Length > 0 ? " on " + LabelOf(PLAYER_TARGETS, b.Text("Target")) : "")} {LabelOf(s_timings, b.Text("Timing"))}"
                 + $"{(b.Number("Times") > 1 ? $" x{b.Number("Times")}" : "")}{(b.Flag("Silent") ? " (silent)" : "")}{(b.Flag("Enchant") ? b.List("Enchants") is { Count: > 0 } picked ? $" (enchanted: {string.Join(" + ", picked)})" : " (enchanted)" : "")}", "player"),
        new("D.MyPips", BlockKind.Do, "pips", "give me pips", "Gives you pips (or takes them away).",
            "{Amount} {Kind} pips for me", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds)],
            b => $"{(b.Number("Amount") < 0 ? "take" : "give me")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips", "player"),
        new("D.MyHeal", BlockKind.Do, "heal", "heal me", "Heals you by hit points, or by a share of your most health.",
            "Heal me by {Amount}", [F("Amount", CheatFieldType.HealAmount)], b => $"heal me by {(b.Flag("Percent") ? $"{b.Number("Amount")}%" : N(b.Number("Amount")))}", "player"),
        new("D.Banner", BlockKind.Do, "quote", "show a banner", "A line from the game shown over you in battle, as .ca shows it. Type a few of its words or its number.",
            "Show {Line} over me", [F("Line", CheatFieldType.Line)], b => b.Text("Line").Length == 0 ? "show (no line picked)" : $"show \"{b.Text("Line")}\"", "player"),
        new("D.MyChat", BlockKind.Do, "chat", "message to me", "Any words you like, in your own chat window.",
            "Tell me in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell me \"{b.Text("Text")}\"", "player"),

        // Do... (both)
        new("D.PickOne", BlockKind.Do, "dice", "pick one at random", "Of the blocks under this one (how many: 0 is all of them), only one goes off, picked at random. Chances such as 2, 1, 1 make the first twice as likely as each other one.",
            "Do only one of the next {Count} blocks, with chances {Weights}", [F("Count", CheatFieldType.Number, 0, max: 20), F("Weights", CheatFieldType.Words, "")],
            b => $"(one of the next {(b.Number("Count") > 0 ? N(b.Number("Count")) : "blocks")}, at random)", "both"),
        new("D.Other", BlockKind.Do, "puzzle", "something else", "An action this app does not know, kept as the file has it.",
            "Do {Do}", [F("Do", CheatFieldType.Words)], b => $"do {b.Text("Do")}", "both"),
    ];

    private static readonly Dictionary<string, CheatSpec> s_byId = All.ToDictionary(s => s.Id);

    internal static CheatSpec Get(string id) => s_byId.TryGetValue(id, out var spec) ? spec : s_byId["D.Other"];

    /// <summary>The server's Do for a Do block.</summary>
    internal static string DoOf(string id) => id switch {
        "D.Say" or "D.Chat" or "D.Banner" or "D.MyChat" => "Say", "D.MyPips" => "Pips", "D.MyHeal" => "Heal", _ => id[2..],
    };

    /// <summary>The server's When for a When block.</summary>
    internal static string WhenOf(string id) => id switch { "W.MyPips" => "Pips", _ => id[2..] };

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
