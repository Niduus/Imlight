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
 * INTERACT QUEST SELECT COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Handles a quest goal where the player's goal is to interact with a
 * specific object in the game world.
 *
 * USAGE EXAMPLE:
 *
 * NOTE:
 * Collection goals (tally count > 1) consume the object on use; single-use goals
 * leave the object in place and drive post-use state via completeResults.
 * A usage goal's client tags name the object, or a goal tag of one of its interact
 * options (Ddl_WC_DarkCave_Bubble1 on WC_DarkCave_Bubble1), or a global registry entry the option's
 * results remove (a staff whose use removes the entry its goal names).
 *
 * TODO:
 *
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractQuestSelectComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName => "Interact";
    public string NpcIcon { get; private set; } = null;
    public string NpcNameKey { get; private set; } = null;
    // The object's own text (m_interactTextOverride), else chest text for a chest, else none: the memento then shows the
    // plain object "Interact" text. Every quest object said chest text, so Last Wood's fire pits offered the smoke signal
    // step with a chest's window (2026-11-25).
    public string NpcTextKey { get; private set; }
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;
    public float DEFAULT_INTERACTION_RADIUS => 300f;

    private readonly Dictionary<string, List<GoalTemplate>> _usageGoalsByQuest = [];

    // The wizards who picked this collection object up (a gemstone, a cog): it is gone for them and stays for everyone else.
    private readonly HashSet<ulong> _collectedBy = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
           && (HasBehavior(template, "WizardSelectBehavior")
               || IsNamedByAnyUsageGoal(gameObjectTemplate)
               || string.Equals(gameObjectTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw", StringComparison.OrdinalIgnoreCase)
               || string.Equals(gameObjectTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw instance", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (playerCharacter?.QuestBehavior?.CurrentQuestInstances == null) {
            yield break;
        }

        var hasMawUseStep = playerCharacter.QuestBehavior.CurrentQuestInstances.Any(q =>
            string.Equals(q.QuestName, "AZ-THREE-C01-002", StringComparison.OrdinalIgnoreCase)
            && q.GoalProgress.Any(g =>
                g.GoalName == "1_WizardQuestGoalsUseItem"
                && g.DoesPlayerHaveGoal()
                && !g.IsGoalCompleted()));
        if (hasMawUseStep
            && Entity.Template is GameObjectTemplate mawTemplate
            && (string.Equals(mawTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mawTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw instance", StringComparison.OrdinalIgnoreCase))) {
            yield return new InteractableOption { m_serviceName = ServiceName };
        }

        // If we did not find any quests with active usage goals, don't show the interaction option at all.
        if (_usageGoalsByQuest.Count == 0) {
            yield break;
        }

        // If there are no quests with active usage goals, don't show the interaction option at all.
        var questsWithActiveUsageGoals = GetQuestsWithActiveUsageGoals(playerCharacter);
        if (questsWithActiveUsageGoals is null || questsWithActiveUsageGoals.Count == 0) {
            yield break;
        }

        // Only show interaction option if player has an active usage goal that matches this object. An object already
        // used (the staff stands in the Throne Room of Fire's map platform) stays usable for a wizard whose own step
        // still needs it: its state is shared by the whole instance, so the step that came later, or a teammate's,
        // could never be done (Niduus, 2026-11-04).
        if (!_collectedBy.Contains(playerCharacter.CharId) && HasActiveMatchingUsageGoal(playerCharacter)) {
            yield return new InteractableOption { m_serviceName = ServiceName };
        }
    }

    public override void OnStart() {
        if (Entity.Template is not GameObjectTemplate gameObjectTemplate) {
            return;
        }

        // Determine icon and name key from template.
        NpcIcon = gameObjectTemplate.m_sIcon;
        NpcNameKey = gameObjectTemplate.m_displayName;
        var ownText = gameObjectTemplate.m_behaviors?
            .OfType<InteractableBehaviorTemplate>()
            .Select(behavior => behavior?.m_interactTextOverride?.ToString())
            .FirstOrDefault(text => !string.IsNullOrEmpty(text));
        NpcTextKey = ownText
            ?? (gameObjectTemplate.m_objectName.ToString().Contains("Chest", StringComparison.OrdinalIgnoreCase)
                ? "GUI_ChestInteract"
                : null);

        // Register every usage goal whose client tags name this object; the object is
        // interactable only while one of those goals is active (see GetServiceOptions).
        foreach (var (questName, goal) in UsageGoals.Of(gameObjectTemplate)) {
            if (!_usageGoalsByQuest.TryGetValue(questName, out var goalList)) {
                goalList = [];
                _usageGoalsByQuest[questName] = goalList;
            }
            goalList.Add(goal);
        }
    }

    private static bool C14MagicRaftAtZocalo(string zone, string name, string tag)
        => string.Equals(zone, "Azteca/AZ_Z00_Zocalo", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(name) && name.Contains("MagicRaft", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(tag) && tag.Contains("AZ-MagicRaft", StringComparison.OrdinalIgnoreCase));

    private static bool C14MagicRaftAtZultunDock(string zone, string name, string tag)
        => string.Equals(zone, "Azteca/AZ_Z05_ZultunDock", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(name) && (name.Contains("MagicRaft", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ZULTUNDOCK", StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(tag) && (tag.Contains("MagicRaft", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("ZULTUNDOCK", StringComparison.OrdinalIgnoreCase)));

    private static void CompleteC14Usage(IActorRef playerActor, Wizard playerCharacter) {
        if (playerCharacter?.QuestBehavior?.CurrentQuestInstances is null) {
            return;
        }

        var quest = playerCharacter.QuestBehavior.CurrentQuestInstances.FirstOrDefault(q =>
            string.Equals(q.QuestName, "AZ-ZOCA-C14-001", StringComparison.OrdinalIgnoreCase));
        var goal = quest?.GoalProgress.FirstOrDefault(g =>
            g.GoalName == "1_WizardQuestGoalsUseItem"
            && g.DoesPlayerHaveGoal()
            && !g.IsGoalCompleted());
        if (quest is not null && goal is not null) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
                QuestID = quest.ID,
                GoalID = goal.ID,
                SuppressCompletionDialog = false,
            });
        }
    }

    private static bool C10PetalDoor(string zone, string name, string tag)
        => string.Equals(zone, "Azteca/AZ_Z09_AltoAlto", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(name) && name.Contains("PetalDoor", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(tag) && tag.Contains("PetalDoor-Z09", StringComparison.OrdinalIgnoreCase));

    private static void CompleteC10Ritual(IActorRef playerActor, Wizard playerCharacter) {
        if (playerCharacter?.QuestBehavior?.CurrentQuestInstances is null) {
            return;
        }

        var quest = playerCharacter.QuestBehavior.CurrentQuestInstances.FirstOrDefault(q =>
            string.Equals(q.QuestName, "AZ-ALTO-C10-001", StringComparison.OrdinalIgnoreCase));
        var goal = quest?.GoalProgress.FirstOrDefault(g =>
            g.GoalName == "1_PerformRitual"
            && g.DoesPlayerHaveGoal()
            && !g.IsGoalCompleted());
        if (quest is not null && goal is not null) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
                QuestID = quest.ID,
                GoalID = goal.ID,
                SuppressCompletionDialog = false,
            });
        }
    }

    private static void CompleteCloudPillarUsage(IActorRef playerActor, Wizard playerCharacter) {
        if (playerCharacter?.QuestBehavior?.CurrentQuestInstances is null) {
            return;
        }

        var quest = playerCharacter.QuestBehavior.CurrentQuestInstances.FirstOrDefault(q =>
            string.Equals(q.QuestName, "AZ-CLOUD-C02-003", StringComparison.OrdinalIgnoreCase));
        var goal = quest?.GoalProgress.FirstOrDefault(g =>
            g.GoalName == "1_WizardQuestGoalsUseItem"
            && g.DoesPlayerHaveGoal()
            && !g.IsGoalCompleted());
        if (quest is not null && goal is not null) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
                QuestID = quest.ID,
                GoalID = goal.ID,
                SuppressCompletionDialog = false,
            });
        }
    }

    private static void RaiseCloudPillar(IActorRef zoneActor, IActorRef playerActor, CoreObject playerObject) {
        zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = "AZ-SkyPillarRise",
            PlayerActor = playerActor,
            PlayerGameObject = playerObject,
        });

        foreach (var tag in new[] { "AZ-Sky-PillarTeleport", "AZ-Sky-PillarTeleport instance" }) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_REMOVEDYNAMOD {
                DynaMod = new ResRemoveDynaMod {
                    m_dynaModClientTag = tag,
                },
                ContextActor = playerActor,
            });
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var zone = Entity.Zone?.ZonePath;
        var name = (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();
        var tag = (string?)Entity.Info?.m_zoneTag;
        if (C10PetalDoor(zone, name, tag)) {
            CompleteC10Ritual(playerActor, playerCharacter);
            Entity.GetComponentOfType<InteractObjectStateComponent>()?.ApplyGoalOptions(playerActor, playerObject);
            playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = "Azteca/AZ_Z10_FloatingMountains",
                DestinationLocation = "Start",
                SendToClient = true,
                ByZoneDoor = true,
                OwnerCharId = playerCharacter.CharId,
            });
            return;
        }
        if (C14MagicRaftAtZocalo(zone, name, tag)) {
            CompleteC14Usage(playerActor, playerCharacter);
            playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = "Azteca/AZ_Z05_ZultunDock",
                DestinationLocation = "Start",
                SendToClient = true,
                ByZoneDoor = true,
                OwnerCharId = playerCharacter.CharId,
            });
            return;
        }
        if (C14MagicRaftAtZultunDock(zone, name, tag)) {
            playerActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = "Azteca/AZ_Z00_Zocalo",
                DestinationLocation = "Start",
                SendToClient = true,
                ByZoneDoor = true,
                OwnerCharId = playerCharacter.CharId,
            });
            return;
        }
        if (string.Equals(zone, "Azteca/AZ_Z06_CloudburstIsland", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrEmpty(name) && name.Contains("Cloud-Bearer-Pedestal", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(tag) && tag.Contains("Cloud-Bearer-Pedestal", StringComparison.OrdinalIgnoreCase))) {
            CompleteCloudPillarUsage(playerActor, playerCharacter);
            RaiseCloudPillar(Entity.ZoneRef, playerActor, playerObject);
        }

        var mawQuest = playerCharacter?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q =>
            string.Equals(q.QuestName, "AZ-THREE-C01-002", StringComparison.OrdinalIgnoreCase));
        var mawGoal = mawQuest?.GoalProgress?.FirstOrDefault(g =>
            g.GoalName == "1_WizardQuestGoalsUseItem"
            && g.DoesPlayerHaveGoal()
            && !g.IsGoalCompleted());
        if (mawQuest is not null
            && mawGoal is not null
            && Entity.Template is GameObjectTemplate mawTemplate
            && (string.Equals(mawTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mawTemplate.m_objectName.ToString(), "AZ_Rubble-TheMaw instance", StringComparison.OrdinalIgnoreCase))) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
                QuestID = mawQuest.ID,
                GoalID = mawGoal.ID,
                SuppressCompletionDialog = false,
            });
            return;
        }

        // One use completes every active usage goal that matches this object, across all of the
        // player's quests, each through the normal goal-completion path.
        var activeGoals = FindActiveMatchingGoals(playerCharacter);
        if (activeGoals.Count == 0 || (playerCharacter is not null && _collectedBy.Contains(playerCharacter.CharId))) {
            return;
        }

        var shownDialogs = new HashSet<string>();
        foreach (var (quest, goal, goalProgress) in activeGoals) {
            // Two goals that share a completion dialog show it once.
            var suppressDialog = false;
            var willComplete = goalProgress.CurrentProgress + 1 >= (goal.m_tallyCounter?.m_count ?? 1);
            var dialogKey = CompletionDialogKey(goal);
            if (willComplete && dialogKey is not null) {
                suppressDialog = !shownDialogs.Add(dialogKey);
            }

            // Route the use through the quest service: it increments the tally, reports the
            // new count to the client (progress SENDGOAL), and completes the goal at the cap.
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_COMPLETEUSAGEGOAL {
                QuestID = quest.ID,
                GoalID = goalProgress.ID,
                SuppressCompletionDialog = suppressDialog,
            });
        }

        // The object's own options (a state such as "RedDown", posted events such as "InsertCrystalRed")
        // run when it is used for the goal.
        // Once only: a second use of an object already in its used state only counts for the steps.
        var objectState = Entity.GetComponentOfType<InteractObjectStateComponent>();
        if (objectState?.IsInGoalOptionState() != true) {
            objectState?.ApplyGoalOptions(playerActor, playerObject);
        }

        var goalMax = activeGoals.Max(g => g.Goal.m_tallyCounter?.m_count ?? 1);

        // Collection goals (tally count > 1, e.g. the Triton cogs) consume the object:
        // each use takes that instance away. Single-use objects (levers,
        // fairy cages) persist and manage their own post-use state via the goal's
        // completeResults (dyna-mods).
        // Only for the wizard who picked it up, as in the live game: every other wizard on the quest still finds it there.
        // Taken from the whole area, a gemstone in Krokotopia's Hall of Champions ("Get Some Bling") came back only when
        // the area emptied, which a busy area never does: with fifteen wizards there, no gem was left for 45 minutes and
        // nobody could finish the step (Niduus's log, 2026-11-20 20:45-21:30).
        if (goalMax > 1 && !InteractObjectStateComponent.KeepsStateAfterGoalUse(Entity.Template as GameObjectTemplate)) {
            var leaveServiceRangeMsg = new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE {
                MobileID = Entity.ActiveGameObject.m_globalID.Full
            };
            playerActor.Tell(leaveServiceRangeMsg);

            if (playerCharacter is not null) {
                _collectedBy.Add(playerCharacter.CharId);
            }

            Entity.DeleteObjectFor(playerActor);
        }
        else {
            ShowUsedLook(playerActor, activeGoals);
        }
    }

    // A used quest object takes its used look for this wizard and keeps it (UsedQuestObjects): the bubble pops, the
    // crystal ball shatters. Left to the quest when one of its goals already changes the object itself.
    private void ShowUsedLook(IActorRef playerActor, List<(QuestInstance Quest, GoalTemplate Goal, GoalInstance GoalProgress)> activeGoals) {
        if (Entity.Info?.m_zoneTag is not { Length: > 0 } tag
            || UsedQuestObjects.UsedState(Entity.Template as GameObjectTemplate) is not { } usedState
            || InteractObjectStateComponent.KeepsStateAfterGoalUse(Entity.Template as GameObjectTemplate)) {
            return;
        }

        var questChangesIt = activeGoals.Any(g => g.Goal.m_completeResults?.m_results?.Any(r =>
            (r is ResAddDynaMod add && (string) add.m_dynaModClientTag == tag)
            || (r is ResRemoveDynaMod remove && (string) remove.m_dynaModClientTag == tag)) == true);
        if (questChangesIt) {
            return;
        }

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_ADDDYNAMOD {
            DynaMod = new ResAddDynaMod {
                m_dynaModClientTag = tag,
                m_dynaModState = usedState,
                m_zoneName = Entity.Zone?.ZonePath,
            },
            ContextActor = playerActor,
            // Remembered, so a later step that needs this object again can bring it back (Quests/QuestSafety).
            UsedLookTemplateId = (Entity.Template as GameObjectTemplate)?.m_templateID ?? 0,
        });
    }

    private bool HasActiveMatchingUsageGoal(Wizard playerCharacter)
        => FindActiveMatchingGoals(playerCharacter).Count > 0;

    private List<(QuestInstance Quest, GoalTemplate Goal, GoalInstance GoalProgress)> FindActiveMatchingGoals(Wizard playerCharacter) {
        var result = new List<(QuestInstance, GoalTemplate, GoalInstance)>();
        var questsWithActiveUsageGoals = GetQuestsWithActiveUsageGoals(playerCharacter);
        if (questsWithActiveUsageGoals == null) {
            return result;
        }

        // Stable order (quest id, then the goal's order in its template) so dialogs play in the same order every time.
        foreach (var quest in questsWithActiveUsageGoals.OrderBy(q => q.ID)) {
            if (!_usageGoalsByQuest.TryGetValue(quest.QuestName, out var goals)) {
                continue;
            }

            foreach (var goal in goals) {
                if (string.Equals(quest.QuestName, "AZ-SWAMP-C01-001", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(goal.m_goalName, "3_WizardQuestGoals00000006", StringComparison.OrdinalIgnoreCase)) {
                    var zone = Entity.Zone?.ZonePath;
                    var objectName = (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();
                    if (!string.Equals(zone, "Azteca/Interiors/AZ_Z04_DeadTreeCavern", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(objectName, "AZ-SWAMP-NPC01", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(objectName, "AZ-SWAMP-NPC01Projection", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(objectName, "AZ-SWAMP-Jade-Wind-Sun", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                }

                var goalProgress = quest.GoalProgress.FirstOrDefault(gp =>
                    IsActiveUsageGoal(gp, goal.m_goalName));

                if (goalProgress != null) {
                    result.Add((quest, goal, goalProgress));
                }
            }
        }

        return result;
    }

    private static string CompletionDialogKey(GoalTemplate goal) {
        var dialog = (goal.m_dialogList as ActorDialogList)?.m_dialogs?.FirstOrDefault(d => d.m_dialogTag == "Completion");
        return dialog?.m_dialogEntries is null
            ? null
            : string.Join("|", dialog.m_dialogEntries.Select(e => e.m_dialog));
    }

    private static List<QuestInstance> GetQuestsWithActiveUsageGoals(Wizard playerCharacter)
        => playerCharacter.QuestBehavior?.CurrentQuestInstances?
            .Where(quest => quest.GoalProgress.Any(gp =>
                IsActiveUsageGoal(gp, null)))
            .ToList();

    private static bool IsActiveUsageGoal(GoalInstance goalProgress, string goalName)
        => goalProgress.GoalType == GOAL_TYPE.GOAL_TYPE_USAGE
               && goalProgress.CurrentProgress > -1
               && goalProgress.CurrentProgress != int.MaxValue
               && (goalName == null || goalProgress.GoalName == goalName);

    private static bool HasBehavior(CoreTemplate template, string behaviorName)
        => template.m_behaviors.Any(x => x is not null && x.m_behaviorName == behaviorName);

    private static bool IsNamedByAnyUsageGoal(GameObjectTemplate gameObjectTemplate)
        => UsageGoals.Names(gameObjectTemplate);

    /// <summary>
    /// The tags a usage goal can name an interact option by: the option's goal tags, and the
    /// global registry entries its results remove (a staff whose option removes the entry its goal's client tag names).
    /// </summary>
    internal static IEnumerable<string> OptionTags(InteractOptionTemplate option) {
        if (option is null) {
            yield break;
        }

        foreach (var tag in option.m_goalTags ?? []) {
            yield return tag;
        }

        if (option is not InteractStateOptionTemplate { m_results.m_results: { } results }) {
            yield break;
        }

        foreach (var removal in results.OfType<ResRemoveEntry>().Where(r => !r.m_isQuestRegistry && !string.IsNullOrEmpty(r.m_entryName))) {
            yield return removal.m_entryName;
        }
    }

    /// <summary>
    /// True when some usage goal names this option by one of its tags, so using the option is using that goal.
    /// </summary>
    internal static bool IsNamedByUsageGoal(InteractOptionTemplate option)
        => UsageGoals.Names(option);

    internal static Vector3? ActiveGoalSpot(ZoneEntity entity, Wizard wizard) {
        if (entity?.Zone?.ZoneData?.m_objectList is null || wizard is null) {
            return null;
        }

        foreach (var quest in wizard.QuestBehavior?.CurrentQuestInstances?.ToList() ?? []) {
            if (!string.Equals(quest.QuestName, "AZ-ZOCA-C11-002", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var active = quest.GoalProgress;
            string tag = quest.IsGoalActive("2_WizardQuestGoals00000006") ? "AZ-3POINTS-NPC14"
                : quest.IsGoalActive("3_WizardQuestGoals00000006") ? "AZ-PalaceGuard02"
                : quest.IsGoalActive("4_WizardQuestGoals00000006") ? "AZ-PalaceGuard03"
                : quest.IsGoalActive("5_WizardQuestGoals00000006") ? "AZ-PalaceGuard04"
                : null;

            if (string.IsNullOrEmpty(tag)) {
                continue;
            }

            var placed = entity.Zone.ZoneData.m_objectList.FirstOrDefault(o =>
                string.Equals(NormalizeInstanceTag((string?)o?.m_zoneTag), tag, StringComparison.OrdinalIgnoreCase));
            if (placed is not null) {
                return placed.m_location;
            }
        }

        return null;
    }

    private static string NormalizeInstanceTag(string tag)
        => tag?.EndsWith(" instance", StringComparison.OrdinalIgnoreCase) == true
            ? tag[..^" instance".Length]
            : tag;

    /// <summary>The usage goals that using this object counts for (the quest checker asks, QuestChecker.cs).</summary>
    internal static IEnumerable<(string Quest, GoalTemplate Goal)> UsageGoalsOf(GameObjectTemplate template)
        => UsageGoals.Of(template);

    private static IEnumerable<InteractOptionTemplate> InteractOptions(GameObjectTemplate gameObjectTemplate)
        => gameObjectTemplate.m_behaviors
            .OfType<InteractableBehaviorTemplate>()
            .SelectMany(behavior => behavior.m_interactOptions ?? []);

    /// <summary>
    /// Every usage goal of every quest, looked up by what can name an object: a client tag, the quest and goal an
    /// option's requirement names, or a scavenge adjective. Built once for each set of quest changes; every object of
    /// every zone used to walk all quests on its own, which flooded the server with garbage right after a start.
    /// </summary>
    private sealed class UsageGoals {

        private static readonly object s_lock = new();
        private static UsageGoals s_current;

        private readonly int _version;
        private readonly IReadOnlyList<QuestTemplate> _quests;
        private readonly List<(string Quest, GoalTemplate Goal)> _all = [];
        private readonly Dictionary<string, List<int>> _byTag = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Quest, string Goal), List<int>> _byGoal = [];
        private readonly Dictionary<string, List<int>> _byAdjective = new(StringComparer.Ordinal);

        private UsageGoals(int version, IReadOnlyList<QuestTemplate> quests) {
            _version = version;
            _quests = quests;
            foreach (var quest in quests) {
                if (quest?.m_goals is null) {
                    continue;
                }

                foreach (var goal in quest.m_goals) {
                    if (goal is null || goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE) {
                        continue;
                    }

                    var index = _all.Count;
                    _all.Add((quest.m_questName, goal));
                    foreach (var tag in goal.m_clientTags ?? []) {
                        Add(_byTag, tag, index);
                    }

                    Add(_byGoal, (quest.m_questName, goal.m_goalName), index);
                    // Objects name the steps of quests we rebuilt by the game's names ("Goal 3"), Game/Quests/VanillaGoalNames.
                    foreach (var gameName in Quests.VanillaGoalNames.GameNames(quest, goal.m_goalName)) {
                        Add(_byGoal, (quest.m_questName, gameName), index);
                    }

                    if (goal is ScavengeGoalTemplate scavengeGoal) {
                        foreach (var adjective in scavengeGoal.m_itemAdjectives ?? []) {
                            Add(_byAdjective, (string) adjective, index);
                        }
                    }
                }
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int index) {
            if (key is null) {
                return;
            }

            if (!map.TryGetValue(key, out var list)) {
                list = [];
                map[key] = list;
            }

            if (list.Count == 0 || list[^1] != index) {
                list.Add(index);
            }
        }

        private static UsageGoals Current() {
            var quests = SpiralDB.QuestTemplates;
            var version = Quests.QuestOptions.Version;
            var known = s_current;
            if (known is not null && known._version == version && ReferenceEquals(known._quests, quests)) {
                return known;
            }

            // One thread builds it; the rest wait for that one instead of each building their own.
            lock (s_lock) {
                known = s_current;
                if (known is not null && known._version == version && ReferenceEquals(known._quests, quests)) {
                    return known;
                }

                s_current = new UsageGoals(version, quests);

                return s_current;
            }
        }

        /// <summary>True when some usage goal names this object.</summary>
        internal static bool Names(GameObjectTemplate template) => Matches(template).Any();

        /// <summary>True when some usage goal names this option by one of its tags, or follows its use.</summary>
        internal static bool Names(InteractOptionTemplate option) {
            var goals = Current();

            return OptionTags(option).Any(goals._byTag.ContainsKey)
                || RequiredGoals(option).Any(goals._byGoal.ContainsKey);
        }

        /// <summary>The usage goals that name this object, in quest order.</summary>
        internal static IEnumerable<(string Quest, GoalTemplate Goal)> Of(GameObjectTemplate template) {
            var goals = Current();

            return Matches(template).Distinct().Order().Select(index => goals._all[index]);
        }

        private static IEnumerable<int> Matches(GameObjectTemplate template) {
            var goals = Current();
            var options = InteractOptions(template).ToList();

            var tags = options.SelectMany(OptionTags).Prepend((string) template.m_objectName);
            foreach (var tag in tags) {
                if (tag is not null && goals._byTag.TryGetValue(tag, out var list)) {
                    foreach (var index in list) {
                        yield return index;
                    }
                }
            }

            foreach (var key in options.SelectMany(RequiredGoals)) {
                if (goals._byGoal.TryGetValue(key, out var list)) {
                    foreach (var index in list) {
                        yield return index;
                    }
                }
            }

            foreach (var adjective in template.m_adjectiveList ?? []) {
                if ((string) adjective is { } name && goals._byAdjective.TryGetValue(name, out var list)) {
                    foreach (var index in list) {
                        yield return index;
                    }
                }
            }
        }

        // The quest and goal of each requirement of an option that needs the goal not yet complete: an option that needs
        // the goal already complete follows the goal's use; it is not the use itself.
        private static IEnumerable<(string Quest, string Goal)> RequiredGoals(InteractOptionTemplate option)
            => option is InteractStateOptionTemplate { m_requirements.m_requirements: { } requirements }
                ? requirements.OfType<ReqHasGoal>()
                    .Where(r => r.m_requiredStatus != GoalStatusRequirement.Complete)
                    .Select(r => ((string) r.m_questName, (string) r.m_goalName))
                : [];

    }

}
