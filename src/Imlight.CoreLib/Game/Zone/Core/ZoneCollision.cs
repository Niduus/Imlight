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
 * ZONE COLLISION
 * ========================================================================
 *
 * PURPOSE:
 * The solid boxes and cylinders of a zone (walls, fountains, lamp posts), from collision.bcd in the zone's .wad, read
 * only. A teleport checks its landing spot against them so the wizard never lands inside one (Niduus 2026-11-02:
 * Firecat Alley's teleporters put him in a wall and in the Wormguts Tower fountain).
 *
 * The file: u32 shape count; per shape u32 type, u32 category, u32 collide flags; a mesh (type 6) then has u32 point
 * count, u32 face count, the points (3 floats) and faces (3 u32 + 3 floats); then every shape has its name (u32
 * length + text), a 3x3 rotation, its middle (3 floats), scale, material (u32 length + text) and its type again,
 * followed by its size: box 3 floats (full lengths), ray 2, sphere 1 (radius), cylinder and tube 2 (length, radius),
 * plane 4, mesh none. Meshes are the ground and are not needed here.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class ZoneCollision {

    private const string COLLISION_FILE_NAME = "collision.bcd";

    // How tall a wizard stands, for which solids are at his height.
    private const float WIZARD_HEIGHT = 150f;

    private enum ShapeType : uint { Box = 0, Ray = 1, Sphere = 2, Cylinder = 3, Tube = 4, Plane = 5, Mesh = 6 }

    // A solid: its middle, rotation (rows of the 3x3), and half sizes (box: x, y, z; cylinder: radius, radius, half
    // length; sphere: radius in all three).
    private readonly record struct Solid(ShapeType Type, Vector3 Middle, Vector3 Row0, Vector3 Row1, Vector3 Row2, Vector3 Half);

    private static readonly ConcurrentDictionary<string, Solid[]> s_solids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far a wizard standing at <paramref name="feet"/> is from the nearest solid at his height, along the ground;
    /// 0 when inside one, float.MaxValue when none is near or the zone has no collision file.
    /// </summary>
    public static float Clearance(string zonePath, Vector3 feet) {
        var best = float.MaxValue;
        foreach (var solid in Solids(zonePath)) {
            var d = feet - solid.Middle;
            // Into the solid's own axes (the rotation's rows are its axes in the world).
            var local = new Vector3(
                (solid.Row0.X * d.X) + (solid.Row1.X * d.Y) + (solid.Row2.X * d.Z),
                (solid.Row0.Y * d.X) + (solid.Row1.Y * d.Y) + (solid.Row2.Y * d.Z),
                (solid.Row0.Z * d.X) + (solid.Row1.Z * d.Y) + (solid.Row2.Z * d.Z));

            // Only solids his height overlaps.
            if (local.Z >= solid.Half.Z || local.Z <= -solid.Half.Z - WIZARD_HEIGHT) {
                continue;
            }

            float distance;
            if (solid.Type == ShapeType.Box) {
                var outX = MathF.Max(MathF.Abs(local.X) - solid.Half.X, 0f);
                var outY = MathF.Max(MathF.Abs(local.Y) - solid.Half.Y, 0f);
                distance = MathF.Sqrt((outX * outX) + (outY * outY));
            } else {
                distance = MathF.Max(MathF.Sqrt((local.X * local.X) + (local.Y * local.Y)) - solid.Half.X, 0f);
            }

            best = MathF.Min(best, distance);
        }

        return best;
    }

    private static Solid[] Solids(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return [];
        }

        return s_solids.GetOrAdd(zonePath, Load);
    }

    private static Solid[] Load(string zonePath) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad) || wad.OpenFile(COLLISION_FILE_NAME) is not { } memory) {
                return [];
            }

            return Parse(memory.ToArray());
        } catch (Exception ex) {
            Logger.Debug("Zone {0}: could not read its collision file ({1}).", Logger.Args(zonePath, ex.Message));

            return [];
        }
    }

    private static Solid[] Parse(byte[] data) {
        var solids = new List<Solid>();
        var at = 0;
        uint U() { var v = BitConverter.ToUInt32(data, at); at += 4; return v; }
        float F() { var v = BitConverter.ToSingle(data, at); at += 4; return v; }
        Vector3 V() => new(F(), F(), F());
        void SkipText() { var length = (int) U(); at += length; }

        var count = U();
        for (var i = 0; i < count; i++) {
            var type = (ShapeType) U();
            U(); // category
            U(); // collide flags
            if (type == ShapeType.Mesh) {
                var points = (int) U();
                var faces = (int) U();
                at += (points * 12) + (faces * 24);
            }

            SkipText(); // name
            var (row0, row1, row2) = (V(), V(), V());
            var middle = V();
            var scale = F();
            SkipText(); // material
            var sizeType = (ShapeType) U();
            switch (sizeType) {
                case ShapeType.Box: {
                    var size = V() * (scale / 2f);
                    solids.Add(new Solid(ShapeType.Box, middle, row0, row1, row2, size));

                    break;
                }

                case ShapeType.Sphere: {
                    var radius = F() * scale;
                    solids.Add(new Solid(ShapeType.Cylinder, middle, row0, row1, row2, new Vector3(radius, radius, radius)));

                    break;
                }

                case ShapeType.Cylinder or ShapeType.Tube: {
                    // Length first, then radius, in the game's files (Niduus 2026-11-12, the Headmistress's house: its
                    // pillars are 318 long and 55 wide; read the other way round they were 318-wide discs no taller
                    // than a step, so a teleport could land inside a pillar).
                    var length = F() * scale;
                    var radius = F() * scale;
                    solids.Add(new Solid(ShapeType.Cylinder, middle, row0, row1, row2, new Vector3(radius, radius, length / 2f)));

                    break;
                }

                case ShapeType.Ray:
                    at += 8;

                    break;
                case ShapeType.Plane:
                    at += 16;

                    break;
                case ShapeType.Mesh:
                    break;
                default:
                    // An unknown shape: what was read so far is still good.
                    return [.. solids];
            }
        }

        return [.. solids];
    }

}
