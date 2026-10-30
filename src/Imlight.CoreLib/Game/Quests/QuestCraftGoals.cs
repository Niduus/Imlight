/*
 * "Craft" steps of crafting side quests (Legend of Two Rings, Two Ring Circus, the Needle quests; 2026-10-30).
 *
 * In the game such a step is done when you craft the item it names. Our quest files (spiraldb-extra, from Wizard404)
 * make it a usage step whose client tag is "ImlightCraft:<item template id>"; crafting that item (in the crafting
 * window or with .recipe make) completes it through the quest service, like using an object would.
 */

using System;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

internal static class QuestCraftGoals {
    internal const string TAG_PREFIX = "ImlightCraft:";

    /// <summary>True when the goal is done by crafting an item.</summary>
    internal static bool IsCraftGoal(GoalTemplate goal)
        => goal?.m_clientTags?.Any(tag => ((string) tag)?.StartsWith(TAG_PREFIX, StringComparison.Ordinal) == true) == true;

    /// <summary>Counts a crafted item for every active craft step of the wizard's quests that names it.</summary>
    internal static void Credit(IActorRef session, Wizard wizard, ulong itemTemplateId, int count) {
        if (session is null || wizard?.QuestBehavior?.CurrentQuestInstances is not { } quests || itemTemplateId == 0 || count <= 0) {
            return;
        }

        var tag = TAG_PREFIX + itemTemplateId;
        foreach (var quest in quests.ToList()) {
            var template = QuestTemplateCollection.GetQuestByName(quest.QuestName);
            foreach (var goal in template?.m_goals ?? []) {
                if (goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE
                    || goal.m_clientTags?.Any(t => (string) t == tag) != true) {
                    continue;
                }

                var progress = quest.GoalProgress.FirstOrDefault(g => g.GoalName == (string) goal.m_goalName);
                if (progress is null || progress.CurrentProgress < 0 || progress.CurrentProgress == int.MaxValue
                    || !quest.IsGoalActive(progress.GoalName)) {
                    continue;
                }

                var left = Math.Max(1, (goal.m_tallyCounter?.m_count ?? 1) - progress.CurrentProgress);
                for (var i = 0; i < Math.Min(count, left); i++) {
                    session.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL { QuestID = quest.ID, GoalID = progress.ID });
                }

                Logger.Information("Quests: {0} crafted {1}, which counts for quest {2} step {3}.",
                    Logger.Args(wizard.CharId, itemTemplateId, quest.QuestName, (string) goal.m_goalName));
            }
        }
    }
}
