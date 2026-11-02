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

    // Name: the speaker's name written out, for a creature with no persona (a boss's lines); QuestTextKeys keys it.
    private sealed record Line(string Persona, string Text, string Sound, string Picture, uint Actor, string Name = null);

    // A dynamod the game's quest start adds or removes (an NPC coming out or leaving, a sigil glowing) that the capture
    // lacked. Type "add" or "remove"; the rest as the game's ResAddDynaMod has them.
    private sealed record Mod(string Type, string Tag, string Zone, string State, bool Shows, bool Remove, bool Persist);

    private sealed record Entry(string Quest, string Goal, string Tag, bool Replace, List<Line> Lines, int Count,
        List<Mod> Start = null);

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

            int lines = 0, counts = 0, mods = 0;
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

                    if (entry.Start is { Count: > 0 }) {
                        mods += AddStartMods(quest, entry.Start);
                    }
                } catch (Exception ex) {
                    Logger.Warning("Quest fixes: '{0}' could not be fixed: {1}", Logger.Args(entry.Quest, ex.Message));
                }
            }

            Logger.Information("Quest fixes: {0} dialog lines, {1} defeat counts and {2} quest-start dynamods set to the game's own.",
                Logger.Args(lines, counts, mods));
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
        // "replace" rebuilds the dialog in the listed order. A line the capture already had (same sound) is moved, not
        // rebuilt, so its camera, animations and walk-away NPC stay; its speaker and picture take the listed ones.
        var kept = new List<NPCDialogEntry>();
        if (entry.Replace) {
            kept.AddRange(dialog.m_dialogEntries.OfType<NPCDialogEntry>());
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

            var old = string.IsNullOrEmpty(line.Sound) ? null : kept.FirstOrDefault(e =>
                string.Equals(SoundName(e.m_soundFile), SoundName(line.Sound), StringComparison.OrdinalIgnoreCase));
            if (old is not null) {
                if (!string.IsNullOrEmpty(line.Persona)) {
                    old.m_personaName = line.Persona;
                }

                if (!string.IsNullOrEmpty(line.Picture)) {
                    old.m_picture = line.Picture;
                }

                if (line.Name is not null) {
                    old.m_nameOverride = line.Name;
                }

                dialog.m_dialogEntries.Add(old);
                added++;
                continue;
            }

            dialog.m_dialogEntries.Add(new NPCDialogEntry {
                m_personaName = line.Persona ?? "",
                m_nameOverride = line.Name ?? "",
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

    private static int AddStartMods(QuestTemplate quest, List<Mod> mods) {
        quest.m_startResults ??= new ResultList();
        quest.m_startResults.m_results ??= [];
        var results = quest.m_startResults.m_results;
        var added = 0;
        foreach (var mod in mods.Where(m => !string.IsNullOrEmpty(m?.Tag))) {
            var remove = string.Equals(mod.Type, "remove", StringComparison.OrdinalIgnoreCase);
            // Already there (a reload, or the capture had it): leave it.
            if (remove ? results.OfType<ResRemoveDynaMod>().Any(r => (string) r.m_dynaModClientTag == mod.Tag)
                    : results.OfType<ResAddDynaMod>().Any(r => (string) r.m_dynaModClientTag == mod.Tag)) {
                continue;
            }

            if (remove) {
                results.Add(new ResRemoveDynaMod { m_dynaModClientTag = mod.Tag });
            } else {
                results.Add(new ResAddDynaMod {
                    m_dynaModClientTag = mod.Tag,
                    m_dynaModState = mod.State ?? "",
                    m_zoneName = mod.Zone ?? "",
                    m_bShowsObject = mod.Shows,
                    m_dynaModRemove = mod.Remove,
                    m_persist = mod.Persist,
                });
            }

            added++;
        }

        return added;
    }

    // "|Sound_Dialogue_002|WorldData|Sound/Dialogue/Gamma_002_09.mp3" -> "Gamma_002_09.mp3".
    private static string SoundName(string sound) {
        sound ??= "";
        var slash = sound.LastIndexOfAny(['/', '|']);
        return slash < 0 ? sound : sound[(slash + 1)..];
    }

}
