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
 * Last Updated: 10/14/2026
 */

using System;
using System.Linq;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.CoreLib.Shared.Items;
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
            case 6:
                // Sent right after a tab's list arrives. Answering with the tab's entries again as an update
                // gives the game their stock counts.
                SendStock(message.category);
                break;
            default:
                Logger.Information("Bazaar: unhandled command {0} (category {1}, template {2}, item {3}).",
                    Logger.Args(message.Command, message.category, message.itemTemplateID, message.itemGlobalID));
                break;
        }
    }

    private void SendAuctionHouseContents(ulong npcId, uint category, uint key) {
        Logger.Information("Bazaar: category {0} ({1}) requested.",
            Logger.Args(category, CategoryName(category) ?? "unknown name"));

        // Serialize in the compact binary format the client expects.
        // category=0 is reserved for empty/update signals per the live capture.
        var entryList = EntriesFor(category);
        Logger.Debug("Bazaar: sending {0} entries.", Logger.Args(entryList.Count));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS {
            Contents = WriteAuctionBlob(category, entryList),
            GlobalID = npcId,
            LastSegment = 0
        });
    }

    private void SendStock(uint category) {
        var entryList = EntriesFor(category);
        if (entryList.Count == 0) {
            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = WriteAuctionBlob(0, entryList),
            CharacterID = GetActiveWizard().CharId
        });
    }

    /// <summary>
    /// What a tab shows. The treasure card tab asks once per school (the category is the school's id) and gets
    /// that school's cards; the snack tab gets the snacks; every other tab gets the bazaar's own entries and the
    /// snacks, which the game sorts into its tabs. One answer that is too big makes the game drop the connection,
    /// so an answer is kept under MAX_ENTRIES.
    /// </summary>
    private static List<AuctionHouseEntry> EntriesFor(uint category) {
        var schoolCards = s_shownTreasureCards.Value.Where(card => card.SchoolHash == category).ToList();
        if (schoolCards.Count > 0 || IsTreasureCardCategory(category)) {
            var cards = schoolCards.Count > 0 ? schoolCards : s_shownTreasureCards.Value;

            return [.. cards.Take(Math.Min(s_treasureCardLimit, MAX_ENTRIES)).Select(card => card.Entry)];
        }

        if (IsSnackCategory(category)) {
            return [.. s_snacks.Value.Values.Select(snack => snack.Entry)];
        }

        var houseEntries = AuctionHouseCollection.GetAllAuctionHouseEntries() ?? [];

        return [.. houseEntries.Where(entry => Snack(entry.m_templateID) is null)
            .Concat(s_snacks.Value.Values.Select(snack => snack.Entry))
            .Take(MAX_ENTRIES)];
    }

    private void ConfirmBuyFromAuctionHouse(ulong templateId, uint key) {
        var entry = Snack(templateId)?.Entry ?? AuctionHouseCollection.GetAuctionHouseEntry(templateId) ?? TreasureCard(templateId)?.Entry;

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

        if (Snack(templateId) is { } snack) {
            BuySnack(wizard, snack);

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

        // A full backpack denies the buy before the stock or the gold is touched.
        if (wizard.InventoryBehavior.FreeSlots < 1 || !wizard.AddItemToInventory(item)) {
            Logger.Warning("Could not add bazaar item {0} to the inventory of {1}, so the purchase is denied.",
                Logger.Args(templateId, wizard.CharId));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1, WebFailure = 0, Credits = 0 });

            return;
        }

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

        // Tell the client about the item. It is in the inventory already.
        var addItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = itemData,
        };
        SendToSocket(addItemMsg);

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

        var isNoAuction = template.m_adjectiveList.Any(x => x == "FLAG_NoAuction") || ItemHelper.IsStitched(item);
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

    // Pet snacks in the bazaar: every snack of rank 8 or higher, never sold out, for 1 gold each.
    private const int SNACK_STOCK = 99;
    private const int SNACK_PRICE = 1;
    private const int MIN_SNACK_RANK = 8;
    private const string SNACK_FOLDER = "ObjectData/PetSnacks/";

    // Names the client might give the snack tab (the other tabs ask with an adjective, like Hat or Reagent).
    private static readonly HashSet<uint> s_snackCategories
        = [.. new[] { "PetSnack", "PetSnacks", "Snack", "Snacks" }.SelectMany(CategoryHashes)];

    private static bool IsSnackCategory(uint category) => s_snackCategories.Contains(category);

    private sealed record BazaarSnack(AuctionHouseEntry Entry, PetSnackItemTemplate Template);

    private static readonly Lazy<Dictionary<ulong, BazaarSnack>> s_snacks = new(BuildSnacks);

    private static Dictionary<ulong, BazaarSnack> BuildSnacks() {
        var snacks = new Dictionary<ulong, BazaarSnack>();
        try {
            foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                string file = location?.m_filename;
                if (file is null || !file.StartsWith(SNACK_FOLDER, StringComparison.OrdinalIgnoreCase)
                    || CoreObjectFactory.GetCoreTemplate(location.m_id) is not PetSnackItemTemplate snack
                    || snack.m_rank < MIN_SNACK_RANK) {
                    continue;
                }

                snacks[location.m_id] = new BazaarSnack(new AuctionHouseEntry {
                    m_templateID = (GID) (ulong) location.m_id,
                    m_numForSale = SNACK_STOCK,
                    m_buyPrice = SNACK_PRICE,
                    m_sellPrice = SNACK_PRICE,
                }, snack);
            }
        }
        catch (Exception e) {
            Logger.Error("Could not list the bazaar snacks: {0}", Logger.Args(e));
        }

        Logger.Information("Bazaar has {0} pet snacks of rank {1} or higher.", Logger.Args(snacks.Count, MIN_SNACK_RANK));

        return snacks;
    }

    private static BazaarSnack Snack(ulong templateId)
        => s_snacks.Value.TryGetValue(templateId & 0xFFFFFFFFUL, out var snack) ? snack : null;

    private void BuySnack(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, BazaarSnack bazaarSnack) {
        var price = bazaarSnack.Entry.m_buyPrice;
        var templateId = (ulong) bazaarSnack.Entry.m_templateID & 0xFFFFFFFFUL;
        if (wizard.GameStats.m_currentGold < price || !wizard.AddSnack(templateId, out var snack)
            || !_itemSerializer.Serialize(snack, 24, out var snackData)) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1, WebFailure = 0, Credits = 0 });

            return;
        }

        // Same messages as .mod addsnack: the snack goes into the snack bag.
        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = snackData });
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = snack.m_globalID,
            ItemTemplateID = (uint) snack.m_templateID,
            ItemLocation = 1,
        });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
            GlobalID = wizard.GameObjectID,
            ItemID = snack.m_globalID,
            Quantity = snack.m_quantity,
        });

        wizard.RemoveGold(price);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        // Stock never goes down.
        SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = WriteAuctionBlob(0, [bazaarSnack.Entry]),
            CharacterID = wizard.CharId
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 0, WebFailure = 0, Credits = 0 });
    }

    // Treasure cards in the bazaar: every one in the game, never sold out, priced from 1 to 200 gold.
    private const int TREASURE_STOCK = 999;
    private const string TREASURE_CARD_FOLDER = "TreasureCards/";
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
        => Enum.IsDefined(typeof(Imlight.CoreLib.Shared.Behaviors.MagicSchool), (int) category)
            ? $"{(Imlight.CoreLib.Shared.Behaviors.MagicSchool) (int) category} treasure cards"
            : s_treasureCategoryNames.Concat(s_otherCategoryNames).FirstOrDefault(name => CategoryHashes(name).Contains(category));

    // About 20 bytes an entry. 5218 entries in one answer made the game drop the connection.
    private const int MAX_ENTRIES = 1000;
    // [Bazaar] TreasureCards: how many cards a tab lists (0 turns Elik's treasure cards off).
    private static readonly int s_treasureCardLimit
        = Math.Clamp(Imlight.Common.ConfigurationManager.GetValue("Bazaar.TreasureCards", 500), 0, MAX_ENTRIES);

    // The order cards are shown in: one from each school in turn, cheapest first, so a short list still
    // has every school.
    private static readonly Lazy<List<BazaarTreasureCard>> s_shownTreasureCards = new(() => {
        var bySchool = s_treasureCards.Value.Values
            .GroupBy(card => card.School)
            .Select(group => group.OrderBy(card => card.Entry.m_buyPrice).ThenBy(card => card.SpellName).ToList())
            .ToList();
        var shown = new List<BazaarTreasureCard>();
        for (var i = 0; bySchool.Any(group => i < group.Count); i++) {
            shown.AddRange(bySchool.Where(group => i < group.Count).Select(group => group[i]));
        }

        return shown;
    });

    private sealed record BazaarTreasureCard(AuctionHouseEntry Entry, string SpellName, uint SpellTemplateId, string School) {
        // The id the game uses for the school (Fire is 2343174), which is what the treasure card tab asks for.
        public uint SchoolHash { get; } = Enum.TryParse<Imlight.CoreLib.Shared.Behaviors.MagicSchool>(School, true, out var school)
            ? (uint) school
            : 0;
    }

    private static readonly Lazy<Dictionary<ulong, BazaarTreasureCard>> s_treasureCards = new(BuildTreasureCards);

    private static Dictionary<ulong, BazaarTreasureCard> BuildTreasureCards() {
        var cards = new Dictionary<ulong, BazaarTreasureCard>();
        var seen = new HashSet<string>();
        // The plain "<name> TC" card comes first, so it is the one kept when a card has several versions.
        var spells = Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()
            .OrderBy(pair => ((string) pair.Template.m_name)?.EndsWith(" TC", StringComparison.Ordinal) == true ? 0 : 1)
            .ThenBy(pair => ((string) pair.Template.m_name)?.Length ?? int.MaxValue)
            .ThenBy(pair => (string) pair.Template.m_name, StringComparer.Ordinal);
        foreach (var (path, spell) in spells) {
            string name = spell.m_name;
            // Only real treasure cards: the ones in the game's TreasureCards folder. Pet, monster and trained
            // spells with the same picture (Wyldfire, for one) live elsewhere.
            if (string.IsNullOrEmpty(name) || !spell.m_Treasure
                || path?.Replace('\\', '/').Contains(TREASURE_CARD_FOLDER, StringComparison.OrdinalIgnoreCase) != true
                || string.IsNullOrEmpty((string) spell.m_sMagicSchoolName) || string.IsNullOrEmpty((string) spell.m_displayName)
                || !seen.Add($"{spell.m_displayName}|{spell.m_sMagicSchoolName}")
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
