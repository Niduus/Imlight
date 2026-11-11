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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// A creature's Intellect, Selfishness and Aggression (Niduus, 2026-11-11: "make the mobs Intellect, Selfishness and
/// Aggression customizable"), as the chance a round (0 to 1) that CombatCreatureAIComponent rolls each one.
///   Intellect    picks its biggest attack or heal instead of a random one (and aims a multi-target spell well).
///   Selfishness  casts a buff, or a heal when it is hurt, on itself instead of a teammate.
///   Aggression   attacks instead of preparing (blades, traps, heals, shields).
/// Which value counts, read every round so a change applies in a battle already going:
///   1. the creature's own in the app's Bosses tab (BossOverrides Intellect / Selfishness / Aggression, percent);
///   2. [Combat] CreatureIntellect / CreatureSelfishness / CreatureAggression in Imlight.ini (percent, -1 = off);
///   3. the game's own (NPCBehaviorTemplate). The game keeps Intellect and Selfishness as 0 to 1 (a few creatures say
///      9 or 7, out of 10) and Aggression as a whole number out of 10 (8 = 80%). Aggression used to be read as 0 to 1,
///      so every creature with 1 or more attacked every round it could.
/// </summary>
internal static class CreatureAttitude {

    internal const string INTELLECT = "Intellect";
    internal const string SELFISHNESS = "Selfishness";
    internal const string AGGRESSION = "Aggression";

    /// <summary>The game's Intellect of a creature, 0 to 1.</summary>
    internal static float GameIntellect(NPCBehaviorTemplate npc) => Fraction(npc?.m_fIntelligence ?? 0);

    /// <summary>The game's Selfishness of a creature, 0 to 1.</summary>
    internal static float GameSelfishness(NPCBehaviorTemplate npc) => Fraction(npc?.m_fSelfishFactor ?? 0);

    /// <summary>The game's Aggression of a creature, 0 to 1 (the game keeps it out of 10).</summary>
    internal static float GameAggression(NPCBehaviorTemplate npc) => Math.Clamp((npc?.m_nAggressiveFactor ?? 0) / 10f, 0f, 1f);

    /// <summary>A chance as a whole percent, as the app shows it.</summary>
    internal static int Percent(float chance) => (int) MathF.Round(Math.Clamp(chance, 0f, 1f) * 100f);

    /// <summary>
    /// The chance that counts this round: the creature's own change (percent), else the server-wide setting, else the game's.
    /// </summary>
    internal static float Effective(string which, float? own, float game) {
        if (own is { } percent && !float.IsNaN(percent)) {
            return Math.Clamp(percent / 100f, 0f, 1f);
        }

        var everyone = ConfigurationManager.GetValue($"Combat.Creature{which}", -1f);

        return everyone >= 0 ? Math.Clamp(everyone / 100f, 0f, 1f) : game;
    }

    // 0 to 1, or a few creatures' 9 or 7: out of 10.
    private static float Fraction(float value)
        => float.IsNaN(value) ? 0f : Math.Clamp(value > 1f ? value / 10f : value, 0f, 1f);

}
