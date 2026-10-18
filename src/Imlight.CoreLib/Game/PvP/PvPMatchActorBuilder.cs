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
 * PVP
 * ========================================================================
 * 
 * PURPOSE:
 * Builds the serialized MatchActor the client keeps for its own queue state. The client opens and closes its
 * status window from the status of this object.
 * 
 * USAGE EXAMPLE:
 * var actorBlob = PvPMatchActorBuilder.BuildQueued(entry, PvPMatchActorBuilder.StatusFindingMatch);
 * 
 * NOTE:
 * The idle actor equals the one a live server sends on login. The status values are the ones the client
 * reads: 4 finding a match, 8 match found, 10 waiting for the opponent, 11 waiting to teleport, 12 in the arena.
 * The time the player entered the queue is Unix seconds, which is inferred.
 * 
 * TODO:
 * What does the client do with m_enteredQueue? The idle actor of a live server carries 41.
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.PvP;

internal static class PvPMatchActorBuilder {

    public const int STATUS_IDLE = 0;
    public const int STATUS_FINDING_MATCH = 4;
    public const int STATUS_MATCH_FOUND = 8;
    public const int STATUS_WAITING_FOR_OPPONENT = 10;
    public const int STATUS_WAITING_TO_TELEPORT = 11;
    public const int STATUS_IN_ARENA = 12;

    private const int IdleEnteredQueue = 41;

    /// <summary>
    /// The actor of a player who is not in any queue or match. Sending it resets the status window of the client.
    /// </summary>
    public static ByteString BuildIdle(ulong charId)
        => Serialize(Create(charId, STATUS_IDLE, null));

    public static ByteString BuildQueued(PvPQueueEntry entry, int status)
        => Serialize(Create(entry.CharId, status, entry));

    private static MatchActor Create(ulong charId, int status, PvPQueueEntry entry) {
        var actor = new MatchActor {
            m_nActorID = charId,
            m_status = status,
            m_costAdj = new MatchCostAdjustment { m_matchAdjustmentType = ADJUSTMENT_TYPE.UNSET_MAX },
            m_overridingELO = -1,
            m_matchKFactor = 32,
            m_allowRangeToExceedMaxForMatches = true,
            m_matchCrownsCost = -1,
            m_enteredQueue = IdleEnteredQueue,
            m_meetBracketBadgeRequirnments = true,
            m_optInOut = "0"
        };

        if (entry is not null) {
            actor.m_nTournamentNameID = entry.TournamentNameId;
            actor.m_nMatchNameID = entry.MatchNameId;
            actor.m_leagueID = entry.LeagueId;
            actor.m_seasonID = entry.SeasonId;
            actor.m_allowRangeToExceedMaxForMatches = entry.ExpandSearch;
            actor.m_enteredQueue = (int) new DateTimeOffset(entry.EnteredAtUtc).ToUnixTimeSeconds();
            actor.m_rating = entry.Rating;
            actor.m_1v1 = true;
        }

        return actor;
    }

    private static ByteString Serialize(MatchActor actor) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(actor, 4, out var data)) {
            Logger.Error("Failed to serialize the match actor of {CharId}.", Logger.Args((ulong) actor.m_nActorID));

            return default;
        }

        return data;
    }

}
