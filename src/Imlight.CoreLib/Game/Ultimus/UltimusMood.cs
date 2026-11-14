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
using Imlight.CoreLib.Game.BotShared;

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

    internal const string LOST = BotMood.LOST;
    internal const string WON = BotMood.WON;
    internal const string STUCK = BotMood.STUCK;
    internal const string SURPRISE = BotMood.SURPRISE;

    // His mood is the shared engine's (Ultimus Core, BotShared.BotMood), with his playbook's Mood.* numbers.
    private static BotMood Mood => BotMood.Of("Ultimus", name => UltimusPlaybook.Value("Mood." + name));

    internal static void Saw(string what) => Mood.Saw(what);

    private static int Count(string what) => Mood.Count(what);

    private static int WinStreak() => Mood.WinStreak();

    /// <summary>A fight lost in the last hour.</summary>
    internal static bool LostRecently => Mood.LostRecently;

    internal static Level Now => (Level) (int) Mood.Now;

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
    internal static float HealShift => Mood.HealShift;

}
