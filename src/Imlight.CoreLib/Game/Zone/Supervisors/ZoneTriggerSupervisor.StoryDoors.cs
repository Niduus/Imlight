/*
 * STORY DOORS THAT STAY SHUT
 *
 * Some doors in the world are opened once, for good, by a line of a quest's dialog: Bartleby's mouth in Ravenwood (the
 * way to the Spiral Door) opens on the dialog event "WC-MAIN-C01-010_OpenMouth", which sets two looks on the wizard
 * (the mouth open, its invisible wall gone). A wizard who never heard that line (a bot, a skipped or rebuilt quest, a
 * level given by command) kept a shut mouth and a wall in front of it forever, though the door's own teleport lets
 * them through (Niduus 2026-11-19, a player's report: "why is bartleby closed i'm 190").
 *
 * When a wizard enters such a zone and the door's own teleport trigger would let them through, but the door's look is
 * not on them yet, the opening event is sent for them, as the dialog would have. The game files are not changed.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

internal sealed partial class ZoneTriggerSupervisor {

    /// <param name="Zone">The zone the door is in.</param>
    /// <param name="OpenEvent">The event whose trigger gives the wizard the door's open look.</param>
    /// <param name="WayThrough">The door's own teleport trigger: its requirements say who may go through.</param>
    /// <param name="DoorTag">The look (dynamic mod tag) the open event sets.</param>
    /// <param name="OpenState">The state of that look when the door is open.</param>
    private sealed record StoryDoor(string Zone, string OpenEvent, string WayThrough, string DoorTag, string OpenState);

    private static readonly StoryDoor[] s_storyDoors = [
        new("WizardCity/WC_Ravenwood", "WC-MAIN-C01-010_OpenMouth", "Trigger 1", "WC_BartlebyMouth_Door instance", "IdleOpen"),
    ];

    private void OpenStoryDoors(ZONE_102_PROTOCOL.MSG_POSTEVENT message) {
        if (message.PlayerActor is null || Zone.TriggersByName is not { } triggers) {
            return;
        }

        foreach (var door in s_storyDoors) {
            if (!string.Equals(door.Zone, Zone.ZonePath, StringComparison.OrdinalIgnoreCase)
                || !triggers.TryGetValue(door.WayThrough, out var wayThrough)
                || !triggers.Values.Any(t => t?.m_fireEvents?.Any(e => e == door.OpenEvent) == true)) {
                continue;
            }

            try {
                var wizard = Core.ZoneTrigger.QueryWizard(message.PlayerActor);
                if (wizard is null
                    || wizard.DynamodSet?.Dynamods?.Any(d => d is not null
                        && string.Equals(d.ClientTag, door.DoorTag, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.ModState, door.OpenState, StringComparison.OrdinalIgnoreCase)) == true
                    || !EvaluateRequirements(wayThrough, message)) {
                    continue;
                }

                Logger.Information("Zone {0}: {1} may go through {2} but never had it opened; opening it for them ({3}).",
                    Logger.Args(Zone.ZonePath, wizard.CharId, door.DoorTag, door.OpenEvent));
                Self.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                    EventName = door.OpenEvent,
                    PlayerActor = message.PlayerActor,
                    PlayerGameObject = message.PlayerGameObject,
                });
            }
            catch (Exception ex) {
                Logger.Debug("Zone {0}: could not check story door {1}: {2}", Logger.Args(Zone.ZonePath, door.DoorTag, ex.Message));
            }
        }
    }

}
