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
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

/// <summary>
/// The game's quest chains are numbered steps of one code: KT-BAL-C04-001, -002, -003. The captured quests keep the
/// first step's needs (school, level) but many later steps came with no needs at all, so their giver offered every one
/// of them at once, to wizards of any school: Alhazred listed the whole Krokotopia Balance chain (Takin' the Omnibus,
/// Winds of Change, Red Heat, Good Day Sunshine, Sun King...) to a wizard who had done none of it (Niduus 2026-11-04:
/// "why does he have like 500 quests for me?"). A later step with no needs of its own now needs the step before it
/// done, as the steps that kept their needs already do (KT-BAL-C06-002 needs KT-BAL-C06-001 "Complete").
/// </summary>
public static partial class QuestChainOrder {

    [GeneratedRegex(@"^(.+-C\d+-)(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChainStepRegex();

    /// <summary>Gives each later chain step with no needs the step before it. Runs once after SpiralDB loads.</summary>
    public static void ApplyAll(IReadOnlyList<QuestTemplate> quests) {
        try {
            var chains = new Dictionary<string, SortedDictionary<int, List<QuestTemplate>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in quests) {
                if (quest?.m_questName is null || ChainStepRegex().Match((string) quest.m_questName) is not { Success: true } match) {
                    continue;
                }

                if (!chains.TryGetValue(match.Groups[1].Value, out var steps)) {
                    chains[match.Groups[1].Value] = steps = [];
                }

                var step = int.Parse(match.Groups[2].Value);
                if (!steps.TryGetValue(step, out var same)) {
                    steps[step] = same = [];
                }

                same.Add(quest);
            }

            var gated = 0;
            foreach (var steps in chains.Values) {
                string previous = null;
                foreach (var same in steps.Values) {
                    var name = (string) same[0].m_questName;

                    if (previous is not null) {
                        foreach (var quest in same.Where(NeedsOrder)) {
                            quest.m_requirements = new RequirementList {
                                m_applyNOT = false,
                                m_operator = Operator.ROP_AND,
                                m_requirements = [
                                    new ReqHasEntry {
                                        m_entryName = "Complete",
                                        m_displayName = "",
                                        m_isQuestRegistry = true,
                                        m_questName = previous,
                                        m_applyNOT = false,
                                        m_operator = Operator.ROP_AND,
                                    },
                                ],
                            };
                            gated++;
                        }
                    }

                    // Only a step a wizard can still take is "the step before" (a retired one would leave the next unreachable).
                    if (same.Any(q => !q.m_outdated)) {
                        previous = name;
                    }
                }
            }

            Logger.Information("Quest chains: {0} later steps now need the step before them.", Logger.Args(gated));
        }
        catch (Exception ex) {
            Logger.Error("Quest chain order failed: {0}", Logger.Args(ex.Message));
        }
    }

    // A step offered by an NPC (it has a "Prep" line) that came with no needs of its own.
    private static bool NeedsOrder(QuestTemplate quest)
        => !quest.m_outdated
            && quest.m_requirements?.m_requirements is not { Count: > 0 }
            && (quest.m_dialogList as ActorDialogList)?.m_dialogs?.Any(d => (string) d?.m_dialogTag == "Prep") == true;
}
