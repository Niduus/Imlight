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

using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The game server's side of the Quest Team mini server (QuestTeamBrain), like Bot Players' BotLink: starts it while
/// [QuestTeam] Enabled is on (off by default), starts it again if it stops, kills it when the switch is turned off (and
/// the team logs out), and tells it what the team does. The team only plays while its mini server answers.
/// </summary>
public static class QuestTeamLink {

    // How long the brain may take to answer one question.
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(3);

    private static readonly Lock s_lock = new();
    private static Process s_brain;
    private static DateTime s_brainStarted;
    private static int s_brainPort;
    private static int s_failedStarts;
    private static volatile bool s_ready;
    private static volatile bool s_wasEnabled;

    /// <summary>Whether the mini server is running and answering, so the team can play.</summary>
    public static bool Ready => s_ready && QuestTeamWire.Enabled;

    /// <summary>Starts looking after the mini server: on while the switch is on, off (killed) while it is off.</summary>
    public static void StartWatching() {
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(10));
            while (true) {
                try {
                    Watch();
                }
                catch (Exception e) {
                    Logger.Error("Quest Team: could not look after the mini server: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        });
    }

    private static void Watch() {
        if (!QuestTeamWire.Enabled) {
            if (s_wasEnabled) {
                s_wasEnabled = false;
                Logger.Information("Quest Team was turned off: the team logs out and the mini server stops.");
                QuestTeamDirector.Stop("Quest Team was turned off");
            }

            StopBrain("[QuestTeam] Enabled is off");

            return;
        }

        if (!s_wasEnabled) {
            s_wasEnabled = true;
            Logger.Information("Quest Team is on: starting its mini server.");
        }

        bool running;
        lock (s_lock) {
            running = s_brain is { HasExited: false };
        }

        if (running && s_brainPort != QuestTeamWire.Port) {
            StopBrain("[QuestTeam] Port changed");
            running = false;
        }

        if (!running) {
            if (s_ready) {
                // It stopped: the team waits (logged out) until it is back.
                QuestTeamDirector.Stop("the Quest Team mini server stopped");
            }

            s_ready = false;
            StartBrain();

            return;
        }

        var answer = Ask(new TeamRequest { Op = "ping" }, quiet: true);
        var ready = answer?.Ok == true;
        if (ready && !s_ready) {
            Logger.Information("Quest Team: the mini server is ready.");
            s_failedStarts = 0;
        }

        s_ready = ready;

        // The team plays while its mini server answers.
        if (ready) {
            QuestTeamDirector.EnsureRunning();
        }
    }

    private static void StartBrain() {
        lock (s_lock) {
            // A brain that keeps stopping is tried less often: 10 s, 20 s, ... up to 5 minutes.
            var wait = TimeSpan.FromSeconds(Math.Min(300, 10 * (1 << Math.Min(s_failedStarts, 5))));
            if (s_brain is not null && DateTime.UtcNow - s_brainStarted < wait) {
                return;
            }

            if (s_brain is not null) {
                Logger.Warning("Quest Team: the mini server stopped (exit code {0}); starting it again. Its log is in {1}.",
                    Logger.Args(SafeExitCode(s_brain), QuestTeamWire.Folder));
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

            foreach (var arg in new[] { "--questteam", "--parent", Environment.ProcessId.ToString() }) {
                start.ArgumentList.Add(arg);
            }

            start.Environment[Logger.LOG_PATH_VARIABLE] = Path.Combine(QuestTeamWire.Folder, QuestTeamWire.LOG_FILE);
            Directory.CreateDirectory(QuestTeamWire.Folder);
            try {
                s_brain = Process.Start(start);
                s_brainStarted = DateTime.UtcNow;
                s_brainPort = QuestTeamWire.Port;
                Logger.Information("Quest Team: started the mini server (process {0}, port {1}).", Logger.Args(s_brain?.Id, s_brainPort));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                Logger.Error("Quest Team: could not start the mini server, so the team does not play. {0}", Logger.Args(e.Message));
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
                    Logger.Information("Quest Team: stopping the mini server ({0}).", Logger.Args(why));
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
    internal static TeamReply Ask(TeamRequest request, bool quiet = false) {
        lock (s_lineLock) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    if (s_line is not { Connected: true }) {
                        CloseLineLocked();
                        var line = new TcpClient { NoDelay = true, ReceiveTimeout = (int) s_answerWait.TotalMilliseconds, SendTimeout = (int) s_answerWait.TotalMilliseconds };
                        if (!line.ConnectAsync("127.0.0.1", QuestTeamWire.Port).Wait(TimeSpan.FromSeconds(1))) {
                            line.Dispose();

                            return null;
                        }

                        s_line = line;
                        var stream = line.GetStream();
                        s_reader = new StreamReader(stream, Encoding.UTF8);
                        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    s_writer.WriteLine(QuestTeamWire.Write(request));
                    var answer = s_reader.ReadLine();
                    if (answer is not null) {
                        return QuestTeamWire.Read<TeamReply>(answer);
                    }

                    CloseLineLocked();
                }
                catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException
                                              or System.Text.Json.JsonException) {
                    if (!quiet && attempt == 1) {
                        Logger.Warning("Quest Team: the mini server did not answer {0}: {1}", Logger.Args(request.Op, e.GetBaseException().Message));
                    }

                    CloseLineLocked();
                }
            }

            return null;
        }
    }

    /// <summary>Tells the brain what a wizard of the team did, for its log, without waiting.</summary>
    // How a Team Logs line is coloured in Ahziael (Niduus, 2026-11-12): no tag is white (normal), MINOR yellow (does not
    // hold the quest back), NOTABLE orange (could stop a quest finishing), CRITICAL red (stops or stopped the story, fix now),
    // COMMAND cyan (something Ahziael did for them, with the reason).
    internal const string MINOR = "MINOR";
    internal const string NOTABLE = "NOTABLE";
    internal const string CRITICAL = "CRITICAL";
    internal const string COMMAND = "COMMAND";
    // PINK (Niduus, 2026-11-12): something abnormal the team noticed (odd drops, a fight that lasts far too long), not
    // necessarily a broken quest.
    internal const string ABNORMAL = "ABNORMAL";

    internal static void Note(string who, string text, string level = null) {
        if (!string.IsNullOrEmpty(level)) {
            text = $"{level}: {text}";
        }

        Logger.Information("Quest Team | {0}: {1}", Logger.Args(who, text));
        Send(new TeamRequest { Op = "note", Who = who, Text = text });
    }

    /// <summary>Tells the brain of an issue, which Ahziael announces.</summary>
    internal static void Issue(string kind, string quest, string title, string world, string text, string expected = "",
                               List<string> seen = null) {
        Logger.Warning("Quest Team | ISSUE ({0}) {1}: {2} Expected: {3}", Logger.Args(kind, string.IsNullOrEmpty(title) ? quest : title, text, expected));
        Send(new TeamRequest { Op = "issue", Kind = kind, Quest = quest, Title = title, World = world, Text = text, Expected = expected, Seen = seen ?? [] });
    }

    /// <summary>Tells the brain without waiting for its answer.</summary>
    internal static void Send(TeamRequest request) {
        if (!Ready) {
            return;
        }

        _ = Task.Run(() => Ask(request, quiet: true));
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
