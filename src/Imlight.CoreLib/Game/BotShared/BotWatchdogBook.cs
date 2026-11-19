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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The watchdog book (Niduus, 2026-11-19: "Expand more on the Watchdog and incorporate some of its needed data to the bots").
/// Every watchdog alarm and stop of every bot (Ultimus, each TCO7 member, the Quest Team) is written down with where it
/// happened, the quest and step, how long nothing moved, and, once the bot got going again, what got it going. All the bots
/// read it: stuck by a spot in the book, the move that freed a bot there is tried first (server data before any AI), and a
/// spot that stopped a bot with nothing known to free it counts as a roadblock at once. Kept in logs\ultimus\watchdog-book.json
/// (shared like his maps) and written out in plain words as watchdog-book.txt.
/// </summary>
internal static class BotWatchdogBook {

    internal const string FILE = "watchdog-book.json";
    internal const string TEXT_FILE = "watchdog-book.txt";
    internal const string ALARM = "alarm", STOP = "stop";
    private const float SAME_SPOT = 800f;
    private const int MOST = 400;

    internal sealed class Entry {
        public string Bot { get; set; } = "";
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public string Quest { get; set; } = "";
        public string Step { get; set; } = "";
        public string Kind { get; set; } = ALARM;
        public double Minutes { get; set; }
        public int Times { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        public bool Open { get; set; }
        public string FreedBy { get; set; } = "";
        public double FreedAfterMinutes { get; set; }
    }

    private static readonly object s_lock = new();
    private static List<Entry> s_entries;

    private static string Folder => Ultimus.UltimusWire.SharedFolder;

    /// <summary>A watchdog alarm or stop: written down (or counted again at the same spot on the same quest).</summary>
    internal static void Note(string bot, string zone, float x, float y, string quest, string step, string kind, double minutes) {
        try {
            lock (s_lock) {
                var all = Entries();
                var entry = all.FirstOrDefault(e => string.Equals(e.Zone, zone ?? "", StringComparison.OrdinalIgnoreCase)
                                                    && string.Equals(e.Quest, quest ?? "", StringComparison.OrdinalIgnoreCase)
                                                    && Far(e.X, e.Y, x, y) <= SAME_SPOT);
                if (entry is null) {
                    all.Add(entry = new Entry { Zone = zone ?? "", X = x, Y = y, Quest = quest ?? "", First = DateTime.UtcNow });
                    if (all.Count > MOST) {
                        all.Remove(all.OrderBy(e => e.Last).First());
                    }
                }

                // A stop outranks an alarm at the same spot; the longest wait is kept.
                entry.Bot = bot ?? "";
                entry.Step = step ?? "";
                entry.Kind = kind == STOP || entry.Kind == STOP ? STOP : ALARM;
                entry.Minutes = Math.Max(entry.Minutes, Math.Round(minutes));
                entry.Times++;
                entry.Last = DateTime.UtcNow;
                entry.Open = true;
                Save(all);
            }
        }
        catch (Exception e) {
            Logger.Debug("Bots: the watchdog book could not be written: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// The bot got going again after a watchdog alarm: <paramref name="move"/> (an unstuck move's name, as UltimusFixes and
    /// BotLessons call them) is what freed it, written on its open entries.
    /// </summary>
    internal static void Freed(string bot, string move) {
        if (string.IsNullOrWhiteSpace(move)) {
            return;
        }

        try {
            lock (s_lock) {
                var all = Entries();
                var open = all.Where(e => e.Open && string.Equals(e.Bot, bot ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
                if (open.Count == 0) {
                    return;
                }

                foreach (var entry in open) {
                    entry.Open = false;
                    entry.FreedBy = move;
                    entry.FreedAfterMinutes = Math.Round((DateTime.UtcNow - entry.Last).TotalMinutes + entry.Minutes);
                }

                Save(all);
            }
        }
        catch (Exception e) {
            Logger.Debug("Bots: the watchdog book could not be written: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>What the book knows of this spot: the entries within reach, newest first.</summary>
    internal static List<Entry> Near(string zone, float x, float y) {
        lock (s_lock) {
            return [.. Entries().Where(e => string.Equals(e.Zone, zone ?? "", StringComparison.OrdinalIgnoreCase) && Far(e.X, e.Y, x, y) <= SAME_SPOT)
                .OrderByDescending(e => e.Last)];
        }
    }

    /// <summary>The move that freed a bot by this spot before (one of <paramref name="allowed"/>), with a line saying so; null when none.</summary>
    internal static string Advice(string zone, float x, float y, ICollection<string> allowed, out string line) {
        line = "";
        var freed = Near(zone, x, y).Where(e => e.FreedBy.Length > 0 && (allowed is null || allowed.Contains(e.FreedBy)))
            .GroupBy(e => e.FreedBy).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (freed is null) {
            return null;
        }

        var first = freed.First();
        line = $"The watchdog book says {first.Bot} lost {first.Minutes:0} minutes here once, and \"{freed.Key}\" got it going again.";

        return freed.Key;
    }

    /// <summary>Whether a bot was stopped by its watchdog by this spot and nothing is known to free it (a roadblock at once).</summary>
    internal static bool Trap(string zone, float x, float y)
        => Near(zone, x, y).Any(e => e.Kind == STOP && e.FreedBy.Length == 0);

    /// <summary>The book's lines about this spot, for a bot's stuck thinking (at most three).</summary>
    internal static IEnumerable<string> Lines(string zone, float x, float y)
        => Near(zone, x, y).Take(3).Select(e => $"{e.Bot}: watchdog {e.Kind} after {e.Minutes:0} min on \"{e.Step}\"" +
                                                 (e.FreedBy.Length > 0 ? $"; freed by \"{e.FreedBy}\"" : "; nothing has freed a bot here yet"));

    /// <summary>A short summary for a bot's link file: how many spots, how many stops, how many have a known way out.</summary>
    internal static string Summary() {
        lock (s_lock) {
            var all = Entries();
            return $"{all.Count} watchdog spots ({all.Count(e => e.Kind == STOP)} stops), {all.Count(e => e.FreedBy.Length > 0)} with a known way out";
        }
    }

    private static List<Entry> Entries() {
        if (s_entries is not null) {
            return s_entries;
        }

        s_entries = [];
        try {
            var path = Path.Combine(Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) is { } saved) {
                s_entries = saved.Where(e => e is not null).ToList();
            }
        }
        catch (Exception e) {
            Logger.Debug("Bots: the watchdog book could not be read: {0}", Logger.Args(e.Message));
        }

        return s_entries;
    }

    private static void Save(List<Entry> all) {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, FILE), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        var text = new StringBuilder();
        text.AppendLine($"Watchdog book ({DateTime.Now:yyyy-MM-dd HH:mm}): every spot where a bot's watchdog went off, and what got it going again.");
        text.AppendLine();
        foreach (var e in all.OrderByDescending(e => e.Last)) {
            text.AppendLine($"{e.Last.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Bot}  {e.Kind.ToUpperInvariant()} x{e.Times}  {e.Minutes:0} min  {e.Zone} ({e.X:0}, {e.Y:0})");
            text.AppendLine($"    quest: {e.Quest}  step: {e.Step}");
            text.AppendLine(e.FreedBy.Length > 0 ? $"    got going again by: {e.FreedBy} (after {e.FreedAfterMinutes:0} min)" :
                e.Open ? "    still open" : "    got going again by itself");
        }

        File.WriteAllText(Path.Combine(Folder, TEXT_FILE), text.ToString());
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}
