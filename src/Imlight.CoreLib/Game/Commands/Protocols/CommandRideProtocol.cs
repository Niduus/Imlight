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
 * MULTI-PERSON MOUNTS
 * ========================================================================
 *
 * PURPOSE:
 * .ride: get on a group mate's multi-person mount by name, or off again, for when the X prompt is not at hand.
 *
 * USAGE EXAMPLE:
 * .ride              (lists the mounts with a free seat)
 * .ride Some Wizard
 * .ride off
 */

using System;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Mounts;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandRideProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("ride")]
    private void RideCommand([Remainder] string who = "") {
        var me = Context.Character;
        var text = (who ?? "").Trim();
        if (!MountRides.Enabled) {
            InformSenderClient("Multi-person mounts are off ([Mounts] MultiPersonMounts = false in Imlight.ini).");

            return;
        }

        if (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("stop", StringComparison.OrdinalIgnoreCase)) {
            Context.SessionActor.Tell(new SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_REQUEST { GetOff = true });

            return;
        }

        var drivers = MountRides.Drivers
            .Where(driver => driver.CharId != me.CharId
                             && (!MountRides.RequireGroup || GroupRegistry.AreGrouped(me.CharId, driver.CharId)))
            .ToList();
        if (text.Length == 0) {
            InformSenderClient(drivers.Count == 0
                ? "Nobody in your group is on a mount with room for passengers."
                : "Mounts you can ride: " + string.Join("; ", drivers.Select(driver =>
                    $"{driver.Name} ({driver.RiderIds.Length} of {driver.Passengers} seats taken)")) + ". Type .ride and their name.");

            return;
        }

        var match = drivers.FirstOrDefault(driver => string.Equals(driver.Name, text, StringComparison.OrdinalIgnoreCase))
                    ?? drivers.FirstOrDefault(driver => driver.Name?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true);
        if (match is null) {
            InformSenderClient($"Nobody named {text} is on a mount you can ride{(MountRides.RequireGroup ? " (they have to be in your group)" : "")}.");

            return;
        }

        Context.SessionActor.Tell(new SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_REQUEST { DriverCharId = match.CharId });
    }

}
