/*
 * Repairs to quests captured from the live game, checked against the real game's own quest files (Wizard404).
 * spiraldb-extra/QuestFixes.json lists them; at boot each one is applied to its quest (Niduus 2026-10-31, "double
 * check main quests"). Two kinds:
 *  - lines: dialog the capture missed or got wrong. The giver's reminder when you talk to them mid-quest
 *    ("Underway"), what an NPC says when you walk in somewhere or use something (a step's "Completion"), or a quest
 *    offer that holds the quest before's lines ("replace": true swaps the whole dialog for these lines).
 *  - count: how many creatures a defeat step needs, where the game asks for a different number.
 * Lines are written in English; QuestTextKeys gives them the game's own keys right after.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

public static class QuestFixes {

    internal const string FILE_NAME = "QuestFixes.json";

    private sealed record Line(string Persona, string Text, string Sound, string Picture, uint Actor);

    private sealed record Entry(string Quest, string Goal, string Tag, bool Replace, List<Line> Lines, int Count);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Applies the listed repairs. Runs once after SpiralDB loads, before quest text gets its keys.</summary>
    public static void ApplyAll(IReadOnlyList<QuestTemplate> quests, string extraPath) {
        try {
            var path = Path.Combine(extraPath ?? "", FILE_NAME);
            if (!File.Exists(path)) {
                return;
            }

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), s_options) ?? [];
            var byName = quests.Where(q => q?.m_questName is not null)
                .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int lines = 0, counts = 0;
            foreach (var entry in entries) {
                if (entry?.Quest is null || !byName.TryGetValue(entry.Quest, out var quest)) {
                    continue;
                }

                try {
                    GoalTemplate goal = null;
                    if (!string.IsNullOrEmpty(entry.Goal)) {
                        goal = quest.m_goals?.FirstOrDefault(g => g is not null
                            && string.Equals(g.m_goalName, entry.Goal, StringComparison.OrdinalIgnoreCase));
                        if (goal is null) {
                            Logger.Warning("Quest fixes: quest '{0}' has no step '{1}'.", Logger.Args(entry.Quest, entry.Goal));
                            continue;
                        }
                    }

                    if (entry.Count > 0 && goal is BountyGoalTemplate bounty) {
                        bounty.m_bountyTotal = entry.Count;
                        if (bounty.m_tallyCounter is { } tally) {
                            tally.m_count = entry.Count;
                        }

                        counts++;
                    }

                    if (entry.Lines is { Count: > 0 } && !string.IsNullOrEmpty(entry.Tag)) {
                        lines += AddLines(quest, goal, entry);
                    }
                } catch (Exception ex) {
                    Logger.Warning("Quest fixes: '{0}' could not be fixed: {1}", Logger.Args(entry.Quest, ex.Message));
                }
            }

            Logger.Information("Quest fixes: {0} dialog lines and {1} defeat counts set to the game's own.",
                Logger.Args(lines, counts));
        } catch (Exception ex) {
            Logger.Warning("Quest fixes could not be applied: {0}", Logger.Args(ex.Message));
        }
    }

    private static int AddLines(QuestTemplate quest, GoalTemplate goal, Entry entry) {
        ActorDialogList list;
        if (goal is null) {
            list = quest.m_dialogList as ActorDialogList;
            if (list is null) {
                quest.m_dialogList = list = new ActorDialogList();
            }
        } else {
            list = goal.m_dialogList as ActorDialogList;
            if (list is null) {
                goal.m_dialogList = list = new ActorDialogList();
            }
        }

        list.m_dialogs ??= [];
        var dialog = list.m_dialogs.FirstOrDefault(d => d is not null && d.m_dialogTag == entry.Tag);
        if (dialog is null) {
            dialog = new ActorDialog { m_dialogTag = entry.Tag };
            list.m_dialogs.Add(dialog);
        }

        dialog.m_dialogEntries ??= [];
        if (entry.Replace) {
            dialog.m_dialogEntries.Clear();
        }

        var added = 0;
        foreach (var line in entry.Lines.Where(l => !string.IsNullOrWhiteSpace(l?.Text))) {
            // Already there (a reload, or a capture that has it after all): leave it.
            if (dialog.m_dialogEntries.OfType<NPCDialogEntry>().Any(e =>
                    (!string.IsNullOrEmpty(line.Sound) && string.Equals(e.m_soundFile, line.Sound, StringComparison.OrdinalIgnoreCase))
                    || e.m_dialog == line.Text)) {
                continue;
            }

            dialog.m_dialogEntries.Add(new NPCDialogEntry {
                m_personaName = line.Persona ?? "",
                m_nameOverride = "",
                m_guiDisplay = "",
                m_action = "",
                m_dialogEvent = "",
                m_cameraName = "",
                m_dialog = line.Text,
                m_soundFile = line.Sound ?? "",
                m_picture = line.Picture ?? "",
                m_actorTemplateID = line.Actor,
            });
            added++;
        }

        return added;
    }

}
