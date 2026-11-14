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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's memory journal (Niduus, 2026-11-14: "Do you think he remembers? We should probably allow him to have memories to
/// note down"): the notable moments of his play, written down as they happen (a quest done, a world started or finished, a
/// level, a tough fight won or lost, a player met, a step that would not work), kept in logs/ultimus/journal.json across
/// restarts, and read back for his opinions and for "do you remember...?". Nothing in it is made up: every entry is
/// something that happened to him.
/// </summary>
internal static class UltimusJournal {

    private const string FILE = "journal.json";
    private const int MAX_ENTRIES = 1000;

    internal const string QUEST = "quest", WORLD = "world", LEVEL = "level", WON = "won", LOST = "lost", MET = "met", STUCK = "stuck",
        GEAR = "gear", PARTY = "party";

    internal sealed class Entry {
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string World { get; set; } = "";
        public string Area { get; set; } = "";
        public string About { get; set; } = "";
        public string Text { get; set; } = "";
        // How it felt: +1 good, -1 bad, 0 neither.
        public int Feeling { get; set; }
    }

    private static readonly object s_lock = new();
    private static List<Entry> s_entries;

    /// <summary>Writes a moment down. <paramref name="about"/>: who or what it is about (a quest, a creature, a player).</summary>
    internal static void Write(string kind, string text, string world = "", string area = "", string about = "", int feeling = 0) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        lock (s_lock) {
            var all = Entries();
            // The same moment twice in a row is one memory.
            if (all.Count > 0 && all[^1].Text == text && DateTime.UtcNow - all[^1].At < TimeSpan.FromMinutes(10)) {
                return;
            }

            all.Add(new Entry { At = DateTime.UtcNow, Kind = kind, World = world ?? "", Area = area ?? "", About = about ?? "", Text = text.Trim(), Feeling = Math.Sign(feeling) });
            if (all.Count > MAX_ENTRIES) {
                // The oldest plain moments go first; good and bad ones are kept longer.
                var drop = all.FindIndex(e => e.Feeling == 0);
                all.RemoveAt(drop >= 0 ? drop : 0);
            }

            Save();
        }
    }

    /// <summary>Moments about a world (its name, as "Wizard City"), an area, a quest, a creature or a player (copies).</summary>
    internal static List<Entry> About(string subject) {
        if (string.IsNullOrWhiteSpace(subject)) {
            return [];
        }

        var s = subject.Trim();
        lock (s_lock) {
            return [.. Entries().Where(e => Matches(e.World, s) || Matches(e.Area, s) || Matches(e.About, s)
                                                  || e.Text.Contains(s, StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>The latest moments, newest first (copies).</summary>
    internal static List<Entry> Recent(int count) {
        lock (s_lock) {
            return [.. Entries().AsEnumerable().Reverse().Take(count)];
        }
    }

    internal static int Count {
        get {
            lock (s_lock) {
                return Entries().Count;
            }
        }
    }

    private static bool Matches(string value, string subject)
        => !string.IsNullOrWhiteSpace(value) && (value.Contains(subject, StringComparison.OrdinalIgnoreCase) || subject.Contains(value, StringComparison.OrdinalIgnoreCase) && value.Length >= 4);

    /// <summary>"3 days ago", "an hour ago", "just now".</summary>
    internal static string Ago(DateTime at) {
        var span = DateTime.UtcNow - at;
        return span.TotalMinutes < 2 ? "just now"
            : span.TotalMinutes < 60 ? $"{span.TotalMinutes:0} minutes ago"
            : span.TotalHours < 2 ? "an hour ago"
            : span.TotalHours < 24 ? $"{span.TotalHours:0} hours ago"
            : span.TotalDays < 2 ? "yesterday"
            : $"{span.TotalDays:0} days ago";
    }

    private static List<Entry> Entries() {
        if (s_entries is not null) {
            return s_entries;
        }

        s_entries = [];
        try {
            var path = Path.Combine(UltimusWire.Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) is { } read) {
                s_entries = [.. read.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Text)).OrderBy(e => e.At)];
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_entries;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            var path = Path.Combine(UltimusWire.Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Entries(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
