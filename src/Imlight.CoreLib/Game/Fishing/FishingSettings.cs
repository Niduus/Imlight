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
using Imlight.Common;

namespace Imlight.CoreLib.Game.Fishing;

/// <summary>The [Fishing] section of Imlight.ini.</summary>
internal static class FishingSettings {

    internal static bool Enabled => ConfigurationManager.GetValue("Fishing.Enabled", true);

    /// <summary>How many fish the basket holds.</summary>
    internal static int BasketSize => Math.Clamp(ConfigurationManager.GetValue("Fishing.BasketSize", 100), 1, 1000);

    /// <summary>Teach every wizard the Lesser lure of each school and Reveal Fish when they log in without them.</summary>
    internal static bool StarterSpells => ConfigurationManager.GetValue("Fishing.StarterSpells", true);

    /// <summary>The fewest seconds before a new fish swims in for one that was caught.</summary>
    internal static int MinRespawnSeconds => Math.Clamp(ConfigurationManager.GetValue("Fishing.MinRespawnSeconds", 30), 1, 3600);

    /// <summary>Fishing XP is multiplied by this.</summary>
    internal static double XpMultiplier => Math.Clamp(ConfigurationManager.GetValue("Fishing.XpMultiplier", 1.0), 0, 100);

    /// <summary>Casting a fishing spell uses its energy.</summary>
    internal static bool UseEnergy => ConfigurationManager.GetValue("Fishing.UseEnergy", true);

    /// <summary>Chests swim in the ponds: a fish with a loot chance in the zone's fishing file is a chest that often.</summary>
    internal static bool Chests => ConfigurationManager.GetValue("Fishing.Chests", true);

    /// <summary>The game's chest chances are multiplied by this.</summary>
    internal static double ChestChanceMultiplier => Math.Clamp(ConfigurationManager.GetValue("Fishing.ChestChanceMultiplier", 1.0), 0, 100);

    /// <summary>
    /// The prize of one tournament fish: [Fishing] Tournament&lt;Medal&gt;Gold and Tournament&lt;Medal&gt;Items (item template
    /// ids, comma separated), for GoldMedal, SilverMedal, BronzeMedal and Consolation.
    /// </summary>
    internal static (int Gold, List<uint> Items) TournamentPrize(TournamentMedal medal) {
        var (key, gold) = medal switch {
            TournamentMedal.Gold => ("GoldMedal", 10000),
            TournamentMedal.Silver => ("SilverMedal", 5000),
            TournamentMedal.Bronze => ("BronzeMedal", 2500),
            _ => ("Consolation", 250),
        };
        var items = new List<uint>();
        foreach (var part in ConfigurationManager.GetValue($"Fishing.Tournament{key}Items", "").Split(',', ';', ' ')) {
            if (uint.TryParse(part.Trim(), out var id) && id != 0) {
                items.Add(id);
            }
        }

        return (Math.Max(0, ConfigurationManager.GetValue($"Fishing.Tournament{key}Gold", gold)), items);
    }

    /// <summary>Every shop vendor also offers "Sell Fish". Off: only Lucky Hookline buys fish (Niduus 2026-10-25).</summary>
    internal static bool SellAtShops => ConfigurationManager.GetValue("Fishing.SellFishAtAllShops", false);

}
