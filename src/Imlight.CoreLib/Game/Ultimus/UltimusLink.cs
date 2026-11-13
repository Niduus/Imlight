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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The game server's side of Ultimus's mini server (UltimusBrain), forked from the Quest Team's QuestTeamLink: starts it
/// while [Ultimus] Enabled is on (off by default), starts it again if it stops, kills it when the switch is turned off (and
/// Ultimus logs out), and tells it what Ultimus does. Ultimus only plays while its mini server answers.
/// </summary>
public static class UltimusLink {

    // How long the brain may take to answer one question.
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(3);

    private static readonly Lock s_lock = new();
    private static Process s_brain;
    private static DateTime s_brainStarted;
    private static int s_brainPort;
    private static int s_failedStarts;
    private static volatile bool s_ready;
    private static volatile bool s_wasEnabled;

    /// <summary>Whether the mini server is running and answering, so Ultimus can play.</summary>
    public static bool Ready => s_ready && UltimusWire.Enabled;

    /// <summary>Starts looking after the mini server: on while the switch is on, off (killed) while it is off.</summary>
    public static void StartWatching() {
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(10));
            while (true) {
                try {
                    Watch();
                }
                catch (Exception e) {
                    Logger.Error("Ultimus: could not look after the mini server: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        });
    }

    private static void Watch() {
        if (!UltimusWire.Enabled) {
            if (s_wasEnabled) {
                s_wasEnabled = false;
                Logger.Information("Ultimus was turned off: Ultimus logs out and the mini server stops.");
                UltimusDirector.Stop("Ultimus was turned off");
            }

            StopBrain("[Ultimus] Enabled is off");

            return;
        }

        if (!s_wasEnabled) {
            s_wasEnabled = true;
            Logger.Information("Ultimus is on: starting its mini server.");
        }

        bool running;
        lock (s_lock) {
            running = s_brain is { HasExited: false };
        }

        if (running && s_brainPort != UltimusWire.Port) {
            StopBrain("[Ultimus] Port changed");
            running = false;
        }

        if (!running) {
            if (s_ready) {
                // It stopped: Ultimus waits (logged out) until it is back.
                UltimusDirector.Stop("the Ultimus mini server stopped");
            }

            s_ready = false;
            StartBrain();

            return;
        }

        var answer = Ask(new UltimusRequest { Op = "ping" }, quiet: true);
        var ready = answer?.Ok == true;
        if (ready && !s_ready) {
            Logger.Information("Ultimus: the mini server is ready.");
            s_failedStarts = 0;
        }

        s_ready = ready;

        // Ultimus plays while its mini server answers.
        if (ready) {
            UltimusDirector.EnsureRunning();
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
                Logger.Warning("Ultimus: the mini server stopped (exit code {0}); starting it again. Its log is in {1}.",
                    Logger.Args(SafeExitCode(s_brain), UltimusWire.Folder));
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

            foreach (var arg in new[] { "--ultimus", "--parent", Environment.ProcessId.ToString() }) {
                start.ArgumentList.Add(arg);
            }

            start.Environment[Logger.LOG_PATH_VARIABLE] = Path.Combine(UltimusWire.Folder, UltimusWire.LOG_FILE);
            Directory.CreateDirectory(UltimusWire.Folder);
            try {
                s_brain = Process.Start(start);
                s_brainStarted = DateTime.UtcNow;
                s_brainPort = UltimusWire.Port;
                Logger.Information("Ultimus: started the mini server (process {0}, port {1}).", Logger.Args(s_brain?.Id, s_brainPort));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                Logger.Error("Ultimus: could not start the mini server, so Ultimus does not play. {0}", Logger.Args(e.Message));
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
                    Logger.Information("Ultimus: stopping the mini server ({0}).", Logger.Args(why));
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
    internal static UltimusReply Ask(UltimusRequest request, bool quiet = false) {
        lock (s_lineLock) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    if (s_line is not { Connected: true }) {
                        CloseLineLocked();
                        var line = new TcpClient { NoDelay = true, ReceiveTimeout = (int) s_answerWait.TotalMilliseconds, SendTimeout = (int) s_answerWait.TotalMilliseconds };
                        if (!line.ConnectAsync("127.0.0.1", UltimusWire.Port).Wait(TimeSpan.FromSeconds(1))) {
                            line.Dispose();

                            return null;
                        }

                        s_line = line;
                        var stream = line.GetStream();
                        s_reader = new StreamReader(stream, Encoding.UTF8);
                        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    s_writer.WriteLine(UltimusWire.Write(request));
                    var answer = s_reader.ReadLine();
                    if (answer is not null) {
                        return UltimusWire.Read<UltimusReply>(answer);
                    }

                    CloseLineLocked();
                }
                catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException
                                              or System.Text.Json.JsonException) {
                    if (!quiet && attempt == 1) {
                        Logger.Warning("Ultimus: the mini server did not answer {0}: {1}", Logger.Args(request.Op, e.GetBaseException().Message));
                    }

                    CloseLineLocked();
                }
            }

            return null;
        }
    }

    // How an Ultimus log line is coloured in Ahziael (Niduus, 2026-11-12, same as the Quest Team's): no tag is white
    // (normal), MINOR yellow (does not hold the quest back), NOTABLE orange (could stop a quest finishing), CRITICAL red
    // (stops or stopped the story, fix now), COMMAND cyan (something Ahziael did for it, with the reason), ABNORMAL pink
    // (something abnormal it noticed: "Ultimus and QT are to report in PINK abnormal behavior they've notice").
    internal const string MINOR = "MINOR";
    internal const string NOTABLE = "NOTABLE";
    internal const string CRITICAL = "CRITICAL";
    internal const string COMMAND = "COMMAND";
    internal const string ABNORMAL = "ABNORMAL";
    // Ultimus thinking out loud (brown in Ahziael's Ultimus Thoughts; Niduus, 2026-11-13).
    internal const string THOUGHT = "THOUGHT";

    /// <summary>Tells the brain what Ultimus did, for its log, without waiting.</summary>
    internal static void Note(string who, string text, string level = null) {
        if (!string.IsNullOrEmpty(level)) {
            text = $"{level}: {text}";
        }

        Logger.Information("Ultimus | {0}: {1}", Logger.Args(who, text));
        Send(new UltimusRequest { Op = "note", Who = who, Text = text });
    }

    /// <summary>Tells the brain of an issue, which Ahziael announces.</summary>
    internal static void Issue(string kind, string quest, string title, string world, string text, string expected = "",
                               List<string> seen = null, string step = "", List<string> debug = null) {
        Logger.Warning("Ultimus | ISSUE ({0}) {1}: {2} Expected: {3}", Logger.Args(kind, string.IsNullOrEmpty(title) ? quest : title, text, expected));
        Send(new UltimusRequest { Op = "issue", Kind = kind, Quest = quest, Title = title, World = world, Text = text, Expected = expected, Seen = seen ?? [],
            Step = step ?? "", Debug = debug ?? [] });
    }

    /// <summary>
    /// Something abnormal Ultimus noticed (pink in Ahziael): not a broken step, but not what a player would expect either
    /// (an odd server message, being moved without asking, a battle with nothing in it...).
    /// </summary>
    internal static void Abnormal(string text) => Note("Ultimus", text, ABNORMAL);

    /// <summary>A mistake Ultimus made and the lesson it takes from it, kept in learned.jsonl.</summary>
    internal static void Learned(string kind, string quest, string step, string world, string mistake, string lesson) {
        Note("Ultimus", $"LEARNED: {mistake} Next time: {lesson}", MINOR);
        Send(new UltimusRequest { Op = "learned", Kind = kind, Quest = quest, Step = step, World = world, Text = mistake, Expected = lesson });
    }

    /// <summary>Tells the brain without waiting for its answer.</summary>
    internal static void Send(UltimusRequest request) {
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
