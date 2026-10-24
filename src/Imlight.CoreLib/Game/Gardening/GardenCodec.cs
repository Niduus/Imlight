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
 *
 * ========================================================================
 * GARDENING
 * ========================================================================
 *
 * PURPOSE:
 * Writes a garden the way the client's GardeningCompression reads it, for the custom data of the gardening proxy.
 *
 * USAGE EXAMPLE:
 * byte[] data = GardenCodec.Write(house.Garden);
 *
 * NOTE:
 * - The client loads the proxy's m_customData with GardeningCompression::Decompress in its lite form
 *   (0x141d9c740, 0x140f32420): little endian, u32 magic 0xDECADE02, u16 version (4), u16 count, then per entry
 *   u32 (X << 16) | Y with each half sign and magnitude (0x8000 + |v| when negative), f32 Z, u32 template, u32 flags,
 *   and for a plant (flag 1 clear) u32 pest template, u32 protection template, u32 next stage time, u32 stage start time
 *   and u32 harvest time. The other times of a plant are the server's own.
 * - The client refuses a garden past 19,900 bytes and will not plow or plant past 19,000, so a garden stays below that.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Gardening;

internal static class GardenCodec {

    private const uint LiteMagic = 0xDECADE02;
    private const ushort Version = 4;
    private const int HeaderSize = 8;
    private const int SoilSize = 16;
    private const int PlantSize = 36;
    private const uint NegativeBit = 0x8000;
    private const uint CoordinateMask = 0x7FFF;

    /// <summary>The most bytes a garden may take: the client plows and plants only below 19,000.</summary>
    internal const int MaxSize = 19000;

    /// <summary>
    /// The bytes a garden takes.
    /// </summary>
    internal static int SizeOf(IEnumerable<GardenSpot> garden) {
        var size = HeaderSize;
        foreach (var spot in garden ?? []) {
            size += spot.IsSoil() ? SoilSize : PlantSize;
        }

        return size;
    }

    /// <summary>
    /// Writes a garden.
    /// </summary>
    /// <param name="garden">The soil and plants.</param>
    /// <returns>The proxy's custom data.</returns>
    internal static byte[] Write(IReadOnlyCollection<GardenSpot> garden) {
        garden ??= [];
        using var stream = new MemoryStream(SizeOf(garden));
        using var writer = new BinaryWriter(stream);
        writer.Write(LiteMagic);
        writer.Write(Version);
        writer.Write((ushort) Math.Min(garden.Count, ushort.MaxValue));
        foreach (var spot in garden) {
            writer.Write((Pack(spot.X) << 16) | Pack(spot.Y));
            writer.Write(spot.Z);
            writer.Write(spot.TemplateId);
            writer.Write(spot.Flags);
            if (spot.IsSoil()) {
                continue;
            }

            writer.Write(spot.PestTemplateId);
            writer.Write(spot.ProtectionTemplateId);
            writer.Write(Seconds(spot.NextStage));
            writer.Write(Seconds(spot.StageStart));
            writer.Write(Seconds(spot.HarvestAt));
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// Rounds a coordinate the way the client stores it.
    /// </summary>
    internal static int Round(float value) {
        if (!float.IsFinite(value)) {
            return 0;
        }

        var magnitude = (int) Math.Min(MathF.Abs(value) + 0.5f, CoordinateMask);

        return value >= 0 ? magnitude : -magnitude;
    }

    private static uint Pack(int value)
        => value >= 0 ? (uint) value & CoordinateMask : NegativeBit + ((uint) -value & CoordinateMask);

    private static uint Seconds(long time)
        => time <= 0 ? 0 : (uint) Math.Min(time, uint.MaxValue);

}
