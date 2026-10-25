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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
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
    // Lines the app sends every player before it restarts, updates or stops the server (Niduus, 2026-10-23).
    private const string ANNOUNCEMENTS_FILE = "server-announcements.txt";

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
        TryDelete(ANNOUNCEMENTS_FILE);
        s_timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
    }

    private static void Poll() {
        PollAnnouncements();
        try {
            if (!File.Exists(COMMANDS_FILE)) {
                return;
            }

            // Moving the file first keeps a command the app adds meanwhile for the next round.
            var taken = COMMANDS_FILE + ".running";
            File.Move(COMMANDS_FILE, taken, overwrite: true);
            var commands = File.ReadAllLines(taken).Select(ForWizard)
                .Where(line => line.Command.Length > 0).ToList();
            TryDelete(taken);

            var players = OnlinePlayerCollection.GetOnlinePlayers();
            foreach (var (wizard, command) in commands) {
                Logger.Information("Imlight app command: {0}{1}", Logger.Args(command, wizard is { } id ? $" (for wizard {id})" : ""));
                foreach (var player in players.Where(p => wizard is null || p.CharacterId == wizard)) {
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

    /// <summary>
    /// A line the app starts with "@&lt;wizard id&gt; " runs only for that wizard (the Quests tab's buttons, for the wizard
    /// picked there: Niduus, 2026-10-24, playing two wizards on one PC); any other line runs for every wizard on this PC.
    /// </summary>
    private static (ulong? Wizard, string Command) ForWizard(string line) {
        line = line.Trim();
        ulong? wizard = null;
        if (line.StartsWith('@')) {
            var space = line.IndexOf(' ');
            if (space > 1 && ulong.TryParse(line.AsSpan(1, space - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) {
                wizard = id;
                line = line[(space + 1)..].Trim();
            }
        }

        return (wizard, line.TrimStart('.').Trim());
    }

    private static void PollAnnouncements() {
        try {
            if (!File.Exists(ANNOUNCEMENTS_FILE)) {
                return;
            }

            var taken = ANNOUNCEMENTS_FILE + ".sending";
            File.Move(ANNOUNCEMENTS_FILE, taken, overwrite: true);
            var lines = File.ReadAllLines(taken).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
            TryDelete(taken);

            foreach (var line in lines) {
                // The app's Announcement card puts "box|", "chat|" or "both|" in front; a plain line is a box.
                var (box, chat, text, fromCard) = Split(line);
                Logger.Information("Imlight app announcement to everyone ({0}): {1}", Logger.Args(box && chat ? "box and chat" : box ? "box" : "chat", text));
                foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(p => p.ActorPath is not null)) {
                    var actor = s_system.ActorSelection(player.ActorPath);
                    if (box) {
                        actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = fromCard ? AnnouncementBox(text) : text, Modal = 1 });
                    }

                    if (chat) {
                        actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = AnnouncementChat(text), Modal = 0 });
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The app is still writing the file; it is read on the next round.
        }
        catch (Exception ex) {
            Logger.Error("Could not send the Imlight app's announcement: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>
    /// An announcement as the game's message box shows it (Modal 1), with the header the app's Announcement card puts
    /// on. The cheats' "announce to the battle" uses the same wording, so both look alike. The words come from the
    /// Manager's Announcements tab (Announcements.cs); turned off there, just the text shows.
    /// </summary>
    internal static string AnnouncementBox(string text) => Announcements.Text("manager.box", ("text", text)) ?? text;

    /// <summary>An announcement as a chat line (Modal 0).</summary>
    internal static string AnnouncementChat(string text) => Announcements.Text("manager.chat", ("text", text)) ?? text;

    private static (bool Box, bool Chat, string Text, bool FromCard) Split(string line) {
        var bar = line.IndexOf('|');
        var where = bar > 0 ? line[..bar].Trim().ToLowerInvariant() : "";

        return where switch {
            "box" => (true, false, line[(bar + 1)..].Trim(), true),
            "chat" => (false, true, line[(bar + 1)..].Trim(), true),
            "both" => (true, true, line[(bar + 1)..].Trim(), true),
            _ => (true, false, line, false),
        };
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
