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

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResPostEventHandler : BaseResultHandler<ResPostEvent> {

    // A quest built from the game's own files (tools/game-quests) writes the game's ResAddEncounter this way: the server's
    // type for it has no field for the encounter's name.
    internal const string START_ENCOUNTER_PREFIX = "ImlightStartEncounter:";

    public override bool Execute(IResultContext context) {
        string eventName = Result?.m_eventName;
        if (eventName is not null && eventName.StartsWith(START_ENCOUNTER_PREFIX, System.StringComparison.Ordinal)) {
            var playerRef = context.GetPlayerRef();
            if (playerRef is null) {
                return false;
            }

            playerRef.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
                Action = "encounter",
                QuestName = eventName[START_ENCOUNTER_PREFIX.Length..],
            }, ActorRefs.NoSender);

            return true;
        }

        var zoneActor = context.GetZoneActor();
        if (zoneActor == null) {
            return false;
        }

        if (Result is null) {
            return false;
        }

        var msg = new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            PlayerActor = context.GetPlayerRef(),
            PlayerGameObject = context.GetPlayerObj(),
            EventName = Result.m_eventName,
            PlayerSpawned = context.IsPlayerSpawn()
        };

        zoneActor.Tell(msg);

        return true;
    }

}