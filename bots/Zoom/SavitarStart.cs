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
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Savitar, the second quest scout (Niduus, 2026-11-22: "This bot will quest ahead of Ultimus to do exactly what zoom does
/// until Zoom catches up. Savitar is to be fire"). He plays exactly as Zoom does (bots/Zoom); what is his own is here:
/// - He starts where Ultimus is: the first time he is made, Ultimus's finished quests (the quest registry) and the quests
///   Ultimus holds, with each step as far as Ultimus has it, are copied to him (logs/savitar/started-from-ultimus.txt says
///   when and what). From there he scouts what Ultimus meets next.
/// - When Zoom holds or has finished the main quest Savitar is on, Zoom has caught up: Savitar stops where he is for good
///   (logs/savitar/handoff.txt). Deleting that file starts him scouting again at the next bots start.
/// </summary>
internal static class SavitarStart {

    internal const string STARTED_FILE = "started-from-ultimus.txt";
    internal const string HANDOFF_FILE = "handoff.txt";

    private static string Folder => UltimusSeat.Savitar.Folder;

    /// <summary>Copies Ultimus's story to Savitar's wizard, once (he must be logged out).</summary>
    internal static void FromUltimus(ulong savitarChar) {
        var marker = Path.Combine(Folder, STARTED_FILE);
        if (File.Exists(marker)) {
            return;
        }

        try {
            var ultimusChar = UltimusRoster.CharOf(UltimusSeat.Ultimus);
            if (ultimusChar == 0 || WizardCollection.GetCharacter(ultimusChar) is not { QuestBehavior: { } from }) {
                Logger.Warning("Savitar: Ultimus's wizard was not found, so he starts from the beginning of the story for now (tried again next start).");
                return;
            }

            if (WizardCollection.GetCharacter(savitarChar) is not { QuestBehavior: { } to } savitar) {
                Logger.Warning("Savitar: his wizard {0} was not found to start where Ultimus is.", Logger.Args(savitarChar));
                return;
            }

            // What Ultimus has finished (and every other quest flag of his story).
            foreach (var (key, value) in from.Registry) {
                to.Registry[key] = value;
            }

            // The quests he had: gone, Ultimus's come in their place.
            foreach (var name in to.CurrentQuestInstances.Where(q => q is not null).Select(q => q.QuestName).ToList()) {
                savitar.RemoveQuest(name);
            }

            var copied = 0;
            var mains = new System.Collections.Generic.List<string>();
            foreach (var quest in from.CurrentQuestInstances.Where(q => q is not null)) {
                if (QuestTemplateCollection.GetQuestByName(quest.QuestName) is not { } template) {
                    continue;
                }

                var mine = new QuestInstance(template, savitarChar);
                foreach (var goal in quest.GoalProgress ?? []) {
                    if (goal is null || string.IsNullOrEmpty(goal.GoalName)) {
                        continue;
                    }

                    if (goal.IsGoalCompleted()) {
                        mine.StartGoal(goal.GoalName);
                        mine.CompleteGoal(goal.GoalName);
                    }
                    else if (goal.DoesPlayerHaveGoal()) {
                        mine.StartGoal(goal.GoalName);
                        for (var i = 0; i < Math.Min(goal.CurrentProgress, 1000); i++) {
                            mine.IncrementGoal(goal.GoalName);
                        }
                    }
                }

                if (savitar.AddQuest(mine)) {
                    copied++;
                    if (quest.QuestName.Contains("-MAIN-", StringComparison.OrdinalIgnoreCase)) {
                        mains.Add(QuestGuide.QuestTitle(template));
                    }
                }
            }

            WizardCollection.UpdateCharacterQuestBehavior(savitar);
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(marker, [
                $"Started from Ultimus at {DateTime.Now:yyyy-MM-dd HH:mm}: {from.Registry.Count} quest registry entries and {copied} quest(s) he held.",
                $"Main quest(s): {(mains.Count > 0 ? string.Join(", ", mains) : "none")}",
                "Delete this file to copy Ultimus's story to Savitar again at the next bots start.",
            ]);
            Logger.Information("Savitar: starts where Ultimus is: {0} finished-quest entries and {1} quest(s) copied (main: {2}).",
                Logger.Args(from.Registry.Count, copied, mains.Count > 0 ? string.Join(", ", mains) : "none"));
        }
        catch (Exception e) {
            Logger.Warning("Savitar: could not start him where Ultimus is: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// Why Savitar is handed off (Zoom holds or finished his main quest, or his handoff.txt is there), else null. Writes
    /// handoff.txt the first time.
    /// </summary>
    internal static string CaughtUp(string savitarMain) {
        var file = Path.Combine(Folder, HANDOFF_FILE);
        if (File.Exists(file)) {
            return "handed off before (" + HANDOFF_FILE + ")";
        }

        if (string.IsNullOrEmpty(savitarMain)) {
            return null;
        }

        try {
            var zoomChar = UltimusRoster.CharOf(UltimusSeat.Zoom);
            if (zoomChar == 0 || WizardCollection.GetCharacter(zoomChar) is not { QuestBehavior: { } zoom }) {
                return null;
            }

            var title = QuestTemplateCollection.GetQuestByName(savitarMain) is { } t ? QuestGuide.QuestTitle(t) : savitarMain;
            var why = zoom.HasCompletedQuest(savitarMain) ? $"Zoom has finished {title}, the main quest Savitar is on"
                : zoom.HasQuest(savitarMain) ? $"Zoom is on {title} too"
                : null;
            if (why is null) {
                return null;
            }

            Directory.CreateDirectory(Folder);
            File.WriteAllLines(file, [
                $"Handed off at {DateTime.Now:yyyy-MM-dd HH:mm}: {why}.",
                "Savitar stands where he is from now on. Delete this file to have him scout again (next bots start).",
            ]);
            Logger.Information("Savitar: handed off ({0}); he stops scouting.", Logger.Args(why));
            return why;
        }
        catch (Exception e) {
            Logger.Debug("Savitar: could not check how far Zoom is: {0}", Logger.Args(e.Message));
            return null;
        }
    }

}
