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
using Imlight.CoreLib.Game.Zone.Core;
using NVector3 = System.Numerics.Vector3;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// What ground a bot stands on (Niduus, 2026-11-12: "bot placements are very random and don't seem organic", a bot in
/// the middle of a pond): the street's own ground from its collision file (paths, grass, water, walls). Bots come in and
/// stop only on paths, away from water, and walk on paths and grass but never through water.
/// </summary>
internal static class BotGround {

    private static readonly ConcurrentDictionary<string, bool> s_fewPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kind of ground at the point (ZoneGround.PATH, GRASS, WATER...), or null when the street has none.</summary>
    internal static byte? KindAt(string zonePath, NVector3 point) {
        if (Cell(zonePath, point, out var ground) is not { } at) {
            return ground is null ? null : ZoneGround.NONE;
        }

        return ground.Kinds[at];
    }

    /// <summary>A place to come in or stop at: a path (or a dock or bridge), not at the water's edge.</summary>
    internal static bool GoodSpot(string zonePath, NVector3 point) {
        var ground = ZoneGround.WalkOf(zonePath);
        if (ground is null) {
            return true;
        }

        var kind = KindAt(zonePath, point);
        var onPath = kind is ZoneGround.PATH or ZoneGround.PATH_OVER or ZoneGround.WOOD
                     // A street of grass and plain floors (no paths marked): those count as its paths.
                     || (FewPaths(zonePath, ground) && kind is ZoneGround.GRASS or ZoneGround.NONE);

        return onPath && Around(zonePath, point, 2, ZoneGround.WATER) == 0;
    }

    /// <summary>Ground a bot may walk over: anything but water and walls.</summary>
    internal static bool CanStep(string zonePath, NVector3 point)
        => KindAt(zonePath, point) is not (ZoneGround.WATER or ZoneGround.WALL);

    /// <summary>
    /// How open and busy-looking the place is: how many path cells are around it. A square or a wide street scores
    /// high, a narrow lane or the edge of the grass low; players gather in the open places.
    /// </summary>
    internal static int Openness(string zonePath, NVector3 point)
        => Around(zonePath, point, 4, ZoneGround.PATH) + Around(zonePath, point, 4, ZoneGround.WOOD);

    private static int Around(string zonePath, NVector3 point, int ring, byte kind) {
        if (Cell(zonePath, point, out var ground) is not { } at) {
            return 0;
        }

        var (x, y) = (at % ground.Width, at / ground.Width);
        var count = 0;
        for (var dy = -ring; dy <= ring; dy++) {
            for (var dx = -ring; dx <= ring; dx++) {
                var (nx, ny) = (x + dx, y + dy);
                if (nx >= 0 && ny >= 0 && nx < ground.Width && ny < ground.Height && ground.Kinds[(ny * ground.Width) + nx] == kind) {
                    count++;
                }
            }
        }

        return count;
    }

    private static int? Cell(string zonePath, NVector3 point, out ZoneGround.WalkGround ground) {
        ground = ZoneGround.WalkOf(zonePath);
        if (ground is null) {
            return null;
        }

        var gx = (int) MathF.Floor((point.X - ground.Left) / ground.Cell);
        var gy = (int) MathF.Floor((ground.Top - point.Y) / ground.Cell);
        if (gx < 0 || gy < 0 || gx >= ground.Width || gy >= ground.Height) {
            return null;
        }

        return (gy * ground.Width) + gx;
    }

    // Whether the street marks hardly any path (an indoor-like place or one of plain grass).
    private static bool FewPaths(string zonePath, ZoneGround.WalkGround ground) => s_fewPaths.GetOrAdd(zonePath, _ => {
        var (paths, walkable) = (0, 0);
        foreach (var kind in ground.Kinds) {
            if (kind is ZoneGround.PATH or ZoneGround.PATH_OVER or ZoneGround.WOOD) {
                paths++;
            }

            if (kind is not (ZoneGround.WATER or ZoneGround.WALL or ZoneGround.NONE)) {
                walkable++;
            }
        }

        return walkable == 0 || paths < walkable / 20;
    });

}
