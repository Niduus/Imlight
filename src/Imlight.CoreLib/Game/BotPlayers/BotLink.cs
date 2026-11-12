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
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// The game server's side of the Bot Players mini server (BotBrain): starts it while [BotPlayers] Enabled is on (off by
/// default), starts it again if it stops, kills it when the switch is turned off (and sends every bot away), and asks it
/// what the bots do. Without the mini server there are no bots: unlike the Bazaar, the game server has no stand-in.
/// </summary>
public static class BotLink {

    // How long the brain may take to answer one question.
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(3);

    private static readonly Lock s_lock = new();
    private static Process s_brain;
    private static DateTime s_brainStarted;
    private static int s_brainPort;
    private static int s_failedStarts;
    private static volatile bool s_ready;
    private static volatile bool s_wasEnabled;

    /// <summary>Whether the mini server is running and answering, so bots can come out.</summary>
    public static bool Ready => s_ready && BotWire.Enabled;

    /// <summary>Starts looking after the mini server: on while the switch is on, off (killed) while it is off.</summary>
    public static void StartWatching() {
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(10));
            while (true) {
                try {
                    Watch();
                }
                catch (Exception e) {
                    Logger.Error("Bot Players: could not look after the mini server: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        });
    }

    private static void Watch() {
        if (!BotWire.Enabled) {
            if (s_wasEnabled) {
                s_wasEnabled = false;
                Logger.Information("Bot Players was turned off: every bot leaves and the mini server stops.");
                BotBodies.RemoveAll("Bot Players was turned off");
            }

            StopBrain("[BotPlayers] Enabled is off");

            return;
        }

        if (!s_wasEnabled) {
            s_wasEnabled = true;
            Logger.Information("Bot Players is on: starting its mini server.");
        }

        bool running;
        lock (s_lock) {
            running = s_brain is { HasExited: false };
        }

        if (running && s_brainPort != BotWire.Port) {
            StopBrain("[BotPlayers] Port changed");
            running = false;
        }

        if (!running) {
            if (s_ready) {
                // It stopped: the bots it was running have no brain, so they leave too.
                BotBodies.RemoveAll("the Bot Players mini server stopped");
            }

            s_ready = false;
            StartBrain();

            return;
        }

        var answer = Ask(new BotRequest { Op = "ping" }, quiet: true);
        var ready = answer?.Ok == true;
        if (ready && !s_ready) {
            Logger.Information("Bot Players: the mini server is ready ({0} bots in its roster).", Logger.Args(answer.Known));
            s_failedStarts = 0;
            // A new brain knows of no bot out yet; one that ran before forgets those of a game server that started again.
            if (BotBodies.Count == 0) {
                Ask(new BotRequest { Op = "reset", Text = "the game server just connected" }, quiet: true);
            }
        }

        s_ready = ready;
    }

    private static void StartBrain() {
        lock (s_lock) {
            // A brain that keeps stopping is tried less often: 10 s, 20 s, ... up to 5 minutes.
            var wait = TimeSpan.FromSeconds(Math.Min(300, 10 * (1 << Math.Min(s_failedStarts, 5))));
            if (s_brain is not null && DateTime.UtcNow - s_brainStarted < wait) {
                return;
            }

            if (s_brain is not null) {
                Logger.Warning("Bot Players: the mini server stopped (exit code {0}); starting it again. Its log is in {1}.",
                    Logger.Args(SafeExitCode(s_brain), BotWire.Folder));
                s_failedStarts++;
                s_brain.Dispose();
                s_brain = null;
            }

            var self = Environment.ProcessPath;
            if (self is null) {
                return;
            }

            var start = new ProcessStartInfo(self) {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // A framework-dependent build runs through dotnet; pass the program file along.
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }

            foreach (var arg in new[] { "--bots", "--parent", Environment.ProcessId.ToString() }) {
                start.ArgumentList.Add(arg);
            }

            start.Environment[Logger.LOG_PATH_VARIABLE] = Path.Combine(BotWire.Folder, BotWire.LOG_FILE);
            Directory.CreateDirectory(BotWire.Folder);
            try {
                s_brain = Process.Start(start);
                s_brainStarted = DateTime.UtcNow;
                s_brainPort = BotWire.Port;
                Logger.Information("Bot Players: started the mini server (process {0}, port {1}).", Logger.Args(s_brain?.Id, s_brainPort));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                Logger.Error("Bot Players: could not start the mini server, so there are no bots. {0}", Logger.Args(e.Message));
                s_brainStarted = DateTime.UtcNow;
            }
        }
    }

    private static int? SafeExitCode(Process process) {
        try {
            return process.ExitCode;
        }
        catch (InvalidOperationException) {
            return null;
        }
    }

    private static void StopBrain(string why) {
        lock (s_lock) {
            s_ready = false;
            if (s_brain is null) {
                return;
            }

            try {
                if (!s_brain.HasExited) {
                    Logger.Information("Bot Players: stopping the mini server ({0}).", Logger.Args(why));
                    s_brain.Kill();
                }
            }
            catch (InvalidOperationException) {
                // Already gone.
            }

            s_brain.Dispose();
            s_brain = null;
            s_failedStarts = 0;
        }

        CloseLine();
    }

    // ---------------------------------------------------------------------
    // Questions
    // ---------------------------------------------------------------------

    private static readonly Lock s_lineLock = new();
    private static TcpClient s_line;
    private static StreamReader s_reader;
    private static StreamWriter s_writer;

    /// <summary>Asks the brain; null when it is not there or did not answer in time.</summary>
    internal static BotReply Ask(BotRequest request, bool quiet = false) {
        lock (s_lineLock) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    if (s_line is not { Connected: true }) {
                        CloseLineLocked();
                        var line = new TcpClient { NoDelay = true, ReceiveTimeout = (int) s_answerWait.TotalMilliseconds, SendTimeout = (int) s_answerWait.TotalMilliseconds };
                        if (!line.ConnectAsync("127.0.0.1", BotWire.Port).Wait(TimeSpan.FromSeconds(1))) {
                            line.Dispose();

                            return null;
                        }

                        s_line = line;
                        var stream = line.GetStream();
                        s_reader = new StreamReader(stream, Encoding.UTF8);
                        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    s_writer.WriteLine(BotWire.Write(request));
                    var answer = s_reader.ReadLine();
                    if (answer is not null) {
                        return BotWire.Read<BotReply>(answer);
                    }

                    CloseLineLocked();
                }
                catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException
                                              or System.Text.Json.JsonException) {
                    if (!quiet && attempt == 1) {
                        Logger.Warning("Bot Players: the mini server did not answer {0}: {1}", Logger.Args(request.Op, e.GetBaseException().Message));
                    }

                    CloseLineLocked();
                }
            }

            return null;
        }
    }

    /// <summary>Tells the brain what happened to a bot, for its log, without waiting.</summary>
    internal static void Note(int id, string name, string what, string text) {
        Logger.Information("Bot Players | bot #{0} ({1}) {2}: {3}", Logger.Args(id, name, what, text));
        if (!Ready) {
            return;
        }

        _ = Task.Run(() => Ask(new BotRequest { Op = "event", Id = id, Name = name, Event = what, Text = text }, quiet: true));
    }

    private static void CloseLine() {
        lock (s_lineLock) {
            CloseLineLocked();
        }
    }

    private static void CloseLineLocked() {
        s_reader?.Dispose();
        s_writer?.Dispose();
        s_line?.Dispose();
        s_reader = null;
        s_writer = null;
        s_line = null;
    }

}
