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
using Imlight.CoreLib.Game.Pet;
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

    // Building every list takes several seconds. Done on the first request, the window gave up waiting and stayed
    // empty (Niduus's log of 2026-10-19), so the lists are built in the background as soon as a player logs in.
    private static readonly Lazy<System.Threading.Tasks.Task> s_warmUp = new(() => System.Threading.Tasks.Task.Run(() => {
        try {
            _ = s_treasureCards.Value;
            _ = s_shownTreasureCards.Value;
            _ = s_reagents.Value;
            _ = s_jewels.Value;
            _ = s_snacks.Value;
        }
        catch (Exception e) {
            Logger.Error("Could not build the bazaar lists: {0}", Logger.Args(e));
        }
    }));

    private readonly System.Threading.Tasks.Task _warmUp = s_warmUp.Value;

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
            case 4:
            case 5:
            case 7:
            case 8:
                // Buy More is 7 (template only) and Sell More is 8 (with the held item's id), per Niduus's log. The
                // request is matched by what its ids name: a snack or an item the player holds is sold, a bazaar
                // entry is bought.
                Logger.Information("Bazaar: more request {0} (template {1}, item {2}, quantity {3}, type {4}, category {5}).",
                    Logger.Args(message.Command, message.itemTemplateID, message.itemGlobalID, message.quantity, message.itemType, message.category));
                BuyOrSellMore(message);
                break;
            case 6:
                Logger.Debug("Bazaar: stock request (category {0}).", Logger.Args(message.category));
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
        var entryList = s_sendEverything ? WholeBazaar(category) : EntriesFor(category);
        var first = entryList.Take(s_oneList).ToList();
        Logger.Debug("Bazaar: sending {0} entries ({1} in the list), first {2}.", Logger.Args(entryList.Count, first.Count,
            string.Join(", ", first.Take(3).Select(entry => (ulong) entry.m_templateID))));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS {
            Contents = WriteAuctionBlob(category, first),
            GlobalID = npcId,
            LastSegment = 0
        });

        // The game shows only the first list of a visit (Niduus, 2026-10-19). Anything past [Bazaar] OneList goes as
        // stock updates, which did not add to the list when tried, so OneList should hold everything.
        SendUpdates(entryList.Skip(s_oneList));
    }

    private void SendStock(uint category)
        => SendUpdates(s_sendEverything ? WholeBazaar(category) : EntriesFor(category));

    private void SendUpdates(IEnumerable<AuctionHouseEntry> entries) {
        foreach (var part in entries.Chunk(UPDATE_ENTRIES)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
                UpdateInfo = WriteAuctionBlob(0, part),
                CharacterID = GetActiveWizard().CharId
            });
        }
    }

    private const int UPDATE_ENTRIES = 800;

    // [Bazaar] OneList: how many entries the first list may hold. Big lists used to drop the connection, but that was
    // the long frame length bug (see SocketSender.FixLongFrameLength); 2946 in one list worked after the fix (Niduus,
    // 2026-10-19). The list is written with a 16-bit byte length, so 20-byte entries top out at MAX_ONE_LIST. Whatever
    // does not fit goes as stock updates, which the game did not add to the list.
    private const int MAX_ONE_LIST = (ushort.MaxValue - 8) / 20;
    private static readonly int s_oneList
        = Math.Clamp(Imlight.Common.ConfigurationManager.GetValue("Bazaar.OneList", MAX_ONE_LIST), 1, MAX_ONE_LIST);

    // [Bazaar] SendEverything: every tab of a window gets everything that window sells, because the game keeps one
    // list per window and each answer replaced the last, so only the tab that answer was for showed anything (Niduus,
    // 2026-10-19). Off sends each tab only its own items.
    private static readonly bool s_sendEverything
        = Imlight.Common.ConfigurationManager.GetValue("Bazaar.SendEverything", true);

    // Lazy: s_jewelShapes is set further down, after this field would be.
    private static readonly Lazy<HashSet<uint>> s_jewelTabs = new(() => [.. s_jewelShapes.Select(StringHash.Compute)]);

    /// <summary>
    /// Everything the bazaar sells, with the tab asked for first and then the rest of its window. The game shows only
    /// the first list of a visit, so every shortcut has to bring every tab's items, or the tabs of the other shortcuts
    /// stay empty (Niduus, 2026-10-19). One list holds at most MAX_ONE_LIST, so what comes last can be cut.
    /// </summary>
    private static List<AuctionHouseEntry> WholeBazaar(uint category) {
        IEnumerable<AuctionHouseEntry> snacks = s_snacks.Value.Values.Select(snack => snack.Entry);
        IEnumerable<AuctionHouseEntry> reagents = s_reagents.Value.ById.Values;
        IEnumerable<AuctionHouseEntry> jewels = s_jewels.Value.ById.Values;
        var cards = s_shownTreasureCards.Value.Select(card => card.Entry);
        var house = (AuctionHouseCollection.GetAllAuctionHouseEntries() ?? []).Where(entry => Snack(entry.m_templateID) is null);

        var ordered = s_jewelTabs.Value.Contains(category)
            ? (s_jewels.Value.ByCategory.TryGetValue(category, out var own) ? own : [])
                // Then what the real bazaar sells and the Star tab's pet jewels, then the rest, so what does not fit
                // (all 4416 do not) is the extra health and card jewels of other tabs.
                .Concat(jewels.Where(entry => !s_jewels.Value.NoAuction.Contains((ulong) entry.m_templateID)))
                .Concat(s_jewels.Value.ByCategory.TryGetValue(s_petJewelTab, out var pets) ? pets : [])
                .Concat(jewels).Concat(snacks).Concat(reagents).Concat(cards)
            : category == MIXED_CATEGORY || IsSnackCategory(category) || s_reagents.Value.ByCategory.ContainsKey(category)
                ? EntriesFor(category).Concat(snacks).Concat(reagents).Concat(cards).Concat(jewels)
                : EntriesFor(category).Concat(cards).Concat(snacks).Concat(reagents).Concat(jewels);

        return [.. ordered.Concat(house).DistinctBy(entry => (ulong) entry.m_templateID)];
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
            // One school at a time (Niduus): the tab's general request gets the first school, not every card.
            var cards = schoolCards.Count > 0 ? schoolCards : FirstSchoolCards();

            return [.. cards.Take(Math.Min(s_treasureCardLimit, MAX_ENTRIES)).Select(card => card.Entry)];
        }

        if (category == MIXED_CATEGORY) {
            // The window with the snack, reagent, jewel and pin tabs asks this first and fills every one of its tabs
            // from the answer: only snacks were in it, and only the snack tab had anything (Niduus, 2026-10-19).
            return [.. s_snacks.Value.Values.Select(snack => snack.Entry)
                .Concat(s_reagents.Value.ById.Values)
                .Concat(s_jewelShapes.SelectMany(shape => s_jewels.Value.ByCategory.TryGetValue(StringHash.Compute(shape), out var list)
                    ? list.Take(s_catalogLimit)
                    : []))
                .Distinct()
                .Take(MAX_ENTRIES)];
        }

        if (IsSnackCategory(category)) {
            return [.. s_snacks.Value.Values.Select(snack => snack.Entry)];
        }

        if (s_reagents.Value.ByCategory.TryGetValue(category, out var reagents)) {
            return [.. reagents.Take(s_catalogLimit)];
        }

        if (s_jewels.Value.ByCategory.TryGetValue(category, out var jewels)) {
            // The Star tab (pet jewels) lists every one of them (Niduus, 2026-10-19); the other tabs stay short.
            return [.. jewels.Take(category == s_petJewelTab ? MAX_ENTRIES : s_catalogLimit)];
        }

        var houseEntries = AuctionHouseCollection.GetAllAuctionHouseEntries() ?? [];

        return [.. houseEntries.Where(entry => Snack(entry.m_templateID) is null)
            .Concat(s_snacks.Value.Values.Select(snack => snack.Entry))
            .Take(MAX_ENTRIES)];
    }

    private void ConfirmBuyFromAuctionHouse(ulong templateId, uint key) {
        var entry = Snack(templateId)?.Entry ?? AuctionHouseCollection.GetAuctionHouseEntry(templateId) ?? TreasureCard(templateId)?.Entry
            ?? Reagent(templateId) ?? Jewel(templateId);

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

        if (Reagent(templateId) is { } reagent) {
            BuyReagent(wizard, reagent);

            return;
        }

        // The game sends the id with the bazaar's type bits on top (9 << 40); the template is the low 32 bits.
        // Jewels failed to buy with the full id (Niduus, 2026-10-19).
        Logger.Information("Bazaar: buying {0}.", Logger.Args(templateId));
        templateId &= 0xFFFFFFFFUL;
        if (CoreObjectFactory.FinalizeCoreObject(templateId) is not WizClientObjectItem item) {
            Logger.Warning("Bazaar: no item template {0} to buy.", Logger.Args(templateId));
            SendBuyConfirm(failed: true);

            return;
        }

        item.m_primaryColor = texture;
        item.m_secondaryColor = decal;

        // Try serializing item data.
        if (!_itemSerializer.Serialize(item, 1, out var itemData)) {
            Logger.Error("Failed to serialize item data.");

            return;
        }

        // Jewels and pins are always in stock; other items come from the bazaar's own stock.
        var jewel = Jewel(templateId);
        var entry = jewel ?? AuctionHouseCollection.GetAuctionHouseEntry(templateId);
        if (entry is null) { // Entry does not exist.
            return;
        }

        if (wizard.GameStats.m_currentGold < entry.m_buyPrice) {
            SendBuyConfirm(failed: true);

            return;
        }
        var goldCost = entry.m_buyPrice;

        // A full backpack denies the buy before the stock or the gold is touched.
        if (wizard.InventoryBehavior.FreeSlots < 1 || !wizard.AddItemToInventory(item)) {
            Logger.Warning("Could not add bazaar item {0} to the inventory of {1}, so the purchase is denied.",
                Logger.Args(templateId, wizard.CharId));
            SendBuyConfirm(failed: true);

            return;
        }

        // Update stock and push to database.
        if (jewel is null) {
            entry.m_numForSale -= 1;
            if (entry.m_numForSale < 1) {
                // Remove entry if no more stock.
                AuctionHouseCollection.RemoveAuctionHouseEntry(templateId);
            }
            else {
                AuctionHouseCollection.UpdateAuctionHouseEntry(entry);
            }
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
        PetTome.Announce(SessionActor.ActorRef, wizard, item);

        // Alert client of new item.
        SendItemAcquisition(item.m_globalID, item.m_templateID);

        // Update gold balances.
        wizard.RemoveGold(goldCost);
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Confirm transaction.
        SendBuyConfirm(failed: false);
    }

    // The most copies one Buy More or Sell More request moves.
    private const uint MAX_MORE = 99;

    private void BuyOrSellMore(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        var wizard = GetActiveWizard();
        var quantity = (int) Math.Clamp(message.quantity, 1u, MAX_MORE);
        foreach (var id in new[] { message.itemGlobalID, message.itemTemplateID }.Where(id => id != 0)) {
            if (wizard.PetSnackBehavior.HasSnackID(id)) {
                var snack = wizard.PetSnackBehavior.Snacks.First(s => s.m_globalID.Full == id);
                var templateId = (ulong) snack.m_templateID;
                var gold = SellSnack(wizard, id, quantity, out var sold);
                SendMoreAcknowledgement(templateId, isSell: true, gold, sold);

                return;
            }

            if (wizard.AlchemyBehavior.HasReagentID(id)) {
                var templateId = (ulong) wizard.AlchemyBehavior.Reagents.First(r => r.m_globalID.Full == id).m_templateID;
                var gold = SellReagent(wizard, id, quantity, out var sold);
                SendMoreAcknowledgement(templateId, isSell: true, gold, sold);

                return;
            }

            if (wizard.InventoryBehavior.HasItem(id)) {
                // The held item and more of the same kind, up to the number asked for; one confirm for the lot.
                var templateId = (ulong) wizard.InventoryBehavior.GetItem(id).m_templateID;
                var copies = new[] { id }.Concat((wizard.InventoryBehavior.Items ?? [])
                        .Where(item => item is not null && (ulong) item.m_templateID == templateId && item.m_globalID.Full != id
                            && !ItemHelper.IsLocked(item))
                        .Select(item => item.m_globalID.Full))
                    .Take(quantity)
                    .ToList();
                var goldBefore = wizard.GameStats.m_currentGold;
                _sellingMore = true;
                try {
                    foreach (var copy in copies) {
                        SellToAuctionHouse(copy, message.key);
                    }
                }
                finally {
                    _sellingMore = false;
                }

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });
                SendMoreAcknowledgement(templateId, isSell: true, wizard.GameStats.m_currentGold - goldBefore, copies.Count);

                return;
            }

            // Sell More (8) never buys: an id the player does not hold is not sold.
            if (message.Command == 8) {
                Logger.Information("Bazaar: Sell More of {0}, which the player does not hold.", Logger.Args(id));

                continue;
            }

            if (Snack(id) is not null || TreasureCard(id) is not null || Reagent(id) is not null || Jewel(id) is not null
                || AuctionHouseCollection.GetAuctionHouseEntry(id) is not null) {
                // Each copy is bought quietly; the game gets one confirm (one "ka-ching") for the whole lot.
                var goldBefore = wizard.GameStats.m_currentGold;
                var bought = 0;
                _moreBought = 0;
                _moreFailed = false;
                try {
                    // A copy that is skipped without a confirm still counts as a try, so this always ends.
                    for (var i = 0; i < quantity && !_moreFailed; i++) {
                        BuyFromAuctionHouse(id, message.texture, message.decal, message.key);
                    }
                }
                finally {
                    bought = _moreBought;
                    _moreBought = NOT_BUYING_MORE;
                    _moreFailed = false;
                }

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = (sbyte) (bought > 0 ? 0 : 1), WebFailure = 0, Credits = 0 });
                SendMoreAcknowledgement(id, isSell: false, goldBefore - wizard.GameStats.m_currentGold, bought);

                return;
            }
        }
    }

    // While a Sell More runs, each copy is sold without its own confirm.
    private bool _sellingMore;

    private int SellReagent(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, ulong reagentId, int quantity, out int sold) {
        sold = 0;
        var reagent = wizard.AlchemyBehavior.Reagents?.FirstOrDefault(r => r.m_globalID.Full == reagentId);
        if (reagent is null || CoreObjectFactory.GetCoreTemplate(reagent.m_templateID) is not WizItemTemplate template) {
            return 0;
        }

        var templateId = reagent.m_templateID;
        var count = Math.Min(quantity, reagent.m_quantity);
        ClientReagentItem left = null;
        for (var i = 0; i < count && wizard.RemoveReagent(reagentId, out left); i++) {
            sold++;
        }

        if (sold == 0) {
            return 0;
        }

        var gold = Math.Max(1, (int) Math.Ceiling(template.m_baseCost * 0.5f)) * sold;
        if (left is not null && left.m_quantity > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity });
        }
        else {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = reagentId });
        }

        wizard.AddGold(gold);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 2,
            ItemTemplateID = templateId,
            Cost = gold,
            ReturnCode = 0
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });

        return gold;
    }

    // While a Buy More runs, how many copies it has bought so far; NOT_BUYING_MORE otherwise.
    private const int NOT_BUYING_MORE = -1;
    private int _moreBought = NOT_BUYING_MORE;
    private bool _moreFailed;

    private void SendBuyConfirm(bool failed) {
        if (_moreBought == NOT_BUYING_MORE) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = (sbyte) (failed ? 1 : 0), WebFailure = 0, Credits = 0 });
        }
        else if (failed) {
            _moreFailed = true;
        }
        else {
            _moreBought++;
        }
    }

    // A Buy More announces only its first copy.
    private void SendItemAcquisition(GID itemId, ulong templateId) {
        if (_moreBought > 0) {
            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = itemId,
            ItemTemplateID = (uint) templateId,
            ItemLocation = 1,
        });
    }

    private void SendMoreAcknowledgement(ulong templateId, bool isSell, int totalPrice, int quantity)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEMOREACKNOWLEDGEMENT {
            ItemTemplateID = templateId,
            IsSell = (sbyte) (isSell ? 1 : 0),
            TotalPrice = totalPrice,
            QuantityPurchased = quantity,
        });

    /// <summary>
    /// Sells snacks from the snack bag to the bazaar at half their value (at least 1 gold each).
    /// </summary>
    /// <returns>The gold paid.</returns>
    private int SellSnack(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, ulong snackId, int quantity, out int sold) {
        sold = 0;
        var snack = wizard.PetSnackBehavior.Snacks?.FirstOrDefault(s => s.m_globalID.Full == snackId);
        if (snack is null || CoreObjectFactory.GetCoreTemplate(snack.m_templateID) is not WizItemTemplate template) {
            return 0;
        }

        var count = Math.Min(quantity, snack.m_quantity);
        if (count < 1 || !wizard.RemoveSnack(snackId, count, out var left)) {
            return 0;
        }

        sold = count;
        var gold = Math.Max(1, (int) Math.Ceiling(template.m_baseCost * 0.5f)) * count;
        if (left is not null && left.m_quantity > 0) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity });
        }
        else {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKREMOVE { GlobalID = wizard.GameObjectID, ItemID = snackId });
        }

        wizard.AddGold(gold);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 2,
            ItemTemplateID = snack.m_templateID,
            Cost = gold,
            ReturnCode = 0
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });

        return gold;
    }

    private void SellToAuctionHouse(ulong itemGlobalId, uint key) {
        var wizard = GetActiveWizard();
        // Snacks live in the snack bag, not the backpack.
        if (!wizard.InventoryBehavior.HasItem(itemGlobalId) && wizard.PetSnackBehavior.HasSnackID(itemGlobalId)) {
            SellSnack(wizard, itemGlobalId, 1, out _);

            return;
        }

        // Reagents live in the reagent bag.
        if (!wizard.InventoryBehavior.HasItem(itemGlobalId) && wizard.AlchemyBehavior.HasReagentID(itemGlobalId)) {
            SellReagent(wizard, itemGlobalId, 1, out _);

            return;
        }

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

        var isLocked = ItemHelper.IsLocked(item);
        if (isLocked) {
            Logger.Warning("Item lock: refused to sell {0} of {1} to the bazaar, the item is locked.",
                Logger.Args(ItemHelper.DescribeForLog(item), wizard.CharId));
        }

        var isNoAuction = isLocked || template.m_adjectiveList.Any(x => x == "FLAG_NoAuction") || ItemHelper.IsStitched(item);
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

        if (_sellingMore) {
            return;
        }

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

    // Reagents, jewels and pins in the bazaar (Niduus, for testing): every one, never sold out, for 1 gold each.
    private const int CATALOG_STOCK = 999;
    private const int CATALOG_PRICE = 1;
    private const string REAGENT_FOLDER = "ObjectData/Reagents/";
    private const string JEWEL_FOLDER = "ObjectData/Jewels/";

    // Every tab asks with an adjective: the reagent window has Reagent (all of them) and one tab per kind, the jewel
    // window one tab per shape. Seen in Niduus's log of 2026-10-19.
    private static readonly string[] s_reagentKinds = [
        "Reagent", "ReagentHarvest", "ReagentHarvestRare", "ReagentShop", "ReagentDrop", "ReagentEvent", "ReagentShard", "ReagentPet",
    ];
    private static readonly string[] s_jewelShapes = [
        "CircleJewel", "SquareJewel", "TriangleJewel", "TearJewel", "PetJewel", "PinSquareSword", "PinSquareShield", "PinSquarePip",
    ];

    private static readonly uint s_petJewelTab = StringHash.Compute("PetJewel");

    // [Bazaar] TabItems: how many reagents, jewels or pins one tab lists. Square jewels showed only the first 120
    // (Niduus, 2026-10-19), so every one is sent now.
    private static readonly int s_catalogLimit
        = Math.Clamp(Imlight.Common.ConfigurationManager.GetValue("Bazaar.TabItems", MAX_ENTRIES), 1, MAX_ENTRIES);


    private sealed record BazaarCatalog(Dictionary<ulong, AuctionHouseEntry> ById, Dictionary<uint, List<AuctionHouseEntry>> ByCategory, HashSet<ulong> NoAuction);

    private static readonly Lazy<BazaarCatalog> s_reagents = new(() => BuildCatalog(REAGENT_FOLDER, s_reagentKinds, "reagents"));

    private static readonly Lazy<BazaarCatalog> s_jewels = new(() => BuildCatalog(JEWEL_FOLDER, s_jewelShapes, "jewels and pins", keepAll: true));

    /// <summary>
    /// Lists every template in a folder under each of the given adjectives it has, lowest level first. Items the real
    /// bazaar never takes (FLAG_NoAuction) are left out, unless a tab has nothing else. With keepAll they are kept
    /// too (test items are still left out).
    /// </summary>
    private static BazaarCatalog BuildCatalog(string folder, string[] categories, string what, bool keepAll = false) {
        var byId = new Dictionary<ulong, AuctionHouseEntry>();
        var byCategory = new Dictionary<uint, List<AuctionHouseEntry>>();
        var levels = new Dictionary<ulong, int>();
        var noAuction = new Dictionary<uint, List<AuctionHouseEntry>>();
        var noAuctionIds = new HashSet<ulong>();
        var templates = TemplatesIn<WizItemTemplate>(folder)
            .Select(pair => (pair.Id, pair.Template, Adjectives: (pair.Template.m_adjectiveList ?? []).Select(adjective => adjective.ToString()).ToList()))
            .OrderBy(item => item.Adjectives.Contains("FLAG_NoAuction"))
            .ThenBy(item => item.Id);
        foreach (var (id, template, adjectives) in templates) {
            var tabs = categories.Where(adjectives.Contains).ToList();
            if (tabs.Count == 0) {
                continue;
            }

            // Every jewel and pin (Niduus, 2026-10-19: Tear had only the mana ones; the health and card ones are all
            // FLAG_NoAuction).
            if (keepAll && ((string) template.m_objectName ?? "").Contains("TEST")) {
                continue;
            }

            if (adjectives.Contains("FLAG_NoAuction")) {
                noAuctionIds.Add(id);
            }

            // Items the real bazaar never takes are kept aside, for a tab that would otherwise be empty (every pet
            // jewel is one of them).
            var target = adjectives.Contains("FLAG_NoAuction") && !keepAll ? noAuction : byCategory;
            var entry = CatalogEntry(id);
            levels[id] = RequiredLevel(template);
            foreach (var tab in tabs) {
                var hash = StringHash.Compute(tab);
                if (!target.TryGetValue(hash, out var list)) {
                    target[hash] = list = [];
                }

                list.Add(entry);
            }
        }

        foreach (var (hash, list) in noAuction) {
            if (!byCategory.ContainsKey(hash)) {
                byCategory[hash] = list;
            }
        }

        // Lowest level first: a tab lists only the first TabItems items, and in file order those were mostly
        // high level jewels and pins (pins start at level 40), none of which showed (Niduus, 2026-10-19).
        foreach (var list in byCategory.Values) {
            list.Sort((a, b) => levels[(ulong) a.m_templateID].CompareTo(levels[(ulong) b.m_templateID]) is var byLevel and not 0
                ? byLevel
                : ((ulong) a.m_templateID).CompareTo((ulong) b.m_templateID));
            foreach (var entry in list) {
                byId[(ulong) entry.m_templateID] = entry;
            }
        }

        Logger.Information("Bazaar has {0} {1}: {2}.", Logger.Args(byId.Count, what,
            string.Join(", ", categories.Select(tab => $"{tab} {(byCategory.TryGetValue(StringHash.Compute(tab), out var list) ? list.Count : 0)}"))));

        return new BazaarCatalog(byId, byCategory, noAuctionIds);
    }

    private static int RequiredLevel(WizItemTemplate template)
        => (int) ((template.m_equipRequirements?.m_requirements ?? [])
            .OfType<ReqMagicLevel>()
            .Where(requirement => !requirement.m_applyNOT)
            .Select(requirement => requirement.m_numericValue)
            .DefaultIfEmpty(0)
            .Max());

    private static AuctionHouseEntry CatalogEntry(ulong id) => new() {
        m_templateID = (GID) id,
        m_numForSale = CATALOG_STOCK,
        m_buyPrice = CATALOG_PRICE,
        m_sellPrice = CATALOG_PRICE,
    };

    private static IEnumerable<(ulong Id, T Template)> TemplatesIn<T>(string folder) where T : class {
        var found = new List<(ulong, T)>();
        try {
            foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                string file = location?.m_filename;
                if (file is not null && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && CoreObjectFactory.GetCoreTemplate(location.m_id) is T template) {
                    found.Add((location.m_id, template));
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Could not list the bazaar's {0}: {1}", Logger.Args(folder, e));
        }

        return found;
    }

    private static AuctionHouseEntry Reagent(ulong templateId)
        => s_reagents.Value.ById.TryGetValue(templateId & 0xFFFFFFFFUL, out var entry) ? entry : null;

    private static AuctionHouseEntry Jewel(ulong templateId)
        => s_jewels.Value.ById.TryGetValue(templateId & 0xFFFFFFFFUL, out var entry) ? entry : null;

    private void BuyReagent(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, AuctionHouseEntry entry) {
        var templateId = (ulong) entry.m_templateID & 0xFFFFFFFFUL;
        if (wizard.GameStats.m_currentGold < entry.m_buyPrice || !wizard.AddReagent(templateId, out var reagent)
            || !_itemSerializer.Serialize(reagent, 27, out var reagentData)) {
            SendBuyConfirm(failed: true);

            return;
        }

        // Same messages as a crown shop reagent: it goes into the reagent bag.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = wizard.GameObjectID, Data = reagentData });
        SendItemAcquisition(reagent.m_globalID, reagent.m_templateID);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
            GlobalID = wizard.GameObjectID,
            ItemID = reagent.m_globalID,
            Quantity = reagent.m_quantity,
        });

        wizard.RemoveGold(entry.m_buyPrice);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
            UpdateInfo = WriteAuctionBlob(0, [entry]),
            CharacterID = wizard.CharId
        });
        SendBuyConfirm(failed: false);
    }

    private static BazaarSnack Snack(ulong templateId)
        => s_snacks.Value.TryGetValue(templateId & 0xFFFFFFFFUL, out var snack) ? snack : null;

    private void BuySnack(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, BazaarSnack bazaarSnack) {
        var price = bazaarSnack.Entry.m_buyPrice;
        var templateId = (ulong) bazaarSnack.Entry.m_templateID & 0xFFFFFFFFUL;
        if (wizard.GameStats.m_currentGold < price || !wizard.AddSnack(templateId, out var snack)
            || !_itemSerializer.Serialize(snack, 24, out var snackData)) {
            SendBuyConfirm(failed: true);

            return;
        }

        // Same messages as .mod addsnack: the snack goes into the snack bag.
        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = snackData });
        SendItemAcquisition(snack.m_globalID, snack.m_templateID);
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
        SendBuyConfirm(failed: false);
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
        "ReagentHarvest", "ReagentHarvestRare", "ReagentShop", "ReagentDrop", "ReagentEvent", "ReagentShard", "ReagentPet",
        "CircleJewel", "SquareJewel", "TriangleJewel", "TearJewel", "PetJewel", "PinSquareSword", "PinSquareShield",
        "PinSquarePip", "PetSnack", "Cantrips", "CastleMagic", "Gardening", "CastleBlock", "Astral",
    ];
    private static readonly HashSet<uint> s_treasureCategories = [.. s_treasureCategoryNames.SelectMany(CategoryHashes)];

    private static IEnumerable<uint> CategoryHashes(string name)
        => [StringHash.Compute(name), StringHash.Compute(name.ToLowerInvariant())];

    private static bool IsTreasureCardCategory(uint category) => s_treasureCategories.Contains(category);

    private static string CategoryName(uint category)
        => Enum.IsDefined(typeof(Imlight.CoreLib.Shared.Behaviors.MagicSchool), (int) category)
            ? $"{(Imlight.CoreLib.Shared.Behaviors.MagicSchool) (int) category} treasure cards"
            : s_treasureCategoryNames.Concat(s_otherCategoryNames).FirstOrDefault(name => CategoryHashes(name).Contains(category));

    private static List<BazaarTreasureCard> FirstSchoolCards() {
        var first = s_shownTreasureCards.Value.FirstOrDefault(card => card.SchoolHash != 0);

        return first is null ? [] : [.. s_shownTreasureCards.Value.Where(card => card.SchoolHash == first.SchoolHash)];
    }

    // What the snack, reagent, jewel and pin window asks for when it opens (its name is not known).
    private const uint MIXED_CATEGORY = 1610882238;

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
            SendBuyConfirm(failed: true);

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
        SendBuyConfirm(failed: false);
    }

}
