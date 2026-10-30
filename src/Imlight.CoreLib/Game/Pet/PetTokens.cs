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
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Pet Talent Tokens (Niduus, 2026-10-30: "add vendor for Pet Talent Tokens and add them in as drops when a pet is
/// Ancient and higher"). The tokens are the game's PetPower_Reagent* reagents, the ingredients of Lowe Springfield's pet
/// jewel recipes: Talent Token, the Elemental, Spirit and Harmonious Tokens, one token per school and the combat tokens
/// (Hydra, Frostbite...). The game's pet level loot tables are named after them (PetLevel_Ancient_Common_01,
/// PetLevel_Epic_Elemental_01, PetLevel_Mega_Fire_01...), so a pet reaching an age gives that age's tokens:
/// Ancient a Talent Token, Epic the token of its school's group, Mega its school's token, Ultra its school's token and a
/// combat token. A won battle can also drop one while the pet worn is Ancient or older, and Tennant Wastelander in the
/// Pet Pavilion's pet shop sells them all. Every number is in Imlight.ini [PetTokens], read live.
/// </summary>
internal static class PetTokens {

    internal const uint TALENT = 1500062;
    internal const uint ELEMENTAL = 1500063;
    internal const uint SPIRIT = 1500064;
    internal const uint HARMONIOUS = 1500077;

    private const int ANCIENT = 4;
    private const int EPIC = 5;
    private const int MEGA = 6;
    private const int ULTRA = 7;

    private static readonly Dictionary<string, uint> s_schoolTokens = new(StringComparer.OrdinalIgnoreCase) {
        ["Ice"] = 1500065,
        ["Fire"] = 1500066,
        ["Storm"] = 1500067,
        ["Life"] = 1500068,
        ["Death"] = 1500069,
        ["Myth"] = 1500070,
        ["Balance"] = 1500071,
    };

    // Hydra, Frostbite, Smoke Screen, Feint, Potboiler and Shatter.
    private static readonly uint[] s_combatTokens = [1500074, 1500075, 1500076, 1500096, 1500097, 1500098];

    /// <summary>Every token, in the order the vendor lists them.</summary>
    internal static readonly uint[] All = [TALENT, ELEMENTAL, SPIRIT, HARMONIOUS, .. s_schoolTokens.Values.Order(), .. s_combatTokens];

    // Tennant Wastelander (WC-PARK-NPC01), beside Lowe Springfield and her pet jewel recipes in the Pet Pavilion's pet shop.
    internal static uint VendorNpc => ConfigurationManager.GetValue("PetTokens.VendorNpc", 175491u);

    private static bool VendorOn => ConfigurationManager.GetValue("PetTokens.Vendor", true);

    /// <summary>Whether an NPC is the token vendor.</summary>
    internal static bool IsVendor(uint templateId) => VendorOn && templateId != 0 && templateId == VendorNpc;

    /// <summary>What the token vendor asks for a token, in gold, or null for anything else.</summary>
    internal static int? PriceOf(uint templateId) {
        if (templateId == TALENT) {
            return Math.Max(0, ConfigurationManager.GetValue("PetTokens.TalentTokenPrice", 250));
        }

        if (templateId is ELEMENTAL or SPIRIT or HARMONIOUS) {
            return Math.Max(0, ConfigurationManager.GetValue("PetTokens.EpicTokenPrice", 500));
        }

        if (s_schoolTokens.ContainsValue(templateId)) {
            return Math.Max(0, ConfigurationManager.GetValue("PetTokens.SchoolTokenPrice", 1000));
        }

        return s_combatTokens.Contains(templateId) ? Math.Max(0, ConfigurationManager.GetValue("PetTokens.CombatTokenPrice", 2500)) : null;
    }

    /// <summary>
    /// The tokens a pet earns on reaching a level, added to that level's loot: none below Ancient or while
    /// [PetTokens] LevelUpTokens is false.
    /// </summary>
    internal static void AddLevelUpTokens(DropTableResult loot, WizClientObjectItem pet, int level) {
        if (!ConfigurationManager.GetValue("PetTokens.LevelUpTokens", true) || level < ANCIENT) {
            return;
        }

        var count = Math.Max(0, ConfigurationManager.GetValue("PetTokens.LevelUpAmount", 1));
        if (count == 0) {
            return;
        }

        var school = SchoolOf(pet);
        uint[] tokens = level switch {
            ANCIENT => [TALENT],
            EPIC => [EpicTokenOf(school)],
            MEGA => [SchoolTokenOf(school)],
            _ => [SchoolTokenOf(school), s_combatTokens[Random.Shared.Next(s_combatTokens.Length)]],
        };
        foreach (var token in tokens) {
            Add(loot, token, count);
        }
    }

    /// <summary>
    /// After a won battle: maybe a token, if the pet worn is old enough ([PetTokens] BattleDrops, BattleDropChance,
    /// BattleDropAmount and BattleDropMinAge). The token is one of those the pet's age gives or a younger age gave.
    /// </summary>
    internal static void GrantBattleDrop(IActorRef session, Wizard wizard) {
        if (wizard is null || !ConfigurationManager.GetValue("PetTokens.BattleDrops", true)) {
            return;
        }

        var pet = wizard.GetEquippedPetItem();
        if (!PetProgression.TryGetPet(pet, out var behavior, out _)) {
            return;
        }

        var level = (int) behavior.m_level;
        var minAge = Math.Clamp(ConfigurationManager.GetValue("PetTokens.BattleDropMinAge", ANCIENT), ANCIENT, ULTRA);
        var chance = Math.Clamp(ConfigurationManager.GetValue("PetTokens.BattleDropChance", 10.0), 0, 100);
        if (level < minAge || chance <= 0 || Random.Shared.NextDouble() * 100 >= chance) {
            return;
        }

        var school = SchoolOf(pet);
        List<uint> pool = [TALENT];
        if (level >= EPIC) {
            pool.Add(EpicTokenOf(school));
        }

        if (level >= MEGA) {
            pool.Add(SchoolTokenOf(school));
        }

        if (level >= ULTRA) {
            pool.AddRange(s_combatTokens);
        }

        var loot = new DropTableResult();
        Add(loot, pool[Random.Shared.Next(pool.Count)], Math.Max(1, ConfigurationManager.GetValue("PetTokens.BattleDropAmount", 1)));
        LootGranter.GrantAndDisplay(session, wizard, loot);
    }

    private static void Add(DropTableResult loot, uint token, int count) {
        var existing = loot.Items.FirstOrDefault(item => item.ItemId == token.ToString());
        if (existing is not null) {
            existing.Quantity += count;

            return;
        }

        var name = (CoreObjectFactory.GetCoreTemplate(token) as WizItemTemplate)?.m_displayName;
        loot.Items.Add(new DropItemResult { ItemId = token.ToString(), ItemName = (string) name ?? token.ToString(), Quantity = count });
    }

    // The pet's school from its item template ("Fire"); a pet of no known school counts as Balance.
    private static string SchoolOf(WizClientObjectItem pet) {
        string school = (CoreObjectFactory.GetCoreTemplate((uint) pet.m_templateID) as WizItemTemplate)?.m_school;

        return string.IsNullOrWhiteSpace(school) || !s_schoolTokens.ContainsKey(school) ? "Balance" : school;
    }

    private static uint EpicTokenOf(string school) => school.ToLowerInvariant() switch {
        "fire" or "ice" or "storm" => ELEMENTAL,
        "life" or "death" or "myth" => SPIRIT,
        _ => HARMONIOUS,
    };

    private static uint SchoolTokenOf(string school) => s_schoolTokens[school];

}
