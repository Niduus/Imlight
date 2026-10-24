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
/// events, each with how long it lasts, and when the first one started. The events run one after another, each starting
/// as the one before it ends, and with Repeat the queue starts over when the last one ends. Every second the server
/// works out which event is on; when that changes, everyone online is told, and a wizard logging in is told which
/// event is on and how long it has left.
///
/// File:
///   Queue      the events in order: Kind (Deckathalon), School (for a Deckathalon: Fire, Ice, Storm, Myth, Life,
///              Death or Balance), Days and Hours (how long it lasts).
///   StartedAt  when the first event of the queue started (UTC), or null while the queue waits to be started.
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
            : Kind;

        /// <summary>The same event on the same start: what scores and rewards are kept by.</summary>
        internal string Key => $"{Kind}-{School}@{Start:yyyy-MM-ddTHH:mm}";

        internal bool IsDeckathalon => string.Equals(Kind, DECKATHALON, StringComparison.OrdinalIgnoreCase);

    }

    private sealed class EventFile {
        public List<QueuedEvent> Queue { get; set; } = [];
        public DateTime? StartedAt { get; set; }
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
    private static RunningEvent s_current;

    // Accounts that logged in and have not yet been told the event.
    private static readonly ConcurrentDictionary<ulong, byte> s_toGreet = new();

    /// <summary>The event that is on now, or null.</summary>
    internal static RunningEvent Current => s_current;

    /// <summary>
    /// Starts watching the app's file. Calling it again does nothing.
    /// </summary>
    internal static void Start(ActorSystem system) {
        if (Interlocked.CompareExchange(ref s_system, system, null) is not null) {
            return;
        }

        Load();
        s_current = RunningAt(s_file, DateTime.UtcNow);
        if (s_current is not null) {
            Logger.Information("Event on: {0}{1}.", Logger.Args(s_current.Name, TimeLeft(s_current)));
        }

        s_timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>The login server calls this when an account logs in; its next arrival in the world tells it the event.</summary>
    internal static void LoggedIn(ulong accountId) => s_toGreet[accountId] = 0;

    /// <summary>The lines a wizard arriving in the world is told, once per login: the event that is on.</summary>
    internal static List<string> GreetingFor(ulong accountId) {
        if (!s_toGreet.TryRemove(accountId, out _) || s_current is not { } running) {
            return [];
        }

        return [$"Event on: {running.Name}{TimeLeft(running)}.{HowToJoin(running)}"];
    }

    /// <summary>
    /// Which event of a queue is on at a time: the events follow each other from StartedAt, and with Repeat the queue
    /// goes round again. Null when the queue is not started, is empty, or has ended.
    /// </summary>
    private static RunningEvent RunningAt(EventFile file, DateTime now) {
        if (file?.StartedAt is not { } startedAt || file.Queue is not { Count: > 0 } queue) {
            return null;
        }

        var start = startedAt.ToUniversalTime();
        var total = TimeSpan.FromTicks(queue.Sum(e => e.Length.Ticks));
        if (total <= TimeSpan.Zero || now < start) {
            return null;
        }

        if (file.Repeat && now - start >= total) {
            var rounds = (now - start).Ticks / total.Ticks;
            start += TimeSpan.FromTicks(total.Ticks * rounds);
        }

        foreach (var queued in queue) {
            var end = start + queued.Length;
            if (now < end) {
                return new RunningEvent(queued.Kind ?? DECKATHALON, Deckathalon.SchoolName(queued.School) ?? queued.School ?? "", start, end);
            }

            start = end;
        }

        return null;
    }

    private static void Tick() {
        try {
            if (File.Exists(EVENTS_FILE) ? File.GetLastWriteTimeUtc(EVENTS_FILE) != s_fileTime : s_file.Queue.Count > 0) {
                Load();
            }

            var now = RunningAt(s_file, DateTime.UtcNow);
            var was = s_current;
            if (was?.Key == now?.Key) {
                return;
            }

            s_current = now;
            var lines = new List<string>();
            if (was is not null) {
                lines.Add($"Event over: {was.Name}.");
            }

            if (now is not null) {
                lines.Add($"Event on: {now.Name}{TimeLeft(now)}.{HowToJoin(now)}");
            }

            foreach (var line in lines) {
                Logger.Information("{0}", Logger.Args(line));
            }

            Broadcast(lines);
        }
        catch (Exception ex) {
            Logger.Error("Could not check the events: {0}", Logger.Args(ex.Message));
        }
    }

    private static string HowToJoin(RunningEvent running)
        => running.IsDeckathalon ? " Type .deckathalon to climb the tower." : "";

    private static void Broadcast(List<string> lines) {
        if (s_system is null || lines.Count == 0) {
            return;
        }

        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(p => p.CharacterId != 0 && p.ActorPath is not null)) {
            var session = s_system.ActorSelection(player.ActorPath);
            foreach (var line in lines) {
                session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
            }
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
