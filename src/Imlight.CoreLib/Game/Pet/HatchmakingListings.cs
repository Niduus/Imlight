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
 * offline when someone hatches with it. Listing again refreshes the copy. While the kiosk's mini server
 * (KioskBot, [HatchKiosk] OwnServer) runs, every question goes to it; otherwise the game server reads
 * and writes the same file itself (HatchmakingStore), so nothing is lost when the mini server sleeps.
 *
 * TODO:
 * - Live listings age out and have a cooldown after each hatch; these stay until removed.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/21/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;

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
    /// <summary>The pets that hatched with this one, newest last, for My Hatches (the owner can hatch with them).</summary>
    public List<HatchedPartner> HatchedWith { get; set; } = [];
    /// <summary>What the owner has not been told yet: someone hatched with this pet.</summary>
    public List<KioskNotice> Notices { get; set; } = [];
}

/// <summary>A pet that hatched with a kiosk pet: a copy, so the kiosk pet's owner can hatch with it in turn.</summary>
internal sealed class HatchedPartner {
    public PetParent Parent { get; set; }
    public uint NameKeys { get; set; }
    public string ItemData { get; set; }
}

/// <summary>"Your pet has hatched with ..." for the owner of a kiosk pet, and the Hatch Peppers that come with it.</summary>
internal sealed class KioskNotice {
    public uint MyTemplateId { get; set; }
    public uint MyNameKeys { get; set; }
    public string MyLabel { get; set; }
    public uint TheirTemplateId { get; set; }
    public string TheirLabel { get; set; }
    public int Peppers { get; set; }
}

/// <summary>
/// The kiosk's listings, for the game server and the bots. Asks the kiosk's mini server when it is running (see
/// <see cref="KioskLink"/>), and reads the listings file itself when it is asleep, starting, or turned off.
/// </summary>
internal static class HatchmakingListings {

    /// <summary>
    /// Every pet at the kiosk: the players' first, then the kiosk's own (pet.kiosk, KioskHousePets).
    /// </summary>
    internal static List<HatchmakingListing> All()
        => [.. Players(), .. KioskHousePets.Listings()];

    /// <summary>
    /// Only the pets players put up.
    /// </summary>
    internal static List<HatchmakingListing> Players()
        => KioskLink.Ask(new KioskRequest { Op = "all" }) is { Ok: true } answer ? answer.Listings ?? [] : HatchmakingStore.All();

    /// <summary>
    /// A listing for one of a player's pets: a copy of the pet as the game reads it.
    /// </summary>
    internal static HatchmakingListing For(WizClientObjectItem item, PetParent pet) => new() {
        Parent = pet,
        NameKeys = CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(item, out var name) ? name.m_nameKeys : 0,
        ItemData = ItemSync.TrySerializeForKiosk(item, out var data) ? Convert.ToBase64String(data) : null,
    };

    internal static List<HatchmakingListing> Of(ulong ownerCharId)
        => KioskLink.Ask(new KioskRequest { Op = "of", Owner = ownerCharId }) is { Ok: true } answer
            ? answer.Listings ?? []
            : HatchmakingStore.Of(ownerCharId);

    internal static HatchmakingListing Find(ulong petItemId)
        => KioskHousePets.Find(petItemId) ?? FindPlayers(petItemId);

    private static HatchmakingListing FindPlayers(ulong petItemId)
        => KioskLink.Ask(new KioskRequest { Op = "find", Id = petItemId }) is { Ok: true } answer
            ? answer.Listing
            : HatchmakingStore.Find(petItemId);

    /// <summary>
    /// Lists a pet, or refreshes its copy if it is listed already.
    /// </summary>
    internal static void Add(HatchmakingListing listing) {
        if (KioskLink.Ask(new KioskRequest { Op = "add", Listing = listing }) is not { Ok: true }) {
            HatchmakingStore.Add(listing);
        }
    }

    internal static bool Remove(ulong petItemId)
        => KioskLink.Ask(new KioskRequest { Op = "remove", Id = petItemId }) is { Ok: true } answer
            ? answer.Done
            : HatchmakingStore.Remove(petItemId);

    internal static void CountHatch(ulong petItemId) {
        if (KioskLink.Ask(new KioskRequest { Op = "count", Id = petItemId }) is not { Ok: true }) {
            HatchmakingStore.CountHatch(petItemId);
        }
    }

    /// <summary>
    /// Someone hatched with a kiosk pet: counts it, keeps a copy of their pet for the owner's My Hatches, and leaves the
    /// owner a notice. The owner's character id, or 0 when the pet is no longer at the kiosk.
    /// </summary>
    internal static ulong RecordHatch(ulong petItemId, HatchedPartner partner, KioskNotice notice)
        => KioskLink.Ask(new KioskRequest { Op = "hatched", Id = petItemId, Partner = partner, Notice = notice }) is { Ok: true } answer
            ? answer.Owner
            : HatchmakingStore.RecordHatch(petItemId, partner, notice);

    /// <summary>Takes the notices left for an owner (they are told once).</summary>
    internal static List<KioskNotice> TakeNotices(ulong ownerCharId)
        => KioskLink.Ask(new KioskRequest { Op = "notices", Owner = ownerCharId }) is { Ok: true } answer
            ? answer.Notices ?? []
            : HatchmakingStore.TakeNotices(ownerCharId);

}

/// <summary>
/// The listings file itself, hatchmaking-listings.json next to the server. The kiosk's mini server and the game server
/// both use it (one at a time: the game server only while the mini server is not answering), and each reads the file
/// again when the other one changed it.
/// </summary>
internal static class HatchmakingStore {

    internal const string FILE = "hatchmaking-listings.json";

    private static readonly object s_lock = new();
    private static List<HatchmakingListing> s_listings;
    // The file as this program last read or wrote it, to see when the other program changed it.
    private static DateTime s_fileTime;
    private static long s_fileLength = -1;

    internal static int Count {
        get {
            lock (s_lock) {
                return Load().Count;
            }
        }
    }

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

    internal static void Add(HatchmakingListing listing) {
        if (listing?.Parent is null) {
            return;
        }

        lock (s_lock) {
            var listings = Load();
            var old = listings.FindIndex(l => l.Parent?.ItemId == listing.Parent.ItemId);
            if (old >= 0) {
                listing.HatchCount = listings[old].HatchCount;
                listing.ListedAt = listings[old].ListedAt;
                listing.HatchedWith = listings[old].HatchedWith ?? [];
                listing.Notices = listings[old].Notices ?? [];
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

    // The most partners kept per kiosk pet for My Hatches.
    private const int MAX_PARTNERS = 50;

    internal static ulong RecordHatch(ulong petItemId, HatchedPartner partner, KioskNotice notice) {
        lock (s_lock) {
            var listing = Load().FirstOrDefault(l => l.Parent?.ItemId == petItemId);
            if (listing is null) {
                return 0;
            }

            listing.HatchCount++;
            if (partner?.Parent is not null) {
                listing.HatchedWith ??= [];
                listing.HatchedWith.Add(partner);
                if (listing.HatchedWith.Count > MAX_PARTNERS) {
                    listing.HatchedWith.RemoveRange(0, listing.HatchedWith.Count - MAX_PARTNERS);
                }
            }

            if (notice is not null) {
                notice.MyTemplateId = listing.Parent.TemplateId;
                notice.MyNameKeys = listing.NameKeys;
                listing.Notices ??= [];
                listing.Notices.Add(notice);
            }

            Save();

            return listing.Parent.OwnerCharId;
        }
    }

    internal static List<KioskNotice> TakeNotices(ulong ownerCharId) {
        lock (s_lock) {
            var taken = new List<KioskNotice>();
            foreach (var listing in Load().Where(l => l.Parent?.OwnerCharId == ownerCharId && l.Notices is { Count: > 0 })) {
                taken.AddRange(listing.Notices);
                listing.Notices.Clear();
            }

            if (taken.Count > 0) {
                Save();
            }

            return taken;
        }
    }

    private static List<HatchmakingListing> Load() {
        var (time, length) = FileStamp();
        if (s_listings is not null && time == s_fileTime && length == s_fileLength) {
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

        (s_fileTime, s_fileLength) = (time, length);

        return s_listings;
    }

    private static void Save() {
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                // Written whole and then swapped in, so the other program never reads half a file.
                File.WriteAllText(FILE + ".tmp", JsonSerializer.Serialize(s_listings));
                File.Move(FILE + ".tmp", FILE, overwrite: true);
                (s_fileTime, s_fileLength) = FileStamp();

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (attempt == 2) {
                    Logger.Warning("Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
                }
                else {
                    System.Threading.Thread.Sleep(50);
                }
            }
        }
    }

    private static (DateTime Time, long Length) FileStamp() {
        try {
            var file = new FileInfo(FILE);

            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return (s_fileTime, s_fileLength);
        }
    }

}
