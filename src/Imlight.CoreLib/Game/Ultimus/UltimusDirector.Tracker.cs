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
using System.Text.Json.Nodes;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// His quest tracker in detail (Niduus, 2026-11-14: "Make sure his quest tracker is in detail"), for the Spectator's quest
/// box and Ahziael's Ultimus page: the quest and its world and area, every open step in the game's own words with real
/// names and counts, what he is doing for it right now and why, the step after, the steps he parked and why, a "needs help"
/// flag, what the read-ahead found for this quest, and which creatures counted for the step so far.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // Why he picked his move now (the mission check's "because"), shown in the tracker.
    private string _whyNow = "";

    // Parked steps: what and where, for the tracker (the time is in _parkedAt).
    private readonly Dictionary<string, (string Quest, string What, string Where)> _parkedWhy = [];

    // The read-ahead's finding per quest (UltimusDirector.ReadAhead.cs).
    private readonly Dictionary<string, string> _readAheadFound = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tracker's details of the quest he is on (empty fields when there is none).</summary>
    private JsonObject TrackerDetail(UltimusMember.Status leader) {
        var detail = new JsonObject();
        var questName = _currentQuest ?? _target?.Quest;
        var wizard = leader?.Wizard;
        detail["world"] = string.IsNullOrEmpty(_storyWorld) ? "" : WorldName(_storyWorld);
        detail["area"] = UltimusText.Zone(leader?.Zone ?? "");
        detail["target"] = _target?.What ?? "";
        detail["why"] = _whyNow;

        var steps = new JsonArray();
        var next = "";
        if (!string.IsNullOrEmpty(questName) && QuestTemplateCollection.GetQuestByName(questName) is { } template && wizard is not null) {
            detail["questWorld"] = WorldName(QuestStory.WorldOf(template));
            detail["questLevel"] = (int) template.m_questLevel;
            var held = wizard.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
                && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));
            var goals = (template.m_goals ?? []).Where(g => g is not null).ToList();
            var lastActive = -1;
            for (var i = 0; i < goals.Count; i++) {
                var goal = goals[i];
                string name = goal.m_goalName;
                if (held is null || !held.IsGoalActive(name) || goal.m_hideGoal) {
                    continue;
                }

                lastActive = i;
                var progress = held.GoalProgress?.FirstOrDefault(p => p is not null && p.GoalName == name);
                var zone = OverlayQuestTracker.StepZone(goal);
                var row = new JsonObject {
                    ["s"] = OverlayQuestTracker.GameStepText(goal, progress),
                    ["where"] = string.IsNullOrEmpty(zone) ? "" : UltimusText.Zone(zone),
                    ["now"] = _target is not null && _target.Goal == name && string.Equals(_target.Quest, questName, StringComparison.OrdinalIgnoreCase),
                };
                if (UltimusDrops.Describe(questName, name) is { Length: > 0 } drops) {
                    row["drops"] = drops;
                }

                steps.Add(row);
            }

            // The step after the open ones, in the quest's own order (what comes next once these are done).
            if (lastActive >= 0 && goals.Skip(lastActive + 1).FirstOrDefault(g => !g.m_hideGoal && (held is null || !held.IsGoalCompleted(g.m_goalName))) is { } after) {
                next = OverlayQuestTracker.GameStepText(after);
            }

            if (_readAheadFound.TryGetValue(questName, out var found)) {
                detail["readAhead"] = found;
            }
        }

        detail["steps"] = steps;
        detail["next"] = next;

        var parked = new JsonArray();
        // The same lines with their step key, for Ahziael's "Retry now" button by each (UltimusDirector.Break.cs).
        var parkedRows = new JsonArray();
        foreach (var (key, at) in _parkedAt.Where(p => DateTime.UtcNow - p.Value < ParkedFor(p.Key)).OrderByDescending(p => p.Value)) {
            var (quest, what, where) = _parkedWhy.GetValueOrDefault(key);
            var minutes = Math.Max(1, (int) Math.Ceiling((ParkedFor(key) - (DateTime.UtcNow - at)).TotalMinutes));
            parkedRows.Add(new JsonObject { ["key"] = key, ["quest"] = quest ?? "", ["what"] = what ?? key, ["where"] = where ?? "", ["minutes"] = minutes,
                ["fix"] = _parkedFix.GetValueOrDefault(key) ?? "", ["tips"] = new JsonArray([.. TipsFor(key).Select(t => (JsonNode) t)]) });
            parked.Add($"{(string.IsNullOrEmpty(quest) ? "" : UltimusText.Title(quest) + ": ")}\"{what ?? key}\"" +
                       $"{(string.IsNullOrEmpty(where) ? "" : " in " + where)}: every fix tried twice; back to it in {minutes} min" +
                       (_parkedFix.TryGetValue(key, out var fix) && fix.Length > 0 ? $". Suggested fix: {fix}" : ""));
        }

        detail["parked"] = parked;
        detail["parkedRows"] = parkedRows;
        detail["needsHelp"] = parked.Count > 0;

        return detail;
    }

}
