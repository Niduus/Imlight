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
/// Story flags a quest sets when it starts or ends ("AccessToPolaris", "PL-OpenTheaterBackDoor",
/// "EmpyreaTeleportGate-InnerAthanor"): doors, gates and teleporters of the game ask for them. Several Polaris, Mirage
/// and Empyrea quests were missing them until 2026-10-31 (Arc 3 main quest check), so a wizard who had already
/// started or finished one of those quests never got the flag. At login every wizard gets the flags of the quests they
/// started or finished and lacks, once. Only the flags listed here: other flags may have been taken away again on
/// purpose by a zone (Ravenwood's "GraduationTime" is), and those must not come back.
/// </summary>
internal static class QuestFlagCatchUp {

    private static readonly HashSet<string> s_caughtUp = new(StringComparer.OrdinalIgnoreCase) {
        "AccessToPolaris", "AccessToMirage", "AccessToArcanum", "PL-OpenTheaterBackDoor",
        "EmpyreaTeleportGate-InnerAthanor", "EmpyreaTeleportGate-OuterAthanor",
    };

    private static readonly object s_lock = new();
    private static IReadOnlyList<QuestTemplate> s_quests;
    private static int s_version = -1;
    private static List<(string Quest, string Entry, ulong Value)> s_flags = [];

    internal static void Apply(Wizard wizard) {
        if (wizard?.QuestBehavior is null) {
            return;
        }

        try {
            var started = wizard.QuestBehavior.CurrentQuestInstances?.Select(q => q?.QuestName).Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var given = new List<string>();
            foreach (var (quest, entry, value) in Flags()) {
                if (wizard.HasRegistryValue(entry) || (!started.Contains(quest) && !wizard.HasCompletedQuest(quest))) {
                    continue;
                }

                wizard.QuestBehavior.SetRegistryValue(entry, value);
                given.Add(entry);
            }

            if (given.Count > 0) {
                WizardData.Collections.WizardCollection.UpdateCharacterQuestBehavior(wizard);
                Logger.Information("Quest flags caught up for {0}: {1}.", Logger.Args(wizard.CharId, string.Join(", ", given)));
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest flags could not be caught up: {0}", Logger.Args(ex.Message));
        }
    }

    // Each quest's plain (not quest-registry) flags from its start and end results, without requirements of their own.
    private static List<(string Quest, string Entry, ulong Value)> Flags() {
        var quests = SpiralDB.QuestTemplates;
        var version = QuestOptions.Version;
        lock (s_lock) {
            if (ReferenceEquals(quests, s_quests) && version == s_version) {
                return s_flags;
            }

            var set = new List<(string, string, ulong)>();
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in quests ?? []) {
                if (quest is null) {
                    continue;
                }

                string name = quest.m_questName;
                foreach (var result in Plain(quest.m_startResults).Concat(Plain(quest.m_endResults))) {
                    switch (result) {
                        case ResModifyEntry { m_isQuestRegistry: false } entry when !string.IsNullOrEmpty(entry.m_entryName):
                            set.Add((name, entry.m_entryName, entry.m_value <= 0 ? 1UL : (ulong) entry.m_value));
                            break;
                        case ResRemoveEntry { m_isQuestRegistry: false } entry when !string.IsNullOrEmpty(entry.m_entryName):
                            removed.Add(entry.m_entryName);
                            break;
                    }
                }

                foreach (var goal in quest.m_goals ?? []) {
                    foreach (var result in Plain(goal?.m_completeResults)) {
                        if (result is ResRemoveEntry { m_isQuestRegistry: false } entry && !string.IsNullOrEmpty(entry.m_entryName)) {
                            removed.Add(entry.m_entryName);
                        }
                    }
                }
            }

            s_flags = set.Where(f => s_caughtUp.Contains(f.Item2) && !removed.Contains(f.Item2)).Distinct().ToList();
            s_quests = quests;
            s_version = version;

            return s_flags;
        }
    }

    private static IEnumerable<Result> Plain(ResultList list) {
        foreach (var result in list?.m_results ?? []) {
            if (result is null || result.m_requirements?.m_requirements is { Count: > 0 }) {
                continue;
            }

            yield return result;
        }
    }

}
