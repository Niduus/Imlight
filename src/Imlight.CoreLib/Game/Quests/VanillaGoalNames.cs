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
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// The game names a quest's steps "Goal", "Goal 1", "Goal 2" ... in the order the quest lists them, and its zone
/// triggers and objects ask for those names: Arcanum's Sand Sea spawns the Renegade Djinni while AR-PreMR-MAIN-002
/// "Goal 6" is under way, the Brain's lever in Empyrea works while EM-SEPI-MAIN-007 "Goal 3" is. The quests we rebuilt
/// from Wizard404 (Celestia onward) call their steps "7_WizardQuestGoalsKill" and so on, so those triggers and objects
/// never saw their step and the creature never came or the lever never worked (Arc 3 main quest check, 2026-10-31).
/// A few quests name steps by words instead (WC-MidEM-MAIN-002 "Goal_Use_SpringWater"); those are listed below.
/// Only quests that have no step of the asked name use this, so quests with the game's own names are untouched.
/// </summary>
internal static class VanillaGoalNames {

    private static readonly Regex s_numbered = new(@"^Goal(?: (\d+))?$", RegexOptions.Compiled);

    // Steps the game names by words: quest -> (game's name -> place of the step in the quest, from 0).
    private static readonly Dictionary<string, Dictionary<string, int>> s_named = new(StringComparer.OrdinalIgnoreCase) {
        ["WC-MidEM-MAIN-002"] = new(StringComparer.OrdinalIgnoreCase) {
            ["Goal_Defeat_RotSpores_02"] = 7,
            ["Goal_Use_RottedRoots_01"] = 6,
            ["Goal_Use_DeadRoots_02"] = 8,
            ["Goal_Use_SpringWater"] = 12,
            ["Goal_Talkto_MoolindaWu"] = 13,
        },
        ["AR-MidEM-MAIN-001"] = new(StringComparer.OrdinalIgnoreCase) {
            ["Goal_Explore_Bartleby"] = 3,
        },
    };

    /// <summary>
    /// Our name for the step the game calls <paramref name="gameName"/> in <paramref name="questName"/>, or null when the
    /// quest has a step of that very name (or none it could mean).
    /// </summary>
    internal static string OurName(string questName, string gameName) {
        if (string.IsNullOrEmpty(questName) || string.IsNullOrEmpty(gameName)) {
            return null;
        }

        var goals = QuestTemplateCollection.GetQuestByName(questName)?.m_goals;
        if (goals is null || goals.Count == 0 || goals.Any(g => (string) g?.m_goalName == gameName)) {
            return null;
        }

        int index;
        if (s_named.TryGetValue(questName, out var named) && named.TryGetValue(gameName, out var place)) {
            index = place;
        }
        else if (s_numbered.Match(gameName) is { Success: true } match) {
            index = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
        }
        else {
            return null;
        }

        return index >= 0 && index < goals.Count ? (string) goals[index]?.m_goalName : null;
    }

    /// <summary>The names the game gives our step <paramref name="ourName"/> of <paramref name="quest"/> (none for
    /// quests whose steps already carry the game's names).</summary>
    internal static IEnumerable<string> GameNames(QuestTemplate quest, string ourName) {
        var goals = quest?.m_goals;
        if (goals is null || string.IsNullOrEmpty(ourName)) {
            yield break;
        }

        var index = goals.FindIndex(g => (string) g?.m_goalName == ourName);
        if (index < 0) {
            yield break;
        }

        var numbered = index == 0 ? "Goal" : $"Goal {index}";
        if (!goals.Any(g => (string) g?.m_goalName == numbered)) {
            yield return numbered;
        }

        if (s_named.TryGetValue((string) quest.m_questName ?? "", out var named)) {
            foreach (var (name, place) in named) {
                if (place == index && !goals.Any(g => (string) g?.m_goalName == name)) {
                    yield return name;
                }
            }
        }
    }

}
