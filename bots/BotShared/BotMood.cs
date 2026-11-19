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
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// A bot's mood, part of Ultimus Core (Niduus, 2026-11-14): careful after a lost fight, being stuck again and again or
/// many surprises in the last hour; bold after many wins in a row. Careful heals sooner; bold heals a little later. Each
/// bot has its own (Ultimus alone, the Quest Team as a team), with its own numbers (Ultimus's from his playbook).
/// </summary>
internal sealed class BotMood {

    internal enum Level { Careful, Steady, Bold }

    internal const string LOST = "lost";
    internal const string WON = "won";
    internal const string STUCK = "stuck";
    internal const string SURPRISE = "surprise";

    private static readonly ConcurrentDictionary<string, BotMood> s_all = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan s_memory = TimeSpan.FromHours(1);

    // The starting numbers: stuck this many times makes careful, this many wins bold, and how far each moves the heal line.
    private static readonly Dictionary<string, double> s_defaults = new(StringComparer.Ordinal) {
        ["CarefulAfterStuck"] = 4, ["BoldAfterWins"] = 5, ["CarefulHealShift"] = 0.1, ["BoldHealShift"] = 0.05,
    };

    private readonly object _lock = new();
    private readonly List<(DateTime At, string What)> _events = [];
    private Func<string, double> _numbers;

    /// <summary>The mood of a bot; <paramref name="numbers"/> gives its own numbers by name (none: the starting ones).</summary>
    internal static BotMood Of(string bot, Func<string, double> numbers = null) {
        var mood = s_all.GetOrAdd(bot ?? "", _ => new BotMood());
        if (numbers is not null) {
            mood._numbers = numbers;
        }

        return mood;
    }

    private double Number(string name) {
        try {
            return _numbers?.Invoke(name) ?? s_defaults[name];
        }
        catch (Exception) {
            return s_defaults[name];
        }
    }

    internal void Saw(string what) {
        lock (_lock) {
            _events.Add((DateTime.UtcNow, what));
            _events.RemoveAll(e => DateTime.UtcNow - e.At > s_memory);
        }
    }

    internal int Count(string what) {
        lock (_lock) {
            return _events.Count(e => e.What == what && DateTime.UtcNow - e.At <= s_memory);
        }
    }

    /// <summary>Fights won since the last lost fight.</summary>
    internal int WinStreak() {
        lock (_lock) {
            var streak = 0;
            foreach (var e in _events.Where(e => e.What is WON or LOST).OrderByDescending(e => e.At)) {
                if (e.What == LOST) {
                    break;
                }

                streak++;
            }

            return streak;
        }
    }

    internal bool LostRecently => Count(LOST) > 0;

    internal Level Now => Count(LOST) > 0 || Count(STUCK) >= Number("CarefulAfterStuck") || Count(SURPRISE) >= 3 ? Level.Careful
        : WinStreak() >= Number("BoldAfterWins") ? Level.Bold
        : Level.Steady;

    /// <summary>How much sooner (plus) or later (minus) it heals in battle, as a share of health.</summary>
    internal float HealShift => Now switch {
        Level.Careful => (float) Number("CarefulHealShift"),
        Level.Bold => -(float) Number("BoldHealShift"),
        _ => 0f,
    };

}
