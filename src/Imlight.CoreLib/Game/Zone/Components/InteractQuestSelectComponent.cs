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
        && (HasBehavior(template, "WizardSelectBehavior") || IsNamedByAnyUsageGoal(gameObjectTemplate)
            || UsageGoals.HasPictureOf(gameObjectTemplate));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (playerCharacter?.QuestBehavior?.CurrentQuestInstances == null) {
            yield break;
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
        // A teleporter taken away by an older build stays usable (IsTravel): Zoom's Survey Camp stone, 2026-11-25.
        if ((!_collectedBy.Contains(playerCharacter.CharId) || IsTravel) && HasActiveMatchingUsageGoal(playerCharacter)) {
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
        var zone = Entity.Zone?.ZonePath;
        foreach (var (questName, goal) in UsageGoals.Of(gameObjectTemplate)) {
            if (!IsUsedHere(gameObjectTemplate, goal, zone)) {
                continue;
            }

            if (!_usageGoalsByQuest.TryGetValue(questName, out var goalList)) {
                goalList = [];
                _usageGoalsByQuest[questName] = goalList;
            }
            goalList.Add(goal);
        }

        foreach (var (questName, goal) in StandInGoalsOf(gameObjectTemplate, Entity.Zone?.ZonePath)) {
            if (!_usageGoalsByQuest.TryGetValue(questName, out var goalList)) {
                goalList = [];
                _usageGoalsByQuest[questName] = goalList;
            }

            if (!goalList.Contains(goal)) {
                goalList.Add(goal);
            }
        }
    }

    /// <summary>
    /// False when every option of the object that names this step is for another zone. One Obsidian Chest template
    /// stands in nine places (Haunted Cave, Hall of Champions, Chelsea Court, ...); each option needs its own zone
    /// (ReqInZone) and names its own step of "Wizard Tours" (DS-ACAD-C01-001). Every chest counted every open step, so
    /// the first chest opened gave all the Dragon Rider Staff pieces at once (player report, 2026-11-28).
    /// </summary>
    internal static bool IsUsedHere(GameObjectTemplate template, GoalTemplate goal, string zone) {
        if (template is null || goal is null || string.IsNullOrEmpty(zone)) {
            return true;
        }

        var tags = goal.m_clientTags ?? [];
        var naming = InteractOptions(template).Where(o => OptionTags(o).Any(t => tags.Any(g => string.Equals((string) g, t, StringComparison.Ordinal)))).ToList();

        return naming.Count == 0 || naming.Any(o => OptionFitsZone(o, zone));
    }

    /// <summary>
    /// True unless the option needs the wizard to be in certain zones (ReqInZone) and this zone is none of them.
    /// </summary>
    internal static bool OptionFitsZone(InteractOptionTemplate option, string zone) {
        if (string.IsNullOrEmpty(zone) || option is not InteractStateOptionTemplate { m_requirements.m_requirements: { Count: > 0 } requirements }) {
            return true;
        }

        var zones = requirements.OfType<ReqInZone>()
            .Where(r => !r.m_applyNOT)
            .Select(r => (string) r.m_zoneName)
            .Where(z => !string.IsNullOrEmpty(z))
            .ToList();
        if (zones.Count == 0 || requirements.Any(r => r is not null && r.m_operator == Operator.ROP_OR)) {
            return true;
        }

        return zones.Any(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The "use" steps this object stands in for: steps that happen in this zone, whose own object no zone file of it
    /// places, and whose quest button picture is this object's. Sobaka's boat (ZF_Sobakas_Boat, the step of "Fastest Hunk
    /// of Junk", "Down By The River" and "Catching Shadows" in Zafaria) is placed nowhere in the game files; the boats
    /// you can press, Stone Town's ZF_TeleportToWaterfront and Waterfront's ZF_TeleportToStoneTown2, carry its picture
    /// (Location_ZF_SobakasDingy) and are the only things that do. Riding one never counted the step, so no wizard could
    /// finish those quests (Zoom, 2026-11-26 04:30-05:15). Only for a step with nothing of its own in the zone: 332 of the
    /// game's 743 "use" steps share their picture with other things (the Quetzal cages), and those keep their own objects.
    /// </summary>
    internal static IEnumerable<(string Quest, GoalTemplate Goal)> StandInGoalsOf(GameObjectTemplate template, string zone) {
        if (template is null || string.IsNullOrEmpty(zone)) {
            yield break;
        }

        var own = UsageGoals.Of(template).ToList();
        foreach (var (quest, goal) in UsageGoals.PictureGoalsOf(template)) {
            if (!string.Equals((string) goal.m_destinationZone, zone, StringComparison.OrdinalIgnoreCase)
                || own.Any(o => o.Quest == quest && ReferenceEquals(o.Goal, goal))
                || Imlight.CoreLib.Game.Tools.QuestStepSpots.OwnUsageObjects(zone, quest, goal.m_goalName).Count > 0) {
                continue;
            }

            yield return (quest, goal);
        }
    }

    /// <summary>The quest button picture of a "use" step (empty when it has none), for the stand-ins above.</summary>
    internal static string PictureOf(string quest, string goalName)
        => UsageGoals.PictureOf(quest, goalName);

    /// <summary>The picture an object shows on a quest button (its icon), when it can be pressed at all.</summary>
    internal static string PictureOf(GameObjectTemplate template)
        => template?.m_sIcon is { Length: > 0 } icon && InteractOptions(template).Any() ? (string) icon : "";

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // One use completes every active usage goal that matches this object, across all of the
        // player's quests, each through the normal goal-completion path.
        var activeGoals = FindActiveMatchingGoals(playerCharacter);
        if (activeGoals.Count == 0 || (playerCharacter is not null && _collectedBy.Contains(playerCharacter.CharId) && !IsTravel)) {
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
        if (goalMax > 1 && !InteractObjectStateComponent.KeepsStateAfterGoalUse(Entity.Template as GameObjectTemplate)
            && !StaysAfterUse(activeGoals, goalMax)) {
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

    // A thing that must stay after a use that counts: a teleporter (the area's own way around), or the only thing of its
    // kind in the area when the step wants more uses than there are things to use. "The Big Guy" (Celestia) asks for 3
    // uses of Survey Camp's teleport stone, the only one there: the first use took the stone away for the wizard, so the
    // step stopped at 1 of 3 for good (Zoom's log, 2026-11-25 16:40-17:15).
    private bool IsTravel => Entity.GetComponentOfType<StreetTeleporterComponent>() is not null
                             || Entity.GetComponentOfType<InteractDoorTeleportComponent>() is not null
                             || Entity.GetComponentOfType<WorldTeleportDoorComponent>() is not null
                             || Entity.GetComponentOfType<CustomTeleporterComponent>() is not null;

    private bool StaysAfterUse(List<(QuestInstance Quest, GoalTemplate Goal, GoalInstance GoalProgress)> activeGoals, int goalMax) {
        if (IsTravel) {
            return true;
        }

        var zone = Entity.Zone?.ZonePath;
        if (string.IsNullOrEmpty(zone)) {
            return false;
        }

        return activeGoals.Any(g => g.Goal is not null
            && Imlight.CoreLib.Game.Tools.QuestStepSpots.UsageObjects(zone, g.Quest.QuestName, g.Goal.m_goalName) is { Count: > 0 } placed && placed.Count < goalMax);
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
        private readonly Dictionary<string, List<int>> _byPicture = new(StringComparer.OrdinalIgnoreCase);

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

                    if ((string) goal.m_displayImage1 is { Length: > 0 } picture
                        && picture.StartsWith("GUI/QuestButtons/", StringComparison.OrdinalIgnoreCase)) {
                        Add(_byPicture, picture, index);
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

        /// <summary>True when some usage goal shows this object's quest button picture (a possible stand-in).</summary>
        internal static bool HasPictureOf(GameObjectTemplate template)
            => InteractQuestSelectComponent.PictureOf(template) is { Length: > 0 } picture && Current()._byPicture.ContainsKey(picture);

        /// <summary>The usage goals whose quest button picture is this object's, in quest order.</summary>
        internal static IEnumerable<(string Quest, GoalTemplate Goal)> PictureGoalsOf(GameObjectTemplate template) {
            var goals = Current();
            if (InteractQuestSelectComponent.PictureOf(template) is not { Length: > 0 } picture || !goals._byPicture.TryGetValue(picture, out var list)) {
                return [];
            }

            return list.Distinct().Order().Select(index => goals._all[index]).ToList();
        }

        /// <summary>The quest button picture of one usage goal (empty when it has none).</summary>
        internal static string PictureOf(string quest, string goalName) {
            var goals = Current();
            if (!goals._byGoal.TryGetValue((quest, goalName), out var list)) {
                return "";
            }

            return list.Select(index => (string) goals._all[index].Goal.m_displayImage1).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
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
