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
 * ARCHMASTERY
 * ========================================================================
 *
 * PURPOSE:
 * The numbers behind Archmastery, as the game's own tutorial (Tutorial.lang, Help.lang) explains them:
 * - The Archmastery rating comes from the level table from level 50, plus gear, jewels and pets (Archmastery
 *   Rating and Archmastery %), so a jewel turns it on earlier. Creatures have it from their own stats.
 * - The Power Orb fills each round. The combatant with the highest Archmastery sets the pace for the battle;
 *   everyone else fills at their share of it (50 against 100 is half as fast).
 * - In PvP the orb is also as efficient as the main deck is full of PvP cards (a half full deck is half as
 *   good); in PvE it is always 100% (WizStatisticEffectConfig m_archmasteryFullnessMinPvE is 1).
 * - The school comes from the deck (MSG_SetDeckArchmastery), and can be switched mid-battle from the orb.
 *
 * USAGE EXAMPLE:
 * var rating = Archmastery.RatingOf(stats, MagicSchool.Fire, 60, isPlayer: true);
 *
 * Last Updated: 10/24/2026
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

internal static class Archmastery {

    // [Archmastery] Enabled: the Power Orb turns power pips into school pips.
    internal static readonly bool Enabled = ConfigurationManager.GetValue("Archmastery.Enabled", true);
    // Gives wizards under level 50 the level 50 Archmastery, so they get school pips too.
    internal static readonly bool AllLevels = ConfigurationManager.GetValue("Archmastery.AllLevels", false);
    // Extra Archmastery for every wizard (on top of gear and jewels).
    internal static readonly float BonusRating = Math.Max(0f, ConfigurationManager.GetValue("Archmastery.BonusRating", 0f));
    // How much the fastest orb fills in a round (1 = a school pip every round). 0 uses the game's own number.
    internal static readonly float FillOverride = Math.Clamp(ConfigurationManager.GetValue("Archmastery.FillPerRound", 0f), 0f, 1f);
    // Creatures with Archmastery in their stats fill their own orb (and set the pace), like the game.
    internal static readonly bool Creatures = ConfigurationManager.GetValue("Archmastery.Creatures", true);

    internal const int LEVEL = 50;
    internal const float MAX_POWER = 7f;
    private const float DEFAULT_FILL = 1f;
    private const float DEFAULT_POWER_PIP_COST = 3f;

    internal static readonly MagicSchool[] Schools = [
        MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Storm, MagicSchool.Myth,
        MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance,
    ];

    /// <summary>
    /// The school the client sent for the deck or the orb as one of the seven schools, or null when it is not one.
    /// 0 means "my own school". The client sends the school's id; an index (0 to 6) is taken too, just in case.
    /// </summary>
    internal static uint? ToSchoolId(uint sent) {
        if (sent == 0) {
            return 0;
        }

        if (Schools.Any(s => (uint) s == sent)) {
            return sent;
        }

        // The school's name hash, or its index in the game's school list.
        var template = MagicSchools.GetMagicSchool(sent) ?? (sent < 64 ? MagicSchools.GetMagicSchool((int) sent) : null);
        if (template is not null && Enum.TryParse<MagicSchool>(template.m_schoolName, out var school) && Schools.Contains(school)) {
            return (uint) school;
        }

        return null;
    }

    /// <summary>
    /// The level table's Archmastery (0 under level 50 unless AllLevels) plus [Archmastery] BonusRating.
    /// This is what the client is told in MSG_UPDATEARCHMASTERY; gear is added by the client itself.
    /// </summary>
    internal static float BaseRating(MagicSchool school, int level) {
        if (!Enabled) {
            return 0;
        }

        if (level < LEVEL && !AllLevels) {
            return BonusRating;
        }

        var rating = MagicLevelsConfig.GetPlayerLevelInfo(school, level)?.m_archmastery ?? 0f;
        if (rating <= 0) {
            // The level table may have no number for this level; the level 50 one stands in.
            rating = MagicLevelsConfig.GetPlayerLevelInfo(school, LEVEL)?.m_archmastery ?? 0f;
            if (rating <= 0) {
                rating = 1f;
            }
        }

        return rating + BonusRating;
    }

    /// <summary>
    /// A combatant's whole Archmastery: the base (players only) plus gear, jewels, pets or creature stats, then
    /// Archmastery % on top.
    /// </summary>
    internal static float RatingOf(ServerWizGameStats stats, MagicSchool school, int level, bool isPlayer) {
        if (!Enabled || stats is null || (!isPlayer && !Creatures)) {
            return 0;
        }

        var rating = (isPlayer ? BaseRating(school, level) : 0f) + stats.m_archmasteryBonus;
        // Archmastery %: the game's table holds fractions from -1 (-100%) up, so 0.05 is +5%.
        var percent = stats.m_archmasteryBonusPercent;
        if (percent != 0) {
            rating *= Math.Max(0f, 1f + percent);
        }

        return Math.Max(0f, rating);
    }

    /// <summary>
    /// How full the orb gets this round, in school pips (1 = one school pip): the game's fastest fill times this
    /// combatant's share of the highest Archmastery in the battle, times the deck efficiency.
    /// </summary>
    internal static float FillThisRound(float rating, float highest, float efficiency) {
        if (rating <= 0) {
            return 0;
        }

        var config = WizStatisticEffectConfigLoader.Config;
        var cost = config?.m_archmasteryConversionCost ?? 0f;
        var fastest = FillOverride > 0 ? FillOverride
            : cost > 0 && config.m_archmasteryFillRateMax > 0 ? Math.Min(1f, config.m_archmasteryFillRateMax / cost)
            : DEFAULT_FILL;
        var scaler = config?.m_archmasteryFillRateScaler is > 0 ? config.m_archmasteryFillRateScaler : 1f;
        var share = highest > 0 ? Math.Clamp(rating / highest * scaler, 0f, 1f) : 1f;

        return fastest * share * Math.Clamp(efficiency, 0f, 1f);
    }

    /// <summary>
    /// The power a white pip costs, in school pips (3 in the game's data).
    /// </summary>
    internal static float PowerPipCost {
        get {
            var config = WizStatisticEffectConfigLoader.Config;
            var cost = config?.m_archmasteryConversionCost ?? 0f;

            return cost > 0 && config.m_archmasteryPowerPipCost > 0 ? config.m_archmasteryPowerPipCost / cost : DEFAULT_POWER_PIP_COST;
        }
    }

    /// <summary>
    /// "Power Orb Efficiency": always the PvE minimum (100% in the game's data) outside PvP. In PvP, how full the
    /// main deck is of cards allowed in PvP, never below the game's PvP minimum.
    /// </summary>
    internal static float Efficiency(bool pvp, DeckBehavior deck, int deckSize) {
        var config = WizStatisticEffectConfigLoader.Config;
        if (!pvp) {
            var minPvE = config?.m_archmasteryFullnessMinPvE ?? 1f;

            return minPvE > 0 ? Math.Min(1f, minPvE) : 1f;
        }

        var minPvP = Math.Clamp(config?.m_archmasteryFullnessMinPvP ?? 0f, 0f, 1f);
        if (deck?.m_spellList is null || deckSize <= 0) {
            return Math.Max(minPvP, 1f);
        }

        var pvpCards = 0;
        foreach (var data in deck.m_spellList) {
            if (data is null || CoreObjectFactory.GetCoreTemplate(data.m_templateID) is not SpellTemplate spell) {
                continue;
            }
            // Treasure cards sit in the side deck; PvE-only cards don't count.
            if (spell.m_Treasure || spell.m_PvE) {
                continue;
            }
            pvpCards += (int) data.m_quantity;
        }

        return Math.Clamp(Math.Max(minPvP, (float) pvpCards / deckSize), 0f, 1f);
    }

}
