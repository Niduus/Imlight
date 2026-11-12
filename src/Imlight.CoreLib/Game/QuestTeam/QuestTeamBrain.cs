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
using System.Globalization;
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

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The Quest Team's mini server (Niduus, 2026-11-12: "Make them their own section and server, using the same bot
/// features"). The game server starts it as a second copy of Imlight.Director with --questteam while [QuestTeam] Enabled is
/// on, and kills it when it is turned off, like the Bot Players mini server. It keeps the team's books: the log of what
/// each wizard does (logs/quest-team/quest-team.txt), the quest log (every quest started and finished, with how long it
/// took), how long each quest is expected to take (learned from the last time it was played), and the issues Ahziael
/// announces (a broken quest, the team itself stuck, a quest taking too long). The four wizards themselves play inside the
/// game server (QuestTeamDirector), through a pretend game each, so the server's own quest code runs as for a player.
/// </summary>
public static class QuestTeamBrain {

    private static readonly Lock s_lock = new();
    private static int s_port;
    private static volatile bool s_ready;
    private static Dictionary<string, QuestTime> s_times = new(StringComparer.OrdinalIgnoreCase);
    private static List<Issue> s_issues = [];
    private static bool s_timesDirty;

    private sealed class QuestTime {
        public string Title { get; set; } = "";
        public double Seconds { get; set; }
        public int Times { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class Issue {
        public int Id { get; set; }
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string World { get; set; } = "";
        public string Text { get; set; } = "";
        // What a player would do and see at that step (Niduus, 2026-11-12: "with expected behavior noted ... so I can
        // find the intended behavior in a human stance").
        public string Expected { get; set; } = "";
        // Where each wizard was and what it held when it happened.
        public List<string> Seen { get; set; } = [];
        // The last lines of the team's log when it happened, so Ahziael shows what led up to it.
        public List<string> Log { get; set; } = [];
    }

    // The last lines of the log, for an issue's "what happened".
    private static readonly Queue<string> s_recent = new();
    private const int RECENT_LINES = 40;

    public static void Run(string[] args) {
        var parent = ParentOf(args);
        Directory.CreateDirectory(QuestTeamWire.Folder);
        Logger.Information("Quest Team mini server starting (game server process {0}).", Logger.Args(parent?.Id.ToString() ?? "none"));
        LiveSettings.Start();
        LoadFiles();
        WriteStatus("starting");

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
            Logger.Warning("Quest Team: the game server ({0}) is already gone, so the mini server stops.", Logger.Args(pid));
            Environment.Exit(0);

            return null;
        }
    }

    private static async Task WatchParent(Process parent) {
        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try {
                if (parent is not null && parent.HasExited) {
                    Logger.Information("Quest Team: the game server stopped, so the mini server stops too.");
                    Stop(0);
                }

                if (!QuestTeamWire.Enabled) {
                    Logger.Information("Quest Team: [QuestTeam] Enabled was turned off, so the mini server stops.");
                    Stop(0);
                }

                if (s_ready && QuestTeamWire.Port != s_port) {
                    Logger.Information("Quest Team: [QuestTeam] Port changed to {0}; the mini server restarts on it.", Logger.Args(QuestTeamWire.Port));
                    Stop(0);
                }

                SaveTimesIfChanged();
                WriteStatus(s_ready ? "ready" : "starting");
            }
            catch (Exception e) {
                Logger.Warning("Quest Team: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Stop(int code) {
        SaveTimesIfChanged();
        WriteStatus("stopped");
        Environment.Exit(code);
    }

    private static void Listen() {
        s_port = QuestTeamWire.Port;
        TcpListener listener;
        try {
            listener = new TcpListener(IPAddress.Loopback, s_port);
            listener.Start();
        }
        catch (SocketException e) {
            Logger.Fatal("Quest Team could not open port {0} ({1}), so the mini server stops.", Logger.Args(s_port, e.Message));
            Stop(1);

            return;
        }

        s_ready = true;
        WriteStatus("ready");
        Logger.Information("Quest Team mini server is open on 127.0.0.1:{0}; it knows how long {1} quests take.", Logger.Args(s_port, s_times.Count));
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
                    TeamReply reply;
                    try {
                        reply = Answer(QuestTeamWire.Read<TeamRequest>(line));
                    }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException) {
                        Logger.Error("Quest Team could not answer a question: {0}", Logger.Args(e.Message));
                        reply = new TeamReply { Ok = false, Error = e.Message };
                    }

                    writer.WriteLine(QuestTeamWire.Write(reply));
                }
            }
            catch (IOException) {
                // The game server hung up.
            }
            catch (Exception e) {
                Logger.Error("Quest Team could not answer: {0}", Logger.Args(e));
            }
        }
    }

    private static TeamReply Answer(TeamRequest request) {
        switch (request?.Op) {
            case "ping":
                return new TeamReply { Ok = true };
            case "note":
                Note(request.Who, request.Text);

                return new TeamReply { Ok = true };
            case "quest":
                return Quest(request);
            case "issue":
                AddIssue(request);

                return new TeamReply { Ok = true };
            case "status":
                WriteFile(Path.Combine(QuestTeamWire.Folder, "status.json"), request.Json ?? "{}");

                return new TeamReply { Ok = true };
            default:
                return new TeamReply { Ok = false, Error = $"Unknown question {request?.Op}." };
        }
    }

    private static void Note(string who, string text) {
        var line = $"{who}: {text}";
        Logger.Information("{0}", Logger.Args(line));
        lock (s_lock) {
            s_recent.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (s_recent.Count > RECENT_LINES) {
                s_recent.Dequeue();
            }
        }
    }

    // ---------------------------------------------------------------------
    // The quest log and how long quests take
    // ---------------------------------------------------------------------

    private static TeamReply Quest(TeamRequest request) {
        var code = request.Quest ?? "";
        var title = string.IsNullOrWhiteSpace(request.Title) ? code : request.Title;
        switch (request.Event) {
            case "start": {
                double expected;
                bool learned;
                lock (s_lock) {
                    learned = s_times.TryGetValue(code, out var known) && known.Seconds > 0;
                    expected = learned ? known.Seconds : Guess(request);
                }

                QuestLog($"STARTED  {title} ({code}), {request.World}: {request.Steps} steps, {request.Fights} fights. " +
                         $"Expected about {Minutes(expected)}{(learned ? " (how long it took last time)" : " (a first guess)")}.");
                Note("Team", $"starts {title} ({code}); expected about {Minutes(expected)}.");

                return new TeamReply { Ok = true, Expected = expected, Learned = learned };
            }
            case "step":
                QuestLog($"  step    {title}: {request.Text}");

                return new TeamReply { Ok = true };
            case "done": {
                lock (s_lock) {
                    if (!s_times.TryGetValue(code, out var known)) {
                        s_times[code] = known = new QuestTime();
                    }

                    known.Title = title;
                    known.Seconds = request.Seconds;
                    known.Times++;
                    known.Last = DateTime.UtcNow;
                    s_timesDirty = true;
                }

                QuestLog($"FINISHED {title} ({code}) in {Minutes(request.Seconds)}{(string.IsNullOrEmpty(request.Text) ? "" : ": " + request.Text)}.");
                Note("Team", $"finished {title} in {Minutes(request.Seconds)}.");

                return new TeamReply { Ok = true };
            }
            default:
                QuestLog($"{request.Event?.ToUpperInvariant()} {title} ({code}): {request.Text}");

                return new TeamReply { Ok = true };
        }
    }

    // A first guess for a quest never played: a minute, plus time for each step and each fight.
    private static double Guess(TeamRequest request)
        => 60 + (Math.Max(1, request.Steps) * 75) + (Math.Max(0, request.Fights) * 90);

    private static string Minutes(double seconds) {
        if (seconds < 90) {
            return $"{Math.Max(1, Math.Round(seconds))} seconds";
        }

        var minutes = seconds / 60;

        return minutes < 90 ? $"{Math.Round(minutes)} minutes" : $"{minutes / 60:0.#} hours";
    }

    private static void QuestLog(string text) {
        try {
            File.AppendAllText(Path.Combine(QuestTeamWire.Folder, QuestTeamWire.QUEST_LOG_FILE),
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {text}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("Quest Team could not write its quest log: {0}", Logger.Args(e.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Issues, which Ahziael announces
    // ---------------------------------------------------------------------

    private static void AddIssue(TeamRequest request) {
        string text;
        lock (s_lock) {
            var issue = new Issue {
                Id = s_issues.Count == 0 ? 1 : s_issues.Max(i => i.Id) + 1,
                At = DateTime.UtcNow,
                Kind = request.Kind ?? "team",
                Quest = request.Quest ?? "",
                Title = request.Title ?? "",
                World = request.World ?? "",
                Text = request.Text ?? "",
                Expected = request.Expected ?? "",
                Seen = request.Seen ?? [],
                Log = [.. s_recent],
            };
            s_issues.Add(issue);
            // Only the newest 300 are kept.
            if (s_issues.Count > 300) {
                s_issues.RemoveRange(0, s_issues.Count - 300);
            }

            text = JsonSerializer.Serialize(s_issues, new JsonSerializerOptions { WriteIndented = true });
        }

        Logger.Warning("ISSUE ({0}) {1}: {2}", Logger.Args(request.Kind, string.IsNullOrEmpty(request.Title) ? request.Quest : request.Title, request.Text));
        QuestLog($"ISSUE    {(string.IsNullOrEmpty(request.Title) ? request.Quest : request.Title)}: {request.Text}");
        WriteFile(Path.Combine(QuestTeamWire.Folder, QuestTeamWire.ISSUES_FILE), text);
    }

    // ---------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------

    private static void LoadFiles() {
        try {
            var times = Path.Combine(QuestTeamWire.Folder, QuestTeamWire.QUEST_TIMES_FILE);
            if (File.Exists(times)) {
                s_times = JsonSerializer.Deserialize<Dictionary<string, QuestTime>>(File.ReadAllText(times)) ?? [];
                s_times = new Dictionary<string, QuestTime>(s_times, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Quest Team could not read how long quests take ({0}); it starts again.", Logger.Args(e.Message));
            s_times = new Dictionary<string, QuestTime>(StringComparer.OrdinalIgnoreCase);
        }

        try {
            var issues = Path.Combine(QuestTeamWire.Folder, QuestTeamWire.ISSUES_FILE);
            if (File.Exists(issues)) {
                s_issues = JsonSerializer.Deserialize<List<Issue>>(File.ReadAllText(issues)) ?? [];
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Quest Team could not read its issues ({0}); it starts a new list.", Logger.Args(e.Message));
            s_issues = [];
        }
    }

    private static void SaveTimesIfChanged() {
        string text;
        lock (s_lock) {
            if (!s_timesDirty) {
                return;
            }

            s_timesDirty = false;
            text = JsonSerializer.Serialize(s_times, new JsonSerializerOptions { WriteIndented = true });
        }

        if (!WriteFile(Path.Combine(QuestTeamWire.Folder, QuestTeamWire.QUEST_TIMES_FILE), text)) {
            lock (s_lock) {
                s_timesDirty = true;
            }
        }
    }

    private static bool WriteFile(string path, string text) {
        try {
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("Quest Team could not write {0}: {1}", Logger.Args(Path.GetFileName(path), e.Message));

            return false;
        }
    }

    // What Ahziael shows: the state, this program's id, when it last said so, and its port.
    private static void WriteStatus(string state)
        => WriteFile(Path.Combine(QuestTeamWire.Folder, QuestTeamWire.STATUS_FILE),
            $"{state}\n{Environment.ProcessId}\n{DateTime.UtcNow:O}\n{s_port}\n");

}
