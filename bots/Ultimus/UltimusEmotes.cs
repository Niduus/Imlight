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
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's emotes (Niduus, 2026-11-14, approved): a wave for a player who greets him, a cheer after a tough fight won, and
/// a sit when he has nothing to do for a while, played from the emote wheel as a player plays them. The animation names
/// are the ones real players' games send from their own emote wheels (learned as they use them, kept in
/// emotes-seen.txt), or [Ultimus] EmoteWave, EmoteCheer, EmoteSit when set; until one is known a plain guess is tried.
/// </summary>
internal static class UltimusEmotes {

    internal const string WAVE = "wave", CHEER = "cheer", SIT = "sit";
    private const string FILE = "emotes-seen.txt";

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<ConcurrentDictionary<string, DateTime>> s_seenSeat = new(() => new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));
    private static ConcurrentDictionary<string, DateTime> s_seen { get => s_seenSeat.Value; set => s_seenSeat.Value = value; }
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<bool> s_readSeat = new();
    private static bool s_read { get => s_readSeat.Value; set => s_readSeat.Value = value; }

    /// <summary>A real player played an emote from their wheel: its animation name is remembered.</summary>
    internal static void Seen(ulong charId, string animation) {
        if (string.IsNullOrWhiteSpace(animation) || animation.Length > 80 || UltimusRoster.IsMember(charId)
            || QuestTeam.QuestTeamRoster.IsMember(charId)) {
            return;
        }

        Read();
        if (s_seen.TryAdd(animation.Trim(), DateTime.UtcNow)) {
            try {
                Directory.CreateDirectory(UltimusWire.SharedFolder);
                File.AppendAllLines(Path.Combine(UltimusWire.SharedFolder, FILE), [animation.Trim()]);
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(FILE, e.Message));
            }
        }
    }

    /// <summary>The animation for a wave, a cheer or a sit: the setting, else one learned from players, else a guess.</summary>
    internal static string Animation(string kind) {
        var set = ConfigurationManager.GetValue("Ultimus.Emote" + char.ToUpperInvariant(kind[0]) + kind[1..], "");
        if (!string.IsNullOrWhiteSpace(set)) {
            return set.Trim();
        }

        Read();
        string[] words = kind switch {
            WAVE => ["wave", "hello", "greet"],
            CHEER => ["cheer", "celebrat", "applau", "victory"],
            _ => ["sit"],
        };
        var learned = s_seen.Keys.Where(n => words.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(n => kind != SIT || !n.Contains("site", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Length).FirstOrDefault();

        return learned ?? kind switch { WAVE => "Wave", CHEER => "Cheer", _ => "Sit" };
    }

    /// <summary>Whether the name was learned from a player or set (not a guess).</summary>
    internal static bool Known(string kind, string animation)
        => s_seen.ContainsKey(animation) || !string.IsNullOrWhiteSpace(ConfigurationManager.GetValue("Ultimus.Emote" + char.ToUpperInvariant(kind[0]) + kind[1..], ""));

    private static void Read() {
        if (s_read) {
            return;
        }

        s_read = true;
        try {
            var path = Path.Combine(UltimusWire.SharedFolder, FILE);
            if (File.Exists(path)) {
                foreach (var line in File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l))) {
                    s_seen.TryAdd(line.Trim(), DateTime.MinValue);
                }
            }
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(FILE, e.Message));
        }
    }

}
