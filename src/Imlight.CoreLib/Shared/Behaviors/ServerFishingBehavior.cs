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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Shared.Behaviors;

/// <summary>A fish in the basket.</summary>
[Serializable]
public class BasketFish {

    public uint TemplateId { get; set; }
    public float Size { get; set; }

}

/// <summary>Everything caught of one kind of fish, for the fishing journal.</summary>
[Serializable]
public class FishRecord {

    public uint TemplateId { get; set; }
    public uint Caught { get; set; }
    public float Smallest { get; set; }
    public float Largest { get; set; }
    public uint SmallFries { get; set; }
    public uint Whoppers { get; set; }

}

/// <summary>
/// The fish basket and the fishing journal of a wizard, saved with the character (WizardCollection.UpdateCharacterFishingBehavior)
/// and sent as the player object's FishingBehavior.
/// </summary>
public class ServerFishingBehavior {

    public List<BasketFish> Basket { get; set; } = [];
    public List<FishRecord> History { get; set; } = [];

    /// <summary>Puts a fish in the basket and counts it in the journal.</summary>
    public void Add(uint templateId, float size, bool smallFry, bool whopper) {
        Basket ??= [];
        History ??= [];
        Basket.Add(new BasketFish { TemplateId = templateId, Size = size });

        var record = History.FirstOrDefault(r => r.TemplateId == templateId);
        if (record is null) {
            record = new FishRecord { TemplateId = templateId, Smallest = size, Largest = size };
            History.Add(record);
        }

        record.Caught++;
        record.Smallest = Math.Min(record.Smallest, size);
        record.Largest = Math.Max(record.Largest, size);
        record.SmallFries += smallFry ? 1u : 0u;
        record.Whoppers += whopper ? 1u : 0u;
    }

    /// <summary>Takes the fish of this kind closest to this size out of the basket.</summary>
    public BasketFish Take(uint templateId, float size) {
        var fish = (Basket ?? [])
            .Where(f => f.TemplateId == templateId)
            .OrderBy(f => Math.Abs(f.Size - size))
            .FirstOrDefault();
        if (fish is not null) {
            Basket.Remove(fish);
        }

        return fish;
    }

    public FishingBehavior GetClientBehaviorInstance() => new() {
        m_caughtFishList = (Basket ?? []).Select(f => new CaughtFish { m_templateID = f.TemplateId, m_size = f.Size }).ToList(),
        m_fishHistoryList = (History ?? []).Select(r => new FishHistory {
            m_templateID = r.TemplateId,
            m_numberCaught = r.Caught,
            m_smallestSize = r.Smallest,
            m_largestSize = r.Largest,
            m_numberSmallFrysCaught = r.SmallFries,
            m_numberWoppersCaught = r.Whoppers,
        }).ToList(),
    };

}
