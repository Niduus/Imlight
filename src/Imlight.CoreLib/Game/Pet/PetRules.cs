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
 * PET RULES
 * ========================================================================
 *
 * PURPOSE:
 * Holds the pet rules the client files do not define. Each one has a default and can be
 * overridden in the [Pets] section of Imlight.ini.
 *
 * USAGE EXAMPLE:
 * pet.m_maximumHappiness = (uint) PetRules.MaximumHappiness;
 *
 * NOTE:
 * Rules that exist in a client file are read from that file, never from here. Everyone counts as a member.
 *
 * TODO:
 * - Only game and snack XP get the member bonus, community sources do not say whether combat XP does.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The server-side pet rules, with their defaults.
/// </summary>
internal static class PetRules {

    private const int DefaultStartingHappiness = 50;
    private const int DefaultMaximumHappiness = 100;
    private const int DefaultMemberXpMultiplier = 2;

    /// <summary>
    /// The happiness a pet starts with.
    /// </summary>
    internal static int StartingHappiness { get; } = Math.Max(0, ConfigurationManager.GetValue("Pets.StartingHappiness", DefaultStartingHappiness));

    /// <summary>
    /// The most happiness a pet can have.
    /// </summary>
    internal static int MaximumHappiness { get; } = Math.Max(1, ConfigurationManager.GetValue("Pets.MaximumHappiness", DefaultMaximumHappiness));

    /// <summary>
    /// The factor members get on the XP their pets earn.
    /// </summary>
    internal static int MemberXpMultiplier { get; } = Math.Max(1, ConfigurationManager.GetValue("Pets.MemberXpMultiplier", DefaultMemberXpMultiplier));

    /// <summary>
    /// When false (the real game), experience past a new level is lost: the pet goes up one level and
    /// starts it at 0. When true, the extra experience counts toward the next level.
    /// </summary>
    internal static bool XpCarriesOver { get; } = ConfigurationManager.GetValue("Pets.XpCarriesOver", false);

    /// <summary>
    /// After a pet game is skipped, show the end of game window so a snack can be fed from it.
    /// </summary>
    internal static bool SkipShowsFeedWindow { get; } = ConfigurationManager.GetValue("Pets.SkipShowsFeedWindow", true);

    /// <summary>
    /// Gets the factor applied to XP from a source, for a wizard who counts as a member.
    /// </summary>
    /// <param name="source">Where the XP comes from.</param>
    /// <returns>The factor, 1 when the source gets no bonus.</returns>
    internal static int GetXpMultiplier(PetXpSource source)
        => source is PetXpSource.Game or PetXpSource.Snack ? MemberXpMultiplier : 1;

}
