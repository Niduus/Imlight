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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Events;

/// <summary>
/// The game events the Imlight app runs (Niduus, 2026-10-24: "Have events be controlled by the manager with able to
/// queue new ones up as old ones expire"). The app's Events card writes events.json next to the server: a queue of
/// events, each with how long it lasts, and when the first one started. Events of different kinds run side by side;
/// those of one kind (every Deckathalon is one kind, every Monthly Gauntlet another) run one after another, each starting
/// as the one before it ends, and with Repeat each kind's line starts over when its last one ends. Every second the
/// server works out which events are on; when that changes, everyone online is told, and a wizard logging in is told
/// which events are on and how long they have left. An event that ended stays in the game's event window for a week,
/// for its rewards (Ended).
///
/// File:
///   Queue      the events in order: Kind (Deckathalon, or another game event: ClassProjects.Queueable), School (for a
///              Deckathalon: Fire, Ice, Storm, Myth, Life, Death or Balance; for a Monthly Gauntlet: which one), Days
///              and Hours (how long it lasts).
///   StartedAt  when the first event of the queue started (UTC), or null while the queue waits to be started.
///   LaneStarts when each kind's line of events started, where it differs from StartedAt.
///   Repeat     true: after the last event the queue starts again from the first.
/// </summary>
internal static class GameEvents {

    internal const string EVENTS_FILE = "events.json";
    internal const string DECKATHALON = "Deckathalon";

    /// <summary>One event of the queue.</summary>
    internal sealed class QueuedEvent {
        public string Kind { get; set; } = DECKATHALON;
        public string School { get; set; } = "";
        public int Days { get; set; }
        public int Hours { get; set; }

        internal TimeSpan Length => TimeSpan.FromDays(Math.Max(0, Days)) + TimeSpan.FromHours(Math.Max(0, Hours));
    }

    /// <summary>The event that is on now: what it is, and when it started and ends.</summary>
    internal sealed record RunningEvent(string Kind, string School, DateTime Start, DateTime End) {

        internal string Name => string.Equals(Kind, DECKATHALON, StringComparison.OrdinalIgnoreCase)
            ? $"{School} Deckathalon"
            : string.IsNullOrWhiteSpace(School) ? Kind : $"{School} {Kind}";

        /// <summary>The same event on the same start: what scores and rewards are kept by.</summary>
        internal string Key => $"{Kind}-{School}@{Start:yyyy-MM-ddTHH:mm}";

        internal bool IsDeckathalon => string.Equals(Kind, DECKATHALON, StringComparison.OrdinalIgnoreCase);

    }

    private sealed class EventFile {
        public List<QueuedEvent> Queue { get; set; } = [];
        public DateTime? StartedAt { get; set; }
        // When each kind's line of events started, where it is not StartedAt (a kind added later starts then).
        public Dictionary<string, DateTime> LaneStarts { get; set; }
        public bool Repeat { get; set; }
    }

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static Timer s_timer;
    private static ActorSystem s_system;
    private static DateTime s_fileTime;
    private static EventFile s_file = new();

    // Accounts that logged in and have not yet been told the event.
    private static readonly ConcurrentDictionary<ulong, byte> s_toGreet = new();

    /// <summary>
    /// The Deckathalon on now, or else the first event on, or null. Code for one kind of event uses this; see Running
    /// for all of them.
    /// </summary>
    internal static RunningEvent Current => s_running.FirstOrDefault(e => e.IsDeckathalon) ?? s_running.FirstOrDefault();

    /// <summary>Every event on now: one of each kind at most (Niduus, 2026-10-25: "events can run side by side but 2
    /// events of the same type can not").</summary>
    internal static IReadOnlyList<RunningEvent> Running => s_running;

    /// <summary>
    /// The events that ended in the last ENDED_DAYS days (Niduus, 2026-10-25: "Events that end stay in the list so
    /// players can get their rewards for 7 days"), newest first. The server keeps them in events-ended.json.
    /// </summary>
    internal static IReadOnlyList<RunningEvent> Ended {
        get {
            var since = DateTime.UtcNow - TimeSpan.FromDays(ENDED_DAYS);
            lock (s_endedLock) {
                return [.. s_ended.Where(e => e.End > since && !s_running.Any(r => r.Key == e.Key)).OrderByDescending(e => e.End)];
            }
        }
    }

    internal const int ENDED_DAYS = 7;
    private const string ENDED_FILE = "events-ended.json";
    private static readonly object s_endedLock = new();
    private static List<RunningEvent> s_ended = [];
    private static List<RunningEvent> s_running = [];

    /// <summary>
    /// Starts watching the app's file. Calling it again does nothing.
    /// </summary>
    internal static void Start(ActorSystem system) {
        if (Interlocked.CompareExchange(ref s_system, system, null) is not null) {
            return;
        }

        Load();
        LoadEnded();
        s_running = RunningAt(s_file, DateTime.UtcNow);
        foreach (var running in s_running) {
            Logger.Information("Event on: {0}{1}.", Logger.Args(running.Name, TimeLeft(running)));
        }

        s_timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>The login server calls this when an account logs in; its next arrival in the world tells it the event.</summary>
    internal static void LoggedIn(ulong accountId) => s_toGreet[accountId] = 0;

    /// <summary>The lines a wizard arriving in the world is told, once per login: the events that are on.</summary>
    internal static List<string> GreetingFor(ulong accountId) {
        if (!s_toGreet.TryRemove(accountId, out _)) {
            return [];
        }

        return [.. s_running.Select(running => $"Event on: {running.Name}{TimeLeft(running)}.{HowToJoin(running)}")];
    }

    /// <summary>The kind of event a queued one is, for "one of each kind at a time": every Deckathalon is one kind,
    /// every Monthly Gauntlet another.</summary>
    internal static string KindOf(string kind) => string.IsNullOrWhiteSpace(kind) ? DECKATHALON : kind.Trim();

    /// <summary>
    /// Which events of a queue are on at a time. The queue holds a line of events of each kind; the lines run side by
    /// side, each from its own start (LaneStarts, else StartedAt), the events of a line one after another, and with
    /// Repeat each line goes round again. Empty when the queue is not started or every line has ended.
    /// </summary>
    private static List<RunningEvent> RunningAt(EventFile file, DateTime now) {
        var running = new List<RunningEvent>();
        if (file?.StartedAt is not { } startedAt || file.Queue is not { Count: > 0 } queue) {
            return running;
        }

        foreach (var lane in queue.GroupBy(e => KindOf(e.Kind), StringComparer.OrdinalIgnoreCase)) {
            var start = (file.LaneStarts is not null && file.LaneStarts.TryGetValue(lane.Key, out var laneStart) ? laneStart : startedAt)
                .ToUniversalTime();
            var events = lane.ToList();
            var total = TimeSpan.FromTicks(events.Sum(e => e.Length.Ticks));
            if (total <= TimeSpan.Zero || now < start) {
                continue;
            }

            if (file.Repeat && now - start >= total) {
                var rounds = (now - start).Ticks / total.Ticks;
                start += TimeSpan.FromTicks(total.Ticks * rounds);
            }

            foreach (var queued in events) {
                var end = start + queued.Length;
                if (now < end) {
                    running.Add(new RunningEvent(KindOf(queued.Kind), Deckathalon.SchoolName(queued.School) ?? queued.School ?? "", start, end));

                    break;
                }

                start = end;
            }
        }

        return running;
    }

    private static void Tick() {
        try {
            if (File.Exists(EVENTS_FILE) ? File.GetLastWriteTimeUtc(EVENTS_FILE) != s_fileTime : s_file.Queue.Count > 0) {
                Load();
            }

            var utcNow = DateTime.UtcNow;
            var now = RunningAt(s_file, utcNow);
            var was = s_running;
            var over = was.Where(w => !now.Any(n => n.Key == w.Key)).ToList();
            var started = now.Where(n => !was.Any(w => w.Key == n.Key)).ToList();
            if (over.Count == 0 && started.Count == 0) {
                return;
            }

            if (over.Count > 0) {
                // An event that ended stays in the event window for a week, for its rewards.
                lock (s_endedLock) {
                    s_ended.RemoveAll(e => e.End <= utcNow - TimeSpan.FromDays(ENDED_DAYS) || over.Any(o => o.Key == e.Key));
                    s_ended.AddRange(over.Select(o => o with { End = o.End < utcNow ? o.End : utcNow }));
                }

                SaveEnded();
            }

            s_running = now;
            var lines = new List<string>();
            lines.AddRange(over.Select(o => $"Event over: {o.Name}.{(o.IsDeckathalon ? "" : " Its rewards can still be claimed for 7 days.")}"));
            lines.AddRange(started.Select(n => $"Event on: {n.Name}{TimeLeft(n)}.{HowToJoin(n)}"));

            foreach (var line in lines) {
                Logger.Information("{0}", Logger.Args(line));
            }

            Broadcast(lines);
        }
        catch (Exception ex) {
            Logger.Error("Could not check the events: {0}", Logger.Args(ex.Message));
        }
    }

    private sealed class EndedFile {
        public List<EndedEvent> Ended { get; set; } = [];
    }

    private sealed class EndedEvent {
        public string Kind { get; set; } = "";
        public string School { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
    }

    private static void LoadEnded() {
        try {
            if (!File.Exists(ENDED_FILE)) {
                return;
            }

            var file = JsonSerializer.Deserialize<EndedFile>(File.ReadAllText(ENDED_FILE), s_options);
            lock (s_endedLock) {
                s_ended = [.. (file?.Ended ?? []).Where(e => e is not null)
                    .Select(e => new RunningEvent(KindOf(e.Kind), e.School ?? "", e.Start.ToUniversalTime(), e.End.ToUniversalTime()))];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(ENDED_FILE, ex.Message));
        }
    }

    private static void SaveEnded() {
        try {
            EndedFile file;
            lock (s_endedLock) {
                file = new EndedFile { Ended = [.. s_ended.Select(e => new EndedEvent { Kind = e.Kind, School = e.School, Start = e.Start, End = e.End })] };
            }

            var temp = $"{ENDED_FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ENDED_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not save {0}: {1}", Logger.Args(ENDED_FILE, ex.Message));
        }
    }

    private static string HowToJoin(RunningEvent running)
        => running.IsDeckathalon ? " Type .deckathalon to climb the tower."
            : " Click the event button at the top of the screen to see it, or type .events.";

    private static void Broadcast(List<string> lines) {
        if (s_system is null || lines.Count == 0) {
            return;
        }

        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(p => p.CharacterId != 0 && p.ActorPath is not null)) {
            var session = s_system.ActorSelection(player.ActorPath);
            foreach (var line in lines) {
                session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
            }

            // The game's event button follows the event (EventButton.cs).
            session.Tell(EventButton.ButtonMessage());
        }
    }

    /// <summary>" (2d 3h left)", or nothing.</summary>
    internal static string TimeLeft(RunningEvent running) {
        var left = running.End - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) {
            return "";
        }

        var text = left.TotalDays >= 1 ? $"{(int) left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{left.Hours}h {left.Minutes}m"
            : $"{Math.Max(1, (int) Math.Ceiling(left.TotalMinutes))}m";

        return $" ({text} left)";
    }

    private static void Load() {
        if (!File.Exists(EVENTS_FILE)) {
            s_file = new EventFile();
            s_fileTime = default;

            return;
        }

        try {
            var time = File.GetLastWriteTimeUtc(EVENTS_FILE);
            var file = JsonSerializer.Deserialize<EventFile>(File.ReadAllText(EVENTS_FILE), s_options) ?? new EventFile();
            file.Queue = [.. (file.Queue ?? []).Where(e => e is not null && e.Length > TimeSpan.Zero)];
            s_file = file;
            s_fileTime = time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // The app is still writing the file; it is read on the next round.
        }
    }

}
