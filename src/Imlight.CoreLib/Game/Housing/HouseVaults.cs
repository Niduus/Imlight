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
 * HOUSE VAULTS
 * ========================================================================
 *
 * PURPOSE:
 * The storage furniture of a house: the Gear Vault, the Jewel Vault, the Gardening Shed (Seed Vault), the Treasure
 * Card Vault and the Treasure Card Poster. Says which placed items are one, how much they hold, and builds the data
 * the client shows them from.
 *
 * USAGE EXAMPLE:
 * var kind = HouseVaults.KindOf(placed.TemplateId);
 * HousingBlobObject entry = HouseVaults.ToBlobObject(placed);
 * ZoneBroadcast(HouseVaults.Patch(kind, placed.Vault, change.Revision, change.GidId), ...);
 *
 * NOTE:
 * - The client opens the window itself when the object is clicked; nothing is asked of the server. What it shows comes from
 *   the object's blob entry: a HousingBlobCustomObject whose m_customData the client loads into the object's behavior
 *   (ClientHousingManager builds the object, 0x140f32420). A change is a MSG_PATCH* with the same data, which the client
 *   takes only when the entry already is a custom object and the version is its blob's version + 1; it asks for the blob
 *   otherwise (ClientBlobPatcher::MSG_PatchHousingGearVault, 0x140f1d340). The patch also refreshes an open window.
 * - The data of the Gear, Jewel and Seed vaults is the behavior serialized by SerializerBinary with object sizes and
 *   property mask 0x10000, which only m_gearList, m_jewelList and m_seedList carry. A gear entry is a WizardEquippedItemInfo
 *   serialized the same way with mask 0x18 (GearVaultWindow, 0x140fcc1b0). A jewel or seed is one template id per item.
 * - The Treasure Card Vault's data is 0xFACE1ACE, version 1, the count and one (spell, enchantment) pair per card, and it
 *   holds 1000 (TreasureCardVaultBehaviorBase, 0x141dfca80 and 0x141dfcf10). The poster's is 0xFACE0ACE, version 1, the
 *   spell, the enchantment and a flags byte (0x141dfac70).
 * - The windows show "count/100" for gear, jewels and seeds and "count/1000" for treasure cards, so those are the limits.
 * - A vault keeps what it holds wherever it goes (Niduus, 2026-10-23): on the PlacedItem in a house, on the AtticItem in
 *   the attic, and in Wizard.PackedVaults by the item's global id in the backpack and the bank. The gear, jewels and seeds
 *   keep their stored documents all the way. A vault that holds anything is not thrown away, sold or deleted.
 *
 * TODO:
 * - Check with a capture whether the spell id the client sends is the template id or the name hash; the data echoes it.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing.Families;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// The kinds of storage furniture.
/// </summary>
internal enum VaultKind {
    None,
    Gear,
    Jewel,
    Seed,
    TreasureCard,
    Poster,
}

internal static class HouseVaults {

    private const string ItemSubType = "Item";
    private const uint BehaviorMask = 0x10000;
    private const uint GearEntryMask = 0x18;
    private const uint CardVaultMagic = 0xFACE1ACE;
    private const uint PosterMagic = 0xFACE0ACE;
    private const uint DataVersion = 1;

    // Template id to vault kind, so a house with many items reads each template once.
    private static readonly ConcurrentDictionary<uint, VaultKind> s_kinds = new();

    /// <summary>
    /// Finds which storage furniture a template is.
    /// </summary>
    /// <param name="templateId">The template of a placed item.</param>
    /// <returns><see cref="VaultKind.None"/> for anything that is not a vault, shed or poster.</returns>
    internal static VaultKind KindOf(uint templateId)
        => templateId == 0 ? VaultKind.None : s_kinds.GetOrAdd(templateId, Resolve);

    /// <summary>
    /// How many entries a vault holds: what its window shows as the space.
    /// </summary>
    internal static int Capacity(VaultKind kind)
        => kind switch {
            VaultKind.Gear or VaultKind.Jewel or VaultKind.Seed => 100,
            VaultKind.TreasureCard => 1000,
            _ => 0,
        };

    /// <summary>
    /// The name players know the object by, for messages.
    /// </summary>
    internal static string NameOf(VaultKind kind)
        => kind switch {
            VaultKind.Gear => "Gear Vault",
            VaultKind.Jewel => "Jewel Vault",
            VaultKind.Seed => "Seed Vault",
            VaultKind.TreasureCard => "Treasure Card Vault",
            VaultKind.Poster => "Treasure Card Poster",
            _ => "object",
        };

    /// <summary>
    /// Keeps the contents of a vault with its backpack item, so a picked up vault holds them wherever it goes and shows
    /// them again when it is placed. The caller holds HouseDeeds.BackpackLock and writes the owner with the change.
    /// </summary>
    /// <param name="wizard">The owner of the item.</param>
    /// <param name="itemGid">The global id of the vault's backpack item.</param>
    /// <param name="vault">What the vault holds; <c>null</c> or empty keeps nothing.</param>
    /// <returns><c>true</c> when contents were kept.</returns>
    internal static bool Pack(Wizard wizard, ulong itemGid, HouseVault vault) {
        if (vault is null || (!vault.HoldsItems && vault.Poster is null)) {
            return false;
        }

        wizard.PackedVaults ??= [];
        wizard.PackedVaults[itemGid] = vault;

        return true;
    }

    /// <summary>
    /// Takes the contents kept with a vault's backpack item off it, for the vault to hold them in a house or an attic.
    /// The caller holds HouseDeeds.BackpackLock and writes the owner with the change.
    /// </summary>
    /// <param name="wizard">The owner of the item.</param>
    /// <param name="itemGid">The global id of the backpack item.</param>
    /// <returns>The contents, or <c>null</c> when the item keeps none.</returns>
    internal static HouseVault TakePacked(Wizard wizard, ulong itemGid)
        => wizard.PackedVaults is not null && wizard.PackedVaults.Remove(itemGid, out var vault) ? vault : null;

    /// <summary>
    /// Builds the client's blob entry of a placed item: a custom object with the vault data for storage furniture.
    /// </summary>
    /// <param name="item">The placed item, normalized.</param>
    /// <returns>The blob entry.</returns>
    internal static HousingBlobObject ToBlobObject(PlacedItem item) {
        var entry = item.ToBlobObject();
        var kind = KindOf(item.TemplateId);
        if (kind == VaultKind.None || !TryBuildData(kind, item.Vault, out var data) || data is null) {
            return entry;
        }

        return new HousingBlobCustomObject {
            m_gameObjectTemplateID = entry.m_gameObjectTemplateID,
            m_positionXY = entry.m_positionXY,
            m_positionZ = entry.m_positionZ,
            m_yaw = entry.m_yaw,
            m_extraData1 = entry.m_extraData1,
            m_customData = data.Value,
        };
    }

    /// <summary>
    /// Builds the patch that gives every client the new contents of a vault.
    /// </summary>
    /// <param name="kind">The kind of the vault.</param>
    /// <param name="vault">The new contents.</param>
    /// <param name="revision">The revision of the change: the patch's version.</param>
    /// <param name="gidId">The vault's GIDID.</param>
    /// <returns>The patch, or <c>null</c> when the data could not be built.</returns>
    internal static IMessage Patch(VaultKind kind, HouseVault vault, uint revision, uint gidId) {
        if (!TryBuildData(kind, vault, out var built) || built is not { } data) {
            return null;
        }

        return kind switch {
            VaultKind.Gear => new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHHOUSINGGEARVAULT {
                Data = data, SubType = ItemSubType, PackageNumber = 0, VersionNumber = revision, GIDID = gidId,
            },
            VaultKind.Jewel => new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHHOUSINGJEWELVAULT {
                Data = data, SubType = ItemSubType, PackageNumber = 0, VersionNumber = revision, GIDID = gidId,
            },
            VaultKind.Seed => new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHHOUSINGGARDENINGSHED {
                Data = data, SubType = ItemSubType, PackageNumber = 0, VersionNumber = revision, GIDID = gidId,
            },
            VaultKind.TreasureCard => new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHTREASURECARDVAULT {
                Data = data, SubType = ItemSubType, PackageNumber = 0, VersionNumber = revision, GIDID = gidId,
            },
            VaultKind.Poster => new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHTREASURECARDPOSTER {
                Data = data, SubType = ItemSubType, PackageNumber = 0, VersionNumber = revision, GIDID = gidId,
            },
            _ => null,
        };
    }

    /// <summary>
    /// Builds the entry a gear vault shows for an item: its public look, serialized as the window reads it.
    /// </summary>
    /// <param name="item">The backpack item.</param>
    /// <param name="info">The entry when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the item could not be serialized.</returns>
    internal static bool TryBuildGearInfo(WizClientObjectItem item, out byte[] info) {
        info = null;
        try {
            if (!NewSerializer().Serialize(ItemHelper.GetPublicItem(item), GearEntryMask, out var serialized)) {
                return false;
            }

            info = serialized;

            return info is { Length: > 0 };
        } catch (Exception exception) {
            Logger.Error("Vaults: could not build the gear vault entry of item {ItemId}: {Reason}",
                Logger.Args(item.m_globalID.Full, exception.Message));

            return false;
        }
    }

    /// <summary>
    /// Builds the data the client loads a vault from.
    /// </summary>
    /// <param name="kind">The kind of the vault.</param>
    /// <param name="vault">Its contents; <c>null</c> for an empty one.</param>
    /// <param name="data">The data, or <c>null</c> for a poster that shows no card.</param>
    /// <returns><c>false</c> when the data could not be built.</returns>
    internal static bool TryBuildData(VaultKind kind, HouseVault vault, out ByteString? data) {
        data = null;
        vault ??= HouseVault.Empty;
        try {
            switch (kind) {
                case VaultKind.Gear:
                    return TrySerialize(new GearVaultBehavior {
                        m_gearList = [.. (vault.Items ?? []).Select(item => new ByteString(item.Info ?? []))],
                    }, out data);
                case VaultKind.Jewel:
                    return TrySerialize(new JewelVaultBehavior { m_jewelList = [.. (vault.Items ?? []).Select(item => item.TemplateId)] }, out data);
                case VaultKind.Seed:
                    return TrySerialize(new GardeningShedBehavior { m_seedList = [.. (vault.Items ?? []).Select(item => item.TemplateId)] }, out data);
                case VaultKind.TreasureCard:
                    data = CardVaultData(vault.Cards ?? []);

                    return true;
                case VaultKind.Poster:
                    data = vault.Poster is null ? null : (ByteString?) PosterData(vault.Poster, vault.PosterFlags);

                    return true;
                default:
                    return false;
            }
        } catch (Exception exception) {
            Logger.Error("Vaults: could not build the data of a {Kind}: {Reason}", Logger.Args(NameOf(kind), exception.Message));

            return false;
        }
    }

    private static ByteString CardVaultData(IReadOnlyList<VaultCard> cards) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(CardVaultMagic);
        writer.Write(DataVersion);
        writer.Write((uint) cards.Count);
        foreach (var card in cards) {
            writer.Write(card.ClientSpellId);
            writer.Write(card.Enchantment);
        }

        writer.Flush();

        return new ByteString(stream.ToArray());
    }

    private static ByteString PosterData(VaultCard card, byte flags) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PosterMagic);
        writer.Write(DataVersion);
        writer.Write(card.ClientSpellId);
        writer.Write(card.Enchantment);
        writer.Write(flags);
        writer.Flush();

        return new ByteString(stream.ToArray());
    }

    private static bool TrySerialize(PropertyClass behavior, out ByteString? data) {
        data = null;
        if (!NewSerializer().Serialize(behavior, BehaviorMask, out var serialized)) {
            return false;
        }

        data = serialized;

        return true;
    }

    // The client reads these with object and property sizes and no serializer flags (SerializerBinary(data, true, 0)).
    private static ObjectSerializer NewSerializer()
        => new(Versionable: true, Behaviors: SerializerFlags.None);

    private static VaultKind Resolve(uint templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not WizItemTemplate template) {
            return VaultKind.None;
        }

        foreach (var family in HousingFamilies.Of(template)) {
            switch (family.Id) {
                case "Gear":
                    return VaultKind.Gear;
                case "JewelVault":
                    return VaultKind.Jewel;
                case "SeedShed":
                    return VaultKind.Seed;
                case "TCVault":
                    return VaultKind.TreasureCard;
                case "TCPoster":
                    return VaultKind.Poster;
            }
        }

        return VaultKind.None;
    }

}
