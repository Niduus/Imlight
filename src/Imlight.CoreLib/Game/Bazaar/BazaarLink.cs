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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Bazaar;

/// <summary>
/// The game server's side of the Bazaar bot: starts it, starts it again if it stops, and asks it everything the
/// Bazaar window needs. When [Bazaar] OwnServer is off, or the bot is not running, the game server keeps a Bazaar of
/// its own (the same <see cref="BazaarBook"/>), so the Bazaar always works. While the bot is still reading the game
/// files (a minute or so after a start) there is no Bazaar yet: <see cref="Opening"/> is true.
/// </summary>
public static class BazaarLink {

    private static bool OwnServer => ConfigurationManager.GetValue("Bazaar.OwnServer", true);

    // How long the bot may take to answer one question, and to read the game files after it starts.
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_startWait = TimeSpan.FromMinutes(5);

    private static readonly Lock s_lock = new();
    private static Process s_bot;
    private static DateTime s_botStarted;
    private static int s_botPort;
    private static int s_failedStarts;
    private static volatile bool s_ready;
    private static volatile bool s_firstStartDue;

    // The game server's own Bazaar, built only when it is needed. It saves its stock only when it is the Bazaar for good
    // (OwnServer off); while it stands in for a stopped bot, the bot's saved stock is left alone.
    private static readonly Lazy<BazaarBook> s_ownBook = new(() => {
        Logger.Information("Bazaar: the game server reads the Bazaar itself (the Bazaar bot is {0}).",
            Logger.Args(OwnServer ? "not running" : "turned off in [Bazaar] OwnServer"));
        Directory.CreateDirectory(BazaarWire.Folder);
        var book = BazaarBook.Build(OwnServer ? null : Path.Combine(BazaarWire.Folder, BazaarWire.STOCK_FILE));
        Logger.Information("Bazaar: {0}", Logger.Args(book.Describe()));

        return book;
    });

    /// <summary>Whether the Bazaar bot is running but not ready yet, so the Bazaar has nothing to show.</summary>
    public static bool Opening {
        get {
            lock (s_lock) {
                return OwnServer && !s_ready
                    && (s_firstStartDue || (s_bot is { HasExited: false } && DateTime.UtcNow - s_botStarted < s_startWait));
            }
        }
    }

    /// <summary>
    /// Starts the Bazaar bot after a short wait (so it does not read the game files while the server still does) and
    /// keeps it running while [Bazaar] OwnServer is on; stops it when it is turned off.
    /// </summary>
    public static void StartWatching() {
        s_firstStartDue = true;
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true) {
                try {
                    Watch();
                }
                catch (Exception e) {
                    Logger.Error("Bazaar: could not look after the Bazaar bot: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }

    private static void Watch() {
        if (!OwnServer) {
            StopBot("[Bazaar] OwnServer was turned off");

            return;
        }

        bool running;
        lock (s_lock) {
            running = s_bot is { HasExited: false };
        }

        if (running && s_botPort != BazaarWire.Port) {
            StopBot("[Bazaar] Port changed");
            running = false;
        }

        if (!running) {
            s_ready = false;
            StartBot();

            return;
        }

        var answer = Ask(new BazaarRequest { Op = "ping" }, quiet: true);
        if (answer?.Ok == true && !s_ready) {
            Logger.Information("Bazaar: the Bazaar bot is ready with {0} entries; the Bazaar is its own program now.", Logger.Args(answer.Stock));
            s_failedStarts = 0;
        }

        s_ready = answer?.Ok == true;
    }

    private static void StartBot() {
        lock (s_lock) {
            // A bot that keeps stopping is tried less often: 20 s, 40 s, ... up to 5 minutes.
            var wait = TimeSpan.FromSeconds(Math.Min(300, 20 * (1 << Math.Min(s_failedStarts, 4))));
            if (s_bot is not null && DateTime.UtcNow - s_botStarted < wait) {
                return;
            }

            if (s_bot is not null) {
                Logger.Warning("Bazaar: the Bazaar bot stopped (exit code {0}); starting it again. Its log is in {1}.",
                    Logger.Args(SafeExitCode(s_bot), BazaarWire.Folder));
                s_failedStarts++;
                s_bot.Dispose();
                s_bot = null;
            }

            s_firstStartDue = false;
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

            foreach (var arg in new[] { "--bazaar", "--parent", Environment.ProcessId.ToString() }) {
                start.ArgumentList.Add(arg);
            }

            start.Environment[Logger.LOG_PATH_VARIABLE] = Path.Combine(BazaarWire.Folder, BazaarWire.LOG_FILE);
            Directory.CreateDirectory(BazaarWire.Folder);
            try {
                s_bot = Process.Start(start);
                s_botStarted = DateTime.UtcNow;
                s_botPort = BazaarWire.Port;
                Logger.Information("Bazaar: started the Bazaar bot (process {0}, port {1}).", Logger.Args(s_bot?.Id, s_botPort));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                Logger.Error("Bazaar: could not start the Bazaar bot; the game server runs the Bazaar itself. {0}", Logger.Args(e.Message));
                s_botStarted = DateTime.UtcNow;
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
                    Logger.Information("Bazaar: stopping the Bazaar bot ({0}).", Logger.Args(why));
                    s_bot.Kill();
                }
            }
            catch (InvalidOperationException) {
                // Already gone.
            }

            s_bot.Dispose();
            s_bot = null;
            s_failedStarts = 0;
        }
    }

    // ---------------------------------------------------------------------
    // Questions
    // ---------------------------------------------------------------------

    // One open line to the bot, used by one question at a time.
    private static readonly Lock s_lineLock = new();
    private static TcpClient s_line;
    private static StreamReader s_reader;
    private static StreamWriter s_writer;

    private static BazaarReply Ask(BazaarRequest request, bool quiet = false) {
        lock (s_lineLock) {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    if (s_line is not { Connected: true }) {
                        CloseLine();
                        var line = new TcpClient { NoDelay = true, ReceiveTimeout = (int) s_answerWait.TotalMilliseconds, SendTimeout = (int) s_answerWait.TotalMilliseconds };
                        if (!line.ConnectAsync("127.0.0.1", BazaarWire.Port).Wait(TimeSpan.FromSeconds(1))) {
                            line.Dispose();

                            return null;
                        }

                        s_line = line;
                        var stream = line.GetStream();
                        s_reader = new StreamReader(stream, Encoding.UTF8);
                        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    s_writer.WriteLine(BazaarWire.Write(request));
                    var answer = s_reader.ReadLine();
                    if (answer is not null) {
                        return BazaarWire.Read<BazaarReply>(answer);
                    }

                    CloseLine();
                }
                catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException
                                              or System.Text.Json.JsonException) {
                    if (!quiet && attempt == 1) {
                        Logger.Warning("Bazaar: the Bazaar bot did not answer {0}: {1}", Logger.Args(request.Op, e.GetBaseException().Message));
                    }

                    CloseLine();
                }
            }

            return null;
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

    /// <summary>The bot's answer, or the game server's own Bazaar's when the bot is not there.</summary>
    private static BazaarReply Answer(BazaarRequest request) {
        // Still opening: nothing to show yet, and the game server does not read the game files for a bot about to answer.
        if (Opening) {
            return new BazaarReply { Ok = false };
        }

        if (OwnServer && s_ready) {
            if (Ask(request) is { } answer) {
                return answer;
            }

            // It stopped answering; the watchdog starts it again.
            s_ready = false;
            Logger.Warning("Bazaar: the Bazaar bot stopped answering; the game server answers for it until it is back.");
        }

        return BazaarBot.Answer(s_ownBook.Value, request);
    }

    /// <summary>The 20-byte entries a tab shows (see <see cref="BazaarBook.Records"/>).</summary>
    public static byte[] Records(uint category, bool everything)
        => Answer(new BazaarRequest { Op = "list", Category = category, Everything = everything }).Records ?? [];

    /// <summary>The entries as they are now, for the game's stock update.</summary>
    public static byte[] RecordsOf(IEnumerable<ulong> ids)
        => Answer(new BazaarRequest { Op = "records", Ids = [.. ids] }).Records ?? [];

    public static BazaarQuote Find(ulong id) => Answer(new BazaarRequest { Op = "find", Id = id }).Quote;

    /// <summary>Takes copies out of the stock for a buyer; false when not that many are left.</summary>
    public static bool Take(ulong id, int count, string who, int gold)
        => Answer(new BazaarRequest { Op = "take", Id = id, Count = count, Who = who, Gold = gold }).Ok;

    /// <summary>Puts back a copy taken for a buy that did not go through (a full bag).</summary>
    public static void PutBack(ulong id)
        => Answer(new BazaarRequest { Op = "putback", Id = id, Count = 1 });

    /// <summary>Puts what a player sold into the stock.</summary>
    public static void Give(ulong id, int count, string who, int gold)
        => Answer(new BazaarRequest { Op = "give", Id = id, Count = count, Who = who, Gold = gold });

}
