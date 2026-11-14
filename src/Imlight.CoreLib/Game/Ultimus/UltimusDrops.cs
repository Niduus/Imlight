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
/// Which creatures really count for a fight or collect step (Niduus, 2026-11-14, "learn which creatures/spots actually drop
/// the item"): after each battle won for a step, whether the step's count went up is written down for that creature. The
/// planner then goes for the ones that gave first, and when the step's own creature tags match nothing in the area, it
/// still knows which creatures worked before. Kept in logs/ultimus/drop-memory.json, across restarts. The Quest Team
/// reads and adds to the same memory (Niduus, 2026-11-14: "all"), so every bot learns from the others' fights.
/// </summary>
internal static class UltimusDrops {

    private const string FILE = "drop-memory.json";

    internal sealed class Record {
        public int Gave { get; set; }
        public int Not { get; set; }
        public string Name { get; set; } = "";
        public string Zone { get; set; } = "";
    }

    // quest|goal -> creature template -> record
    private static Dictionary<string, Dictionary<string, Record>> s_steps;
    private static readonly object s_lock = new();

    private static string Path_ => Path.Combine(UltimusWire.Folder, FILE);

    private static Dictionary<string, Dictionary<string, Record>> Steps {
        get {
            if (s_steps is not null) {
                return s_steps;
            }

            try {
                s_steps = File.Exists(Path_)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Record>>>(File.ReadAllText(Path_)) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                Logger.Debug("Ultimus: drop memory could not be read: {0}", Logger.Args(ex.Message));
                s_steps = [];
            }

            return s_steps;
        }
    }

    private static string Key(string quest, string goal) => $"{quest}|{goal}";

    /// <summary>
    /// How far a step is for <paramref name="wizard"/>: its count now, a large number once it is done (or the quest is gone:
    /// it was finished). Read before and after a battle won for the step, by Ultimus and the Quest Team alike.
    /// </summary>
    internal static int StepCount(WizardData.Models.Player.Wizard wizard, string quest, string goal) {
        var held = wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
            && string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase));
        if (held is null) {
            return int.MaxValue;
        }

        var progress = held.GoalProgress?.FirstOrDefault(g => g is not null && string.Equals(g.GoalName, goal, StringComparison.Ordinal));
        return progress is null || progress.IsGoalCompleted() ? int.MaxValue : progress.CurrentProgress;
    }

    /// <summary>A battle won against <paramref name="template"/> for this step; <paramref name="counted"/>: the step's count went up.</summary>
    internal static void Fought(string quest, string goal, ulong template, string name, string zone, bool counted) {
        if (string.IsNullOrEmpty(quest) || string.IsNullOrEmpty(goal) || template == 0) {
            return;
        }

        lock (s_lock) {
            var step = Steps.TryGetValue(Key(quest, goal), out var known) ? known : Steps[Key(quest, goal)] = [];
            var id = template.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var record = step.TryGetValue(id, out var r) ? r : step[id] = new Record();
            if (counted) {
                record.Gave++;
            }
            else {
                record.Not++;
            }

            record.Name = name ?? record.Name;
            record.Zone = zone ?? record.Zone;
            Save();
        }
    }

    /// <summary>How good a creature is for this step: above 0 it gave before, below 0 it was fought 3+ times for nothing.</summary>
    internal static int Score(string quest, string goal, ulong template) {
        lock (s_lock) {
            if (!Steps.TryGetValue(Key(quest, goal), out var step)
                || !step.TryGetValue(template.ToString(System.Globalization.CultureInfo.InvariantCulture), out var record)) {
                return 0;
            }

            return record.Gave > 0 ? 1 + record.Gave : record.Not >= 3 ? -1 : 0;
        }
    }

    /// <summary>The creatures that gave for this step before (most first).</summary>
    internal static List<ulong> Givers(string quest, string goal) {
        lock (s_lock) {
            return Steps.TryGetValue(Key(quest, goal), out var step)
                ? [.. step.Where(s => s.Value.Gave > 0).OrderByDescending(s => s.Value.Gave)
                    .Select(s => ulong.TryParse(s.Key, out var id) ? id : 0).Where(id => id != 0)]
                : [];
        }
    }

    /// <summary>One line on what is known for this step, for the quest tracker ("" when nothing).</summary>
    internal static string Describe(string quest, string goal) {
        lock (s_lock) {
            if (!Steps.TryGetValue(Key(quest, goal), out var step) || step.Count == 0) {
                return "";
            }

            var gave = step.Values.Where(r => r.Gave > 0).OrderByDescending(r => r.Gave).Select(r => $"{r.Name} ({r.Gave})").Take(3).ToList();
            var not = step.Values.Where(r => r.Gave == 0 && r.Not >= 3).Select(r => r.Name).Take(3).ToList();
            var parts = new List<string>();
            if (gave.Count > 0) {
                parts.Add("counted from " + string.Join(", ", gave));
            }

            if (not.Count > 0) {
                parts.Add("never from " + string.Join(", ", not));
            }

            return string.Join("; ", parts);
        }
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path_, JsonSerializer.Serialize(s_steps, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: drop memory could not be saved: {0}", Logger.Args(ex.Message));
        }
    }

}
