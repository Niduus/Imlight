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
 *
 * ========================================================================
 * QUEST MADLIBS
 * ========================================================================
 * 
 * PURPOSE:
 * Manages "Madlibs" for quests and goals, providing dynamic text
 * generation based on quest and goal templates and player progress.
 * 
 * USAGE EXAMPLE:
 * var questMadlib = QuestMadlibs.GetMadLibForQuest(questTemplate);
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/21/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Imlight.CoreLib.Game.Madlibs;

internal static class QuestMadlibs {

    internal static MadlibBlock GetMadLibForQuest(QuestTemplate quest)
        => new() {
            m_madlibs = [
        new MadlibArgT_ByteString {
                        m_madlibToken = "NAME",
                        m_madlibArgument = quest.m_questTitle
                    },
                    new MadlibArgT_ByteString {
                        m_madlibToken = "LEVEL",
                        m_madlibArgument = quest.m_questLevel.ToString()
                    },
        ],
            m_blockToken = "QUEST"
        };

    /// <summary>
    /// How many kills, items or uses a tallied goal needs. A defeat goal keeps its count in m_bountyTotal:
    /// its tally counter's m_count is 0 in most quests ("Pesky Pirates" needs 3 Skeletal Pirates, not 0).
    /// A goal that names no count needs one.
    /// </summary>
    internal static int GetGoalTotal(GoalTemplate gTemplate) {
        var total = gTemplate is BountyGoalTemplate { m_bountyTotal: > 0 } bounty
            ? bounty.m_bountyTotal
            : gTemplate.m_tallyCounter?.m_count ?? 0;

        return Math.Max(total, 1);
    }

    internal static MadlibBlock GetAppropriateMadlibBlockForGoal(GoalTemplate gTemplate, GoalInstance gInstance)
        => gTemplate.m_goalType switch {
            GOAL_TYPE.GOAL_TYPE_WAYPOINT => GetMadlibBlockForWaypointGoal(gTemplate),
            GOAL_TYPE.GOAL_TYPE_PERSONA => GetMadlibBlockForPersonaGoal(gTemplate),
            GOAL_TYPE.GOAL_TYPE_BOUNTY 
                or GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT 
                or GOAL_TYPE.GOAL_TYPE_USAGE 
                    => GetMadlibBlockForTalliedGoal(gTemplate, gInstance),
            _ => new MadlibBlock()
        };

    private static MadlibBlock GetMadlibBlockForWaypointGoal(GoalTemplate gTemplate)
        => new() {
            m_madlibs = [
                new MadlibArgT_ByteString {
                    m_madlibToken = "NAME",
                    m_madlibArgument = gTemplate.m_goalTitle
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "LOCATION",
                    m_madlibArgument = gTemplate.m_locationName
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "TALLYTEXT",
                    m_madlibArgument = gTemplate.m_tallyCounter?.m_descriptor ?? string.Empty
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "TALLYTEXT2",
                    m_madlibArgument = gTemplate.m_tallyCounter?.m_descriptor2 ?? string.Empty
                },
            ],
            m_blockToken = "GOAL"
        };

    private static MadlibBlock GetMadlibBlockForPersonaGoal(GoalTemplate gTemplate) {
        string firstName = string.Empty;
        string lastName = string.Empty;
        string title = string.Empty;
        string nickname = string.Empty;
        string nameFormat = null;

        if (gTemplate.m_dialogList is ActorDialogList dialogList) {
            var completionDialogEntry = dialogList.m_dialogs
                .FirstOrDefault(de => de.m_dialogTag == "Completion");

            // Retail sends the Completion dialog's own NPC madlib block keys;
            // the blocks are ordered to match the entries.
            var npcBlock = completionDialogEntry?.m_madlibs?.FirstOrDefault()?.m_madlibBlock;
            if (npcBlock is not null) {
                firstName = GetStringArgument(npcBlock, "FIRSTNAME") ?? firstName;
                lastName = GetStringArgument(npcBlock, "LASTNAME") ?? lastName;
                title = GetStringArgument(npcBlock, "TITLE") ?? title;
                nickname = GetStringArgument(npcBlock, "NICKNAME") ?? nickname;
                nameFormat = GetStringArgument(npcBlock, "FULLNAME");
            }
            else {
                // Fallback: resolve the display name from the entry's actor template.
                var templateId = completionDialogEntry?.m_dialogEntries
                    ?.FirstOrDefault()?.m_actorTemplateID ?? 0;

                (firstName, lastName) = NameKeysOfTemplate(templateId);
            }
        }

        // No Completion dialog names the NPC ("I'm With the Band" has none, so its "Talk To" showed no name;
        // Niduus, 2026-10-24): take the NPC the goal itself names, else one of the goal's other dialogs.
        if (string.IsNullOrEmpty(firstName) && nameFormat is null) {
            (firstName, lastName) = NameKeysOfTemplate(TemplateOfPersona((gTemplate as PersonaGoalTemplate)?.m_personaName));
        }

        if (string.IsNullOrEmpty(firstName) && nameFormat is null && gTemplate.m_dialogList is ActorDialogList otherDialogs) {
            var templateId = otherDialogs.m_dialogs
                .SelectMany(d => d.m_dialogEntries ?? [])
                .Select(e => e?.m_actorTemplateID ?? 0)
                .FirstOrDefault(id => id != 0);
            (firstName, lastName) = NameKeysOfTemplate(templateId);
        }

        var madLibs = new MadlibBlock() {
            m_madlibs = [
                new MadlibArgT_ByteString {
                    m_madlibToken = "NAME",
                    m_madlibArgument = gTemplate.m_goalTitle
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "LOCATION",
                    m_madlibArgument = gTemplate.m_locationName
                },
                ..GetTallyArguments(gTemplate),
                new MadlibArgT_ByteString {
                    m_madlibToken = "FIRSTNAME",
                    m_madlibArgument = firstName
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "LASTNAME",
                    m_madlibArgument = lastName
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "TITLE",
                    m_madlibArgument = title
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "NICKNAME",
                    m_madlibArgument = nickname
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "FULLNAME",
                    m_madlibArgument = GetGoalNameFormat(nameFormat, lastName)
                }
            ],
            m_blockToken = "GOAL"
        };

        return madLibs;
    }

    /// <summary>
    /// The first and last name keys of an NPC template's display name. A name the game does not split into a first and
    /// a last name (one word, or parts the name table lacks) is all first name, which the quest book shows whole.
    /// </summary>
    private static (string First, string Last) NameKeysOfTemplate(ulong templateId) {
        if (templateId == 0 || CoreObjectFactory.GetCoreTemplate(templateId) is not GameObjectTemplate npcTemplate) {
            return (string.Empty, string.Empty);
        }

        string fullName = npcTemplate.m_displayName;
        var (first, last) = Locale.GetEnglishNameKeys(fullName);

        return string.IsNullOrEmpty(first) ? (fullName ?? string.Empty, string.Empty) : (first, last);
    }

    private static readonly ConcurrentDictionary<string, uint> s_personaTemplates = new(StringComparer.Ordinal);

    /// <summary>
    /// The template of the NPC a persona goal names: its object name, or the object name plus "_Persona"
    /// (GH-HERO-NPC01_Persona is ObjectData/GH/GH-HERO-NPC01.xml). 0 when no template has that name.
    /// </summary>
    private static uint TemplateOfPersona(string personaName) {
        if (string.IsNullOrEmpty(personaName)) {
            return 0;
        }

        return s_personaTemplates.GetOrAdd(personaName, name => {
            const string suffix = "_Persona";
            var objectName = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
            var file = "/" + objectName + ".xml";

            return CoreObjectFactory.TryGetCoreTemplateID(
                location => location is not null && ((string) location.m_filename)?.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true,
                out var id) ? id : 0;
        });
    }

    private static MadlibArgT_ByteString[] GetTallyArguments(GoalTemplate gTemplate)
        => gTemplate.m_tallyCounter is null ? [] : [
            new MadlibArgT_ByteString {
                m_madlibToken = "TALLYTEXT",
                m_madlibArgument = gTemplate.m_tallyCounter.m_descriptor
            },
            new MadlibArgT_ByteString {
                m_madlibToken = "TALLYTEXT2",
                m_madlibArgument = gTemplate.m_tallyCounter.m_descriptor2
            },
        ];

    private static string GetGoalNameFormat(string npcNameFormat, string lastName) {
        // The goal variant of a name format is the NPC's own format with "Goal_" after the prefix.
        const string prefix = "NPCFormats_";
        if (npcNameFormat is not null && npcNameFormat.StartsWith(prefix, StringComparison.Ordinal)) {
            return string.Concat(prefix, "Goal_", npcNameFormat.AsSpan(prefix.Length));
        }

        return string.IsNullOrEmpty(lastName) ? "NPCFormats_Goal_First_Only" : "NPCFormats_Goal_First_Last";
    }

    private static string GetStringArgument(MadlibBlock block, string token)
        => block.m_madlibs?.OfType<MadlibArgT_ByteString>()
            .FirstOrDefault(arg => arg.m_madlibToken == token)?.m_madlibArgument;

    private static MadlibBlock GetMadlibBlockForTalliedGoal(GoalTemplate gTemplate, GoalInstance gInstance)
        => new() {
            m_madlibs = [
                new MadlibArgT_ByteString {
                    m_madlibToken = "NAME",
                    m_madlibArgument = gTemplate.m_goalTitle
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "LOCATION",
                    m_madlibArgument = gTemplate.m_locationName
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "TALLYTEXT",
                    m_madlibArgument = gTemplate.m_tallyCounter?.m_descriptor ?? string.Empty
                },
                new MadlibArgT_ByteString {
                    m_madlibToken = "TALLYTEXT2",
                    m_madlibArgument = gTemplate.m_tallyCounter?.m_descriptor2 ?? string.Empty
                },
                new MadlibArgT_int {
                    m_madlibToken = "COUNT",
                    m_madlibArgument = gInstance?.CurrentProgress ?? 0
                },
                new MadlibArgT_int {
                    m_madlibToken = "TOTAL",
                    m_madlibArgument = GetGoalTotal(gTemplate)
                },
                new MadlibArgT_int {
                    m_madlibToken = "SUBSCRIBER_TOTAL",
                    m_madlibArgument = GetGoalTotal(gTemplate)
                }
            ],
            m_blockToken = "GOAL"
        };

}