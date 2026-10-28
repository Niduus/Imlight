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
        // Niduus, 2026-10-27: "blocks to state "ALL PLAYERS" with "ALL PLAYERS (Excluding self)" as an option": every
        // wizard standing, on either side; the second without whoever's cheat it is.
        ("AllPlayers", "ALL PLAYERS"),
        ("AllPlayersButSelf", "ALL PLAYERS (Excluding self)"),
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
        // Niduus, 2026-10-26: both sides, standing.
        ("Everyone", "everyone (both sides)"),
        ("EveryoneElse", "everyone but itself"),
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

    // Whom a Cast or FreeCast of a "select your targets" spell picks (Niduus, 2026-10-26), for its Pick.
    private static readonly (string Value, string Label)[] s_picks = [
        ("", "no pick: every enemy"), ("WeakestEnemy", "the weakest players"), ("StrongestEnemy", "the strongest players"),
        ("Enemy", "players at random"), ("MostPips", "the players with the most pips"), ("Marked", "whoever has the mark"),
        ("Unmarked", "the players without the mark"), ("AllEnemies", "every player"), ("Caster", "the attacker"),
        ("Matched", "the one the If found"), ("Last", "the same as the block before"), ("WeakestAlly", "its weakest allies"),
        ("StrongestAlly", "its strongest allies"), ("Ally", "allies at random"), ("AllAllies", "its whole side"),
        ("Everyone", "everyone (both sides)"), ("EveryoneElse", "everyone but itself"),
        ("e1", "the other side's circle 1"), ("e2", "the other side's circle 2"), ("e3", "the other side's circle 3"), ("e4", "the other side's circle 4"),
        ("p1", "its side's circle 1"), ("p2", "its side's circle 2"), ("p3", "its side's circle 3"), ("p4", "its side's circle 4"),
    ];

    private const string PICK_HELP = " For a spell where you select the targets (Qismah's Curse, the Fusion spells, the tier B AoEs): pick whom it hits"
        + " and how many (0: all it names), such as the 2 weakest players or the marked ones. It is cast once and the game plays it on"
        + " the picked ones (only those on the side the spell hits count); the Target is not used then. With no pick it hits every enemy.";

    private static string PickWords(CheatBlock b) => b.Text("Pick").Length == 0 ? ""
        : $" (picks {(b.Number("Count") > 0 ? $"{b.Number("Count")} of " : "")}{LabelOf(s_picks, b.Text("Pick"))})";

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
        ("GloomGlobal", "global (less healing)"),
        ("PierceBlade", "pierce boost (spear)"), ("PierceWeakness", "less pierce"), ("PierceTrap", "pierce trap (more pierce taken)"),
        ("PierceShield", "pierce shield (less pierce taken)"), ("FlatBlade", "flat damage boost"), ("FlatWeakness", "flat damage weakness"),
        ("FlatShield", "flat shield"), ("FlatTrap", "flat trap"), ("Accuracy", "accuracy boost"), ("Smoke", "accuracy loss (smoke)"),
        ("Critical", "critical boost"), ("CritBlock", "critical block boost"), ("HealBoost", "healing given boost"),
        ("HealWeakness", "less healing given"), ("HealIn", "more healing taken"), ("Infection", "less healing taken (infection)"),
        ("FlatHeal", "flat healing given"), ("FlatHealIn", "flat healing taken"), ("DotShield", "less damage over time taken"),
        ("DotTrap", "more damage over time taken"), ("StunBlock", "stun block"), ("StunResist", "stun resist"),
        ("DamageCap", "damage cap (most damage a hit can do)"), ("PowerPip", "power pip chance"), ("PipConversion", "pip conversion"),
        ("DispelBlock", "dispel block")];
    // The kinds whose amount is a number, not a percent.
    private static readonly HashSet<string> s_flatKinds = ["Absorb", "FlatBlade", "FlatWeakness", "FlatShield", "FlatTrap", "FlatHeal", "FlatHealIn",
        "StunBlock", "DamageCap", "PipConversion", "DispelBlock"];
    private static readonly (string, string)[] s_boostKinds = [("Damage", "damage"), ("Resist", "resist"), ("Heal", "healing"), ("Accuracy", "accuracy"),
        ("Critical", "critical"), ("Block", "block"), ("Pierce", "pierce"), ("StunResist", "stun resist"), ("HealIn", "healing taken"), ("PowerPip", "power pip chance")];
    private static readonly (string, string)[] s_statKinds = [("Damage", "damage"), ("Resist", "resist"), ("Pierce", "pierce"), ("Critical", "critical"), ("Block", "block"), ("Accuracy", "accuracy")];
    private static readonly (string, string)[] s_statusKinds = [("Stunned", "stunned"), ("Confused", "confused"), ("Polymorphed", "polymorphed")];
    private static readonly (string, string)[] s_timeKinds = [("Hour", "hour"), ("Month", "month"), ("Day", "day of the month"), ("Weekday", "weekday")];
    private static readonly (string, string)[] s_spellRules = [("Hit", "always land"), ("Fizzle", "always fizzle"), ("Crit", "always crit"), ("NoCrit", "never crit"), ("Off", "go back to normal")];
    private static readonly (string, string)[] s_fleeOnOff = [("", "can't flee"), ("Off", "can flee again")];
    private static readonly (string, string)[] s_convertTo = [("Blade", "blades"), ("Weakness", "weaknesses"), ("Shield", "shields"), ("Trap", "traps"), ("Absorb", "absorbs")];
    private static readonly (string, string)[] s_protectOnOff = [("", "can't be removed"), ("Off", "can be removed again")];
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
        ("Dispel", "dispel"),
    ];
    // Niduus, 2026-10-26: a school, or its rival (opposite).
    private static readonly (string, string)[] s_schoolMatch = [("", "the school"), ("Rival", "the rival of"), ("RivalOfIt", "the rival of its own school (box not used):")];
    private static readonly (string, string)[] s_markWho = [("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at"), ("Self", "it"), ("AnyEnemy", "someone on the other side"),
        ("AllEnemies", "every player")];
    private static readonly (string, string)[] s_castBy = [("Creature", "this creature:"), ("Ally", "a random ally"), ("WeakestAlly", "its weakest ally"), ("StrongestAlly", "its strongest ally"), ("Summoner", "whoever summoned it")];
    // Chromatic (Niduus, 2026-10-27: "Chromatic SELF (Meaning gain a blade of that targets school) Chromatic ENEMY (Give a
    // weakness on that enemy specifically for their school) ... COVER ALL CORNERS"): a school the server picks when the
    // block runs, from the fighters (CombatResolver.Chromatic.cs).
    internal static readonly (string Value, string Label)[] CHROMATIC = [
        ("Chromatic", "Chromatic: each target's own school"),
        ("ChromaticRival", "Chromatic: the rival of each target's school"),
        ("ChromaticTrigger", "Chromatic: the school of whoever set it off"),
        ("ChromaticReceiver", "Chromatic: the school of whoever the spell was aimed at"),
        ("ChromaticFoes", "Chromatic: one of each school on the other side"),
        ("ChromaticAllies", "Chromatic: one of each school on its own side"),
        ("ChromaticLast", "Chromatic: the school of the block before's target"),
        ("ChromaticOwn", "Chromatic: its own school"),
        ("ChromaticOwnRival", "Chromatic: the rival of its own school"),
    ];
    private static readonly (string, string)[] s_anySchool = [("", "universal (every school)"), .. SCHOOLS[1..], .. CHROMATIC];
    // Niduus, 2026-10-27 ("add a lock zones block so I can like lock the moon slot or gem in battle"): the circles of a
    // battle by the symbol the game draws on them.
    private static readonly (string, string)[] s_circles = [
        ("Sun", "Sun (wizards' circle 1)"), ("Eye", "Eye (wizards' circle 2)"), ("Star", "Star (wizards' circle 3)"), ("Moon", "Moon (wizards' circle 4)"),
        ("Dagger", "Dagger (creatures' circle 1)"), ("Key", "Key (creatures' circle 2)"), ("Gem", "Gem (creatures' circle 3)"), ("Spiral", "Spiral (creatures' circle 4)"),
        ("OwnSide", "every circle on its own side"), ("OtherSide", "every circle on the other side"), ("All", "every circle")];
    private static string CircleName(string kind) => s_circles.FirstOrDefault(c => c.Item1.Equals(kind, StringComparison.OrdinalIgnoreCase)).Item2 ?? kind;
    private static readonly (string, string)[] s_changeSchools = [.. SCHOOLS[1..], .. CHROMATIC];
    // Niduus, 2026-10-25 ("Expand on existing cheats"): the blocks of CombatResolver.MoreCheats.cs.
    // The Do blocks that put effects on, and so take a tag (declared before All, which reads it; Niduus, 2026-10-27: "my cheat for casting 7% x3 Chromatic
    // blades on my self, I want to add a tag on it like BABA"); "remove effects" takes only effects with the tag.
    private static readonly HashSet<string> s_tagged = ["D.Cast", "D.FreeCast", "D.CastBy", "D.AddEffects", "D.AddCharm", "D.Aura", "D.Boost", "D.Immune"];

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

    // Niduus, 2026-10-26 ("You can legit do anything"): the conditions (each one entry of the cheat's Conditions, with NOT
    // and OR groups) and the creative Do blocks (the server's CombatResolver.CreativeCheats.cs).
    internal static readonly (string Value, string Label)[] COND_WHO = [
        ("", "it"), ("AllPlayers", "ALL PLAYERS"), ("AllPlayersButSelf", "ALL PLAYERS (Excluding self)"), ("AnyPlayer", "any player"),
        ("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at"), ("Matched", "the one the If found"),
        ("LastAttacker", "the last one who hurt it"), ("Summoner", "whoever summoned it"),
        ("AnyEnemy", "someone on the other side"), ("AnyAlly", "someone on its side"), ("Anyone", "anyone"),
        ("EveryEnemy", "everyone on the other side"), ("EveryAlly", "everyone on its side"), ("Everyone", "everyone"),
        ("EnemySide", "the other side, added up"), ("AllySide", "its side, added up"), ("BothSides", "both sides, added up"),
        ("p1", "its side's circle 1"), ("p2", "its side's circle 2"), ("p3", "its side's circle 3"), ("p4", "its side's circle 4"),
        ("e1", "the other side's circle 1"), ("e2", "the other side's circle 2"), ("e3", "the other side's circle 3"), ("e4", "the other side's circle 4"),
    ];
    private static readonly (string, string)[] s_ops = [(">=", "at least"), ("<=", "at most"), (">", "more than"), ("<", "less than"), ("=", "exactly"), ("!=", "anything but")];
    private static readonly (string, string)[] s_casters = [("", "anyone"), ("AllPlayers", "ALL PLAYERS"), ("AllPlayersButSelf", "ALL PLAYERS (Excluding self)"), ("Self", "it"), ("Allies", "its side"), ("OtherAllies", "its allies"),
        ("Enemies", "the other side"), ("Caster", "the attacker"), ("e1", "the other side's circle 1"), ("e2", "the other side's circle 2"),
        ("e3", "the other side's circle 3"), ("e4", "the other side's circle 4"), ("p1", "its side's circle 1"), ("p2", "its side's circle 2"),
        ("p3", "its side's circle 3"), ("p4", "its side's circle 4")];
    private static readonly (string, string)[] s_onWho = [("", "anyone"), ("AllPlayers", "ALL PLAYERS"), ("AllPlayersButSelf", "ALL PLAYERS (Excluding self)"), ("Self", "it"), ("AnyAlly", "someone on its side"), ("AnyEnemy", "someone on the other side"),
        ("Caster", "the attacker"), ("Receiver", "whoever that spell was aimed at")];
    private static readonly (string, string)[] s_results = [("", "landed or not"), ("Landed", "landed"), ("Crit", "landed as a critical"), ("Fizzled", "fizzled")];
    private static readonly (string, string)[] s_within = [("", "this battle"), ("Round", "this round"), ("LastRound", "last round")];
    private static readonly (string, string)[] s_yesNo = [("", "either"), ("Yes", "yes"), ("No", "no")];
    private static readonly (string, string)[] s_standSide = [("", "its side"), ("Them", "the other side"), ("All", "both sides")];
    private static readonly (string, string)[] s_inSide = [("", "either side"), ("Us", "its side"), ("Them", "the other side")];
    private static readonly (string, string)[] s_condPips = [("", "pips (all kinds)"), ("Normal", "normal pips"), ("Power", "power pips"), ("Shadow", "shadow pips"),
        ("Fire", "Fire pips"), ("Ice", "Ice pips"), ("Storm", "Storm pips"), ("Myth", "Myth pips"), ("Life", "Life pips"), ("Death", "Death pips"), ("Balance", "Balance pips")];
    private static readonly (string, string)[] s_scopes = [("", "its own"), ("Battle", "the battle's"),
        ("Saved", "its own, kept across battles"), ("SavedAll", "everyone's, kept across battles")];
    // Niduus, 2026-10-28 ("what if I wanted it to be just universal stuff? like traps, Hex, Curse and Feint"): which
    // effects by their school.
    private static readonly (string, string)[] s_effectSchools = [("", "of any school"), ("Universal", "universal (all-school) only"), .. SCHOOLS[1..]];
    private static readonly (string, string)[] s_rememberFrom = [
        ("Spell", "the size of the spell that set it off"), ("Newest", "the size of the newest"), ("Biggest", "the size of the biggest"),
        ("Total", "the sizes added up of all"), ("Count", "how many there are of"), ("Health", "the health of"),
        ("HealthPercent", "the health % of"), ("Pips", "the pips of"), ("Round", "the round number"),
        ("LastHitTaken", "the damage of the last hit taken by"), ("LastHitDealt", "the damage of the last hit dealt by"),
        ("Hand", "the cards in the hand of"), ("Deck", "the cards left in the deck of"), ("Treasure", "the treasure cards left of"),
    ];
    private static readonly (string, string)[] s_reviveWho = [("", "one fallen ally at random"), ("AllAllies", "every fallen ally"), ("Self", "itself (me)")];
    private static readonly (string, string)[] s_overTimeKinds = [("", "damage and heal over time and bombs"), ("DoT", "damage over time and bombs"), ("HoT", "heal over time")];
    private static readonly (string, string)[] s_hitShares = [("Thorns", "thorns: who hits it takes back"), ("Lifesteal", "lifesteal: heals from the damage it deals"), ("HealFromHits", "hits heal it instead, by")];
    private static readonly (string, string)[] s_cardKinds = [("Hand", "cards in the hand"), ("Deck", "cards left in the deck"), ("Treasure", "treasure cards left"), ("TreasureInHand", "treasure cards in the hand")];
    private static readonly (string, string)[] s_cardPlayedKinds = [("", "any card"), ("Treasure", "treasure card"), ("Item", "item card (from gear)"), ("Deck", "card from the deck")];
    private static readonly (string, string)[] s_lastHitKinds = [("Taken", "taken"), ("Dealt", "dealt")];
    private static readonly (string, string)[] s_varOps = [("Set", "set to"), ("Add", "add"), ("Subtract", "take away"), ("Multiply", "multiply by"), ("Random", "a random number from 0 to")];
    private static readonly (string, string)[] s_maxOps = [("Set", "become"), ("Add", "go up by (below 0: down)")];
    private static readonly (string, string)[] s_onOff = [("Off", "off"), ("On", "back on")];

    // A condition block: the server's Conditions entry "If" is the id after "C."; every one can be turned round (NOT) and
    // put in an OR group (the blocks of one group need only one of them to hold).
    private static CheatSpec Cond(string id, string icon, string palette, string help, string sentence, CheatField[] fields, Func<CheatBlock, string> summary)
        => new(id, BlockKind.If, icon, palette,
            help + " Tick NOT to turn it round. Give blocks the same OR group (1, 2...) and only one of them needs to hold; group 0 must always hold. When it looks at someone on a side, the one it found is \"the one the If found\" for the Do blocks.",
            sentence + " {Not} OR group {Any}", [.. fields, new("Not", CheatFieldType.Toggle, false, Label: "NOT"), F("Any", CheatFieldType.Number, 0, max: 9)],
            b => (b.Flag("Not") ? "not: " : "") + summary(b) + (b.Number("Any") > 0 ? $" (or-group {b.Number("Any")})" : ""));

    private static readonly (string, string)[] s_ranks = [("None", "any rank"), ("QualityAssurance", "Quality Assurance"), ("HallMonitor", "Hall Monitor"),
        ("Developer", "Developer"), ("Administrator", "Administrator")];

    private const string WHO_HAS_IT = " In a player cheat, about \"it\", it says who gets the cheat: every wizard it fits, on any PC (a player cheat with none of"
        + " these blocks is only for the wizard you play on this PC). It also works about anyone else (the attacker, someone on the other side...)"
        + " and in a boss's cheats, where a creature is never a wizard. Tick NOT for everyone but them.";

    // A picked wizard as words: the name, with the account.
    private static string WizardWords(CheatBlock b) => b.Text("Name").Length == 0 ? "(no wizard picked)"
        : b.Text("Account").Length > 0 ? $"{b.Text("Name")} ({b.Text("Account")})" : b.Text("Name");

    private static string Op(CheatBlock b) => LabelOf(s_ops, b.Text("Op").Length > 0 ? b.Text("Op") : ">=");

    private static string Who(CheatBlock b, string key = "Who") => LabelOf(COND_WHO, b.Text(key));

    private static CheatField WhoField() => F("Who", CheatFieldType.Choice, "", COND_WHO);

    private static CheatField OpField() => F("Op", CheatFieldType.Choice, ">=", s_ops);

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

    private static string SchoolWord(CheatBlock b) => b.Text("School").Length > 0 ? SchoolName(b.Text("School")) + " " : "";

    // A school as a sentence says it: a Chromatic one by its words ("chromatic (rival of each target's school)").
    internal static string SchoolName(string school)
        => CHROMATIC.FirstOrDefault(c => c.Value.Equals(school, StringComparison.OrdinalIgnoreCase)).Label is { } label
            ? "chromatic (" + label["Chromatic: ".Length..] + ")" : school;

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

    internal static readonly CheatSpec[] All = WithTags([
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
        // Niduus, 2026-10-28 (the cheat ideas list): Whens after a move (CombatResolver.ExtraCheats.cs).
        new("W.EffectAdded", BlockKind.When, "shield", "an effect is put on", "A charm, ward, aura or over-time is put on it (or on someone on a side), after the spell lands. \"The attacker\" is whoever cast it, \"the one hit\" whoever got it, and \"the size of the spell that set it off\" (remember) reads that spell. Example: when a trap is put on me, turn traps on me into blades.",
            "When {RemovedFrom} gets a {RemovedKind} from {RemovedSpell}",
            [F("RemovedFrom", CheatFieldType.Choice, "", s_removedFrom), F("RemovedKind", CheatFieldType.Choice, "", s_hasKinds), F("RemovedSpell", CheatFieldType.Words, "")],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} gets {(b.Text("RemovedSpell").Length > 0 ? b.Text("RemovedSpell") + " " : "a ")}{LabelOf(s_hasKinds, b.Text("RemovedKind"))}"),
        new("W.EffectUsed", BlockKind.When, "stop", "an effect is used up", "A shield, trap, blade or weakness on it (or on someone on a side) is used up by a hit or heal, not taken off by a spell. Example: every time my shield breaks, put a 20% blade on me.",
            "When {RemovedFrom} uses up a {RemovedKind} from {RemovedSpell}",
            [F("RemovedFrom", CheatFieldType.Choice, "", s_removedFrom), F("RemovedKind", CheatFieldType.Choice, "Shield", s_hasKinds), F("RemovedSpell", CheatFieldType.Words, "")],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} uses up {(b.Text("RemovedSpell").Length > 0 ? b.Text("RemovedSpell") + " " : "a ")}{LabelOf(s_hasKinds, b.Text("RemovedKind"))}"),
        new("W.Healed", BlockKind.When, "heal", "someone is healed", "Its health (or someone's on a side) goes up from a move. Example: whenever the boss heals, it gets a 10% blade.",
            "When {RemovedFrom} is healed", [F("RemovedFrom", CheatFieldType.Choice, "", s_removedFrom)],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} is healed"),
        new("W.Stunned", BlockKind.When, "stop", "someone is stunned", "It (or someone on a side) is stunned by a move. Example: when a player is stunned, the boss gets a power pip.",
            "When {RemovedFrom} is stunned", [F("RemovedFrom", CheatFieldType.Choice, "AnyEnemy", s_removedFrom)],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} is stunned"),
        new("W.CardPlayed", BlockKind.When, "cards", "a card is played", "It (or someone on a side) plays a treasure card, an item card or a card from the deck, after it lands. Name spells to count only those. \"The attacker\" and \"the one hit\" are whoever played it. Example: when an enemy plays a treasure card, take 2 pips from them.",
            "When {RemovedFrom} plays a {RemovedKind} {RemovedSpell}",
            [F("RemovedFrom", CheatFieldType.Choice, "AnyEnemy", s_removedFrom), F("RemovedKind", CheatFieldType.Choice, "Treasure", s_cardPlayedKinds), F("RemovedSpell", CheatFieldType.Words, "")],
            b => $"When {LabelOf(s_removedFrom, b.Text("RemovedFrom"))} plays {(b.Text("RemovedSpell").Length > 0 ? b.Text("RemovedSpell") + " as " : "")}a {LabelOf(s_cardPlayedKinds, b.Text("RemovedKind"))}"),
        new("W.Summoned", BlockKind.When, "users", "someone comes in", "A minion or creature comes into the battle on a side during a move (a summon spell, or a cheat's minion). \"The one hit\" is the newcomer. Example: when the other side summons a minion, stun it.",
            "When someone comes in on {RemovedFrom}", [F("RemovedFrom", CheatFieldType.Choice, "AnyEnemy", s_removedFrom)],
            b => $"When someone comes in on {LabelOf(s_removedFrom, b.Text("RemovedFrom"))}"),
        // Niduus, 2026-10-27: "I want it so IF tags = 0, THEN CAST tags. Not check WHEN tags".
        new("W.Always", BlockKind.When, "bolt", "always (when its Ifs are true)", "No event: it goes off whenever its If blocks are all true, looked at after every move and at the start and end of each round (once each look). Give it If blocks, or it goes off every time. \"The attacker\" and \"the one hit\" are itself. Example, blades that stay up: Always, only if it has none blade tagged BABA (has none), then put a 7% Chromatic blade on itself tagged BABA, three times.",
            "Always, whenever its Ifs are true", [], _ => "Always (whenever its Ifs are true)"),
        // Niduus, 2026-10-27: "If I have 0 BABA tag blades, it will immediately cast the cheat again".
        new("W.TagGone", BlockKind.When, "layers", "runs out of tagged effects", "The moment it (or someone on a side) has no effect left with this tag, after having had one: used up by a hit, taken off, or run out. Tag the effects with the Tag box of the block that puts them on (cast, add an effect of your own size...). It goes off once each time they run out, right after the move that used the last one. \"Also at a round's start\" makes it go off at the start of any round when there are none, so it needs no other When for the first time. \"The attacker\" and \"the one hit\" are the one who ran out. Example: When it runs out of effects tagged BABA (also at a round's start), put a 7% Chromatic blade on itself, tagged BABA, three times.",
            "When {TagWho} has no effects tagged {Tag} left {TagStart}",
            [F("TagWho", CheatFieldType.Choice, "", s_removedFrom), F("Tag", CheatFieldType.Words, "BABA"), new("TagStart", CheatFieldType.Toggle, false, Label: "also at a round's start")],
            b => $"When {LabelOf(s_removedFrom, b.Text("TagWho"))} runs out of effects tagged \"{b.Text("Tag")}\"{(b.Flag("TagStart") ? " (also at a round's start)" : "")}"),

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
        new("I.Has", BlockKind.If, "shield", "has a charm or ward", "Only if that one has a charm, ward, aura or over-time of this kind on them, or one from this spell (any version), at least this many (0 or 1: one; Coatl answers a fourth blade). Tagged (optional): only effects with this tag, as a Do block's Tag box put on (BABA). With \"has none\", only if they have fewer: Kurokage answers a wizard who attacks with no shield up. \"Someone on the other side\" or \"someone on its side\" looks at everyone there (\"has none\": no one there), and \"the one the If found\" is the target of its Do blocks.",
            "Only if {HasWho} has {HasCount} {HasKind} from {HasSpell} tagged {HasTag} {HasNot}",
            [F("HasWho", CheatFieldType.Choice, "Caster", s_hasWho), F("HasCount", CheatFieldType.Number, 0, max: 99), F("HasKind", CheatFieldType.Choice, "Shield", s_hasKinds), F("HasSpell", CheatFieldType.Words, ""),
             F("HasTag", CheatFieldType.Words, ""), new("HasNot", CheatFieldType.Toggle, false, Label: "has none")],
            b => $"only if {LabelOf(s_hasWho, b.Text("HasWho"))} {(b.Flag("HasNot") ? "has no" : b.Number("HasCount") > 1 ? $"has {b.Number("HasCount")} or more" : "has a")} {(b.Text("HasSpell").Length > 0 ? b.Text("HasSpell") + " " : "")}{LabelOf(s_hasKinds, b.Text("HasKind"))}{(b.Text("HasTag").Length > 0 ? $" tagged \"{b.Text("HasTag")}\"" : "")}"),
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

        // If... conditions (Niduus, 2026-10-26: "expand more on conditions"), with NOT and OR groups.
        Cond("C.Health", "heart", "anyone's health", "Someone's health, in hit points or as a share of their most. \"Someone on the other side\" holds if any one of them does, \"everyone on…\" only if all do, and \"…added up\" looks at the side together (its average share). Example: the other side, added up, at most 30 % — the players are nearly done.",
            "If {Who} has a health of {Op} {Value} {Percent}", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 50), new("Percent", CheatFieldType.Toggle, true, Label: "% of the most")],
            b => $"if {Who(b)} has {Op(b)} {N(b.Number("Value"))}{(b.Flag("Percent") ? "%" : "")} health"),
        Cond("C.HealthLost", "hit", "lost this round", "How much health someone lost since this round started (after heals, as hit points or a share of their most). Example: it lost at least 20 % this round — answer a burst.",
            "If {Who} lost {Op} {Value} {Percent} health this round", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 20), new("Percent", CheatFieldType.Toggle, true, Label: "% of the most")],
            b => $"if {Who(b)} lost {Op(b)} {N(b.Number("Value"))}{(b.Flag("Percent") ? "%" : "")} health this round"),
        Cond("C.HealthGained", "heal", "healed this round", "How much health someone gained since this round started. Example: someone on the other side healed at least 1,000 this round — punish the heal.",
            "If {Who} gained {Op} {Value} {Percent} health this round", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 1000), new("Percent", CheatFieldType.Toggle, false, Label: "% of the most")],
            b => $"if {Who(b)} healed {Op(b)} {N(b.Number("Value"))}{(b.Flag("Percent") ? "%" : "")} this round"),
        Cond("C.Standing", "users", "how many stand", "How many stand on a side right now (it counts on its own side). Example: exactly 1 on its side — it is alone.",
            "If {Op} {Value} stand on {Side}", [OpField(), F("Value", CheatFieldType.Number, 2, max: 8), F("Side", CheatFieldType.Choice, "", s_standSide)],
            b => $"if {Op(b)} {b.Number("Value")} stand on {LabelOf(s_standSide, b.Text("Side"))}"),
        Cond("C.Outnumbered", "users", "outnumbered", "Fewer stand on its side than on the other.",
            "If its side is outnumbered", [], _ => "if outnumbered"),
        Cond("C.Round", "clock", "odd / even / every Nth round", "The battle's round number, divided by Every, leaves this. Every 2 leaves 1: odd rounds; leaves 0: even rounds. Every 3 leaves 0: rounds 3, 6, 9...",
            "If the round, divided by {Every}, leaves {Offset}", [F("Every", CheatFieldType.Number, 2, max: 99), F("Offset", CheatFieldType.Number, 1, max: 98)],
            b => b.Number("Every") == 2 ? b.Number("Offset") % 2 == 1 ? "in odd rounds" : "in even rounds" : $"in rounds that leave {b.Number("Offset")} of {Math.Max(1, b.Number("Every"))}"),
        Cond("C.GoesFirst", "flag", "its side goes first", "Its side moves first this round (NOT: the other side does).",
            "If its side goes first this round", [], _ => "if its side goes first"),
        Cond("C.Fired", "list", "a cheat went off … times", "How many times one of its cheats went off this battle. Cheat 0 is this one; 1 is the first in its list. Example: cheat 0 at most 2 times, as a limit you can turn round.",
            "If cheat {Cheat} went off {Op} {Value} times", [F("Cheat", CheatFieldType.Number, 0, max: 200), OpField(), F("Value", CheatFieldType.Number, 3, max: 999)],
            b => $"if {(b.Number("Cheat") > 0 ? $"cheat {b.Number("Cheat")}" : "this cheat")} went off {Op(b)} {b.Number("Value")} times"),
        Cond("C.Casts", "sparkle", "spells cast", "Counts the spells cast this battle (or this round, or last round) by whom, on whom, of which spell (names or spell IDs, with commas), school and kind, and how they went. Example: \"nobody has cast Tower Shield this battle\": anyone, exactly 0, Tower Shield. \"It took a critical this round\": anyone, at least 1, on it, landed as a critical, this round.",
            "If {Who} cast {Op} {Value} {School} {Kind} spells named {Spell} on {On} that {Result}, {Within}",
            [F("Who", CheatFieldType.Choice, "", s_casters), OpField(), F("Value", CheatFieldType.Number, 1, max: 999), F("School", CheatFieldType.School), F("Kind", CheatFieldType.Category),
             F("Spell", CheatFieldType.Words), F("On", CheatFieldType.Choice, "", s_onWho), F("Result", CheatFieldType.Choice, "", s_results), F("Within", CheatFieldType.Choice, "", s_within)],
            b => $"if {LabelOf(s_casters, b.Text("Who"))} cast {Op(b)} {b.Number("Value")} {(b.Text("Spell").Length > 0 ? b.Text("Spell") : "spells")} {LabelOf(s_within, b.Text("Within"))}"),
        Cond("C.LastSpell", "bolt", "the last spell", "The last spell someone (or a side, or a circle) cast: its name or spell ID, school, kind, pip cost (0 to 0: any), treasure card or not, enchanted or not, and how it went. Example: the other side's last spell was a treasure card — answer card tricks.",
            "If the last spell {Who} cast was {Spell} of {School}, {Kind}, {CostMin} to {CostMax} pips, treasure {Treasure}, enchanted {Enchanted}, and it {Result}",
            [F("Who", CheatFieldType.Choice, "", s_casters), F("Spell", CheatFieldType.Words), F("School", CheatFieldType.School), F("Kind", CheatFieldType.Category),
             F("CostMin", CheatFieldType.Number, 0, max: 14), F("CostMax", CheatFieldType.Number, 0, max: 14), F("Treasure", CheatFieldType.Choice, "", s_yesNo),
             F("Enchanted", CheatFieldType.Choice, "", s_yesNo), F("Result", CheatFieldType.Choice, "", s_results)],
            b => $"if the last spell of {LabelOf(s_casters, b.Text("Who"))} was {(b.Text("Spell").Length > 0 ? b.Text("Spell") : $"{SchoolWord(b)}{(b.Text("Kind").Length > 0 ? LabelOf(CATEGORIES, b.Text("Kind")) + " " : "")}a spell")}"),
        Cond("C.Effects", "layers", "effects on someone", "How many charms, wards, auras or over-times of a kind (or from a spell: names or spell IDs) someone has. Tagged (optional): only effects with this tag, as a Do block's Tag box put on. Example: someone on the other side has at least 3 blades — they are about to hit hard; it has at most 0 blades tagged BABA — they are all used up.",
            "If {Who} has {Op} {Value} {EffectSchool} {Kind} from {Spell} tagged {Tag}", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 1, max: 99), F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "", s_hasKinds), F("Spell", CheatFieldType.Words), F("Tag", CheatFieldType.Words)],
            b => $"if {Who(b)} has {Op(b)} {b.Number("Value")}{Universal(b)} {LabelOf(s_hasKinds, b.Text("Kind"))}{(b.Text("Spell").Length > 0 ? $" from {b.Text("Spell")}" : "")}{(b.Text("Tag").Length > 0 ? $" tagged \"{b.Text("Tag")}\"" : "")}"),
        Cond("C.Pips", "pips", "pips of a kind", "Someone's pips of one kind: power, normal, shadow, or a school's. \"Both sides, added up\" with shadow pips counts the shadow pips in play.",
            "If {Who} has {Op} {Value} {Kind}", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 3, max: 99), F("Kind", CheatFieldType.Choice, "", s_condPips)],
            b => $"if {Who(b)} has {Op(b)} {b.Number("Value")} {LabelOf(s_condPips, b.Text("Kind"))}"),
        // Niduus, 2026-10-28 (the cheat ideas list; server: CombatResolver.ExtraCheats.cs).
        Cond("C.Cards", "cards", "cards in hand or deck", "How many cards someone has in their hand, left in their deck, or treasure cards left (or in the hand). Example: if the player has 2 or fewer cards left in the deck, give them 3 pips.",
            "If {Who} has {Op} {Value} {Kind}", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 3, max: 999), F("Kind", CheatFieldType.Choice, "Hand", s_cardKinds)],
            b => $"if {Who(b)} has {Op(b)} {b.Number("Value")} {LabelOf(s_cardKinds, b.Text("Kind"))}"),
        Cond("C.LastHit", "hit", "the last hit's damage", "How much damage the last hit someone took (or dealt) did. Example: if it took a hit of 2,000 or more, heal 20%.",
            "If the last hit {Kind} by {Who} did {Op} {Value}", [F("Kind", CheatFieldType.Choice, "Taken", s_lastHitKinds), WhoField(), OpField(), F("Value", CheatFieldType.Number, 1000, max: 10_000_000)],
            b => $"if the last hit {LabelOf(s_lastHitKinds, b.Text("Kind"))} by {Who(b)} did {Op(b)} {N(b.Number("Value"))}"),
        Cond("C.Stat", "user", "a stat", "Someone's damage, resist, pierce, critical, block or accuracy from their gear and stats (not blades or shields), for a school or all. Percent; a rating for critical and block. Example: the attacker has more than 40 pierce — raise its shields.",
            "If {Who} has {Kind} for {School} {Op} {Value}", [WhoField(), F("Kind", CheatFieldType.Choice, "Damage", s_statKinds), F("School", CheatFieldType.Choice, "", SCHOOLS), OpField(), F("Value", CheatFieldType.Number, 40, max: 10_000)],
            b => $"if {Who(b)} has {Op(b)} {b.Number("Value")} {LabelOf(s_statKinds, b.Text("Kind"))}{(b.Text("School").Length > 0 ? $" ({b.Text("School")})" : "")}"),
        Cond("C.Status", "stop", "stunned or confused", "Someone is stunned, confused or polymorphed right now.",
            "If {Who} is {Kind}", [WhoField(), F("Kind", CheatFieldType.Choice, "Stunned", s_statusKinds)],
            b => $"if {Who(b)} is {LabelOf(s_statusKinds, b.Text("Kind"))}"),
        Cond("C.Time", "clock", "time or date", "The server PC's clock: the hour (0-23), the month (1-12), the day of the month, or the weekday (names, commas between). Example: month = 10 for Halloween cheats; weekday Saturday, Sunday for weekend ones.",
            "If the {Kind} is {Op} {Value} (weekdays: {Name})", [F("Kind", CheatFieldType.Choice, "Hour", s_timeKinds), OpField(), F("Value", CheatFieldType.Number, 18, max: 31), F("Name", CheatFieldType.Words, "")],
            b => b.Text("Kind") == "Weekday" ? $"if it is {b.Text("Name")}" : $"if the {LabelOf(s_timeKinds, b.Text("Kind"))} is {Op(b)} {b.Number("Value")}"),
        Cond("C.Wears", "user", "wears an item", "A wizard has an item on (gear, pet, mount, deck) whose name or ID holds one of these words (commas between). Example: the attacker wears Darkmoor — the boss gets angrier.",
            "If {Who} wears {Name}", [WhoField(), F("Name", CheatFieldType.Words, "Darkmoor")],
            b => $"if {Who(b)} wears {b.Text("Name")}"),
        Cond("C.Quest", "flag", "finished a quest", "A wizard has finished one of these quests (their names as the game keeps them, commas between).",
            "If {Who} finished {Name}", [WhoField(), F("Name", CheatFieldType.Words, "")],
            b => $"if {Who(b)} finished {b.Text("Name")}"),
        Cond("C.Badge", "flag", "has a badge", "A wizard has one of these badges (badge IDs, commas between; the Badges list shows them).",
            "If {Who} has badge {Name}", [WhoField(), F("Name", CheatFieldType.Words, "")],
            b => $"if {Who(b)} has badge {b.Text("Name")}"),
        Cond("C.Level", "user", "level", "Someone's level (a side added up: their average). Example: the attacker is at most level 30 — go easy on low levels.",
            "If {Who} is level {Op} {Value}", [WhoField(), OpField(), F("Value", CheatFieldType.Number, 50, max: 200)],
            b => $"if {Who(b)} is level {Op(b)} {b.Number("Value")}"),
        Cond("C.School", "sparkle", "school of someone", "Someone is of this school (a wizard's school, or a creature's). \"The rival of\" is that school's opposite: Fire and Ice, Storm and Myth, Life and Death (Balance, Sun, Moon, Star and Shadow have none). Example: \"the attacker is of the rival of Ice\" is true for a Fire wizard. \"The rival of its own school\" skips the school box and uses the school of whose cheat it is: an Ice boss then picks out Fire wizards.",
            "If {Who} is of {Match} {School}", [WhoField(), F("Match", CheatFieldType.Choice, "", s_schoolMatch), F("School", CheatFieldType.School, "Fire")],
            b => b.Text("Match") == "RivalOfIt" ? $"if {Who(b)} is of its rival school"
                : $"if {Who(b)} is {(b.Text("Match") == "Rival" ? "of the rival of " : "")}{(b.Text("School").Length > 0 ? b.Text("School") : "of any school")}"),
        Cond("C.Name", "quote", "name has", "Someone's name has these words (a wizard's name, a custom's, or the creature's as the game shows it).",
            "If {Who}'s name has {Name}", [WhoField(), F("Name", CheatFieldType.Words)],
            b => $"if {Who(b)}'s name has \"{b.Text("Name")}\""),
        Cond("C.Creature", "user", "is this creature", "Someone is of this creature template (pick it, or type its ID).",
            "If {Who} is {Creature}", [WhoField(), F("Creature", CheatFieldType.Creature)],
            b => $"if {Who(b)} is {Creature(b)}"),
        Cond("C.Boss", "skull", "is a boss", "Someone is a boss (the game's boss creatures).",
            "If {Who} is a boss", [WhoField()], b => $"if {Who(b)} is a boss"),
        Cond("C.Player", "user", "is a wizard", "Someone is a wizard (not a creature).",
            "If {Who} is a wizard", [WhoField()], b => $"if {Who(b)} is a wizard"),
        // Who has it (Niduus, 2026-10-28: "expand on cheats so I can put them on other people ... If wizard is ... If
        // wizards name is ... Get creative"): about "it" in a player cheat they pick the wizards who get the cheat.
        Cond("C.Wizard", "user", "wizard is", "That one is this wizard: pick the account, then the wizard on it (the list shows each wizard's school and level)." + WHO_HAS_IT,
            "If {Who} is the wizard {Wizard}", [WhoField(), F("Wizard", CheatFieldType.Wizard)],
            b => $"if {Who(b)} is {WizardWords(b)}"),
        Cond("C.WizardName", "quote", "wizard's name is", "That one's whole name is one of these, with commas between (capitals and extra spaces don't matter). Unlike \"name has\", the whole name must match: \"Ahzi Fire\" is not \"Ahzi Firedust\"." + WHO_HAS_IT,
            "If {Who}'s name is {Name}", [WhoField(), F("Name", CheatFieldType.Words)],
            b => $"if {Who(b)}'s name is \"{(b.Text("Name").Length > 0 ? b.Text("Name") : "(no name typed)")}\""),
        Cond("C.Account", "users", "wizard's account", "That one is a wizard on this account. \"Any account\" is every wizard." + WHO_HAS_IT,
            "If {Who} is on the account {Account}", [WhoField(), F("Account", CheatFieldType.Account)],
            b => $"if {Who(b)} is on {(b.Text("Account").Length > 0 ? $"the account {b.Text("Account")}" : "any account")}"),
        Cond("C.ThisPc", "user", "played on this PC", "That one is a wizard played on the server's own PC (yours). Turned round with NOT: every wizard but yours." + WHO_HAS_IT,
            "If {Who} is played on this PC", [WhoField()], b => $"if {Who(b)} is played on this PC"),
        Cond("C.Friend", "heart", "friend of", "That one is on this wizard's friend list. Example: give a cheat to every friend of your main wizard." + WHO_HAS_IT,
            "If {Who} is a friend of {Wizard}", [WhoField(), F("Wizard", CheatFieldType.Wizard)],
            b => $"if {Who(b)} is a friend of {WizardWords(b)}"),
        Cond("C.Grouped", "users", "in a group with", "That one is in a group with this wizard. With no wizard picked: in a group with whoever's cheat it is." + WHO_HAS_IT,
            "If {Who} is in a group with {Wizard}", [WhoField(), F("Wizard", CheatFieldType.Wizard)],
            b => $"if {Who(b)} is in a group with {(b.Text("Wizard").Length > 0 || b.Text("Name").Length > 0 ? WizardWords(b) : "it")}"),
        Cond("C.Rank", "flag", "account rank", "That one's account is at least this rank (the rank the Accounts card sets). Example: a cheat only admins get." + WHO_HAS_IT,
            "If {Who}'s account is at least {Rank}", [WhoField(), F("Rank", CheatFieldType.Choice, "Administrator", s_ranks)],
            b => $"if {Who(b)}'s account is at least {LabelOf(s_ranks, b.Text("Rank"))}"),
        Cond("C.AnyWizard", "users", "every wizard", "With \"it\", gives a player cheat to every wizard on the server, not only yours; add school, level or other blocks to narrow it (every Fire wizard under level 20). About someone else it means \"is a wizard\"." + WHO_HAS_IT,
            "If {Who} is any wizard", [WhoField()], b => b.Text("Who").Length == 0 ? "for every wizard" : $"if {Who(b)} is any wizard"),
        Cond("C.Online", "users", "wizard is online", "This wizard is online anywhere on the server right now, in this battle or not. Example: a boss gets a damage boost while your friend is on.",
            "If {Wizard} is online", [F("Wizard", CheatFieldType.Wizard)], b => $"if {WizardWords(b)} is online"),
        Cond("C.InBattle", "users", "someone is in the battle", "A creature of this template, or anyone whose name has these words, stands in the battle. Example: \"Fairy\" on its side — only while a fairy still stands.",
            "If {Creature} or someone named {Name} stands on {Side}", [F("Creature", CheatFieldType.Creature), F("Name", CheatFieldType.Words), F("Side", CheatFieldType.Choice, "", s_inSide)],
            b => $"if {(b.Id("Creature") > 0 ? Creature(b) : $"\"{b.Text("Name")}\"")} stands on {LabelOf(s_inSide, b.Text("Side"))}"),
        Cond("C.Roll", "dice", "chance (in a group)", "A chance that comes up this often, rolled each time: unlike \"chance\" it can be put in an OR group or turned round.",
            "If a {Value} chance comes up", [F("Value", CheatFieldType.Percent, 50, max: 100)], b => $"{b.Number("Value")}% roll"),
        Cond("C.Var", "list", "a variable", "A number a \"set a variable\" or \"remember\" block keeps. Its own: each fighter has its own; the battle's: one for everyone. Example: rage at least 5. Fill in the second variable to compare two variables instead (trap bigger than blade).",
            "If {Scope} variable {Var} is {Op} {Value} (or than {Scope2} variable {Var2})", [F("Scope", CheatFieldType.Choice, "", s_scopes), F("Var", CheatFieldType.Words, "rage"), OpField(), new("Value", CheatFieldType.Number, 5, Max: 1_000_000, Min: -1_000_000),
                F("Scope2", CheatFieldType.Choice, "", s_scopes), F("Var2", CheatFieldType.Words, "")],
            b => $"if {b.Text("Var")} is {Op(b)} {(b.Text("Var2").Trim().Length > 0 ? b.Text("Var2").Trim() : N(b.Number("Value")))}"),

        new("C.Other", BlockKind.If, "puzzle", "another If", "An If of the cheat's Conditions this app does not know, kept as the file has it.",
            "If {If}", [F("If", CheatFieldType.Words)], b => $"if {b.Text("If")}", "alias"),

        // Do... (first made for bosses)
        new("D.Cast", BlockKind.Do, "sparkle", "cast a spell", "A free spell: no pips, it never fizzles and works through stun. Silent: no casting animation. A Dispel on the boss stops it; add the \"if dispelled\" If block to cast it again." + PICK_HELP,
            "Cast {Spell} on {Target} {Silent} picking {Count} of {Pick}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), new("Silent", CheatFieldType.Toggle, false, Label: "silent"),
             F("Count", CheatFieldType.Number, 0, max: 8), F("Pick", CheatFieldType.Choice, "", s_picks)],
            b => $"cast {SpellName(b)}{Target(b)}{(b.Flag("Silent") ? " (silent)" : "")}{PickWords(b)}"),
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
        new("D.RemoveEffects", BlockKind.Do, "broom", "remove effects", "Takes hanging effects of a kind (shields, traps, blades, weaknesses, any charm or ward, auras, damage or heal over time) off the target, newest first. 0 means all of them. Name a spell to take off only its effects (any version). Protected ones (Aegis, Indemnity) stay, unless \"even protected ones\" is ticked. Example: a player's spell lands, \"remove 1 blade from the receiver made by Balanceblade\" (even protected ones), then \"only if something came off\" and \"add an effect of your own size\": it destroys their blade and takes a bigger one.",
            "Remove {Count} {EffectSchool} {Kind} from {Target} made by {Spell} {Bypass}", [F("Count", CheatFieldType.Number, 0, max: 99), F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Ward", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("Spell", CheatFieldType.Words, ""),
             new("Bypass", CheatFieldType.Toggle, false, Label: "even protected ones")],
            b => $"remove {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")}{Universal(b)} {(b.Text("Spell").Length > 0 ? b.Text("Spell") + " " : "")}{LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b).Replace(" on ", " from ")}{(b.Flag("Bypass") ? " (even protected ones)" : "")}".Replace("  ", " ")),
        new("D.RemoveAuras", BlockKind.Do, "broom", "remove auras", "Takes auras (and boosts from cheats) off.",
            "Remove the auras of {Target}", [F("Target", CheatFieldType.Target, "AllEnemies")], b => $"remove the auras{Target(b).Replace(" on ", " of ")}"),
        new("D.AddEffects", BlockKind.Do, "shield", "add a spell's charms", "The charms and wards of a spell (Tower Shield, Fire Blade, Feint...) go on at once, with no casting.",
            "Put the charms and wards of {Spell} on {Target}", [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target, "Self")],
            b => $"put the effects of {SpellName(b)}{Target(b)}"),
        new("D.AddCharm", BlockKind.Do, "shield", "add an effect of your own size", "A blade, weakness, shield, trap or absorb of your own size goes on at once, and is used up like a real one: the amount is a percent (hit points for an absorb), so \"55, Storm, blade\" is a +55% Storm blade. A damage or heal over time is the amount in all, spread over the rounds (3 if 0); pick its school. The game only draws an effect it sees cast, so a real spell of that kind and school is cast for it (Shadowblade for a Shadow blade, Balanceblade for all schools...) and then made your size: the game shows the real spell's number, the server uses yours. \"Shown as\" (optional) picks that spell yourself; two of the same spell then count once on a hit, as real ones do. A damage or heal over time is not drawn. After \"remove effects\" and \"only if something came off\" it is a reward: \"Clear DoT on the target for +55% Storm blade on me\". Cast animation: the game plays the casting of the cast spell's own school, so Sun or Shadow casts a real Sun or Shadow spell of that kind (example: 666, Shadow, blade, on me, Sun animation casts Sun Blade, then makes it a +666% Shadow blade; the game shows the Sun Blade's icon). Sun has a blade, shield and absorb, Shadow all five kinds, Moon a blade, Star a blade, shield and absorb; any other pick is cast normally. With \"shown as\" filled in, that spell's school decides. A global is a battlefield bubble: it takes the current one's place, as a real one does, and its school's real bubble is cast for it (Wyldfire, Balefrost, Darkwind, Blood Moon, Circle of Thorns, Deadzone, Balance of Power, Shadow; Power Play for all schools; Sanctuary for more healing, Doom and Gloom for less), then made your size: \"100, Storm, global\" is a +100% Storm global. The target does not matter for a global. A yellow note shows when the game will show an effect of another school than yours (it then uses it on other hits than the server does). Silent: the spell cast for it plays no casting animation; the effect still shows. More kinds: pierce boost (spear) and less pierce, pierce trap and pierce shield, flat damage boost and weakness, flat shield and trap, accuracy boost and loss, critical and critical block boosts, healing given or taken (more or less, percent or flat), damage over time taken (more or less), stun block, stun resist, damage cap (the most a hit can do), power pip chance, pip conversion and dispel block. For each, the game's own spell of that kind closest in school and size is cast so the game draws it, then made your size. Flat kinds, absorb, stun block, damage cap, pip conversion and dispel block are a number, not a percent (the % sign is then ignored). Aura (several stats): an aura that changes several stats at once for its rounds (the aura's own if 0), never used up, one aura at a time as in the game. The amount is its damage; \"aura stats\" adds more, with commas: damage, resist, heal, healing in, accuracy, pierce, critical, block, stun resist, power pip, each with a number (minus lowers it). Example: 50, Fire, aura, stats \"resist 20, accuracy 15, pierce 10\" is +50% Fire damage, 20% less damage taken, +15% accuracy and +10% pierce. On itself, the game's aura most like it is cast so it shows (or \"shown as\"), with your numbers on the stats it has; on another one it goes on unseen. Chromatic schools are picked when the block runs: \"each target's own school\" puts a Fire weakness on a Fire wizard and an Ice one on an Ice wizard (ALL PLAYERS: each gets their own); \"the school of whoever set it off\" gives itself a blade of the attacker's school; \"one of each school on the other side\" gives one of each players' schools.",
            "Put a {Amount} {School} {Kind} on {Target} for {Rounds} rounds, shown as {Spell}, with the {Look} cast animation {Silent} {AuraStats} (or as big as variable {AmountVar})", [F("Amount", CheatFieldType.Percent, 50, max: 100_000), F("School", CheatFieldType.Choice, "", s_anySchool), F("Kind", CheatFieldType.Choice, "Blade", s_addKinds), F("Target", CheatFieldType.Target, "Self"), F("Rounds", CheatFieldType.Number, 0, max: 99), F("Spell", CheatFieldType.Words, ""), F("Look", CheatFieldType.Choice, "", s_castLooks),
             new("Silent", CheatFieldType.Toggle, false, Label: "silent"), F("AuraStats", CheatFieldType.AuraStats, ""), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, b.Text("Kind") == "Aura"
                ? $"put an aura of {(b.Number("Amount") != 0 ? $"damage {b.Number("Amount")}%" : "")}{(b.Text("AuraStats").Length > 0 ? (b.Number("Amount") != 0 ? ", " : "") + b.Text("AuraStats") : "")}{(SchoolWord(b).Length > 0 ? $" ({SchoolWord(b).Trim()})" : "")}{Target(b)} for {(b.Number("Rounds") > 0 ? RoundsFor(b) : "its own rounds")}{(b.Flag("Silent") ? ", silent" : "")}"
                : b.Text("Kind") is "Global" or "HealGlobal" or "GloomGlobal"
                ? $"put up a {(b.Text("Kind") == "GloomGlobal" ? "-" : "+")}{b.Number("Amount")}% {(b.Text("Kind") == "Global" ? SchoolWord(b) : "")}{LabelOf(s_addKinds, b.Text("Kind"))}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}{(b.Text("Look").Length > 0 && b.Text("Spell").Length == 0 ? $", {b.Text("Look")} cast" : "")}"
                : b.Text("Kind") is "DoT" or "HoT"
                ? $"put a {N(b.Number("Amount"))} {SchoolWord(b)}{LabelOf(s_addKinds, b.Text("Kind"))} over {Math.Max(1, b.Number("Rounds") > 0 ? b.Number("Rounds") : 3)} rounds{Target(b)}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}"
                : $"put a {b.Number("Amount")}{(s_flatKinds.Contains(b.Text("Kind")) ? "" : "%")} {SchoolWord(b)}{LabelOf(s_addKinds, b.Text("Kind"))}{Target(b)}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}{(b.Text("Look").Length > 0 && b.Text("Spell").Length == 0 ? $", {b.Text("Look")} cast" : "")}")),
        new("D.Aura", BlockKind.Do, "sparkle", "aura", "An aura of its own: one or more stats at once for its rounds (the shown aura's own if 0), never used up, and one aura at a time as in the game (a new one takes the old one's place). Pick each stat in the 🌀 list: its school (\"block's school\" = all), the stat (damage, resist, accuracy, pierce, critical, critical block, healing given, healing taken, stun resist, power pip chance) and how much; a minus number lowers it. Example: Shadow damage 100, Fire resist 20, accuracy 15. Works on bosses, creatures and players alike. On itself, the game's aura most like it is cast so the game draws it (or \"shown as\"), with your numbers on the stats that aura has; on anyone else it goes on unseen. Silent: no casting animation.",
            "Put an aura on {Target} for {Rounds} rounds {AuraStats} shown as {Spell} {Silent}",
            [F("Target", CheatFieldType.Target, "Self"), F("Rounds", CheatFieldType.Number, 0, max: 99), F("AuraStats", CheatFieldType.AuraStats, "damage 25"),
             F("Spell", CheatFieldType.Words, ""), new("Silent", CheatFieldType.Toggle, false, Label: "silent")],
            b => $"put an aura of {(b.Text("AuraStats").Length > 0 ? b.Text("AuraStats") : "no stats")}{Target(b)} for {(b.Number("Rounds") > 0 ? $"{b.Number("Rounds")} rounds" : "its own rounds")}{(b.Text("Spell").Length > 0 ? $" (shown as {b.Text("Spell")})" : "")}{(b.Flag("Silent") ? ", silent" : "")}"),
        new("D.IfCleared", BlockKind.Do, "broom", "only if something came off", "Put it after \"remove effects\" (or remove charms, wards or auras): the next blocks (how many) go off only if at least this many effects came off. With \"once for each\" they go off once for each effect that came off. Example, Reap the Whirlwind's \"Clear DoT for +55% Storm blade\": remove 1 damage over time from whoever the spell was aimed at, then this block (next 1 block), then put a 55% Storm blade on itself.",
            "Do the next {Count} blocks only if at least {Amount} effects came off {Each}", [new("Count", CheatFieldType.Number, 1, Max: 20, Min: 1), new("Amount", CheatFieldType.Number, 1, Max: 99, Min: 1), new("Each", CheatFieldType.Toggle, false, Label: "once for each")],
            b => b.Flag("Each") ? $"(the next {Math.Max(1, b.Number("Count"))} once for each effect that came off)"
                : $"(the next {Math.Max(1, b.Number("Count"))} only if {Math.Max(1, b.Number("Amount"))} or more came off)"),
        new("D.Boost", BlockKind.Do, "bolt", "boost or enrage", "Raises (or with a minus number, lowers) its damage, resist, healing or accuracy for some rounds; 0 rounds is the rest of the battle. It is never used up and stacks: on a \"from round … on\" cheat it is an enrage.",
            "Change the {Kind} of {Target} by {Amount} for {School} spells for {Rounds} rounds (or by variable {AmountVar})", [F("Kind", CheatFieldType.Choice, "Damage", s_boostKinds), F("Target", CheatFieldType.Target, "Self"), new("Amount", CheatFieldType.Number, 10, Max: 1000, Min: -100), F("School", CheatFieldType.Choice, "", s_anySchool), F("Rounds", CheatFieldType.Number, 0, max: 99), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"{LabelOf(s_boostKinds, b.Text("Kind"))} {(b.Number("Amount") >= 0 ? "+" : "")}{b.Number("Amount")}%{(b.Text("School").Length > 0 ? $" ({SchoolName(b.Text("School"))})" : "")}{Target(b)} for {RoundsFor(b)}")),
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
            "Heal {Target} by {Amount} (or by variable {AmountVar})", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.HealAmount), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"heal{Target(b)} by {(b.Flag("Percent") ? $"{b.Number("Amount")}%" : N(b.Number("Amount")))}")),
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
        new("D.Chat", BlockKind.Do, "chat", "chat message", "Any words you like, sent to every player's chat window (in a player cheat, to your own). {boss}, {player}, {round} and a variable's name such as {rage} are filled in.",
            "Tell every player in chat: {Text}", [F("Text", CheatFieldType.Words, "")], b => $"tell everyone \"{b.Text("Text")}\""),
        // Niduus, 2026-10-26: "custom boss words that appear over me and in combat".
        new("D.Bubble", BlockKind.Do, "quote", "speech bubble", "Any words you like, in a chat bubble over whoever you pick (itself, the attacker, a player...) while the round plays, and in every player's chat window with their name. Works for bosses and for you in a player cheat. {boss}, {player}, {round} and a variable's name such as {rage} are filled in. Unlike \"say a line\" (only the game's own lines, in the boss's speech box), these can be any words.",
            "Say {Text} in a bubble over {Target}", [F("Text", CheatFieldType.Words, ""), F("Target", CheatFieldType.Target, "Self")],
            b => $"say \"{b.Text("Text")}\" in a bubble{Target(b, " over itself").Replace(" on ", " over ")}"),
        new("D.Announce", BlockKind.Do, "chat", "announce to the battle", "A server announcement, as the Announcement card sends it, but only to the wizards in this battle: a message box with \"Announcement\" on top, an \"Announcement:\" chat line, or both. {boss} in the words is the boss (or you, in a player cheat), {player} the wizard who set it off, {round} the round. Example: a Round 3 cheat that does this with \"The ground trembles...\" as a box. Unlike \"say a line\" (a game line over the boss) and \"chat message\" (chat with the boss's name in front), it reads as from the server. A variable's name in braces, such as {rage}, is its number.",
            "Announce {Text} to {Who} {Style}", [F("Text", CheatFieldType.Words, ""), F("Who", CheatFieldType.Choice, "", s_announceWho), F("Style", CheatFieldType.Choice, "Box", s_announceStyles)],
            b => $"announce \"{b.Text("Text")}\" to {LabelOf(s_announceWho, b.Text("Who"))} {LabelOf(s_announceStyles, b.Text("Style").Length > 0 ? b.Text("Style") : "Box")}"),
        new("D.Pips", BlockKind.Do, "pips", "give or take pips", "Gives pips (to fill empty slots first) or takes them away. A school's pips too: Crash the Bandit Coot spends his two matching Fire pips (take 2 Fire school pips from itself) after his Crash Attack.",
            "{Amount} {Kind} pips, for {Target} (or as many as variable {AmountVar})", [F("Amount", CheatFieldType.Sign, 1, max: 14), F("Kind", CheatFieldType.Choice, "Normal", s_pipKinds), F("Target", CheatFieldType.Target, "Self"), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"{(b.Number("Amount") < 0 ? "take" : "give")} {Math.Abs(b.Number("Amount"))} {LabelOf(s_pipKinds, b.Text("Kind"))} pips{Target(b).Replace(" on ", b.Number("Amount") < 0 ? " from " : " to ")}")),
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
        new("D.FreeCast", BlockKind.Do, "sparkle", "cast for free", "A free spell, as the .c1 and .c2 commands cast it. Pick when: right away, before or after your card, or first or last in the round. Silent skips the casting animation; enchanted adds the enchantments you pick (several stack), or the best one that fits when none is picked. Self spells go on the target: a spell that only goes on its caster (a self blade, Shadow Shrike) goes on the target you pick instead, once on each for every ally or every player. For example, Shadow Shrike on the weakest ally, or a self blade on every ally." + PICK_HELP,
            "Cast {Spell} on {Target} {Timing} {Times} times {Silent} {Enchant} {Enchants} {OnTarget} picking {Count} of {Pick}",
            [F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target), F("Timing", CheatFieldType.Choice, "Now", s_timings),
             F("Times", CheatFieldType.Number, 1, max: 10), new("Silent", CheatFieldType.Toggle, false, Label: "silent"), new("Enchant", CheatFieldType.Toggle, false, Label: "enchanted"),
             F("Enchants", CheatFieldType.Enchants), new("OnTarget", CheatFieldType.Toggle, false, Label: "self spells go on the target"),
             F("Count", CheatFieldType.Number, 0, max: 8), F("Pick", CheatFieldType.Choice, "", s_picks)],
            b => $"cast {SpellName(b)}{Target(b)} {LabelOf(s_timings, b.Text("Timing"))}{PickWords(b)}"
                 + $"{(b.Number("Times") > 1 ? $" x{b.Number("Times")}" : "")}{(b.Flag("Silent") ? " (silent)" : "")}{(b.Flag("Enchant") ? b.List("Enchants") is { Count: > 0 } picked ? $" (enchanted: {string.Join(" + ", picked)})" : " (enchanted)" : "")}{(b.Flag("OnTarget") ? " (self spells on the target)" : "")}"),
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
        // Do... (Niduus, 2026-10-26: "You can legit do anything")
        new("D.SetVar", BlockKind.Do, "list", "set a variable", "Keeps a number by a name, such as rage, for the If \"a variable\" and for messages: {rage} in a message's words shows it. Its own: each fighter has its own; the battle's: one for everyone.",
            "Set {Scope} variable {Var}: {Op} {Amount} (or variable {AmountVar})", [F("Scope", CheatFieldType.Choice, "", s_scopes), F("Var", CheatFieldType.Words, "rage"), F("Op", CheatFieldType.Choice, "Add", s_varOps), new("Amount", CheatFieldType.Number, 1, Max: 1_000_000, Min: -1_000_000), F("AmountVar", CheatFieldType.Words, "")],
            b => b.Text("AmountVar").Trim().Length > 0 ? $"{b.Text("Var")}: {LabelOf(s_varOps, b.Text("Op").Length > 0 ? b.Text("Op") : "Set")} {b.Text("AmountVar").Trim()}"
                : $"{b.Text("Var")}: {LabelOf(s_varOps, b.Text("Op").Length > 0 ? b.Text("Op") : "Set")} {N(b.Number("Amount"))}"),
        new("D.ConvertEffects", BlockKind.Do, "swap", "convert effects", "Takes effects of a kind off the target and gives each one back as another kind, the same size and school, drawn by the game: every trap on you becomes a blade of its size. 0 means all of them.",
            "Turn {Count} {EffectSchool} {Kind} on {Target} into {To} {Silent}", [F("Count", CheatFieldType.Number, 0, max: 99), F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Trap", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("To", CheatFieldType.Choice, "Blade", s_convertTo), new("Silent", CheatFieldType.Toggle, false, Label: "silent")],
            b => $"turn {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")}{Universal(b)} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b)} into {LabelOf(s_convertTo, b.Text("To"))}"),
        new("D.CopyEffects", BlockKind.Do, "copy", "copy effects", "Copies effects of a kind from the target onto itself (you, in a player cheat), the same size and school; the target keeps them. 0 means all of them.",
            "Copy {Count} {EffectSchool} {Kind} from {Target} {Silent}", [F("Count", CheatFieldType.Number, 0, max: 99), F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Blade", s_hasKinds), F("Target", CheatFieldType.Target, "Caster"), new("Silent", CheatFieldType.Toggle, false, Label: "silent")],
            b => $"copy {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")}{Universal(b)} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b).Replace(" on ", " from ")}"),
        new("D.ScaleEffects", BlockKind.Do, "bolt", "make effects bigger or smaller", "Every effect of a kind on the target gets this percent of its size: 200 doubles, 50 halves. The game keeps showing the old number, but the hits use the new one.",
            "Make {EffectSchool} {Kind} on {Target} {Amount} % of their size", [F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Blade", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), new("Amount", CheatFieldType.Number, 200, Max: 10_000, Min: 0)],
            b => $"make{Universal(b)} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b)} {b.Number("Amount")}% of their size"),
        new("D.EffectsSchool", BlockKind.Do, "swap", "change effects' school", "Every effect of a kind on the target becomes of another school, or universal: a Fire blade becomes a Storm blade. The game keeps showing the old icon, but the hits use the new school.",
            "Make {EffectSchool} {Kind} on {Target} into {School}", [F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Blade", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("School", CheatFieldType.Choice, "", s_anySchool)],
            b => $"make{Universal(b)} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b)} into {(b.Text("School").Length > 0 ? b.Text("School") : "universal")}"),
        new("D.ProtectEffects", BlockKind.Do, "shield", "protect effects", "Effects of a kind on the target can't be removed (or, picked the other way, can be again).",
            "{EffectSchool} {Kind} on {Target} {To}", [F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "Blade", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("To", CheatFieldType.Choice, "", s_protectOnOff)],
            b => $"{Universal(b).Trim()} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b)} {LabelOf(s_protectOnOff, b.Text("To"))}".Trim()),
        new("D.Damage", BlockKind.Do, "hit", "do damage", "The target loses health, by hit points or by a share of its most health, with no spell. It never goes below 1 (add \"defeat\" to finish one off).",
            "Take {Amount} {Percent} health from {Target} (or variable {AmountVar})", [F("Amount", CheatFieldType.Number, 500, max: 1_000_000), new("Percent", CheatFieldType.Toggle, false, Label: "% of the most"), F("Target", CheatFieldType.Target, "Caster"), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"take {N(b.Number("Amount"))}{(b.Flag("Percent") ? "%" : "")} health{Target(b).Replace(" on ", " from ")}")),
        new("D.Drain", BlockKind.Do, "heal", "drain health", "The target loses health (hit points or a share of its most) and it (you, in a player cheat) gets as much back.",
            "Drain {Amount} {Percent} health from {Target} (or variable {AmountVar})", [F("Amount", CheatFieldType.Number, 500, max: 1_000_000), new("Percent", CheatFieldType.Toggle, false, Label: "% of the most"), F("Target", CheatFieldType.Target, "Caster"), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"drain {N(b.Number("Amount"))}{(b.Flag("Percent") ? "%" : "")} health{Target(b).Replace(" on ", " from ")}")),
        new("D.SpellRule", BlockKind.Do, "bolt", "always land, fizzle or crit", "The target's next spells (how many; 0 is the rest of the battle) always land, always fizzle, always crit or never crit. \"Go back to normal\" ends it. Example: when I pass, my next spell crits.",
            "{Target}'s next {Count} spells {Kind}", [F("Target", CheatFieldType.Target, "Self"), F("Count", CheatFieldType.Number, 1, max: 99), F("Kind", CheatFieldType.Choice, "Crit", s_spellRules)],
            b => $"the next {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} spells{Target(b).Replace(" on ", " of ")} {LabelOf(s_spellRules, b.Text("Kind"))}"),
        new("D.HandSize", BlockKind.Do, "cards", "hand size", "The target's hand is filled up to this many cards (0 to 7) for the next hands (0 is the rest of the battle). Cards already in the hand stay.",
            "Fill {Target}'s hand up to {Amount} cards for {Rounds} hands", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.Number, 4, max: 7), F("Rounds", CheatFieldType.Number, 1, max: 99)],
            b => $"hand of {b.Number("Amount")} cards{Target(b).Replace(" on ", " for ")} for {(b.Number("Rounds") > 0 ? $"{b.Number("Rounds")} hands" : "the rest of the battle")}"),
        new("D.Reward", BlockKind.Do, "gift", "give a reward", "Gives gold, experience, training points and items (item IDs or treasure card names, commas between; each this many times) right away, as a drop would, with a chat line saying so. For a boss: to every wizard in the battle (or whoever you pick); in a player cheat: to you. Example: when it is defeated, if no one healed, give the rare hat.",
            "Give {Target} {Gold} gold, {Xp} experience, {Tp} training points and items {Items} x {Count}", [F("Target", CheatFieldType.Target, ""), F("Gold", CheatFieldType.Number, 0, max: 1_000_000), F("Xp", CheatFieldType.Number, 0, max: 1_000_000), F("Tp", CheatFieldType.Number, 0, max: 100), F("Items", CheatFieldType.Words, ""), F("Count", CheatFieldType.Number, 1, max: 99)],
            b => $"give{Target(b).Replace(" on ", " ")} {string.Join(", ", new[] { b.Number("Gold") > 0 ? $"{N(b.Number("Gold"))} gold" : "", b.Number("Xp") > 0 ? $"{N(b.Number("Xp"))} XP" : "", b.Number("Tp") > 0 ? $"{b.Number("Tp")} training points" : "", b.Text("Items").Trim().Length > 0 ? $"items {b.Text("Items").Trim()}" : "" }.Where(t => t.Length > 0))}".TrimEnd()),
        new("D.NoFlee", BlockKind.Do, "stop", "lock fleeing", "The target (everyone, if none is picked) can't flee for the rest of the battle: pressing Flee counts as a pass. Pick \"can flee again\" to unlock it.",
            "{Target} {Kind}", [F("Target", CheatFieldType.Target, "Everyone"), F("Kind", CheatFieldType.Choice, "", s_fleeOnOff)],
            b => $"{(Target(b).Trim().Length > 0 ? Target(b).Replace(" on ", "").Trim() : "everyone")} {LabelOf(s_fleeOnOff, b.Text("Kind"))}"),
        new("D.Revive", BlockKind.Do, "heart", "revive a fallen ally", "A fallen one on its side (yours, in a player cheat) gets up with this much health, or this share of their most health: one at random, every one, or itself. Example: when the last ally falls, revive every fallen ally at 30%. The health ring may show it only from the next round.",
            "Revive {Target} with {Amount} {Percent} health (or variable {AmountVar})", [F("Target", CheatFieldType.Choice, "", s_reviveWho), F("Amount", CheatFieldType.Number, 50, max: 1_000_000), new("Percent", CheatFieldType.Toggle, true, Label: "% of the most"), F("AmountVar", CheatFieldType.Words, "")],
            b => SizeFromVar(b, $"revive {LabelOf(s_reviveWho, b.Text("Target"))} with {N(b.Number("Amount"))}{(b.Flag("Percent") ? "%" : "")} health")),
        new("D.Detonate", BlockKind.Do, "bolt", "set off over-time effects now", "Damage over time, heal over time and bombs on the target go off at once with all the rounds they have left (this percent of it; 100 if 0), and are gone. Shields and traps count as for a tick. How many: 0 is all of them. Example: when a player casts a heal, detonate every damage over time on them.",
            "Detonate {Count} {Kind} on {Target} at {Amount} %", [F("Count", CheatFieldType.Number, 0, max: 99), F("Kind", CheatFieldType.Choice, "", s_overTimeKinds), F("Target", CheatFieldType.Target, "Caster"), F("Amount", CheatFieldType.Number, 100, max: 10_000)],
            b => $"set off {(b.Number("Count") > 0 ? N(b.Number("Count")) : "all")} {LabelOf(s_overTimeKinds, b.Text("Kind"))}{Target(b)} now{(b.Number("Amount") is > 0 and not 100 ? $" at {b.Number("Amount")}%" : "")}"),
        new("D.HitShare", BlockKind.Do, "swap", "thorns, lifesteal or hits heal", "For some rounds (0: the rest of the battle) the target gets one of these, a percent of each hit: thorns (whoever hits it takes that much back, never below 1 health), lifesteal (it heals by that much of the damage it deals) or hits heal it (that much of each hit heals it instead; 100 is every hit). 0 percent takes it off. The game shows the hit's normal number; the health ring catches up.",
            "Give {Target} {Kind} {Amount} % for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Self"), F("Kind", CheatFieldType.Choice, "Thorns", s_hitShares), F("Amount", CheatFieldType.Number, 25, max: 1000), F("Rounds", CheatFieldType.Number, 0, max: 99), F("AmountVar", CheatFieldType.Words, "")],
            b => b.Number("Amount") == 0 && b.Text("AmountVar").Trim().Length == 0 ? $"take {LabelOf(s_hitShares, b.Text("Kind")).Split(':')[0]} off{Target(b)}"
                : SizeFromVar(b, $"{LabelOf(s_hitShares, b.Text("Kind"))} {b.Number("Amount")}%{Target(b)} for {RoundsFor(b)}")),
        new("D.StealCard", BlockKind.Do, "cards", "steal a card", "Takes cards (at random, or only of these spells) out of the target's hand at their next hand, and puts them in its own next hand (yours, in a player cheat). A wizard sees the change at the next planning phase.",
            "Steal {Count} {Spell} cards from {Target}", [F("Count", CheatFieldType.Number, 1, max: 7), F("Spell", CheatFieldType.Words, ""), F("Target", CheatFieldType.Target, "Caster")],
            b => $"steal {Math.Max(1, b.Number("Count"))} {(b.Text("Spell").Length > 0 ? b.Text("Spell") + " " : "")}card{(b.Number("Count") > 1 ? "s" : "")}{Target(b).Replace(" on ", " from ")}"),
        new("D.Remember", BlockKind.Do, "list", "remember a number", "Keeps a number read from the battle right now in a variable, for later blocks: put the variable's name in another block's \"as big as variable\" box (a blade as big as a trap), in an \"a variable\" If, or as {name} in a message. " +
            "\"The size of the spell that set it off\": a +30% trap is 30 (pick the Kind to read that part of the spell). The newest / biggest / all: effects of the Kind on the target right now. Example: when an enemy casts a trap, remember the size of the spell (trap) as trap, take its trap off, put a blade as big as variable trap on yourself.",
            "Remember {From} {EffectSchool} {Kind} {Target} as {Scope} variable {Var}", [F("From", CheatFieldType.Choice, "Spell", s_rememberFrom), F("EffectSchool", CheatFieldType.Choice, "", s_effectSchools), F("Kind", CheatFieldType.Choice, "", s_hasKinds), F("Target", CheatFieldType.Target, "Self"), F("Scope", CheatFieldType.Choice, "", s_scopes), F("Var", CheatFieldType.Words, "trap")],
            b => $"remember {LabelOf(s_rememberFrom, b.Text("From").Length > 0 ? b.Text("From") : "Spell")}{Universal(b)}{(b.Text("Kind").Length > 0 ? " " + LabelOf(s_hasKinds, b.Text("Kind")) : "")}{(b.Text("From") is "Spell" or "" or "Round" ? "" : Target(b))} as {b.Text("Var")}"),
        new("D.IfVar", BlockKind.Do, "list", "only if a variable", "The blocks under it (how many) go off only if the variable is so. Example: rage at least 5, then 3 blocks of an enrage.",
            "Only if {Scope} variable {Var} is {Op} {Amount}, do the next {Count} blocks",
            [F("Scope", CheatFieldType.Choice, "", s_scopes), F("Var", CheatFieldType.Words, "rage"), F("Op", CheatFieldType.Choice, ">=", s_ops), new("Amount", CheatFieldType.Number, 5, Max: 1_000_000, Min: -1_000_000), F("Count", CheatFieldType.Number, 1, max: 20)],
            b => $"(the next {Math.Max(1, b.Number("Count"))} only if {b.Text("Var")} is {Op(b)} {N(b.Number("Amount"))})"),
        new("D.Repeat", BlockKind.Do, "layers", "repeat", "The blocks under it (how many) go off this many times in a row.",
            "Do the next {Count} blocks {Times} times", [F("Count", CheatFieldType.Number, 1, max: 20), F("Times", CheatFieldType.Number, 3, max: 20)],
            b => $"(the next {Math.Max(1, b.Number("Count"))} blocks {b.Number("Times")} times)"),
        new("D.MaxHealth", BlockKind.Do, "heart", "most health", "Sets or raises (below 0: lowers) someone's most health; their health stays (never above the new most). The server counts it at once; the game's health bar keeps its old top until it is next sent.",
            "Make the most health of {Target} {Op} {Amount} {Percent}", [F("Target", CheatFieldType.Target, "Self"), F("Op", CheatFieldType.Choice, "Add", s_maxOps), new("Amount", CheatFieldType.Number, 1000, Max: 100_000_000, Min: -100_000_000), new("Percent", CheatFieldType.Toggle, false, Label: "% of the most now")],
            b => $"most health{Target(b)} {LabelOf(s_maxOps, b.Text("Op").Length > 0 ? b.Text("Op") : "Set").Split(' ')[0]} {N(b.Number("Amount"))}{(b.Flag("Percent") ? "%" : "")}"),
        new("D.Clone", BlockKind.Do, "users", "clone someone", "A copy of a creature (or a custom) joins its side, as a minion, in a free circle: the same model, health and cheats. Not for wizards.",
            "Bring in {Count} copies of {Target} on its side", [F("Count", CheatFieldType.Number, 1, max: 3), F("Target", CheatFieldType.Target, "Self")],
            b => $"clone{Target(b)}{(b.Number("Count") > 1 ? $" x{b.Number("Count")}" : "")}"),
        new("D.CopySpell", BlockKind.Do, "sparkle", "copy the spell", "It casts the spell that set this cheat off too (free), on the target or as that spell aims. Use it under a When about a spell.",
            "Cast the spell that set this off too, on {Target}", [F("Target", CheatFieldType.Target)],
            b => $"copy that spell{Target(b)}"),
        new("D.Redirect", BlockKind.Do, "hit", "redirect the spell", "The attacker's spell fizzles, and the attacker casts it (free) on the target picked instead: a random player if none, or e1-e4, AllEnemies... as seen from this one. Like reflect, it goes off before the spell lands.",
            "Stop the attacker's spell and have them cast it on {Target}", [F("Target", CheatFieldType.Target, "Enemy")],
            b => $"redirect it{Target(b, " on a random player")}"),
        new("D.StealEffects", BlockKind.Do, "shield", "steal effects", "Charms, wards or auras of a kind (any if empty) come off the target and go on this one (0: all of them). They work on the server; the game draws only the ones taken off, not the ones put on.",
            "Take {Count} {Kind} off {Target} and put them on itself", [F("Count", CheatFieldType.Number, 1, max: 20), F("Kind", CheatFieldType.Choice, "", s_hasKinds), F("Target", CheatFieldType.Target, "Caster")],
            b => $"steal {(b.Number("Count") > 0 ? b.Number("Count").ToString(CultureInfo.InvariantCulture) : "all")} {LabelOf(s_hasKinds, b.Text("Kind"))}{Target(b)}"),
        new("D.RunCheat", BlockKind.Do, "puzzle", "run another cheat", "The Do blocks of another of its cheats go off now (1 is the first in its list). Ticked: only if that cheat's own Ifs hold too.",
            "Do cheat {Cheat}'s blocks {Check}", [F("Cheat", CheatFieldType.Number, 1, max: 200), new("Check", CheatFieldType.Toggle, false, Label: "only if its Ifs hold")],
            b => $"run cheat {b.Number("Cheat")}"),
        new("D.CheatOnOff", BlockKind.Do, "stop", "turn a cheat off / on", "Turns one of its cheats (1 is the first in its list) off for the rest of the battle, or back on.",
            "Turn cheat {Cheat} {Kind}", [F("Cheat", CheatFieldType.Number, 1, max: 200), F("Kind", CheatFieldType.Choice, "Off", s_onOff)],
            b => $"turn cheat {b.Number("Cheat")} {LabelOf(s_onOff, b.Text("Kind").Length > 0 ? b.Text("Kind") : "Off")}"),
        new("D.Silence", BlockKind.Do, "stop", "silence", "The target's spells fizzle for this many rounds (from this one when the round is under way). Shown as a fizzle.",
            "Silence {Target} for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Caster"), F("Rounds", CheatFieldType.Number, 1, max: 99)],
            b => $"silence{Target(b)} for {Math.Max(1, b.Number("Rounds"))} rounds"),
        new("D.AddCard", BlockKind.Do, "cards", "add a card to a hand", "Puts a card in someone's next hand (in place of a random card when the hand is full). A wizard sees it in the hand the game shows at the next planning phase; a creature's AI can pick it for its next move.",
            "Put {Count} {Spell} in the next hand of {Target}", [F("Count", CheatFieldType.Number, 1, max: 7), F("Spell", CheatFieldType.Spell), F("Target", CheatFieldType.Target, "Self")],
            b => $"add {SpellName(b)}{(b.Number("Count") > 1 ? $" x{b.Number("Count")}" : "")} to the hand{Target(b)}"),
        new("D.Discard", BlockKind.Do, "cards", "discard cards", "Takes cards out of someone's next hand, as if discarded: at random, or only cards of these spells (names or numbers, commas between) or of this kind. 0 is all that fit. A wizard sees the smaller hand at the next planning phase; a creature's AI cannot pick them.",
            "Discard {Count} cards of {Spell} {Kind} from the next hand of {Target}", [F("Count", CheatFieldType.Number, 1, max: 7), F("Spell", CheatFieldType.Words), F("Kind", CheatFieldType.Category), F("Target", CheatFieldType.Target, "Caster")],
            b => $"discard {(b.Number("Count") > 0 ? b.Number("Count").ToString(CultureInfo.InvariantCulture) : "all")} {(b.Text("Spell").Length > 0 ? b.Text("Spell") : b.Text("Kind").Length > 0 ? LabelOf(CATEGORIES, b.Text("Kind")) : "random")} cards{Target(b)}"),
        new("D.ShuffleHand", BlockKind.Do, "shuffle", "shuffle the hand", "Someone's hand goes back into their deck and a new hand is drawn (ticked: the used and discarded cards go back in too). A wizard sees the new hand at the next planning phase; a creature's AI picks from it.",
            "Shuffle the hand of {Target} back into the deck {DeckToo}", [F("Target", CheatFieldType.Target, "Caster"), new("DeckToo", CheatFieldType.Toggle, false, Label: "used cards too")],
            b => $"shuffle the hand{Target(b)}{(b.Flag("DeckToo") ? " (used cards too)" : "")}"),
        new("D.LockCards", BlockKind.Do, "lock", "keep cards out", "Cards of these spells (names or numbers, commas between) or of this kind stay out of someone's next hands (how many hands), back in the deck. A wizard does not see them in the hands the game shows; a creature's AI cannot pick them.",
            "Keep {Spell} {Kind} out of the next {Rounds} hands of {Target}", [F("Spell", CheatFieldType.Words), F("Kind", CheatFieldType.Category), F("Rounds", CheatFieldType.Number, 1, max: 99), F("Target", CheatFieldType.Target, "Caster")],
            b => $"keep {(b.Text("Spell").Length > 0 ? b.Text("Spell") : LabelOf(CATEGORIES, b.Text("Kind")))} cards out of {Math.Max(1, b.Number("Rounds"))} hands{Target(b)}"),
        new("D.ExtraTurn", BlockKind.Do, "repeat", "extra turn", "Someone casts again at the end of the round, after everyone, for this many rounds (1 is this round): the spell picked, or else the one they cast this round, on the same target if it still stands. It is free and drawn as a normal cast; a wizard does not pick a second card.",
            "Give {Target} an extra turn with {Spell} for {Rounds} rounds, {Times} times a round", [F("Target", CheatFieldType.Target, "Self"), F("Spell", CheatFieldType.Spell), F("Rounds", CheatFieldType.Number, 1, max: 99), F("Times", CheatFieldType.Number, 1, max: 3)],
            b => $"extra turn{Target(b)}{(b.Text("Spell").Length > 0 || b.Id("SpellID") > 0 ? $" with {SpellName(b)}" : "")} for {Math.Max(1, b.Number("Rounds"))} rounds"),
        new("D.PipGain", BlockKind.Do, "pips", "pips a round", "Someone gets this many pips each round (0: none) from the next round, for this many rounds (0: the rest of the battle). The power pip chance, if given, is how many in a hundred are power pips. The game shows the pips as they come.",
            "Give {Target} {Amount} pips a round, {Power} % power pips, for {Rounds} rounds", [F("Target", CheatFieldType.Target, "Self"), F("Amount", CheatFieldType.Number, 2, max: 14), F("Power", CheatFieldType.Words), F("Rounds", CheatFieldType.Number, 0, max: 99)],
            b => $"{b.Number("Amount")} pips a round{Target(b)}{(b.Text("Power").Length > 0 ? $", {b.Text("Power").TrimEnd('%')}% power" : "")}{(b.Number("Rounds") > 0 ? $" for {b.Number("Rounds")} rounds" : "")}"),
        new("D.ChangeSchool", BlockKind.Do, "school", "change school", "Someone counts as another school for this many rounds (0: the rest of the battle; from this one when the round is under way): for school Ifs, spell needs, school pips, mastery and archmastery. Ticked: its boosts and resists of its own school and the new one trade places. The game shows nothing; only the battle counts it.",
            "Make {Target} count as {School} for {Rounds} rounds {Stats}", [F("Target", CheatFieldType.Target, "Self"), F("School", CheatFieldType.Choice, "Fire", s_changeSchools), F("Rounds", CheatFieldType.Number, 3, max: 99), new("Stats", CheatFieldType.Toggle, false, Label: "boosts and resists too")],
            b => $"counts as {SchoolName(b.Text("School"))}{Target(b)}{(b.Number("Rounds") > 0 ? $" for {b.Number("Rounds")} rounds" : "")}{(b.Flag("Stats") ? " (stats too)" : "")}"),
        new("D.LockCircle", BlockKind.Do, "lock", "lock a circle", "Locks a battle circle for this many rounds (0: the rest of the battle; from this one when the round is under way): no one can join the battle there, no walking-in monster or wizard and no summoned minion. Whoever already stands on it stays. Works the same for a wizard's cheat and a creature's. The game draws nothing on a locked circle.",
            "Lock the {Kind} for {Rounds} rounds", [F("Kind", CheatFieldType.Choice, "Moon", s_circles), F("Rounds", CheatFieldType.Number, 0, max: 99)],
            b => $"locks {CircleName(b.Text("Kind"))}{(b.Number("Rounds") > 0 ? $" for {b.Number("Rounds")} rounds" : " for the battle")}"),
        new("D.UnlockCircle", BlockKind.Do, "unlock", "unlock a circle", "Opens a circle a lock block closed, before its rounds run out.",
            "Unlock the {Kind}", [F("Kind", CheatFieldType.Choice, "Moon", s_circles)],
            b => $"unlocks {CircleName(b.Text("Kind"))}"),
        new("D.Other", BlockKind.Do, "puzzle", "something else", "An action this app does not know, kept as the file has it.",
            "Do {Do}", [F("Do", CheatFieldType.Words)], b => $"do {b.Text("Do")}"),
    ]);

    private const string TAG_HELP = " Tag (optional): a name of your own, such as BABA, put on every effect this block puts on. The \"has a charm or ward\" If,"
        + " the \"effects on someone\" If, \"remove effects\" and the When \"runs out of tagged effects\" can then look at only those.";

    private static CheatSpec[] WithTags(CheatSpec[] specs) => [.. specs.Select(spec =>
        s_tagged.Contains(spec.Id)
            ? spec with {
                Help = spec.Help + TAG_HELP, Sentence = spec.Sentence + " tagged {Tag}", Fields = [.. spec.Fields, F("Tag", CheatFieldType.Words, "")],
                Summary = b => spec.Summary(b) + (b.Text("Tag").Length > 0 ? $" (tagged \"{b.Text("Tag")}\")" : ""),
            }
            : spec.Id == "D.RemoveEffects"
            ? spec with {
                Help = spec.Help + " Tag (optional): only effects with this tag (a tag a block put on, such as BABA) come off.",
                Sentence = spec.Sentence + " tagged {Tag}", Fields = [.. spec.Fields, F("Tag", CheatFieldType.Words, "")],
                Summary = b => spec.Summary(b) + (b.Text("Tag").Length > 0 ? $" (only those tagged \"{b.Text("Tag")}\")" : ""),
            }
            : spec)];

    private static readonly Dictionary<string, CheatSpec> s_byId = All.ToDictionary(s => s.Id);

    internal static CheatSpec Get(string id) => s_byId.TryGetValue(id, out var spec) ? spec : s_byId["D.Other"];

    /// <summary>Whether an If block is one entry of the cheat's Conditions (the "C." blocks).</summary>
    internal static bool IsCondition(string id) => id.StartsWith("C.", StringComparison.Ordinal);

    /// <summary>The server's "If" of a condition block.</summary>
    internal static string ConditionOf(CheatBlock block) => block.Spec == "C.Other" ? block.Text("If") : block.Spec[2..];

    /// <summary>The block for a Conditions entry's "If", or "C.Other" for one this app does not know.</summary>
    internal static CheatSpec ConditionSpec(string kind)
        => All.FirstOrDefault(s => IsCondition(s.Id) && s.Id != "C.Other" && s.Id[2..].Equals((kind ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? s_byId["C.Other"];

    // " universal" or " Fire" for a block's EffectSchool, else nothing.
    private static string Universal(CheatBlock b) => b.Text("EffectSchool").Trim() switch {
        "" => "", "Universal" => " universal", var school => " " + school,
    };

    // A block's amount taken from a variable (AmountVar; the Remember block keeps one).
    private static string SizeFromVar(CheatBlock b, string text)
        => b.Text("AmountVar").Trim().Length > 0 ? $"{text} (as big as {b.Text("AmountVar").Trim()})" : text;

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
