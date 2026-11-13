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
 * BADGE REWARDS
 * ========================================================================
 *
 * PURPOSE:
 * What a badge's reward loot table (BadgeTemplate m_rewardLootTable) gives, for the gift box of the badge book and its
 * Claim Reward button.
 *
 * NOTE:
 * The loot tables live on the real server, not in the game files, so the common ones are worked out from their names
 * and the reward texts the game shows (Wizard404 badge pages, 2026-10):
 *   BadgeLT_MaxSpellTier_<Spell>  500 of that spell's Spellement (Shard-<Spell>-01), the Spellwright badges (240).
 *   SkeletonKey-Gold-LT           1 Gold Skeleton Key (1217523), the Challenge badges (86).
 * The other 26 (raid house guests, saga boss rematches, "Your School" Chronicle Spellements, guild museum rewards) are
 * not known yet: the badge window still shows the game's reward text, and claiming gives nothing.
 */

using System;
using System.Collections.Generic;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Badges;

internal static class BadgeRewards {

    private const string SPELLWRIGHT_PREFIX = "BadgeLT_MaxSpellTier_";
    private const int SPELLWRIGHT_AMOUNT = 500;
    private const string GOLD_KEY_TABLE = "SkeletonKey-Gold-LT";
    internal const ulong GOLD_SKELETON_KEY = 1217523;

    // Spellement files whose names differ from the badge's loot table name.
    private static readonly Dictionary<string, string> s_shardAliases = new(StringComparer.OrdinalIgnoreCase) {
        ["Cyclops"] = "Cyclopse",
    };

    /// <summary>The items a badge reward loot table gives; empty when the table is not known.</summary>
    internal static List<DropItemResult> ItemsOf(string lootTable) {
        if (string.IsNullOrEmpty(lootTable)) {
            return [];
        }

        if (string.Equals(lootTable, GOLD_KEY_TABLE, StringComparison.OrdinalIgnoreCase)) {
            return CoreObjectFactory.HasTemplate(GOLD_SKELETON_KEY) ? [Item(GOLD_SKELETON_KEY, 1)] : [];
        }

        if (lootTable.StartsWith(SPELLWRIGHT_PREFIX, StringComparison.OrdinalIgnoreCase)) {
            var shard = ShardOf(lootTable[SPELLWRIGHT_PREFIX.Length..]);

            return shard != 0 ? [Item(shard, SPELLWRIGHT_AMOUNT)] : [];
        }

        return [];
    }

    /// <summary>The reward as a drop table result, for the loot list the game shows and for granting.</summary>
    internal static DropTableResult ResultOf(string lootTable)
        => new() { DropTableId = lootTable ?? string.Empty, Items = ItemsOf(lootTable) };

    private static ulong ShardOf(string spell) {
        if (s_shardAliases.TryGetValue(spell, out var alias)) {
            spell = alias;
        }

        foreach (var name in new[] { spell, spell.Replace('_', '-'), spell.Replace("_", "") }) {
            var id = CoreObjectFactory.IdOfFileName($"Shard-{name}-01");
            if (id != 0) {
                return id;
            }
        }

        return 0;
    }

    private static DropItemResult Item(ulong templateId, int quantity)
        => new() { ItemId = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture), Quantity = quantity };

}
