/*
 * BOSSES THAT WAIT FOR A CUTSCENE
 *
 * Some boss rooms keep their boss out of sight until a cutscene the live server plays has ended. Morganthe's Chamber
 * in the Shadow Palace (Khrysalis, "Defeat Morganthe", KR-PALA-C04-001) is one: its trigger
 * "HideHumanMorgantheSpawnMobs" hides the talking Morganthe and spawns the four Morganthes to fight only on the event
 * "Morganthe_MS6_15_Close", the end of her speech. Nothing in the game files sends that event (the live server's
 * scene does), so she stood there and the fight never started (a player's report Niduus passed on, 2026-11-09).
 *
 * When a wizard enters a zone, this looks for such triggers: one that spawns a creature the wizard has an open
 * "defeat" step for in this zone, and that waits for an event nothing here sends (no zone trigger, no quest, not a
 * volume, kill or step event). That event is sent once for the zone instance, a few seconds after the wizard arrives,
 * as if the scene had played. The game files are not changed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    private sealed record WaitingSpawnCheck(IActorRef PlayerActor, CoreObject PlayerObject);

    private sealed record WaitingSpawnWizard(IActorRef PlayerActor, CoreObject PlayerObject, Wizard Wizard);

    // Long enough for the zone and the wizard's quest steps to settle after arrival.
    private static readonly TimeSpan s_waitingSpawnDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_waitingSpawnQueryTimeout = TimeSpan.FromSeconds(5);
    private const string ADJECTIVE_FILE_SUFFIX = ".AdjRef";

    private readonly HashSet<IActorRef> _waitingSpawnChecks = [];
    private readonly HashSet<string> _waitingSpawnsPosted = new(StringComparer.Ordinal);

    // Every event a quest sends (start, end and step results), gathered once.
    private static HashSet<string> s_questPostedEvents;

    private void ScheduleWaitingSpawnCheck(IActorRef playerActor, CoreObject playerObject) {
        if (!_waitingSpawnChecks.Add(playerActor)) {
            return;
        }

        Context.System.Scheduler.ScheduleTellOnce(s_waitingSpawnDelay, Self,
            new WaitingSpawnCheck(playerActor, playerObject), Self);
    }

    [InternalMessageHandler(typeof(WaitingSpawnCheck))]
    private void ReceiveWaitingSpawnCheck(WaitingSpawnCheck message) {
        _waitingSpawnChecks.Remove(message.PlayerActor);
        if (message.PlayerActor is IInternalActorRef { IsTerminated: true } || WaitingSpawnTriggers().Count == 0) {
            return;
        }

        var player = message.PlayerActor;
        var playerObject = message.PlayerObject;
        player.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(), s_waitingSpawnQueryTimeout)
            .ContinueWith(t => new WaitingSpawnWizard(player, playerObject, t.IsCompletedSuccessfully ? t.Result?.Wizard : null))
            .PipeTo(Self);
    }

    [InternalMessageHandler(typeof(WaitingSpawnWizard))]
    private void ReceiveWaitingSpawnWizard(WaitingSpawnWizard message) {
        if (message.Wizard is null) {
            return;
        }

        try {
            var wanted = OpenDefeatSteps(message.Wizard);
            if (wanted.Count == 0) {
                return;
            }

            foreach (var (trigger, eventName, templateId) in WaitingSpawnTriggers()) {
                if (_waitingSpawnsPosted.Contains(eventName)) {
                    continue;
                }

                var step = wanted.FirstOrDefault(w => CreatureCounts(w.Goal, templateId));
                if (step.Goal is null) {
                    continue;
                }

                _waitingSpawnsPosted.Add(eventName);
                Logger.Information("Zone {0}: {1} waits for \"{2}\", which only a live cutscene sends; sent now so it "
                    + "can be fought for step '{3}' of '{4}' (trigger {5}).",
                    Logger.Args(Zone.ZonePath, CreatureName(templateId), eventName,
                        (string) step.Goal.m_goalName, step.Quest, trigger.m_triggerName));

                Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                    EventName = eventName,
                    PlayerActor = message.PlayerActor,
                    PlayerGameObject = message.PlayerObject,
                });
            }
        }
        catch (Exception ex) {
            Logger.Warning("Zone {0}: the check for bosses waiting on a cutscene failed: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }

    // Triggers of this zone that spawn a creature on an event nothing sends: (trigger, event, creature template).
    private List<(Trigger Trigger, string Event, ulong TemplateId)> WaitingSpawnTriggers() {
        var found = new List<(Trigger, string, ulong)>();
        var questEvents = QuestPostedEvents();
        foreach (var (trigger, _) in _orderedTriggers) {
            var spawns = trigger?.m_results?.m_results?.OfType<ResSpawn>().Where(s => s.templateID != 0).ToList();
            if (spawns is not { Count: > 0 } || trigger.m_fireEvents is null) {
                continue;
            }

            foreach (var fire in trigger.m_fireEvents.Select(e => e.ToString())) {
                if (!IsUnsentEvent(fire, questEvents)) {
                    continue;
                }

                foreach (var spawn in spawns) {
                    found.Add((trigger, fire, spawn.templateID));
                }
            }
        }

        return found;
    }

    private bool IsUnsentEvent(string eventName, HashSet<string> questEvents)
        => !string.IsNullOrEmpty(eventName)
        && eventName != START_ZONE_EVENT
        && eventName != START_ZONE_ENTER_EVENT
        && !eventName.StartsWith("Enter_", StringComparison.Ordinal)
        && !eventName.StartsWith("Exit_", StringComparison.Ordinal)
        && !eventName.StartsWith("GoalComplete_", StringComparison.Ordinal)
        && !eventName.EndsWith(".EnterState", StringComparison.Ordinal)
        && !eventName.Contains("Killed", StringComparison.OrdinalIgnoreCase)
        && !eventName.Contains("Defeat", StringComparison.OrdinalIgnoreCase)
        && !_chainedEvents.Contains(eventName)
        && !questEvents.Contains(eventName);

    private static HashSet<string> QuestPostedEvents() => LazyInitializer.EnsureInitialized(ref s_questPostedEvents, () => {
        var events = new HashSet<string>(StringComparer.Ordinal);
        void Add(ResultList list) {
            foreach (var post in list?.m_results?.OfType<ResPostEvent>() ?? []) {
                if (!string.IsNullOrEmpty(post.m_eventName)) {
                    events.Add(post.m_eventName);
                }
            }
        }

        foreach (var quest in SpiralDB.QuestTemplates ?? []) {
            if (quest is null) {
                continue;
            }

            Add(quest.m_startResults);
            Add(quest.m_endResults);
            foreach (var goal in quest.m_goals ?? []) {
                Add(goal?.m_activateResults);
                Add(goal?.m_completeResults);
            }
        }

        return events;
    });

    // The wizard's started, unfinished "defeat" steps that happen in this zone.
    private List<(string Quest, BountyGoalTemplate Goal)> OpenDefeatSteps(Wizard wizard) {
        var steps = new List<(string, BountyGoalTemplate)>();
        foreach (var instance in wizard.QuestBehavior?.CurrentQuestInstances ?? []) {
            var template = QuestTemplateCollection.GetQuestByName(instance.QuestName);
            if (template?.m_goals is null) {
                continue;
            }

            foreach (var progress in instance.GoalProgress ?? []) {
                if (!progress.DoesPlayerHaveGoal() || progress.IsGoalCompleted()) {
                    continue;
                }

                var goal = template.m_goals.OfType<BountyGoalTemplate>()
                    .FirstOrDefault(g => string.Equals(g.m_goalName, progress.GoalName, StringComparison.Ordinal));
                if (goal is not null && HappensHere(goal)) {
                    steps.Add((instance.QuestName, goal));
                }
            }
        }

        return steps;
    }

    private bool HappensHere(GoalTemplate goal) {
        string zone = QuestSafety.ZoneOf(goal);
        string raw = ((string) goal.m_destinationZone ?? "").Trim();

        return string.Equals(zone, Zone.ZonePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, Zone.ZonePath, StringComparison.OrdinalIgnoreCase);
    }

    // As a won battle counts a creature for the step: one of its adjectives, the creature named by an ".AdjRef"
    // adjective, or the stand-in a rebuilt quest names.
    private static bool CreatureCounts(BountyGoalTemplate goal, ulong templateId) {
        var wanted = (goal.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToList();
        if (wanted.Count == 0) {
            return false;
        }

        if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template
            && (template.m_adjectiveList ?? []).Any(a => wanted.Contains((string) a))) {
            return true;
        }

        foreach (var adjective in wanted.Where(a => a.EndsWith(ADJECTIVE_FILE_SUFFIX, StringComparison.OrdinalIgnoreCase))) {
            if (CoreObjectFactory.IdOfFileName(adjective[..^ADJECTIVE_FILE_SUFFIX.Length]) == templateId) {
                return true;
            }
        }

        return BountyStandIns.Counts(wanted, templateId);
    }

    private static string CreatureName(ulong templateId)
        => QuestSafety.NameOf(templateId) is { Length: > 0 } name ? name : templateId.ToString();

}
