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
/// Players' tips, shared with the Quest Team (Niduus, 2026-11-14): what a player told Ultimus while he was stuck on a quest
/// is kept here by quest, in logs/ultimus/player-tips.json, across restarts. The Quest Team reads it when it gets stuck on
/// that quest or files an issue about it, so the tip is in front of Niduus wherever the quest goes wrong. Already scrubbed
/// of private details before it gets here.
/// </summary>
internal static class UltimusTips {

    private const string FILE = "player-tips.json";
    private const int PER_QUEST = 8;

    internal sealed class Tip {
        public string Step { get; set; } = "";
        public string From { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime At { get; set; }
    }

    // quest -> tips (newest last)
    private static Dictionary<string, List<Tip>> s_quests;
    private static readonly object s_lock = new();

    private static string Path_ => Path.Combine(UltimusWire.Folder, FILE);

    private static Dictionary<string, List<Tip>> Quests {
        get {
            if (s_quests is not null) {
                return s_quests;
            }

            try {
                var read = File.Exists(Path_) ? JsonSerializer.Deserialize<Dictionary<string, List<Tip>>>(File.ReadAllText(Path_)) : null;
                s_quests = new Dictionary<string, List<Tip>>(read ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                Logger.Debug("Ultimus: player tips could not be read: {0}", Logger.Args(ex.Message));
                s_quests = new Dictionary<string, List<Tip>>(StringComparer.OrdinalIgnoreCase);
            }

            return s_quests;
        }
    }

    /// <summary>A player's tip for a step of <paramref name="quest"/> (already scrubbed).</summary>
    internal static void Add(string quest, string step, string from, string text) {
        if (string.IsNullOrEmpty(quest) || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        lock (s_lock) {
            var list = Quests.TryGetValue(quest, out var known) ? known : Quests[quest] = [];
            list.Add(new Tip { Step = step ?? "", From = from ?? "", Text = text, At = DateTime.UtcNow });
            if (list.Count > PER_QUEST) {
                list.RemoveRange(0, list.Count - PER_QUEST);
            }

            Save();
        }
    }

    /// <summary>The tips players gave for <paramref name="quest"/>, newest first, as "From (on "step"): text" lines.</summary>
    internal static List<string> For(string quest, int most = 3) {
        if (string.IsNullOrEmpty(quest)) {
            return [];
        }

        lock (s_lock) {
            return Quests.TryGetValue(quest, out var list)
                ? [.. list.AsEnumerable().Reverse().Take(most)
                    .Select(t => $"{t.From}{(t.Step.Length > 0 ? $" (on \"{UltimusText.Short(t.Step, 60)}\")" : "")}: {t.Text}")]
                : [];
        }
    }

    /// <summary>The quest was marked fixed: its old tips no longer apply.</summary>
    internal static void Forget(string quest) {
        lock (s_lock) {
            if (!string.IsNullOrEmpty(quest) && Quests.Remove(quest)) {
                Save();
            }
        }
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path_, JsonSerializer.Serialize(s_quests, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: player tips could not be saved: {0}", Logger.Args(ex.Message));
        }
    }

}
