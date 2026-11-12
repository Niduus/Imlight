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
 *
 * PURPOSE:
 * Every teleport to a spot (x, y, z) checks that spot before the wizard lands there (Niduus 2026-11-12: "TP and got
 * stuck. Make sure placement checks of the character are working"; the Quest Team TP button put him inside a table of
 * books in the Headmistress's house). A spot is good when, in the zone's own collision file (read only):
 *   - no box or cylinder (wall, fountain, lamp post, furniture) is within a wizard's width of it,
 *   - there is floor under it (water is fine: he can walk out of a pond, as the Commons' pond is walked through),
 *   - and no surface (a table top, a shelf, a bench) sits between his feet and his head anywhere under his width.
 * A bad spot moves to the nearest good one around it on the same floor, then to the nearest good point of the zone's
 * walk grid, and with nothing good at all the wizard goes to the zone's start. [World] SafeLanding turns it off.
 */

using System;
using System.Numerics;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class SafeLanding {

    // How far from a wall or piece of furniture a wizard's middle must be (he is about this wide each way).
    private const float ROOM = 30f;

    // A surface higher than this over his feet and lower than his head is something he would stand inside.
    private const float STEP_UP = 22f;
    private const float HEAD = 150f;

    // Walk-ground cells no wider than this are fine enough to see furniture and ledges by their height.
    private const float FINE_CELL = 16f;

    // How far round the wanted spot to look for a good one, and in how fine rings.
    private const float SEARCH_REACH = 360f;
    private const float RING = 20f;

    // A spot on another floor (a balcony over the room, the room under a stair) does not count.
    private const float SAME_FLOOR = 60f;

    // The walk grid's points are tried up to this far away.
    private const float NAV_REACH = 1500f;

    // How high over the floor the wizard lands, so he drops onto it rather than into it.
    private const float LIFT = 5f;

    /// <summary>[World] SafeLanding (on unless set false), read every time so it applies live.</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("World.SafeLanding", true);

    /// <summary>What the check found: the spot to use, and whether (and why) it moved.</summary>
    internal readonly record struct Result(Vector3 Spot, bool Moved, bool ToStart, string Why);

    /// <summary>
    /// Whether a wizard can stand at <paramref name="feet"/>: null when he can, else the reason he can't (for the log).
    /// A zone with no collision file has nothing to check and every spot is good.
    /// </summary>
    internal static string? Problem(string zonePath, Vector3 feet) {
        if (ZoneCollision.Clearance(zonePath, feet with { Z = feet.Z + LIFT }) < ROOM) {
            return "inside or against a wall or object";
        }

        var ground = ZoneGround.WalkOf(zonePath);
        if (ground is null) {
            return null;
        }

        if (CellOf(ground, feet.X, feet.Y) is not { } middle) {
            return "off the edge of the zone";
        }

        var kind = ground.Kinds[middle];
        if (kind == ZoneGround.NONE || float.IsNaN(ground.Heights[middle])) {
            return "no floor under it";
        }

        // Everything under his width: no wall, and no surface between his feet and his head. Only cells whose middle is
        // under him count (an outdoor street's cells are about 70 wide, an indoor room's about 8). The surface check needs
        // the finer cells of a room: a wide cell's highest ground can be a step or a slope beside him.
        var reach = (int) MathF.Ceiling(ROOM / ground.Cell);
        var (cx, cy) = (middle % ground.Width, middle / ground.Width);
        var fine = ground.Cell <= FINE_CELL;
        for (var dy = -reach; dy <= reach; dy++) {
            for (var dx = -reach; dx <= reach; dx++) {
                var (x, y) = (cx + dx, cy + dy);
                if (x < 0 || y < 0 || x >= ground.Width || y >= ground.Height) {
                    continue;
                }

                var (mx, my) = (ground.Left + ((x + 0.5f) * ground.Cell), ground.Top - ((y + 0.5f) * ground.Cell));
                if ((dx != 0 || dy != 0) && (((mx - feet.X) * (mx - feet.X)) + ((my - feet.Y) * (my - feet.Y))) > ROOM * ROOM) {
                    continue;
                }

                var at = (y * ground.Width) + x;
                if (ground.Kinds[at] == ZoneGround.WALL) {
                    return "against a wall";
                }

                var high = ground.Heights[at];
                if (fine && !float.IsNaN(high) && high > feet.Z + STEP_UP && high < feet.Z + HEAD) {
                    return "inside furniture or a ledge";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The spot to land at for a wanted spot: the wanted one when it is good, else the nearest good one on the same
    /// floor, else the nearest good walk point, else null (then the caller sends the wizard to the zone's start).
    /// </summary>
    internal static Result? Check(string zonePath, Vector3 wanted) {
        if (string.IsNullOrEmpty(zonePath)) {
            return new Result(wanted, false, false, "");
        }

        // The floor under the wanted spot, so a spot given a little in the air (or the floor's own height) still counts.
        var feet = OnFloor(zonePath, wanted.X, wanted.Y, wanted.Z) is { } floor ? wanted with { Z = floor } : wanted;
        if (Problem(zonePath, feet) is not { } why) {
            return new Result(wanted, false, false, "");
        }

        // Rings round the spot, nearest first, on the same floor.
        for (var radius = RING; radius <= SEARCH_REACH; radius += RING) {
            var steps = Math.Max(12, (int) (MathF.Tau * radius / RING));
            for (var i = 0; i < steps; i++) {
                var angle = i * MathF.Tau / steps;
                var (x, y) = (wanted.X + (MathF.Cos(angle) * radius), wanted.Y + (MathF.Sin(angle) * radius));
                if (OnFloor(zonePath, x, y, feet.Z) is not { } z) {
                    continue;
                }

                var spot = new Vector3(x, y, z);
                if (Problem(zonePath, spot) is null) {
                    return new Result(spot with { Z = z + LIFT }, true, false, why);
                }
            }
        }

        // The zone's walk grid: the nearest good point near that floor.
        Vector3? best = null;
        var bestDistance = NAV_REACH * NAV_REACH;
        foreach (var point in ZoneNav.Points(zonePath)) {
            var (dx, dy) = (point.X - wanted.X, point.Y - wanted.Y);
            var distance = (dx * dx) + (dy * dy) + (MathF.Abs(point.Z - feet.Z) > SAME_FLOOR ? NAV_REACH * NAV_REACH / 4 : 0);
            if (distance < bestDistance && Problem(zonePath, point) is null) {
                (best, bestDistance) = (point, distance);
            }
        }

        if (best is { } nav) {
            return new Result(nav with { Z = nav.Z + LIFT }, true, false, why);
        }

        return null;
    }

    // The floor height at (x, y) nearest to z (of the cell's highest and lowest ground) when it is within SAME_FLOOR
    // of z; null for no floor known there or only one on another level. With no walk ground at all, z itself.
    private static float? OnFloor(string zonePath, float x, float y, float z) {
        var ground = ZoneGround.WalkOf(zonePath);
        if (ground is null) {
            return z;
        }

        if (CellOf(ground, x, y) is not { } at) {
            return null;
        }

        var (high, low) = (ground.Heights[at], ground.Lows[at]);
        float? best = null;
        foreach (var h in new[] { high, low }) {
            if (!float.IsNaN(h) && MathF.Abs(h - z) <= SAME_FLOOR && (best is null || MathF.Abs(h - z) < MathF.Abs(best.Value - z))) {
                best = h;
            }
        }

        return best;
    }

    private static int? CellOf(ZoneGround.WalkGround ground, float x, float y) {
        var gx = (int) MathF.Floor((x - ground.Left) / ground.Cell);
        var gy = (int) MathF.Floor((ground.Top - y) / ground.Cell);
        if (gx < 0 || gy < 0 || gx >= ground.Width || gy >= ground.Height) {
            return null;
        }

        return (gy * ground.Width) + gx;
    }

}
