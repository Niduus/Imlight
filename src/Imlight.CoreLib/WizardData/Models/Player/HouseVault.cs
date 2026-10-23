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
 * What a storage object placed in a house holds: the Gear Vault, the Jewel Vault, the Gardening Shed (the
 * client's "Seed Vault"), the Treasure Card Vault and the Treasure Card Poster. It is stored on the placed item,
 * so it is written with the house and survives restarts like the house does.
 *
 * USAGE EXAMPLE:
 * var vault = (placed.Vault ?? HouseVault.Empty) with { Items = [.. placed.Vault?.Items ?? [], entry] };
 * var updated = placed with { Vault = vault };
 *
 * NOTE:
 * - Gear, jewels and seeds stay the items they were: the item documents are kept, the vault only holds their
 *   global ids, so an item comes back exactly as it went in. Gear also keeps the public look the client draws it with.
 * - Treasure cards are not items here (the treasure book holds template ids), so the vault holds one card per entry.
 * - Like every stored part of a placed item, a vault is never changed in place; a change replaces it.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// One backpack item kept in a vault or a shed.
/// </summary>
public sealed record VaultItem {

    /// <summary>
    /// The global id of the item; its stored document is kept while it is in the vault.
    /// </summary>
    public ulong ItemGid { get; init; }

    public uint TemplateId { get; init; }

    /// <summary>
    /// For gear, the serialized public look of the item (a WizardEquippedItemInfo), which is the entry the client
    /// shows and sends back to name the item. Empty for jewels and seeds.
    /// </summary>
    public byte[] Info { get; init; }

}

/// <summary>
/// One treasure card kept in a Treasure Card Vault or shown on a poster.
/// </summary>
public sealed record VaultCard {

    /// <summary>
    /// The spell id as the client sent it, which is what the client is given back in the vault data.
    /// </summary>
    public uint ClientSpellId { get; init; }

    /// <summary>
    /// The spell template the treasure book holds.
    /// </summary>
    public uint TemplateId { get; init; }

    public uint Enchantment { get; init; }

}

/// <summary>
/// The contents of a storage object placed in a house.
/// </summary>
public sealed record HouseVault {

    public static HouseVault Empty { get; } = new();

    /// <summary>
    /// The gear, jewels or seeds, in the order they went in.
    /// </summary>
    public List<VaultItem> Items { get; init; } = [];

    /// <summary>
    /// The treasure cards of a Treasure Card Vault, one entry per card.
    /// </summary>
    public List<VaultCard> Cards { get; init; } = [];

    /// <summary>
    /// The card a poster shows, or <c>null</c>.
    /// </summary>
    public VaultCard Poster { get; init; }

    /// <summary>
    /// The flags the client sent with the poster's card.
    /// </summary>
    public byte PosterFlags { get; init; }

    /// <summary>
    /// Whether the vault holds anything that would be lost with it. A poster's card is shown, not held.
    /// </summary>
    [JsonIgnore] public bool HoldsItems => (Items?.Count ?? 0) > 0 || (Cards?.Count ?? 0) > 0;

    /// <summary>
    /// Counts the entries of a template.
    /// </summary>
    /// <param name="templateId">The item template.</param>
    /// <returns>The number of items of the template.</returns>
    public int CountTemplate(uint templateId)
        => (Items ?? []).Count(item => item.TemplateId == templateId);

}
