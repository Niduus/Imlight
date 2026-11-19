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
 */

/*
 * BOSSES THAT A WALK-IN SPOT BRINGS OUT
 *
 * Some bosses start switched off and come out only when a wizard walks through one small spot of the room. Sprockets in
 * Counterweight East (Big Ben, "Stolen Away", MB-MUSEHub-C02-001) is one: his spawner (Spawn point 1 (1)) is switched on
 * by the trigger of "Activator Volume 11 (1)", a ball 200 wide on the ramp up to the top, and he stands on the top
 * platform. A player on Niduus's server reached the top with "Defeat Sprockets" open and found nobody there
 * (2026-11-19); Ultimus also waited there for an hour on 2026-11-15. The spot can be missed (a wizard only counts as in
 * it when a position check lands inside it), and nothing else ever brings him out.
 *
 * So whenever a wizard walks into any spot of the room, the room checks: a boss spawner that only walk-in spots switch
 * on, whose creature one of the wizard's open "defeat" steps needs, is switched on when the wizard is near where the
 * boss stands (within WAKE_REACH on the ground and WAKE_HEIGHT up or down). It spawns only if he is not there already.
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

    private const float WAKE_REACH = 1800;
    private const float WAKE_HEIGHT = 450;

    // Spawners that start switched off and that only triggers fired by walking into a spot switch on, with where they spawn.
    private readonly List<(SpawnObject Spawner, float X, float Y, float Z)> _walkInSpawners = [];
    private readonly HashSet<ulong> _walkInSpawnersWoken = [];

    private void NoteWalkInSpawners(IEnumerable<SpawnObject> spawners, IEnumerable<Trigger> triggers, List<PathObjectTemplate> paths,
        List<NodeObject> nodes) {
        _walkInSpawners.Clear();
        try {
            var byWalkIn = new HashSet<ulong>();
            var byOther = new HashSet<ulong>();
            foreach (var trigger in (triggers ?? []).Where(t => t?.m_results?.m_results is not null)) {
                var walkIn = trigger.m_fireEvents is { Count: > 0 } fire
                             && fire.All(e => ((string) e).StartsWith("Enter_", StringComparison.Ordinal));
                foreach (var spawn in trigger.m_results.m_results.OfType<ResSpawn>().Where(r => r.m_activate)) {
                    (walkIn ? byWalkIn : byOther).Add((ulong) spawn.m_spawnID);
                }
            }

            foreach (var spawner in spawners ?? []) {
                var id = spawner?.m_id.Full ?? 0;
                if (spawner is not { m_active: false, m_spawnList.Count: > 0 } || !byWalkIn.Contains(id) || byOther.Contains(id)) {
                    continue;
                }

                var pathId = spawner.m_spawnList.Select(i => i?.m_objectInfo?.m_pathID.Full ?? 0).FirstOrDefault(p => p != 0);
                var path = paths?.FirstOrDefault(p => p.m_id.Full == pathId);
                var node = path?.m_nodeIDs is { Count: > 0 } ids ? nodes?.FirstOrDefault(n => ids.Contains(n.m_id)) : null;
                if (node is null) {
                    continue;
                }

                _walkInSpawners.Add((spawner, node.m_location.X, node.m_location.Y, node.m_location.Z));
            }
        }
        catch (Exception ex) {
            Logger.Debug("Zone {0}: the walk-in boss spawners could not be read: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }

    private bool HoldsWalkInSpawners() => _walkInSpawners.Count > _walkInSpawnersWoken.Count;

    private void WakeWalkInStepSpawners(WaitingSpawnWizard message) {
        if (!HoldsWalkInSpawners() || message.PlayerObject is null) {
            return;
        }

        try {
            var steps = OpenDefeatStepsAnywhere(message.Wizard);
            if (steps.Count == 0) {
                return;
            }

            var at = message.PlayerObject.m_location;
            foreach (var (spawner, x, y, z) in _walkInSpawners) {
                var id = spawner.m_id.Full;
                if (_walkInSpawnersWoken.Contains(id)) {
                    continue;
                }

                var ground = MathF.Sqrt(((at.X - x) * (at.X - x)) + ((at.Y - y) * (at.Y - y)));
                if (ground > WAKE_REACH || MathF.Abs(at.Z - z) > WAKE_HEIGHT) {
                    continue;
                }

                var templates = spawner.m_spawnList.Select(i => i?.m_objectInfo?.m_templateID.Full ?? 0).Where(t => t != 0).ToList();
                var step = steps.FirstOrDefault(s => templates.Any(t => CreatureCounts(s.Goal, t)));
                if (step.Goal is null) {
                    continue;
                }

                _walkInSpawnersWoken.Add(id);
                Logger.Information("Zone {0}: spawner \"{1}\" ({2}) only comes out at a walk-in spot; the wizard is {3:0} away "
                    + "with step '{4}' of '{5}' open, so it is switched on now (only if he is not there already).",
                    Logger.Args(Zone.ZonePath, (string) spawner.m_name, CreatureName(templates[0]), ground,
                        (string) step.Goal.m_goalName, step.Quest));

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
            Logger.Warning("Zone {0}: the check for walk-in boss spawners failed: {1}", Logger.Args(Zone.ZonePath, ex.Message));
        }
    }
}
