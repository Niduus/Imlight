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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Decides when a wizard has walked into a health, mana or gold wisp. Moves are checked every quarter second, so a
/// wizard running or on a mount could step right past a wisp between two checks and never touch it (Niduus,
/// 2026-10-26: "wisp detection can be a bit looser to pick it up better"). This checks the whole line walked since the
/// last check, on the ground ([World] WispPickupRadius, read live), with some room for height.
/// </summary>
internal sealed class WispPickup {

    // Farther than this between two checks is a teleport, not a walk: only where the wizard landed counts.
    private const float MAX_STEP = 1500f;

    internal static float Radius => Math.Clamp(ConfigurationManager.GetValue("World.WispPickupRadius", 175f), 25f, 1000f);

    private readonly Dictionary<ulong, (float X, float Y, float Z)> _lastLocations = [];

    /// <summary>
    /// True when the wizard is in reach of the wisp at <paramref name="wisp"/> now or passed through it since the last
    /// check.
    /// </summary>
    internal bool Reached(CoreObject player, CoreObject wisp) {
        var radius = Radius;
        var now = (player.m_location.X, player.m_location.Y, player.m_location.Z);
        var key = player.m_globalID.Full;
        var from = _lastLocations.TryGetValue(key, out var last) ? last : now;
        _lastLocations[key] = now;

        var stepX = now.X - from.X;
        var stepY = now.Y - from.Y;
        var stepZ = now.Z - from.Z;
        var stepLengthSquared = (stepX * stepX) + (stepY * stepY);
        if (stepLengthSquared > MAX_STEP * MAX_STEP) {
            from = now;
            stepX = stepY = stepZ = 0;
            stepLengthSquared = 0;
        }

        // The point of the walked line closest to the wisp, on the ground.
        var target = wisp.m_location;
        var t = stepLengthSquared > 0
            ? Math.Clamp((((target.X - from.X) * stepX) + ((target.Y - from.Y) * stepY)) / stepLengthSquared, 0f, 1f)
            : 1f;
        var closestX = from.X + (stepX * t);
        var closestY = from.Y + (stepY * t);
        var closestZ = from.Z + (stepZ * t);

        var dx = target.X - closestX;
        var dy = target.Y - closestY;
        var dz = target.Z - closestZ;

        // Wisps float a little above the ground, and slopes and stairs move the wizard up and down.
        return (dx * dx) + (dy * dy) <= radius * radius && Math.Abs(dz) <= radius * 1.5f;
    }

    /// <summary>
    /// Forgets a wizard who left the zone, so their next walk in starts fresh.
    /// </summary>
    internal void Forget(ulong playerId) => _lastLocations.Remove(playerId);

}
