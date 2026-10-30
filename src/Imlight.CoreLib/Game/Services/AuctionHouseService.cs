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
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.Bazaar;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Game.Trading;

namespace Imlight.CoreLib.Game.Services;

internal class AuctionHouseService(SessionActor sessionActor) : MessageService(sessionActor) {

    private readonly CoreObjectSerializer _itemSerializer = new(
        behaviors: SerializerFlags.None
    );


    // The game's list: the tab's category (0 for an update), how many entries, then the 20-byte entries (id with the
    // bazaar's type bits, stock, buy price, sell price), written by the Bazaar (BazaarBook.Records).
    private static ByteString WriteAuctionBlob(uint categoryHash, ReadOnlySpan<byte> records) {
        var writer = new BitWriter();
        writer.WriteUInt32(categoryHash);
        writer.WriteUInt32((uint) (records.Length / BazaarBook.RECORD_BYTES));
        var blob = writer.GetData();
        var all = new byte[blob.Length + records.Length];
        blob.CopyTo(all, 0);
        records.CopyTo(all.AsSpan(blob.Length));

        return all;
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
                // LOCK_SELL_PRICE: the game asks what an item sells for and shows "Are you sure...?"; Yes sends 4.
                // The game also asks again by itself when the backpack changes, so this never sells
                // (Niduus, 2026-10-28: selling sold before Yes, and Yes bought a card back).
                LockSellPrice(message.itemGlobalID, message.itemTemplateID);
                break;
            case 3:
                BuyFromAuctionHouse(message.itemTemplateID, message.texture, message.decal, message.key);
                break;
            case 4:
                // SELL_TO_AUCTIONHOUSE: the Yes of the sell question. A treasure card comes with only its card id.
                Logger.Information("Bazaar: sell {0} (template {1}).", Logger.Args(message.itemGlobalID, message.itemTemplateID));
                SellToAuctionHouse(message.itemGlobalID != 0 ? message.itemGlobalID
                    : (message.itemTemplateID & ~0xFFFFFFFFUL) == CARD_TYPE ? message.itemTemplateID : _lockedSellId, message.key);
                break;
            case 5:
            case 7:
            case 8:
                // Buy More is 7 (template only) and Sell More is 8 (with the held item's id), per Niduus's log. The
                // request is matched by what its ids name: a snack or an item the player holds is sold, a bazaar
                // entry is bought.
                // Every field, to find where the game puts the number asked for (quantity was 0 in Niduus's log of 2026-10-27).
                Logger.Information("Bazaar: more request {0} (template {1}, item {2}, quantity {3}, type {4}, category {5}, key {6}, " +
                                   "texture {7}, decal {8}, decal2 {9}, pet name {10}, backpack buddy {11}).",
                    Logger.Args(message.Command, message.itemTemplateID, message.itemGlobalID, message.quantity, message.itemType, message.category,
                        message.key, message.texture, message.decal, message.decal2, message.petName, message.BackpackBuddy));
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
        Logger.Information("Bazaar: category {0} requested.", Logger.Args(category));
        if (BazaarLink.Opening) {
            // The Bazaar bot is still reading the game files (about a minute after a server start).
            InformGameClient("Elik is still setting up the Bazaar. Try again in a minute.");
            SendList(npcId, category, []);

            return;
        }

        var records = BazaarLink.Records(category, s_sendEverything);
        Logger.Debug("Bazaar: sending {0} entries.", Logger.Args(records.Length / BazaarBook.RECORD_BYTES));
        SendList(npcId, category, records);
    }

    // What MSG_AUCTIONHOUSECONTENTS' LastSegment tells the game (read from its Bazaar window, 2026-10-27). The game keeps
    // the bytes of the list it was sent and reads the entries from the start of them:
    //   LAST_ONLY (99): these bytes are the whole list; replace what it had and show it.
    //   FIRST_PART (1): replace what it had with these bytes, and wait for more.
    //   MORE_PART (2): add these bytes to what it has, and wait for more.
    //   LAST_PART (0): add these bytes to what it has and show the list.
    // Every list used to go with 0, so each answer was added after the first one and the game read the first one again:
    // every tab showed the hats (Niduus, 2026-10-27: "Bazaar is only rendering hats").
    private const sbyte LAST_ONLY = 99;
    private const sbyte FIRST_PART = 1;
    private const sbyte MORE_PART = 2;
    private const sbyte LAST_PART = 0;

    // A message's Contents is written with a 16-bit length; a bigger list goes in parts, cut anywhere (the game joins the
    // bytes), with one header for the whole list.
    private const int PART_BYTES = 60_000;

    private void SendList(ulong npcId, uint category, ReadOnlySpan<byte> records) {
        byte[] list = WriteAuctionBlob(category, records);
        if (list.Length <= PART_BYTES) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS { Contents = list, GlobalID = npcId, LastSegment = LAST_ONLY });

            return;
        }

        for (var from = 0; from < list.Length; from += PART_BYTES) {
            var length = Math.Min(PART_BYTES, list.Length - from);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSECONTENTS {
                Contents = list.AsSpan(from, length).ToArray(),
                GlobalID = npcId,
                LastSegment = from == 0 ? FIRST_PART : from + length == list.Length ? LAST_PART : MORE_PART,
            });
        }
    }

    // Sent right after a tab's list arrives: the tab's own entries again, as an update, give the game their stock.
    private void SendStock(uint category) {
        if (!BazaarLink.Opening) {
            SendUpdates(BazaarLink.Records(category, everything: false));
        }
    }

    private void SendUpdates(byte[] records) {
        var perUpdate = UPDATE_ENTRIES * BazaarBook.RECORD_BYTES;
        for (var from = 0; from < records.Length; from += perUpdate) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_AUCTIONHOUSEUPDATE {
                UpdateInfo = WriteAuctionBlob(0, records.AsSpan(from, Math.Min(perUpdate, records.Length - from))),
                CharacterID = GetActiveWizard().CharId
            });
        }
    }

    // Tells the game how many are left of one entry, after a buy or a sell.
    private void SendStockOf(ulong templateId)
        => SendUpdates(BazaarLink.RecordsOf([templateId & 0xFFFFFFFFUL]));

    private const int UPDATE_ENTRIES = 800;

    // [Bazaar] SendEverything: every tab gets everything the Bazaar sells, its own items first. Off (the default) answers
    // each tab with its own items: every tab asks with its adjective when it is clicked (Hat, Robe, Reagent, SquareJewel,
    // Furniture...; a school's name for treasure cards: Fire, Astral, Shadow, Monstrology, Cantrips, Gardening...).
    private static bool s_sendEverything => Imlight.Common.ConfigurationManager.GetValue("Bazaar.SendEverything", false);

    private static string WizardName(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard)
        => wizard?.PlayerNameBehavior?.GetWizardName() is { Length: > 0 } name ? name : $"wizard {wizard?.CharId}";

    private void ConfirmBuyFromAuctionHouse(ulong templateId, uint key) {
        if (BazaarLink.Find(templateId) is not { } quote) {
            // todo: figure out correct response, should inform player the transaction failed and reload content.
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 1,
            ItemTemplateID = templateId,
            Cost = quote.BuyPrice,
            ReturnCode = 0
        });
    }

    private void BuyFromAuctionHouse(ulong templateId, int texture, int decal, uint key) {
        var wizard = GetActiveWizard();
        if (BazaarLink.Find(templateId) is not { } quote) {
            Logger.Warning("Bazaar: {0} is not sold here.", Logger.Args(templateId));
            SendBuyConfirm(failed: true);

            return;
        }

        if (quote.Stock < 1 || wizard.GameStats.m_currentGold < quote.BuyPrice) {
            SendBuyConfirm(failed: true);

            return;
        }

        if (quote.Kind is BazaarKind.Card or BazaarKind.Snack or BazaarKind.Reagent) {
            SendBuyConfirm(failed: BuyStack(wizard, quote, 1) == 0);

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
            SendBuyConfirm(failed: true);

            return;
        }

        // A full backpack denies the buy before the stock or the gold is touched. A jewel goes in the jewel pouch.
        if (BankRules.TakesBackpackSlot(item) && wizard.BackpackFreeSlots < 1) {
            SendBuyConfirm(failed: true);

            return;
        }

        if (!BazaarLink.Take(templateId, 1, WizardName(wizard), quote.BuyPrice)) {
            // Sold out in the meantime.
            SendBuyConfirm(failed: true);

            return;
        }

        if (!wizard.AddItemToInventory(item)) {
            Logger.Warning("Could not add bazaar item {0} to the inventory of {1}, so the purchase is denied.",
                Logger.Args(templateId, wizard.CharId));
            BazaarLink.PutBack(templateId);
            SendBuyConfirm(failed: true);

            return;
        }

        SendStockOf(templateId);

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
        wizard.RemoveGold(quote.BuyPrice);
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Confirm transaction.
        SendBuyConfirm(failed: false);
    }


    // The most copies one Buy More or Sell More request moves.
    private const uint MAX_MORE = 999;

    private void BuyOrSellMore(WIZARD_12_PROTOCOL.MSG_AUCTIONHOUSEREQUEST message) {
        var wizard = GetActiveWizard();
        var quantity = (int) Math.Clamp(message.quantity, 1u, MAX_MORE);
        // Buy More (7) never sells. A treasure card's bazaar id is the same as the id of the copies in the book, so Buy
        // More of a card the player already had sold those copies instead (Niduus, 2026-10-30: Epic and Elucidate
        // cards that then would not go in the deck).
        var buying = message.Command == 7;
        foreach (var id in new[] { message.itemGlobalID, message.itemTemplateID }.Where(id => id != 0)) {
            if (!buying) {
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
                    var sold = SellItems(wizard, copies);
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = (sbyte) (sold > 0 ? 0 : 1) });
                    SendMoreAcknowledgement(templateId, isSell: true, wizard.GameStats.m_currentGold - goldBefore, sold);

                    return;
                }

                // Treasure cards are in the treasure book, by their card id (0x60 << 40 on top of the spell template).
                if (SellCards(wizard, id, quantity, out var cardsSold) is var cardGold && cardsSold > 0) {
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });
                    SendMoreAcknowledgement(id, isSell: true, cardGold, cardsSold);

                    return;
                }

                // Sell More (8) never buys: an id the player does not hold is not sold.
                if (message.Command == 8) {
                    Logger.Information("Bazaar: Sell More of {0}, which the player does not hold.", Logger.Args(id));

                    continue;
                }
            }

            if (BazaarLink.Find(id) is { Kind: BazaarKind.Card or BazaarKind.Snack or BazaarKind.Reagent } stackQuote) {
                // Cards, snacks and reagents go in all at once: one take from the stock and one save, so the game
                // gets its answer right away instead of after every copy is saved (Niduus, 2026-10-28).
                var goldBefore = wizard.GameStats.m_currentGold;
                var bought = BuyStack(wizard, stackQuote, quantity);
                Logger.Information("Bazaar: {0} bought {1} of {2} x {3}.",
                    Logger.Args(WizardName(wizard), bought, quantity, stackQuote.Name));
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = (sbyte) (bought > 0 ? 0 : 1), WebFailure = 0, Credits = 0 });
                SendMoreAcknowledgement(id, isSell: false, goldBefore - wizard.GameStats.m_currentGold, bought);
                if (bought > 0 && bought < quantity) {
                    InformGameClient($"Bought {bought} of {quantity}: not enough gold, stock or room for the rest.");
                }

                return;
            }

            if (BazaarLink.Find(id) is not null) {
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

    private int SellReagent(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, ulong reagentId, int quantity, out int sold) {
        sold = 0;
        var reagent = wizard.AlchemyBehavior.Reagents?.FirstOrDefault(r => r.m_globalID.Full == reagentId);
        if (reagent is null || CoreObjectFactory.GetCoreTemplate(reagent.m_templateID) is not WizItemTemplate template) {
            return 0;
        }

        var templateId = reagent.m_templateID;
        var count = Math.Min(quantity, reagent.m_quantity);

        // The whole count comes off the stack in one save (Niduus, 2026-10-28: selling as quick as buying).
        if (count < 1 || !wizard.RemoveReagents(reagentId, count, out var left)) {
            return 0;
        }

        sold = count;

        var gold = Math.Max(1, (int) Math.Ceiling(template.m_baseCost * 0.5f)) * sold;
        BazaarLink.Give(templateId, sold, WizardName(wizard), gold);
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
        BazaarLink.Give(snack.m_templateID, count, WizardName(wizard), gold);
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
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });

        return gold;
    }

    /// <summary>
    /// Sells backpack items of one kind to the Bazaar in one go: removed and saved once, one stock change and one
    /// gold change, so a big Sell More answers right away (Niduus, 2026-10-28). Sends no sell confirm.
    /// </summary>
    /// <returns>How many were sold.</returns>
    private int SellItems(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, List<ulong> itemIds) {
        var first = itemIds.Count > 0 ? wizard.InventoryBehavior.GetItem(itemIds[0]) : null;
        if (first is null || CoreObjectFactory.GetCoreTemplate(first.m_templateID) is not WizItemTemplate template
            || template.m_adjectiveList.Any(x => x == "FLAG_NoAuction")) {
            return 0;
        }

        var sellable = itemIds.Where(id => wizard.InventoryBehavior.GetItem(id) is { } item
                && !ItemHelper.IsLocked(item) && !ItemHelper.IsStitched(item) && !wizard.IsFilledVault(id))
            .ToList();
        var sold = wizard.RemoveItemsFromInventory(sellable, "sold at the Bazaar");
        if (sold.Count == 0) {
            return 0;
        }

        // Bazaar buys items at 50% of their value.
        var each = (int) Math.Ceiling(template.m_baseCost * 0.5f);
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            each = (int) Math.Ceiling(each * 1.225f);
        }

        var gold = each * sold.Count;
        foreach (var id in sold) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = id });
        }

        BazaarLink.Give(first.m_templateID, sold.Count, WizardName(wizard), gold);
        SendStockOf(first.m_templateID);
        wizard.AddGold(gold);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        return sold.Count;
    }

    // The type bits the game puts on a treasure card's id in the Bazaar.
    private const ulong CARD_TYPE = 0x60UL << 40;

    /// <summary>
    /// Sells treasure cards from the treasure book to the Bazaar: taken out and saved once (Niduus, 2026-10-28).
    /// Sends no sell confirm.
    /// </summary>
    /// <returns>The gold paid.</returns>
    private int SellCards(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, ulong cardId, int quantity, out int sold) {
        sold = 0;
        if ((cardId & ~0xFFFFFFFFUL) != CARD_TYPE) {
            return 0;
        }

        var templateId = (uint) (cardId & 0xFFFFFFFFUL);
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not SpellTemplate spell) {
            return 0;
        }

        while (sold < quantity && wizard.SpellbookBehavior.RemoveTreasureCard(templateId)) {
            sold++;
        }

        if (sold == 0) {
            return 0;
        }

        WizardCollection.RemoveTreasureCards(wizard, templateId, sold);
        Logger.Information("Bazaar: {0} sold {1} of {2} x {3} card.",
            Logger.Args(WizardName(wizard), sold, quantity, (string) spell.m_name));
        var spellId = (int) StringHash.Compute(spell.m_name);
        for (var i = 0; i < sold; i++) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK { SpellID = spellId, EnchantmentID = 0 });
        }

        var gold = CardSellPrice(cardId) * sold;
        BazaarLink.Give(cardId, sold, WizardName(wizard), gold);
        SendStockOf(cardId);
        wizard.AddGold(gold);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        return gold;
    }

    // The item the game last asked the sell price of, for a sell (4) that names no item.
    private ulong _lockedSellId;

    /// <summary>
    /// Answers the game's "what does this sell for" (command 2) with the price of one, or a refusal. Sells nothing.
    /// </summary>
    private void LockSellPrice(ulong itemGlobalId, ulong templateId) {
        var wizard = GetActiveWizard();
        var id = itemGlobalId != 0 ? itemGlobalId : templateId;
        _lockedSellId = id;
        ulong shownTemplate = templateId;
        var price = 0;
        if (wizard.InventoryBehavior.GetItem(id) is { } item) {
            shownTemplate = item.m_templateID;
            if (CoreObjectFactory.GetCoreTemplate(item.m_templateID) is WizItemTemplate template
                && !ItemHelper.IsLocked(item) && !wizard.IsFilledVault(id) && !ItemHelper.IsStitched(item)
                && !template.m_adjectiveList.Any(x => x == "FLAG_NoAuction")) {
                price = (int) Math.Ceiling(template.m_baseCost * 0.5f);
                if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
                    price = (int) Math.Ceiling(price * 1.225f);
                }
            }
        }
        else if (wizard.PetSnackBehavior.Snacks?.FirstOrDefault(snack => snack.m_globalID.Full == id) is { } snack) {
            shownTemplate = snack.m_templateID;
            price = BagPrice(snack.m_templateID);
        }
        else if (wizard.AlchemyBehavior.Reagents?.FirstOrDefault(reagent => reagent.m_globalID.Full == id) is { } reagent) {
            shownTemplate = reagent.m_templateID;
            price = BagPrice(reagent.m_templateID);
        }
        else if ((id & ~0xFFFFFFFFUL) == CARD_TYPE && wizard.SpellbookBehavior.TreasureCardCount((uint) (id & 0xFFFFFFFFUL)) > 0) {
            shownTemplate = id;
            price = CardSellPrice(id);
        }
        else {
            // Gone already (the game asks again after a sale): nothing to answer.
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_AUCTIONRESPONSE {
            Command = 2,
            ItemTemplateID = shownTemplate,
            Cost = price,
            ReturnCode = (sbyte) (price > 0 ? 0 : 1),
        });
    }

    // Snacks and reagents sell for half their value, at least 1 gold.
    private static int BagPrice(ulong templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template ? Math.Max(1, (int) Math.Ceiling(template.m_baseCost * 0.5f)) : 1;

    private static int CardSellPrice(ulong cardId) {
        var each = BazaarLink.Find(cardId)?.SellPrice ?? 0;
        if (each < 1 && CoreObjectFactory.GetCoreTemplate((uint) (cardId & 0xFFFFFFFFUL)) is SpellTemplate spell) {
            each = (int) Math.Ceiling(spell.m_baseCost * 0.5f);
        }

        return Math.Max(1, each);
    }

    private void SellToAuctionHouse(ulong itemGlobalId, uint key) {
        var wizard = GetActiveWizard();
        // Treasure cards live in the treasure book.
        if (!wizard.InventoryBehavior.HasItem(itemGlobalId) && SellCards(wizard, itemGlobalId, 1, out _) > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 0 });

            return;
        }

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
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 1 });
            return;
        }

        var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID);

        var isLocked = ItemHelper.IsLocked(item);
        if (isLocked) {
            Logger.Warning("Item lock: refused to sell {0} of {1} to the bazaar, the item is locked.",
                Logger.Args(ItemHelper.DescribeForLog(item), wizard.CharId));
        }

        // A vault would be sold with what it holds.
        var isFilledVault = wizard.IsFilledVault(item.m_globalID);
        if (isFilledVault) {
            Logger.Information("Vaults: refused to sell vault {ItemId} of {CharId} to the bazaar, it still holds items.",
                Logger.Args(item.m_globalID.Full, wizard.CharId));
            InformGameClient("A vault that still holds items cannot be sold.");
        }

        var isNoAuction = isLocked || isFilledVault || template.m_adjectiveList.Any(x => x == "FLAG_NoAuction") || ItemHelper.IsStitched(item);
        if (isNoAuction) { // The item cannot be sold to the bazaar.
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { ClientRequestID = 0, GlobalID = 0, Failure = 1 });
            return;
        }

        // Calculate gold sell value.
        var gold = (int) Math.Ceiling(template.m_baseCost * 0.5f); // Bazaar buys items at 50% of their value.
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            gold = (int) Math.Ceiling(gold * 1.225f);
        }

        // Remove item from inventory, then put it up in the Bazaar (the Bazaar bot keeps the stock).
        _ = wizard.RemoveItemFromInventory(itemGlobalId, reason: "sold at the Bazaar");
        BazaarLink.Give(item.m_templateID, 1, WizardName(wizard), gold);
        SendStockOf(item.m_templateID);

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


    /// <summary>
    /// Buys up to <paramref name="count"/> of a card, snack or reagent: as many as the gold, the stock and the stack
    /// room allow, taken from the stock in one go and saved once. Sends no buy confirm.
    /// </summary>
    /// <returns>How many were bought.</returns>
    private int BuyStack(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard, BazaarQuote quote, int count) {
        var templateId = quote.Kind == BazaarKind.Card ? quote.Id : quote.Id & 0xFFFFFFFFUL;
        var n = Math.Min(count, quote.Stock);
        if (quote.BuyPrice > 0) {
            n = Math.Min(n, wizard.GameStats.m_currentGold / quote.BuyPrice);
        }

        // The game's treasure book holds 999 cards (Castle Magic aside) and drops any past that, so the server would
        // hold cards the game does not show (Niduus, 2026-10-28: Elucidate cards that would not stay in the deck).
        if (quote.Kind == BazaarKind.Card && TreasureCardTradePolicy.CountsTowardCapacity(quote.SpellTemplateId)) {
            n = Math.Min(n, TradeLimits.TreasureBookCapacity
                - TreasureCardTradePolicy.CountCapacityUsed(wizard.SpellbookBehavior.TreasureCardTemplateIds ?? []));
        }

        n = quote.Kind switch {
            BazaarKind.Snack => Math.Min(n, ServerPetSnackBehavior.MaxStack - (wizard.PetSnackBehavior.GetSnack(templateId)?.m_quantity ?? 0)),
            BazaarKind.Reagent => Math.Min(n, ServerAlchemyBehavior.MaxStack - (wizard.AlchemyBehavior.GetReagent(templateId)?.m_quantity ?? 0)),
            _ => n,
        };
        if (n < 1 || (quote.Kind == BazaarKind.Card && string.IsNullOrEmpty(quote.SpellName))
            || !BazaarLink.Take(templateId, n, WizardName(wizard), quote.BuyPrice * n)) {
            return 0;
        }

        int added;
        switch (quote.Kind) {
            case BazaarKind.Reagent: {
                added = wizard.AddReagents(templateId, n, out var reagent);
                if (added < 1 || !_itemSerializer.Serialize(reagent, 27, out var reagentData)) {
                    BazaarLink.PutBack(templateId, n);

                    return 0;
                }

                // Same messages as a crown shop reagent: it goes into the reagent bag.
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = wizard.GameObjectID, Data = reagentData });
                SendItemAcquisition(reagent.m_globalID, reagent.m_templateID);
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
                    GlobalID = wizard.GameObjectID,
                    ItemID = reagent.m_globalID,
                    Quantity = reagent.m_quantity,
                });
                break;
            }
            case BazaarKind.Snack: {
                added = wizard.AddSnacks(templateId, n, out var snack);
                if (added < 1 || !_itemSerializer.Serialize(snack, 24, out var snackData)) {
                    BazaarLink.PutBack(templateId, n);

                    return 0;
                }

                // Same messages as .mod addsnack: the snack goes into the snack bag.
                SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = snackData });
                SendItemAcquisition(snack.m_globalID, snack.m_templateID);
                SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
                    GlobalID = wizard.GameObjectID,
                    ItemID = snack.m_globalID,
                    Quantity = snack.m_quantity,
                });
                break;
            }
            default: {
                // Same as buying from a treasure card shop: the cards go into the treasure book and are saved once.
                var spellId = (int) StringHash.Compute(quote.SpellName);
                for (var i = 0; i < n; i++) {
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK { SpellID = spellId, EnchantmentID = 0 });
                    wizard.SpellbookBehavior.AddTreasureCard(quote.SpellTemplateId);
                }

                WizardCollection.AddTreasureCards(wizard, quote.SpellTemplateId, n);
                added = n;
                break;
            }
        }

        if (added < n) {
            BazaarLink.PutBack(templateId, n - added);
        }

        wizard.RemoveGold(quote.BuyPrice * added);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendStockOf(quote.Kind == BazaarKind.Card ? quote.Id : templateId);

        return added;
    }

}
