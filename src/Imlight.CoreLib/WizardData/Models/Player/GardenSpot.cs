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
 * One entry of a house's garden: a patch of soil or a plant, with the times the server grows it by.
 *
 * USAGE EXAMPLE:
 * var plant = new GardenSpot { X = 120, Y = -40, Z = 0.5f, TemplateId = seedlingId, StageStart = now, NextStage = now + 3600 };
 *
 * NOTE:
 * - X and Y are whole numbers, as the client keeps them; the client finds an entry by its exact position.
 * - Flags are the client's GardenPlant flags (Game/Gardening/GardenFlags). Soil has flag 1 and no times.
 * - Every time is Unix seconds. 0 means "not scheduled".
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// A patch of soil or a plant in a house's garden.
/// </summary>
public sealed record GardenSpot {

    public int X { get; init; }
    public int Y { get; init; }
    public float Z { get; init; }

    /// <summary>The soil's template, or the template of the plant's current stage.</summary>
    public uint TemplateId { get; init; }

    public uint Flags { get; init; }

    /// <summary>The seed item the plant grew from, so its harvest can give seeds back.</summary>
    public uint SeedTemplateId { get; init; }

    public long StageStart { get; init; }
    public long NextStage { get; init; }

    /// <summary>When a mature or elder plant is ready to harvest next.</summary>
    public long HarvestAt { get; init; }

    public uint HarvestCount { get; init; }

    // When each need shows up next. A need that has shown up keeps its flag until a spell meets it.
    public long WaterAt { get; init; }
    public long SunAt { get; init; }
    public long PollinationAt { get; init; }
    public long MagicAt { get; init; }
    public long MusicAt { get; init; }

    /// <summary>When the plant wilts unless its needs are met (or its pest is gone) first.</summary>
    public long WiltAt { get; init; }

    public uint PestTemplateId { get; init; }
    public long PestCheckAt { get; init; }

    public uint ProtectionTemplateId { get; init; }
    public long ProtectionUntil { get; init; }

    /// <summary>When the server last grew the plant. A plant with a need or a pest does not grow in the meantime.</summary>
    public long CheckedAt { get; init; }

    /// <summary>Whether this is a patch of soil rather than a plant.</summary>
    public bool IsSoil() => (Flags & 1) != 0;

}
