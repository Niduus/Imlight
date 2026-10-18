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
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.CrownShop;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The crown shop: the item list, price locks and purchases with crowns or gold.
/// Every message is logged with its fields, since how the game reads the list is still being worked out.
/// </summary>
internal class CrownShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const int ERROR_NONE = 0;
    private const int ERROR_FAILED = 1;
    private const int MAX_COUNT = 99;

    private readonly CoreObjectSerializer _snackSerializer = new(behaviors: SerializerFlags.None);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CrownShopService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST))]
    private void ReceiveSegmentDataRequest(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST message) {
        Logger.Information("Crown shop: the game asked for its segment data.");
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_RESPONSE { Success = 1, Data = "" });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST))]
    private void ReceiveListRequest(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST message) {
        var list = CrownShopCatalog.PackedList;
        Logger.Information("Crown shop: the game asked for the list (update {0}, {1} bytes of data). Sending {2}, {3} bytes.",
            Logger.Args(message.UpdateID, message.Data.Length, CrownShopCatalog.Describe(), list.Length));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_LIST_RESPONSE {
            Data = list,
            Updates = "",
            UpdateID = message.UpdateID,
            Error = list.Length > 0 ? ERROR_NONE : ERROR_FAILED,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_CACHESEGREQSSUMMARY_REQUEST))]
    private void ReceiveCacheSummary(WIZARD_12_PROTOCOL.MSG_PCS_CACHESEGREQSSUMMARY_REQUEST message)
        => Logger.Information("Crown shop: segment summary (update {0}, {1} bytes).", Logger.Args(message.UpdateID, message.Data.Length));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST))]
    private void ReceiveWishlist(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST message)
        => Logger.Debug("Crown shop: wishlist update ({0} bytes).", Logger.Args(message.Data.Length));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CrownShopLogging))]
    private void ReceiveLogging(WIZARD2_53_PROTOCOL.MSG_CrownShopLogging message) {
        // The game turning its crown shop logging on or off; nothing to do.
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST))]
    private void ReceivePriceLock(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST message) {
        var price = CrownShopCatalog.GetPrice(message.Item);
        Logger.Information("Crown shop: price lock for {0} (flags {1}, sale {2}): {3}.",
            Logger.Args(message.Item, message.ItemFlags, message.SaleID, price?.ToString() ?? "not sold"));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_RESPONSE {
            Item = message.Item,
            CostGold = price?.Gold ?? 0,
            CostCrowns = price?.Crowns ?? 0,
            CostTickets = 0,
            Error = price is null ? ERROR_FAILED : ERROR_NONE,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST))]
    private void ReceivePurchase(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST message) {
        var wizard = GetActiveWizard();
        var price = CrownShopCatalog.GetPrice(message.Item);
        var count = Math.Clamp(message.Count, 1, MAX_COUNT);
        Logger.Information("Crown shop: purchase of {0} x{1} (type {2}, cost {3}, recipient {4}): {5}.",
            Logger.Args(message.Item, message.Count, message.Type, message.Cost, message.Recipient, price?.ToString() ?? "not sold"));

        if (price is null) {
            Respond(message, 0, ERROR_FAILED);
            InformGameClient("The crown shop does not sell that item.");

            return;
        }

        // Crown items cost crowns; the rest cost gold.
        var useCrowns = price.Crowns > 0 || price.Gold == 0;
        var cost = (useCrowns ? price.Crowns : price.Gold) * count;
        if (useCrowns ? wizard.Crowns < cost : wizard.GameStats.m_currentGold < cost) {
            Respond(message, cost, ERROR_FAILED);
            InformGameClient(useCrowns ? $"You need {cost} crowns for that." : $"You need {cost} gold for that.");

            return;
        }

        var templateId = (uint) (message.Item & 0xFFFFFFFFUL);
        string result = null;
        var given = 0;
        try {
            for (var i = 0; i < count; i++) {
                if (!Give(wizard, templateId, message.PetName, ref result)) {
                    break;
                }

                given++;
            }
        }
        catch (Exception e) {
            Logger.Error("Crown shop: giving {0} to {1} failed: {2}", Logger.Args(templateId, wizard.CharId, e));
        }

        if (given == 0) {
            Respond(message, 0, ERROR_FAILED);
            InformGameClient(result ?? "That item can't be bought here yet. Please send the server log.");

            return;
        }

        cost = (useCrowns ? price.Crowns : price.Gold) * given;
        if (useCrowns) {
            wizard.SetCrowns(wizard.Crowns - cost);
            if (CrownsMessage.Create(wizard.Crowns) is { } crowns) {
                SendToSocket(crowns);
            }
        }
        else {
            wizard.RemoveGold(cost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch
            });
        }

        Respond(message, cost, ERROR_NONE, given);
        if (result is not null) {
            InformGameClient(result);
        }
    }

    private void Respond(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST message, int cost, int error, int count = 0)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
            Item = message.Item,
            Type = message.Type,
            Cost = cost,
            Count = count,
            Gifted = 0,
            Error = error,
        });

    /// <summary>
    /// Gives one of an item: the Level 50 Elixir is used right away, snacks go to the snack bag, and pets and
    /// other items to the backpack.
    /// </summary>
    private bool Give(Wizard wizard, uint templateId, uint petName, ref string result) {
        if (templateId == Level50Elixir.TEMPLATE_ID) {
            result = Level50Elixir.Use(SessionActor.ActorRef, GetActiveGameObject(), wizard);

            return true;
        }

        var template = CoreObjectFactory.GetCoreTemplate(templateId);
        if (template is PetSnackItemTemplate) {
            if (!wizard.AddSnack(templateId, out var snack) || !_snackSerializer.Serialize(snack, 24, out var snackData)) {
                return false;
            }

            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = snackData });
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
                GlobalID = wizard.GameObjectID,
                ItemID = snack.m_globalID,
                Quantity = snack.m_quantity,
            });
            Acquired(snack.m_globalID, templateId);

            return true;
        }

        WizClientObjectItem item;
        if (PetFactory.IsPetTemplate(templateId)) {
            item = PetFactory.CreateHatchedPet(wizard.CharId, templateId);
            if (item is not null && petName != 0 && WizardNameBank.IsValidPetName(petName)) {
                PetFactory.TrySetPetName(item, petName);
            }

            if (item is null || !wizard.AddPetToInventory(item)) {
                return false;
            }
        }
        else if (template is WizItemTemplate) {
            if (!wizard.AddItemToInventory(templateId, out item)) {
                return false;
            }
        }
        else {
            Logger.Warning("Crown shop: template {0} is a {1}, which can't be bought yet.",
                Logger.Args(templateId, template?.GetType().Name ?? "missing template"));
            result = "That kind of item can't be bought from the crown shop yet.";

            return false;
        }

        if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, item)) {
            wizard.RemoveItemFromInventory(item.m_globalID);
            WizardItemCollection.RemoveItem(wizard.CharId, item.m_globalID);

            return false;
        }

        Acquired(item.m_globalID, templateId);

        return true;
    }

    private void Acquired(ulong globalId, uint templateId)
        => SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = globalId,
            ItemTemplateID = templateId,
            ItemLocation = 1,
        });

}
