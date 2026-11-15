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
using System;
using System.Collections.Concurrent;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Creatures a bot stood on for a while with no battle starting: a player walking into a creature starts one, so this one
/// is not a creature that can be fought right now (a defeated one still shown where its battle was, or one held by
/// something else). Ultimus, the Quest Team and every bot leave it alone for a while and pick another. QT log,
/// 2026-11-15: Freya stood on the Skeletal Pirate left at the spot of her 8:38 battle on Unicorn Way for over two hours.
/// </summary>
internal static class DudCreatures {

    // How long one is left alone; then it is tried again (a creature that respawned there is fought as usual).
    internal static readonly TimeSpan LeaveFor = TimeSpan.FromMinutes(10);

    // How long a bot stands on one with no battle before it counts.
    internal static readonly TimeSpan StandFor = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<ulong, DateTime> s_duds = new();

    internal static void Mark(ulong globalId) {
        if (globalId != 0) {
            s_duds[globalId] = DateTime.UtcNow + LeaveFor;
        }
    }

    internal static bool IsDud(ulong globalId) {
        if (!s_duds.TryGetValue(globalId, out var until)) {
            return false;
        }

        if (DateTime.UtcNow < until) {
            return true;
        }

        s_duds.TryRemove(globalId, out _);
        return false;
    }

    /// <summary>Not in a battle and not one that would not start one: a creature worth walking into.</summary>
    internal static bool Fightable(ulong globalId) => !CombatDuelComponent.IsInBattle(globalId) && !IsDud(globalId);
}
