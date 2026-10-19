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
using System.Threading;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Runs the commands typed in the Imlight app. The app adds them to manager-commands.txt next to the server; twice a
/// second the server takes the file and runs each line as a command of the wizard playing on this PC. Players on other
/// PCs never run them (CommandService checks).
/// </summary>
internal static class ManagerCommands {

    private const string COMMANDS_FILE = "manager-commands.txt";

    private static Timer s_timer;
    private static ActorSystem s_system;

    /// <summary>
    /// Starts watching for commands. Calling it again does nothing.
    /// </summary>
    internal static void Start(ActorSystem system) {
        if (Interlocked.CompareExchange(ref s_system, system, null) is not null) {
            return;
        }

        // Commands left from before the server started are not run.
        TryDelete(COMMANDS_FILE);
        s_timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
    }

    private static void Poll() {
        try {
            if (!File.Exists(COMMANDS_FILE)) {
                return;
            }

            // Moving the file first keeps a command the app adds meanwhile for the next round.
            var taken = COMMANDS_FILE + ".running";
            File.Move(COMMANDS_FILE, taken, overwrite: true);
            var commands = File.ReadAllLines(taken).Select(line => line.Trim().TrimStart('.').Trim())
                .Where(line => line.Length > 0).ToList();
            TryDelete(taken);

            var players = OnlinePlayerCollection.GetOnlinePlayers();
            foreach (var command in commands) {
                Logger.Information("Imlight app command: {0}", Logger.Args(command));
                foreach (var player in players) {
                    s_system.ActorSelection(player.ActorPath).Tell(new SERVICE_101_PROTOCOL.MSG_MANAGERCOMMAND { Command = command });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The app is still writing the file; it is read on the next round.
        }
        catch (Exception ex) {
            Logger.Error("Could not run the Imlight app's commands: {0}", Logger.Args(ex.Message));
        }
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
