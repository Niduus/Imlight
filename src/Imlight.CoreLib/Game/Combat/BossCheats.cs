/*
 * Boss cheats (Justin, 2026-10-21), read from spiraldb-extra/BossCheats/*.json, one file per boss template.
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

        internal bool Is(string when) => When.Equals(when, StringComparison.OrdinalIgnoreCase);

        /// <summary>The spells to cast this time: Casts, or Cast as a list of one; the cheat's Silent counts for each.</summary>
        internal List<CheatCast> CastList() {
            List<CheatCast> casts = [.. (Casts ?? []).Where(c => c is not null && (c.SpellID != 0 || !string.IsNullOrWhiteSpace(c.Spell)))
                .Select(c => Silent ? c with { Silent = true } : c)];
            if (casts.Count == 0 && !string.IsNullOrWhiteSpace(Cast)) {
                casts.Add(new CheatCast { Spell = Cast, Silent = Silent });
            }

            return Random && casts.Count > 1 ? [casts[System.Random.Shared.Next(casts.Count)]] : casts;
        }
    }

    /// <summary>One spell of a cheat's Casts.</summary>
    internal sealed record CheatCast {
        public string Spell { get; set; } = "";
        public uint SpellID { get; set; }
        public bool Silent { get; set; }
        public bool DispelImmune { get; set; } = true;
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
