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
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>The ground's height where a Ultimus wizard walks, so the game shows them on the ground, not in the air.</summary>
internal static class UltimusGround {

    /// <summary>The ground at (x, y) nearest to <paramref name="z"/> (a balcony or the hall under it), or <paramref name="z"/> when unknown.</summary>
    internal static float HeightAt(string zonePath, float x, float y, float z) {
        try {
            if (ZoneGround.WalkOf(zonePath) is not { } ground) {
                return z;
            }

            var gx = (int) MathF.Floor((x - ground.Left) / ground.Cell);
            var gy = (int) MathF.Floor((ground.Top - y) / ground.Cell);
            if (gx < 0 || gy < 0 || gx >= ground.Width || gy >= ground.Height) {
                return z;
            }

            var at = (gy * ground.Width) + gx;
            var (high, low) = (ground.Heights[at], ground.Lows[at]);
            if (float.IsNaN(high)) {
                return z;
            }

            // A cell's ground at two heights: the one nearer to where the wizard is (no jumping onto a roof).
            var best = float.IsNaN(low) || MathF.Abs(high - z) <= MathF.Abs(low - z) ? high : low;

            // A big jump is a cell the file reads wrong (a roof edge): keep the height for now.
            return MathF.Abs(best - z) > 400 ? z : best;
        }
        catch (Exception) {
            return z;
        }
    }

}
