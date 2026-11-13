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
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Which main quests are the story (Niduus, 2026-11-12: "main quests that allow you to advance to complete the world.
/// While they are marked as main quests, not all main quests are essential to complete the world"). A world's story is
/// its last main quest (the one at the end of the longest chain of main quests in that world) and every main quest of
/// the same world it needs first, at any depth. Other main quests of the world are main, not story. Only reads the
/// quests the server already has; worked out once and again when the Quest Editor changes quests.
/// </summary>
internal static class QuestStory {

    internal const string INSTANCE = "instance";
    internal const string STORY = "story";
    internal const string MAIN = "main";
    internal const string SIDE = "side";

    private static readonly Lock s_lock = new();
    private static HashSet<string> s_story;
    private static int s_version = -1;

    /// <summary>A main quest: marked main in the game data, or "MAIN" in its code.</summary>
    internal static bool IsMain(QuestTemplate quest)
        => quest is not null && (quest.m_mainline || ((string) quest.m_questName ?? "").Contains("MAIN", StringComparison.OrdinalIgnoreCase));

    /// <summary>A quest of a world's story chain (the main quests that lead to the world's end).</summary>
    internal static bool IsStory(QuestTemplate quest) => KindOf(quest) == STORY;

    /// <summary>"instance" (a dungeon's own quest), "story", "main" or "side".</summary>
    internal static string KindOf(QuestTemplate quest) {
        if (quest is not null && DungeonQuestIndex.IsDungeonQuest(quest.m_questName)) {
            return INSTANCE;
        }

        if (!IsMain(quest)) {
            return SIDE;
        }

        return Story().Contains((string) quest.m_questName ?? "") ? STORY : MAIN;
    }

    /// <summary>The world a quest belongs to: its code's first part ("WC" of "WC-MAIN-C01-001"), or its first step's world.</summary>
    internal static string WorldOf(QuestTemplate quest) {
        var name = (string) quest?.m_questName ?? "";
        var dash = name.IndexOf('-');
        if (dash is > 0 and <= 5) {
            return name[..dash].ToUpperInvariant();
        }

        var zone = (quest?.m_goals ?? []).Where(g => g is not null).Select(g => (string) g.m_destinationZone).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z)) ?? "";
        var slash = zone.IndexOf('/');

        return (slash > 0 ? zone[..slash] : zone).Trim().ToUpperInvariant();
    }

    private static HashSet<string> Story() {
        var version = Quests.QuestOptions.Version;
        lock (s_lock) {
            if (s_story is not null && s_version == version) {
                return s_story;
            }
        }

        HashSet<string> story;
        try {
            story = Build();
        }
        catch (Exception ex) {
            Logger.Warning("Quest tracker: the story quests could not be worked out: {0}", Logger.Args(ex.Message));
            story = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        lock (s_lock) {
            (s_story, s_version) = (story, version);
        }

        return story;
    }

    private static HashSet<string> Build() {
        var mains = QuestTemplateCollection.GetAllQuests()
            .Where(q => q is not null && !string.IsNullOrEmpty(q.m_questName) && IsMain(q) && !Quests.QuestOptions.IsOff(q.m_questName))
            .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var needs = mains.ToDictionary(p => p.Key,
            p => QuestGuide.NeededQuests(p.Value.m_requirements).Where(mains.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);

        // How long the chain of main quests before each one is (a loop counts once).
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int Depth(string name) {
            if (depth.TryGetValue(name, out var known)) {
                return known;
            }

            if (!visiting.Add(name)) {
                return 0;
            }

            var deepest = 0;
            foreach (var need in needs.GetValueOrDefault(name) ?? []) {
                deepest = Math.Max(deepest, Depth(need));
            }

            visiting.Remove(name);
            depth[name] = deepest + 1;

            return deepest + 1;
        }

        var story = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var world in mains.Values.GroupBy(WorldOf, StringComparer.OrdinalIgnoreCase)) {
            // The world's last quest: the deepest; between equals, the latest code.
            var last = world.OrderByDescending(q => Depth(q.m_questName)).ThenByDescending(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase).First();
            var worldName = world.Key;
            var waiting = new Stack<string>([last.m_questName]);
            while (waiting.Count > 0) {
                var name = waiting.Pop();
                if (!story.Add(name)) {
                    continue;
                }

                foreach (var need in needs.GetValueOrDefault(name) ?? []) {
                    if (string.Equals(WorldOf(mains[need]), worldName, StringComparison.OrdinalIgnoreCase)) {
                        waiting.Push(need);
                    }
                }
            }
        }

        Logger.Information("Quest tracker: {0} of {1} main quests are story quests.", Logger.Args(story.Count, mains.Count));

        return story;
    }

}
