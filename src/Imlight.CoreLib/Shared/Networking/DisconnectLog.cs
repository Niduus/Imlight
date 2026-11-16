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
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>
/// One plain line for every time a player's game is disconnected, with whose side it came from (Niduus, 2026-11-16: a
/// player asked "any place where logs could tell me if I'm just dropping connection or if there is something wrong on
/// the server end"). Each line goes in the server log ("Disconnect: ...") and in logs/disconnects/disconnects-DATE.txt,
/// which holds nothing else. Zone changes and the login screen handing the game to the game server are normal and not
/// listed. The server stopping or starting is listed too, since that disconnects everyone.
/// </summary>
internal static class DisconnectLog {

    internal const string FOLDER = "logs/disconnects";

    // Each cause starts with whose side it came from, so the file can be read at a glance.
    private const string PLAYER = "PLAYER'S SIDE: ";
    private const string SERVER = "SERVER SIDE: ";
    private const string NORMAL = "NORMAL: ";
    private const string UNKNOWN = "UNKNOWN: ";
    private const string STOPPING = "the server is stopping";

    private static readonly object s_lock = new();

    internal static string PlayerSide(string why) => PLAYER + why;
    internal static string ServerSide(string why) => SERVER + why;
    internal static string Normal(string why) => NORMAL + why;

    /// <summary>The cause of a failed read from the player's game, in plain words.</summary>
    internal static string ForSocketError(Exception error) => error switch {
        SocketException { SocketErrorCode: SocketError.ConnectionReset } =>
            PlayerSide("the connection was cut from the player's computer (their internet dropped, or the game crashed or was force-closed)"),
        SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.NetworkDown or SocketError.NetworkReset } =>
            PlayerSide($"the connection timed out: nothing more came from the player's computer (their internet dropped; {((SocketException) error).SocketErrorCode})"),
        SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted } =>
            ServerSide($"Windows on this PC ended the connection ({((SocketException) error).SocketErrorCode}): a firewall, antivirus or the network adapter"),
        SocketException socket => UNKNOWN + $"the connection failed ({socket.SocketErrorCode}: {socket.Message})",
        _ => UNKNOWN + $"the connection failed ({error.GetType().Name}: {error.Message})",
    };

    /// <summary>Writes the line for a player's session that just ended. Quest Team sessions and normal hand-offs are left out.</summary>
    internal static void SessionEnded(SessionActor session, string cause, string closeReason, DateTime startedUtc) {
        try {
            // Session numbers are counted per server, so only a game server session can be a player's in the online list.
            var atLogin = session.ServerRef?.Path.Name is { } server && server.Contains(".Login", StringComparison.OrdinalIgnoreCase);
            var player = atLogin ? null : OnlinePlayerCollection.TakeLastOnSession(session.SessionID);
            if (session.IsHeadless) {
                return;
            }

            if (session.ExpectedEnd is { } expected) {
                Logger.Debug("SessionActor {Id} ended as expected: {Why}.", Logger.Args(session.SessionID, expected));

                return;
            }

            var account = player?.AccountName ?? session.GetAssociatedAccount()?.Username;
            var who = player is { WizardName.Length: > 0 } ? $"{player.WizardName} (account {account ?? "?"})"
                : account is not null ? $"account {account}"
                : "a game that had not logged in yet";
            if (player is not null && OnlineList.IsBotWizard(player.CharacterId)) {
                who += " [a server bot]";
            }

            var where = atLogin ? "the login screen"
                : player is { CurrentZone.Length: > 0 } ? $"the game in {player.CurrentZoneDisplayName ?? player.CurrentZone}" : "the game";
            var lasted = DateTime.UtcNow - startedUtc;
            var length = lasted.TotalMinutes >= 1 ? $"{(int) lasted.TotalMinutes} min {lasted.Seconds} s" : $"{lasted.TotalSeconds:0} s";
            cause ??= closeReason == "socket closed"
                ? UNKNOWN + "the connection closed without a reason given"
                : ServerSide($"the server closed it ({closeReason})");

            Write($"{who} from {session.RemoteIp} was disconnected from {where} after {length} on this connection. {cause}.");
        }
        catch (Exception ex) {
            // A note that could not be written never gets in the way of closing the session.
            Logger.Debug("Could not write the disconnect line for session {Id}: {Message}", Logger.Args(session.SessionID, ex.Message));
        }
    }

    /// <summary>The app is stopping or restarting the server: everyone online is about to be disconnected.</summary>
    internal static void ServerStopping(string notice) {
        var people = OnlinePlayerCollection.GetCachedOnlinePlayers()
            .Where(p => !OnlineList.IsBotWizard(p.CharacterId))
            .Select(p => p.WizardName is { Length: > 0 } name ? name : p.AccountName ?? "?")
            .ToList();
        var online = people.Count == 0 ? "Nobody was playing." : $"Everyone online is disconnected: {string.Join(", ", people)}.";
        Write(ServerSide($"{STOPPING} (Ahziael said: \"{notice}\"). {online}"));
    }

    /// <summary>The server started. Says so when the last run ended without the app stopping it (a crash, closed window, PC restart).</summary>
    internal static void ServerStarted() {
        var last = LastLine();
        if (last is not null && !last.Contains(STOPPING, StringComparison.Ordinal) && !last.Contains("server started", StringComparison.Ordinal)) {
            Write(ServerSide("the server stopped before this without Ahziael stopping it (its window was closed, it crashed, or the PC " +
                "restarted or slept). Anyone online then was disconnected."));
        }

        Write(NORMAL + "the server started; players can log in again.");
    }

    private static string Folder => Path.GetFullPath(FOLDER);

    private static void Write(string line) {
        Logger.Information("Disconnect: {Line}", Logger.Args(line));
        lock (s_lock) {
            try {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"disconnects-{DateTime.Now:yyyy-MM-dd}.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // The line is still in the server log.
            }
        }
    }

    private static string LastLine() {
        try {
            var newest = Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("disconnects-*.txt").OrderByDescending(f => f.Name).FirstOrDefault()
                : null;

            return newest is null ? null : File.ReadLines(newest.FullName).LastOrDefault(l => l.Trim().Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

}
