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
/// condition field each, and Do blocks are the cheat's Actions. Every block works in a boss's cheats and in the player
/// cheats alike (Niduus, 2026-10-23: "ALL CHEATS are to be compatible for PLAYERS and CREATURES"): "it" is whoever the
/// cheat is of, and "a player" someone on the other side. The few blocks first made for one side only and the same as
/// another block are kept, as "alias", to read old files.
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
        ("WeakestAlly", "its weakest ally"),
        ("StrongestAlly", "its strongest ally"),
        ("Summoner", "whoever summoned it"),
        ("LastAttacker", "the last one who hurt it"),
        ("AllAllies", "its whole side"),
        // Niduus, 2026-10-24 (the gauntlets): the target of the block before, the ally hit (When an ally is hit), and
        // whoever has the block's mark.
        ("Last", "the same as the block before"),
        ("Hit", "the one hit"),
        ("Marked", "whoever has the mark"),
        // Niduus, 2026-10-24 (Wizard404's cheats for every creature): whose cheat it is (for "an ally casts"), whom the
        // "has" If found on a side, the players without the mark, and the player with the most pips.
        ("Owner", "itself (whose cheat it is)"),
        ("Matched", "the one the If found"),
        ("Unmarked", "every player without the mark"),
        ("MostPips", "the player with the most pips"),
        // The .c1 commands' circles, for players and bosses alike (Niduus, 2026-10-23).
        ("p1", "its side's circle 1"), ("p2", "its side's circle 2"), ("p3", "its side's circle 3"), ("p4", "its side's circle 4"),
        ("e1", "the other side's circle 1"), ("e2", "the other side's circle 2"), ("e3", "the other side's circle 3"),
        ("e4", "the other side's circle 4"),
    ];

    internal static readonly (string Value, string Label)[] SCHOOLS = [
        ("", "any school"), ("Fire", "Fire"), ("Ice", "Ice"), ("Storm", "Storm"), ("Myth", "Myth"), ("Life", "Life"),
        ("Death", "Death"), ("Balance", "Balance"), ("Star", "Star"), ("Sun", "Sun"), ("Moon", "Moon"), ("Shadow", "Shadow"),
    ];

    internal static readonly (string Value, string Label)[] CATEGORIES = [
        ("", "any"), ("Damage", "damage"), ("Heal", "heal"), ("Charm", "charm (any)"), ("Ward", "ward (any)"),
        ("Charm,Ward", "charm or ward"), ("Shield", "shield"), ("Trap", "trap (jinx)"), ("Blade", "blade"), ("Weakness", "weakness (curse)"),
        ("Aura", "aura"), ("Global", "global (bubble)"), ("Aura,Global", "aura or global"), ("Charm,Global", "charm or global"), ("Minion", "minion"),
        ("Shadow", "shadow"), ("Enchantment", "enchantment"), ("Stun", "stun"), ("Steal", "steal"), ("Dispel", "dispel"),
        ("Polymorph", "polymorph"), ("Treasure", "treasure card"), ("DoT", "damage over time"), ("HoT", "heal over time"),
    ];

    private static readonly (string, string)[] s_charmKinds = [("All", "of any"), ("Good", "good"), ("Bad", "bad")];
    private static readonly (string, string)[] s_pipKinds = [("Normal", "normal"), ("Power", "power"), ("Shadow", "shadow"),
        // A school's own pips (Niduus, 2026-10-25: Crash the Bandit Coot spends his two matching pips).
        ("Fire", "Fire school"), ("Ice", "Ice school"), ("Storm", "Storm school"), ("Myth", "Myth school"), ("Life", "Life school"),
        ("Death", "Death school"), ("Balance", "Balance school")];
    private static readonly (string, string)[] s_pipsOf = [("", "it"), ("Enemy", "a player")];
    private static readonly (string, string)[] s_pipsKind = [("", "pips"), ("Shadow", "shadow pips")];
    private static readonly (string, string)[] s_creatureWho = [("", "the one the When is about"), ("Receiver", "whoever the spell was aimed at")];
    // AddCharm's cast animation: the game plays the casting of the cast spell's school, so these cast a spell of that school.
    private static readonly (string, string)[] s_castLooks = [("", "normal"), ("Sun", "Sun"), ("Shadow", "Shadow"), ("Moon", "Moon"), ("Star", "Star"),
        ("Fire", "Fire"), ("Ice", "Ice"), ("Storm", "Storm"), ("Myth", "Myth"), ("Life", "Life"), ("Death", "Death"), ("Balance", "Balance")];
    private static readonly (string, string)[] s_announceStyles = [("Box", "as a box"), ("Chat", "in chat"), ("Both", "as a box and in chat")];
    private static readonly (string, string)[] s_announceWho = [("", "everyone in the battle"), ("Players", "only the players' side")];
    private static readonly (string, string)[] s_addKinds = [("Blade", "blade"), ("Weakness", "weakness"), ("Shield", "shield"), ("Trap", "trap"), ("Absorb", "absorb"),
        ("DoT", "damage over time"), ("HoT", "heal over time"), ("Global", "global (more damage)"), ("HealGlobal", "global (more healing)"),
        ("GloomGlobal", "global (less healing)")];
    private static readonly (string, string)[] s_boostKinds = [("Damage", "damage"), ("Resist", "resist"), ("Heal", "healing"), ("Accuracy", "accuracy")];
    private static readonly (string, string)[] s_timings = [
        ("Now", "right away"), ("Before", "before my card"), ("After", "after my card"), ("First", "first in the round"), ("Last", "last in the round"),
    ];

    private static readonly (string, string)[] s_dispelThen = [("Retry", "retry")];
    private static readonly (string, string)[] s_fizzles = [("Landed", "lands"), ("Fizzled", "fizzles"), ("Any", "lands or fizzles")];
    private static readonly (string, string)[] s_sides = [("Us", "we go first"), ("Them", "they go first"), ("Swap", "swap who goes first")];
    private static readonly (string, string)[] s_hasWho = [("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at"), ("Self", "it"),
        ("AnyEnemy", "someone on the other side"), ("AnyAlly", "someone on its side"),
        // Niduus, 2026-10-25: someone without it, and a whole side counted together.
        ("EnemyWithout", "someone on the other side without"), ("AllyWithout", "someone on its side without"), ("AnyoneWithout", "anyone without"),
        ("EnemySide", "the other side together"), ("AllySide", "its side together")];
    private static readonly (string, string)[] s_hasKinds = [
        ("", "charm, ward or aura"), ("Shield", "shield"), ("Trap", "trap (jinx)"), ("Blade", "blade"), ("Weakness", "weakness (curse)"), ("Charm", "charm (any)"),
        ("Ward", "ward (any)"), ("Aura", "aura"), ("DoT", "damage over time"), ("HoT", "heal over time"),
    ];
    private static readonly (string, string)[] s_markWho = [("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at"), ("Self", "it"), ("AnyEnemy", "someone on the other side"),
        ("AllEnemies", "every player")];
    private static readonly (string, string)[] s_castBy = [("Creature", "this creature:"), ("Ally", "a random ally"), ("WeakestAlly", "its weakest ally"), ("StrongestAlly", "its strongest ally"), ("Summoner", "whoever summoned it")];
    private static readonly (string, string)[] s_anySchool = [("", "every school"), .. SCHOOLS[1..]];
    // Niduus, 2026-10-25 ("Expand on existing cheats"): the blocks of CombatResolver.MoreCheats.cs.
    private static readonly (string, string)[] s_removedFrom = [("", "it"), ("AnyAlly", "someone on its side"), ("AnyEnemy", "someone on the other side"), ("Anyone", "anyone")];
    private static readonly (string, string)[] s_spellDoes = [
        ("RemoveCharm", "removes a charm"), ("RemoveBlade", "removes a blade"), ("RemoveWeakness", "removes a weakness (curse)"),
        ("RemoveWard", "removes a ward"), ("RemoveShield", "removes a shield"), ("RemoveTrap", "removes a trap (jinx)"),
        ("RemoveOverTime", "removes an over-time"), ("RemoveDoT", "removes a damage over time"), ("RemoveHoT", "removes a heal over time"),
        ("RemoveAura", "removes an aura"), ("Steal", "steals an effect"), ("Push", "pushes an effect"), ("Swap", "swaps effects"),
        ("Detonate", "detonates over-times"), ("Dispel", "dispels"), ("Stun", "stuns"), ("Polymorph", "polymorphs"),
        ("Pips", "gives or takes pips"), ("Damage", "does damage"), ("Heal", "heals"),
    ];
    private static readonly (string, string)[] s_global = [("Up", "a"), ("None", "no")];
    private static readonly (string, string)[] s_pipsWho = [("", "it"), ("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at"),
        ("AnyEnemy", "someone on the other side"), ("AnyAlly", "someone on its side")];
    private static readonly (string, string)[] s_pipSchools = [("", "matching (any one school)"), ("Fire", "Fire"), ("Ice", "Ice"), ("Storm", "Storm"),
        ("Myth", "Myth"), ("Life", "Life"), ("Death", "Death"), ("Balance", "Balance")];
    private static readonly (string, string)[] s_circlesWho = [("", "the attacker"), ("Receiver", "whoever that spell was aimed at"), ("Self", "it")];

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

    // Creature ids as names, when the app has them.
    private static string CreatureList(List<string> ids) => ids.Count == 0 ? "(no creatures picked)"
        : string.Join(", ", ids.Select(w => uint.TryParse(w, out var id) && CreatureNames?.Invoke(id) is { } name ? name : w));

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
        // When... (first made for bosses; a player cheat's "it" is you, and its "players" the creatures)
        new("W.BattleStart", BlockKind.When, "flag", "the battle starts", "Its first round. A boss brought in later counts from its own first round.",
            "When the battle starts", [], _ => "When the battle starts"),
        new("W.RoundStart", BlockKind.When, "clock", "a round starts", "The start of a round, before anyone moves. Pick the round, every few rounds, or both.",
            "At the start of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the start of {Rounds(b)}"),
        new("W.FromRound", BlockKind.When, "clock", "from round … on", "Every round from this round on, such as an enrage after round 10. \"Every\" above 1 skips rounds between.",
            "At the start of every round from round {Round} on, every {Every} rounds", [F("Round", CheatFieldType.Number, 10, max: 999), F("Every", CheatFieldType.Number, 1, max: 99)],
            b => $"Every {(b.Number("Every") > 1 ? $"{b.Number("Every")} rounds" : "round")} from round {b.Number("Round")} on"),
        new("W.RoundEnd", BlockKind.When, "clockend", "a round ends", "The end of a round, after everyone has moved.",
            "At the end of {Rounds}", [F("Rounds", CheatFieldType.Rounds)], b => $"At the end of {Rounds(b)}"),
        new("W.HealthBelow", BlockKind.When, "heart", "its health drops", "The first time its health (in a player cheat, yours) falls under the mark (once each phase). Works even after it is defeated, for a second life.",
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
        new("W.Pips", BlockKind.When, "pips", "pips reach", "At the start of a round, when it (or any player) has at least this many pips. A power pip counts as one; \"shadow pips\" counts only those. With \"a player\", it goes off for each player who has them, and \"the attacker\" (and \"the one the If found\") is that player.",
            "When {PipsOf} has at least {Pips} {PipsKind}", [F("PipsOf", CheatFieldType.Choice, "", s_pipsOf), F("Pips", CheatFieldType.Number, 5, max: 14), F("PipsKind", CheatFieldType.Choice, "", s_pipsKind)],
            b => $"When {LabelOf(s_pipsOf, b.Text("PipsOf"))} has {b.Number("Pips")} or more {LabelOf(s_pipsKind, b.Text("PipsKind"))}"),

        new("W.EnemyJoins", BlockKind.When, "users", "a player joins", "Someone comes into the battle on the other side after this round (2: from round 3 on), such as a wizard who is late. \"The attacker\" is the newcomer. A summoned minion counts only with \"minions too\".",
            "When a player joins the battle after round {Round} {Minions}", [F("Round", CheatFieldType.Number, 2, max: 999), new("Minions", CheatFieldType.Toggle, false, Label: "minions too")],
            b => $"When a player joins after round {b.Number("Round")}{(b.Flag("Minions") ? " (minions too)" : "")}"),
        new("W.AllyHealthBelow", BlockKind.When, "heart", "an ally's health drops", "Someone else on its side first falls under the mark (once for each of them). \"The attacker\" is that ally. Add \"this creature\" to watch only one of them.",
            "When an ally's health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When an ally drops below {N(b.Number("Health"))} health" : $"When an ally drops below {b.Number("Percent")}% health"),

        new("W.AllyHit", BlockKind.When, "hit", "an ally is hit", "A spell from the other side hurts someone else on its side. \"The one hit\" is that ally, \"the attacker\" the one who cast it. Pick the school and the kind of card, or any.",
            "When an ally is hit by a {School} {Category} spell {Count}", [F("School", CheatFieldType.School), F("Category", CheatFieldType.Category), F("Count", CheatFieldType.MatchCount)],
            b => $"When an ally is hit by {(b.Text("School").Length > 0 || b.Text("Category").Length > 0 ? Spell(b, "a spell") : "anything")}"),

        new("W.AllyCasts", BlockKind.When, "sparkle", "an ally's spell lands", "Someone else on its side casts a spell that lands. \"The attacker\" is that ally. The Rat answers The Scorpion's Enfeeble: an ally's spell lands, \"these spells\" Enfeeble, \"this creature\" The Scorpion, cast Shade Scimitars on the attacker. Add \"aimed at it\" for an ally's spell on it (Buck Gordon's Convert Blade).",
            "When an ally casts a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When an ally casts {Spell(b, "a spell")}"),
        new("W.EffectRemoved", BlockKind.When, "stop", "an effect is removed", "Someone else's spell takes a charm, ward, aura or over-time off it (or off someone on a side), not one used up by a hit. \"The attacker\" is the one whose spell took it, \"the one hit\" the one it came off. Xibalba: aura removed, cast Blessing on itself. Empress of Flames: a DoT removed from someone on the other side, only if the other side together has no DoT, wait 2 rounds, cast Scald.",
            "When {RemovedFrom} loses a {RemovedKind} from {RemovedSpell} to a spell",
            [F("RemovedFrom", CheatFieldType.Choice, "", s_removedFrom), F("RemovedKind", CheatFieldType.Choice, "", s_hasKinds), F("RemovedSpell", CheatFieldType.Words, "")],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} loses {(b.Text("RemovedSpell").Length > 0 ? b.Text("RemovedSpell") + " " : "a ")}{LabelOf(s_hasKinds, b.Text("RemovedKind"))}"),

        // When... (first made for the player cheats; a boss's "I" is the boss)
        new("W.ICast", BlockKind.When, "sparkle", "I cast", "Your spell, just before it lands. Pick the kind of card and school, and add a \"these spells\" block to name spells.",
            "When I cast a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When I cast {Spell(b, "a spell")}"),
        new("W.ICrit", BlockKind.When, "bolt", "I crit", "Your spell lands as a critical hit (or a critical heal).",
            "When I land a critical {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When I crit with {Spell(b, "a spell")}"),
        new("W.IPass", BlockKind.When, "hourglass", "I pass", "You pass your turn.", "When I pass", [], _ => "When I pass"),
        new("W.SideFizzles", BlockKind.When, "bolt", "my side fizzles", "A spell of yours or of someone on your side fizzles. \"The attacker\" is the one whose spell fizzled. For the other side's fizzles use \"a player casts\" with fizzled.",
            "When I or an ally fizzle a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When I or an ally fizzle {Spell(b, "a spell")}"),
        new("W.EnemyCasts", BlockKind.When, "hit", "an enemy casts", "An enemy's spell, just before it lands. \"Right away\" casts yours before it.",
            "When an enemy casts a {Category} spell of {School} {Count}", [F("Category", CheatFieldType.Category), F("School", CheatFieldType.School), F("Count", CheatFieldType.MatchCount)],
            b => $"When an enemy casts {Spell(b, "a spell")}"),
        new("W.MyHealthBelow", BlockKind.When, "heart", "my health drops", "The first time in a battle your health falls under the mark.",
            "When my health drops below {Health}", [F("Health", CheatFieldType.Health)],
            b => b.Number("Health") > 0 ? $"When my health drops below {N(b.Number("Health"))}" : $"When my health drops below {b.Number("Percent")}%", "alias"),
        new("W.MyPips", BlockKind.When, "pips", "my pips reach", "At the start of a round, when you have at least this many pips. A power pip counts as one.",
            "When I have at least {Pips} pips", [F("Pips", CheatFieldType.Number, 7, max: 14)], b => $"When I have {b.Number("Pips")} or more pips", "alias"),
        new("W.Other", BlockKind.When, "bolt", "something else", "A When this app does not know, kept as the file has it.",
            "When {When}", [F("When", CheatFieldType.Words)], b => $"When {b.Text("When")}"),

        // Passive... (Niduus, 2026-10-24: always true of whoever the cheat is of, with no When)
        new("P.Circle", BlockKind.Passive, "target", "its circle", "It always takes this circle of its side when it comes into a battle (at the start, walking in, or summoned). If someone already stands there, it takes the free circle nearest to it. Circle 1 is the first of its side, as the game numbers them.",
            "Always stand on circle {Circle}", [new("Circle", CheatFieldType.Number, 4, Max: 4, Min: 1)],
            b => $"Always on circle {Math.Clamp(b.Number("Circle"), 1, 4)}"),
        new("P.Minions", BlockKind.Passive, "users", "comes with", "It comes into a battle with these creatures on its side (creature ids, with commas between), and this many more picked at random from the second list. When a battle starts against it, the room's own creatures of those kinds join first and the rest are summoned; when it joins a battle later, they are summoned with it. Add \"how many players\" to make it a cheat of its own: those creatures come too once that many players are in the battle, in its first round (such as an extra minion for 3 wizards).",
            "Come into a battle with {Brings}, and {BringsCount} picked at random from {BringsRandom}",
            [F("Brings", CheatFieldType.Cards), F("BringsCount", CheatFieldType.Number, 0, max: 3), F("BringsRandom", CheatFieldType.Cards)],
            b => $"Comes with {CreatureList(b.List("Brings"))}{(b.Number("BringsCount") > 0 && b.List("BringsRandom").Count > 0 ? $" and {b.Number("BringsCount")} of {CreatureList(b.List("BringsRandom"))}" : "")}"),

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
        new("I.HealthAbove", BlockKind.If, "heart", "health above", "Only while its health (for a player cheat, yours) is above this share of the most.",
            "Only while health is above {HealthAbove}", [F("HealthAbove", CheatFieldType.Percent, 50, max: 100)], b => $"while above {b.Number("HealthAbove")}% health"),
        new("I.HealthUnder", BlockKind.If, "heart", "health under", "Only while its health (for a player cheat, yours) is under this share of the most.",
            "Only while health is under {HealthUnder}", [F("HealthUnder", CheatFieldType.Percent, 50, max: 100)], b => $"while under {b.Number("HealthUnder")}% health"),
        new("I.Rounds", BlockKind.If, "clock", "in these rounds", "Only in these rounds of the battle. Leave the second box at 0 for no end.",
            "Only from round {FromRound} to round {ToRound}", [F("FromRound", CheatFieldType.Number, 5, max: 999), F("ToRound", CheatFieldType.Number, 0, max: 999)],
            b => b.Number("ToRound") > 0 ? $"in rounds {b.Number("FromRound")}-{b.Number("ToRound")}" : $"from round {b.Number("FromRound")} on"),
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
            "Only if the spell hits one target", [], _ => "only single-target spells"),
        new("I.NotOnCaster", BlockKind.If, "target", "on someone else", "Only a spell aimed at someone other than the one who cast it, such as a blade on another wizard.",
            "Only if the spell is aimed at someone else", [], _ => "only spells on someone else"),
        new("I.Pips", BlockKind.If, "pips", "pips left", "For \"at the end of a round\": only if it (or any player) still has at least this many pips then.",
            "Only if {PipsOf} has at least {Pips} {PipsKind} left", [F("PipsOf", CheatFieldType.Choice, "", s_pipsOf), F("Pips", CheatFieldType.Number, 1, max: 14), F("PipsKind", CheatFieldType.Choice, "", s_pipsKind)],
            b => $"only if {LabelOf(s_pipsOf, b.Text("PipsOf"))} has {b.Number("Pips")} or more {LabelOf(s_pipsKind, b.Text("PipsKind"))} left"),
        new("I.NotCategory", BlockKind.If, "stop", "not this kind", "Only a spell that is not of this kind, such as a wrong answer to a riddle that wants a blade.",
            "Only if the spell is not a {NotCategory} spell", [F("NotCategory", CheatFieldType.Category)], b => $"only spells that are not {LabelOf(CATEGORIES, b.Text("NotCategory"))}"),
        new("I.CasterSchool", BlockKind.If, "school", "player's school", "Only when the player who set it off is a wizard of this school.",
            "Only if the player is a {CasterSchool} wizard", [F("CasterSchool", CheatFieldType.Choice, "Fire", SCHOOLS[1..])], b => $"only {b.Text("CasterSchool")} wizards"),
        new("I.Fizzle", BlockKind.If, "stop", "lands or fizzles", "Whether the player's spell has to land, fizzle, or either, for a player-cast When. Without this block only a spell that lands counts.",
            "Only if the spell {Fizzle}", [F("Fizzle", CheatFieldType.Choice, "Fizzled", s_fizzles)],
            b => $"only if the spell {LabelOf(s_fizzles, b.Text("Fizzle"))}"),
        new("I.Dispelled", BlockKind.If, "stop", "if dispelled", "When a Dispel on it stops one of its spells, the spell fizzles and is cast again: this many more times, or until it gets through with \"continue until cast\" (20 tries at most). Cheats that were dispel-proof get this block with \"continue until cast\".",
            "If dispelled, {DispelThen} {DispelRetries} more times {RetryUntilCast}",
            [F("DispelThen", CheatFieldType.Choice, "Retry", s_dispelThen), F("DispelRetries", CheatFieldType.Number, 1, max: 20),
             new("RetryUntilCast", CheatFieldType.Toggle, false, Label: "continue until cast")],
            b => b.Flag("RetryUntilCast") ? "if dispelled, retry until cast" : $"if dispelled, retry {b.Number("DispelRetries")} more times"),
        new("I.Spells", BlockKind.If, "cards", "these spells", "Only these spells, by name, with commas between. \"Any version of\" makes Scald mean every Scald: its treasure card, item card, a monster's copy and the rest. \"Exactly\" means only spells with that very name.",
            "Only {AnyVersion} these spells: {Spells} {Count}",
            [new("AnyVersion", CheatFieldType.VersionChoice, true), F("Spells", CheatFieldType.Cards), F("Count", CheatFieldType.MatchCount)],
            b => $"only {string.Join(", ", b.List("Spells"))}{(b.Flag("AnyVersion") ? " (any version)" : "")}"),
        new("I.Cost", BlockKind.If, "pips", "pip cost", "Only spells that cost this many pips. Leave the top at 0 for no top; -1 in the top means only 0-pip spells (an X spell or a 0-pip card).",
            "Only spells that cost {CostMin} to {CostMax} pips", [F("CostMin", CheatFieldType.Number, 4, max: 14), new("CostMax", CheatFieldType.Number, 0, Max: 14, Min: -1)],
            b => b.Number("CostMax") < 0 ? "only 0-pip spells" : b.Number("CostMax") > 0 ? $"only {b.Number("CostMin")}-{b.Number("CostMax")} pip spells" : $"only spells of {b.Number("CostMin")}+ pips"),
        new("I.CardType", BlockKind.If, "cards", "card type", "Only cards of this type, as the game names it: Damage, Heal, Ward, Charm, Aura, Global, Steal, Manipulation...",
            "Only cards of type {Kind}", [F("Kind", CheatFieldType.Words, "Damage")], b => $"only {b.Text("Kind")} cards"),

        new("I.Creature", BlockKind.If, "users", "this creature", "Only when the one the When is about is this creature: the one that fell, whose health dropped or that joined, else the one whose spell or pass set it off. With \"anyone but it\", only when it is someone else. For a When about a spell, \"whoever the spell was aimed at\" looks at the one it went on (a jinx on a Sea Lion) instead of its caster.",
            "Only if {CreatureWho} is {Creature} {NotCreature}", [F("CreatureWho", CheatFieldType.Choice, "", s_creatureWho), F("Creature", CheatFieldType.Creature), new("NotCreature", CheatFieldType.Toggle, false, Label: "anyone but it")],
            b => $"only if {(b.Text("CreatureWho").Length > 0 ? LabelOf(s_creatureWho, b.Text("CreatureWho")) : "it")} is {(b.Flag("NotCreature") ? "not " : "")}{Creature(b)}"),
        // If... (Niduus, 2026-10-24: the gauntlets' cheats)
        new("I.Has", BlockKind.If, "shield", "has a charm or ward", "Only if that one has a charm, ward, aura or over-time of this kind on them, or one from this spell (any version), at least this many (0 or 1: one; Coatl answers a fourth blade). With \"has none\", only if they have fewer: Kurokage answers a wizard who attacks with no shield up. \"Someone on the other side\" or \"someone on its side\" looks at everyone there (\"has none\": no one there), and \"the one the If found\" is the target of its Do blocks.",
            "Only if {HasWho} has {HasCount} {HasKind} from {HasSpell} {HasNot}",
            [F("HasWho", CheatFieldType.Choice, "Caster", s_hasWho), F("HasCount", CheatFieldType.Number, 0, max: 99), F("HasKind", CheatFieldType.Choice, "Shield", s_hasKinds), F("HasSpell", CheatFieldType.Words, ""), new("HasNot", CheatFieldType.Toggle, false, Label: "has none")],
            b => $"only if {LabelOf(s_hasWho, b.Text("HasWho"))} {(b.Flag("HasNot") ? "has no" : b.Number("HasCount") > 1 ? $"has {b.Number("HasCount")} or more" : "has a")} {(b.Text("HasSpell").Length > 0 ? b.Text("HasSpell") + " " : "")}{LabelOf(s_hasKinds, b.Text("HasKind"))}"),
        new("I.Allies", BlockKind.If, "users", "how many allies", "Only while this many others stand on its side. Leave the top at 0 for no top.",
            "Only while {AlliesMin} to {AlliesMax} others stand on its side", [F("AlliesMin", CheatFieldType.Number, 1, max: 7), F("AlliesMax", CheatFieldType.Number, 0, max: 7)],
            b => b.Number("AlliesMax") > 0 ? $"with {b.Number("AlliesMin")}-{b.Number("AlliesMax")} allies" : $"with {b.Number("AlliesMin")}+ allies"),
        new("I.AlliesUp", BlockKind.If, "users", "these allies", "Only while each creature of the first list stands on its side, and none of the second (creature ids, with commas between): Yoshinobu's chain changes as Hisamoto and Kensaburo fall.",
            "Only while {AlliesUp} stand and {AlliesDown} are down", [F("AlliesUp", CheatFieldType.Cards), F("AlliesDown", CheatFieldType.Cards)],
            b => string.Join(", ", new[] { b.List("AlliesUp").Count > 0 ? $"while {CreatureList(b.List("AlliesUp"))} stand" : "",
                b.List("AlliesDown").Count > 0 ? $"once {CreatureList(b.List("AlliesDown"))} are down" : "" }.Where(w => w.Length > 0)) is { Length: > 0 } w ? w : "(no creatures picked)"),
        new("I.Marked", BlockKind.If, "target", "marked", "Only if that one has a mark a \"mark\" block put on. With \"does not\", only if not; with \"someone on the other side\", whether anyone there has it (\"does not\": no one there has it).",
            "Only if {MarkWho} has the mark {Mark} {NotMarked}", [F("MarkWho", CheatFieldType.Choice, "Caster", s_markWho), F("Mark", CheatFieldType.Words, "Tag"), new("NotMarked", CheatFieldType.Toggle, false, Label: "does not")],
            b => $"only if {LabelOf(s_markWho, b.Text("MarkWho"))} {(b.Flag("NotMarked") ? "does not have" : "has")} the mark \"{b.Text("Mark")}\""),
        new("I.MultiTarget", BlockKind.If, "users", "many targets", "Only a spell that hits a whole side, not one target.",
            "Only if the spell hits a whole side", [], _ => "only spells on a whole side"),

        new("I.SpellDoes", BlockKind.If, "cards", "the spell does", "Only a spell that does this, judged from its own effects, so any card that does it counts: Rotten Rob answers \"another spell that clears DoTs\" (a player casts, the spell removes a damage over time: Triage on every player). With \"does not\", only a spell that does not. A file can list several with commas (RemoveDoT, RemoveTrap).",
            "Only if the spell {SpellDoes} {NotSpellDoes}", [F("SpellDoes", CheatFieldType.Choice, "RemoveCharm", s_spellDoes), new("NotSpellDoes", CheatFieldType.Toggle, false, Label: "does not")],
            b => $"only spells that {(b.Flag("NotSpellDoes") ? "do not " : "")}{LabelOf(s_spellDoes, b.Text("SpellDoes")).Replace("removes", b.Flag("NotSpellDoes") ? "remove" : "removes")}"),
        new("I.Enchanted", BlockKind.If, "sparkle", "card's enchantment", "Only a card enchanted with one of these (names with commas, any version), Any (any enchantment) or None. With \"is not\", only a card that is not: Despair answers a blade unless it is enchanted with Aegis, and a Feint unless Indemnity or Melt.",
            "Only if the card is enchanted with {Enchant} {NotEnchant}", [F("Enchant", CheatFieldType.Words, "Aegis"), new("NotEnchant", CheatFieldType.Toggle, false, Label: "is not")],
            b => $"only cards {(b.Flag("NotEnchant") ? "not " : "")}enchanted with {(b.Text("Enchant").Length > 0 ? b.Text("Enchant") : "(nothing picked)")}"),
        new("I.Global", BlockKind.If, "school", "a global is up", "Only while a global (a battlefield bubble) is up, or with \"no\", while none is. Pick its school, or name its spell (any version): Winged Sorrow heals back to full only under its own Winged Sorrow - Storm Global.",
            "Only while {Global} global of {GlobalSchool} from {GlobalSpell} is up", [F("Global", CheatFieldType.Choice, "Up", s_global), F("GlobalSchool", CheatFieldType.Choice, "", SCHOOLS), F("GlobalSpell", CheatFieldType.Words, "")],
            b => $"only while {(b.Text("Global").Equals("None", StringComparison.OrdinalIgnoreCase) ? "no" : "a")} {(b.Text("GlobalSchool").Length > 0 ? b.Text("GlobalSchool") + " " : "")}{(b.Text("GlobalSpell").Length > 0 ? b.Text("GlobalSpell") + " " : "")}global is up"),
        new("I.SchoolPips", BlockKind.If, "pips", "school pips", "Only if that one has at least this many school pips of a school, or of any one school (\"matching\"). \"Its most\" asks for the school they have the most pips of. Crash the Bandit Coot: 2 matching Fire school pips, Sun Serpent. Someone on a side is \"the one the If found\".",
            "Only if {SchoolPipsWho} has {SchoolPips} {SchoolPipsSchool} school pips {SchoolPipsMost}",
            [F("SchoolPipsWho", CheatFieldType.Choice, "", s_pipsWho), F("SchoolPips", CheatFieldType.Number, 2, max: 7), F("SchoolPipsSchool", CheatFieldType.Choice, "", s_pipSchools),
             new("SchoolPipsMost", CheatFieldType.Toggle, false, Label: "its most")],
            b => $"only if {LabelOf(s_pipsWho, b.Text("SchoolPipsWho"))} has {b.Number("SchoolPips")}+ {(b.Text("SchoolPipsSchool").Length > 0 ? b.Text("SchoolPipsSchool") : "matching")} school pips{(b.Flag("SchoolPipsMost") ? " (its most)" : "")}"),
        new("I.PipsNow", BlockKind.If, "pips", "pips now", "Only if that one has this many pips at the moment the When goes off, for any When (a power pip counts as one). Leave the top at 0 for no top; -1 in the top means no pips. Locomaton answers Conductor's Whistle with Mass Feint at 6 pips and Power Nova at 7.",
            "Only if {PipsWho} has {PipsMin} to {PipsMax} pips now", [F("PipsWho", CheatFieldType.Choice, "", s_pipsWho), F("PipsMin", CheatFieldType.Number, 6, max: 14), new("PipsMax", CheatFieldType.Number, 0, Max: 14, Min: -1)],
            b => $"only if {LabelOf(s_pipsWho, b.Text("PipsWho"))} has {(b.Number("PipsMax") < 0 ? "no" : b.Number("PipsMax") > 0 ? $"{b.Number("PipsMin")}-{b.Number("PipsMax")}" : $"{b.Number("PipsMin")}+")} pips"),
        new("I.Circle", BlockKind.If, "target", "in circle", "Only if that one stands in one of these circles of its own side (1-4 as the game numbers them, with commas between). Luska Charmbeak answers a single-target spell from a wizard not in the first circle: circles 2, 3, 4.",
            "Only if {CirclesWho} stands in circle {Circles}", [F("CirclesWho", CheatFieldType.Choice, "", s_circlesWho), F("Circles", CheatFieldType.Words, "2, 3, 4")],
            b => $"only if {LabelOf(s_circlesWho, b.Text("CirclesWho"))} is in circle {(b.Text("Circles").Length > 0 ? b.Text("Circles") : "(none picked)")}"),

        // Do... (first made for bosses)
        new("D.Cast", BlockKind.Do, "sparkle", "cast a spell", "A free spell: no pips, it never fizzles and works through stun. Silent: no casting animation. A Dispel on the boss stops it; add the \"if dispelled\" If block to cast it again.",
            "Cast {Spell} on {Target} {Silent}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), new("Silent", CheatFieldType.Toggle, false, Label: "silent")],
            b => $"cast {SpellName(b)}{Target(b)}{(b.Flag("Silent") ? " (silent)" : "")}"),
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
        new("D.RemoveEffects", BlockKind.Do, "broom", "remove effects", "Takes hanging effects of a kind (shields, traps, blades, weaknesses, any charm or ward, auras, damage or heal over time) off the target, newest first. 0 means all of them. Name a spell to take off only its effects (any version). Protected ones stay.",
            "Remove {Count} {Kind} from {Target} made by {Spell}", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "Ward", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("Spell", CheatFieldType.Words, "")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {(b.Text("Spell").Length > 0 ? b.Text("Spell") + " " : "")}{LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b).Replace(" on ", " from ")}".Replace("  ", " ")),
        new("D.RemoveAuras", BlockKind.Do, "broom", "remove auras", "Takes auras (and boosts from cheats) off.",
            "Remove the auras of {Target}", [F("Target", CheatFieldType.Target, "AllEnemies")], b => $"remove the auras{Target(b).Replace(" on ", " of ")}"),
        new("D.AddEffects", BlockKind.Do, "shield", "add a spell's charms", "The charms and wards of a spell (Tower Shield, Fire Blade, Feint...) go on at once, with no casting.",
            "Put the charms and wards of {Spell} on {Target}", [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target, "Self")],
            b => $"put the effects of {SpellName(b)}{Target(b)}"),
        new("D.AddCharm", BlockKind.Do, "shield", "add an effect of your own size", "A blade, weakness, shield, trap or absorb of your own size goes on at once, and is used up like a real one: the amount is a percent (hit points for an absorb), so \"55, Storm, blade\" is a +55% Storm blade. A damage or heal over time is the amount in all, spread over the rounds (3 if 0); pick its school. The game only draws an effect it sees cast, so a real spell of that kind and school is cast for it (Shadowblade for a Shadow blade, Balanceblade for all schools...) and then made your size: the game shows the real spell's number, the server uses yours. \"Shown as\" (optional) picks that spell yourself; two of the same spell then count once on a hit, as real ones do. A damage or heal over time is not drawn. After \"remove effects\" and \"only if something came off\" it is a reward: \"Clear DoT on the target for +55% Storm blade on me\". Cast animation: the game plays the casting of the cast spell's own school, so Sun or Shadow casts a real Sun or Shadow spell of that kind (example: 666, Shadow, blade, on me, Sun animation casts Sun Blade, then makes it a +666% Shadow blade; the game shows the Sun Blade's icon). Sun has a blade, shield and absorb, Shadow all five kinds, Moon a blade, Star a blade, shield and absorb; any other pick is cast normally. With \"shown as\" filled in, that spell's school decides. A global is a battlefield bubble: it takes the current one's place, as a real one does, and its school's real bubble is cast for it (Wyldfire, Balefrost, Darkwind, Blood Moon, Circle of Thorns, Deadzone, Balance of Power, Shadow; Power Play for all schools; Sanctuary for more healing, Doom and Gloom for less), then made your size: \"100, Storm, global\" is a +100% Storm global. The target does not matter for a global. A yellow note shows when the game will show an effect of another school than yours (it then uses it on other hits than the server does).",
            "Put a {Amount} {School} {Kind} on {Target} for {Rounds} rounds, shown as {Spell}, with the {Look} cast animation", [F("Amount", CheatFieldType.Percent, 50, max: 100_000), F("School", CheatFieldType.Choice, "", s_anySchool), F("Kind", CheatFieldType.Choice, "Blade", s_addKinds), F("Target", CheatFieldType.Target, "Self"), F("Rounds", CheatFieldType.Number, 0, max: 99), F("Spell", CheatFieldType.Words, ""), F("Look", CheatFieldType.Choice, "", s_castLooks)],
            b => b.Text("Kind") is "Global" or "HealGlobal" or "GloomGlobal"
                ? $"put up a {(b.Text("Kind") == "GloomGlobal" ? "-" : "+")}{b.Number("Amount")}% {(b.Text("Kind") == "Global" ? SchoolWord(b) : "")}{LabelOf(s_addKinds, b.Text("Kind"))}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}{(b.Text("Look").Length > 0 && b.Text("Spell").Length == 0 ? $", {b.Text("Look")} cast" : "")}"
                : b.Text("Kind") is "DoT" or "HoT"
                ? $"put a {N(b.Number("Amount"))} {SchoolWord(b)}{LabelOf(s_addKinds, b.Text("Kind"))} over {Math.Max(1, b.Number("Rounds") > 0 ? b.Number("Rounds") : 3)} rounds{Target(b)}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}"
                : $"put a {b.Number("Amount")}{(b.Text("Kind") == "Absorb" ? "" : "%")} {SchoolWord(b)}{LabelOf(s_addKinds, b.Text("Kind"))}{Target(b)}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}{(b.Text("Look").Length > 0 && b.Text("Spell").Length == 0 ? $", {b.Text("Look")} cast" : "")}"),
        new("D.IfCleared", BlockKind.Do, "broom", "only if something came off", "Put it after \"remove effects\" (or remove charms, wards or auras): the next blocks (how many) go off only if at least this many effects came off. With \"once for each\" they go off once for each effect that came off. Example, Reap the Whirlwind's \"Clear DoT for +55% Storm blade\": remove 1 damage over time from whoever the spell was aimed at, then this block (next 1 block), then put a 55% Storm blade on itself.",
            "Do the next {Count} blocks only if at least {Amount} effects came off {Each}", [new("Count", CheatFieldType.Number, 1, Max: 20, Min: 1), new("Amount", CheatFieldType.Number, 1, Max: 99, Min: 1), new("Each", CheatFieldType.Toggle, false, Label: "once for each")],
            b => b.Flag("Each") ? $"(the next {Math.Max(1, b.Number("Count"))} once for each effect that came off)"
                : $"(the next {Math.Max(1, b.Number("Count"))} only if {Math.Max(1, b.Number("Amount"))} or more came off)"),
        new("D.Boost", BlockKind.Do, "bolt", "boost or enrage", "Raises (or with a minus number, lowers) its damage, resist, healing or accuracy for some rounds; 0 rounds is the rest of the battle. It is never used up and stacks: on a \"from round … on\" cheat it is an enrage.",
            "Change the {Kind} of {Target} by {Amount} for {School} spells for {Rounds} rounds", [F("Kind", CheatFieldType.Choice, "Damage", s_boostKinds), F("Target", CheatFieldType.Target, "Self"), new("Amount", CheatFieldType.Number, 10, Max: 1000, Min: -100), F("School", CheatFieldType.Choice, "", s_anySchool), F("Rounds", CheatFieldType.Number, 0, max: 99)],
            b => $"{LabelOf(s_boostKinds, b.Text("Kind"))} {(b.Number("Amount") >= 0 ? "+" : "")}{b.Number("Amount")}%{(b.Text("School").Length > 0 ? $" ({b.Text("School")})" : "")}{Target(b)} for {RoundsFor(b)}"),
        new("D.Immune", BlockKind.Do, "shield", "become immune", "No damage from that school (or any) for some rounds; 0 rounds is the rest of the battle. Pierce still gets through, as against a shield.",
            "Make {Target} immune to {School} damage for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Self"), F("School", CheatFieldType.Choice, "Fire", s_anySchool), F("Rounds", CheatFieldType.Number, 2, max: 99)],
            b => $"immune to {SchoolWord(b)}damage{Target(b)} for {RoundsFor(b)}"),
        new("D.DamageCap", BlockKind.Do, "shield", "cap damage", "No hit does more than this much damage to the target, for some rounds (0 is the rest of the battle), as Equalize does for a whole battle. 0 damage takes the cap off. Crash the Bandit Coot: defeated the first time, back to full, cap his damage at 4,000.",
            "Cap the damage {Target} takes at {Amount} for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.Number, 4000), F("Rounds", CheatFieldType.Number, 0, max: 99)],
            b => b.Number("Amount") > 0 ? $"cap damage{Target(b)} at {N(b.Number("Amount"))} for {RoundsFor(b)}" : $"take the damage cap off{Target(b)}"),
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
        new("D.FirstTurn", BlockKind.Do, "clock", "turn order", "Which side acts first, as .wegofirst and .theygofirst do. \"We\" is the cheat's own side (the boss's, or yours in a player cheat). At the start of a round it counts from that round's spells; later in a round, from the next round. It lasts until the battle ends or the order is changed again.",
            "Turn order: {Side}", [F("Side", CheatFieldType.Choice, "Us", s_sides)], b => LabelOf(s_sides, b.Text("Side"))),
        new("D.SetPhase", BlockKind.Do, "layers", "next phase", "The fight moves on to this phase from the next round. Use In phase blocks to give each phase its own cheats.",
            "Move on to phase {Phase}", [F("Phase", CheatFieldType.Number, 2, max: 99)], b => $"move on to phase {b.Number("Phase")}"),
        new("D.Say", BlockKind.Do, "quote", "say a line", "A line from the game shown over the boss in battle (over you, in a player cheat). Type a few of its words or its number (Phrases tab).",
            "Say {Line}", [F("Line", CheatFieldType.Line)], b => $"say \"{b.Text("Line")}\""),
        new("D.Chat", BlockKind.Do, "chat", "chat message", "Any words you like, sent to every player's chat window (in a player cheat, to your own).",
            "Tell every player in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell everyone \"{b.Text("Text")}\""),
        new("D.Announce", BlockKind.Do, "chat", "announce to the battle", "A server announcement, as the Announcement card sends it, but only to the wizards in this battle: a message box with \"Announcement\" on top, an \"Announcement:\" chat line, or both. {boss} in the words is the boss (or you, in a player cheat), {player} the wizard who set it off, {round} the round. Example: a Round 3 cheat that does this with \"The ground trembles...\" as a box. Unlike \"say a line\" (a game line over the boss) and \"chat message\" (chat with the boss's name in front), it reads as from the server.",
            "Announce {Text} to {Who} {Style}", [F("Text", CheatFieldType.Words, ""), F("Who", CheatFieldType.Choice, "", s_announceWho), F("Style", CheatFieldType.Choice, "Box", s_announceStyles)],
            b => $"announce \"{b.Text("Text")}\" to {LabelOf(s_announceWho, b.Text("Who"))} {LabelOf(s_announceStyles, b.Text("Style").Length > 0 ? b.Text("Style") : "Box")}"),
        new("D.Pips", BlockKind.Do, "pips", "give or take pips", "Gives pips (to fill empty slots first) or takes them away. A school's pips too: Crash the Bandit Coot spends his two matching Fire pips (take 2 Fire school pips from itself) after his Crash Attack.",
            "{Amount} {Kind} pips, for {Target}", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Self")],
            b => $"{(b.Number("Amount") < 0 ? "take" : "give")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", b.Number("Amount") < 0 ? " from " : " to ")}"),
        new("D.StealPips", BlockKind.Do, "pips", "steal pips", "Takes pips from the target and gives them to the boss.",
            "Steal {Amount} {Kind} pips from {Target}", [F("Amount", CheatFieldType.Number, 2, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Caster")],
            b => $"steal {b.Number("Amount")} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", " from ")}"),
        new("D.Defeat", BlockKind.Do, "skull", "defeat itself", "The boss is defeated after the cheat (its drops still come).",
            "Defeat itself", [], _ => "defeat itself"),
        new("D.RandomOne", BlockKind.Do, "dice", "one spell at random", "Of all its Cast blocks, only one is cast each time, picked at random.",
            "Cast only one of these spells, picked at random", [], _ => "(only one spell, at random)"),
        // Do... (Niduus, 2026-10-24: the gauntlets' cheats)
        new("D.CastBy", BlockKind.Do, "sparkle", "an ally casts", "Someone else on its side casts a free spell, as \"cast a spell\" does: one of this creature's kind (Yoshinobu's chain: Hisamoto casts Unicorn), or an ally. Nothing happens when no such one stands.",
            "Have {By} {Creature} cast {Spell} on {Target} {Silent}",
            [F("By", CheatFieldType.Choice, "Creature", s_castBy), F("Creature", CheatFieldType.Creature), F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), new("Silent", CheatFieldType.Toggle, false, Label: "silent")],
            b => $"{(b.Text("By") is "" or "Creature" ? Creature(b) : LabelOf(s_castBy, b.Text("By")))} casts {SpellName(b)}{Target(b)}{(b.Flag("Silent") ? " (silent)" : "")}"),
        new("D.Mark", BlockKind.Do, "target", "mark", "Puts a mark of this name on the target (a random player if none is picked), for the \"marked\" If block. It shows nothing in the game; add a line or a spell for that.",
            "Put the mark {Mark} on {Target}", [F("Mark", CheatFieldType.Words, "Tag"), F("Target", CheatFieldType.Target, "Enemy")],
            b => $"mark \"{b.Text("Mark")}\"{Target(b)}"),
        new("D.Unmark", BlockKind.Do, "broom", "take a mark off", "Takes the mark of this name off the target (whoever has it, if none is picked).",
            "Take the mark {Mark} off {Target}", [F("Mark", CheatFieldType.Words, "Tag"), F("Target", CheatFieldType.Target, "Marked")],
            b => $"take the mark \"{b.Text("Mark")}\"{Target(b).Replace(" on ", " off ")}"),
        new("D.InTurn", BlockKind.Do, "list", "in turn", "Of the next choices (how many: 0 is all of them), the first goes off the first time the cheat does, the second the next time, and round again. Each choice is this many blocks, such as a line and its spell.",
            "Do the next {Count} choices in turn, {Group} blocks each", [F("Count", CheatFieldType.Number, 0, max: 20), new("Group", CheatFieldType.Number, 1, Max: 10, Min: 1)],
            b => $"(the next {(b.Number("Count") > 0 ? N(b.Number("Count")) : "")} choices in turn{(b.Number("Group") > 1 ? $", {b.Number("Group")} blocks each" : "")})".Replace("next  ", "next ")),
        new("D.OnlySchool", BlockKind.Do, "school", "only for a school", "The next blocks (how many) go off only if the target is a wizard or creature of this school. \"The same as the block before\" checks whoever the block before went on: Imogen Nightgrove's Brace for that wizard's school.",
            "Do the next {Count} blocks only if {Target} is of {School}", [new("Count", CheatFieldType.Number, 1, Max: 20, Min: 1), F("Target", CheatFieldType.Target, "Last"), F("School", CheatFieldType.Choice, "Fire", SCHOOLS[1..])],
            b => $"(the next {Math.Max(1, b.Number("Count"))} only for {b.Text("School")}{Target(b).Replace(" on ", ": ")})"),

        // Do... (first made for the player cheats; "alias" ones are the same as a block above, kept to read old files)
        new("D.FreeCast", BlockKind.Do, "sparkle", "cast for free", "A free spell, as the .c1 and .c2 commands cast it. Pick when: right away, before or after your card, or first or last in the round. Silent skips the casting animation; enchanted adds the enchantments you pick (several stack), or the best one that fits when none is picked.",
            "Cast {Spell} on {Target} {Timing} {Times} times {Silent} {Enchant} {Enchants}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), F("Timing", CheatFieldType.Choice, "Now", s_timings),
             F("Times", CheatFieldType.Number, 1, max: 10), new("Silent", CheatFieldType.Toggle, false, Label: "silent"), new("Enchant", CheatFieldType.Toggle, false, Label: "enchanted"),
             F("Enchants", CheatFieldType.Enchants)],
            b => $"cast {SpellName(b)}{Target(b)} {LabelOf(s_timings, b.Text("Timing"))}"
                 + $"{(b.Number("Times") > 1 ? $" x{b.Number("Times")}" : "")}{(b.Flag("Silent") ? " (silent)" : "")}{(b.Flag("Enchant") ? b.List("Enchants") is { Count: > 0 } picked ? $" (enchanted: {string.Join(" + ", picked)})" : " (enchanted)" : "")}"),
        new("D.MyPips", BlockKind.Do, "pips", "give me pips", "Gives you pips (or takes them away).",
            "{Amount} {Kind} pips for me", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds)],
            b => $"{(b.Number("Amount") < 0 ? "take" : "give me")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips", "alias"),
        new("D.MyHeal", BlockKind.Do, "heal", "heal me", "Heals you by hit points, or by a share of your most health.",
            "Heal me by {Amount}", [F("Amount", CheatFieldType.HealAmount)], b => $"heal me by {(b.Flag("Percent") ? $"{b.Number("Amount")}%" : N(b.Number("Amount")))}", "alias"),
        new("D.Banner", BlockKind.Do, "quote", "show a banner", "A line from the game shown over you in battle, as .ca shows it. Type a few of its words or its number.",
            "Show {Line} over me", [F("Line", CheatFieldType.Line)], b => b.Text("Line").Length == 0 ? "show (no line picked)" : $"show \"{b.Text("Line")}\"", "alias"),
        new("D.MyChat", BlockKind.Do, "chat", "message to me", "Any words you like, in your own chat window.",
            "Tell me in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell me \"{b.Text("Text")}\"", "alias"),

        // Do... (both)
        new("D.Wait", BlockKind.Do, "hourglass", "wait … rounds", "The blocks under this one go off at the start of a round this many rounds later, if it still stands: a minion falls in round 3, and with 4 they go off in round 7.",
            "Wait {Rounds} rounds, then do the blocks under this one", [F("Rounds", CheatFieldType.Number, 4, max: 99)],
            b => $"wait {(b.Number("Rounds") == 1 ? "1 round" : $"{Math.Max(1, b.Number("Rounds"))} rounds")}"),
        new("D.PickOne", BlockKind.Do, "dice", "pick one at random", "Of the blocks under this one (how many: 0 is all of them), only one goes off, picked at random. Chances such as 2, 1, 1 make the first twice as likely as each other one.",
            "Do only one of the next {Count} choices, {Group} blocks each, with chances {Weights}", [F("Count", CheatFieldType.Number, 0, max: 20), new("Group", CheatFieldType.Number, 1, Max: 10, Min: 1), F("Weights", CheatFieldType.Words, "")],
            b => $"(one of the next {(b.Number("Count") > 0 ? N(b.Number("Count")) : "blocks")}, at random)"),
        new("D.Other", BlockKind.Do, "puzzle", "something else", "An action this app does not know, kept as the file has it.",
            "Do {Do}", [F("Do", CheatFieldType.Words)], b => $"do {b.Text("Do")}"),
    ];

    private static readonly Dictionary<string, CheatSpec> s_byId = All.ToDictionary(s => s.Id);

    internal static CheatSpec Get(string id) => s_byId.TryGetValue(id, out var spec) ? spec : s_byId["D.Other"];

    /// <summary>The server's Do for a Do block.</summary>
    internal static string DoOf(string id) => id switch {
        "D.Say" or "D.Chat" or "D.Banner" or "D.MyChat" => "Say", "D.MyPips" => "Pips", "D.MyHeal" => "Heal", _ => id[2..],
    };

    /// <summary>The server's When for a When block.</summary>
    internal static string WhenOf(string id) => id switch { "W.MyPips" => "Pips", _ when id.StartsWith("P.", StringComparison.Ordinal) => "Passive", _ => id[2..] };

    /// <summary>The server's Passive (which passive it is) for a Passive block, or "".</summary>
    internal static string PassiveOf(string id) => id.StartsWith("P.", StringComparison.Ordinal) ? id[2..] : "";

    /// <summary>A cheat in one line, such as "When hit by Fire: cast Supernova Sun on the attacker, 50% chance."</summary>
    internal static string Summary(CheatRule rule) {
        var when = rule.When.Kind.Summary(rule.When);
        if (rule.When.Kind.Kind == BlockKind.Passive) {
            return $"Passive: {rule.When.Kind.Summary(rule.When).ToLowerInvariant()}.";
        }

        var dos = rule.Dos.Where(d => d.On).Select(d => d.Kind.Summary(d)).ToList();
        var ifs = rule.Ifs.Where(i => i.On).Select(i => i.Kind.Summary(i)).ToList();
        var what = dos.Count == 0 ? "does nothing yet" : string.Join(", then ", dos);
        var text = $"{when}: {what}" + (ifs.Count > 0 ? ", " + string.Join(", ", ifs) : "");

        return text.EndsWith('"') && text.Length > 1 && ".!?".Contains(text[^2]) ? text : text + ".";
    }

}
