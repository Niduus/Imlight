/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Where the Quest Team plays: the first game server that starts. Its sessions are opened there like a game's
/// connections (Server.HeadlessSessionOpen), and their login keys come from it like the login server's.
/// </summary>
internal static class QuestTeamHost {

    private static volatile IActorRef s_server;
    private static volatile ActorSystem s_system;

    internal static IActorRef Server => s_server;

    internal static ActorSystem System => s_system;

    /// <summary>A game server started: the first one is the Quest Team's.</summary>
    internal static void GameServerReady(IActorRef server, ActorSystem system) {
        if (s_server is null) {
            s_system = system;
            s_server = server;
        }
    }

    /// <summary>A new session for the pretend game <paramref name="client"/>, or null when the server did not open one.</summary>
    internal static async Task<(IActorRef Session, ushort Id)> OpenSession(IActorRef client, string who) {
        if (s_server is not { } server) {
            return (null, 0);
        }

        try {
            var opened = await server.Ask<HeadlessSessionOpened>(new HeadlessSessionOpen(client, who), TimeSpan.FromSeconds(10));

            return (opened.Session, opened.Id);
        }
        catch (Exception) {
            return (null, 0);
        }
    }

    /// <summary>A login key for the account, as the login server gets one when a player picks a wizard.</summary>
    internal static async Task<ByteString?> CreateKey(Account account) {
        if (s_server is not { } server) {
            return null;
        }

        try {
            var answer = await server.Ask<SERVER_100_PROTOCOL.MSG_CREATEKEYRSP>(new SERVER_100_PROTOCOL.MSG_CREATEKEY { Account = account },
                TimeSpan.FromSeconds(10));

            return answer.Key;
        }
        catch (Exception) {
            return null;
        }
    }

}
