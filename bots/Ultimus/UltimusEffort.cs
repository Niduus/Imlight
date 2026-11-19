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
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How well his own efforts went, the bad as much as the good (Niduus, 2026-11-16: "We need him to become more aware of
/// his efforts, choosing to be fast and efficient instead of fleeing battles and losing dungeons ... We need his awareness
/// to not only reflect the good he does, but the bad"). Each fight won (and how fast), each fight lost, each flee, each
/// dungeon lost and each step he could not get past is kept for the day, written to logs/ultimus/effort.txt, and given to
/// his stuck thinking and his lessons. A flee from a dungeon or instance is the worst mark: the whole run starts over.
/// </summary>
internal static class UltimusEffort {

    private const string FILE = "effort.txt";

    /// <summary>What kind of effort it was.</summary>
    internal enum Kind {
        FightWon,
        FightLost,
        Fled,
        DungeonLost,
        StuckStep,
        PotionSaved,
        PotionUsedWell,
    }

    private static readonly object s_lock = new();
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<DateTime> s_daySeat = new(() => DateTime.Today);
    private static DateTime s_day { get => s_daySeat.Value; set => s_daySeat.Value = value; }
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<Dictionary<Kind, int>> s_countsSeat = new(() => new Dictionary<Kind, int>());
    private static Dictionary<Kind, int> s_counts { get => s_countsSeat.Value; set => s_countsSeat.Value = value; }
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<double> s_wonSecondsSeat = new();
    private static double s_wonSeconds { get => s_wonSecondsSeat.Value; set => s_wonSecondsSeat.Value = value; }
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<double> s_lostSecondsSeat = new();
    private static double s_lostSeconds { get => s_lostSecondsSeat.Value; set => s_lostSecondsSeat.Value = value; }

    private static bool IsBad(Kind kind) => kind is Kind.FightLost or Kind.Fled or Kind.DungeonLost or Kind.StuckStep;

    /// <summary>Notes one effort, good or bad, with what it was and the seconds it took.</summary>
    internal static void Note(Kind kind, string what, double seconds = 0) {
        lock (s_lock) {
            if (DateTime.Today != s_day) {
                s_day = DateTime.Today;
                s_counts.Clear();
                s_wonSeconds = s_lostSeconds = 0;
            }

            s_counts[kind] = s_counts.GetValueOrDefault(kind) + 1;
            if (kind == Kind.FightWon) {
                s_wonSeconds += seconds;
            }
            else if (IsBad(kind)) {
                s_lostSeconds += seconds;
            }
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.AppendAllText(Path.Combine(UltimusWire.Folder, FILE),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(IsBad(kind) ? "BAD " : "GOOD")} {kind}: {what}{(seconds > 0 ? $" ({seconds:0}s)" : "")}{Environment.NewLine}");
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }
    }

    private static int Count(Kind kind) => s_counts.GetValueOrDefault(kind);

    /// <summary>Today, both sides, in his own words; empty before anything happened.</summary>
    internal static string Today() {
        lock (s_lock) {
            if (DateTime.Today != s_day || s_counts.Count == 0) {
                return "";
            }

            var won = Count(Kind.FightWon);
            var good = new List<string>();
            if (won > 0) {
                good.Add($"{won} fight(s) won, {s_wonSeconds / won:0} seconds each on average");
            }

            if (Count(Kind.PotionUsedWell) > 0) {
                good.Add($"{Count(Kind.PotionUsedWell)} potion(s) saved for a boss");
            }

            var bad = new List<string>();
            if (Count(Kind.FightLost) > 0) {
                bad.Add($"{Count(Kind.FightLost)} fight(s) lost");
            }

            if (Count(Kind.Fled) > 0) {
                bad.Add($"{Count(Kind.Fled)} flee(s)");
            }

            if (Count(Kind.DungeonLost) > 0) {
                bad.Add($"{Count(Kind.DungeonLost)} dungeon(s) lost and started over");
            }

            if (Count(Kind.StuckStep) > 0) {
                bad.Add($"{Count(Kind.StuckStep)} step(s) I could not get past");
            }

            if (s_lostSeconds >= 60) {
                bad.Add($"about {s_lostSeconds / 60:0} minute(s) lost to them");
            }

            return $"My effort today. Good: {(good.Count > 0 ? string.Join(", ", good) : "nothing yet")}. " +
                   $"Bad: {(bad.Count > 0 ? string.Join(", ", bad) : "nothing")}.";
        }
    }

    /// <summary>Lines for his stuck thinking: both sides of today, and the rule that fleeing a dungeon is the worst choice.</summary>
    internal static List<string> Facts() {
        var facts = new List<string>();
        if (Today() is { Length: > 0 } today) {
            facts.Add(today);
        }

        facts.Add("Fast and efficient beats fleeing: fleeing a battle in a dungeon or instance loses the whole run, so it is the worst use of time. " +
                  "Prepare before (potions right before the boss, a full deck) instead.");

        return facts;
    }

}
