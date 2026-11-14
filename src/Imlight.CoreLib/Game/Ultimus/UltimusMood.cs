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
using System.Linq;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's mood: how careful he plays (Niduus, 2026-11-14, "All" to: "after a death or many stuck spots, he plays
/// carefully. On a winning streak, he plays faster"). Careful: he rests to full before every fight, heals sooner in
/// battle and trusts only plans he is sure of. Bold: he rests less and heals a little later. What he did in the last hour
/// sets it: a lost fight, being stuck again and again, or winning fight after fight. His playbook sets the numbers
/// (Mood.*).
/// </summary>
internal static class UltimusMood {

    internal enum Level { Careful, Steady, Bold }

    private static readonly object s_lock = new();
    private static readonly List<(DateTime At, string What)> s_events = [];

    internal const string LOST = "lost";
    internal const string WON = "won";
    internal const string STUCK = "stuck";
    internal const string SURPRISE = "surprise";

    private static readonly TimeSpan s_memory = TimeSpan.FromHours(1);

    internal static void Saw(string what) {
        lock (s_lock) {
            s_events.Add((DateTime.UtcNow, what));
            s_events.RemoveAll(e => DateTime.UtcNow - e.At > s_memory);
        }
    }

    private static int Count(string what) {
        lock (s_lock) {
            return s_events.Count(e => e.What == what && DateTime.UtcNow - e.At <= s_memory);
        }
    }

    // Fights won since the last lost fight.
    private static int WinStreak() {
        lock (s_lock) {
            var streak = 0;
            foreach (var e in s_events.Where(e => e.What is WON or LOST).OrderByDescending(e => e.At)) {
                if (e.What == LOST) {
                    break;
                }

                streak++;
            }

            return streak;
        }
    }

    internal static Level Now {
        get {
            if (Count(LOST) > 0 || Count(STUCK) >= UltimusPlaybook.Value("Mood.CarefulAfterStuck") || Count(SURPRISE) >= 3) {
                return Level.Careful;
            }

            return WinStreak() >= UltimusPlaybook.Value("Mood.BoldAfterWins") ? Level.Bold : Level.Steady;
        }
    }

    /// <summary>Why he feels the way he does, in his words.</summary>
    internal static string Why() {
        var lost = Count(LOST);
        var stuck = Count(STUCK);
        var surprises = Count(SURPRISE);

        return Now switch {
            Level.Careful when lost > 0 => $"I lost a fight in the last hour, so I play carefully: full health before every fight, and I heal sooner ({UltimusPlaybook.Cite("Mood.CarefulHealShift")})",
            Level.Careful when stuck > 0 && surprises < 3 => $"I got stuck {stuck} times in the last hour, so I play carefully and trust only ways I am sure of",
            Level.Careful => $"{surprises} things went differently than I expected in the last hour, so I play carefully",
            Level.Bold => $"I won {WinStreak()} fights in a row, so I play faster: I rest less and heal a little later ({UltimusPlaybook.Cite("Mood.BoldHealShift")})",
            _ => "Things go as I expect, so I play steady",
        };
    }

    /// <summary>How much sooner (plus) or later (minus) he heals in battle, as a share of his health.</summary>
    internal static float HealShift => Now switch {
        Level.Careful => (float) UltimusPlaybook.Value("Mood.CarefulHealShift"),
        Level.Bold => -(float) UltimusPlaybook.Value("Mood.BoldHealShift"),
        _ => 0f,
    };

}
