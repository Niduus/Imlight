/*
 * ========================================================================
 * QUEST SAFETY GUARDS
 * ========================================================================
 *
 * PURPOSE:
 * Keeps a quest from leaving a wizard stuck or giving its rewards twice (Niduus, 2026-10-31: "Add safety guards for side
 * quests"). The guards, each a switch in the [QuestSafety] section of Imlight.ini, read live:
 *   FixStepsThatCannotStart  a step whose requirements the wizard doesn't meet (a step for another school) is still
 *                            started when it is the only way on, so the quest never ends up with nothing to do. At
 *                            every zone load a quest with no step left to do moves on or is finished, and a quest that
 *                            got new steps since it was started gets them.
 *   SkipTurnedOffSteps       a step whose NPC, creature or object is turned off (Game Rules > On and Off) is done for
 *                            the wizard, with a message. A step in a turned-off zone or world is only explained, since
 *                            a zone is usually off for a while only.
 *   RestoreUsedObjects       a quest object that took its used look for the wizard (the Crystal Ball shattered) comes
 *                            back when a later step, or the same quest taken again, needs it.
 *   RewardsOnce              a quest removed and taken again (Ahziael's Start, .quest remove, .toquest) gives no second
 *                            gold, XP or items for the steps it already rewarded (Secret Strife of Pets gave a second
 *                            pet). Repeatable quests (Quest Editor) and dungeon quests keep their own rules.
 *   BringBackHiddenObjects   an NPC or object a quest hides only while part of it runs (Watson in Weird Science) comes
 *                            back once that part is over, even when the quest was finished another way (QuestHiddenObjects).
 *   SideQuestsOnly           the guards look after side quests only (off: every quest, main ones too).
 * Players can type .stuck to run every guard at once and see what each of their quests waits on.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/31/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Quests;

internal static class QuestSafety {

    private const string SECTION = "QuestSafety";

    internal const string START_STEP = "start";
    internal const string END_STEP = "end";
    private const string ACTIVATE_PREFIX = "+";
    private const string ADJECTIVE_SUFFIX = ".AdjRef";
    private const int MAX_USED_LOOKS = 500;

    private static bool Get(string key, bool defaultValue) => ConfigurationManager.GetValue($"{SECTION}.{key}", defaultValue);

    internal static bool Enabled => Get("Enabled", true);
    internal static bool FixStepsThatCannotStart => Enabled && Get("FixStepsThatCannotStart", true);
    internal static bool SkipTurnedOffSteps => Enabled && Get("SkipTurnedOffSteps", true);
    internal static bool RestoreUsedObjects => Enabled && Get("RestoreUsedObjects", true);
    internal static bool RewardsOnce => Enabled && Get("RewardsOnce", true);
    internal static bool BringBackHiddenObjects => Enabled && Get("BringBackHiddenObjects", true);
    internal static bool SideQuestsOnly => Get("SideQuestsOnly", false);

    /// <summary>Whether the guards look after this quest.</summary>
    internal static bool Covers(QuestTemplate quest)
        => quest is not null && Enabled && (!SideQuestsOnly || !quest.m_mainline);

    /// <summary>The name of an activated step in the reward record ("+Goal 2").</summary>
    internal static string ActivateStep(string goalName) => ACTIVATE_PREFIX + goalName;

    // ---------------------------------------------------------------------
    // Rewards once
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes down what a quest's run already rewarded, just before that run is removed or started over: everything when
    /// it was finished, else its start and the steps that started or were done.
    /// </summary>
    internal static void RememberRewardsBeforeReset(Wizard wizard, string questName) {
        try {
            if (wizard is null || string.IsNullOrEmpty(questName)) {
                return;
            }

            var quest = QuestTemplateCollection.GetQuestByName(questName);
            if (quest is null || QuestOptions.IsRepeatable(quest.m_questName) || DungeonQuestIndex.IsDungeonQuest(quest.m_questName)) {
                return;
            }

            string name = quest.m_questName;
            var steps = new List<string>();
            if (wizard.HasCompletedQuest(name)) {
                steps.Add(START_STEP);
                steps.Add(END_STEP);
                foreach (var goal in quest.m_goals ?? []) {
                    steps.Add(goal.m_goalName);
                    steps.Add(ActivateStep(goal.m_goalName));
                }
            }

            var active = wizard.QuestBehavior?.CurrentQuestInstances?
                .FirstOrDefault(q => q is not null && string.Equals(q.QuestName, name, StringComparison.OrdinalIgnoreCase));
            if (active is not null) {
                steps.Add(START_STEP);
                foreach (var progress in active.GoalProgress ?? []) {
                    if (progress is null || !progress.DoesPlayerHaveGoal()) {
                        continue;
                    }

                    steps.Add(ActivateStep(progress.GoalName));
                    if (progress.IsGoalCompleted()) {
                        steps.Add(progress.GoalName);
                    }
                }
            }

            if (steps.Count == 0) {
                return;
            }

            wizard.QuestRewardsGiven ??= [];
            if (!wizard.QuestRewardsGiven.TryGetValue(name, out var given)) {
                given = [];
                wizard.QuestRewardsGiven[name] = given;
            }

            var before = given.Count;
            foreach (var step in steps.Where(s => !string.IsNullOrEmpty(s) && !given.Contains(s))) {
                given.Add(step);
            }

            if (given.Count != before) {
                WizardCollection.UpdateCharacterQuestRewardsGiven(wizard);
                Logger.Information("Quest safety: {0} quest '{1}' is being reset; {2} rewarded steps noted so they don't reward again.",
                    Logger.Args(wizard.CharId, name, given.Count));
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not note the rewards of '{0}': {1}", Logger.Args(questName, ex.Message));
        }
    }

    /// <summary>
    /// The results of a quest step without its gold, XP and items when an earlier run of the quest already gave them
    /// (RewardsOnce). Everything else (doors, teleports, dialog, the quest's flags) still runs.
    /// </summary>
    internal static ResultList WithoutRepeatRewards(Wizard wizard, string questName, string step, ResultList results, out bool removed) {
        removed = false;
        if (results?.m_results is not { Count: > 0 } list || wizard?.QuestRewardsGiven is not { Count: > 0 } ledger
            || string.IsNullOrEmpty(questName) || !RewardsOnce
            || !ledger.TryGetValue(questName, out var given) || !given.Contains(step)
            || QuestOptions.IsRepeatable(questName) || DungeonQuestIndex.IsDungeonQuest(questName)
            || !Covers(QuestTemplateCollection.GetQuestByName(questName))) {
            return results;
        }

        var kept = list.Where(r => r is not (ResDropTable or ResAddGold)).ToList();
        if (kept.Count == list.Count) {
            return results;
        }

        removed = true;
        Logger.Information("Quest safety: {0} quest '{1}' step '{2}' already rewarded on an earlier run; {3} reward results skipped.",
            Logger.Args(wizard.CharId, questName, step, list.Count - kept.Count));

        return new ResultList { m_results = kept };
    }

    // ---------------------------------------------------------------------
    // Turned-off NPCs, creatures, objects and zones
    // ---------------------------------------------------------------------

    /// <summary>What a turned-off thing keeps a step from (null when nothing does), and whether it is a zone (only explained).</summary>
    internal static (string Why, bool ZoneOnly) WhyTurnedOff(string questName, GoalTemplate goal) {
        try {
            if (goal is null) {
                return (null, false);
            }

            switch (goal) {
                case PersonaGoalTemplate persona: {
                    var template = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                    if (TurnedOff.IsTemplateOff(template)) {
                        return ($"{NameOf(template)} is turned off on this server", false);
                    }

                    // Talking counts in any zone, so the step's zone doesn't matter.
                    return (null, false);
                }
                case BountyGoalTemplate bounty:
                    if (!QuestOptions.AnyCreatureCounts(questName, goal.m_goalName) && OffFoe(bounty) is { } foe) {
                        return ($"{NameOf(foe)}, the creature to defeat, is turned off on this server", false);
                    }

                    break;
                default:
                    if (goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE && OffObject(questName, goal) is { } item) {
                        return ($"{NameOf(item)}, the object to use, is turned off on this server", false);
                    }

                    break;
            }

            var zone = ZoneOf(goal);
            if (zone.Length > 0 && TurnedOff.IsZoneOff(zone)) {
                return ($"the place it happens ({zone}) is turned off on this server for now", true);
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not check step '{0}' of '{1}': {2}", Logger.Args(goal?.m_goalName, questName, ex.Message));
        }

        return (null, false);
    }

    // A "defeat" step that names its creature by that creature's own adjective (".AdjRef") and every such creature is off.
    private static ulong? OffFoe(BountyGoalTemplate bounty) {
        if (TurnedOff.OffTemplates.Count == 0) {
            return null;
        }

        var wanted = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToList();
        if (wanted.Count == 0 || !wanted.All(a => a.EndsWith(ADJECTIVE_SUFFIX, StringComparison.OrdinalIgnoreCase))) {
            return null;
        }

        ulong first = 0;
        foreach (var adjective in wanted) {
            var id = CoreObjectFactory.IdOfFileName(adjective[..^ADJECTIVE_SUFFIX.Length]);
            if (id == 0 || !TurnedOff.IsTemplateOff(id)) {
                return null;
            }

            first = first == 0 ? id : first;
        }

        return first == 0 ? null : first;
    }

    // A "use" step whose every object is turned off.
    private static ulong? OffObject(string questName, GoalTemplate goal) {
        foreach (var id in TurnedOff.OffTemplates) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate template) {
                continue;
            }

            var named = InteractQuestSelectComponent.UsageGoalsOf(template)
                .Any(g => string.Equals(g.Quest, questName, StringComparison.OrdinalIgnoreCase) && g.Goal is not null
                    && string.Equals((string) g.Goal.m_goalName, (string) goal.m_goalName, StringComparison.Ordinal));
            if (named && !HasOtherObject(questName, goal, id)) {
                return id;
            }
        }

        return null;
    }

    // Whether some object that is not turned off still counts for the step (one of its client tags names another template).
    private static bool HasOtherObject(string questName, GoalTemplate goal, ulong offTemplate) {
        foreach (var tag in goal.m_clientTags ?? []) {
            var id = CoreObjectFactory.IdOfFileName((string) tag);
            if (id != 0 && id != offTemplate && !TurnedOff.IsTemplateOff(id)) {
                return true;
            }
        }

        return false;
    }

    // The zone a step happens in: a "go to" step's zone, else its destination zone, as the server names it ("" if none).
    internal static string ZoneOf(GoalTemplate goal) {
        string tag = goal is WaypointGoalTemplate waypoint ? (string) waypoint.m_zoneTag : null;
        string raw = string.IsNullOrEmpty(tag) ? (string) goal.m_destinationZone ?? "" : tag;
        raw = raw.Trim();
        if (raw.Length == 0) {
            return "";
        }

        return AccessPassManager.GetContainedZoneName(raw) ?? raw;
    }

    internal static string NameOf(ulong templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template) {
                string key = template.m_displayName;
                var name = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);
                return string.IsNullOrWhiteSpace(name) ? (string) template.m_objectName ?? templateId.ToString() : name.Trim();
            }
        }
        catch (Exception) {
            // Named by its number below.
        }

        return templateId.ToString();
    }

    /// <summary>A step's title as the quest book shows it, or its name.</summary>
    internal static string StepTitleOf(GoalTemplate goal) {
        string key = goal?.m_goalTitle;
        var title = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);
        return string.IsNullOrWhiteSpace(title) ? (string) goal?.m_goalName ?? "" : title.Trim();
    }

    // ---------------------------------------------------------------------
    // Used quest objects
    // ---------------------------------------------------------------------

    /// <summary>Remembers that a quest object took its used look for the wizard.</summary>
    internal static void NoteUsedLook(Wizard wizard, string zone, string tag, ulong templateId, string state) {
        try {
            wizard.UsedQuestLooks ??= [];
            wizard.UsedQuestLooks.RemoveAll(l => l is null || string.Equals(l.Tag, tag, StringComparison.Ordinal));
            wizard.UsedQuestLooks.Add(new UsedQuestLook { Zone = zone, Tag = tag, TemplateId = templateId, State = state });
            if (wizard.UsedQuestLooks.Count > MAX_USED_LOOKS) {
                wizard.UsedQuestLooks.RemoveRange(0, wizard.UsedQuestLooks.Count - MAX_USED_LOOKS);
            }

            WizardCollection.UpdateCharacterUsedQuestLooks(wizard);
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not note a used quest object: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>
    /// Takes the used look off every object the wizard's active "use" steps need again (and that still has that look).
    /// Returns each object's tag and the look it goes back to; the caller tells the client.
    /// </summary>
    internal static List<(string Tag, string State, ulong TemplateId)> RestoreUsedLooks(Wizard wizard, Func<string, string, bool> isActiveUseStep) {
        var restored = new List<(string, string, ulong)>();
        if (!RestoreUsedObjects || wizard?.UsedQuestLooks is not { Count: > 0 } looks || wizard.QuestBehavior?.CurrentQuestInstances is null) {
            return restored;
        }

        var changed = false;
        try {
            foreach (var look in looks.ToList()) {
                if (look is null || string.IsNullOrEmpty(look.Tag)) {
                    continue;
                }

                // Gone already: the quest or the player took the look off some other way.
                var mod = wizard.DynamodSet?.Dynamods?.FirstOrDefault(d => d is not null && string.Equals(d.ClientTag, look.Tag, StringComparison.Ordinal));
                if (mod is null || !string.Equals(mod.ModState, look.State, StringComparison.Ordinal)) {
                    looks.Remove(look);
                    changed = true;
                    continue;
                }

                if (CoreObjectFactory.GetCoreTemplate(look.TemplateId) is not GameObjectTemplate template) {
                    continue;
                }

                var needed = InteractQuestSelectComponent.UsageGoalsOf(template).Any(g => g.Goal is not null && isActiveUseStep(g.Quest, g.Goal.m_goalName)
                    && Covers(QuestTemplateCollection.GetQuestByName(g.Quest)));
                if (!needed) {
                    continue;
                }

                wizard.RemoveDynamod(look.Tag);
                looks.Remove(look);
                changed = true;
                restored.Add((look.Tag, UsedQuestObjects.StartState(template), look.TemplateId));
                Logger.Information("Quest safety: {0} needs '{1}' ({2}) again for a step; its used look is taken off.",
                    Logger.Args(wizard.CharId, look.Tag, look.TemplateId));
            }

            if (changed) {
                WizardCollection.UpdateCharacterUsedQuestLooks(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Error("Quest safety could not bring quest objects back: {0}", Logger.Args(ex.Message));
        }

        return restored;
    }

}
