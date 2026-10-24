/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.MonsterMagic;

/// <summary>
/// Monstrology ("Monster Magic" in the game's code). An Extract spell played onto a damage spell takes Animus from the
/// monsters of its kind that the spell hits (CombatResolver.Monstrology.cs); a won battle keeps them in the wizard's
/// tome (Wizard.MonsterTome) and gives Monstrology XP. The tome window (Monstrology tome, the T key) turns Animus and
/// gold into a house guest or a Summon or Expel Monster treasure card (MonstrologyService).
/// <para>
/// Read from the game (WizardGraphicalClient.exe, 2026-10-24): MonsterMagicBehaviorBase::OnPostLoad reads m_monsterData
/// as uint32 0x1DEC0DED, uint32 version 1, uint16 count, then count times (uint32 monster template id, uint16 Animus).
/// The tome's buttons send MSG_MONSTERMAGICREQUESTCREATE with RequestType 1 (Summon card), 2 (house guest) or
/// 3 (Expel card); the answer's RequestType is the result: 0 "Monstrology Creation Failed!", 1 to 3 "created!".
/// The window itself checks Animus, gold and rank (NPCBehaviorTemplate m_nLevel) against the Monstrology level, and
/// takes the costs from the monster's MobMonsterMagicBehaviorTemplate.
/// </para>
/// </summary>
internal static class Monstrology {

    internal const int SUMMON_CARD = 1;
    internal const int HOUSE_GUEST = 2;
    internal const int EXPEL_CARD = 3;
    internal const int FAILED = 0;

    // The Monster Magic member benefit (MemberBenefits): double Animus.
    private const uint MONSTER_MAGIC_BENEFIT = 16;

    private const uint TOME_IDENTIFIER = 0x1DEC0DED;
    private const uint TOME_VERSION = 1;
    private const string EXTRACT_ADJECTIVE = "Collect_";

    internal static bool Enabled => ConfigurationManager.GetValue("Monstrology.Enabled", true);
    private static int StartLevel => Math.Clamp(ConfigurationManager.GetValue("Monstrology.StartLevel", 1), 1, MaxLevel);
    private static int MinWizardLevel => ConfigurationManager.GetValue("Monstrology.MinWizardLevel", 12);
    internal static int HighRankChance => Math.Clamp(ConfigurationManager.GetValue("Monstrology.HighRankChance", 10), 0, 100);
    internal static int BonusChance => Math.Clamp(ConfigurationManager.GetValue("Monstrology.BonusChance", 20), 0, 100);
    private static int XpPerAnimus => Math.Max(0, ConfigurationManager.GetValue("Monstrology.XpPerAnimus", 1));
    private static int MaxAnimus => Math.Clamp(ConfigurationManager.GetValue("Monstrology.MaxAnimus", 999), 1, ushort.MaxValue);

    /// <summary>
    /// What Monstrology needs to know about a monster template.
    /// </summary>
    internal sealed record Monster(uint TemplateId, uint TomeId, string Name, int Rank, MobMonsterMagicBehaviorTemplate Magic,
                                   HashSet<string> Kinds);

    private static readonly ConcurrentDictionary<uint, Monster> s_monsters = new();

    private static readonly Lazy<MonsterMagicXPConfig> s_xpConfig = new(() => {
        try {
            var config = RootArchiveLoader.GetFile<MonsterMagicXPConfig>("MonsterMagicXPConfig.xml");
            Logger.Information("Monstrology: {0} levels loaded.", Logger.Args(config?.m_levelInfo?.Count ?? 0));

            return config;
        }
        catch (Exception e) {
            Logger.Error("Monstrology: MonsterMagicXPConfig.xml could not be read: {0}", Logger.Args(e));

            return null;
        }
    });

    private static List<MonsterMagicLevelInfo> Levels => s_xpConfig.Value?.m_levelInfo ?? [];

    internal static int MaxLevel => s_xpConfig.Value?.m_maxLevel is byte max and > 0 ? max : 35;

    /// <summary>
    /// The monster a template is, or null when it has no Monstrology behavior (players, NPCs, objects).
    /// </summary>
    internal static Monster MonsterOf(uint templateId) {
        if (templateId == 0) {
            return null;
        }

        return s_monsters.GetOrAdd(templateId, id => {
            try {
                if (CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate template
                    || template.m_behaviors?.OfType<MobMonsterMagicBehaviorTemplate>().FirstOrDefault() is not { } magic) {
                    return null;
                }

                var npc = template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
                string displayKey = template.m_displayName;
                string objectName = template.m_objectName;
                var name = string.IsNullOrEmpty(displayKey) ? null : Locale.GetEnglishName(displayKey);
                var kinds = (template.m_adjectiveList ?? []).Select(a => (string) a)
                    .Where(a => a?.StartsWith("MONST_", StringComparison.OrdinalIgnoreCase) == true)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return new Monster(id, magic.m_collectedAsTemplateID != 0 ? magic.m_collectedAsTemplateID : id,
                    string.IsNullOrEmpty(name) ? objectName ?? id.ToString() : name, npc?.m_nLevel ?? 1, magic, kinds);
            }
            catch (Exception e) {
                Logger.Error("Monstrology: template {0} could not be read: {1}", Logger.Args(id, e));

                return null;
            }
        });
    }

    /// <summary>
    /// The monster kinds an Extract spell takes Animus from (its "Collect_MONST_..." adjectives, as "MONST_..."), or an
    /// empty set when the spell is not an Extract spell.
    /// </summary>
    internal static HashSet<string> ExtractKinds(SpellTemplate spell) {
        if (spell is null || !IsExtract(spell)) {
            return [];
        }

        return (spell.m_adjectives ?? []).Select(a => (string) a)
            .Where(a => a?.StartsWith(EXTRACT_ADJECTIVE + "MONST_", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a[EXTRACT_ADJECTIVE.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the spell is an Extract (Animus) spell.
    /// </summary>
    internal static bool IsExtract(SpellTemplate spell)
        => CombatEnchantments.Leaves(spell?.m_effects).Any(e => e.m_effectType == kSpellEffects.kCollectEssence);

    /// <summary>
    /// The Animus for one extraction: doubled while the Monster Magic member benefit is on, as the game does.
    /// </summary>
    internal static int WithBenefit(Wizard wizard, int animus)
        => wizard?.GameStats is { } stats && (MemberBenefits(stats.m_subscriberBenefitFlags) & MONSTER_MAGIC_BENEFIT) != 0
            ? animus * 2 : animus;

    private static uint MemberBenefits(uint stored) => Imlight.CoreLib.Game.Tools.MemberBenefits.Flags(stored);

    /// <summary>
    /// The wizard's Monstrology level.
    /// </summary>
    internal static int LevelOf(Wizard wizard) => wizard?.GameStats?.m_monsterMagicLevel ?? 0;

    /// <summary>
    /// The XP a level starts at.
    /// </summary>
    internal static int XpForLevel(int level)
        => Levels.FirstOrDefault(l => l.m_level == level)?.m_xpToLevel ?? 0;

    /// <summary>
    /// The level a wizard with this much XP has.
    /// </summary>
    internal static int LevelForXp(int xp) {
        var level = 0;
        foreach (var info in Levels.OrderBy(l => l.m_level)) {
            if (xp >= info.m_xpToLevel) {
                level = info.m_level;
            }
        }

        return Math.Min(level, MaxLevel);
    }

    /// <summary>
    /// Opens Monstrology for a wizard of the level it opens at (StartLevel, with that level's XP). Returns true when the
    /// wizard was changed; nothing is saved here.
    /// </summary>
    internal static bool Open(Wizard wizard) {
        if (!Enabled || wizard?.GameStats is not { } stats || stats.m_monsterMagicLevel > 0
            || (wizard.MagicSchoolBehavior?.Level ?? 0) < MinWizardLevel) {
            return false;
        }

        stats.m_monsterMagicLevel = (byte) StartLevel;
        stats.m_monsterMagicXP = Math.Max(stats.m_monsterMagicXP, XpForLevel(StartLevel));
        wizard.MonsterTome ??= [];

        return true;
    }

    /// <summary>
    /// Sets the wizard's Monstrology level (and the XP that level starts at) and saves it.
    /// </summary>
    internal static int SetLevel(Wizard wizard, int level) {
        level = Math.Clamp(level, 1, MaxLevel);
        wizard.GameStats.m_monsterMagicLevel = (byte) level;
        wizard.GameStats.m_monsterMagicXP = XpForLevel(level);
        wizard.MonsterTome ??= [];
        WizardCollection.UpdateCharacterMonsterTome(wizard);

        return level;
    }

    /// <summary>
    /// Keeps Animus in the wizard's tome and gives the Monstrology XP for it, then saves.
    /// </summary>
    /// <returns>The level before, so the caller can tell the game about a new level.</returns>
    internal static int Keep(Wizard wizard, IReadOnlyDictionary<uint, int> animus) {
        var before = LevelOf(wizard);
        if (wizard?.GameStats is not { } stats || animus is null || animus.Count == 0) {
            return before;
        }

        Open(wizard);
        wizard.MonsterTome ??= [];
        var kept = 0;
        foreach (var (tomeId, count) in animus) {
            if (count <= 0) {
                continue;
            }

            var had = wizard.MonsterTome.GetValueOrDefault(tomeId);
            var now = Math.Min(MaxAnimus, had + count);
            // A monster already full still counts for XP: the extraction itself worked.
            wizard.MonsterTome[tomeId] = Math.Max(had, now);
            kept += count;
        }

        stats.m_monsterMagicXP += kept * XpPerAnimus;
        var level = Math.Max(stats.m_monsterMagicLevel, LevelForXp(stats.m_monsterMagicXP));
        stats.m_monsterMagicLevel = (byte) Math.Clamp(level, 0, MaxLevel);
        WizardCollection.UpdateCharacterMonsterTome(wizard);

        return before;
    }

    /// <summary>
    /// Takes Animus from the tome and saves; false (and nothing taken) when there is not enough.
    /// </summary>
    internal static bool Spend(Wizard wizard, uint tomeId, int animus) {
        wizard.MonsterTome ??= [];
        var had = wizard.MonsterTome.GetValueOrDefault(tomeId);
        if (animus < 0 || had < animus) {
            return false;
        }

        wizard.MonsterTome[tomeId] = had - animus;
        WizardCollection.UpdateCharacterMonsterTome(wizard);

        return true;
    }

    /// <summary>
    /// The tome in the game's MonsterMagicBehavior m_monsterData format. A monster whose Animus was all spent stays, so
    /// its page stays open, as in the game.
    /// </summary>
    internal static ByteString Write(IReadOnlyDictionary<uint, int> tome) {
        var entries = (tome ?? new Dictionary<uint, int>()).Take(ushort.MaxValue).ToList();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(TOME_IDENTIFIER);
        writer.Write(TOME_VERSION);
        writer.Write((ushort) entries.Count);
        foreach (var (templateId, animus) in entries) {
            writer.Write(templateId);
            writer.Write((ushort) Math.Clamp(animus, 0, ushort.MaxValue));
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// What one of the tome's buttons costs for a monster: Animus and gold, from its MobMonsterMagicBehaviorTemplate.
    /// </summary>
    internal static (int Animus, int Gold) Cost(Monster monster, int requestType) => requestType switch {
        SUMMON_CARD => (monster.Magic.m_essencesPerSummonTC, monster.Magic.m_goldPerSummonTC),
        HOUSE_GUEST => (monster.Magic.m_essencesPerHouseGuest, monster.Magic.m_goldPerHouseGuest),
        EXPEL_CARD => (monster.Magic.m_essencesPerKillTC, monster.Magic.m_goldPerKillTC),
        _ => (0, 0),
    };

    /// <summary>
    /// The treasure card a Summon (1) or Expel (3) request makes: the game names them Summon&lt;id&gt; and Kill&lt;id&gt;
    /// (Spells/MonsterMagicTC). The tome's own monster is tried first, then its alternate and the template itself.
    /// </summary>
    internal static string CardName(Monster monster, int requestType) {
        var prefix = requestType == SUMMON_CARD ? "Summon" : "Kill";

        return new[] { monster.TomeId, monster.Magic.m_alternateMobTemplateID, monster.TemplateId }
            .Where(id => id != 0)
            .Select(id => prefix + id)
            .FirstOrDefault(SpellFactory.Exists);
    }

    /// <summary>
    /// A name for the monster a tome entry is, for messages.
    /// </summary>
    internal static string NameOf(uint tomeId) => MonsterOf(tomeId)?.Name ?? tomeId.ToString();

}
