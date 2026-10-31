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
using System.Collections.Concurrent;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Requirements;

/// <summary>
/// Zone triggers name the game's old quest codes, and the game later rebuilt many quests under new ones: General Akilles'
/// tower waits for "WC-ST03-C01-005", which is now Akilles' Deal (WC-CYCLOPS-MAIN-005). The server only has the new
/// quests, so the boss's taunt on walking up to him (and the students it brings out) never played (Niduus 2026-10-31:
/// "went to fight General Akilles and his Dialogue didn't pop up at all"). The same held for Lord Nightshade, Prince
/// Gobleian, Galvanost's and Firecat Alley's towers and Rattlebones.
/// A trigger's quest the server does not have stands for the quest of the same world that sends the wizard into this
/// zone (a "go to" step here) and then has them defeat something: met from that "go to" step until the defeat is done.
/// </summary>
internal static class RetiredQuestStandIn {

    private static readonly ConcurrentDictionary<(string Retired, string Zone, string Quest), byte> s_logged = new();

    /// <summary>True when the quest is unknown to the server and the wizard is on the defeat step of the quest leading here.</summary>
    public static bool IsMet(IRequirementContext context, string retiredQuest) {
        if (context is not IZoneStateContext { TriggerZonePath: { Length: > 0 } zonePath }
            || string.IsNullOrEmpty(retiredQuest)
            || SpiralDB.QuestExists(retiredQuest)
            || context.GetWizard() is not { } wizard) {
            return false;
        }

        var world = WorldOf(retiredQuest);
        if (world is null) {
            return false;
        }

        if (AdvancedMode.TryGetNormal(zonePath, out var normal)) {
            zonePath = normal;
        }

        foreach (var instance in wizard.QuestBehavior.CurrentQuestInstances) {
            if (!string.Equals(WorldOf(instance.QuestName), world, StringComparison.OrdinalIgnoreCase)
                || SpiralDB.GetQuestByName(instance.QuestName)?.m_goals?.ToList() is not { Count: > 0 } goals) {
                continue;
            }

            var arrival = goals.FindIndex(g => g?.m_goalType == GOAL_TYPE.GOAL_TYPE_WAYPOINT && SameZone((string) g.m_destinationZone, zonePath));
            if (arrival < 0) {
                continue;
            }

            // From the "go to" step (the volume can be posted as the wizard appears, before that step is marked done)
            // up to the last defeat step after it.
            var lastFight = goals.FindLastIndex(g => g?.m_goalType is GOAL_TYPE.GOAL_TYPE_BOUNTY or GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT);
            if (lastFight <= arrival) {
                continue;
            }

            var fighting = goals.Skip(arrival).Take(lastFight - arrival + 1)
                .Any(g => g is not null
                          && instance.GoalProgress.FirstOrDefault(p => p.GoalName == (string) g.m_goalName) is { } progress
                          && progress.DoesPlayerHaveGoal() && !progress.IsGoalCompleted());
            if (!fighting) {
                continue;
            }

            if (s_logged.TryAdd((retiredQuest, zonePath, instance.QuestName), 0)) {
                Logger.Information("Zone {0}: a trigger asks for quest {1}, which the server does not have; {2} stands in for it.",
                    Logger.Args(zonePath, retiredQuest, instance.QuestName));
            }

            return true;
        }

        return false;
    }

    // "WC" of "WC-ST03-C01-005": a stand-in only comes from the same world, so an event quest (HO-Halloween_2013-...)
    // never turns into a story quest of the zone.
    private static string WorldOf(string quest) {
        var dash = quest?.IndexOf('-') ?? -1;

        return dash > 0 ? quest[..dash] : null;
    }

    private static bool SameZone(string a, string b)
        => !string.IsNullOrEmpty(a)
           && string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

}
