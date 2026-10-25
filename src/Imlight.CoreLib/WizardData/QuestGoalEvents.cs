/*
 * Many quest steps rely on the real game's server sending a zone a signal when the step starts or ends: "spawn the
 * Jade Oni" when you reach the obelisk in Rematch, open a door, start a fight. The zone scripts in the game files wait
 * for those signals, but the quests we have (captured from the live game, or rebuilt from guides) do not carry them,
 * so the boss never appears. spiraldb-extra/QuestGoalEvents.json lists, per quest step, the zone events to send; at
 * boot each one is added to that step's start or completion results (Niduus 2026-10-25).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

public static class QuestGoalEvents {

    internal const string FILE_NAME = "QuestGoalEvents.json";

    private sealed record Entry(string Quest, string Goal, string When, List<string> Events);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Adds the listed zone events to their quest steps. Runs once after SpiralDB loads.</summary>
    public static void ApplyAll(IReadOnlyList<QuestTemplate> quests, string extraPath) {
        try {
            var path = Path.Combine(extraPath, FILE_NAME);
            if (!File.Exists(path)) {
                return;
            }

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), s_options) ?? [];
            var byName = quests.Where(q => q?.m_questName is not null)
                .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var entry in entries) {
                if (entry?.Quest is null || entry.Events is not { Count: > 0 } || !byName.TryGetValue(entry.Quest, out var quest)) {
                    continue;
                }

                var goal = quest.m_goals?.FirstOrDefault(g => string.Equals(g.m_goalName, entry.Goal, StringComparison.OrdinalIgnoreCase));
                if (goal is null) {
                    Logger.Warning("Quest step events: quest '{0}' has no step '{1}'.", Logger.Args(entry.Quest, entry.Goal));
                    continue;
                }

                var onStart = string.Equals(entry.When, "start", StringComparison.OrdinalIgnoreCase);
                var results = onStart ? goal.m_activateResults ??= new ResultList() : goal.m_completeResults ??= new ResultList();
                results.m_results ??= [];
                foreach (var name in entry.Events.Where(e => !string.IsNullOrWhiteSpace(e))) {
                    if (results.m_results.OfType<ResPostEvent>().Any(r => r.m_eventName == name)) {
                        continue;
                    }

                    results.m_results.Add(new ResPostEvent { m_eventName = name });
                    added++;
                }
            }

            Logger.Information("Quest step events: added {0} zone events to quest steps.", Logger.Args(added));
        } catch (Exception ex) {
            Logger.Warning("Quest step events could not be added: {0}", Logger.Args(ex.Message));
        }
    }

}
