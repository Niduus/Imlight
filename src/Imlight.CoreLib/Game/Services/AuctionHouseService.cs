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
 * AUCTION HOUSE SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player interactions with the in-game auction house, handling 
 * item buying, selling, and inventory management.
 * 
 * USAGE EXAMPLE:
 * Internal service used within the game server's session management system.
 * Handles various auction house commands through message routing.
 * 
 * NOTE:
 * 
 * TODO:
 * - Implement proper error handling for auction house transactions
 * - Complete implementation of unhandled command cases
 * - Review and refine gold calculation logic
 * 
 * Created by: Joji
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Linq;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty;
using Imcodec.IO;
using Imcodec.Cryptography;
using Imlight.Common;
using Imcodec.Types;

namespace Imlight.CoreLib.Game.Services;

internal class AuctionHouseService(SessionActor sessionActor) : MessageService(sessionActor) {

    private readonly CoreObjectSerializer _itemSerializer = new(
        behaviors: SerializerFlags.None
    );

    private static ByteString WriteAuctionBlob(uint categoryHash, IReadOnlyCollection<AuctionHouseEntry> entries) {
        // Writes auction house entries as binary. This is very similar to PropertyClass serialization.
        //
        // Format:
        //   [0-3]  Category hash (uint32) — StringHash of category name (hat, robe, boots), or 0 for empty
        //   [4-7]  Entry count (uint32)
        //   [8..]  Array of entries, each 20 bytes:
        //          [0-7]  GID / template ID (uint64, little-endian)
        //          [8-11] Stock quantity (int32)
        //          [12-15] Buy price (int32)
        //          [16-19] Sell price (int32)
        var writer = new BitWriter();
        writer.WriteUInt32(categoryHash);
        writer.WriteUInt32((uint) entries.Count);

        foreach (var entry in entries) {
            // Ensure the GID has Type=9
            // Live bazaar entries use Type=9; our database entries may
            // have Type=0 from direct ulong→GID casts.
            var gid = new GID(entry.m_templateID.Full | (9UL << 40));

            writer.WriteUInt64(gid);
            writer.WriteInt32(entry.m_numForSale);
            writer.WriteInt32(entry.m_buyPrice);
            writer.WriteInt32(entry.m_sellPrice);
        }

        return writer.GetData();
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new AuctionHouseService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST))]
    private void ReceiveAuctionHouseRequest(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        switch (message.Command) {
            case 0:
                SendAuctionHouseContents(message.npcGlobalID, message.category, message.key);
                break;
            case 1:
                ConfirmBuyFromAuctionHouse(message.itemTemplateID, message.key);
                break;
            case 2:
                SellToAuctionHouse(message.itemGlobalID, message.key);
                break;
            case 3:
                BuyFromAuctionHouse(message.itemTemplateID, message.texture, message.decal, message.key);
                break;
            default:
                Logger.Information("Bazaar: unhandled command {0} (category {1}, template {2}, item {3}).",
                    Logger.Args(message.Command, message.category, message.itemTemplateID, message.itemGlobalID));
                break;
        }
    }

    private void SendAuctionHouseContents(ulong npcId, uint category, uint key) {
        // Retrieve all Auction House entries. The treasure card tab gets every treasure card in the game.
        Logger.Information("Bazaar: category {0} ({1}) requested.",
            Logger.Args(category, CategoryName(category) ?? "unknown name"));
        var houseEntries = AuctionHouseCollection.GetAllAuctionHouseEntries();

        // The game sorts what it gets into its own tabs, so every answer carries the treasure cards too: the
        // treasure card tab's category name is not known, and a tab the server doesn't recognize got no cards.
        // One answer that is too big makes the game drop the connection (all 5218 cards did), so the list is
        // kept under MAX_ENTRIES with the treasure cards spread over every school.
        List<AuctionHouseEntry> houseList = IsTreasureCardCategory(category) ? [] : houseEntries ?? [];
        var room = Math.Max(0, Math.Min(s_treasureCardLimit, MAX_ENTRIES - houseList.Count));
        List<AuctionHouseEntry> entryList = [.. houseList.Take(MAX_ENTRIES), .. s_shownTreasureCards.Value.Take(room)];

        // Serialize in the compact binary format the client expects.
        // category=0 is reserved for empty/update signals per the live capture.
        var contentsData = WriteAuctionBlob(category, entryList);

        var auctionHouseContentsMsg = new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS {
            Contents = contentsData,
            GlobalID = npcId,
            LastSegment = 0
        };
        SendToSocket(auctionHouseContentsMsg);
    }

    private void ConfirmBuyFromAuctionHouse(ulong templateId, uint key) {
        var entry = AuctionHouseCollection.GetAuctionHouseEntry(templateId) ?? TreasureCard(templateId)?.Entry;

        if (entry is null) {
            // todo: figure out correct response, should inform player the transaction failed and reload content.
            return;
        }

        var auctionRspMsg = new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 1,
            ItemTemplateID = templateId,
            Cost = entry.m_buyPrice,
            ReturnCode = 0
        };
        SendToSocket(auctionRspMsg);
    }

    private void BuyFromAuctionHouse(ulong templateId, int texture, int decal, uint key) {
        var wizard = GetActiveWizard();
        if (TreasureCard(templateId) is { } treasureCard) {
            BuyTreasureCard(wizard, treasureCard);

            return;
        }
        var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(templateId);

        var item = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(templateId);
        item.m_primaryColor = texture;
        item.m_secondaryColor = decal;

        // Try serializing item data.
        if (!_itemSerializer.Serialize(item, 1, out var itemData)) {
            Logger.Error("Failed to serialize item data.");

            return;
        }

        var entry = AuctionHouseCollection.GetAuctionHouseEntry(templateId);
        if (entry is null) { // Entry does not exist.
            return;
        }
        var goldCost = entry.m_buyPrice;

        // Update stock and push to database.
        entry.m_numForSale -= 1;
        if (entry.m_numForSale < 1) {
            // Remove entry if no more stock.
            AuctionHouseCollection.RemoveAuctionHouseEntry(templateId);
        }
        else {
            AuctionHouseCollection.UpdateAuctionHouseEntry(entry);
        }

        // Inform of update.
        var houseEntryData = WriteAuctionBlob(0, [entry]);
        var auctionUpdateMsg = new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = houseEntryData,
            CharacterID = wizard.CharId
        };
        SendToSocket(auctionUpdateMsg);

        // Add item to inventory
        var addItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = itemData,
        };
        SendToSocket(addItemMsg);

        // Add item to inventory after message to prevent crashes.
        wizard.AddItemToInventory(item);

        // Alert client of new item.
        var itemAcqMsg = new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = item.m_globalID,
            ItemTemplateID = (uint) item.m_templateID,
            ItemLocation = 1,
        };
        SendToSocket(itemAcqMsg);

        // Update gold balances.
        wizard.RemoveGold(goldCost);
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Confirm transaction.
        var shopBuyConfirmMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM {
            Failure = 0,
            WebFailure = 0,
            Credits = 0
        };
        SendToSocket(shopBuyConfirmMsg);
    }

    private void SellToAuctionHouse(ulong itemGlobalId, uint key) {
        var wizard = GetActiveWizard();
        var item = wizard.InventoryBehavior.GetItem(itemGlobalId);

        // Check player has item.
        var hasItem = wizard.InventoryBehavior.HasItem(itemGlobalId);
        if (!hasItem) {
            // Todo: respond with error
            var auctionRspErrorMsg = new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
                Command = 5,
                ItemTemplateID = 0, // Can't get this ID if item doesn't exist.
                Cost = 0,
                ReturnCode = 0
            };
            SendToSocket(auctionRspErrorMsg);
            return;
        }

        var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID);

        var isNoAuction = template.m_adjectiveList.Any(x => x == "FLAG_NoAuction");
        if (isNoAuction) { // The item cannot be sold to the bazaar.
            // Todo: respond with error
            var auctionRspErrorMsg = new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
                Command = 2,
                ItemTemplateID = item.m_templateID,
                Cost = 0,
                ReturnCode = 1
            };
            SendToSocket(auctionRspErrorMsg);
            return;
        }

        // Calculate gold sell value.
        var gold = (int) Math.Ceiling(template.m_baseCost * 0.5f); // Bazaar buys items at 50% of their value.
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            gold = (int) Math.Ceiling(gold * 1.225f);
        }

        var auctionRspMsg = new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 2,
            ItemTemplateID = item.m_templateID,
            Cost = gold,
            ReturnCode = 0
        };
        SendToSocket(auctionRspMsg);

        // Update stock and push to database.
        var entry = AuctionHouseCollection.GetAuctionHouseEntry(item.m_templateID);
        if (entry is null) {
            entry = new AuctionHouseEntry {
                m_templateID = (GID) item.m_templateID,
                m_buyPrice = (int)(template.m_baseCost * 2), // Bazaar sells items at 200% of their value.
                m_numForSale = 1,
                m_sellPrice = gold
            };
            AuctionHouseCollection.AddAuctionHouseEntry(entry);
        }
        else {
            if (entry.m_numForSale < 99) { // Stock limit of 99. Players can still sell item but stock will not increase.
                entry.m_numForSale += 1;
                AuctionHouseCollection.UpdateAuctionHouseEntry(entry);
            }
        }

        // Remove item from inventory.
        var removedItemSuccess = wizard.RemoveItemFromInventory(itemGlobalId);

        // Inform of update.
        var houseEntryData = WriteAuctionBlob(0, [entry]);
        var auctionUpdateMsg = new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = houseEntryData,
            CharacterID = wizard.CharId
        };
        SendToSocket(auctionUpdateMsg);

        // Update gold balances.
        wizard.AddGold(gold);

        var updateGoldMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(updateGoldMsg);

        // Remove item from inventory.
        var removeItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = item.m_globalID
        };
        SendToSocket(removeItemMsg);

        var shopSellConfirmMsg = new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM {
            ClientRequestID = 0,
            GlobalID = 0,
            Failure = 0
        };
        SendToSocket(shopSellConfirmMsg);
    }

    // Treasure cards in the bazaar: every one in the game, never sold out, priced from 1 to 200 gold.
    private const int TREASURE_STOCK = 99;
    private const int MIN_TREASURE_PRICE = 1;
    private const int MAX_TREASURE_PRICE = 200;

    // Names the client might give the treasure card tab. Every request's category is logged, so the real one
    // can be added here if none of these match.
    private static readonly string[] s_treasureCategoryNames = [
        "TreasureCards", "TreasureCard", "Treasure Cards", "Treasure Card", "Treasure", "TC", "TCs",
        "Spell", "Spells", "SpellCard", "SpellCards", "Card", "Cards", "CAT_TreasureCards", "Category_TreasureCards",
    ];
    private static readonly string[] s_otherCategoryNames = [
        "Hat", "Hats", "Robe", "Robes", "Boots", "Shoes", "Weapon", "Weapons", "Wand", "Athame", "Athames", "Amulet",
        "Amulets", "Ring", "Rings", "Deck", "Decks", "Pet", "Pets", "Mount", "Mounts", "Furniture", "Housing",
        "Reagent", "Reagents", "Jewel", "Jewels", "Seed", "Seeds", "Snack", "Snacks", "Elixir", "Elixirs", "All",
        "Everything", "Clothing", "Gear", "Equipment", "Item", "Items",
    ];
    private static readonly HashSet<uint> s_treasureCategories = [.. s_treasureCategoryNames.SelectMany(CategoryHashes)];

    private static IEnumerable<uint> CategoryHashes(string name)
        => [StringHash.Compute(name), StringHash.Compute(name.ToLowerInvariant())];

    private static bool IsTreasureCardCategory(uint category) => s_treasureCategories.Contains(category);

    private static string CategoryName(uint category)
        => s_treasureCategoryNames.Concat(s_otherCategoryNames).FirstOrDefault(name => CategoryHashes(name).Contains(category));

    // About 20 bytes an entry. 5218 entries in one answer made the game drop the connection.
    private const int MAX_ENTRIES = 1000;
    private static readonly int s_treasureCardLimit
        = Math.Clamp(Imlight.Common.ConfigurationManager.GetValue("Bazaar.TreasureCards", 500), 0, MAX_ENTRIES);

    // The order cards are shown in: one from each school in turn, cheapest first, so a short list still
    // has every school.
    private static readonly Lazy<List<AuctionHouseEntry>> s_shownTreasureCards = new(() => {
        var bySchool = s_treasureCards.Value.Values
            .GroupBy(card => card.School)
            .Select(group => group.OrderBy(card => card.Entry.m_buyPrice).ThenBy(card => card.SpellName).ToList())
            .ToList();
        var shown = new List<AuctionHouseEntry>();
        for (var i = 0; bySchool.Any(group => i < group.Count); i++) {
            shown.AddRange(bySchool.Where(group => i < group.Count).Select(group => group[i].Entry));
        }

        return shown;
    });

    private sealed record BazaarTreasureCard(AuctionHouseEntry Entry, string SpellName, uint SpellTemplateId, string School);

    private static readonly Lazy<Dictionary<ulong, BazaarTreasureCard>> s_treasureCards = new(BuildTreasureCards);

    private static Dictionary<ulong, BazaarTreasureCard> BuildTreasureCards() {
        var cards = new Dictionary<ulong, BazaarTreasureCard>();
        var seen = new HashSet<string>();
        foreach (var (_, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            if (string.IsNullOrEmpty(name) || !(spell.m_Treasure || name.EndsWith(" TC"))
                || string.IsNullOrEmpty((string) spell.m_sMagicSchoolName) || string.IsNullOrEmpty((string) spell.m_displayName)
                || !seen.Add(name)
                || Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(name) is not { } card) {
                continue;
            }

            var templateId = (uint) card.m_templateID;
            var price = Math.Clamp(spell.m_baseCost, MIN_TREASURE_PRICE, MAX_TREASURE_PRICE);
            cards[templateId] = new BazaarTreasureCard(new AuctionHouseEntry {
                m_templateID = (GID) (ulong) templateId,
                m_numForSale = TREASURE_STOCK,
                m_buyPrice = price,
                m_sellPrice = Math.Max(1, price / 2),
            }, name, templateId, (string) spell.m_sMagicSchoolName);
        }

        Logger.Information("Bazaar has {0} treasure cards.", Logger.Args(cards.Count));

        return cards;
    }

    // The client may send the id with the GID type bits set, so only the template part is compared.
    private static BazaarTreasureCard TreasureCard(ulong templateId)
        => s_treasureCards.Value.TryGetValue(templateId & 0xFFFFFFFFUL, out var card) ? card : null;

    private void BuyTreasureCard(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, BazaarTreasureCard card) {
        var price = card.Entry.m_buyPrice;
        if (wizard.GameStats.m_currentGold < price) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1, WebFailure = 0, Credits = 0 });

            return;
        }

        // Same as buying from a treasure card shop: the card goes into the treasure book and is saved.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
            SpellID = (int) StringHash.Compute(card.SpellName),
            EnchantmentID = 0,
        });
        wizard.SpellbookBehavior.AddTreasureCard(card.SpellTemplateId);
        WizardCollection.AddTreasureCard(wizard, card.SpellTemplateId);

        wizard.RemoveGold(price);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        // Stock never goes down.
        SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = WriteAuctionBlob(0, [card.Entry]),
            CharacterID = wizard.CharId
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 0, WebFailure = 0, Credits = 0 });
    }

}
