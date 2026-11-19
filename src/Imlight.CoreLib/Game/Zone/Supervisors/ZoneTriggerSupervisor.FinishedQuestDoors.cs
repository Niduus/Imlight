/*
 * ONE-TIME QUEST DOORS AFTER THE QUEST IS DONE
 *
 * With [World] OpenAllDoors every door goes through whatever its quest needs say. That is meant for doors a wizard has
 * not earned yet; it also kept open the doors the game uses only while one quest step is under way. Jade Palace's gate
 * to Shoshun Village holds two doors on top of each other: "WaterDojoTrigger" (to the Water Dojo copy where the first
 * MooShu fight is, only while "MS-MAIN-C02-002" Goal 2 or Goal 3 is under way) and "ToPlagueHub" (to Shoshun Village,
 * always). The dojo door is the first one a wizard walks into, so with OpenAllDoors it took everybody back to that fight
 * for good, also long after the quest was done, and Shoshun Village could not be reached (a player's report, Niduus
 * 2026-11-19: stuck on "Talk to Sun Hing in Shoshun Village").
 *
 * A door whose needs are not met, and whose needs only ask for steps of quests the wizard has already finished (and
 * does not hold again), now stays shut as in the real game, also with OpenAllDoors. The game files are not changed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    // Doors already reported once in this zone, so the log is not filled each time a wizard walks past.
    private readonly HashSet<string> _finishedQuestDoorsLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a door that OpenAllDoors would let through belongs only to quest steps the wizard is long past: its needs
    /// are not met and every quest step it asks to be under way is of a quest the wizard has finished and does not hold.
    /// </summary>
    private bool IsFinishedQuestDoor(Trigger trigger, ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        // A dungeon copy runs its own quests afresh each time (CopyJudgesQuest); those doors stay as they were.
        if (Zone.IsInstance || message.PlayerActor is null || trigger?.m_requirements?.m_requirements is not { Count: > 0 }) {
            return false;
        }

        if (Core.ZoneTrigger.HasUndecodableRequirement(trigger.m_requirements)) {
            return false;
        }

        var quests = new HashSet<string>(StringComparer.Ordinal);
        if (!CollectUnderWaySteps(trigger.m_requirements, quests) || quests.Count == 0) {
            return false;
        }

        var wizard = Core.ZoneTrigger.QueryWizard(message.PlayerActor);
        if (wizard is null) {
            return false;
        }

        foreach (var quest in quests) {
            var name = WizardData.QuestCodeAliases.Resolve(quest);
            if (string.IsNullOrEmpty(name) || wizard.HasQuest(name) || !wizard.HasCompletedQuest(name)) {
                return false;
            }
        }

        if (EvaluateRequirements(trigger, message)) {
            return false;
        }

        if (_finishedQuestDoorsLogged.Add(trigger.m_triggerName)) {
            Logger.Information("Zone {0}: door {1} is only for a step of {2}, which {3} already finished; it stays shut as in the game, also with OpenAllDoors.",
                Logger.Args(Zone.ZonePath, trigger.m_triggerName, string.Join(", ", quests), wizard.CharId));
        }

        return true;
    }

    /// <summary>
    /// Gathers the quests whose steps a door's needs ask to be under way (ReqHasGoal, not negated, not "Complete").
    /// False when the needs hold anything else (a negated list, another kind of need), so only plain step doors count.
    /// </summary>
    private static bool CollectUnderWaySteps(RequirementList list, HashSet<string> quests) {
        if (list?.m_requirements is null || list.m_applyNOT) {
            return false;
        }

        foreach (var requirement in list.m_requirements) {
            switch (requirement) {
                case RequirementList nested:
                    if (!CollectUnderWaySteps(nested, quests)) {
                        return false;
                    }
                    break;
                case ReqHasGoal { m_applyNOT: false } goal
                    when goal.m_requiredStatus == GoalStatusRequirement.Incomplete && !string.IsNullOrEmpty(goal.m_questName):
                    quests.Add(goal.m_questName);
                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
