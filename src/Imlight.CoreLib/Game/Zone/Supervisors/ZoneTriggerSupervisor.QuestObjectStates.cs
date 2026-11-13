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
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/*
 * Quest steps that change an object of a dungeon room (ResModifyTriggerObject): the Emperor's Retreat's step "Talk to
 * Shalek" (KT-SPH5-C01-001 Goal 1c) turns the KT_Lever3 lever from On (locked) to Off (ready to pull), and pulling it
 * opens the Door O Frost. A step's change lands in the copy of the room that exists when the step starts. When that
 * copy is gone by the time the wizard comes back (they logged out, the server restarted, or everyone left for a moment),
 * the new copy starts from the zone file and the lever stays locked: nothing can open the door again (Niduus,
 * 2026-11-13). So when a wizard comes into a dungeon room, the object changes of the quests they are on now come back,
 * for every object this copy has not changed itself. Only dungeon copies: the open world is shared with everyone.
 * [Instance] RestoreQuestObjects turns it off.
 */
internal sealed partial class ZoneTriggerSupervisor {

    // Every object some quest step or quest start/end sets, gathered once.
    private static HashSet<string> s_questChangedObjects;

    private bool _questObjectsChecked;
    private bool _holdsQuestObjects;

    private static bool RestoreQuestObjectsEnabled => ConfigurationManager.GetValue("Instance.RestoreQuestObjects", true);

    // Whether this zone holds any object a quest changes (so an arrival is worth looking at).
    private bool HoldsQuestChangedObjects() {
        if (!Zone.IsInstance) {
            return false;
        }

        if (!_questObjectsChecked) {
            _questObjectsChecked = true;
            _holdsQuestObjects = QuestChangedObjects().Any(HoldsObject);
        }

        return _holdsQuestObjects;
    }

    private void RestoreQuestObjectStates(WaitingSpawnWizard message) {
        if (!RestoreQuestObjectsEnabled || !HoldsQuestChangedObjects() || message.Wizard?.QuestBehavior is null) {
            return;
        }

        try {
            // Object -> (state, the quest and step that set it), the latest step last.
            var wanted = new Dictionary<string, (string State, string Quest, string Step)>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in message.Wizard.QuestBehavior.CurrentQuestInstances ?? []) {
                var template = instance is null ? null : QuestTemplateCollection.GetQuestByName(instance.QuestName);
                if (template is null) {
                    continue;
                }

                Collect(template.m_startResults, instance.QuestName, "start", wanted);
                foreach (var goal in template.m_goals ?? []) {
                    if (goal is null) {
                        continue;
                    }

                    var progress = instance.GoalProgress?.FirstOrDefault(p =>
                        string.Equals(p?.GoalName, goal.m_goalName, StringComparison.Ordinal));
                    if (progress is null || !progress.DoesPlayerHaveGoal()) {
                        continue;
                    }

                    Collect(goal.m_activateResults, instance.QuestName, goal.m_goalName, wanted);
                    if (progress.IsGoalCompleted()) {
                        Collect(goal.m_completeResults, instance.QuestName, goal.m_goalName, wanted);
                    }
                }
            }

            foreach (var (objectName, (state, quest, step)) in wanted) {
                // Only objects this copy holds and has not moved itself: a lever the wizard already pulled here stays pulled.
                if (!HoldsObject(objectName) || Zone.ScriptState.GetObjectStateIfChanged(objectName) is not null
                    || string.Equals(Zone.ScriptState.GetObjectState(objectName), state, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                Logger.Information("Zone {0}: {1} goes back to {2}, as step '{3}' of '{4}' left it (this copy of the room "
                    + "is newer than that step).", Logger.Args(Zone.ZonePath, objectName, state, step, quest));
                ApplyObjectState(objectName, state, message.PlayerActor, message.PlayerObject);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Zone {0}: putting back the objects quests changed failed: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }

    // The object changes of a result list that apply without requirements of their own.
    private static void Collect(ResultList list, string quest, string step,
        Dictionary<string, (string State, string Quest, string Step)> wanted) {
        foreach (var result in list?.m_results ?? []) {
            if (result is not ResModifyTriggerObject change || change.m_requirements?.m_requirements is { Count: > 0 }) {
                continue;
            }

            string name = change.m_triggerObjName;
            string state = change.m_triggerObjState;
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(state)) {
                wanted[name] = (state, quest, step);
            }
        }
    }

    private static HashSet<string> QuestChangedObjects() => LazyInitializer.EnsureInitialized(ref s_questChangedObjects, () => {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(ResultList list) {
            foreach (var change in list?.m_results?.OfType<ResModifyTriggerObject>() ?? []) {
                string name = change.m_triggerObjName;
                if (!string.IsNullOrEmpty(name)) {
                    names.Add(name);
                }
            }
        }

        foreach (var quest in SpiralDB.QuestTemplates ?? []) {
            if (quest is null) {
                continue;
            }

            Add(quest.m_startResults);
            foreach (var goal in quest.m_goals ?? []) {
                Add(goal?.m_activateResults);
                Add(goal?.m_completeResults);
            }
        }

        return names;
    });

}
