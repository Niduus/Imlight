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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// The Bot Players mini server, the bots' brain (Niduus, 2026-11-12: "These bots get hosted on their own mini server like
/// how Bazaar is. While the option is off the mini server doesn't run"). The game server starts it as a second copy of
/// Imlight.Director with --bots while [BotPlayers] Enabled is on, and stops it when it is turned off. It keeps the roster
/// of bots (who they are, so the same bots come back, plus some new ones), decides how each one walks and what it does in a
/// battle, and writes the bots' log (logs/bot-players), which Ahziael shows. The bots' bodies (what the players' games
/// see) stay in the game server (BotBodies). It stops by itself when the game server is gone.
/// </summary>
public static class BotBrain {

    private static readonly Lock s_lock = new();
    private static readonly Random s_random = new();
    private static List<RosterEntry> s_roster = [];
    private static readonly HashSet<int> s_out = [];
    private static int s_port;
    private static volatile bool s_ready;
    private static bool s_dirty;

    // How often a bot asked for is one from the roster that comes back, rather than a new one (Niduus: "Should come back
    // and also some new bots as well").
    private static int ReturningPercent => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.ReturningPercent", 70), 0, 100);

    private sealed class RosterEntry {
        public int Id { get; set; }
        public uint NameKeys { get; set; }
        public string Gender { get; set; }
        public string Name { get; set; } = "";
        public string School { get; set; }
        public int Level { get; set; }
        public int Times { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public string LastZone { get; set; } = "";
    }

    public static void Run(string[] args) {
        var parent = ParentOf(args);
        Directory.CreateDirectory(BotWire.Folder);
        Logger.Information("Bot Players mini server starting (game server process {0}).", Logger.Args(parent?.Id.ToString() ?? "none"));
        LiveSettings.Start();
        LoadRoster();
        WriteStatus("starting");

        // Watch the game server from the start: a brain left behind would hold the port.
        _ = Task.Run(() => WatchParent(parent));
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
            Logger.Warning("Bot Players: the game server ({0}) is already gone, so the mini server stops.", Logger.Args(pid));
            Environment.Exit(0);

            return null;
        }
    }

    private static async Task WatchParent(Process parent) {
        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try {
                if (parent is not null && parent.HasExited) {
                    Logger.Information("Bot Players: the game server stopped, so the mini server stops too.");
                    Stop(0);
                }

                // Turned off in Imlight.ini: the game server stops it too, but it does not wait for that.
                if (!BotWire.Enabled) {
                    Logger.Information("Bot Players: [BotPlayers] Enabled was turned off, so the mini server stops.");
                    Stop(0);
                }

                if (s_ready && BotWire.Port != s_port) {
                    Logger.Information("Bot Players: [BotPlayers] Port changed to {0}; the mini server restarts on it.", Logger.Args(BotWire.Port));
                    Stop(0);
                }

                SaveRosterIfChanged();
                WriteStatus(s_ready ? "ready" : "starting");
            }
            catch (Exception e) {
                Logger.Warning("Bot Players: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Stop(int code) {
        lock (s_lock) {
            s_out.Clear();
        }

        SaveRosterIfChanged();
        WriteStatus("stopped");
        Environment.Exit(code);
    }

    private static void Listen() {
        s_port = BotWire.Port;
        TcpListener listener;
        try {
            listener = new TcpListener(IPAddress.Loopback, s_port);
            listener.Start();
        }
        catch (SocketException e) {
            Logger.Fatal("Bot Players could not open port {0} ({1}), so the mini server stops.", Logger.Args(s_port, e.Message));
            Stop(1);

            return;
        }

        s_ready = true;
        WriteStatus("ready");
        Logger.Information("Bot Players mini server is open on 127.0.0.1:{0} with {1} bots in its roster.", Logger.Args(s_port, s_roster.Count));
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
                    BotReply reply;
                    try {
                        reply = Answer(BotWire.Read<BotRequest>(line));
                    }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) {
                        Logger.Error("Bot Players could not answer a question: {0}", Logger.Args(e.Message));
                        reply = new BotReply { Ok = false, Error = e.Message };
                    }

                    writer.WriteLine(BotWire.Write(reply));
                }
            }
            catch (IOException) {
                // The game server hung up.
            }
            catch (Exception e) {
                Logger.Error("Bot Players could not answer: {0}", Logger.Args(e));
            }
        }
    }

    internal static BotReply Answer(BotRequest request) {
        switch (request?.Op) {
            case "ping":
                lock (s_lock) {
                    return new BotReply { Ok = true, Out = s_out.Count, Known = s_roster.Count };
                }
            case "make":
                return new BotReply { Ok = true, Bot = Make(request) };
            case "walk":
                return new BotReply { Ok = true, Walk = Walk(request) };
            case "round":
                return new BotReply { Ok = true, Move = Round(request) };
            case "event":
                Note(request);

                return new BotReply { Ok = true };
            case "reset":
                lock (s_lock) {
                    if (s_out.Count > 0) {
                        Logger.Information("Bot Players: the game server has no bots out ({0}); all {1} are back in the roster.",
                            Logger.Args(request.Text ?? "it started again", s_out.Count));
                    }

                    s_out.Clear();
                }

                return new BotReply { Ok = true };
            default:
                return new BotReply { Ok = false, Error = $"Unknown question {request?.Op}." };
        }
    }

    // ---------------------------------------------------------------------
    // Who the bots are
    // ---------------------------------------------------------------------

    private static BotIdentity Make(BotRequest request) {
        lock (s_lock) {
            var now = DateTime.UtcNow;
            var gender = request.Gender ?? "Male";

            // A bot from the roster comes back now and then: one of the same look's gender (its name is in that gender's
            // list), not out already.
            var idle = s_roster.Where(b => b.Gender == gender && !s_out.Contains(b.Id)
                                           && NameFits(b.NameKeys, request)).ToList();
            RosterEntry bot;
            var returning = idle.Count > 0 && s_random.Next(100) < ReturningPercent;
            if (returning) {
                bot = idle[s_random.Next(idle.Count)];
            }
            else {
                bot = new RosterEntry {
                    Id = s_roster.Count == 0 ? 1 : s_roster.Max(b => b.Id) + 1,
                    NameKeys = RandomName(request),
                    Gender = gender,
                    School = request.School,
                    Level = Math.Max(1, request.Level),
                    FirstSeen = now,
                };
                s_roster.Add(bot);
            }

            // A bot that comes back is at the level of where it comes back (Niduus: a bot's level fits the world it is in).
            bot.Level = Math.Max(1, request.Level);
            bot.Times++;
            bot.LastSeen = now;
            bot.LastZone = request.Zone ?? "";
            s_out.Add(bot.Id);
            s_dirty = true;

            Logger.Information(returning
                    ? "Bot #{0} comes back ({1} time) in {2}."
                    : "Bot #{0} is new: made for {2}.",
                Logger.Args(bot.Id, Ordinal(bot.Times), request.Zone ?? "?"));

            return new BotIdentity {
                Id = bot.Id, NameKeys = bot.NameKeys, Gender = bot.Gender, School = bot.School, Level = bot.Level,
                Returning = returning, Times = bot.Times,
            };
        }
    }

    // A name from the character maker's lists: a first name, and a middle and last part (the lists' sizes come from the
    // game server, which reads the game files). Each part fits in one byte of the keys.
    private static uint RandomName(BotRequest request) {
        var firsts = Math.Clamp(request.Firsts, 1, 256);
        var middles = Math.Clamp(request.Middles, 0, 256);
        var lasts = Math.Clamp(request.Lasts, 0, 256);
        var first = s_random.Next(firsts);
        var middle = middles > 1 ? s_random.Next(1, middles) : 0;
        var last = lasts > 1 ? s_random.Next(1, lasts) : 0;

        return (uint) ((first << 16) | (middle << 8) | last);
    }

    private static bool NameFits(uint keys, BotRequest request)
        => ((keys >> 16) & 0xFF) < request.Firsts && ((keys >> 8) & 0xFF) < Math.Max(1, request.Middles)
           && (keys & 0xFF) < Math.Max(1, request.Lasts);

    private static string Ordinal(int n) => n switch {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th",
    };

    // ---------------------------------------------------------------------
    // What the bots do
    // ---------------------------------------------------------------------

    private static BotWalkPlan Walk(BotRequest request) {
        lock (s_lock) {
            // Stage 0 (the test): a short loop around where it came in, at a walk, with short stops.
            var plan = new BotWalkPlan {
                Radius = 180 + s_random.Next(120),
                Points = 5 + s_random.Next(3),
                Speed = 260,
                PauseSeconds = 1 + (float) s_random.NextDouble() * 3,
            };
            Logger.Information("Bot #{0} ({1}) walks a loop of {2} points, {3:0} out, with stops of about {4:0.0} s.",
                Logger.Args(request.Id, NameOf(request), plan.Points, plan.Radius, plan.PauseSeconds));

            return plan;
        }
    }

    private static string Round(BotRequest request) {
        // Not asked any more: the battle picks a bot's card itself (BotTactics), where it sees every hand at once, and
        // tells this log what it picked. Kept for an older game server that still asks.
        Logger.Information("Bot #{0} ({1}) round {2}: asked to pick; it passes.",
            Logger.Args(request.Id, NameOf(request), request.Round));

        return "pass";
    }

    private static void Note(BotRequest request) {
        lock (s_lock) {
            var bot = s_roster.FirstOrDefault(b => b.Id == request.Id);
            if (bot is not null && !string.IsNullOrEmpty(request.Name) && bot.Name != request.Name) {
                bot.Name = request.Name;
                s_dirty = true;
            }

            if (request.Event == "gone") {
                s_out.Remove(request.Id);
            }

            Logger.Information("Bot #{0} ({1}) {2}: {3}", Logger.Args(request.Id, NameOf(request), request.Event, request.Text ?? ""));
        }
    }

    private static string NameOf(BotRequest request) {
        if (!string.IsNullOrEmpty(request.Name)) {
            return request.Name;
        }

        lock (s_lock) {
            return s_roster.FirstOrDefault(b => b.Id == request.Id)?.Name is { Length: > 0 } name ? name : "no name yet";
        }
    }

    // ---------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------

    private static string RosterPath => Path.Combine(BotWire.Folder, BotWire.ROSTER_FILE);

    private static void LoadRoster() {
        try {
            if (File.Exists(RosterPath)) {
                s_roster = JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(RosterPath)) ?? [];
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Bot Players could not read its roster ({0}); it starts a new one.", Logger.Args(e.Message));
            s_roster = [];
        }
    }

    private static void SaveRosterIfChanged() {
        string text;
        lock (s_lock) {
            if (!s_dirty) {
                return;
            }

            s_dirty = false;
            text = JsonSerializer.Serialize(s_roster, new JsonSerializerOptions { WriteIndented = true });
        }

        try {
            File.WriteAllText(RosterPath + ".tmp", text);
            File.Move(RosterPath + ".tmp", RosterPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("Bot Players could not save its roster: {0}", Logger.Args(e.Message));
            lock (s_lock) {
                s_dirty = true;
            }
        }
    }

    // What Ahziael shows: the state, this program's id, how many bots are out, when it last said so, its port and how
    // many bots the roster knows.
    private static void WriteStatus(string state) {
        try {
            int count, known;
            lock (s_lock) {
                count = s_out.Count;
                known = s_roster.Count;
            }

            var text = $"{state}\n{Environment.ProcessId}\n{count}\n{DateTime.UtcNow:O}\n{s_port}\n{known}\n";
            var path = Path.Combine(BotWire.Folder, BotWire.STATUS_FILE);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Shown as unknown in Ahziael until the next try.
        }
    }

}
