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
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Models.World;

public class DropTable {

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double RollChance { get; set; } = 1.0;

    /// <summary>
    /// A rank and kind of drop ("Boss/Gear"): the table rolls at spiraldb-extra/DropOdds.json's percent for it
    /// (Game/DropTables/DropOdds.cs), and RollChance only counts while that file has no such entry. Null: RollChance.
    /// </summary>
    public string Odds { get; set; } = null;
    public int Weight { get; set; } = 100;
    public double NoneChance { get; set; } = 0.0;
    public double PityCounter { get; set; } = 0.0;
    public int MinGold { get; set; } = 0;
    public int MaxGold { get; set; } = 0;
    public int ExperienceAmount { get; set; } = 0;
    public int TrainingPoints { get; set; } = 0;
    public bool GrantsPotionSlot { get; set; } = false;

    /// <summary>
    /// How Items roll. False (the files of SpiralDB): one item, picked at random among those the player may have.
    /// True (tables made in the app's Drop tables editor): every item rolls on its own, by its Chance, and drops
    /// MinCount to MaxCount of itself; a Guaranteed item always drops.
    /// </summary>
    public bool EachItem { get; set; } = false;

    public List<DropItem> Items { get; set; } = [];

    /// <summary>
    /// The mini-game reward form: exactly one of these is rolled by weight. The quest and mob rollers ignore it.
    /// </summary>
    public List<DropOutcome> Outcomes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = Environment.UserName;
    public string ModifiedBy { get; set; } = Environment.UserName;

}

public class DropItem {

    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public RequirementList? Requirements { get; set; } = null;

    /// <summary>With EachItem: percent chance it drops (100 when left out).</summary>
    public double? Chance { get; set; } = null;

    /// <summary>With EachItem: how many drop, from MinCount to MaxCount (1 when left out).</summary>
    public int? MinCount { get; set; } = null;

    public int? MaxCount { get; set; } = null;

    /// <summary>With EachItem: it always drops, whatever its Chance.</summary>
    public bool Guaranteed { get; set; } = false;

}

public class DropOutcome {

    public string Kind { get; set; } = string.Empty;
    public double DropRate { get; set; } = 0.0;
    public double ManaPercent { get; set; } = 0.0;
    public int ManaFlat { get; set; } = 0;
    public int MinGold { get; set; } = 0;
    public int MaxGold { get; set; } = 0;
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;

}

/// <summary>
/// Represents the actual loot results after rolling a DropTable.
/// This is the intermediate form between DropTable (configuration) and LootInfoList (network format).
/// </summary>
public class DropTableResult {

    public string DropTableId { get; set; } = string.Empty;
    public int GoldAmount { get; set; } = 0;
    public int ExperienceAmount { get; set; } = 0;
    public string MagicSchool { get; set; } = "All";
    public int TrainingPoints { get; set; } = 0;
    public bool GrantsPotionSlot { get; set; } = false;
    public List<DropItemResult> Items { get; set; } = [];

    /// <summary>
    /// Spell template IDs taught to the player by the loot granter (not rolled; filled when granting).
    /// </summary>
    public List<uint> SpellIds { get; set; } = [];

    /// <summary>
    /// Treasure cards added to the player's treasure book by the loot granter (not rolled; filled when granting).
    /// </summary>
    public List<DropTreasureCardResult> TreasureCards { get; set; } = [];
    public bool HasRewards => GoldAmount > 0 || ExperienceAmount > 0 || TrainingPoints > 0 || Items.Count > 0
        || SpellIds.Count > 0 || TreasureCards.Count > 0;

}

public class DropItemResult {

    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;

}

public class DropTreasureCardResult {

    public uint SpellId { get; set; }
    public int Quantity { get; set; } = 1;

}
