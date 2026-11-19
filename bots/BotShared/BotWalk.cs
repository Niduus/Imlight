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
 */

using System;
using System.Collections.Generic;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// How a server-played wizard (the Quest Team and Ultimus) walks (Niduus, 2026-11-13: "don't seem to be using their full
/// movement speed. IF they are, it's a choppy rendition instead of a smooth one"): at a player's running pace, a small
/// step ten times a second as a player's game sends its moves, the step measured by the time that really passed, and a
/// corner of the route taken without stopping on it.
/// </summary>
internal static class BotWalk {

    /// <summary>How often a walking wizard sends where it is.</summary>
    internal const double TICK_SECONDS = 0.1;

    /// <summary>A player's running pace in game units a second ([Bots] RunSpeed, live; 400 by default).</summary>
    internal static float RunSpeed => Math.Clamp(ConfigurationManager.GetValue("Bots.RunSpeed", 400), 150, 1200);

    /// <summary>The longest step after a slow tick, so a busy server never makes a wizard jump.</summary>
    private const double MOST_SECONDS = 0.3;

    /// <summary>
    /// One step along <paramref name="route"/> from (<paramref name="x"/>, <paramref name="y"/>) for the time since the last
    /// one: where it stands after it, and its yaw (it faces the way it walks). Points reached are taken off the route; the
    /// rest of the step goes on toward the next one.
    /// </summary>
    internal static (float X, float Y, float Yaw) Step(Queue<(float X, float Y)> route, float x, float y, double seconds, float speed) {
        var stride = speed * (float) Math.Clamp(seconds, 0.01, MOST_SECONDS);
        var (startX, startY) = (x, y);
        (float X, float Y) face = route.Count > 0 ? route.Peek() : (x, y);
        while (route.Count > 0 && stride > 0) {
            var next = route.Peek();
            face = next;
            var left = Far(x, y, next.X, next.Y);
            if (left <= stride) {
                (x, y) = next;
                stride -= left;
                route.Dequeue();
                continue;
            }

            x += (next.X - x) / left * stride;
            y += (next.Y - y) / left * stride;
            stride = 0;
        }

        // Facing where it walks: a wizard with yaw w faces (-sin w, -cos w) (Niduus, 2026-11-12: they were "shuffling sideways").
        var (dx, dy) = Far(startX, startY, x, y) > 1 ? (x - startX, y - startY) : (face.X - x, face.Y - y);
        var yaw = MathF.Atan2(-dx, -dy);
        if (yaw < 0) {
            yaw += MathF.Tau;
        }

        return (x, y, yaw);
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

}
