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
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 * ========================================================================
 * INTERACT OBJECT STATE COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Makes a zone object that changes state when used (levers, braziers, obelisks, candles, crystal
 * stands) a press-X interactable. Using it puts the object into the state its option names, raises
 * "<object>.<state>.EnterState" and runs the option's results (which post events such as LeverUsed).
 *
 * USAGE EXAMPLE:
 * Nothing to configure: any object template whose interact option names a state, or whose option
 * results post an event, gets this component.
 *
 * NOTE:
 * An option that carries goal tags belongs to a quest usage goal: InteractQuestSelectComponent offers it
 * and calls <see cref="ApplyGoalOptions"/> when the goal is used (which also applies the plain state options, so
 * goal-less stands still raise their EnterState). An option that only carries a quest event (a book) is offered
 * too, and using it posts that event to the zone, where triggers waiting on it fire. Every other option is a candidate while its requirements
 * hold and the object is in the option's visible state. The client gets one press-X entry per object; each
 * use applies the option that fits the current state (see ChooseOption), so two- and four-state objects step
 * through their states. Options whose results show dialog make a readable object (a book) attach too.
 * A chest whose option runs a minigame result attaches too: ResMinigameHandler starts the game.
 *
 * TODO:
 *
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractObjectStateComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName => "Interact";
    public string NpcIcon => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey => (Entity.Template as GameObjectTemplate)?.m_displayName;
    public string NpcTextKey => null;
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private string ObjectName => Entity.Info?.m_zoneTag is { Length: > 0 } tag
        ? tag
        : (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();

    // A wooden chest opens with one use of its own component (WoodenChestComponent); this one would answer the first use with
    // only the Open animation.
    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go && GetStateOptions(go).Any(ChangesWorld) && !WoodenChestComponent.ShouldAttachToEntity(template);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => GetOfferedOptions(playerCharacter).Any()
            ? [new InteractableOption { m_serviceName = ServiceName }]
            : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // One press-X entry stands for the whole object; the memento hands each component the index of its own option.
        if (serviceOptionIndex != 0) {
            return;
        }

        var chosen = ChooseOption(GetOfferedOptions(playerCharacter).ToList(), CurrentState());
        if (chosen is not null) {
            Apply(chosen, playerActor, playerObject);
            // A storm obelisk of the Tomb of Storms: lit for this wizard, for the way into the Well of Spirits (StormObelisks).
            if (StormObelisks.IsObelisk(TemplateId)) {
                StormObelisks.Lit(playerCharacter, TemplateId);
            }

            return;
        }

        // Said in the log so a puzzle piece that does nothing can be traced to its state and requirements.
        Logger.Warning("Use of {0} did nothing for {1}: no option fits its state '{2}' and the wizard's progress.",
            Logger.Args(ObjectName, playerCharacter?.CharId, CurrentState()));
    }

    /// <summary>
    /// Whether one use now takes the wizard somewhere (its option has a teleport among its results), as the Dragonspyre
    /// cathedral arches do (AcadPort1, LibraryPort1, NecroPort1: they light up and send the wizard through).
    /// </summary>
    internal bool UseTeleports(Wizard playerCharacter) {
        try {
            var chosen = ChooseOption(GetOfferedOptions(playerCharacter).ToList(), CurrentState());

            // Only a teleport that knows where it goes: its own destination, or one stored under the object's name (as the
            // teleport handler looks it up). The cathedral arches' own use names none, so keeping it over the door left every
            // wizard standing at the Basilica's arch to The Atheneum (v0.10.936; Ultimus pressed LibraryPort1 for 9 minutes,
            // "A teleport in DragonSpire/DS_Hub_Cathedral names no destination zone").
            return Results(chosen)?.m_results?.Any(r => r is ResTeleport t
                && (!string.IsNullOrEmpty(t.m_destinationZone) || StoredDestination())) == true;
        }
        catch (Exception) {
            return false;
        }
    }

    // Whether the zone's teleport table has a teleport with a destination under this object's name.
    private bool StoredDestination()
        => Entity.Zone?.ZonePath is { Length: > 0 } zone
           && Imlight.CoreLib.WizardData.Collections.ZoneDataCollection.GetZoneData(zone)?.Teleports?
               .Any(t => string.Equals(t?.TriggerName, ObjectName, StringComparison.Ordinal)
                         && !string.IsNullOrEmpty(t.Teleport?.m_destinationZone)) == true;

    /// <summary>
    /// The option one use applies. Options with requirements are the specific ones (the offered list already
    /// holds only those that pass), so they win over unconditional ones. Among several, the use steps to the option
    /// after the one matching the current state, wrapping around; an unmatched state starts at the first.
    /// </summary>
    internal static InteractOptionTemplate ChooseOption(IReadOnlyList<InteractOptionTemplate> offered, string currentState) {
        if (offered.Count == 0) {
            return null;
        }

        var pool = offered.Where(o => Requirements(o)?.m_requirements is { Count: > 0 }).ToList();
        if (pool.Count == 0) {
            pool = [.. offered];
        }

        if (pool.Count == 1) {
            return pool[0];
        }

        var at = string.IsNullOrEmpty(currentState)
            ? -1
            : pool.FindIndex(o => string.Equals(EnterState(o), currentState, StringComparison.OrdinalIgnoreCase));

        return pool[(at + 1) % pool.Count];
    }

    // The state the object is in now, else the state its placement starts it in.
    private string CurrentState()
        => Zone.ScriptState.GetObjectState(ObjectName) is { Length: > 0 } known ? known : Entity.Info?.m_startState;

    /// <summary>
    /// Applies the object's options for a quest goal use: every goal-tagged option, plus the plain state
    /// options whose visible state matches (crystal stands carry no goal tags, quests name them by object).
    /// A plain option is skipped once the object is already in its state, so it never applies twice.
    /// </summary>
    public void ApplyGoalOptions(IActorRef playerActor, CoreObject playerObject) {
        var current = Zone.ScriptState.GetObjectState(ObjectName);
        foreach (var option in GetStateOptions(Entity.Template as GameObjectTemplate)) {
            if (IsGoalOption(option)) {
                // An option for another zone is not this placement's (the Obsidian Chest of each world posts only its own
                // "GotObsidianChestN"; all nine at once finished every step of Wizard Tours).
                if (InteractQuestSelectComponent.OptionFitsZone(option, Entity.Zone?.ZonePath)) {
                    Apply(option, playerActor, playerObject);
                }

                continue;
            }

            if (!ChangesWorld(option)) {
                continue;
            }

            var visibleState = VisibleState(option);
            if (!string.IsNullOrEmpty(visibleState) && !string.IsNullOrEmpty(current)
                && !string.Equals(current, visibleState, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var enterState = EnterState(option);
            if (!string.IsNullOrEmpty(enterState) && string.Equals(current, enterState, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            Apply(option, playerActor, playerObject);
        }
    }

    private void Apply(InteractOptionTemplate option, IActorRef playerActor, CoreObject playerObject) {
        var objectName = ObjectName;
        var newState = EnterState(option);
        if (!string.IsNullOrEmpty(newState) && !string.IsNullOrEmpty(objectName)) {
            Zone.ScriptState.SetObjectState(objectName, newState);
            Entity.ChangeState(newState);

            ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = $"{objectName}.{newState}.EnterState",
                PlayerActor = playerActor,
                PlayerGameObject = playerObject,
            });
        }

        if (!string.IsNullOrEmpty(option.m_questEvent)) {
            ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = option.m_questEvent,
                PlayerActor = playerActor,
                PlayerGameObject = playerObject,
            });
        }

        if (Results(option) is { m_results: { Count: > 0 } } results) {
            Entity.ExecuteResults(results, playerActor, playerObject, objectName);
        }
    }

    private ulong TemplateId => Entity.Info is { } info ? (ulong) info.m_templateID : 0;

    private IEnumerable<InteractOptionTemplate> GetOfferedOptions(Wizard wizard) {
        var current = CurrentState();
        // A storm obelisk another wizard in this copy of the tomb already lit is still there for this one to light: each
        // wizard lights the three for themselves, as in their own game (StormObelisks).
        var obelisk = StormObelisks.IsObelisk(TemplateId);
        if (obelisk && wizard is not null && !StormObelisks.HasLit(wizard, TemplateId)) {
            current = null;
        }

        // An object quest steps are about (Last Wood's fire pits: Head to Foot puts them out) is used for those steps only. Its
        // own use was offered to everyone at any time, so a wizard who had put the pits out long ago could put them out again
        // (2026-11-25). A dungeon switch a trigger listens to stays usable (IsZoneSwitchWithoutGoal).
        var questObject = wizard is not null && !obelisk && Entity.Template is GameObjectTemplate questTemplate
                          && InteractQuestSelectComponent.UsageGoalsOf(questTemplate).Any();

        foreach (var option in GetStateOptions(Entity.Template as GameObjectTemplate)) {
            if (questObject && !IsZoneSwitchWithoutGoal(option, wizard, current)) {
                continue;
            }

            // A quest's own option is offered by its usage goal (InteractQuestSelectComponent). A dungeon lever's option is
            // also the dungeon's own switch: its state change opens gates for anyone, with or without the quest
            // (Counterweight East's levers name Metal Madness, but Stolen Away needs the same gates to reach Sprockets).
            if (IsGoalOption(option) && !IsZoneSwitchWithoutGoal(option, wizard, current)) {
                continue;
            }
            if (!ChangesWorld(option)) {
                continue;
            }

            // An unknown current state offers everything; a known one must match the option's.
            var visibleState = VisibleState(option);
            if (!string.IsNullOrEmpty(visibleState) && !string.IsNullOrEmpty(current)
                && !string.Equals(current, visibleState, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var requirements = Requirements(option);
            // An obelisk's own need (Back to Balance done) is also met by the quest itself, for a wizard who finished it
            // before the server wrote that entry.
            if (requirements?.m_requirements is { Count: > 0 }
                && !(obelisk && StormObelisks.MayLight(wizard))
                && !RequirementDispatcher.EvaluateRequirements(
                    requirements,
                    new ZoneRequirementContext(requirements, null, null, wizard, ZoneActor, ObjectName) {
                        ScriptState = Zone.ScriptState,
                    })) {
                continue;
            }

            yield return option;
        }
    }

    /// <summary>
    /// True when a quest-goal option is also a switch of the zone (a trigger fires on the state it puts the object in),
    /// the object is not in that state yet, and the wizard has no open usage goal for this object (that goal offers it).
    /// </summary>
    private bool IsZoneSwitchWithoutGoal(InteractOptionTemplate option, Wizard wizard, string current) {
        var enterState = EnterState(option);
        var objectName = ObjectName;
        if (string.IsNullOrEmpty(enterState) || string.IsNullOrEmpty(objectName)
            || string.Equals(current, enterState, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var fire = $"{objectName}.{enterState}.EnterState";
        if (Zone.TriggersByName?.Values.Any(t => t?.m_fireEvents?.Any(e => e == fire) == true) != true) {
            return false;
        }

        if (wizard?.QuestBehavior?.CurrentQuestInstances is { } quests && Entity.Template is GameObjectTemplate template) {
            foreach (var (quest, goal) in InteractQuestSelectComponent.UsageGoalsOf(template)) {
                if (quests.Any(q => q.QuestName == quest && q.IsGoalActive(goal.m_goalName))) {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// True when a use already put the object into the state one of its quest-goal options puts it in.
    /// </summary>
    internal bool IsInGoalOptionState() {
        var current = Zone.ScriptState.GetObjectStateIfChanged(ObjectName);

        return !string.IsNullOrEmpty(current)
            && GetStateOptions(Entity.Template as GameObjectTemplate)
                .Any(option => IsGoalOption(option) && string.Equals(EnterState(option), current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when a quest-goal option of the template puts the object into a state, so the object stays in the world after its use.
    /// </summary>
    internal static bool KeepsStateAfterGoalUse(GameObjectTemplate template)
        => GetStateOptions(template).Any(option => IsGoalOption(option) && !string.IsNullOrEmpty(EnterState(option)));

    private static bool IsGoalOption(InteractOptionTemplate option)
        => option.m_goalTags is { Count: > 0 } || InteractQuestSelectComponent.IsNamedByUsageGoal(option);

    private static bool ChangesWorld(InteractOptionTemplate option)
        => !string.IsNullOrEmpty(EnterState(option))
        || !string.IsNullOrEmpty(option.m_questEvent)
        || Results(option)?.m_results?.Any(ChangesWorld) == true;

    // Results that move a zone or quest on: a use that runs one of them is a real step. A quest event (Avalon's Deep
    // Water teleporter, the Silverharp cage), a token or counter (raid coins, plants and essences, the Gauntlet's
    // library pages), a shown or hidden piece or a spawn were once not counted, so those objects could not be used.
    private static bool ChangesWorld(Result result)
        => result is ResPostEvent or ResModifyTriggerObject or ResActorDialog or ResMinigame or ResPostQuestEvent
            or ResZoneTokenEnable or ResZoneTokenDisable or ResZoneTokenModify or ResZoneTokenReset or ResZoneCounter
            or ResAddDynaMod or ResRemoveDynaMod or ResAddTriggerObject or ResRemoveTriggerObject or ResSpawn
            or ResStateChange or ResIfThenElse;

    private static string EnterState(InteractOptionTemplate option)
        => (option as InteractStateOptionTemplate)?.m_enterState.ToString();

    private static string VisibleState(InteractOptionTemplate option)
        => (option as InteractStateOptionTemplate)?.m_visibleState.ToString();

    private static RequirementList Requirements(InteractOptionTemplate option)
        => (option as InteractStateOptionTemplate)?.m_requirements;

    private static ResultList Results(InteractOptionTemplate option)
        => (option as InteractStateOptionTemplate)?.m_results;

    /// <summary>The quest events the template's options post when used.</summary>
    internal static IEnumerable<string> GetQuestEvents(GameObjectTemplate template)
        => GetStateOptions(template).Select(option => (string) option.m_questEvent).Where(name => !string.IsNullOrEmpty(name)).Distinct();

    internal static IEnumerable<Result> GetResults(GameObjectTemplate template)
        => GetStateOptions(template).SelectMany(option => Results(option)?.m_results ?? []);

    private static IEnumerable<InteractOptionTemplate> GetStateOptions(GameObjectTemplate template)
        => template?.m_behaviors?
            .OfType<InteractableBehaviorTemplate>()
            .SelectMany(behavior => behavior.m_interactOptions ?? [])
            .OfType<InteractOptionTemplate>()
        ?? [];

}
