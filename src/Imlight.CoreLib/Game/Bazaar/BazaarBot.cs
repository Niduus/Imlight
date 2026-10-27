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
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Bazaar;

/// <summary>
/// The Bazaar bot: the Bazaar as a mini server of its own (Niduus, 2026-10-27: "I want the Bazaar to have it's own mini
/// server so that way, it doesn't interfer too much with Imlight"). The game server starts it as a second copy of
/// Imlight.Director with --bazaar. It reads the game files on its own, keeps every entry and its stock, restocks, and
/// answers the game server over 127.0.0.1 ([Bazaar] Port). Players and their gold stay in the game server. It writes
/// its own log (logs/bazaar-bot), which Ahziael shows, and stops by itself when the game server is gone.
/// </summary>
public static class BazaarBot {

    private static BazaarBook s_book;
    private static int s_port;
    private static volatile bool s_ready;

    public static void Run(string[] args) {
        var parent = ParentOf(args);
        Directory.CreateDirectory(BazaarWire.Folder);
        Logger.Information("Bazaar bot starting (game server process {0}).", Logger.Args(parent?.Id.ToString() ?? "none"));
        LiveSettings.Start();
        WriteStatus("starting");

        // Watch the game server from the start: a bot left behind would hold the port and the program's files.
        _ = Task.Run(() => WatchParent(parent));

        try {
            var stopwatch = Stopwatch.StartNew();
            _ = CoreObjectFactory.Instance;
            s_book = BazaarBook.Build(Path.Combine(BazaarWire.Folder, BazaarWire.STOCK_FILE));
            Logger.Information("Bazaar bot stocked the Bazaar in {0} s: {1}", Logger.Args(stopwatch.Elapsed.TotalSeconds.ToString("0"), s_book.Describe()));
        }
        catch (Exception e) {
            Logger.Fatal("Bazaar bot could not read the game files, so it stops; the game server runs the Bazaar itself. {0}", Logger.Args(e));
            WriteStatus("failed");
            Environment.Exit(1);
        }

        _ = Task.Run(RestockLoop);
        Listen();
    }

    private static Process ParentOf(string[] args) {
        var at = Array.IndexOf(args, "--parent");
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var pid)) {
            return null;
        }

        try {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException) {
            Logger.Warning("Bazaar bot: the game server ({0}) is already gone, so the bot stops.", Logger.Args(pid));
            Environment.Exit(0);

            return null;
        }
    }

    private static async Task WatchParent(Process parent) {
        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try {
                if (parent is not null && parent.HasExited) {
                    Logger.Information("Bazaar bot: the game server stopped, so the bot stops too.");
                    Stop(0);
                }

                // A different port in Imlight.ini: stop, and the game server starts a bot on the new one.
                if (s_ready && BazaarWire.Port != s_port) {
                    Logger.Information("Bazaar bot: [Bazaar] Port changed to {0}; the bot restarts on it.", Logger.Args(BazaarWire.Port));
                    Stop(0);
                }

                WriteStatus(s_ready ? "ready" : "starting");
            }
            catch (Exception e) {
                Logger.Warning("Bazaar bot: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Stop(int code) {
        s_book?.SaveStock();
        WriteStatus("stopped");
        Environment.Exit(code);
    }

    private static async Task RestockLoop() {
        var last = DateTime.UtcNow;
        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try {
                var minutes = Math.Max(0, ConfigurationManager.GetValue("Bazaar.RestockMinutes", 30));
                if (minutes > 0 && DateTime.UtcNow - last >= TimeSpan.FromMinutes(minutes)) {
                    last = DateTime.UtcNow;
                    s_book.Restock();
                }

                s_book.SaveStock();
            }
            catch (Exception e) {
                Logger.Error("Bazaar bot could not restock: {0}", Logger.Args(e));
            }
        }
    }

    private static void Listen() {
        s_port = BazaarWire.Port;
        TcpListener listener;
        try {
            listener = new TcpListener(IPAddress.Loopback, s_port);
            listener.Start();
        }
        catch (SocketException e) {
            Logger.Fatal("Bazaar bot could not open port {0} ({1}); the game server runs the Bazaar itself.", Logger.Args(s_port, e.Message));
            Stop(1);

            return;
        }

        s_ready = true;
        WriteStatus("ready");
        Logger.Information("Bazaar bot is open on 127.0.0.1:{0}.", Logger.Args(s_port));
        while (true) {
            var client = listener.AcceptTcpClient();
            _ = Task.Run(() => Serve(client));
        }
    }

    private static void Serve(TcpClient client) {
        using (client) {
            try {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) is not null) {
                    writer.WriteLine(BazaarWire.Write(Answer(BazaarWire.Read<BazaarRequest>(line))));
                }
            }
            catch (IOException) {
                // The game server hung up.
            }
            catch (Exception e) {
                Logger.Error("Bazaar bot could not answer: {0}", Logger.Args(e));
            }
        }
    }

    internal static BazaarReply Answer(BazaarRequest request) => Answer(s_book, request);

    internal static BazaarReply Answer(BazaarBook book, BazaarRequest request) {
        switch (request?.Op) {
            case "ping":
                return new BazaarReply { Ok = true, Stock = book.Count };
            case "list":
                return new BazaarReply { Ok = true, Records = book.Records(request.Category, request.Everything) };
            case "records":
                return new BazaarReply { Ok = true, Records = book.RecordsOf(request.Ids ?? []) };
            case "find":
                var quote = book.Find(request.Id);

                return new BazaarReply { Ok = quote is not null, Quote = quote };
            case "take":
                var taken = book.Take(request.Id, request.Count, request.Who, request.Gold, out var left);

                return new BazaarReply { Ok = taken, Stock = left };
            case "give":
                return new BazaarReply { Ok = true, Stock = book.Give(request.Id, request.Count, request.Who, request.Gold) };
            case "putback":
                return new BazaarReply { Ok = true, Stock = book.Give(request.Id, request.Count, null, 0, putBack: true) };
            default:
                return new BazaarReply { Ok = false, Error = $"Unknown question {request?.Op}." };
        }
    }

    // What Ahziael shows: the state, this program's id, how much it sells and when it last said so.
    private static void WriteStatus(string state) {
        try {
            var text = $"{state}\n{Environment.ProcessId}\n{s_book?.Count ?? 0}\n{DateTime.UtcNow:O}\n{s_port}\n";
            var path = Path.Combine(BazaarWire.Folder, BazaarWire.STATUS_FILE);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Shown as unknown in Ahziael until the next try.
        }
    }

}
