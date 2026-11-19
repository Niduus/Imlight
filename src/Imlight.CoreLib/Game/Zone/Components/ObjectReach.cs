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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// How close a player must stand to use a large quest object, measured to the object's own shape instead of its
/// center. A quest object is offered ("Repair", "Use") only within 300 of its center, but some are far bigger than
/// that: Zafaria's fallen Zebra Scout Tower (In The Cold Distance, ZF-SAVA-C06-002) lies about 1,200 long, its center
/// at its foot and its solid shape keeping players 300 or more from it everywhere but the foot, so players walked up
/// to it and were never offered "Repair" (MoMi, 2026-11-18, had to finish the step by command). The shape comes from
/// the object's collision file: each box, sphere or cylinder counts as a circle around its middle.
/// </summary>
internal static class ObjectReach {

    /// <summary>How far from the shape's edge a player still reaches the object.</summary>
    internal const float EDGE_REACH = 200f;

    // Only objects that reach further than the usual 300 from their center are worth measuring this way.
    private const float USUAL_REACH = 300f;

    internal readonly record struct Part(float X, float Y, float Radius);

    private static readonly ConcurrentDictionary<string, Part[]> s_partsByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The object's shape, or null when it has none or none that reaches past the usual 300 from its center.
    /// </summary>
    internal static Shape For(GameObjectTemplate template) {
        var collision = template?.m_behaviors?.OfType<CollisionBehaviorTemplate>().FirstOrDefault();
        if (collision is null || collision.m_bDisableCollision) {
            return null;
        }

        var file = (string) collision.m_clickableCollisionFilename;
        if (string.IsNullOrEmpty(file)) {
            file = (string) collision.m_solidCollisionFilename;
        }

        if (string.IsNullOrEmpty(file)) {
            return null;
        }

        var parts = s_partsByFile.GetOrAdd(file, Read);
        if (parts.Length == 0 || parts.All(p => MathF.Sqrt((p.X * p.X) + (p.Y * p.Y)) + p.Radius + EDGE_REACH <= USUAL_REACH)) {
            return null;
        }

        return new Shape(parts);
    }

    private static Part[] Read(string file) {
        try {
            var bytes = RootArchiveLoader.GetRawFile(file);
            if (bytes is null || bytes.Length == 0) {
                return [];
            }

            using var stream = new MemoryStream(bytes);
            var world = XDocument.Load(stream).Root;
            if (world is null) {
                return [];
            }

            var parts = new List<Part>();
            foreach (var primitive in world.Elements("primitive")) {
                var location = primitive.Element("location");
                var x = Number(location, "x");
                var y = Number(location, "y");
                var type = (string) primitive.Attribute("type");
                float radius;
                if (string.Equals(type, "box", StringComparison.OrdinalIgnoreCase)) {
                    var size = primitive.Element("dimensions");
                    var halfLength = Number(size, "l") / 2;
                    var halfWidth = Number(size, "w") / 2;
                    radius = MathF.Sqrt((halfLength * halfLength) + (halfWidth * halfWidth));
                }
                else {
                    radius = Number(primitive.Element("radius"), "value");
                }

                if (radius > 0) {
                    parts.Add(new Part(x, y, radius));
                }
            }

            return [.. parts];
        }
        catch (Exception e) {
            Logger.Debug("Object reach: collision file {0} could not be read: {1}", Logger.Args(file, e.Message));

            return [];
        }
    }

    private static float Number(XElement element, string attribute)
        => element?.Attribute(attribute) is { } value
            && float.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0f;

    /// <summary>An object's shape on the ground, in its own frame.</summary>
    internal sealed class Shape {

        private readonly Part[] _parts;

        internal Shape(Part[] parts) => _parts = parts;

        /// <summary>
        /// True when the player stands within <see cref="EDGE_REACH"/> of the shape's edge. The object's turn (its
        /// orientation Z) is taken the way battle circles take theirs (CombatDuelComponent): a point at angle a in
        /// the object's frame lies at angle a - turn in the world.
        /// </summary>
        internal bool Reaches(Imcodec.Math.Vector3 objectAt, float turn, float scale, Imcodec.Math.Vector3 player) {
            if (scale <= 0) {
                scale = 1;
            }

            var cos = MathF.Cos(turn);
            var sin = MathF.Sin(turn);
            foreach (var part in _parts) {
                var worldX = objectAt.X + (scale * ((part.X * cos) + (part.Y * sin)));
                var worldY = objectAt.Y + (scale * ((part.Y * cos) - (part.X * sin)));
                var dx = player.X - worldX;
                var dy = player.Y - worldY;
                var reach = (part.Radius * scale) + EDGE_REACH;
                if ((dx * dx) + (dy * dy) <= reach * reach) {
                    return true;
                }
            }

            return false;
        }

    }

}
