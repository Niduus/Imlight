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
 *
 * ========================================================================
 * PET PROGRESS
 * ========================================================================
 *
 * PURPOSE:
 * Pet stats, talents, experience and levels, read from the pet's template in Root.wad.
 *
 * NOTE:
 * - Levels: 1 Baby, 2 Teen, 3 Adult, 4 Ancient, 5 Epic, 6 Mega, 7 Ultra. A level's m_requiredXP in the
 *   template is the total experience the pet needs to reach the next level.
 * - Every level from Teen to Mega shows one talent and one derby talent from the pet's pool.
 * - A talent's value comes from the pet's stats: (first stat + second stat + third stat * modifier) times the
 *   effect's number, as in PetTalentEffects.xml. For example Pain-Giver is (Strength + Will + Power / 2) / 100 percent.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.IO;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

internal static class PetProgress {

    internal const byte BABY_LEVEL = 1;
    internal const byte MEGA_LEVEL = 6;
    internal const byte MAX_LEVEL = 7;
    // [Pets] in Imlight.ini.
    private static float? s_snackXpPerRank;
    private static float? s_favoriteSnackBonus;

    // The snack card shows 15 XP for a rank 10 snack, so 1.5 a rank. Renamed from SnackXpPerRank (was 5) so old ini values do not stick.
    private static float SnackXpPerRank => s_snackXpPerRank ??= Math.Max(0.1f, ReadSetting("Pets.SnackXpEachRank", 1.5f));

    private static float FavoriteSnackBonus => s_favoriteSnackBonus ??= ReadSetting("Pets.FavoriteSnackBonus", 1.5f);

    private static T ReadSetting<T>(string key, T defaultValue) {
        try {
            return ConfigurationManager.GetValue(key, defaultValue);
        }
        catch (Exception) {
            return defaultValue;
        }
    }
    private const int DEFAULT_START_STAT = 10;

    private static readonly string[] s_levelNames = ["Egg", "Baby", "Teen", "Adult", "Ancient", "Epic", "Mega", "Ultra"];
    private static readonly string[] s_statNames = ["Strength", "Intellect", "Agility", "Will", "Power"];

    /// <summary>What a feeding or an XP grant did to a pet.</summary>
    internal sealed class PetGrowth {
        internal uint XpGained;
        internal int LevelsGained;
        internal readonly List<PetTalentBaseTemplate> NewTalents = [];
        internal readonly List<PetTalentBaseTemplate> NewDerbyTalents = [];
        internal readonly List<(string Stat, int Change)> StatChanges = [];
        internal readonly List<(byte Level, PetTalentBaseTemplate Talent, PetTalentBaseTemplate Derby)> LevelUps = [];
    }

    internal static string LevelName(int level) => level >= 0 && level < s_levelNames.Length ? s_levelNames[level] : level.ToString();

    internal static byte? ParseLevel(string text) {
        if (byte.TryParse(text, out var number)) {
            return number is >= BABY_LEVEL and <= MAX_LEVEL ? number : null;
        }

        var index = Array.FindIndex(s_levelNames, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));

        return index >= BABY_LEVEL ? (byte) index : null;
    }

    internal static ClientPetItemBehavior GetBehavior(WizClientObjectItem pet)
        => pet?.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().FirstOrDefault();

    internal static PetItemBehaviorTemplate GetTemplate(WizClientObjectItem pet)
        => pet is null ? null : PetFactory.GetPetItemBehaviorTemplate((uint) pet.m_templateID);

    /// <summary>
    /// Fills in the stats, talent pool and experience goal of a pet that does not have them yet.
    /// </summary>
    /// <returns>True if anything changed and the pet should be saved.</returns>
    internal static bool EnsureInitialized(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null || behavior.m_level < BABY_LEVEL) {
            return false;
        }

        var changed = false;
        if (behavior.m_allTalents is null || behavior.m_allTalents.Count == 0) {
            behavior.m_allTalents = TalentPool(template.m_guaranteedTalents, template.m_talents);
            changed = true;
        }

        if (behavior.m_allDerbyTalents is null || behavior.m_allDerbyTalents.Count == 0) {
            behavior.m_allDerbyTalents = TalentPool(template.m_guaranteedDerbyTalents, template.m_derbyTalents);
            changed = true;
        }

        if (behavior.m_expressedTalents is null) {
            behavior.m_expressedTalents = [];
            changed = true;
        }

        if (behavior.m_expressedDerbyTalents is null) {
            behavior.m_expressedDerbyTalents = [];
            changed = true;
        }

        if (behavior.m_currentStats is null || behavior.m_currentStats.Count == 0) {
            var start = template.m_startStats?.Count > 0 ? template.m_startStats : template.m_conversionStats;
            behavior.m_currentStats = [.. StatNames(template).Select(name => NewStat(name,
                start?.FirstOrDefault(s => SameStat(s?.m_name, name))?.m_value ?? DEFAULT_START_STAT))];
            changed = true;
        }

        var maxBefore = StatSummary(behavior.m_maxStats);
        RecalculateMaxStats(behavior, template);
        changed |= maxBefore != StatSummary(behavior.m_maxStats);

        var required = RequiredXp(template, behavior.m_level);
        if (behavior.m_requiredXP != required) {
            behavior.m_requiredXP = required;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Gives a pet experience and levels it up as far as the experience reaches.
    /// </summary>
    internal static PetGrowth AddXp(WizClientObjectItem pet, uint xp, PetGrowth growth = null) {
        growth ??= new PetGrowth();
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null) {
            return growth;
        }

        EnsureInitialized(pet);
        if (behavior.m_level >= MAX_LEVEL) {
            return growth;
        }

        behavior.m_XP += xp;
        growth.XpGained += xp;

        // m_XP counts from the start of the current level, like the pet window shows it (4/125, then 0/250).
        while (behavior.m_level < MAX_LEVEL) {
            var required = RequiredXp(template, behavior.m_level);
            if (required == 0 || behavior.m_XP < required) {
                break;
            }

            behavior.m_XP -= required;
            LevelUp(behavior, template, growth);

            // Like the real game: the pet starts its new level at 0 and the rest is lost.
            if (!PetRules.XpCarriesOver) {
                growth.XpGained -= Math.Min(growth.XpGained, behavior.m_XP);
                behavior.m_XP = 0;

                break;
            }
        }

        if (behavior.m_level >= MAX_LEVEL) {
            behavior.m_requiredXP = 0;
        }

        return growth;
    }

    /// <summary>
    /// Sets a pet to a level, showing the talents a pet gets on the way up.
    /// </summary>
    internal static PetGrowth SetLevel(WizClientObjectItem pet, byte level) {
        var growth = new PetGrowth();
        var behavior = GetBehavior(pet);
        var template = GetTemplate(pet);
        if (behavior is null || template is null) {
            return growth;
        }

        EnsureInitialized(pet);
        while (behavior.m_level < level && behavior.m_level < MAX_LEVEL) {
            // Each level starts at 0 experience.
            behavior.m_XP = 0;
            LevelUp(behavior, template, growth);
        }

        if (behavior.m_level > level) {
            behavior.m_level = level;
            behavior.m_XP = 0;
            behavior.m_requiredXP = RequiredXp(template, level);
        }

        return growth;
    }

    private static void LevelUp(ClientPetItemBehavior behavior, PetItemBehaviorTemplate template, PetGrowth growth) {
        behavior.m_level++;
        growth.LevelsGained++;

        PetTalentBaseTemplate talent = null, derby = null;
        if (behavior.m_level <= MEGA_LEVEL) {
            talent = Manifest(behavior.m_allTalents, behavior.m_expressedTalents);
            if (talent is not null) {
                growth.NewTalents.Add(talent);
                RecalculateMaxStats(behavior, template);
            }

            derby = Manifest(behavior.m_allDerbyTalents, behavior.m_expressedDerbyTalents);
            if (derby is not null) {
                growth.NewDerbyTalents.Add(derby);
            }
        }

        growth.LevelUps.Add((behavior.m_level, talent, derby));

        behavior.m_requiredXP = RequiredXp(template, behavior.m_level);
    }

    /// <summary>
    /// Picks a talent from the pool that the pet does not show yet. Higher ranked talents are rarer.
    /// </summary>
    private static PetTalentBaseTemplate Manifest(List<uint> pool, List<uint> expressed) {
        var candidates = (pool ?? [])
            .Where(id => !expressed.Contains(id))
            .Select(PetData.GetTalent)
            .Where(t => t is not null)
            .ToList();
        if (candidates.Count == 0) {
            return null;
        }

        var weights = candidates.Select(t => Math.Max(1, 6 - t.m_rank)).ToList();
        var roll = Random.Shared.Next(weights.Sum());
        var index = 0;
        while (roll >= weights[index]) {
            roll -= weights[index];
            index++;
        }

        var picked = candidates[index];
        expressed.Add(PetData.TalentId(picked.m_talentName));

        return picked;
    }

    /// <summary>
    /// Makes a pet show a talent, from its pool or not. Derby talents go to the derby list.
    /// </summary>
    internal static bool AddTalent(WizClientObjectItem pet, PetTalentBaseTemplate talent) {
        var behavior = GetBehavior(pet);
        if (behavior is null || talent is null) {
            return false;
        }

        EnsureInitialized(pet);
        var id = PetData.TalentId(talent.m_talentName);
        var list = talent is PetDerbyTalentTemplate ? behavior.m_expressedDerbyTalents : behavior.m_expressedTalents;
        if (list.Contains(id)) {
            return false;
        }

        list.Add(id);
        RecalculateMaxStats(behavior, GetTemplate(pet));

        return true;
    }

    internal static bool RemoveTalent(WizClientObjectItem pet, PetTalentBaseTemplate talent) {
        var behavior = GetBehavior(pet);
        if (behavior is null || talent is null) {
            return false;
        }

        var id = PetData.TalentId(talent.m_talentName);
        var removed = (behavior.m_expressedTalents?.Remove(id) ?? false) | (behavior.m_expressedDerbyTalents?.Remove(id) ?? false);
        RecalculateMaxStats(behavior, GetTemplate(pet));

        return removed;
    }

    /// <summary>
    /// Raises the pet's stats by the changes, up to its maximum stats.
    /// </summary>
    internal static void ApplyStatChanges(WizClientObjectItem pet, IEnumerable<PetStatModification> changes, PetGrowth growth) {
        var behavior = GetBehavior(pet);
        if (behavior?.m_currentStats is null || changes is null) {
            return;
        }

        foreach (var change in changes) {
            var stat = behavior.m_currentStats.FirstOrDefault(s => SameStat(s.m_name, change?.m_name));
            if (stat is null || change.m_change == 0) {
                continue;
            }

            var max = GetStat(behavior.m_maxStats, stat.m_name);
            var newValue = Math.Clamp(stat.m_value + change.m_change, 0, max);
            if (newValue != stat.m_value) {
                growth?.StatChanges.Add((stat.m_name, newValue - stat.m_value));
                stat.m_value = newValue;
            }
        }
    }

    /// <summary>
    /// Sets every stat to its maximum.
    /// </summary>
    internal static void MaxStats(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        if (behavior?.m_currentStats is null) {
            return;
        }

        foreach (var stat in behavior.m_currentStats) {
            stat.m_value = GetStat(behavior.m_maxStats, stat.m_name);
        }
    }

    /// <summary>
    /// The experience a snack gives: by default one and a half per snack rank, as the snack card shows, half again for a snack the pet likes.
    /// </summary>
    internal static uint SnackXp(WizClientObjectItem pet, PetSnackItemTemplate snack) {
        var xp = SnackXpPerRank * Math.Max(1, snack?.m_rank ?? 1);
        if (IsFavoriteSnack(pet, snack)) {
            xp *= FavoriteSnackBonus;
        }

        return (uint) Math.Max(1, Math.Round(xp, MidpointRounding.AwayFromZero));
    }

    internal static bool IsFavoriteSnack(WizClientObjectItem pet, PetSnackItemTemplate snack) {
        var favorites = GetTemplate(pet)?.m_favoriteSnackCategories;
        if (favorites is null || snack?.m_adjectiveList is null) {
            return false;
        }

        return favorites.Any(f => snack.m_adjectiveList.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The wizard stats a pet's talents give, worked out from the pet's current stats.
    /// </summary>
    /// <param name="pet">The pet.</param>
    /// <param name="extraTalents">Talents from a jewel in the pet's socket.</param>
    /// <returns>Pairs of a stat name in the form CharacterEffectHelper reads (Canonical&lt;School&gt;&lt;Kind&gt;) and the effect.</returns>
    internal static List<(string Name, WizStatisticEffect Effect)> ComputeEffects(WizClientObjectItem pet, IEnumerable<string> extraTalents = null) {
        var result = new List<(string, WizStatisticEffect)>();
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return result;
        }

        var talents = (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat((extraTalents ?? []).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>();
        foreach (var talent in talents) {
            foreach (var info in talent.m_effectList?.OfType<StatisticEffectInfo>() ?? []) {
                var template = PetData.GetTalentEffect(info.m_effectName);
                if (template is null) {
                    continue;
                }

                var basis = GetStat(behavior.m_currentStats, template.m_primaryStat1)
                    + GetStat(behavior.m_currentStats, template.m_primaryStat2)
                    + GetStat(behavior.m_currentStats, template.m_secondaryStat) * template.m_secondaryModifier;
                result.Add((StatName(template), ScaleEffect(template, basis)));
            }
        }

        return result;
    }

    /// <summary>
    /// The cards a pet's talents give its owner in battle, such as Hexster's Hex.
    /// </summary>
    internal static List<ProvideSpellEffectInfo> CardEffects(WizClientObjectItem pet, IEnumerable<string> extraTalents = null) {
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return [];
        }

        return [.. (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat((extraTalents ?? []).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>()
            .SelectMany(talent => talent.m_effectList?.OfType<ProvideSpellEffectInfo>() ?? [])
            .Concat(PowerCardEffects(pet))];
    }

    /// <summary>
    /// The pet's own card for its level, such as the Obsidian Mimic's Black Mantle (the card the pet window shows).
    /// The pet template lists it per level (m_Levels: m_powerCardName, 2 and 3, and m_powerCardCount).
    /// </summary>
    private static IEnumerable<ProvideSpellEffectInfo> PowerCardEffects(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        var level = GetTemplate(pet)?.m_Levels?.FirstOrDefault(info => info is not null && info.m_level == behavior?.m_level);
        if (level is null || level.m_powerCardCount <= 0) {
            yield break;
        }

        var names = new[] { (string) level.m_powerCardName, (string) level.m_powerCardName2, (string) level.m_powerCardName3 }
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        for (var i = 0; i < names.Count; i++) {
            // Copies beyond one per named card go to the first card.
            var copies = i == 0 ? Math.Max(1, (int) level.m_powerCardCount - (names.Count - 1)) : 1;
            yield return new ProvideSpellEffectInfo {
                m_effectName = "PetPowerCard",
                m_spellName = names[i],
                m_numSpells = copies,
            };
        }
    }

    private static WizStatisticEffect ScaleEffect(PetBoostPlayerStatEffectTemplate t, float basis) => new() {
        m_effectNameID = Imcodec.Cryptography.StringHash.Compute(t.m_effectName),
        m_damageBonusPercent = t.m_damageBonusPercent * basis,
        m_damageBonusFlat = t.m_damageBonusFlat * basis,
        m_accuracyBonusPercent = t.m_accuracyBonusPercent * basis,
        m_armorPiercingBonusPercent = t.m_armorPiercingBonusPercent * basis,
        m_damageReducePercent = t.m_damageReducePercent * basis,
        m_damageReduceFlat = t.m_damageReduceFlat * basis,
        m_healBonusPercent = t.m_healBonusPercent * basis,
        m_healIncBonusPercent = t.m_healIncBonusPercent * basis,
        m_hitPointBonus = MathF.Round(t.m_hitPointBonus * basis),
        m_manaBonus = MathF.Round(t.m_manaBonus * basis),
        m_powerPipBonusPercent = t.m_powerPipBonusPercent * basis,
        m_criticalHitRating = t.m_criticalHitRating * basis,
        m_blockRating = t.m_blockRating * basis,
        m_stunResistancePercent = t.m_stunResistancePercent * basis,
        m_pipConversionRating = t.m_pipConversionRating * basis,
        m_fishingLuckBonusPercent = t.m_fishingLuckBonusPercent * basis,
    };

    private static string StatName(PetBoostPlayerStatEffectTemplate t) {
        string category = t.m_effectCategory;
        if (string.IsNullOrEmpty(category)) {
            // One talent in the game files has no category; its effect name still says what it is.
            category = ((string) t.m_effectName ?? "").Replace("PetTalent", "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        }

        // CharacterEffectHelper reads the school after "Canonical" and treats "All" as every school.
        return category is "CriticalHit" or "Block" or "PipConversion" ? $"CanonicalAll{category}" : $"Canonical{category}";
    }

    /// <summary>
    /// A few lines about a pet for the chat window.
    /// </summary>
    internal static string Describe(WizClientObjectItem pet) {
        var behavior = GetBehavior(pet);
        if (behavior is null) {
            return "That item is not a pet.";
        }

        var text = new StringBuilder();
        text.Append($"{pet.m_debugName}: {LevelName(behavior.m_level)} (level {behavior.m_level}), XP {behavior.m_XP}");
        text.Append(behavior.m_requiredXP > 0 ? $"/{behavior.m_requiredXP}. " : ". ");
        text.Append("Stats: ");
        text.Append(string.Join(", ", (behavior.m_currentStats ?? []).Select(s => $"{s.m_name} {s.m_value}/{GetStat(behavior.m_maxStats, s.m_name)}")));
        text.Append(". Talents: ");
        text.Append(TalentList(behavior.m_expressedTalents));
        text.Append(". Derby: ");
        text.Append(TalentList(behavior.m_expressedDerbyTalents));
        text.Append(". Pool: ");
        text.Append(TalentList(behavior.m_allTalents));
        text.Append('.');

        return text.ToString();
    }

    internal static string TalentLabel(PetTalentBaseTemplate talent) {
        var shown = Locale.GetEnglishName(talent.m_displayName);

        return string.IsNullOrEmpty(shown) ? talent.m_talentName : $"{shown} ({talent.m_talentName})";
    }

    private static string TalentList(List<uint> ids) {
        if (ids is null || ids.Count == 0) {
            return "none";
        }

        return string.Join(", ", ids.Select(id => PetData.GetTalent(id) is { } t ? TalentLabel(t) : id.ToString()));
    }

    internal static uint RequiredXp(PetItemBehaviorTemplate template, byte level)
        => template?.m_Levels?.FirstOrDefault(l => l?.m_level == level)?.m_requiredXP ?? 0;

    private static void RecalculateMaxStats(ClientPetItemBehavior behavior, PetItemBehaviorTemplate template) {
        if (template is null) {
            return;
        }

        var bonus = (behavior.m_expressedTalents ?? [])
            .Select(PetData.GetTalent)
            .OfType<PetTalentTemplate>()
            .SelectMany(t => t.m_maxStatList ?? [])
            .Where(s => s is not null)
            .ToList();
        behavior.m_maxStats = [.. StatNames(template).Select(name => NewStat(name,
            GetStat(template.m_maxStats, name) + bonus.Where(b => SameStat(b.m_name, name)).Sum(b => b.m_value)))];

        foreach (var stat in behavior.m_currentStats ?? []) {
            stat.m_value = Math.Min(stat.m_value, GetStat(behavior.m_maxStats, stat.m_name));
        }
    }

    private static List<uint> TalentPool(List<ByteString> guaranteed, List<ByteString> talents)
        => [.. (guaranteed ?? []).Concat(talents ?? [])
            .Select(t => (string) t)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(PetData.TalentId)
            .Distinct()];

    private static IEnumerable<string> StatNames(PetItemBehaviorTemplate template) {
        var names = template.m_maxStats?.Where(s => s is not null).Select(s => (string) s.m_name).ToList();

        return names is { Count: > 0 } ? names : s_statNames;
    }

    private static PetStat NewStat(string name, int value) => new() {
        m_name = name,
        m_statID = Imcodec.Cryptography.StringHash.Compute(name),
        m_value = value,
    };

    internal static int GetStat(List<PetStat> stats, string name)
        => stats?.FirstOrDefault(s => SameStat(s?.m_name, name))?.m_value ?? 0;

    private static bool SameStat(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string StatSummary(List<PetStat> stats)
        => string.Join(",", (stats ?? []).Select(s => $"{s?.m_name}={s?.m_value}"));

}
