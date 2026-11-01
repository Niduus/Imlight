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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// NPCs and objects a quest hides only while part of it runs (QuestSafety.BringBackHiddenObjects). "Weird Science"
/// hides Watson on the Scotland Yard roof when it starts, since he waits for you in Katzenstein's Lab, and puts him back
/// when its Clockwork step is done. A quest finished another way (Fast Forward, Complete Quest, a step done by hand) or
/// taken off never ran that step, so Watson stayed hidden for good: nobody gave "Fetch Bones!" and he could not be
/// clicked (Niduus 2026-11-01). At every area change, a hide whose quest is over, or is past the step that ends it, is
/// taken off. A hide that some quest never takes back is left alone: that one is meant to last.
/// </summary>
internal static class QuestHiddenObjects {

    private const string OFF_STATE = "Off";

    // Per object tag: the quests that hide it, and for each, the steps whose completion shows it again ("end" = quest end).
    private sealed record Hider(string Quest, HashSet<string> ShowSteps);

    private static Tuple<int, IReadOnlyList<QuestTemplate>, Dictionary<string, List<Hider>>> s_known;
    private static readonly object s_lock = new();

    private const string QUEST_END = "\u0001end";

    /// <summary>
    /// Takes off the wizard's hides that no running quest step still wants. Runs before the zone sends the wizard's mods,
    /// so the objects come back by themselves. Returns the tags that were taken off.
    /// </summary>
    internal static List<string> ReleaseFinishedHides(Wizard wizard) {
        var released = new List<string>();
        if (!QuestSafety.BringBackHiddenObjects || wizard?.DynamodSet?.Dynamods is not { Count: > 0 } mods
            || wizard.QuestBehavior?.CurrentQuestInstances is null) {
            return released;
        }

        try {
            var hiders = Hiders();
            foreach (var mod in mods.Where(m => m is not null && IsHide(m.ModState)).ToList()) {
                if (string.IsNullOrEmpty(mod.ClientTag) || !hiders.TryGetValue(mod.ClientTag, out var list)) {
                    continue;
                }

                // Some quest hides it for good (never shows it again): leave it.
                if (list.Any(h => h.ShowSteps.Count == 0)) {
                    continue;
                }

                if (!list.All(h => IsPastHide(wizard, h))) {
                    continue;
                }

                if (wizard.RemoveDynamod(mod.ClientTag)) {
                    released.Add(mod.ClientTag);
                    Logger.Information("Quest safety: {0} had '{1}' hidden by {2}, which no longer needs it; it is back.",
                        Logger.Args(wizard.CharId, mod.ClientTag, string.Join(", ", list.Select(h => h.Quest))));
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not bring hidden quest objects back: {0}", Logger.Args(ex.Message));
        }

        return released;
    }

    // The quest is not running (finished, never taken or taken off), or one of the steps that shows the object is done.
    private static bool IsPastHide(Wizard wizard, Hider hider) {
        var instance = wizard.QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q is not null && string.Equals(q.QuestName, hider.Quest, StringComparison.OrdinalIgnoreCase));
        if (instance is null) {
            return true;
        }

        return hider.ShowSteps.Any(step => step != QUEST_END && instance.IsGoalCompleted(step));
    }

    private static bool IsHide(string state)
        => string.IsNullOrEmpty(state) || string.Equals(state, OFF_STATE, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, List<Hider>> Hiders() {
        var quests = SpiralDB.QuestTemplates;
        var version = QuestOptions.Version;
        var known = s_known;
        if (known is not null && known.Item1 == version && ReferenceEquals(known.Item2, quests)) {
            return known.Item3;
        }

        lock (s_lock) {
            known = s_known;
            if (known is not null && known.Item1 == version && ReferenceEquals(known.Item2, quests)) {
                return known.Item3;
            }

            var map = new Dictionary<string, List<Hider>>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in quests) {
                if (quest?.m_questName is null) {
                    continue;
                }

                var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var shownBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                Read(quest.m_startResults, null, hidden, shownBy);
                Read(quest.m_endResults, QUEST_END, hidden, shownBy);
                foreach (var goal in quest.m_goals ?? []) {
                    if (goal is null) {
                        continue;
                    }

                    Read(goal.m_activateResults, null, hidden, shownBy);
                    Read(goal.m_completeResults, goal.m_goalName, hidden, shownBy);
                }

                foreach (var tag in hidden) {
                    if (!map.TryGetValue(tag, out var list)) {
                        list = [];
                        map[tag] = list;
                    }

                    list.Add(new Hider(quest.m_questName, shownBy.TryGetValue(tag, out var steps) ? steps : new HashSet<string>()));
                }
            }

            s_known = Tuple.Create(version, quests, map);

            return map;
        }
    }

    // Hides anywhere in the quest; shows only where a step's completion (or the quest's end) can be pointed at.
    private static void Read(ResultList results, string step, HashSet<string> hidden, Dictionary<string, HashSet<string>> shownBy) {
        foreach (var result in results?.m_results ?? []) {
            switch (result) {
                case ResAddDynaMod add when !string.IsNullOrEmpty(add.m_dynaModClientTag):
                    if (!add.m_bShowsObject && IsHide(add.m_dynaModState.ToString())) {
                        hidden.Add(add.m_dynaModClientTag);
                    }
                    else if (step is not null) {
                        Shown(shownBy, add.m_dynaModClientTag, step);
                    }

                    break;
                case ResRemoveDynaMod remove when step is not null && !string.IsNullOrEmpty(remove.m_dynaModClientTag):
                    Shown(shownBy, remove.m_dynaModClientTag, step);
                    break;
                case ResIfThenElse branch:
                    Read(branch.m_thenResults, step, hidden, shownBy);
                    Read(branch.m_elseResults, step, hidden, shownBy);
                    break;
            }
        }
    }

    private static void Shown(Dictionary<string, HashSet<string>> shownBy, string tag, string step) {
        if (!shownBy.TryGetValue(tag, out var steps)) {
            steps = new HashSet<string>(StringComparer.Ordinal);
            shownBy[tag] = steps;
        }

        steps.Add(step);
    }

}
