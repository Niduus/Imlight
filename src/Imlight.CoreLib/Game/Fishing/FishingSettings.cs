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
using Imlight.Common;

namespace Imlight.CoreLib.Game.Fishing;

/// <summary>The [Fishing] section of Imlight.ini.</summary>
internal static class FishingSettings {

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Fishing.Enabled", true);

    /// <summary>How many fish the basket holds.</summary>
    internal static readonly int BasketSize = Math.Clamp(ConfigurationManager.GetValue("Fishing.BasketSize", 100), 1, 1000);

    /// <summary>Teach every wizard the Lesser lure of each school and Reveal Fish when they log in without them.</summary>
    internal static readonly bool StarterSpells = ConfigurationManager.GetValue("Fishing.StarterSpells", true);

    /// <summary>The fewest seconds before a new fish swims in for one that was caught.</summary>
    internal static readonly int MinRespawnSeconds = Math.Clamp(ConfigurationManager.GetValue("Fishing.MinRespawnSeconds", 30), 1, 3600);

    /// <summary>Fishing XP is multiplied by this.</summary>
    internal static readonly double XpMultiplier = Math.Clamp(ConfigurationManager.GetValue("Fishing.XpMultiplier", 1.0), 0, 100);

    /// <summary>Casting a fishing spell uses its energy.</summary>
    internal static readonly bool UseEnergy = ConfigurationManager.GetValue("Fishing.UseEnergy", true);

    /// <summary>Every shop vendor also offers "Sell Fish" (Lucky Hookline always does).</summary>
    internal static readonly bool SellAtShops = ConfigurationManager.GetValue("Fishing.SellAtShops", true);

}
