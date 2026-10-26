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

using System;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Results.Contexts;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResTeleportHandler : BaseResultHandler<ResTeleport> {

    public override bool Execute(IResultContext context) {
        if (context.GetPlayerObj() is not WizClientObject playerObj) {
            return false;
        }

        // A player who spawned inside a door's volume would be sent straight back out of it.
        if (context.IsPlayerSpawn()) {
            return true;
        }

        var destinationZone = Result.m_destinationZone;
        var destinationLocation = Result.m_destinationLoc;
        if (string.IsNullOrEmpty(destinationZone)) {
            // A raid's teleport pad names no destination either: it goes to the location at the pad's other end.
            if (context is GenericResultContext { TriggerName: { } triggerName }
                && Raids.Raids.TeleportTarget(context.GetZonePath(), triggerName) is { } padTarget) {
                return RaidResult.MoveTo(context, padTarget);
            }

            // The Deckathalon's tower floors name none either: their portals go up a floor or back to the arena.
            if (Events.Deckathalon.FloorTeleport(context.GetPlayerRef(), playerObj.m_characterId, context.GetZonePath(),
                    (context as GenericResultContext)?.TriggerName)) {
                return true;
            }

            // The exits of a house zone name no destination, so the player leaves to the hub of the house's world.
            if (!HouseExits.TryResolve(context.GetZonePath(), out destinationZone, out destinationLocation)) {
                // A dungeon's exit that names none takes the player back to where they were before the dungeon (their
                // session knows whether this zone is an instance and where that was).
                if ((context as GenericResultContext)?.TriggerName is { } exitName
                    && exitName.Contains("Exit", StringComparison.OrdinalIgnoreCase)) {
                    context.GetPlayerRef().Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                        LeaveInstance = true,
                        SendToClient = true,
                        OwnerCharId = playerObj.m_characterId,
                    });

                    return true;
                }

                Logger.Warning("A teleport in {0} names no destination zone; the player stays where they are.",
                    Logger.Args(context.GetZonePath()));

                return false;
            }
        }

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = destinationZone,
            DestinationLocation = destinationLocation,
            SendToClient = true,
            OwnerCharId = playerObj.m_characterId
        };

        context.GetPlayerRef().Tell(msg);

        return true;
    }

}
