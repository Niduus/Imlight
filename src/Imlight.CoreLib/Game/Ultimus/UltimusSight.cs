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
using System.Numerics;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Core;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's line of sight circle (Niduus, 2026-11-13: "project a Line of Sight circle around him so if his objective is
/// nearby but his pathing doesn't allow him to move freely, this would override and allow him to continue anyways"). A goal
/// inside the circle with nothing in the way (no wall in the area's collision, no climb up a ledge) is walked to in a straight
/// line, whatever the route map says. Blocked, a short way round inside the circle is looked for. As a last resort, when it
/// is stuck by its goal, a small hop to a clear spot right by the goal (the safe landing check, and only a spot it can see
/// the goal from, so never through a wall into another room). [Ultimus] SightRadius sets the circle's size, live.
/// </summary>
internal static class UltimusSight {

    /// <summary>The circle's radius ([Ultimus] SightRadius, 480 by default, about 40 feet; 0 turns it off). Read every time.</summary>
    internal static float Radius => Math.Clamp(ConfigurationManager.GetValue("Ultimus.SightRadius", 480f), 0f, 3000f);

    // A rise this high between two steps of the line is a ledge it can't walk up.
    private const float CLIMB = 45f;

    // A wizard needs this much room round him for the line to count as open.
    private const float BODY = 12f;

    /// <summary>Whether the goal is in the circle.</summary>
    internal static bool InSight(float fromX, float fromY, float toX, float toY) {
        var radius = Radius;

        return radius > 0 && Far(fromX, fromY, toX, toY) <= radius;
    }

    /// <summary>Whether nothing is in the way on a straight line: no wall, no ledge to climb, no gap with no floor.</summary>
    internal static bool Clear(string zonePath, float fromX, float fromY, float toX, float toY) {
        try {
            var ground = ZoneGround.WalkOf(zonePath);
            if (ground is null) {
                return false;
            }

            var length = Far(fromX, fromY, toX, toY);
            var step = Math.Clamp(ground.Cell / 2, 6f, 30f);
            var count = Math.Max(1, (int) MathF.Ceiling(length / step));
            float? last = null;
            for (var i = 1; i <= count; i++) {
                var t = i / (float) count;
                var (x, y) = (fromX + ((toX - fromX) * t), fromY + ((toY - fromY) * t));
                var cx = (int) MathF.Floor((x - ground.Left) / ground.Cell);
                var cy = (int) MathF.Floor((ground.Top - y) / ground.Cell);
                if (cx < 0 || cy < 0 || cx >= ground.Width || cy >= ground.Height) {
                    return false;
                }

                var at = (cy * ground.Width) + cx;
                var height = ground.Heights?[at] ?? float.NaN;
                if (ground.Kinds[at] == ZoneGround.WALL || ground.Kinds[at] == ZoneGround.NONE && float.IsNaN(height)) {
                    return false;
                }

                if (!float.IsNaN(height)) {
                    if (last is { } before && height - before > CLIMB) {
                        return false;
                    }

                    // Walls and objects the ground grid is too coarse to show (a pillar, a fence).
                    if (i < count && ZoneCollision.Clearance(zonePath, new Vector3(x, y, height + 5f)) < BODY) {
                        return false;
                    }

                    last = height;
                }
            }

            return true;
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: line of sight in {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));

            return false;
        }
    }

    /// <summary>A short way round what blocks the straight line, inside the circle: one turn to the side, or null.</summary>
    internal static List<(float X, float Y)> AroundTo(string zonePath, float fromX, float fromY, float toX, float toY) {
        var (dx, dy) = (toX - fromX, toY - fromY);
        var length = MathF.Max(1, MathF.Sqrt((dx * dx) + (dy * dy)));
        var (nx, ny) = (-dy / length, dx / length);
        var radius = Radius;
        foreach (var along in new[] { 0.5f, 0.3f, 0.7f }) {
            foreach (var side in new[] { 120f, -120f, 220f, -220f, 340f, -340f }) {
                var (wx, wy) = (fromX + (dx * along) + (nx * side), fromY + (dy * along) + (ny * side));
                if (Far(fromX, fromY, wx, wy) > radius) {
                    continue;
                }

                if (Clear(zonePath, fromX, fromY, wx, wy) && Clear(zonePath, wx, wy, toX, toY)) {
                    return [(wx, wy), (toX, toY)];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A clear spot right by the goal to hop to: the safe landing check's spot, and only one that sees the goal, so never in
    /// another room. Null when there is none.
    /// </summary>
    internal static (float X, float Y, float Z)? HopBy(string zonePath, float toX, float toY, float z) {
        try {
            if (SafeLanding.Check(zonePath, new Vector3(toX - 40, toY, z)) is not { } landing) {
                return null;
            }

            var spot = landing.Spot;
            if (Far(spot.X, spot.Y, toX, toY) > 200 || !Clear(zonePath, spot.X, spot.Y, toX, toY) && Far(spot.X, spot.Y, toX, toY) > 60) {
                return null;
            }

            return (spot.X, spot.Y, spot.Z);
        }
        catch (Exception) {
            return null;
        }
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}
