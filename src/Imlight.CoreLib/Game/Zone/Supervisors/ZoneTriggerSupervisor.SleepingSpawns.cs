/*
 * BOSSES WHOSE SPAWNER STARTS SWITCHED OFF
 *
 * Some rooms keep a boss's spawner switched off in the game files until the quest step that needs him turns it on
 * (the step's own "spawn" result). Krokotopia's Throne Room of Fire is one: "Spawn point 6" (Krokenkahmen, "Into the
 * Map Room") starts off, and no trigger of the room ever turns it on. When the step started somewhere else (the quest
 * is taken in the Altar of Kings), or the room was a new copy made after the step started, its spawn result went to
 * another room and Krokenkahmen never came: Ultimus waited at his spot for 15 minutes (Niduus, 2026-11-14, and it
 * hits players the same way).
 *
 * When a wizard enters a room, or a "defeat" step of theirs starts while they are in it, this looks for spawners of
 * the room that start switched off, that no trigger of the room ever switches on, and whose creature counts for one
 * of the wizard's open "defeat" steps. Such a spawner is switched on once for this copy of the room, and spawns its
 * creature if none is up. Nothing is skipped: the creature still has to be beaten, and the game files are not changed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    // Spawners that start switched off and that no trigger of this room switches on (read once when the room loads).
    private readonly List<SpawnObject> _sleepingSpawners = [];
    private readonly HashSet<ulong> _sleepingSpawnersWoken = [];

    private void NoteSleepingSpawners(IEnumerable<SpawnObject> spawners, IEnumerable<Trigger> triggers) {
        _sleepingSpawners.Clear();
        try {
            var switchedOnByTriggers = new HashSet<ulong>();
            foreach (var spawn in (triggers ?? []).Where(t => t?.m_results?.m_results is not null)
                         .SelectMany(t => t.m_results.m_results).OfType<ResSpawn>()) {
                if (spawn.m_activate) {
                    switchedOnByTriggers.Add((ulong) spawn.m_spawnID);
                }
            }

            foreach (var spawner in spawners ?? []) {
                if (spawner is { m_active: false, m_spawnList.Count: > 0 } && !switchedOnByTriggers.Contains(spawner.m_id.Full)) {
                    _sleepingSpawners.Add(spawner);
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: the switched-off spawners could not be read: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }

    private bool HoldsSleepingSpawners() => _sleepingSpawners.Count > _sleepingSpawnersWoken.Count;

    // A "defeat" step started while the wizard is in this room (QuestService.StartGoal).
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CHECKSTEPSPAWNS))]
    private void ReceiveCheckStepSpawns(ZONE_102_PROTOCOL.MSG_CHECKSTEPSPAWNS message) {
        if (message.PlayerActor is not null) {
            ScheduleWaitingSpawnCheck(message.PlayerActor, message.PlayerGameObject);
        }
    }

    private void WakeSleepingStepSpawners(WaitingSpawnWizard message) {
        if (!HoldsSleepingSpawners()) {
            return;
        }

        try {
            var steps = OpenDefeatStepsAnywhere(message.Wizard);
            if (steps.Count == 0) {
                return;
            }

            foreach (var spawner in _sleepingSpawners) {
                var id = spawner.m_id.Full;
                if (_sleepingSpawnersWoken.Contains(id)) {
                    continue;
                }

                var templates = spawner.m_spawnList.Select(i => i?.m_objectInfo?.m_templateID.Full ?? 0).Where(t => t != 0).ToList();
                var step = steps.FirstOrDefault(s => templates.Any(t => CreatureCounts(s.Goal, t)));
                if (step.Goal is null) {
                    continue;
                }

                _sleepingSpawnersWoken.Add(id);
                Logger.Information("Zone {0}: spawner \"{1}\" ({2}) starts switched off and nothing in this room switches it on; "
                    + "switched on now for step '{3}' of '{4}', which needs it.",
                    Logger.Args(Zone.ZonePath, (string) spawner.m_name, CreatureName(templates[0]), (string) step.Goal.m_goalName, step.Quest));

                ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                    Messages = [new ZONE_102_PROTOCOL.MSG_ZONEPATHSPAWN {
                        SpawnObjectID = (uint) id,
                        Activate = true,
                        OnlyIfAbsent = true,
                        PlayerActor = message.PlayerActor,
                        PlayerObject = message.PlayerObject,
                        PlayerWizard = message.Wizard,
                    }],
                    Targets = ZoneBroadcastTarget.Paths,
                });
            }
        }
        catch (Exception ex) {
            Logger.Warning("Zone {0}: the check for switched-off boss spawners failed: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }

    // The wizard's started, unfinished "defeat" steps, wherever they say they happen (many name no area).
    private static List<(string Quest, BountyGoalTemplate Goal)> OpenDefeatStepsAnywhere(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) {
        var steps = new List<(string, BountyGoalTemplate)>();
        foreach (var instance in wizard?.QuestBehavior?.CurrentQuestInstances ?? []) {
            var template = instance is null ? null : Imlight.CoreLib.WizardData.Collections.QuestTemplateCollection.GetQuestByName(instance.QuestName);
            if (template?.m_goals is null) {
                continue;
            }

            foreach (var progress in instance.GoalProgress ?? []) {
                if (progress is null || !progress.DoesPlayerHaveGoal() || progress.IsGoalCompleted()) {
                    continue;
                }

                var goal = template.m_goals.OfType<BountyGoalTemplate>()
                    .FirstOrDefault(g => string.Equals(g.m_goalName, progress.GoalName, StringComparison.Ordinal));
                if (goal is not null) {
                    steps.Add((instance.QuestName, goal));
                }
            }
        }

        return steps;
    }

}
