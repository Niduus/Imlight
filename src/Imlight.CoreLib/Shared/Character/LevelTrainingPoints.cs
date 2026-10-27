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
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Shared.Character;

/// <summary>
/// Training points a wizard gets for leveling up. Read live from Imlight.ini on every level up:
/// [Trainers] NaturalTrainingPoints = the game's own (Root.wad MagicXPConfig, 1 point at levels 4, 8, 12, 16 and 20,
/// then every 5 levels), TrainingPointRules = extra points on top (see <see cref="TrainingPointRules"/>), and the
/// older TrainingPointPerLevel + TrainingPointsPerLevel pair, which still counts as one more rule for every level.
/// Turning NaturalTrainingPoints off with rules in place makes the rules replace the game's gain.
/// </summary>
internal static class LevelTrainingPoints {

    private static bool s_natural => ConfigurationManager.GetValue("Trainers.NaturalTrainingPoints", true);

    private static readonly LiveValue<List<TrainingPointRules.Rule>> s_rules = new(() => {
        var rules = TrainingPointRules.Parse(ConfigurationManager.GetValue("Trainers.TrainingPointRules", ""));
        if (ConfigurationManager.GetValue("Trainers.TrainingPointPerLevel", false)) {
            var each = Math.Clamp(ConfigurationManager.GetValue("Trainers.TrainingPointsPerLevel", 1), 0, 100);
            if (each > 0) {
                rules.Add(new TrainingPointRules.Rule(each, 0));
            }
        }

        return rules;
    });

    /// <summary>The game's own training points for reaching <paramref name="level"/>.</summary>
    internal static int Natural(MagicSchool school, int level) {
        try {
            return Math.Max(0, MagicLevelsConfig.GetPlayerLevelInfo(school, level)?.m_trainingPoints ?? 0);
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>All training points for going from <paramref name="fromLevel"/> up to <paramref name="toLevel"/>.</summary>
    internal static int ForLevels(MagicSchool school, int fromLevel, int toLevel) {
        var natural = s_natural;
        var rules = s_rules.Value;
        var points = 0;
        for (var level = Math.Max(0, fromLevel) + 1; level <= toLevel; level++) {
            if (natural) {
                points += Natural(school, level);
            }

            points += TrainingPointRules.For(rules, level);
        }

        return points;
    }

}
