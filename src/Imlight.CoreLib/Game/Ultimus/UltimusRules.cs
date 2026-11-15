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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// An off switch and a "watch only" mode for every new rule (Niduus, 2026-11-15, the robustness round: fewer fix-and-release
/// rounds). Each new behavior has a playbook line in the Rules section: 2 = on, 1 = watch only (he plays as before and
/// only writes "I would have ..." in his Thoughts and in shadow-log.txt), 0 = off. A rule that turns out wrong is switched
/// off in the playbook within seconds, no update needed; a risky new rule can ship in watch-only mode for a night first,
/// and its "would have" lines show whether it is right before it is given control.
/// </summary>
internal static class UltimusRules {

    internal const int OFF = 0, WATCH = 1, ON = 2;

    internal const string FLIP_FLOP = "FlipFlop", SIDE_TRIP_DROP = "SideTripDrop", OFFERED_DOOR = "OfferedDoor", REPEAT_GUARD = "RepeatGuard";

    private const string SHADOW_LOG = "shadow-log.txt";

    private static readonly ConcurrentDictionary<string, DateTime> s_said = new(StringComparer.Ordinal);

    /// <summary>The rule's mode from the playbook (Rules.Name): 0 off, 1 watch only, 2 on.</summary>
    internal static int Mode(string rule) => (int) Math.Clamp(Math.Round(UltimusPlaybook.Value("Rules." + rule)), OFF, ON);

    internal static bool On(string rule) => Mode(rule) == ON;

    internal static bool Watching(string rule) => Mode(rule) == WATCH;

    /// <summary>
    /// Watch-only mode: what the rule would have done, said in his Thoughts (once per 2 minutes for the same rule and
    /// <paramref name="key"/>) and saved to shadow-log.txt with the time.
    /// </summary>
    internal static void WouldHave(string rule, string what, string key = "") {
        var id = rule + "|" + key;
        if (s_said.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2)) {
            return;
        }

        s_said[id] = DateTime.UtcNow;
        UltimusLink.Note(UltimusRoster.NAME, $"WATCH ONLY (playbook Rules.{rule} = 1): I would have {what}", UltimusLink.THOUGHT);
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.AppendAllText(Path.Combine(UltimusWire.Folder, SHADOW_LOG), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{rule}\t{what}{Environment.NewLine}");
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }
    }
}
