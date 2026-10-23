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
 * HOUSING SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * One object placed in a house, as a plain record: template, position, angle, scale and look. The
 * client's blob entry is built from it only when a blob is sent.
 *
 * USAGE EXAMPLE:
 * var item = new PlacedItem { TemplateId = id, X = x, Y = y, Z = z, Yaw = yaw }.Normalize();
 * HousingBlobObject entry = item.ToBlobObject();
 *
 * NOTE:
 * - Normalize quantises every value to what the client can show, so what is stored is what every
 *   client shows after a relog. It returns null for a value the client format cannot hold.
 * - The yaw of an exact angle is stored in the middle of its whole degree, because the client
 *   truncates the angle to whole degrees.
 * - A remap index of 0 is a set index, so the remap is null when it is not used.
 * - Custom is the custom data of a music player or a teleporter, the bytes of the client's HousingBlobCustomObject,
 *   kept in base64. An item with custom data is sent as a custom blob entry.
 * - A vault, shed or poster keeps what it holds in Vault; its blob entry is built by Game/Housing/HouseVaults.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// An object placed in a house.
/// </summary>
public sealed record PlacedItem {

    public const int MaxCoordinate = 0x7FFF;
    public const float MinScale = 0.10f;
    public const float MaxScale = 4.0f;
    public const int MaxBrightness = 100;
    public const int MaxTint = 15;
    public const int MaxRemapIndex = 15;

    private const uint TemplateIdMask = 0x0FFFFFFF;
    private const uint RemapFlag = 0x20000000;
    private const uint ExtendedYawFlag = 0x80000000;
    private const uint ExtendedYawOver255Flag = 0x40000000;
    private const uint TintShift = 3;
    private const uint ScaleShift = 8;
    private const uint ExtendedYawShift = 16;
    private const uint ExtendedYawMask = 0xFF;
    private const uint BrightnessShift = 24;
    private const uint BrightnessMask = 0x7F;
    private const uint RemapMask = 0xF;
    private const uint TileRemapShift = 4;
    private const uint NegativeCoordinateBit = 0x8000;
    private const byte FirstFineScaleCode = 0x1A;
    private const byte FirstQuarterScaleCode = 0x1D;
    private const byte LastQuarterScaleCode = 0x2C;
    private const int DegreesPerSector = 45;
    private const int DegreesPerTurn = 360;
    private const float TwoPi = 6.2831855f;
    private const float Pi = 3.1415927f;
    private const float EighthTurn = 0.7853982f;

    // The sector codes are not in compass order: the code of 45 degrees is 4, of 90 degrees 1, and so on.
    private static readonly byte[] s_sectorByDirection = [0, 4, 1, 5, 2, 6, 3, 7];
    private static readonly float[] s_sectorThresholds = [
        0.3926991f, 1.1780972f, 1.9634954f, 2.7488935f, 3.534292f, 4.3196898f, 5.105088f, 5.8904867f
    ];
    private static readonly (byte Code, float Value)[] s_scaleGrid = BuildScaleGrid();

    /// <summary>
    /// The id of the item inside its house. It is never reused, so it also orders the items.
    /// </summary>
    public uint Id { get; init; }

    /// <summary>
    /// The global id of the backpack item this was placed from, or 0 for an object a layout placed.
    /// </summary>
    public ulong ItemGid { get; init; }

    public uint TemplateId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    /// <summary>
    /// The angle in radians.
    /// </summary>
    public float Yaw { get; init; }

    /// <summary>
    /// Whether the angle is kept in whole degrees instead of the eight compass directions.
    /// </summary>
    public bool ExactYaw { get; init; }

    public float Scale { get; init; } = 1.0f;
    public int Brightness { get; init; } = MaxBrightness;
    public int Tint { get; init; }
    public int? WallpaperRemap { get; init; }
    public int? TileRemap { get; init; }

    /// <summary>
    /// The custom data of the blob entry in base64, or <c>null</c> for a plain entry.
    /// </summary>
    public string Custom { get; init; }

    /// <summary>
    /// Encodes the bytes of a custom blob entry for <see cref="Custom"/>.
    /// </summary>
    /// <param name="data">The bytes the client reads.</param>
    /// <returns>The base64 text, or <c>null</c> for no bytes.</returns>
    public static string EncodeCustom(byte[] data)
        => data is null || data.Length == 0 ? null : Convert.ToBase64String(data);

    /// <summary>
    /// Decodes the custom data of a blob entry.
    /// </summary>
    /// <param name="custom">The stored <see cref="Custom"/> text.</param>
    /// <returns>The bytes the client reads; empty when there are none or the text is not base64.</returns>
    public static byte[] DecodeCustom(string custom) {
        if (string.IsNullOrEmpty(custom)) {
            return [];
        }

        try {
            return Convert.FromBase64String(custom);
        } catch (FormatException) {
            return [];
        }
    }

    /// <summary>
    /// What a vault, shed or poster holds, or <c>null</c> for an object that holds nothing yet.
    /// </summary>
    public HouseVault Vault { get; init; }

    /// <summary>
    /// Creates the placed item of a layout object.
    /// </summary>
    /// <param name="layoutObject">The object of a <c>HousingLayout</c>.</param>
    /// <returns>The normalized item without an id, or <c>null</c> when a value cannot be stored.</returns>
    public static PlacedItem FromLayout(HousingLayoutObject layoutObject)
        => layoutObject is null
            ? null
            : new PlacedItem {
                TemplateId = layoutObject.m_templateID,
                X = layoutObject.m_positionX,
                Y = layoutObject.m_positionY,
                Z = layoutObject.m_positionZ,
                Yaw = layoutObject.m_yaw,
            }.Normalize();

    /// <summary>
    /// Quantises the values to what the client can show.
    /// </summary>
    /// <returns>
    /// The normalized copy, or <c>null</c> when the template id is 0 or too large, a value is not finite, a coordinate
    /// or the height is beyond the client's range, or the scale, brightness, tint or a remap index is out of range.
    /// </returns>
    public PlacedItem Normalize() {
        if (TemplateId == 0 || TemplateId > TemplateIdMask || !TryRoundCoordinate(X, out var x)
            || !TryRoundCoordinate(Y, out var y) || !float.IsFinite(Z) || MathF.Abs(Z) > MaxCoordinate
            || !float.IsFinite(Yaw) || !float.IsFinite(Scale) || Scale < MinScale || Scale > MaxScale
            || Brightness is < 0 or > MaxBrightness || Tint is < 0 or > MaxTint
            || WallpaperRemap is < 0 or > MaxRemapIndex || TileRemap is < 0 or > MaxRemapIndex) {
            return null;
        }

        var exactYaw = ExactYaw;
        float yaw;
        var degrees = exactYaw ? ClientDegrees(Yaw) : 0;
        if (exactYaw && degrees % DegreesPerSector != 0) {
            yaw = (degrees + 0.5f) * Pi / 180f;
        } else {
            var direction = exactYaw ? degrees / DegreesPerSector : DirectionOf(Yaw);
            yaw = direction * EighthTurn;
            exactYaw = false;
        }

        return this with { X = x, Y = y, Yaw = yaw, ExactYaw = exactYaw, Scale = SnapScale(Scale) };
    }

    /// <summary>
    /// Packs the item into the client's blob entry. The item should be normalized.
    /// </summary>
    /// <returns>The blob entry; a <see cref="HousingBlobCustomObject"/> when the item carries custom data.</returns>
    public HousingBlobObject ToBlobObject() {
        var template = TemplateId & TemplateIdMask;
        var extra = 0u;

        var degrees = ExactYaw ? ClientDegrees(Yaw) : 0;
        if (ExactYaw && degrees % DegreesPerSector != 0) {
            template |= ExtendedYawFlag;
            if (degrees > byte.MaxValue) {
                template |= ExtendedYawOver255Flag;
            }

            extra |= ((uint) degrees & ExtendedYawMask) << (int) ExtendedYawShift;
        }

        if (WallpaperRemap is not null || TileRemap is not null) {
            template |= RemapFlag;
            extra |= ((uint) (WallpaperRemap ?? 0) & RemapMask) | (((uint) (TileRemap ?? 0) & RemapMask) << (int) TileRemapShift);
        }

        extra |= (uint) ScaleCode(Scale) << (int) ScaleShift;
        if (Brightness != MaxBrightness) {
            extra |= (((uint) Brightness + 1) & BrightnessMask) << (int) BrightnessShift;
        }

        var positionXY = (PackCoordinate(X) << 16) | PackCoordinate(Y);
        var yaw = (byte) (((uint) Tint << (int) TintShift) | SectorCode(Yaw));
        if (Custom is not null) {
            return new HousingBlobCustomObject {
                m_gameObjectTemplateID = template,
                m_positionXY = positionXY,
                m_positionZ = Z,
                m_yaw = yaw,
                m_extraData1 = extra,
                m_customData = DecodeCustom(Custom),
            };
        }

        return new HousingBlobObject {
            m_gameObjectTemplateID = template,
            m_positionXY = positionXY,
            m_positionZ = Z,
            m_yaw = yaw,
            m_extraData1 = extra,
        };
    }

    private static bool TryRoundCoordinate(float value, out float rounded) {
        rounded = 0;
        if (!float.IsFinite(value) || MathF.Abs(value) > MaxCoordinate + 1) {
            return false;
        }

        var magnitude = value >= 0 ? (int) (value + 0.5f) : (int) (0.5f - value);
        if (magnitude > MaxCoordinate) {
            return false;
        }

        rounded = value >= 0 ? magnitude : -magnitude;

        return true;
    }

    private static uint PackCoordinate(float value)
        => value >= 0 ? (uint) (value + 0.5f) & MaxCoordinate : NegativeCoordinateBit + ((uint) (0.5f - value) & MaxCoordinate);

    private static float ReduceYaw(float yaw) {
        var reduced = yaw - (float) (int) (yaw / TwoPi) * TwoPi;

        return reduced < 0 ? reduced + TwoPi : reduced;
    }

    private static int ClientDegrees(float yaw)
        => (int) (ReduceYaw(yaw) * 180f / Pi) % DegreesPerTurn;

    private static int DirectionOf(float yaw) {
        var reduced = ReduceYaw(yaw);
        for (var direction = 0; direction < s_sectorThresholds.Length; direction++) {
            if (reduced < s_sectorThresholds[direction]) {
                return direction;
            }
        }

        return 0;
    }

    private static byte SectorCode(float yaw) => s_sectorByDirection[DirectionOf(yaw)];

    private static float SnapScale(float scale) => NearestScale(scale).Value;

    private static byte ScaleCode(float scale) => NearestScale(scale).Code;

    private static (byte Code, float Value) NearestScale(float scale) {
        var best = s_scaleGrid[0];
        foreach (var step in s_scaleGrid) {
            if (MathF.Abs(step.Value - scale) < MathF.Abs(best.Value - scale)) {
                best = step;
            }
        }

        return best;
    }

    private static (byte Code, float Value)[] BuildScaleGrid() {
        var grid = new List<(byte, float)> {
            (0, 1.0f),
            (FirstFineScaleCode, 0.10f),
            (FirstFineScaleCode + 1, 0.15f),
            (FirstFineScaleCode + 2, 0.20f),
        };
        for (var code = FirstQuarterScaleCode; code <= LastQuarterScaleCode; code++) {
            grid.Add(((byte) code, 0.25f * (code - FirstQuarterScaleCode + 1)));
        }

        return [.. grid];
    }

}
