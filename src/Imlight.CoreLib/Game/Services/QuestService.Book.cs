using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// `.questbook` (Niduus, 2026-11-04: "players can view their next steps"): each quest in the wizard's quest book with
/// the step they are on (step 2 of 5), the step after it, and the quest it opens once done. Anyone can use it. Ahziael's
/// Quests page, My Quests tab, shows the same with buttons to go to a step, finish it or finish the quest.
/// </summary>
internal partial class QuestService {

    private void CommandQuestBook(Wizard wizard) {
        var active = wizard.QuestBehavior.CurrentQuestInstances.Where(q => q is not null).ToList();
        if (active.Count == 0) {
            CommandReply("Your quest book is empty. Talk to an NPC with a quest mark over their head, or open Ahziael's Quests page to see what comes next.");
            return;
        }

        // Main story first, like the game's quest book.
        var rows = active
            .Select(q => (Instance: q, Template: GetQuestTemplate(q.QuestName)))
            .OrderByDescending(r => r.Template?.m_mainline == true || r.Instance.QuestName.Contains("MAIN", StringComparison.OrdinalIgnoreCase))
            .ToList();

        CommandReply($"Your quest book ({rows.Count} quest{(rows.Count == 1 ? "" : "s")}):");
        foreach (var (qInstance, qTemplate) in rows) {
            var title = qTemplate is null ? qInstance.QuestName : QuestGuide.QuestTitle(qTemplate);
            var goals = (qTemplate?.m_goals ?? []).Where(g => g is not null).ToList();
            if (goals.Count == 0) {
                CommandReply($"  {title}: no steps the server knows.");
                continue;
            }

            var current = new List<int>();
            for (var i = 0; i < goals.Count; i++) {
                if (qInstance.IsGoalActive(goals[i].m_goalName)) {
                    current.Add(i);
                }
            }

            if (current.Count == 0) {
                CommandReply($"  {title}: no step is running. Type .stuck to fix it.");
                continue;
            }

            var now = string.Join(" and ", current.Select(i => QuestGuide.StepText(goals[i])).Where(t => t.Length > 0).Distinct());
            CommandReply($"  {title}: step {current[0] + 1} of {goals.Count}, {now}.");

            var after = current.Max() + 1;
            if (after < goals.Count) {
                CommandReply($"    Next: {QuestGuide.StepText(goals[after])}.");
            }
            else {
                var next = QuestGuide.QuestsAfter(qInstance.QuestName).FirstOrDefault();
                CommandReply(next is null
                    ? "    This is the last step."
                    : $"    Last step. Then: {QuestGuide.QuestTitle(next)}.");
            }
        }
    }

}
