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
using System.Text.Json.Nodes;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The overlay's Quest Tracker (Niduus, 2026-11-12: "It will show all your quest in catagory in the UI. Story, Main and
/// Side quests"). Each game window's tracker asks every couple of seconds (op "quests" on the overlay link,
/// <see cref="OverlayLinks.QuestsAsync"/>); the wizard's own session answers with every quest held now, its kind (story,
/// main or side, <see cref="QuestStory"/>), its world and its steps now with where they are. Picking a quest in the tracker
/// draws its way on the Minimap (<see cref="OverlayQuestRoute"/>). Only reads; nothing is changed.
/// </summary>
internal static class OverlayQuestTracker {

    /// <summary>[Overlay] QuestTracker: the Quest Tracker may get its quests from this server. On unless turned off; live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Overlay.QuestTracker", true);

    private const int MAX_QUESTS = 120;
    private const int MAX_STEPS = 6;

    /// <summary>Every quest the wizard holds now, story first, then main, then side; the ones here before the rest.</summary>
    internal static JsonObject Build(Wizard wizard) {
        string zonePath = wizard.Zone ?? "";
        var rows = new List<(JsonObject Row, int Order, bool Here, int Level)>();
        foreach (var held in wizard.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).ToList() ?? []) {
            try {
                if (QuestTemplateCollection.GetQuestByName(held.QuestName) is not { } quest) {
                    continue;
                }

                var kind = QuestStory.KindOf(quest);
                var steps = new JsonArray();
                var here = false;
                foreach (var progress in held.GoalProgress ?? []) {
                    if (progress is null || !held.IsGoalActive(progress.GoalName) || steps.Count >= MAX_STEPS) {
                        continue;
                    }

                    var goal = quest.m_goals?.FirstOrDefault(g => g is not null && (string) g.m_goalName == progress.GoalName);
                    if (goal is null || goal.m_hideGoal) {
                        continue;
                    }

                    var stepZone = StepZone(goal);
                    var stepHere = stepZone.Length == 0 || SameZone(stepZone, zonePath)
                                   || QuestArrowGoals.GetSpots(quest, goal).Any(s => SameZone(s.Zone, zonePath));
                    here |= stepHere;
                    steps.Add(new JsonObject {
                        ["s"] = StepTitle(goal), ["where"] = stepHere || stepZone.Length == 0 ? "" : ZoneNames.Shown(stepZone), ["here"] = stepHere,
                    });
                }

                var row = new JsonObject {
                    ["id"] = held.QuestName, ["t"] = English(quest.m_questTitle, held.QuestName), ["k"] = kind,
                    ["w"] = QuestStory.WorldOf(quest), ["l"] = (int) quest.m_questLevel, ["steps"] = steps, ["here"] = here,
                };
                var order = kind == QuestStory.STORY ? 0 : kind == QuestStory.MAIN ? 1 : 2;
                rows.Add((row, order, here, quest.m_questLevel));
            }
            catch (Exception ex) {
                Logger.Debug("Quest tracker: quest {0} could not be read: {1}", Logger.Args(held.QuestName, ex.Message));
            }
        }

        return new JsonObject {
            ["zone"] = zonePath,
            ["place"] = zonePath.Length == 0 ? "" : ZoneNames.Shown(zonePath),
            ["quests"] = new JsonArray([.. rows.OrderBy(r => r.Order).ThenByDescending(r => r.Here).ThenBy(r => r.Level)
                .Take(MAX_QUESTS).Select(r => (JsonNode) r.Row)]),
        };
    }

    internal static bool SameZone(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) {
            return false;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var contained = AccessPassManager.GetContainedZoneName(a.Trim());

        return contained is not null && string.Equals(contained, b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The zone a step is in, or empty when the quest doesn't say.</summary>
    internal static string StepZone(GoalTemplate goal) {
        string zone = goal is WaypointGoalTemplate { m_zoneTag.IsEmpty: false } waypoint ? (string) waypoint.m_zoneTag : (string) goal.m_destinationZone;
        if (string.IsNullOrWhiteSpace(zone)) {
            return "";
        }

        return AccessPassManager.GetContainedZoneName(zone.Trim()) ?? zone.Trim();
    }

    /// <summary>The step in plain words, with who or what ("Talk to Headmaster Ambrose", "Defeat 3 Rattlebones").</summary>
    internal static string StepTitle(GoalTemplate goal) {
        var text = QuestGuide.StepText(goal);

        return string.IsNullOrWhiteSpace(text) ? English(goal.m_goalTitle, (string) goal.m_goalName ?? "") : English(text, text);
    }

    internal static string English(string key, string fallback) {
        var text = string.IsNullOrEmpty(key) || key.Contains(' ') ? key : Locale.GetEnglishName(key);
        text = string.IsNullOrWhiteSpace(text) ? fallback ?? "" : text;
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return text.Length > 120 ? text[..120] : text;
    }

}
