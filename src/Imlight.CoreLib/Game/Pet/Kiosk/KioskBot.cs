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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The Pet Hatching Kiosk as a mini server of its own, like the Bazaar bot (Niduus, 2026-11-21: "have it be a mini server
/// like Bazaar, only it activates the wake-up for it once it's interacted with. Same settings and behavior as Bazaar").
/// The game server starts it as a second copy of Imlight.Director with --kiosk when a player uses a kiosk. It keeps the
/// pets put up at the kiosk (hatchmaking-listings.json) and answers the game server over 127.0.0.1 ([HatchKiosk] Port).
/// Players, their pets and the hatching itself stay in the game server. It writes its own log (logs/kiosk-bot), which
/// Ahziael shows, and stops by itself when the game server is gone or puts it to sleep.
/// </summary>
public static class KioskBot {

    private static int s_port;
    private static volatile bool s_ready;

    public static void Run(string[] args) {
        var parent = ParentOf(args);
        Directory.CreateDirectory(KioskWire.Folder);
        Logger.Information("Kiosk bot starting (game server process {0}).", Logger.Args(parent?.Id.ToString() ?? "none"));
        LiveSettings.Start();
        WriteStatus("starting");

        // Watch the game server from the start: a mini server left behind would hold the port.
        _ = Task.Run(() => WatchParent(parent));

        Logger.Information("Kiosk bot read {0} pets put up at the kiosk.", Logger.Args(HatchmakingStore.Count));
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
            Logger.Warning("Kiosk bot: the game server ({0}) is already gone, so the bot stops.", Logger.Args(pid));
            Environment.Exit(0);

            return null;
        }
    }

    private static async Task WatchParent(Process parent) {
        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try {
                if (parent is not null && parent.HasExited) {
                    Logger.Information("Kiosk bot: the game server stopped, so the bot stops too.");
                    Stop(0);
                }

                // A different port in Imlight.ini: stop, and the game server starts a bot on the new one.
                if (s_ready && KioskWire.Port != s_port) {
                    Logger.Information("Kiosk bot: [HatchKiosk] Port changed to {0}; the bot restarts on it.", Logger.Args(KioskWire.Port));
                    Stop(0);
                }

                WriteStatus(s_ready ? "ready" : "starting");
            }
            catch (Exception e) {
                Logger.Warning("Kiosk bot: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Stop(int code) {
        // Every change is saved as it happens, so there is nothing left to save.
        WriteStatus("stopped");
        Environment.Exit(code);
    }

    private static void Listen() {
        s_port = KioskWire.Port;
        TcpListener listener;
        try {
            listener = new TcpListener(IPAddress.Loopback, s_port);
            listener.Start();
        }
        catch (SocketException e) {
            Logger.Fatal("Kiosk bot could not open port {0} ({1}); the game server runs the kiosk itself.", Logger.Args(s_port, e.Message));
            Stop(1);

            return;
        }

        s_ready = true;
        WriteStatus("ready");
        Logger.Information("Kiosk bot is open on 127.0.0.1:{0}.", Logger.Args(s_port));
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
                    writer.WriteLine(KioskWire.Write(Answer(KioskWire.Read<KioskRequest>(line))));
                }
            }
            catch (IOException) {
                // The game server hung up.
            }
            catch (Exception e) {
                Logger.Error("Kiosk bot could not answer: {0}", Logger.Args(e));
            }
        }
    }

    internal static KioskReply Answer(KioskRequest request) {
        switch (request?.Op) {
            case "ping":
                return new KioskReply { Ok = true, Count = HatchmakingStore.Count };
            case "all":
                return new KioskReply { Ok = true, Listings = HatchmakingStore.All() };
            case "of":
                return new KioskReply { Ok = true, Listings = HatchmakingStore.Of(request.Owner) };
            case "find":
                return new KioskReply { Ok = true, Listing = HatchmakingStore.Find(request.Id) };
            case "add":
                if (request.Listing?.Parent is null) {
                    return new KioskReply { Ok = false, Error = "No pet to put up." };
                }

                var isNew = HatchmakingStore.Find(request.Listing.Parent.ItemId) is null;
                HatchmakingStore.Add(request.Listing);
                if (isNew) {
                    Logger.Information("Kiosk bot: {0} was put up at the kiosk (owner {1}).",
                        Logger.Args(request.Listing.Parent.Label, request.Listing.Parent.OwnerCharId));
                }

                return new KioskReply { Ok = true, Done = true };
            case "remove":
                var removed = HatchmakingStore.Remove(request.Id);
                if (removed) {
                    Logger.Information("Kiosk bot: pet {0} came off the kiosk.", Logger.Args(request.Id));
                }

                return new KioskReply { Ok = true, Done = removed };
            case "count":
                HatchmakingStore.CountHatch(request.Id);
                Logger.Information("Kiosk bot: someone hatched with pet {0}.", Logger.Args(request.Id));

                return new KioskReply { Ok = true, Done = true };
            default:
                return new KioskReply { Ok = false, Error = $"Unknown question {request?.Op}." };
        }
    }

    // What Ahziael shows: the state, this program's id, how many pets are at the kiosk and when it last said so.
    private static void WriteStatus(string state)
        => KioskWire.WriteStatus(state, Environment.ProcessId, HatchmakingStore.Count, s_port);

}
