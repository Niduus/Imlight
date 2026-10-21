/*
 * Boss cheats (Niduus, 2026-10-21), read from spiraldb-extra/BossCheats/*.json, one file per boss template.
 *
 * A cheat is a move a boss makes outside its own turn: a spell cast for free, a line shown over the boss, or both.
 * The game files hold the cheat spells (Spells/G14-CombatTrigger-Spells, AZ-CombatTrigger-Spells and others) and the
 * cheat lines, but not when a boss uses them; that lives on the real game's servers, so each boss's file says it.
 *
 * When:
 *   BattleStart   the boss's first round (a boss added with .joinme starts its own count).
 *   RoundStart    the start of a round: Round = the boss's Nth round, Every = every Nth round, neither = each round.
 *   HealthBelow   the boss's health falls under Percent of its most.
 *   PlayerCasts   a player's spell lands. School, Kind (the card's type: Damage, Heal, Ward, Charm, Aura, Global,
 *                 Steal, Manipulation, Shadow...) and Spells (card names) narrow it.
 *   PlayerPasses  a player passes.
 * Target: Self, Caster (the player who set it off), Enemy (a random player), AllEnemies, or empty to pick as the
 * spell aims. Line: words from the game's own line (as .ca finds it) or its locale key. Once: only the first time.
 * Chance: percent, 100 if left out.
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

        internal bool Is(string when) => When.Equals(when, StringComparison.OrdinalIgnoreCase);
    }

    internal static readonly bool Enabled = ConfigurationManager.GetValue("BossCheats.Enabled", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<Dictionary<ulong, BossCheatFile>> s_bosses = new(Load);

    /// <summary>The cheats of a boss template, or none.</summary>
    internal static List<BossCheat> For(ulong templateId)
        => Enabled && s_bosses.Value.TryGetValue(templateId, out var boss) ? boss.Cheats : [];

    internal static string NameOf(ulong templateId)
        => s_bosses.Value.TryGetValue(templateId, out var boss) && boss.Name.Length > 0 ? boss.Name : templateId.ToString();

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
