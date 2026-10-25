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
 * CLIENT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages client connection lifecycle, handling disconnection and logout 
 * processes for game server sessions.
 * 
 * USAGE EXAMPLE:
 * Internal service for managing client connection state and graceful 
 * disconnection mechanisms.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

internal class ClientService(SessionActor sessionActor) : MessageService(sessionActor) {
    
    private static readonly TimeSpan s_instanceQueryTimeout = TimeSpan.FromSeconds(2);

    // The properties the game writes in MSG_REQASKSERVER (checked against its bytes for ReqHasEntry).
    private const uint RequirementMask = 7;

    protected static Props Props(SessionActor parentActor) 
        => Akka.Actor.Props.Create(() => new ClientService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect() {
        Logger.Information("SessionActor {SessionId} CLIENT_DISCONNECT received.", Logger.Args(SessionActor.SessionID));

        SendToSocket(new GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT());

        Logger.Information("SessionActor {SessionId} CLIENT_DISCONNECT echoed.", Logger.Args(SessionActor.SessionID));

        // The client closes its end of the connection after the echo, so the server does not close first.
        CloseSessionAfterClient();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) {
        Logger.Information("SessionActor {SessionId} QUERY_LOGOUT received.", Logger.Args(SessionActor.SessionID));

        var isInstance = QueryIsInstance(SessionActor.GetZoneActor(), SessionActor.SessionID);
        SendToSocket(new GAME_5_PROTOCOL.MSG_QUERY_LOGOUT { IsInstance = isInstance });

        Logger.Information("SessionActor {SessionId} QUERY_LOGOUT replied (IsInstance={IsInstance}).",
            Logger.Args(SessionActor.SessionID, isInstance));
    }

    /// <summary>
    /// The game asks the server about a requirement it cannot check itself, mostly "has this wizard finished quest X"
    /// (a ReqHasEntry on "&lt;quest&gt;_Complete"), and waits for the answer. Unicorn Way asks about 50 of these on the
    /// way in (Niduus's log of 2026-10-25); none were answered, so the area took long to settle. The answer is the
    /// requirement's own result: like a requirement list, the game applies its NOT itself.
    /// </summary>
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQASKSERVER))]
    private void ReceiveReqServer(GAME_5_PROTOCOL.MSG_REQASKSERVER message) {
        var met = false;
        byte[] bytes = message.Requirement;
        if (TryParseRequirement(bytes, out var requirement) && GetActiveWizard() is { } wizard) {
            var context = new GenericRequirementContext(new RequirementList(), SessionActor.ActorRef,
                GetActiveGameObject(), wizard);
            met = RequirementDispatcher.EvaluateIndividualRequirement(requirement, context);
            Logger.Verbose("SessionActor {0}: the game asked about {1} {2}: {3}.",
                Logger.Args(SessionActor.SessionID, requirement.GetType().Name,
                            (requirement as ReqHasEntry)?.m_entryName, met));
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_REQASKSERVER {
            RequestID = message.RequestID,
            Requirement = message.Requirement,
            Response = met ? 1u : 0u
        });
    }

    private static bool TryParseRequirement(byte[] bytes, out Requirement requirement) {
        requirement = null;
        if (bytes is null || bytes.Length == 0) {
            return false;
        }

        try {
            return new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None)
                .Deserialize(bytes, RequirementMask, out requirement) && requirement is not null;
        }
        catch (Exception ex) {
            Logger.Debug("A requirement the game asked about could not be read: {0}", Logger.Args(ex.Message));

            return false;
        }
    }

    private static byte QueryIsInstance(IActorRef zoneActor, ushort sessionId) {
        if (zoneActor is null) {
            return 0;
        }

        try {
            var rsp = zoneActor.Ask<ZONE_102_PROTOCOL.MSG_QUERYINSTANCEQUESTSRSP>(
                new ZONE_102_PROTOCOL.MSG_QUERYINSTANCEQUESTS(), s_instanceQueryTimeout).Result;

            return (byte) (rsp.IsInstance ? 1 : 0);
        }
        catch (Exception ex) {
            Logger.Error("SessionActor {SessionId} instance query failed: {Message}", Logger.Args(sessionId, ex.Message));

            return 0;
        }
    }

}
