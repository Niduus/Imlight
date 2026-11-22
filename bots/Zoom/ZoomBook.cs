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
/// Zoom's own mind file (Niduus, 2026-11-22: "his goal is to mark down broken quests and/or broken steps in quests. These are
/// to be of his own mind file for data collection"): logs/zoom/zoom-mind.json, with a readable copy in zoom-mind.txt. It
/// holds every broken quest step he found (a step that never finished), every broken quest (the next main quest never
/// came after one he finished), and every main quest he got through. It is his alone: nothing else writes it, and nothing
/// in it goes into what Ultimus and the teams learn from.
/// </summary>
internal static class ZoomBook {

    // Each scout has his own (Zoom: logs/zoom/zoom-mind.json; Savitar: logs/savitar/savitar-mind.json).
    private static UltimusSeat Me => UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom;
    internal static string MIND_JSON => $"{Me.Key.ToLowerInvariant()}-mind.json";
    internal static string MIND_TXT => $"{Me.Key.ToLowerInvariant()}-mind.txt";

    /// <summary>A broken step of a quest.</summary>
    internal const string STEP = "step";

    /// <summary>A broken quest: the next main quest never came.</summary>
    internal const string QUEST = "quest";

    /// <summary>One broken quest or step Zoom found.</summary>
    internal sealed record Broken(DateTime At, string Kind, string Quest, string Title, int Step, string Goal, string Zone, string Why, string Doing);

    /// <summary>One main quest he got through.</summary>
    internal sealed record Scouted(DateTime At, string Quest, string Title, int Seconds, int Teleports, int BrokenSteps);

    /// <summary>His whole mind file.</summary>
    internal sealed class Mind {
        public List<Broken> Broken { get; set; } = [];
        public List<Scouted> Scouted { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<string, Mind> s_minds = [];

    private static string Folder => Me.Folder;

    private static Mind Load() {
        if (s_minds.TryGetValue(Folder, out var known)) {
            return known;
        }

        Mind mind;
        try {
            var path = Path.Combine(Folder, MIND_JSON);
            mind = File.Exists(path) ? JsonSerializer.Deserialize<Mind>(File.ReadAllText(path)) ?? new Mind() : new Mind();
        }
        catch (Exception e) {
            Logger.Warning("{0}: his mind file could not be read ({1}); a new one is started.", Logger.Args(Me.Name, e.Message));
            mind = new Mind();
        }

        mind.Broken ??= [];
        mind.Scouted ??= [];
        s_minds[Folder] = mind;
        return mind;
    }

    /// <summary>Whether this quest's step (or, for a broken quest, what came after it) is already marked down.</summary>
    internal static bool Has(string kind, string quest, string goal) {
        lock (s_lock) {
            return Load().Broken.Any(b => b.Kind == kind && b.Quest == quest && b.Goal == goal);
        }
    }

    /// <summary>Marks a broken quest or step down (once each).</summary>
    internal static void Add(Broken broken) {
        lock (s_lock) {
            var mind = Load();
            if (mind.Broken.Any(b => b.Kind == broken.Kind && b.Quest == broken.Quest && b.Goal == broken.Goal)) {
                return;
            }

            mind.Broken.Add(broken);
            Save(mind);
        }
    }

    /// <summary>A main quest he got through: how long it took, how many teleports, and how many of its steps were broken.</summary>
    internal static void Done(string quest, string title, TimeSpan took, int hops, int broken) {
        lock (s_lock) {
            var mind = Load();
            mind.Scouted.Add(new Scouted(DateTime.UtcNow, quest, title, (int) took.TotalSeconds, hops, broken));
            Save(mind);
        }
    }

    /// <summary>Writes his readable mind page again (his hive's cheat findings changed).</summary>
    internal static void Refresh() {
        lock (s_lock) {
            WriteText(Load());
        }
    }

    private static void Save(Mind mind) {
        try {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, MIND_JSON), JsonSerializer.Serialize(mind, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("{0}: could not write his mind file: {1}", Logger.Args(Me.Name, e.Message));
        }

        WriteText(mind);
    }

    // The readable page (Ahziael, Zoom's mind): broken quests and steps, his cheat findings in short (ZoomHive), scouted quests.
    private static void WriteText(Mind mind) {
        try {
            Directory.CreateDirectory(Folder);
            var lines = new List<string> {
                $"{Me.Name}'s mind: {mind.Broken.Count(b => b.Kind == QUEST)} broken quest(s) and {mind.Broken.Count(b => b.Kind == STEP)} broken quest step(s) " +
                $"found ahead of Ultimus and the teams, {mind.Scouted.Count} main quest(s) scouted. Newest first.",
                "",
                "BROKEN",
            };
            foreach (var b in Enumerable.Reverse(mind.Broken)) {
                lines.Add(b.Kind == QUEST
                    ? $"{b.At.ToLocalTime():yyyy-MM-dd HH:mm}  Broken quest: the next main quest after {b.Title} ({b.Quest}) never came."
                    : $"{b.At.ToLocalTime():yyyy-MM-dd HH:mm}  Broken step: {b.Title} ({b.Quest}), step {b.Step}: {b.Goal}");
                lines.Add($"    Where: {b.Zone}");
                lines.Add($"    What happened: {b.Why}");
                lines.Add($"    He was: {b.Doing}");
                lines.Add("");
            }

            lines.AddRange(ZoomHive.Summary());
            lines.Add($"    (every cheat in full: {ZoomHive.HIVE}/{ZoomHive.CHEATS_TXT})");
            lines.Add("");
            lines.Add("SCOUTED");
            foreach (var s in Enumerable.Reverse(mind.Scouted)) {
                lines.Add($"{s.At.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Title} ({s.Quest}): {s.Seconds / 60} min {s.Seconds % 60} s, " +
                          $"{s.Teleports} teleport{(s.Teleports == 1 ? "" : "s")}{(s.BrokenSteps > 0 ? $", {s.BrokenSteps} broken step(s)" : ", clean")}");
            }

            File.WriteAllLines(Path.Combine(Folder, MIND_TXT), lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("{0}: could not write his mind file: {1}", Logger.Args(Me.Name, e.Message));
        }
    }

}
