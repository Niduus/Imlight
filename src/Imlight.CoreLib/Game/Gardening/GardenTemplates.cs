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
 * GARDENING
 * ========================================================================
 *
 * PURPOSE:
 * What the game files say about seeds, plant stages, garden spells and pests, read once per template.
 *
 * USAGE EXAMPLE:
 * if (GardenTemplates.Plant(templateId) is { } stage && stage.Stage == PlantStage.PS_Mature) { ... }
 * var seconds = GardenTemplates.ParseRate("1d10h30m");
 *
 * NOTE:
 * - Every stage of a plant is its own item template with a SeedBehavior: the seed item (a PS_Mature stage whose next
 *   stage comes 1 minute after planting, which is the seedling), then seedling, young (_Adolescent), mature and elder
 *   (PS_Angel). Wilting (_Sick) and dead are shared by every stage; their young template is where the plant goes back
 *   to when it recovers, as the client does (0x140f286e0, SeedBehaviorTemplate +0x1f4).
 * - A rate is "1d10h30m", "36h0s", "10s" and the like; every unit is optional and "0m" means never.
 * - Harvest rewards are not in the client: every plant's ResLoot names no table (Game/Gardening/GardenHarvests).
 *
 * TODO:
 * - Likes and dislikes (m_likesDislikesList) are not used: every plant grows at its plain rate.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Gardening;

/// <summary>A need of a plant, in the order of its flag bits.</summary>
internal enum GardenNeed { Water, Sun, Pollination, Magic, Music }

/// <summary>When a need shows up again after it is met, and how long the plant lasts before it wilts.</summary>
internal readonly record struct NeedRate(long Min, long Max, long Wilt) {
    internal bool Used => Min > 0 || Max > 0;
}

/// <summary>A pest that can come to a plant, with its chance per hour in percent.</summary>
internal readonly record struct PestChance(uint TemplateId, float PercentPerHour);

/// <summary>One stage of a plant, as its SeedBehaviorTemplate gives it.</summary>
internal sealed record PlantInfo(
    uint TemplateId,
    string Name,
    PlantStage Stage,
    uint NextStageTemplateId,
    long NextStageSeconds,
    uint WiltTemplateId,
    uint DeadTemplateId,
    uint YoungTemplateId,
    uint MatureTemplateId,
    NeedRate[] Needs,
    long HarvestMin,
    long HarvestMax,
    IReadOnlyList<PestChance> Pests,
    long PestUpdateSeconds,
    int HarvestXp,
    int RewardsRating,
    uint MaximumHarvests,
    SeedSize Size,
    int GardeningLevel,
    bool IsSeedItem);

/// <summary>A garden spell.</summary>
internal sealed record GardenSpellInfo(
    uint TemplateId,
    string Name,
    GardenSpellType Type,
    UtilitySpellType Utility,
    bool[] Provides,
    uint SoilTemplateId,
    uint ProtectionTemplateId,
    int BugZapLevel,
    int EnergyCost,
    int Radius,
    int GardeningLevel,
    bool IsTreasureCard);

/// <summary>A pest, or a protection a spell puts on plants.</summary>
internal sealed record PestInfo(uint TemplateId, string Name, int Rank, long WiltMin, long WiltMax, bool Good, bool[] Provides);

/// <summary>
/// Reads the gardening parts of the game's templates.
/// </summary>
internal static class GardenTemplates {

    /// <summary>The template of the object that carries a house's garden in its blob (GardeningProxy.xml).</summary>
    internal const uint ProxyTemplateId = 329071;

    private static readonly Regex s_rate = new(@"^\s*(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?\s*$", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<uint, PlantInfo> s_plants = new();
    private static readonly ConcurrentDictionary<uint, GardenSpellInfo> s_spells = new();
    private static readonly ConcurrentDictionary<uint, PestInfo> s_pests = new();
    private static readonly Lazy<IReadOnlyList<uint>> s_seedItems = new(() => FindTemplates("Gardening", id => Plant(id)?.IsSeedItem == true));
    private static readonly Lazy<IReadOnlyList<uint>> s_spellTemplates = new(() => FindTemplates("Gardening", id => Spell(id) is not null));

    /// <summary>Every seed item.</summary>
    internal static IReadOnlyList<uint> SeedItems => s_seedItems.Value;

    /// <summary>Every garden spell, treasure cards included.</summary>
    internal static IReadOnlyList<uint> SpellTemplates => s_spellTemplates.Value;

    /// <summary>
    /// Reads a rate such as "1d10h30m" as seconds.
    /// </summary>
    /// <param name="rate">The rate text.</param>
    /// <returns>The seconds; 0 for an empty or unreadable rate, which means never.</returns>
    internal static long ParseRate(string rate) {
        if (string.IsNullOrWhiteSpace(rate)) {
            return 0;
        }

        var match = s_rate.Match(rate);
        if (!match.Success) {
            return 0;
        }

        long Part(int group, long unit) => match.Groups[group].Success && long.TryParse(match.Groups[group].Value, out var value) ? value * unit : 0;

        return Part(1, 86400) + Part(2, 3600) + Part(3, 60) + Part(4, 1);
    }

    /// <summary>
    /// Reads a plant stage or a seed item.
    /// </summary>
    /// <param name="templateId">The item template.</param>
    /// <returns>The stage, or <c>null</c> when the template has no SeedBehavior.</returns>
    internal static PlantInfo Plant(uint templateId) {
        if (templateId == 0) {
            return null;
        }

        if (s_plants.TryGetValue(templateId, out var cached)) {
            return cached;
        }

        PlantInfo info = null;
        if (CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template
            && (template.m_behaviors ?? []).OfType<SeedBehaviorTemplate>().FirstOrDefault() is { } seed) {
            string objectName = template.m_objectName;
            info = new PlantInfo(
                templateId,
                objectName ?? $"plant {templateId}",
                seed.m_plantStage,
                seed.m_nextStageTemplateID,
                ParseRate(seed.m_nextStageRate),
                seed.m_wiltTemplateID,
                seed.m_deadTemplateID,
                seed.m_youngTemplateID,
                seed.m_matureTemplateID,
                [
                    Need(seed.m_needsWaterMinRate, seed.m_needsWaterMaxRate, seed.m_needsWaterWiltRate),
                    Need(seed.m_needsSunMinRate, seed.m_needsSunMaxRate, seed.m_needsSunWiltRate),
                    Need(seed.m_needsPollinationMinRate, seed.m_needsPollinationMaxRate, seed.m_needsPollinationWiltRate),
                    Need(seed.m_needsMagicMinRate, seed.m_needsMagicMaxRate, seed.m_needsMagicWiltRate),
                    Need(seed.m_needsMusicMinRate, seed.m_needsMusicMaxRate, seed.m_needsMusicWiltRate),
                ],
                ParseRate(seed.m_harvestMinRate),
                ParseRate(seed.m_harvestMaxRate),
                [.. (seed.m_pestList ?? []).Where(entry => entry is not null && entry.m_pestTemplateID != 0)
                    .Select(entry => new PestChance(entry.m_pestTemplateID, entry.m_percentPerHour))],
                ParseRate(seed.m_pestUpdateRate),
                seed.m_harvestXP,
                seed.m_rewardsRating,
                seed.m_maximumNumberOfHarvests,
                seed.m_seedSize,
                seed.m_gardeningLevelRequirement,
                // The seed item is the only stage the backpack holds: an object, not a placed plant.
                objectName?.EndsWith("_Seed", StringComparison.OrdinalIgnoreCase) == true
                    || (template.m_adjectiveList ?? []).Any(adjective => adjective.ToString() == "Seed"));
        }

        return s_plants.GetOrAdd(templateId, info);
    }

    /// <summary>
    /// Reads a garden spell by its template id, or by the hash of its name as some windows send it.
    /// </summary>
    /// <param name="spellId">The id the client sent.</param>
    /// <returns>The spell, or <c>null</c> when it is not a garden spell.</returns>
    internal static GardenSpellInfo Spell(uint spellId) {
        if (spellId == 0) {
            return null;
        }

        if (s_spells.TryGetValue(spellId, out var cached)) {
            return cached;
        }

        var templateId = spellId;
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not GardenSpellTemplate spell) {
            templateId = Spells.SpellFactory.GetTemplateIdByHash(spellId);
            spell = templateId == 0 ? null : CoreObjectFactory.GetCoreTemplate(templateId) as GardenSpellTemplate;
        }

        GardenSpellInfo info = null;
        if (spell is not null) {
            info = new GardenSpellInfo(
                templateId,
                spell.m_name.ToString() ?? $"spell {templateId}",
                spell.m_gardenSpellType,
                spell.m_utilitySpellType,
                [spell.m_providesWater, spell.m_providesSun, spell.m_providesPollination, spell.m_providesMagic, spell.m_providesMusic],
                spell.m_soilTemplateID,
                spell.m_protectionTemplateID,
                spell.m_bugZapLevel,
                Math.Max(0, spell.m_energyCost),
                spell.m_affectedRadius,
                LevelRequirementOf(spell),
                spell.m_Treasure);
        }

        return s_spells.GetOrAdd(spellId, info);
    }

    /// <summary>
    /// Reads a pest or a protection.
    /// </summary>
    /// <param name="templateId">Its template.</param>
    /// <returns>The pest, or <c>null</c> when the template has no PestBehavior.</returns>
    internal static PestInfo Pest(uint templateId) {
        if (templateId == 0) {
            return null;
        }

        if (s_pests.TryGetValue(templateId, out var cached)) {
            return cached;
        }

        PestInfo info = null;
        if (CoreObjectFactory.GetCoreTemplate(templateId) is CoreTemplate core
            && BehaviorsOf(core).OfType<PestBehaviorTemplate>().FirstOrDefault() is { } pest) {
            info = new PestInfo(templateId, NameOf(core) ?? $"pest {templateId}", pest.m_pestRank,
                ParseRate(pest.m_wiltMinRate), ParseRate(pest.m_wiltMaxRate), pest.m_goodPest,
                [pest.m_providesWater, pest.m_providesSun, pest.m_providesPollination, pest.m_providesMagic, pest.m_providesMusic]);
        }

        return s_pests.GetOrAdd(templateId, info);
    }

    /// <summary>
    /// The lowest gardening level a spell's purchase requirements ask for.
    /// </summary>
    internal static int LevelRequirementOf(SpellTemplate spell) {
        var level = 1;
        foreach (var requirement in spell?.m_purchaseRequirements?.m_requirements ?? []) {
            if (requirement is ReqGardeningLevel gardening) {
                level = Math.Max(level, (int) Math.Ceiling(gardening.m_numericValue));
            }
        }

        return level;
    }

    private static NeedRate Need(string min, string max, string wilt) {
        var low = ParseRate(min);
        var high = Math.Max(low, ParseRate(max));

        return new NeedRate(low, high, ParseRate(wilt));
    }

    private static IEnumerable<BehaviorTemplate> BehaviorsOf(CoreTemplate template)
        => (template as GameObjectTemplate)?.m_behaviors ?? [];

    private static string NameOf(CoreTemplate template)
        => (template as GameObjectTemplate)?.m_objectName.ToString();

    private static IReadOnlyList<uint> FindTemplates(string pathPart, Func<uint, bool> isWanted) {
        var found = new List<uint>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string path = location?.m_filename;
            if (path is null || !path.Contains(pathPart, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            try {
                if (isWanted(location.m_id)) {
                    found.Add(location.m_id);
                }
            } catch (Exception exception) {
                Imlight.Common.Logger.Debug("Gardening: template {TemplateId} ({Path}) could not be read: {Reason}",
                    Imlight.Common.Logger.Args(location.m_id, path, exception.Message));
            }
        }

        found.Sort();

        return found;
    }

}
