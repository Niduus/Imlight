/*
 * Boss cheats (Niduus, 2026-10-21), read from spiraldb-extra/BossCheats/*.json, one file per boss template.
 *
 * A cheat is a move a boss makes outside its own turn: a spell cast for free, a line shown over the boss, or both.
 * The game files hold the cheat spells (Spells/G14-CombatTrigger-Spells, AZ-CombatTrigger-Spells and others) and the
 * cheat lines, but not when a boss uses them; that lives on the real game's servers, so each boss's file says it.
 *
 * When:
 *   BattleStart   the boss's first round (a boss added with .joinme starts its own count).
 *   RoundStart    the start of a round: Round = the boss's Nth round, Every = every Nth round, neither = each round,
 *                 both = round Round, then every Every rounds after it (Round 3, Every 5: rounds 3, 8, 13...).
 *                 NoAllies: only when no creature is left standing on the boss's side.
 *   RoundEnd      the end of a round, after everyone's move, counted the same way.
 *   HealthBelow   the boss's health falls under Percent of its most, or under Health (hit points) when that is set.
 *   PlayerCasts   a player's spell lands. School, Kind (the card's type: Damage, Heal, Ward, Charm, Aura, Global,
 *                 Steal, Manipulation, Shadow...) and Spells (card names) narrow it. OnBoss: only a spell aimed at
 *                 the boss. SingleTarget: only a spell that hits one target.
 *   PlayerPasses  a player passes.
 * Target: Self, Caster (the player who set it off), Receiver (whoever that player's spell was aimed at), Enemy (a random
 * player), FirstEnemy, AllEnemies, or empty to pick as the spell aims. Line: words from the game's own line (as .ca
 * finds it) or its locale key. Once: only the first time. Chance: percent, 100 if left out.
 * Phase: the cheat works only in that phase of the fight (0 or left out = every phase; a boss starts in phase 1).
 * Rounds count from the start of the phase. SetPhase moves the boss on to that phase from the next round.
 * SetHealth: the boss's most and current health become this, even after it was defeated (a second form).
 * Defeat: the boss is defeated after the cheat, if its spell did not do it.
 * Spawn: a creature template that comes into the battle on the boss's side (a second creature with its own model, such
 * as a minion). It takes a free enemy circle and fights from the next round with its own cheat file, counting its
 * rounds from its first round; the boss is defeated (with Defeat) and the line shows over it, and Cast and SetPhase are
 * not used. With no circle free, SetHealth (if set) brings the boss back in its own circle instead, as if there were
 * no Spawn and no Defeat. Drops stay with the boss's own template (NpcDropTable).
 * Silent: the cheat's spell goes off without the casting animation (its line still shows).
 * A boss polymorphed into another creature for the rest of the battle (a second form in its own circle, such as Haunted
 * Malistaire through "Afterlife - Malistaire 02") uses that creature's cheat file from then on, if it has one, starting
 * again at phase 1 from the next round (CombatPolymorph.cs).
 * Casts: several spells instead of Cast, each { "Spell": name, "SpellID": id (wins over the name), "Silent": true,
 *   "DispelImmune": false }, cast in order, or one of them at random with Random: true. A cheat's spell never fizzles
 *   and works through stun; DispelImmune (true if left out, and always for Cast) keeps a Dispel on the boss from
 *   stopping it, false lets a Dispel of the spell's school stop it as it stops a player's spell.
 * The app's Bosses tab changes, turns off and adds cheats in BossOverrides/<template id>.json, laid over this file
 * (BossOverrides.CheatsOf); this file itself is never changed by it.
 *
 * Custom cheats (Niduus, 2026-10-22: "Custom Bosses so cheats can have their conditions and such able to be modified and
 * created"), made as When -> If -> Do blocks in the app, add to the fields above:
 *   When         also BossHit (a spell from the other side lands on the boss and hurts it: School, Kind, Category and
 *                Spells narrow it), MinionDefeated (a creature on the boss's side is defeated) and Pips (at the start
 *                of a round, the boss, or with PipsOf "Enemy" any player, has at least Pips pips; a power pip is one).
 *   Category     for PlayerCasts and BossHit: what the card does, worked out from its effects: Damage, Heal, Shield,
 *                Trap, Blade, Weakness, Aura, Global, Minion, Stun, Steal, Dispel, Polymorph.
 *   CasterSchool only when the player who set it off is of this school.
 *   HealthAbove, HealthUnder   only while the boss's health is above / under this percent of its most.
 *   MaxTimes     at most this many times a battle (0 = no limit). Cooldown: at least this many rounds between two.
 *   Actions      what it does, in order, after the fields above (Cast, Line, SetPhase... count as the first actions):
 *     { "Do": "Cast", "Spell"/"SpellID", "Target", "Silent", "DispelImmune" }   a free spell.
 *     { "Do": "Polymorph", "Spell", "Target" }   a polymorph spell. One that changes the boss for good (Afterlife -
 *                                           Malistaire 02, a kPolymorph) goes on the boss itself unless Target says
 *                                           otherwise; one that attacks in another shape (Polymorph - Dragon NA: he
 *                                           turns into the Black Dragon, hits a player and turns back, all in its
 *                                           cinematic) aims as Target or the spell says. Each is logged as
 *                                           "... polymorph with <spell>: ...".
 *     { "Do": "Interrupt" }                 the player's spell that set it off fizzles. A cheat with it (PlayerCasts
 *                                           or BossHit) goes off before that spell instead of after it.
 *     { "Do": "RemoveCharms"/"RemoveWards", "Kind": "Good"/"Bad"/"All", "Count" (0 = all), "Target" }
 *     { "Do": "AddEffects", "Spell", "Target" }   the charms and wards of a spell go on, with no casting.
 *     { "Do": "Heal", "Amount", "Percent" (Amount is percent of the most health), "Target" (Self if left out) }
 *     { "Do": "Minion", "Creature", "Custom" }    a creature (or a custom of the Customs tab) joins the boss's side.
 *     { "Do": "Form", "Creature", "Custom", "ReplaceBoss" }   a second form, as Spawn (ReplaceBoss as Defeat).
 *     { "Do": "SetHealth", "Amount" }  { "Do": "SetPhase", "Phase" }  { "Do": "Defeat" }
 *     { "Do": "Say", "Line" }               a game line over the boss; "Text": words sent to every player's chat.
 *     { "Do": "Pips", "Amount" (below 0 takes), "Kind": "Normal"/"Power"/"Shadow", "Target" (Self if left out) }
 *   An action with "Off": true is skipped. Target also takes Attacker (as Caster), WeakestEnemy, Ally (a random one
 *   on the boss's side) and AllAllies.
 *
 * More (Niduus, 2026-10-22: "Get more expansive with cheats"; CombatResolver.CheatSteps.cs). Old files read as before:
 *   Spells + AnyVersion   a plain name is every version of that spell ("Scald" is also "Scald (Treasure Card)",
 *                "Scald - Item Card", "Scald - Mob"...; SpellNameFamily.cs). Without AnyVersion, exact names as before.
 *                Cast, Polymorph and AddEffects take a plain name too: a Spell that is no spell's own name casts the
 *                plainest spell of that family. RemoveCharms / RemoveWards take a Spell: only those from that spell.
 *   Category     may hold several kinds with commas ("Charm, Ward"); also Charm (any blade, weakness or other charm),
 *                Ward (any shield, trap or absorb), Shadow, Enchantment and Treasure.
 *   CostMin, CostMax   the pip cost of the spell (CostMax 0 = no top; an X spell costs 0).
 *   When         also PlayerHeals, PlayerCharms, PlayerWards, PlayerAuras, PlayerGlobals, PlayerMinions, PlayerShadows,
 *                PlayerDamages (PlayerCasts for that kind of card), PlayerCrits (a player's spell was a critical hit),
 *                PlayerDefeated (a player falls; Receiver is that player), PlayerHealthBelow (a player's health first
 *                drops under Percent, or Health; once each player; Caster is that player), PlayerFlees (a player ran;
 *                answered at the start of the next round) and FromRound (every round, or every Every rounds, from
 *                round Round on: an enrage).
 *   PlayersMin, PlayersMax   only while this many players stand. FromRound, ToRound: only in these battle rounds.
 *   HasAllies    only while someone else on its side stands.
 *   Actions also:
 *     { "Do": "Reflect" }                   the player's spell fizzles and the boss casts it back (as Interrupt, before).
 *     { "Do": "StealPips", "Amount", "Kind", "Target" (Caster if left out) }   taken from them, given to the boss.
 *     { "Do": "RemoveAuras", "Target" (AllEnemies) }
 *     { "Do": "AddCharm", "Kind": Blade/Weakness/Shield/Trap/Absorb, "Amount" (percent; hit points for Absorb),
 *       "School" ("" = all), "Target" (Self, or Caster for a weakness or trap) }   used up as a real one.
 *     { "Do": "Boost", "Kind": Damage/Resist/Heal/Accuracy, "Amount" (percent, below 0 lowers), "School", "Rounds"
 *       (0 = the rest of the battle), "Target" (Self) }   an aura-like effect that is never used up (enrage: Boost
 *       Damage 10 on a FromRound cheat; it stacks). A new aura cast on that target takes it off, as auras do.
 *     { "Do": "Immune", "School" ("" = all), "Rounds", "Target" (Self) }   a -100% ward of that school that is never
 *       used up (pierce still gets through, as against any shield).
 *     { "Do": "Stun", "Rounds" (1), "Target" (Caster) }   as a stun spell does, with no animation.
 *     { "Do": "HealthTo", "Amount" (percent of the most health), "Target" (Self) }   never below 1.
 *     { "Do": "SwapHealth", "Target" (Caster) }   the boss and the target swap their share of health.
 *     { "Do": "PickOne", "Count" (0 = all the rest), "Weights": "2, 1, 1" }   of the next Count actions only one goes
 *       off, picked at random by Weights (1 each if left out).
 * Each cheat made or changed in the app (and each cheat of a custom) writes "Custom cheat | ..." to the log as it goes off.
 * Player cheats (PlayerCheats.cs) use these same cheats for the wizard played on the server's own PC.
 *
 * File settings: ShadowPips is how many shadow pips the boss can hold (0 or left out = the [Battle] settings), and
 * ShadowPipChance the percent a round to gain one when its stats give none (25 if left out).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class BossCheats {

    internal sealed class BossCheatFile {
        public ulong TemplateID { get; set; }
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public int ShadowPips { get; set; }
        public int ShadowPipChance { get; set; } = 25;
        public List<BossCheat> Cheats { get; set; } = [];
    }

    internal sealed class BossCheat {
        public string When { get; set; } = "";
        public int Round { get; set; }
        public int Every { get; set; }
        public int Percent { get; set; }
        public string School { get; set; } = "";
        public string Kind { get; set; } = "";
        public List<string> Spells { get; set; } = [];
        public string Cast { get; set; } = "";
        public string Target { get; set; } = "";
        public string Line { get; set; } = "";
        public bool Once { get; set; }
        public int Chance { get; set; } = 100;
        public string Notes { get; set; } = "";
        public int Health { get; set; }
        public bool NoAllies { get; set; }
        public bool OnBoss { get; set; }
        public bool SingleTarget { get; set; }
        public int Phase { get; set; }
        public int SetPhase { get; set; }
        public int SetHealth { get; set; }
        public bool Defeat { get; set; }
        public uint Spawn { get; set; }
        public bool Silent { get; set; }
        public List<CheatCast> Casts { get; set; } = [];
        public bool Random { get; set; }
        public string Category { get; set; } = "";
        public string CasterSchool { get; set; } = "";
        public int Pips { get; set; }
        public string PipsOf { get; set; } = "";
        public int HealthAbove { get; set; }
        public int HealthUnder { get; set; }
        public int MaxTimes { get; set; }
        public int Cooldown { get; set; }
        public List<CheatAction> Actions { get; set; } = [];
        // Spells is a list of plain names, each standing for every version of that spell (SpellNameFamily).
        public bool AnyVersion { get; set; }
        // The pip cost of the spell that sets it off: at least CostMin, at most CostMax (0 = no top).
        public int CostMin { get; set; }
        public int CostMax { get; set; }
        // Only while this many players are standing (0 = no limit).
        public int PlayersMin { get; set; }
        public int PlayersMax { get; set; }
        // Only from / up to this round of the battle (0 = no limit).
        public int FromRound { get; set; }
        public int ToRound { get; set; }
        // Only while someone else on its side is still standing (the other way round from NoAllies).
        public bool HasAllies { get; set; }
        // An added cheat turned off in the app (BossOverrides.CheatsOf).
        public bool Off { get; set; }

        /// <summary>A cheat made or changed in the app (BossOverrides.CheatsOf) or a custom's: its firing is logged.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool Custom { get; set; }

        /// <summary>Whether it stops the player's spell, so it goes off before that spell.</summary>
        internal bool Interrupts => (Actions ?? []).Any(a => a is not null && !a.Off && (a.Is("Interrupt") || a.Is("Reflect")));

        /// <summary>
        /// Everything it does, in order: the old fields as actions (a second form, health, phase, line, spells, defeat),
        /// then Actions. Random keeps one of the spells.
        /// </summary>
        internal List<CheatAction> Steps() {
            var steps = new List<CheatAction>();
            if (Spawn > 0) {
                steps.Add(new CheatAction { Do = "Form", Creature = Spawn, ReplaceBoss = Defeat });
            }

            if (SetHealth > 0) {
                steps.Add(new CheatAction { Do = "SetHealth", Amount = SetHealth });
            }

            if (SetPhase > 0) {
                steps.Add(new CheatAction { Do = "SetPhase", Phase = SetPhase });
            }

            if (!string.IsNullOrWhiteSpace(Line)) {
                steps.Add(new CheatAction { Do = "Say", Line = Line });
            }

            steps.AddRange(CastList(all: true).Select(c => new CheatAction {
                Do = "Cast", Spell = c.Spell, SpellID = c.SpellID, Silent = c.Silent, DispelImmune = c.DispelImmune, Target = c.Target ?? "",
            }));
            if (Defeat && Spawn == 0) {
                steps.Add(new CheatAction { Do = "Defeat" });
            }

            steps.AddRange((Actions ?? []).Where(a => a is not null && !a.Off).Select(a => Silent && a.IsCast ? a with { Silent = true } : a));
            var casts = steps.Where(s => s.IsCast).ToList();
            if (Random && casts.Count > 1) {
                var keep = casts[System.Random.Shared.Next(casts.Count)];
                steps.RemoveAll(s => s.IsCast && s != keep);
            }

            return steps;
        }

        internal bool Is(string when) => When.Equals(when, StringComparison.OrdinalIgnoreCase);

        /// <summary>The spells to cast this time: Casts, or Cast as a list of one; the cheat's Silent counts for each.</summary>
        internal List<CheatCast> CastList(bool all = false) {
            List<CheatCast> casts = [.. (Casts ?? []).Where(c => c is not null && (c.SpellID != 0 || !string.IsNullOrWhiteSpace(c.Spell)))
                .Select(c => Silent ? c with { Silent = true } : c)];
            if (casts.Count == 0 && !string.IsNullOrWhiteSpace(Cast)) {
                casts.Add(new CheatCast { Spell = Cast, Silent = Silent });
            }

            return !all && Random && casts.Count > 1 ? [casts[System.Random.Shared.Next(casts.Count)]] : casts;
        }
    }

    /// <summary>One spell of a cheat's Casts.</summary>
    internal sealed record CheatCast {
        public string Spell { get; set; } = "";
        public uint SpellID { get; set; }
        public bool Silent { get; set; }
        public bool DispelImmune { get; set; } = true;
        public string Target { get; set; } = "";
    }

    /// <summary>One thing a cheat does (its Actions); see the top of this file.</summary>
    internal sealed record CheatAction {
        public string Do { get; set; } = "";
        public string Spell { get; set; } = "";
        public uint SpellID { get; set; }
        public string Target { get; set; } = "";
        public bool Silent { get; set; }
        public bool DispelImmune { get; set; } = true;
        public int Amount { get; set; }
        public bool Percent { get; set; }
        public string Kind { get; set; } = "";
        public int Count { get; set; }
        public uint Creature { get; set; }
        public int Custom { get; set; }
        public bool ReplaceBoss { get; set; }
        public int Phase { get; set; }
        public string Line { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Off { get; set; }
        // For Boost, Immune and Stun: how many rounds (0 = the rest of the battle, or 1 for Stun).
        public int Rounds { get; set; }
        // For Boost, Immune and AddCharm: the school ("" or All = every school).
        public string School { get; set; } = "";
        // For PickOne: the chance of each of the next actions, such as "2, 1, 1" (1 each if left out).
        public string Weights { get; set; } = "";
        // Player cheats' FreeCast: how many times, when (First, Last, Before, After, Now), and with an enchantment.
        public int Times { get; set; }
        public string Timing { get; set; } = "";
        public bool Enchant { get; set; }
        // The enchantments picked for an enchanted FreeCast, put on in this order (none: the best one that fits).
        public List<string> Enchants { get; set; } = [];

        internal bool Is(string what) => Do.Equals(what, StringComparison.OrdinalIgnoreCase);

        internal bool IsCast => Is("Cast") || Is("Polymorph");
    }

    internal static readonly bool Enabled = ConfigurationManager.GetValue("BossCheats.Enabled", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<Dictionary<ulong, BossCheatFile>> s_bosses = new(Load);

    /// <summary>
    /// The cheats of a boss template, or none. For a custom's key (Customs.cs): its creature's file with the custom's
    /// own changes laid over it.
    /// </summary>
    internal static List<BossCheat> For(ulong templateId)
        => Enabled ? BossOverrides.CheatsOf(templateId,
            s_bosses.Value.TryGetValue(Customs.BaseOf(templateId), out var boss) ? boss.Cheats : s_none) : [];

    // The cheats of a boss with no file, the same list each time so BossOverrides.CheatsOf keeps its own laid over it.
    private static readonly List<BossCheat> s_none = [];

    /// <summary>How many shadow pips a boss template can hold and its chance a round to gain one, or (0, 0).</summary>
    internal static (int Most, int Chance) ShadowPipsOf(ulong templateId)
        => s_bosses.Value.TryGetValue(Customs.BaseOf(templateId), out var boss) && boss.ShadowPips > 0
            ? (boss.ShadowPips, Math.Clamp(boss.ShadowPipChance, 0, 100))
            : (0, 0);

    internal static string NameOf(ulong templateId) {
        if (Customs.IsCustom(templateId)) {
            var custom = Customs.Get(Customs.IdOf(templateId));
            if (custom is { Name.Length: > 0 }) {
                return $"{custom.Name} (custom {custom.CustomID})";
            }

            templateId = custom?.BaseTemplateID ?? 0;
        }

        return s_bosses.Value.TryGetValue(templateId, out var boss) && boss.Name.Length > 0 ? boss.Name : templateId.ToString();
    }

    private static Dictionary<ulong, BossCheatFile> Load() {
        var bosses = new Dictionary<ulong, BossCheatFile>();
        var dir = Path.Combine(SpiralDB.ExtraPath, "BossCheats");
        if (!Directory.Exists(dir)) {
            return bosses;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json")) {
            try {
                var boss = JsonSerializer.Deserialize<BossCheatFile>(File.ReadAllText(file), s_options);
                if (boss is null || boss.TemplateID == 0) {
                    continue;
                }

                boss.Cheats = [.. (boss.Cheats ?? []).Where(c => c is not null)];
                bosses[boss.TemplateID] = boss;
            }
            catch (Exception e) {
                Logger.Warning("Boss cheats {0} could not be read: {1}", Logger.Args(Path.GetFileName(file), e.Message));
            }
        }

        Logger.Information("Boss cheats loaded for {0} bosses.", Logger.Args(bosses.Count));

        return bosses;
    }

}
