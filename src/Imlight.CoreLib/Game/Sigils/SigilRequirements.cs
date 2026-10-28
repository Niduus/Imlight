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
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Sigils;

/// <summary>
/// Reads a dungeon sigil's requirements (MinigameSigilInfo.m_requirements, from the zone file) against what the server
/// can ever make true. The zone files name the game's own quests, steps and registry entries; many quests were rebuilt
/// from guides under names of their own or were never captured, and nothing on the server sets most "QT-" entries, so a
/// pad waiting for one could never open (Niduus 2026-10-28: Galvanost Tower waits for "QT-WC-ST04-C01-004", which no
/// quest of the server sets). Each requirement the server cannot make true counts as met; the rest are checked as the
/// game checks them (left to right, each NOT on its own item, as RequirementDispatcher folds them).
/// </summary>
internal static class SigilRequirements {

    private const string COMPLETE_SUFFIX = "_Complete";
    private const string QUEST_ENTRY_PREFIX = "QT-";

    private sealed record Known(int Version, IReadOnlyList<QuestTemplate> Quests, HashSet<string> Entries,
                                HashSet<(string Quest, string Entry)> QuestEntries);

    private static Known s_known;
    private static readonly object s_lock = new();

    /// <summary>True when the wizard meets the requirements, counting any the server cannot make true as met.</summary>
    public static bool IsMet(RequirementList requirements, IRequirementContext context) {
        if (requirements?.m_requirements is not { Count: > 0 }) {
            return true;
        }

        var known = Current();
        var result = true;
        var joinOperator = Operator.ROP_AND;
        foreach (var requirement in requirements.m_requirements) {
            if (requirement is null) {
                continue;
            }

            var canChangeResult = joinOperator == Operator.ROP_OR ? !result : result;
            if (canChangeResult) {
                result = requirement switch {
                    RequirementList nested => IsMet(nested, context) != nested.m_applyNOT,
                    _ when !CanBeMet(requirement, known, context) => StandIn(requirement, context),
                    _ => RequirementDispatcher.EvaluateIndividualRequirement(requirement, context) != requirement.m_applyNOT,
                };
            }

            joinOperator = requirement.m_operator;
        }

        return result;
    }

    // A requirement the server cannot make true, read as near as the server can. One that names a quest the server knows
    // (a step or entry the rebuilt quest does not have, or "QT-<quest>") asks for that quest, active or done, so the
    // pad stays shut to players who never reached it; anything else, or a NOT, counts as met.
    private static bool StandIn(Requirement requirement, IRequirementContext context) {
        if (requirement.m_applyNOT || QuestOf(requirement) is not { } quest || !KnowsQuest(quest)) {
            return true;
        }

        return RequirementDispatcher.EvaluateIndividualRequirement(new ReqHasQuest { m_questName = quest }, context);
    }

    private static string QuestOf(Requirement requirement) {
        string quest = requirement switch {
            ReqHasGoal goal => goal.m_questName,
            ReqHasEntry { m_isQuestRegistry: true } entry => entry.m_questName,
            ReqEntryValue { m_isQuestRegistry: true } entry => entry.m_questName,
            ReqHasEntry entry => QuestOfEntry(entry.m_entryName),
            ReqEntryValue entry => QuestOfEntry(entry.m_entryName),
            _ => null,
        };

        return string.IsNullOrEmpty(quest) ? null : quest;
    }

    private static string QuestOfEntry(string entry)
        => string.IsNullOrEmpty(entry) ? null
           : entry.StartsWith(QUEST_ENTRY_PREFIX, StringComparison.OrdinalIgnoreCase) ? entry[QUEST_ENTRY_PREFIX.Length..]
           : entry.EndsWith(COMPLETE_SUFFIX, StringComparison.OrdinalIgnoreCase) ? entry[..^COMPLETE_SUFFIX.Length]
           : null;

    /// <summary>The requirements of the list the server can never make true, described for the log.</summary>
    public static IEnumerable<string> Unmeetable(RequirementList requirements) {
        var known = Current();
        foreach (var requirement in requirements?.m_requirements ?? []) {
            if (requirement is RequirementList nested) {
                foreach (var inner in Unmeetable(nested)) {
                    yield return inner;
                }
            }
            else if (requirement is not null && !CanBeMet(requirement, known, null)) {
                yield return Describe(requirement);
            }
        }
    }

    public static string Describe(RequirementList list)
        => string.Join(" ", (list?.m_requirements ?? []).Where(r => r is not null).Select(r =>
            (r is RequirementList nested ? "(" + Describe(nested) + ")" : Describe(r))
            + (r == list.m_requirements.Last(x => x is not null) ? "" : r.m_operator == Operator.ROP_OR ? " or" : " and")));

    private static string Describe(Requirement r) {
        var not = r.m_applyNOT ? "not " : "";

        return r switch {
            ReqHasQuest q => $"{not}quest {q.m_questName}",
            ReqHasGoal g => $"{not}step {g.m_questName}/{g.m_goalName} {g.m_requiredStatus}",
            ReqHasEntry { m_isQuestRegistry: true } e => $"{not}entry {e.m_questName}/{e.m_entryName}",
            ReqHasEntry e => $"{not}entry {e.m_entryName}",
            ReqEntryValue v => $"{not}entry value {v.m_entryName}",
            _ => $"{not}{r.GetType().Name}",
        };
    }

    // Whether anything on the server can ever make the requirement true for a player.
    private static bool CanBeMet(Requirement requirement, Known known, IRequirementContext context) {
        switch (requirement) {
            case ReqHasQuest quest:
                return KnowsQuest(quest.m_questName);
            case ReqHasGoal goal:
                return KnowsQuest(goal.m_questName)
                       && SpiralDB.GetQuestByName(goal.m_questName)?.m_goals?.Any(g => (string) g?.m_goalName == (string) goal.m_goalName) == true;
            case ReqHasEntry { m_isQuestRegistry: true } entry:
                return KnowsQuest(entry.m_questName)
                       && (string.Equals(entry.m_entryName, "Complete", StringComparison.OrdinalIgnoreCase)
                           || known.QuestEntries.Contains((entry.m_questName, entry.m_entryName)));
            case ReqEntryValue { m_isQuestRegistry: true } value:
                return KnowsQuest(value.m_questName) && known.QuestEntries.Contains((value.m_questName, value.m_entryName));
            case ReqEntryValue value:
                return !string.IsNullOrEmpty(value.m_entryName) && known.Entries.Contains(value.m_entryName);
            case ReqHasEntry entry: {
                string name = entry.m_entryName;
                if (string.IsNullOrEmpty(name)) {
                    return false;
                }

                if (known.Entries.Contains(name)) {
                    return true;
                }

                // "<quest>_Complete" is stamped when the quest is done.
                return name.EndsWith(COMPLETE_SUFFIX, StringComparison.OrdinalIgnoreCase)
                       && KnowsQuest(name[..^COMPLETE_SUFFIX.Length]);
            }
            default:
                // A requirement the server has no handler for could only ever say no.
                return context is null || RequirementDispatcher.FindHandlerForRequirement(requirement.GetType(), context) is not null;
        }
    }

    private static bool KnowsQuest(string questName)
        => !string.IsNullOrEmpty(questName) && SpiralDB.GetQuestByName(questName) is not null;

    // The registry entries some quest of the server sets, worked out once for each set of quest changes.
    private static Known Current() {
        var quests = SpiralDB.QuestTemplates;
        var version = QuestOptions.Version;
        var known = s_known;
        if (known is not null && known.Version == version && ReferenceEquals(known.Quests, quests)) {
            return known;
        }

        lock (s_lock) {
            known = s_known;
            if (known is not null && known.Version == version && ReferenceEquals(known.Quests, quests)) {
                return known;
            }

            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var questEntries = new HashSet<(string, string)>();
            foreach (var quest in quests) {
                if (quest is null) {
                    continue;
                }

                Collect(quest.m_startResults, entries, questEntries);
                Collect(quest.m_endResults, entries, questEntries);
                foreach (var goal in quest.m_goals ?? []) {
                    Collect(goal?.m_activateResults, entries, questEntries);
                    Collect(goal?.m_completeResults, entries, questEntries);
                }
            }

            s_known = new Known(version, quests, entries, questEntries);

            return s_known;
        }
    }

    private static void Collect(ResultList results, HashSet<string> entries, HashSet<(string, string)> questEntries) {
        foreach (var result in results?.m_results ?? []) {
            switch (result) {
                case ResModifyEntry { m_isQuestRegistry: true } entry when !string.IsNullOrEmpty(entry.m_entryName):
                    questEntries.Add((entry.m_questName, entry.m_entryName));
                    break;
                case ResModifyEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                    entries.Add(entry.m_entryName);
                    break;
                case ResIfThenElse branch:
                    Collect(branch.m_thenResults, entries, questEntries);
                    Collect(branch.m_elseResults, entries, questEntries);
                    break;
            }
        }
    }

}
