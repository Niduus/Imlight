using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The quest safety guards (Game/Quests/QuestSafety, [QuestSafety] in Imlight.ini): nothing a quest needs may leave a
/// wizard stuck on it. Run at every zone load, whenever a step starts, and by the player's .stuck command.
/// </summary>
internal partial class QuestService {

    // What this session already told the wizard about a step it can't do now, so a zone change doesn't repeat it.
    private readonly HashSet<string> _safetyTold = new(StringComparer.Ordinal);

    private bool IsActiveUseStep(Wizard wizard, string questName, string goalName)
        => wizard.QuestBehavior.CurrentQuestInstances.Any(q => q is not null
            && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase) && q.IsGoalActive(goalName));

    /// <summary>
    /// Before the zone sends the quest book and the wizard's mods: a quest that got new steps since it was started gets
    /// them (else its next step could never start), and used quest objects an active step needs again get their look back.
    /// </summary>
    private void PrepareQuestsForZone(Wizard wizard) {
        try {
            if (wizard?.QuestBehavior is null || !QuestSafety.Enabled) {
                return;
            }

            if (QuestSafety.FixStepsThatCannotStart) {
                foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.Where(q => q is not null).ToList()) {
                    var qTemplate = QuestTemplateCollection.GetQuestByName(qInstance.QuestName);
                    if (qTemplate?.m_goals is null || !QuestSafety.Covers(qTemplate)) {
                        continue;
                    }

                    var known = (qInstance.GoalProgress ?? []).Where(g => g is not null).Select(g => g.GoalName).ToHashSet(StringComparer.Ordinal);
                    var missing = qTemplate.m_goals.Where(g => g is not null && !known.Contains(g.m_goalName))
                        .Select(g => new GoalInstance(g, wizard.CharId)).ToList();
                    if (missing.Count == 0) {
                        continue;
                    }

                    qInstance.GoalProgress = [.. qInstance.GoalProgress ?? [], .. missing];
                    QuestInstanceCollection.AddMissingGoals(qInstance.ID, missing);
                    Logger.Information("Quest safety: {0} quest '{1}' changed since it was started; {2} new steps added ({3}).",
                        Logger.Args(wizard.CharId, qInstance.QuestName, missing.Count, string.Join(", ", missing.Select(g => g.GoalName))));
                }
            }

            // The zone sends the wizard's mods after this, so the objects come back with their first look by themselves.
            QuestSafety.RestoreUsedLooks(wizard, (quest, goal) => IsActiveUseStep(wizard, quest, goal));
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not prepare the quests of {0}: {1} {2}", Logger.Args(wizard?.CharId, ex.Message, ex.StackTrace));
        }
    }

    /// <summary>
    /// After the zone loaded (or for .stuck): a quest with no step to do moves on or is finished, and a step that needs a
    /// turned-off NPC, creature or object is done for the wizard. Returns what was found, one line each.
    /// </summary>
    private List<string> RepairActiveQuests(Wizard wizard, bool fromCommand) {
        var lines = new List<string>();
        try {
            if (wizard?.QuestBehavior is null || !QuestSafety.Enabled) {
                return lines;
            }

            foreach (var qInstance in wizard.QuestBehavior.CurrentQuestInstances.Where(q => q is not null).ToList()) {
                if (!wizard.HasQuest(qInstance.QuestName)) {
                    continue;
                }

                var qTemplate = GetQuestTemplate(qInstance.QuestName);
                if (qTemplate?.m_goals is null) {
                    if (fromCommand) {
                        lines.Add($"{qInstance.QuestName}: this quest is no longer on the server, so it can't go on (the host can take it off in Ahziael's Quests page).");
                    }

                    continue;
                }

                // Dungeon quests follow their instance; a step waiting on its completion dialog isn't stuck.
                var guarded = QuestSafety.Covers(qTemplate) && !DungeonQuestIndex.IsDungeonQuest(qInstance.QuestName)
                    && !_pendingGoalCompletions.Any(p => p.QuestId == qInstance.ID);
                if (!guarded && !fromCommand) {
                    continue;
                }

                string title = QuestTitleOf(qTemplate);
                var active = qTemplate.m_goals.Where(g => qInstance.IsGoalActive(g.m_goalName)).ToList();
                // A quest the guards don't look after (.stuck only) is just said, not changed.
                if (guarded && active.Count == 0) {
                    if (QuestSafety.FixStepsThatCannotStart && FixQuestWithNothingToDo(wizard, qInstance, qTemplate) is { } fixedLine) {
                        lines.Add($"{title}: {fixedLine}");
                    }

                    continue;
                }

                foreach (var goal in guarded ? active : []) {
                    if (!qInstance.IsGoalActive(goal.m_goalName)) {
                        continue;
                    }

                    if (GuardTurnedOff(qInstance, qTemplate, goal, quiet: true) is { } line) {
                        lines.Add(line);
                    }
                }

                if (fromCommand && wizard.HasQuest(qInstance.QuestName)) {
                    var now = qTemplate.m_goals.Where(g => qInstance.IsGoalActive(g.m_goalName)).ToList();
                    if (now.Count > 0) {
                        var where = now.Select(QuestSafety.ZoneOf).FirstOrDefault(z => z.Length > 0);
                        lines.Add($"{title}: now on '{string.Join("' and '", now.Select(QuestSafety.StepTitleOf))}'{(where is null ? "" : $" ({where})")}.");
                    }
                }
            }

            if (!fromCommand) {
                foreach (var line in lines) {
                    CommandReply(line);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not check the quests of {0}: {1} {2}", Logger.Args(wizard?.CharId, ex.Message, ex.StackTrace));
        }

        return lines;
    }

    // A quest none of whose steps is active: its first steps start again, its next steps start, or (every step done) it
    // is finished. Returns what was done, or null.
    private string FixQuestWithNothingToDo(Wizard wizard, QuestInstance qInstance, QuestTemplate qTemplate) {
        var started = qInstance.GoalProgress.Any(g => g is not null && g.DoesPlayerHaveGoal());
        if (!started) {
            var first = qTemplate.m_goals.Where(g => qTemplate.m_startGoals.Contains(g.m_goalName)).ToList();
            if (first.Count == 0) {
                return null;
            }

            Logger.Information("Quest safety: {0} quest '{1}' had no step started; its first steps start.", Logger.Args(wizard.CharId, qInstance.QuestName));
            var count = first.Count(g => StartGoal(qInstance, g));
            EnsureAStepStarted(qInstance, qTemplate, first, count);
            return "it had no step to do, so it starts again from its first step.";
        }

        if (DetermineNextGoals(qTemplate, qInstance, out var next)) {
            if (next is not { Length: > 0 }) {
                return null;
            }

            Logger.Information("Quest safety: {0} quest '{1}' had no active step; next steps start ({2}).",
                Logger.Args(wizard.CharId, qInstance.QuestName, string.Join(", ", next.Select(g => (string) g.m_goalName))));
            var count = next.Count(g => StartGoal(qInstance, g));
            EnsureAStepStarted(qInstance, qTemplate, [.. next], count);
            return "it had no step to do, so it moved on to its next step.";
        }

        // Every step that started is done and the quest's logic ends here: it is finished, as it would have been.
        if (!qInstance.GoalProgress.Any(g => g is not null && g.IsGoalCompleted())) {
            return null;
        }

        Logger.Information("Quest safety: {0} quest '{1}' had every step done but wasn't finished; finishing it.",
            Logger.Args(wizard.CharId, qInstance.QuestName));
        CompleteQuest(qInstance);
        return wizard.HasQuest(qInstance.QuestName) ? null : "every step was done, so it is finished now.";
    }

    /// <summary>
    /// None of the steps a quest just moved to could start for this wizard (their requirements, such as another school,
    /// all failed): the first one starts anyway, so the quest is never left with nothing to do.
    /// </summary>
    private void EnsureAStepStarted(QuestInstance qInstance, QuestTemplate qTemplate, List<GoalTemplate> tried, int startedCount) {
        try {
            if (startedCount > 0 || tried is not { Count: > 0 } || !QuestSafety.FixStepsThatCannotStart || !QuestSafety.Covers(qTemplate)) {
                return;
            }

            var wizard = GetActiveWizard();
            if (!wizard.HasQuest(qInstance.QuestName)) {
                return;
            }

            var goal = tried.FirstOrDefault(g => g is not null && !qInstance.IsGoalCompleted(g.m_goalName));
            if (goal is null) {
                return;
            }

            Logger.Warning("Quest safety: {0} meets the requirements of none of quest '{1}' next steps ({2}); '{3}' starts anyway.",
                Logger.Args(wizard.CharId, qInstance.QuestName, string.Join(", ", tried.Select(g => (string) g.m_goalName)), goal.m_goalName));
            StartGoal(qInstance, goal, ignoreRequirements: true);
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not start a step of '{0}': {1}", Logger.Args(qInstance?.QuestName, ex.Message));
        }
    }

    /// <summary>A step just started: done for the wizard if it needs something turned off, and its used objects come back.</summary>
    private void GuardStartedGoal(QuestInstance qInstance, GoalTemplate goal) {
        try {
            if (!QuestSafety.Enabled || !qInstance.IsGoalActive(goal.m_goalName)) {
                return;
            }

            var qTemplate = GetQuestTemplate(qInstance.QuestName);
            if (!QuestSafety.Covers(qTemplate) || DungeonQuestIndex.IsDungeonQuest(qInstance.QuestName)) {
                return;
            }

            if (goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE) {
                var wizard = GetActiveWizard();
                var restored = QuestSafety.RestoreUsedLooks(wizard, (quest, goalName)
                    => string.Equals(quest, qInstance.QuestName, StringComparison.OrdinalIgnoreCase) && goalName == goal.m_goalName);
                if (restored.Count > 0) {
                    SessionActor.ActorRef.Tell(new RestoreQuestObjectLooks { Looks = [.. restored.Select(r => (r.Tag, r.State))] });
                    CommandReply($"{string.Join(", ", restored.Select(r => QuestSafety.NameOf(r.TemplateId)).Distinct())} is back for your quest '{QuestTitleOf(qTemplate)}'. If you can't see it, leave the area and come back.");
                }
            }

            if (GuardTurnedOff(qInstance, qTemplate, goal, quiet: false) is { } line) {
                CommandReply(line);
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not check step '{0}' of '{1}': {2}", Logger.Args(goal?.m_goalName, qInstance?.QuestName, ex.Message));
        }
    }

    // A step that needs a turned-off NPC, creature or object is done for the wizard (SkipTurnedOffSteps); one in a
    // turned-off zone is explained once a session. Returns the line to tell the wizard, or null.
    private string GuardTurnedOff(QuestInstance qInstance, QuestTemplate qTemplate, GoalTemplate goal, bool quiet) {
        var (why, zoneOnly) = QuestSafety.WhyTurnedOff(qInstance.QuestName, goal);
        if (why is null) {
            return null;
        }

        string title = QuestTitleOf(qTemplate);
        string step = QuestSafety.StepTitleOf(goal);
        if (!zoneOnly && QuestSafety.SkipTurnedOffSteps) {
            Logger.Information("Quest safety: {0} quest '{1}' step '{2}' done for the wizard: {3}.",
                Logger.Args(GetActiveWizard().CharId, qInstance.QuestName, goal.m_goalName, why));
            var line = $"{title}: the step '{step}' was done for you, because {why}.";
            CompleteGoal(qInstance, goal, showCompletionDialogue: false);
            return line;
        }

        var key = $"{qInstance.QuestName}|{goal.m_goalName}";
        if (quiet && !_safetyTold.Add(key)) {
            return null;
        }

        _safetyTold.Add(key);
        return zoneOnly
            ? $"{title}: the step '{step}' can't be done right now, because {why}. It waits for you until it is turned back on."
            : $"{title}: the step '{step}' can't be done, because {why}. Ask the host to turn it back on (Ahziael, Game Rules, On and Off).";
    }

    /// <summary>
    /// .stuck: every guard runs for the wizard's quests at once (used objects come back, quests with nothing to do move
    /// on, turned-off steps are done or explained), and each quest says what step it is on.
    /// </summary>
    private void CommandStuck(Wizard wizard) {
        if (!QuestSafety.Enabled) {
            CommandReply("The quest safety guards are switched off on this server ([QuestSafety] Enabled).");
            return;
        }

        _safetyTold.Clear();
        var restored = QuestSafety.RestoreUsedLooks(wizard, (quest, goal) => IsActiveUseStep(wizard, quest, goal));
        if (restored.Count > 0) {
            SessionActor.ActorRef.Tell(new RestoreQuestObjectLooks { Looks = [.. restored.Select(r => (r.Tag, r.State))] });
        }

        var lines = RepairActiveQuests(wizard, fromCommand: true);
        if (restored.Count > 0) {
            lines.Insert(0, $"Brought back for your quests: {string.Join(", ", restored.Select(r => QuestSafety.NameOf(r.TemplateId)).Distinct())} (leave the area and come back if you can't see it).");
        }

        if (lines.Count == 0) {
            CommandReply("You have no active quests.");
            return;
        }

        CommandReply("Your quests:");
        foreach (var line in lines) {
            CommandReply("  " + line);
        }
    }

}
