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
 *
 * ========================================================================
 * RECIPE CHANGES
 * ========================================================================
 *
 * PURPOSE:
 * The recipe changes made on the Manager's Recipes tab (Niduus, 2026-10-25: "add config/section for crafting recipes.
 * BEcause like in areas where a GOLD SKELETON KEY takes 400, I want to be able to lower to like 200"): how many of
 * each ingredient a recipe takes, its cooldown and its shop price, and [Crafting] IngredientMultiplier for every
 * recipe left as it is.
 *
 * USAGE EXAMPLE:
 * spiraldb-extra/Customs/RecipeChanges.json:
 *   [ { "Id": 1234, "Name": "Gold Skeleton Key", "Amounts": [ 200, null ], "Cooldown": 3600, "GoldCost": 500 } ]
 * Amounts follow the recipe's ingredients in order; null keeps the game's. The file is read again when it changes.
 *
 * NOTE:
 * The game's crafting window shows the game files' amounts; only the server's count changes.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Crafting;

internal static class RecipeChanges {

    internal sealed class Change {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public List<int?> Amounts { get; set; } = [];
        public int? Cooldown { get; set; }
        public int? GoldCost { get; set; }
    }

    internal const string FILE = "RecipeChanges.json";
    private const string FOLDER = "Customs";

    private static readonly float s_multiplier = Math.Clamp(ConfigurationManager.GetValue("Crafting.IngredientMultiplier", 1f), 0f, 100f);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object s_lock = new();
    private static Dictionary<uint, Change> s_changes = [];
    private static DateTime s_readAt = DateTime.MinValue;

    /// <summary>A recipe's change, or null when it is as the game has it. Reads the file again when it changed.</summary>
    internal static Change Of(uint recipeId) {
        lock (s_lock) {
            var path = Path.Combine(SpiralDB.ExtraPath ?? "", FOLDER, FILE);
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (written != s_readAt) {
                s_readAt = written;
                s_changes = Read(path);
            }

            return s_changes.GetValueOrDefault(recipeId);
        }
    }

    private static Dictionary<uint, Change> Read(string path) {
        if (!File.Exists(path)) {
            return [];
        }

        try {
            var changes = JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(path), s_options) ?? [];
            Logger.Information("Crafting: {0} recipe changes read from {1}.", Logger.Args(changes.Count, FILE));

            return changes.Where(c => c is not null && c.Id != 0).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception e) {
            // The last good changes stay (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Crafting: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return s_changes;
        }
    }

    /// <summary>
    /// How many of a recipe's ingredient one craft takes: the amount set for it, else the game's times
    /// [Crafting] IngredientMultiplier (at least 1).
    /// </summary>
    internal static int AmountOf(uint recipeId, int index, int gameAmount) {
        if (Of(recipeId) is { } change && index < change.Amounts.Count && change.Amounts[index] is { } set) {
            return Math.Max(1, set);
        }

        return s_multiplier == 1f ? gameAmount : Math.Max(1, (int) Math.Round(gameAmount * s_multiplier));
    }

    /// <summary>A recipe's cooldown in seconds before the cooldown multiplier: the one set for it, else the game's.</summary>
    internal static int CooldownOf(uint recipeId, int gameCooldown)
        => Of(recipeId)?.Cooldown is { } set ? Math.Max(0, set) : gameCooldown;

    /// <summary>A recipe's shop price in gold: the one set for it, else the game's.</summary>
    internal static int GoldCostOf(uint recipeId, int gameCost)
        => Of(recipeId)?.GoldCost is { } set ? Math.Max(0, set) : gameCost;

}
