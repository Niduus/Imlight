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
    private const int DefaultMaximumHappiness = 1000;
    private const int DefaultMemberXpMultiplier = 2;

    /// <summary>
    /// The happiness a pet starts with.
    /// </summary>
    internal static int StartingHappiness => Math.Max(0, ConfigurationManager.GetValue("Pets.StartingHappiness", DefaultStartingHappiness));

    /// <summary>
    /// The most happiness a pet can have.
    /// </summary>
    internal static int MaximumHappiness => Math.Max(1, ConfigurationManager.GetValue("Pets.MaxHappiness", DefaultMaximumHappiness));

    private const int DefaultMaxStat = 260;

    /// <summary>
    /// The ceiling on a pet stat's own maximum before talents and jewels: [Pets] MaxStrength, MaxIntellect, MaxAgility,
    /// MaxWill and MaxPower (260 by default, Niduus 2026-10-20). A pet whose kind or parents give less keeps less; it
    /// is not raised to this. 0 means no ceiling.
    /// </summary>
    internal static int MaxStat(string stat)
        => string.IsNullOrEmpty(stat) ? 0 : Math.Max(0, ConfigurationManager.GetValue($"Pets.Max{stat}", DefaultMaxStat));

    /// <summary>
    /// [Pets] StartAtMaxStats: when true, a new pet (bought, dropped, given or hatched) gets the server maximum
    /// (MaxStrength and the others) as its maximum and starts with every stat at it. Off by default (Niduus,
    /// 2026-10-22): a new pet gets its kind's own maximum, or its parents' from hatching, and starts at its kind's
    /// starting stats; games and snacks train the stats up from there.
    /// </summary>
    internal static bool StartAtMaxStats => ConfigurationManager.GetValue("Pets.StartAtMaxStats", false);

    /// <summary>
    /// The factor members get on the XP their pets earn.
    /// </summary>
    internal static int MemberXpMultiplier => Math.Max(1, ConfigurationManager.GetValue("Pets.MemberXpMultiplier", DefaultMemberXpMultiplier));

    /// <summary>
    /// When true (the real game), experience past a new age counts toward the next age (244/250 + 110 is the new age
    /// at 104). When false, the extra is lost and the pet starts the new age at 0. Replaces XpCarriesOver, whose old
    /// false in Niduus's ini would otherwise stick (Niduus, 2026-10-19).
    /// </summary>
    internal static bool KeepExtraXp => ConfigurationManager.GetValue("Pets.KeepExtraXp", true);

    /// <summary>
    /// When true, a pet's XP is a running total that is not reset at a new age, so the 125 XP that made it a Teen
    /// counts toward the 250 for Adult. Off by default; the real game starts each age over (Niduus, 2026-10-19).
    /// </summary>
    internal static bool RunningTotalXp => ConfigurationManager.GetValue("Pets.RunningTotalXp", false);

    /// <summary>
    /// Double XP &amp; Rewards also doubles the stats a snack gives (+15 becomes +30).
    /// </summary>
    internal static bool DoubleSnackStats => ConfigurationManager.GetValue("Pets.DoubleSnackStats", true);

    /// <summary>
    /// After a pet game is skipped, show the end of game window so a snack can be fed from it.
    /// </summary>
    internal static bool SkipShowsFeedWindow => ConfigurationManager.GetValue("Pets.SkipShowsFeedWindow", true);

    /// <summary>
    /// When true, a skipped pet game gives its XP and the stats of a track like a win. Off by default (Niduus,
    /// 2026-10-19): only the snack fed after the skip counts.
    /// </summary>
    internal static bool SkipGameRewards => ConfigurationManager.GetValue("Pets.SkipGameRewards", false);

    /// <summary>
    /// When true, feeding an item to a pet costs pet energy for the tiers PetFeedingRewardConfig.xml marks as costing
    /// energy (none do in the game files today). False makes every item feed free.
    /// </summary>
    internal static bool ItemFeedCostsEnergy => ConfigurationManager.GetValue("Pets.ItemFeedCostsEnergy", true);

    /// <summary>
    /// The pet energy one item feed costs when it costs energy. The game files do not give a number, so 1 is a guess.
    /// </summary>
    internal static int ItemFeedEnergyCost => Math.Max(0, ConfigurationManager.GetValue("Pets.ItemFeedEnergyCost", 1));

    /// <summary>
    /// The pet XP the worn pet gets for each finished quest ([Pets] QuestXp, read live; Niduus 2026-10-27: 4).
    /// </summary>
    internal static int QuestXp => Math.Max(0, ConfigurationManager.GetValue("Pets.QuestXp", 4));

    /// <summary>
    /// Gets the factor applied to XP from a source, for a wizard who counts as a member.
    /// </summary>
    /// <param name="source">Where the XP comes from.</param>
    /// <returns>The factor, 1 when the source gets no bonus.</returns>
    internal static int GetXpMultiplier(PetXpSource source)
        => source is PetXpSource.Game or PetXpSource.Snack
           && Imlight.CoreLib.Game.Tools.MemberBenefits.IsOn(Imlight.CoreLib.Game.Tools.MemberBenefits.DOUBLE_PET_XP)
            ? MemberXpMultiplier : 1;

}
