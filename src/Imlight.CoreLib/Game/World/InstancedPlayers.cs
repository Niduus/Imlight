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
 *
 * ========================================================================
 * INSTANCED PLAYERS
 * ========================================================================
 *
 * PURPOSE:
 * Knows which characters are in an instanced zone (a dungeon, a sigil's private copy, an arena), so a
 * teleport to a friend or group mate there is refused, as on live (Niduus, 2026-10-22).
 *
 * USAGE EXAMPLE:
 * InstancedPlayers.Enter(charId, zone);   // Zone, when a player joins an instance
 * InstancedPlayers.IsInInstance(charId);  // FriendsService, GroupService
 *
 * NOTE:
 * - In memory only. Each zone records its own players and removes only its own, so a player already in
 *   the next zone is not removed by the zone they left.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/22/2026
 */

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Maps each character in an instanced zone to that zone.
/// </summary>
internal static class InstancedPlayers {

    private static readonly ConcurrentDictionary<ulong, object> s_zones = new();

    /// <summary>Records that the character is in this instanced zone.</summary>
    internal static void Enter(ulong charId, object zone) {
        if (charId != 0 && zone is not null) {
            s_zones[charId] = zone;
        }
    }

    /// <summary>Forgets the character, if this zone is still the one recorded for them.</summary>
    internal static void Leave(ulong charId, object zone)
        => s_zones.TryRemove(new KeyValuePair<ulong, object>(charId, zone));

    /// <summary>Whether the character is in an instanced zone.</summary>
    internal static bool IsInInstance(ulong charId)
        => s_zones.ContainsKey(charId);

}
