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
 * HATCHMAKING LISTINGS
 * ========================================================================
 *
 * PURPOSE:
 * The pets players put up at the hatchmaking kiosk, for anyone (the owner too, so a player alone can
 * hatch two of their own pets) to hatch with. Kept in hatchmaking-listings.json next to the server.
 *
 * USAGE EXAMPLE:
 * HatchmakingListings.Add(new HatchmakingListing { Parent = PetHatching.Snapshot(pet, wizard.CharId) });
 *
 * NOTE:
 * A listing holds a copy of the pet (PetParent and the item as the game reads it), so the owner can be
 * offline when someone hatches with it. Listing again refreshes the copy.
 *
 * TODO:
 * - Live listings age out and have a cooldown after each hatch; these stay until removed.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// A pet put up at the hatchmaking kiosk.
/// </summary>
internal sealed class HatchmakingListing {
    public PetParent Parent { get; set; }
    public uint NameKeys { get; set; }
    public long ListedAt { get; set; }
    public uint HatchCount { get; set; }
    /// <summary>The pet item as the game reads it (base64), sent when someone looks at the pet.</summary>
    public string ItemData { get; set; }
}

internal static class HatchmakingListings {

    private const string FILE = "hatchmaking-listings.json";

    private static readonly object s_lock = new();
    private static List<HatchmakingListing> s_listings;

    internal static List<HatchmakingListing> All() {
        lock (s_lock) {
            return [.. Load()];
        }
    }

    internal static List<HatchmakingListing> Of(ulong ownerCharId) {
        lock (s_lock) {
            return [.. Load().Where(l => l.Parent?.OwnerCharId == ownerCharId)];
        }
    }

    internal static HatchmakingListing Find(ulong petItemId) {
        lock (s_lock) {
            return Load().FirstOrDefault(l => l.Parent?.ItemId == petItemId);
        }
    }

    /// <summary>
    /// Lists a pet, or refreshes its copy if it is listed already.
    /// </summary>
    internal static void Add(HatchmakingListing listing) {
        lock (s_lock) {
            var listings = Load();
            var old = listings.FindIndex(l => l.Parent?.ItemId == listing.Parent.ItemId);
            if (old >= 0) {
                listing.HatchCount = listings[old].HatchCount;
                listing.ListedAt = listings[old].ListedAt;
                listings[old] = listing;
            }
            else {
                listing.ListedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                listings.Add(listing);
            }

            Save();
        }
    }

    internal static bool Remove(ulong petItemId) {
        lock (s_lock) {
            var removed = Load().RemoveAll(l => l.Parent?.ItemId == petItemId) > 0;
            if (removed) {
                Save();
            }

            return removed;
        }
    }

    internal static void CountHatch(ulong petItemId) {
        lock (s_lock) {
            var listing = Load().FirstOrDefault(l => l.Parent?.ItemId == petItemId);
            if (listing is not null) {
                listing.HatchCount++;
                Save();
            }
        }
    }

    private static List<HatchmakingListing> Load() {
        if (s_listings is not null) {
            return s_listings;
        }

        s_listings = [];
        try {
            if (File.Exists(FILE)) {
                s_listings = JsonSerializer.Deserialize<List<HatchmakingListing>>(File.ReadAllText(FILE)) ?? [];
                s_listings.RemoveAll(l => l?.Parent is null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_listings;
    }

    private static void Save() {
        try {
            File.WriteAllText(FILE, JsonSerializer.Serialize(s_listings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
