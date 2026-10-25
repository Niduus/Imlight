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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizInventoryBehavior : IClientBehaviorProvider<ClientWizInventoryBehavior> {

    [JsonIgnore] public bool NoTransfer { get; set; } = false;

    private static int s_maxItemsAllowed = ConfigurationManager.Settings["Character.MaxInventoryItems"].AsInt();
    private static int s_maxJewelsAllowed => ConfigurationManager.Settings["Character.MaxJewelsAllowed"].AsInt();
    private static readonly int s_maxItemsAllowedFallback = 20;

    private static int s_numEmotesAllowed => ConfigurationManager.Settings["Character.MaxEmotesAllowed"].AsInt();
    private static int s_numTeleportsAllowed => ConfigurationManager.Settings["Character.MaxTeleportsAllowed"].AsInt();

    public List<ulong> InventoryItemIds { get; set; }

    /// <summary>
    /// This character's own backpack size, set with the .backpack command; 0 uses [Character] MaxInventoryItems.
    /// </summary>
    public int ItemLimit { get; set; }

    /// <summary>
    /// How many items the backpack holds.
    /// </summary>
    [JsonIgnore] public int Limit
        => ItemLimit > 0 ? ItemLimit : s_maxItemsAllowed <= 0 ? s_maxItemsAllowedFallback : s_maxItemsAllowed;

    [JsonIgnore] public List<WizClientObjectItem> Items { get; set; }

    /// <summary>
    /// The number of backpack items that take a slot: jewels go in the jewel pouch, and reagents, pet snacks and
    /// emotes are not counted either, see <see cref="BankRules.TakesBackpackSlot"/>.
    /// </summary>
    [JsonIgnore] public int UsedSlots
        => Items.Count(BankRules.TakesBackpackSlot);

    /// <summary>
    /// The number of items that still fit in the inventory. Equipped items are not counted, as in
    /// <see cref="AddItem"/>; a new item is checked against Wizard.BackpackFreeSlots, which counts them.
    /// </summary>
    [JsonIgnore] public int FreeSlots
        => Math.Max(0, Limit - UsedSlots);

    /// <summary>
    /// Adds an item to the player's inventory.
    /// </summary>
    /// <param name="item">The item to be added.</param>
    /// <returns>True if the item was successfully added, false otherwise.</returns>
    public bool AddItem(WizClientObjectItem item) {
        if (s_maxItemsAllowed <= 0) {
            Logger.Warning("Configuration states that {0} is not allowed to have any items in their inventory! "
                + "This is not possible, and causes many game-breaking bugs. Defaulting to {1} items.",
                Logger.Args(nameof(s_maxItemsAllowed), s_maxItemsAllowedFallback));

            s_maxItemsAllowed = s_maxItemsAllowedFallback;
        }

        if (BankRules.TakesBackpackSlot(item) && UsedSlots >= Limit) {
            Logger.Debug("Player inventory is full. Cannot add item with global id {0}.", Logger.Args(item.m_globalID));

            return false;
        }

        if (HasItem(item.m_globalID)) {
            Logger.Error("Item with same global id {0} already exists in player inventory.", Logger.Args(item.m_globalID));

            return false;
        }

        InventoryItemIds.Add(item.m_globalID);
        Items.Add(item);
        
        return true;
    }

    /// <summary>
    /// Removes an item from the player's inventory based on its unique identifier.
    /// </summary>
    /// <param name="itemId">The unique identifier of the item to be removed.</param>
    /// <returns><c>true</c> if the item was successfully removed; otherwise, <c>false</c>.</returns>
    public bool RemoveItem(ulong itemId, out WizClientObjectItem removedItem) {
        removedItem = null;

        // Get the actual item from the inventory.
        removedItem = Items.Find(i => i.m_globalID == itemId);
        if (removedItem is null) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(itemId));

            return false;
        }

        return RemoveItem(removedItem);
    }

    /// <summary>
    /// Removes an item from the player's inventory.
    /// </summary>
    /// <param name="item">The item to be removed.</param>
    /// <returns><c>true</c> if the item was successfully removed; otherwise, <c>false</c>.</returns>
    public bool RemoveItem(WizClientObjectItem item) {
        if (item is null) {
            throw new NullReferenceException("Item cannot be null.");
        }
        if (!Items.Remove(item)) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(item.m_globalID));

            return false;
        }

        if (!InventoryItemIds.Remove(item.m_globalID)) {
            Logger.Debug("Tried to remove item with global id {0} that does not exist in player inventory.",
                Logger.Args(item.m_globalID));

            return false;
        }

        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> s_notSentReported = new();

    /// <summary>
    /// An item the game cannot make (no such template, or a bundle, which is only ever claimed, never held) makes it
    /// drop the whole backpack and the connection at login (Niduus, 2026-10-21: "can't load my character"). Such items
    /// are left out of what the game is sent.
    /// </summary>
    private static bool CanSendToGame(WizClientObjectItem item) {
        var template = CoreObjectFactory.GetCoreTemplate(item.m_templateID);
        // Bundles, booster packs and currency amounts are not backpack objects to the game: one in the list stops the
        // whole backpack from loading ("Unable to instantiate object of type: 107"), so the character never loads in
        // (Walter Blanco, 2026-10-21: the Witch Hut bundle's Mega-Snack Pack).
        if (template is WizItemTemplate and not (ItemBundleTemplate or BoosterPackTemplate or GoldAmountTemplate
            or LunariAmountTemplate or CurrencyTemplate or TourneyTokensAmountTemplate)) {
            return true;
        }

        // Once per item and server run: the backpack is sent on every load and zone change.
        if (s_notSentReported.TryAdd(item.m_globalID.Full, 0)) {
            Logger.Warning("Inventory: item {0} (template {1}, {2}) is not sent to the game; it cannot show it.",
                Logger.Args(item.m_globalID.Full, item.m_templateID.Full, template?.GetType().Name ?? "no template"));
        }

        return false;
    }

    /// <summary>
    /// Checks if the inventory contains an item with the specified global ID.
    /// </summary>
    /// <param name="globalId">The global ID of the item to check.</param>
    /// <returns>True if the inventory contains an item with the specified global ID, otherwise false.</returns>
    public bool HasItem(ulong globalId) 
        => Items.Any(item => item.m_globalID == globalId);

    /// <summary>
    /// Represents an item in the wizard's inventory.
    /// </summary>
    public WizClientObjectItem GetItem(ulong globalId) => Items.FirstOrDefault(item => item.m_globalID == globalId);

    public ClientWizInventoryBehavior GetClientBehaviorInstance() => new() {
        m_numItemsAllowed = Limit,
        m_numJewelsAllowed = s_maxJewelsAllowed,
        m_itemList = Items.Where(CanSendToGame).Select(item =>
            (item as IClientBehaviorProvider<CoreObject>)?.GetClientBehaviorInstance() ?? item).ToList(),
        m_numCEEmotesAllowed = s_numEmotesAllowed,
        m_numCETeleportsAllowed = s_numTeleportsAllowed
    };

}
