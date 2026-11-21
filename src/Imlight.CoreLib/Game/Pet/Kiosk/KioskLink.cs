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

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The game server's side of the kiosk's mini server (<see cref="KioskBot"/>): wakes it when a player uses a Pet Hatching
/// Kiosk, starts it again if it stops, sends it every question about the pets put up at the kiosk, and lets it sleep
/// [HatchKiosk] IdleMinutes after the last time someone used a kiosk. Same switches as the Bazaar bot: Enabled (off
/// closes the kiosk), IdleMinutes, OwnServer (off has the game server keep the kiosk itself) and Port. The kiosk never
/// waits for the mini server: while it sleeps or starts, the game server reads and writes the same listings file itself.
/// </summary>
public static class KioskLink {

    /// <summary>[HatchKiosk] Enabled: the kiosk's on/off switch in Ahziael. Off closes the Pet Hatching Kiosk.</summary>
    public static bool Enabled => ConfigurationManager.GetValue("HatchKiosk.Enabled", true);

    private static bool OwnServer => ConfigurationManager.GetValue("HatchKiosk.OwnServer", true);

    // Minutes the mini server keeps running after the last time someone used a kiosk, before it sleeps.
    private static TimeSpan IdleTime => TimeSpan.FromMinutes(Math.Clamp(ConfigurationManager.GetValue("HatchKiosk.IdleMinutes", 5), 1, 24 * 60));

    // How long the mini server may take to answer one question, and to open its port after it starts.
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_startWait = TimeSpan.FromMinutes(2);

    private static readonly Lock s_lock = new();
    private static Process s_bot;
    private static DateTime s_botStarted;
    private static int s_botPort;
    private static int s_failedStarts;
    private static volatile bool s_ready;
    private static long s_lastWantedTicks;

    private static DateTime LastWanted => new(Interlocked.Read(ref s_lastWantedTicks), DateTimeKind.Utc);

    private static void MarkWanted() => Interlocked.Exchange(ref s_lastWantedTicks, DateTime.UtcNow.Ticks);

    // The mini server should run: the switches are on, and someone used a kiosk a short while ago.
    private static bool Wanted() => Enabled && OwnServer && DateTime.UtcNow - LastWanted < IdleTime;

    /// <summary>Whether the mini server is running and answering now.</summary>
    public static bool Running => s_ready && Enabled && OwnServer;

    /// <summary>
    /// A player used a kiosk (or its window asked something): wakes the mini server when it sleeps. False when the kiosk
    /// is turned off in Ahziael.
    /// </summary>
    public static bool Visit() {
        if (!Enabled) {
            return false;
        }

        MarkWanted();
        bool asleep;
        lock (s_lock) {
            asleep = s_bot is null;
        }

        if (OwnServer && asleep) {
            // Start it now rather than at the next look, a few seconds later.
            _ = Task.Run(WatchSafely);
        }

        return true;
    }

    /// <summary>
    /// Looks after the mini server every few seconds: starts it while it is wanted, lets it sleep when nobody used a
    /// kiosk for a while, and stops it when [HatchKiosk] Enabled or OwnServer is turned off.
    /// </summary>
    public static void StartWatching() {
        _ = Task.Run(async () => {
            // Not while the server still starts.
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true) {
                WatchSafely();
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }

    private static readonly Lock s_watchLock = new();

    private static void WatchSafely() {
        // One look at a time (Visit can start one between two of the timer's).
        if (!s_watchLock.TryEnter()) {
            return;
        }

        try {
            Watch();
        }
        catch (Exception e) {
            Logger.Error("Kiosk: could not look after the kiosk bot: {0}", Logger.Args(e));
        }
        finally {
            s_watchLock.Exit();
        }
    }

    private static void Watch() {
        if (!Enabled) {
            StopBot("the Pet Hatching Kiosk was turned off in Ahziael");
            KioskWire.WriteStatus("off", 0, HatchmakingStore.Count, KioskWire.Port);

            return;
        }

        if (!OwnServer) {
            StopBot("[HatchKiosk] OwnServer was turned off");
            KioskWire.WriteStatus("inserver", 0, HatchmakingStore.Count, KioskWire.Port);

            return;
        }

        if (!Wanted()) {
            StopBot($"nobody used a kiosk for {IdleTime.TotalMinutes:0} minutes; it starts again when a player uses one");
            KioskWire.WriteStatus("asleep", 0, HatchmakingStore.Count, KioskWire.Port);

            return;
        }

        bool running;
        lock (s_lock) {
            running = s_bot is { HasExited: false };
        }

        if (running && s_botPort != KioskWire.Port) {
            StopBot("[HatchKiosk] Port changed");
            running = false;
        }

        if (!running) {
            s_ready = false;
            StartBot();

            return;
        }

        var answer = Send(new KioskRequest { Op = "ping" }, quiet: true);
        if (answer?.Ok == true && !s_ready) {
            Logger.Information("Kiosk: the kiosk bot is ready with {0} pets; the kiosk asks it from now on.", Logger.Args(answer.Count));
            s_failedStarts = 0;
        }
        else if (answer?.Ok != true && !s_ready && DateTime.UtcNow - s_botStarted > s_startWait) {
            StopBot("it never answered after starting");
            s_failedStarts++;

            return;
        }

        s_ready = answer?.Ok == true;
    }

    private static void StartBot() {
        lock (s_lock) {
            if (s_bot is { HasExited: false }) {
                return;
            }

            // A bot that keeps stopping is tried less often: 20 s, 40 s, ... up to 5 minutes.
            var wait = TimeSpan.FromSeconds(Math.Min(300, 20 * (1 << Math.Min(s_failedStarts, 4))));
            if (s_bot is not null || s_failedStarts > 0) {
                if (DateTime.UtcNow - s_botStarted < wait) {
                    return;
                }
            }

            if (s_bot is not null) {
                Logger.Warning("Kiosk: the kiosk bot stopped (exit code {0}); starting it again. Its log is in {1}.",
                    Logger.Args(SafeExitCode(s_bot), KioskWire.Folder));
                s_failedStarts++;
                s_bot.Dispose();
                s_bot = null;
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

            foreach (var arg in new[] { "--kiosk", "--parent", Environment.ProcessId.ToString() }) {
                start.ArgumentList.Add(arg);
            }

            start.Environment[Logger.LOG_PATH_VARIABLE] = Path.Combine(KioskWire.Folder, KioskWire.LOG_FILE);

            Imlight.CoreLib.Shared.Resources.HelperProcessMemory.UseSmallMemory(start);
            Directory.CreateDirectory(KioskWire.Folder);
            try {
                s_bot = Process.Start(start);
                s_botStarted = DateTime.UtcNow;
                s_botPort = KioskWire.Port;
                Logger.Information("Kiosk: a player used a Pet Hatching Kiosk, so the kiosk bot starts (process {0}, port {1}).", Logger.Args(s_bot?.Id, s_botPort));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                Logger.Error("Kiosk: could not start the kiosk bot. {0}", Logger.Args(e.Message));
                s_botStarted = DateTime.UtcNow;
                s_failedStarts++;
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

    private static void StopBot(string why) {
        lock (s_lock) {
            s_ready = false;
            if (s_bot is null) {
                return;
            }

            try {
                if (!s_bot.HasExited) {
                    Logger.Information("Kiosk: stopping the kiosk bot ({0}).", Logger.Args(why));
                    s_bot.Kill();
                }
            }
            catch (InvalidOperationException) {
                // Already gone.
            }

            CloseLineSafely();
            s_bot.Dispose();
            s_bot = null;
        }
    }

    // ---------------------------------------------------------------------
    // Questions
    // ---------------------------------------------------------------------

    /// <summary>
    /// The mini server's answer, or null when it is not running (asleep, starting, turned off) or did not answer; the
    /// caller then reads the listings file itself.
    /// </summary>
    internal static KioskReply Ask(KioskRequest request) {
        if (!Running) {
            return null;
        }

        if (Send(request) is { } answer) {
            return answer;
        }

        // It stopped answering; the watchdog starts it again, and the game server answers until it is back.
        s_ready = false;
        Logger.Warning("Kiosk: the kiosk bot stopped answering; the game server keeps the kiosk until it is back.");

        return null;
    }

    // One open line to the mini server, used by one question at a time.
    private static readonly Lock s_lineLock = new();
    private static TcpClient s_line;
    private static StreamReader s_reader;
    private static StreamWriter s_writer;

    private static KioskReply Send(KioskRequest request, bool quiet = false) {
        lock (s_lineLock) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    if (s_line is not { Connected: true }) {
                        CloseLine();
                        var line = new TcpClient { NoDelay = true, ReceiveTimeout = (int) s_answerWait.TotalMilliseconds, SendTimeout = (int) s_answerWait.TotalMilliseconds };
                        if (!line.ConnectAsync("127.0.0.1", KioskWire.Port).Wait(TimeSpan.FromSeconds(1))) {
                            line.Dispose();

                            return null;
                        }

                        s_line = line;
                        var stream = line.GetStream();
                        s_reader = new StreamReader(stream, Encoding.UTF8);
                        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    s_writer.WriteLine(KioskWire.Write(request));
                    var answer = s_reader.ReadLine();
                    if (answer is not null) {
                        return KioskWire.Read<KioskReply>(answer);
                    }

                    CloseLine();
                }
                catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException
                                              or System.Text.Json.JsonException) {
                    if (!quiet && attempt == 1) {
                        Logger.Warning("Kiosk: the kiosk bot did not answer {0}: {1}", Logger.Args(request.Op, e.GetBaseException().Message));
                    }

                    CloseLine();
                }
            }

            return null;
        }
    }

    private static void CloseLineSafely() {
        lock (s_lineLock) {
            CloseLine();
        }
    }

    private static void CloseLine() {
        s_reader?.Dispose();
        s_writer?.Dispose();
        s_line?.Dispose();
        s_reader = null;
        s_writer = null;
        s_line = null;
    }

    /// <summary>A short line on the kiosk for reports: on or off, and whether its mini server is running.</summary>
    public static string Describe()
        => !Enabled ? "turned off in Ahziael"
        : !OwnServer ? "run by the game server itself"
        : Running ? "open (its mini server is running)"
        : "asleep; its mini server starts when someone uses a kiosk";

}
