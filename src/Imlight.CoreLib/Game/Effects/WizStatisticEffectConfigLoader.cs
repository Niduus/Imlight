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
 * WIZ STATISTIC EFFECT CONFIGURATION
 * ========================================================================
 * 
 * PURPOSE:
 * Loads the client's WizStatisticEffectConfig.xml from the Root.wad, which
 * ships the level-scaled crit and block scalars (CritAndBlockLevelData),
 * the crit/block level thresholds, and the crit damage add percents.
 * 
 * USAGE EXAMPLE:
 * float divisor = WizStatisticEffectConfigLoader.GetCritDivisor(level);
 * 
 * NOTE:
 * Discovered and constructed at boot by ResourceContainer, like the other
 * RootSingleResourceSingleton subclasses. The level-scaled crit divisor
 * (m_baseCritDivisor + level * m_critScalarDivisor) is the K term of the
 * crit chance formula. The per-tier caps (1.0 down to 0.03) and the scalar
 * pairs await client RE.
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/14/2026
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Effects;

internal class WizStatisticEffectConfigLoader : RootSingleResourceSingleton<WizStatisticEffectConfigLoader>, IMemoryStreamDisposable {

    protected override string ResourceName => "WizStatisticEffectConfig.xml";

    private static WizStatisticEffectConfig s_config;

    protected override void AfterLoad() {
        var serializer = new BindSerializer();
        if (!serializer.Deserialize<WizStatisticEffectConfig>(base.Stream.ToArray(), 1, out var config)) {
            Logger.Error("Failed to load WizStatisticEffectConfig.xml");

            return;
        }

        s_config = config;

        Logger.Information("Loaded {0} crit and block level ranges",
            Logger.Args(config.m_critAndBlockLevelData?.Count ?? 0));
        Logger.Information("Archmastery numbers: fill rate scaler {0}, fill rate max {1}, fullness min {2}, school pip cost {3}, power pip cost {4}.",
            Logger.Args(config.m_archmasteryFillRateScaler, config.m_archmasteryFillRateMax, config.m_archmasteryFullnessMinPvE,
                        config.m_archmasteryConversionCost, config.m_archmasteryPowerPipCost));

        DisposeStream();
    }

    /// <summary>
    /// The game's statistic numbers, or null when the file could not be read.
    /// </summary>
    internal static WizStatisticEffectConfig Config => s_config;

    /// <summary>
    /// The level-scaled crit divisor (m_baseCritDivisor + level * m_critScalarDivisor;
    /// 100 + 3 * level in the shipped data). This is the K term of the crit chance
    /// formula. Returns 0 when the config is missing.
    /// </summary>
    internal static float GetCritDivisor(int level)
        => s_config is null ? 0f : s_config.m_baseCritDivisor + level * s_config.m_critScalarDivisor;

    /// <summary>
    /// The level at which critical hits activate; below it the chance is 0.
    /// </summary>
    internal static int GetCriticalHitLevelThreshold() 
        => s_config?.m_criticalHitLevelThreshold ?? 0;

    /// <summary>
    /// Calculates the chance to retain the unused half of a mastered-school power pip,
    /// using the level and spell-rank values shipped in WizStatisticEffectConfig.
    /// </summary>
    internal static float GetPipConversionChance(int level, int spellRank, float rating) {
        var config = s_config;
        if (config is null || level < config.m_pipConversionLevelThreshold || spellRank is < 1 or > 7 || rating <= 0) {
            return 0f;
        }

        var levelData = config.m_pipConversionLevelData?
            .Where(data => data.m_minLevel <= level)
            .OrderByDescending(data => data.m_minLevel)
            .FirstOrDefault();
        var rankData = levelData?.m_levelValues?.ElementAtOrDefault(spellRank - 1);
        if (rankData is null) {
            return 0f;
        }

        return CalculatePipConversionChance(rating, rankData.m_capValue, rankData.m_scalarBase, rankData.m_scalingFactor);
    }

    internal static float CalculatePipConversionChance(float rating, float cap, float scalarBase, float scalingFactor)
        => Math.Clamp(Math.Min(cap, scalarBase + rating * scalingFactor), 0f, 1f);

    public void DisposeStream()
        => base.Stream.Dispose();

}
