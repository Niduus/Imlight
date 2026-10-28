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
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// The quests each character speaks a line in (its template, from the line's persona or actor), worked out once for each
/// set of quest changes. A quest's talk often has a second character join in (Duncan Grimwater speaks up while Suzie's
/// brother offers The Shadow of Death Magic), and the game turns the camera to that character as it speaks.
/// </summary>
internal static class QuestDialogSpeakers {

    private static Tuple<int, Dictionary<ulong, List<QuestTemplate>>> s_known
        = Tuple.Create(-1, new Dictionary<ulong, List<QuestTemplate>>());
    private static readonly object s_lock = new();

    public static IReadOnlyList<QuestTemplate> QuestsSpokenIn(ulong templateId)
        => templateId != 0 && Speakers().TryGetValue(templateId, out var quests) ? quests : [];

    /// <summary>Every character with a line in the quest's dialogs.</summary>
    public static HashSet<ulong> SpeakersOf(QuestTemplate quest) {
        var speakers = new HashSet<ulong>();
        var entries = (quest?.m_dialogList as ActorDialogList)?.m_dialogs?.SelectMany(d => d?.m_dialogEntries ?? []) ?? [];
        foreach (var entry in entries) {
            var templateId = entry switch {
                null => 0UL,
                NPCDialogEntry npcEntry => QuestMadlibs.SpeakerTemplateOf(npcEntry.m_personaName, npcEntry.m_actorTemplateID),
                _ => (ulong) entry.m_actorTemplateID,
            };
            if (templateId != 0) {
                speakers.Add(templateId);
            }
        }

        return speakers;
    }

    private static Dictionary<ulong, List<QuestTemplate>> Speakers() {
        var known = s_known;
        if (known.Item1 == QuestOptions.Version) {
            return known.Item2;
        }

        lock (s_lock) {
            known = s_known;
            var version = QuestOptions.Version;
            if (known.Item1 == version) {
                return known.Item2;
            }

            var speakers = new Dictionary<ulong, List<QuestTemplate>>();
            foreach (var quest in SpiralDB.QuestTemplates) {
                if (quest is null) {
                    continue;
                }

                foreach (var templateId in SpeakersOf(quest)) {
                    if (!speakers.TryGetValue(templateId, out var quests)) {
                        speakers[templateId] = quests = [];
                    }

                    quests.Add(quest);
                }
            }

            s_known = Tuple.Create(version, speakers);

            return speakers;
        }
    }

}
