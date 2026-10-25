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
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Results.Handlers;

internal sealed class ResDisplayTextHandler : BaseResultHandler<ResDisplayText> {

    public override bool Execute(IResultContext context) {
        var msg = new GAME_5_PROTOCOL.MSG_CLIENTNOTIFYTEXT {
            NotifyText = Result.m_text,
            Type = Result.m_type
        };

        // A raid's lines are for everyone in it (m_unknown_bool: the whole zone, m_unknown_bool_2: everyone near a spot,
        // which in a raid is everyone), and a line with no player to tell goes to the zone too.
        var playerRef = context.GetPlayerRef();
        if (Raids.Raids.IsRaidZone(context.GetZonePath())
            && (playerRef is null || Result.m_unknown_bool || Result.m_unknown_bool_2)) {
            return RaidResult.TellEveryone(context, msg);
        }

        if (playerRef is null) {
            return false;
        }

        playerRef.Tell(msg);

        return true;
    }

}