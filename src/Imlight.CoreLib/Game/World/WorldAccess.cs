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
 * WORLD ACCESS
 * ========================================================================
 *
 * PURPOSE:
 * The worlds the Spiral Door lists for a wizard. A world opens the way the game opens it: with the first quest that
 * sends the wizard there (a quest step whose zone is in that world), started or done.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

internal static class WorldAccess {

    // The Spiral Door's worlds, in the order the game lists them.
    private static readonly string[] s_worlds = [
        "WizardCity", "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Grizzleheim", "Celestia", "Wysteria",
        "Zafaria", "Avalon", "Azteca", "Aquila", "Khrysalis", "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria",
        "Novus", "Wallaru", "Darkmoor",
    ];

    // Open from the start, as before: Wizard City is home, and Grizzleheim and Wysteria are side worlds no main quest
    // sends anyone to.
    private static readonly HashSet<string> s_alwaysOpen = new(["WizardCity", "Grizzleheim", "Wysteria"], StringComparer.Ordinal);

    // A class, not a value tuple: zones read it on threads of their own, so it must never be half written.
    private static Tuple<int, Dictionary<string, string[]>> s_questsByWorld = Tuple.Create(-1, new Dictionary<string, string[]>());
    private static readonly object s_lock = new();

    /// <summary>
    /// The worlds this wizard can pick at the Spiral Door. [World] OpenAllDoors opens them all.
    /// </summary>
    public static List<string> OpenWorlds(Wizard wizard) {
        var openAll = ConfigurationManager.GetValue("World.OpenAllDoors", false);
        var here = wizard?.Zone?.Split('/')[0];
        var byWorld = QuestsByWorld();

        return [.. s_worlds.Where(world => openAll || wizard is null || s_alwaysOpen.Contains(world) || world == here
            || (byWorld.TryGetValue(world, out var quests)
                && quests.Any(q => wizard.HasQuest(q) || wizard.HasCompletedQuest(q))))];
    }

    // The quests with a step in each world, worked out once for each set of quest changes.
    private static Dictionary<string, string[]> QuestsByWorld() {
        var known = s_questsByWorld;
        if (known.Item1 == QuestOptions.Version) {
            return known.Item2;
        }

        lock (s_lock) {
            known = s_questsByWorld;
            var version = QuestOptions.Version;
            if (known.Item1 == version) {
                return known.Item2;
            }

            var byWorld = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
                if (quest?.m_goals is null || string.IsNullOrEmpty(quest.m_questName)) {
                    continue;
                }

                foreach (var goal in quest.m_goals) {
                    var zone = goal?.m_destinationZone.ToString();
                    var slash = zone?.IndexOf('/') ?? -1;
                    if (slash <= 0) {
                        continue;
                    }

                    var world = zone[..slash];
                    if (!byWorld.TryGetValue(world, out var names)) {
                        byWorld[world] = names = [];
                    }
                    names.Add(quest.m_questName);
                }
            }

            var built = byWorld.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
            s_questsByWorld = Tuple.Create(version, built);

            return built;
        }
    }

}
