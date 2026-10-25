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
 * Gardening levels, the garden spells each level teaches, and what a harvest gives.
 *
 * USAGE EXAMPLE:
 * var level = GardenLevels.LevelAt(xp);
 * var loot = GardenHarvests.Roll(harvestedStage, seedTemplateId, elder: false);
 *
 * NOTE:
 * - The XP table is the client's GardeningXPConfig: the XP to go from each level to the next, up to level 20.
 * - spiraldb-extra/Gardening/Spells.json lists the garden trainers' spells (from Wizard404): the rank each needs, its
 *   price and the trainers that teach it. The trainers sell them for gold; the spells no trainer sells come with rank 1.
 *   [Gardening] FreeSpells = true gives every spell with its rank instead.
 * - Harvests are not in the game files, so the server makes them: gold by the plant's rewards rating, a reagent or two
 *   by the seed's rank, and seeds back now and then (always 1 to 3 from an elder plant). A file
 *   spiraldb-extra/Gardening/Harvests.json may give a seed its own harvest: { "<seed template id>": { "Gold": [min, max],
 *   "Items": [ { "ItemId": 123, "Min": 1, "Max": 2, "Chance": 50 } ] } }.
 *
 * TODO:
 * - Real harvest tables per plant, from pages Niduus saves.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Gardening;

/// <summary>
/// The gardening levels.
/// </summary>
internal static class GardenLevels {

    internal const int MaxLevel = 20;

    // GardeningXPConfig: the XP from level n to n + 1, at index n - 1.
    private static readonly int[] s_toNext = [
        20, 100, 200, 500, 1000, 2000, 4000, 7000, 11000, 160000,
        200000, 250000, 312500, 390625, 439450, 494380, 556175, 625700, 703900,
    ];

    /// <summary>The level a wizard is at with this much XP.</summary>
    internal static int LevelAt(int xp) {
        var level = 1;
        long needed = 0;
        foreach (var step in s_toNext) {
            needed += step;
            if (xp < needed) {
                break;
            }

            level++;
        }

        return Math.Min(level, MaxLevel);
    }

    /// <summary>The XP a level starts at.</summary>
    internal static int XpOf(int level)
        => (int) s_toNext.Take(Math.Clamp(level, 1, MaxLevel) - 1).Sum(step => (long) step);

}

/// <summary>A garden spell a gardening rank teaches.</summary>
internal sealed record GardenLesson(uint SpellId, string Name, int Level, int Gold, string Trainer, IReadOnlyList<uint> Trainers, int Order);

/// <summary>
/// The garden spells and the rank each needs.
/// </summary>
internal static class GardenLessons {

    private static readonly Lazy<IReadOnlyList<GardenLesson>> s_lessons = new(Load);

    internal static IReadOnlyList<GardenLesson> All => s_lessons.Value;

    private static readonly Lazy<HashSet<uint>> s_trainers = new(() => [.. All.SelectMany(lesson => lesson.Trainers)]);

    /// <summary>
    /// Whether every garden spell comes with its rank. Off (the default), only the spells no trainer sells (Plow, Plant
    /// Seed, Inspect) come with rank 1, and the garden trainers sell the rest.
    /// </summary>
    internal static bool FreeSpells => ConfigurationManager.GetValue("Gardening.FreeSpells", false);

    /// <summary>Whether an NPC template is a garden trainer.</summary>
    internal static bool IsTrainer(uint npcTemplateId)
        => npcTemplateId != 0 && s_trainers.Value.Contains(npcTemplateId);

    /// <summary>The spells a garden trainer teaches, in its own order.</summary>
    internal static IEnumerable<GardenLesson> ForTrainer(uint npcTemplateId)
        => All.Where(lesson => lesson.Trainers.Contains(npcTemplateId)).OrderBy(lesson => lesson.Level).ThenBy(lesson => lesson.Order);

    /// <summary>A wizard's gardening rank (1 when never set).</summary>
    internal static int RankOf(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard)
        => Math.Max(1, (int) (wizard?.GameStats?.m_gardeningLevel ?? 1));

    /// <summary>The gold a garden spell costs at a trainer: the spell's own price, or the trainer list's.</summary>
    internal static int PriceOf(Imcodec.ObjectProperty.TypeCache.SpellTemplate spell) {
        if (spell is null) {
            return 0;
        }

        if (spell.m_baseCost > 0) {
            return spell.m_baseCost;
        }

        string name = spell.m_name;
        return All.FirstOrDefault(lesson => string.Equals(lesson.Name, name, StringComparison.OrdinalIgnoreCase))?.Gold ?? 0;
    }

    /// <summary>The spells a wizard of a gardening level may know.</summary>
    internal static IEnumerable<GardenLesson> UpTo(int level)
        => All.Where(lesson => lesson.Level <= level);

    private static IReadOnlyList<GardenLesson> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "Gardening", "Spells.json");
        try {
            if (!File.Exists(path)) {
                Logger.Warning("Gardening: {Path} is missing; no garden spells are taught.", Logger.Args(path));

                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lessons = new List<GardenLesson>();
            foreach (var entry in document.RootElement.GetProperty("Spells").EnumerateArray()) {
                lessons.Add(new GardenLesson(
                    entry.GetProperty("SpellId").GetUInt32(),
                    entry.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                    entry.TryGetProperty("Level", out var level) ? level.GetInt32() : 1,
                    entry.TryGetProperty("Gold", out var gold) ? gold.GetInt32() : 0,
                    entry.TryGetProperty("Trainer", out var trainer) && trainer.ValueKind == JsonValueKind.String ? trainer.GetString() : null,
                    entry.TryGetProperty("Trainers", out var trainers) && trainers.ValueKind == JsonValueKind.Array
                        ? [.. trainers.EnumerateArray().Select(id => id.GetUInt32())]
                        : [],
                    entry.TryGetProperty("Order", out var order) ? order.GetInt32() : 0));
            }

            return lessons;
        } catch (Exception exception) {
            Logger.Error("Gardening: could not read {Path}: {Reason}", Logger.Args(path, exception.Message));

            return [];
        }
    }

}

/// <summary>
/// What a harvest gives.
/// </summary>
internal static class GardenHarvests {

    // Reagents by the seed's rank: common ones, then mid ranks, then the rare ones.
    private static readonly uint[] s_lowReagents = [106931, 106933, 106935, 106937, 106939, 106961, 106963];
    private static readonly uint[] s_midReagents = [106959, 106965, 106967, 106969, 1206360];
    private static readonly uint[] s_highReagents = [1421822, 1359769, 1646520];

    private sealed record HarvestItem(uint ItemId, int Min, int Max, float Chance);

    private sealed record Harvest(int GoldMin, int GoldMax, IReadOnlyList<HarvestItem> Items);

    private static readonly Lazy<Dictionary<uint, Harvest>> s_overrides = new(LoadOverrides);

    /// <summary>
    /// Rolls a harvest.
    /// </summary>
    /// <param name="harvested">The stage that was harvested.</param>
    /// <param name="seedTemplateId">The seed the plant grew from.</param>
    /// <param name="elder">Whether it was an elder plant, which gives more seeds.</param>
    internal static DropTableResult Roll(PlantInfo harvested, uint seedTemplateId, bool elder) {
        var random = Random.Shared;
        var result = new DropTableResult { DropTableId = "Gardening" };
        if (s_overrides.Value.TryGetValue(seedTemplateId, out var own)) {
            result.GoldAmount = own.GoldMax > 0 ? random.Next(own.GoldMin, own.GoldMax + 1) : 0;
            foreach (var item in own.Items) {
                if (random.NextSingle() * 100f < item.Chance) {
                    Add(result, item.ItemId, random.Next(item.Min, Math.Max(item.Min, item.Max) + 1));
                }
            }

            return result;
        }

        var seed = seedTemplateId == 0 ? null : GardenTemplates.Plant(seedTemplateId);
        var rank = Math.Max(1, seed?.GardeningLevel ?? harvested?.GardeningLevel ?? 1);
        var rating = Math.Max(1, harvested?.RewardsRating ?? 1);
        result.GoldAmount = random.Next(5, 16) * rating + 5 * rank;

        var reagents = rank >= 8 ? s_highReagents : rank >= 4 ? s_midReagents : s_lowReagents;
        Add(result, reagents[random.Next(reagents.Length)], random.Next(1, 3));
        if (rank >= 4 && random.Next(100) < 30) {
            Add(result, s_lowReagents[random.Next(s_lowReagents.Length)], 1);
        }

        if (seedTemplateId != 0) {
            var seeds = elder ? random.Next(1, 4) : random.Next(100) < 20 ? 1 : 0;
            Add(result, seedTemplateId, seeds);
        }

        return result;
    }

    private static void Add(DropTableResult result, uint itemId, int quantity) {
        if (itemId == 0 || quantity <= 0) {
            return;
        }

        result.Items.Add(new DropItemResult { ItemId = itemId.ToString(), Quantity = quantity });
    }

    private static Dictionary<uint, Harvest> LoadOverrides() {
        var found = new Dictionary<uint, Harvest>();
        var path = Path.Combine(SpiralDB.ExtraPath, "Gardening", "Harvests.json");
        if (!File.Exists(path)) {
            return found;
        }

        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var seed in document.RootElement.EnumerateObject()) {
                if (!uint.TryParse(seed.Name, out var seedId) || seed.Value.ValueKind != JsonValueKind.Object) {
                    continue;
                }

                int goldMin = 0, goldMax = 0;
                if (seed.Value.TryGetProperty("Gold", out var gold) && gold.ValueKind == JsonValueKind.Array && gold.GetArrayLength() == 2) {
                    goldMin = gold[0].GetInt32();
                    goldMax = Math.Max(goldMin, gold[1].GetInt32());
                }

                var items = new List<HarvestItem>();
                if (seed.Value.TryGetProperty("Items", out var list) && list.ValueKind == JsonValueKind.Array) {
                    foreach (var item in list.EnumerateArray()) {
                        items.Add(new HarvestItem(
                            item.GetProperty("ItemId").GetUInt32(),
                            item.TryGetProperty("Min", out var min) ? min.GetInt32() : 1,
                            item.TryGetProperty("Max", out var max) ? max.GetInt32() : 1,
                            item.TryGetProperty("Chance", out var chance) ? chance.GetSingle() : 100f));
                    }
                }

                found[seedId] = new Harvest(goldMin, goldMax, items);
            }

            Logger.Information("Gardening: {Count} seeds have their own harvest in {Path}.", Logger.Args(found.Count, path));
        } catch (Exception exception) {
            Logger.Error("Gardening: could not read {Path}: {Reason}", Logger.Args(path, exception.Message));
        }

        return found;
    }

}
