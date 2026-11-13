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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Which quests the Quest Team and Ultimus work on (Niduus, 2026-11-13: "Some quests are marked as main quests and they
/// do not always allow you to reject picking them up. Unless they are a Story Main quest, they are to be picked up but
/// IGNORED unless required to complete to advance in the Story Main Quest Line"). Story quests and dungeon quests are
/// played; any other quest (main or side) is played only when a story quest needs it done first. A main quest is
/// always taken when offered (the game may not let it be left), then ignored unless needed.
/// </summary>
internal static class BotStory {

    /// <summary>The rank of a quest the bots leave alone.</summary>
    internal const int IGNORED = -1;

    private static readonly Lock s_lock = new();
    private static HashSet<string> s_needed;
    private static int s_version = -1;

    /// <summary>How much the bots want a quest: 3 story, 2 dungeon, 1 needed by the story first, -1 ignored.</summary>
    internal static int Rank(QuestTemplate quest) {
        if (quest is null) {
            return IGNORED;
        }

        return QuestStory.KindOf(quest) switch {
            QuestStory.STORY => 3,
            QuestStory.INSTANCE => 2,
            _ => NeededByStory(quest.m_questName) ? 1 : IGNORED,
        };
    }

    /// <summary>Whether the bots work on this quest at all.</summary>
    internal static bool Plays(QuestTemplate quest) => Rank(quest) > 0;

    /// <summary>Whether to take an offered quest: one they play, or any main quest (taken, then ignored when not needed).</summary>
    internal static bool Takes(QuestTemplate quest) => quest is not null && (Plays(quest) || QuestStory.IsMain(quest));

    /// <summary>Whether a story quest needs this quest done first (any depth, side quests too).</summary>
    internal static bool NeededByStory(string questName) {
        if (string.IsNullOrEmpty(questName)) {
            return false;
        }

        var version = Quests.QuestOptions.Version;
        HashSet<string> needed;
        lock (s_lock) {
            needed = s_version == version ? s_needed : null;
        }

        if (needed is null) {
            needed = BuildNeeded();
            lock (s_lock) {
                (s_needed, s_version) = (needed, version);
            }
        }

        return needed.Contains(questName);
    }

    private static HashSet<string> BuildNeeded() {
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            var waiting = new Stack<string>(QuestStory.Story());
            while (waiting.Count > 0) {
                if (QuestTemplateCollection.GetQuestByName(waiting.Pop()) is not { } quest) {
                    continue;
                }

                foreach (var need in QuestGuide.NeededQuests(quest.m_requirements)) {
                    if (needed.Add(need)) {
                        waiting.Push(need);
                    }
                }
            }
        }
        catch (Exception ex) {
            Imlight.Common.Logger.Warning("Bots: the quests the story needs could not be worked out: {0}", Imlight.Common.Logger.Args(ex.Message));
        }

        return needed;
    }
}
