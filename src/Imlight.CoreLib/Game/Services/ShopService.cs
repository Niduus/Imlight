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
 * SHOP SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player shop interactions, including item buying, selling, 
 * dyeing and pet renaming within the game server session.
 * 
 * USAGE EXAMPLE:
 * Internal service handling shop-related messages and transactions 
 * for player inventory and economic interactions.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.CoreObject;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Game.Badges;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Rentals;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.WizBang;

namespace Imlight.CoreLib.Game.Services;

internal class ShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float SELL_MODIFIER = 0.05f;
    private const float DYED_ITEM_COST_MULTIPLIER = 1.225f;
    // Public equipment carries each dye layer in 5 bits.
    private const int MaxDye = 31;
    private const PropertyFlags EquippedItemMask = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

    private static readonly CoreObjectSerializer s_itemSerializer = new(
        behaviors: Imcodec.ObjectProperty.SerializerFlags.None
    );

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InteractService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST))]
    private void ReceiveShopBuyRequest(WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST message) {
        // Ensure that the player has interacted with an object in the zone.
        var interactedObject = GetZoneObject(message.npcGlobalID);
        if (interactedObject is null) {
            Logger.Warning("Failed to find NPC {0} in zone for shop purchase",
                Logger.Args(message.npcGlobalID));
            var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
            SendToSocket(shopDenyMsg);

            return;
        }

        // Ensure that the interacted object is a vendor.
        var vendorComponent = interactedObject.GetComponentOfType<InteractVendorComponent>();
        if (vendorComponent is null) {
            Logger.Warning("Failed to find VendorComponent for NPC {0} in zone for shop purchase",
                Logger.Args(message.npcGlobalID));
            var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM { Failure = 1 };
            SendToSocket(shopDenyMsg);

            return;
        }

        // Ensure that the vendor has the item that the player is trying to purchase.
        var itemTemplateID = new GID(message.ShopID).MParts.TemplateId;
        if (!vendorComponent.HasItem((GID) message.ShopID)) {
            HandleIllegalPurchaseAttempt(itemTemplateID, message.npcGlobalID);

            return;
        }

        // A custom vendor's own price for the item as it is listed (a recipe's, not what it makes), or the fixed price.
        var price = vendorComponent.PriceOf(itemTemplateID);
        Logger.Information("Shop buy: item {0} (ShopID {1}) from NPC {2}, quantity {3}, currency {4}, fixed price {5}.",
            Logger.Args(itemTemplateID, message.ShopID, message.npcGlobalID, message.quantity, message.CurrencyType, price));

        // A recipe shop's window buys a recipe, which goes into the recipe book for crafting (Game/Crafting).
        if (CoreObjectFactory.GetCoreTemplate(itemTemplateID) is RecipeTemplate boughtRecipe && !Crafting.Crafting.ShopGivesItem) {
            BuyRecipe(itemTemplateID, boughtRecipe, price);

            return;
        }

        // [Crafting] RecipeShopGivesItem (or crafting turned off): the buyer gets what the recipe makes, the elixir,
        // snack or furniture, as before crafting (Niduus, 2026-10-23).
        if (CoreObjectFactory.GetCoreTemplate(itemTemplateID) is RecipeTemplate recipe) {
            var madeId = (uint) recipe.m_itemID;
            if (madeId == 0) {
                Logger.Warning("Shop buy: recipe {0} makes no item.", Logger.Args(itemTemplateID));
                SendShopDenyMessage();

                return;
            }

            Logger.Information("Shop buy: recipe {0} ({1}) gives item {2}.", Logger.Args(itemTemplateID, (string) recipe.m_recipeName, madeId));
            itemTemplateID = madeId;
        }

        var playerWizard = GetActiveWizard();
        var template = CoreObjectFactory.GetCoreTemplate(itemTemplateID) as WizItemTemplate;
        var isPet = PetFactory.IsPetTemplate(itemTemplateID);

        // Snacks go into the snack bag and reagents into the reagent bag, not the backpack (Hao and the house sellers).
        if (template is PetSnackItemTemplate or ReagentItemTemplate) {
            BuyIntoBag(playerWizard, template, price ?? (int) template.m_baseCost, message.quantity);

            return;
        }

        if (BadgeGate.Blocks(template?.m_purchaseRequirements, itemTemplateID, SessionActor.ActorRef, GetActiveGameObject(), playerWizard, SessionActor.GetZoneActor())) {
            Logger.Warning("{0} tried to buy item {1} without the badge it requires.",
                Logger.Args(playerWizard.PlayerNameBehavior.GetWizardName(), itemTemplateID));
            SendShopDenyMessage();

            return;
        }

        // The client refuses a second rental of one type; a forged or racing request gets the same answer.
        if (RentalItems.TryGetRentalTemplate(template, out var rentalTemplate) && RentalItems.OwnsType(playerWizard, rentalTemplate)) {
            Logger.Warning("{0} tried to buy rental {1} while owning a rental of the same type.",
                Logger.Args(playerWizard.PlayerNameBehavior.GetWizardName(), itemTemplateID));
            SendShopDenyMessage();

            return;
        }

        WizClientObjectItem item;
        if (isPet) {
            if (!TryCreatePurchasedPet(playerWizard, template, message, out item)) {
                SendShopDenyMessage();

                return;
            }
        } else {
            item = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(itemTemplateID);
            item.m_primaryColor = message.texture;
            item.m_secondaryColor = message.decal;
        }

        var goldCost = price ?? PriceModifiersConfig.GetShopBuyCost(template, message.texture, message.decal);

        // Check if the user can afford the item.
        if (playerWizard.GameStats.m_currentGold < goldCost) {
            SendShopDenyMessage();

            return;
        }

        // Buy More: the quantity the player picked, as many as the gold pays for. A pet is always one.
        var copies = isPet ? 1 : (int) Math.Clamp(message.quantity, 1u, MAX_COPIES);
        var bought = 0;
        while (bought < copies && playerWizard.GameStats.m_currentGold >= goldCost) {
            if (bought > 0) {
                item = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(itemTemplateID);
                item.m_primaryColor = message.texture;
                item.m_secondaryColor = message.decal;
            }

            if (!ProcessSuccessfulPurchase(playerWizard, item, itemTemplateID, goldCost, isPet, confirm: false)) {
                break;
            }

            bought++;
        }

        if (bought == 0) {
            SendShopDenyMessage();

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
        if (bought < copies) {
            InformGameClient($"Bought {bought} of {copies}: not enough gold or room for the rest.");
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST))]
    private void ReceiveShopSellRequest(WIZARD_12_PROTOCOL.MSG_SHOPSELLREQUEST message) {
        var wizard = GetActiveWizard();
        // Sell More can sell a whole stack, so only the stack size limits it (Buy More stops at MAX_COPIES).
        var quantity = (int) Math.Clamp(message.quantity, 1u, int.MaxValue);
        Logger.Information("Shop sell: item {0}, quantity {1}, quick sell {2}.", Logger.Args(message.GlobalID, message.quantity, message.IsQuickSell));

        // Sell More in the reagent and snack shops sells from the reagent bag or the snack bag.
        if (wizard.AlchemyBehavior.Reagents?.FirstOrDefault(r => r.m_globalID.Full == message.GlobalID) is { } reagent) {
            SellReagent(wizard, reagent, quantity);

            return;
        }

        if (wizard.PetSnackBehavior.Snacks?.FirstOrDefault(s => s.m_globalID.Full == message.GlobalID) is { } snack) {
            SellSnack(wizard, snack, quantity);

            return;
        }

        var item = wizard.InventoryBehavior.GetItem(message.GlobalID);
        if (item is null) {
            Logger.Warning("Failed to find item {0} in inventory for shop sell", Logger.Args(message.GlobalID));
            ProcessFailedSale();

            return;
        }

        if (ItemHelper.IsLocked(item)) {
            Logger.Warning("Item lock: refused to sell {0} of {1} to a shop, the item is locked.",
                Logger.Args(ItemHelper.DescribeForLog(item), wizard.CharId));
            ProcessFailedSale();

            return;
        }

        // A vault would be sold with what it holds.
        if (wizard.IsFilledVault(message.GlobalID)) {
            Logger.Information("Vaults: refused to sell vault {ItemId} of {CharId} to a shop, it still holds items.",
                Logger.Args(message.GlobalID, wizard.CharId));
            InformGameClient("A vault that still holds items cannot be sold. Place it in your house and empty it first.");
            ProcessFailedSale();

            return;
        }

        // Sell More on a backpack item sells that many copies of it.
        if (quantity > 1) {
            var copies = (wizard.InventoryBehavior.Items ?? [])
                .Where(other => other.m_templateID == item.m_templateID && other.m_globalID.Full != message.GlobalID
                    && !(wizard.EquipmentBehavior.EquippedItemIds?.Contains(other.m_globalID.Full) ?? false)
                    && !ItemHelper.IsLocked(other) && !wizard.IsFilledVault(other.m_globalID.Full))
                .Take(quantity - 1)
                .Select(other => other.m_globalID.Full)
                .ToList();
            foreach (var copy in copies) {
                if (wizard.RemoveItemFromInventory(copy)) {
                    ProcessSuccessfulSale(wizard, copy, CalculateItemSellValue((WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID)));
                }
            }
        }

        var removedItemSuccess = wizard.RemoveItemFromInventory(message.GlobalID);
        if (!removedItemSuccess) {
            Logger.Warning("Failed to find item {0} in inventory for shop sell", Logger.Args(message.GlobalID));
            ProcessFailedSale();

            return;
        }

        var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID);
        var gold = CalculateItemSellValue(template);

        ProcessSuccessfulSale(wizard, message.GlobalID, gold);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_DYEREQUEST))]
    private void ReceiveDyeRequest(WIZARD_12_PROTOCOL.MSG_DYEREQUEST message) {
        var wizard = GetActiveWizard();
        var item = wizard.InventoryBehavior.GetItem(message.itemGlobalID);
        var isEquipped = false;
        if (item == null) {
            // Failsafe: item may be equipped instead
            item = wizard.EquipmentBehavior.GetItem(message.itemGlobalID);

            if (item == null) {
                Logger.Error("Failed to find item {0} in inventory for dyes", Logger.Args(message.itemGlobalID));

                var dyeDenyMsg = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 };
                SendToSocket(dyeDenyMsg);

                return;
            }
            else {
                isEquipped = true;
            }
        }

        var template = ItemHelper.GetDisplayTemplate(item);
        if (template is null || !IsValidDye(item, template, message.texture, message.decal, message.decal2)) {
            Logger.Warning("Rejected dye {0}/{1}/{2} for item {3} (template {4})",
                Logger.Args(message.texture, message.decal, message.decal2, item.m_globalID, item.m_templateID.Full));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 });

            return;
        }

        var dyeCost = PriceModifiersConfig.GetDyeCost(template, message.texture, message.decal);
        if (dyeCost > wizard.GameStats.m_currentGold) {
            var dyeDenyMsg = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM { Failure = 1 };
            SendToSocket(dyeDenyMsg);
            
            return;
        }

        // Deduct the cost from the player
        wizard.RemoveGold(dyeCost);
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Apply the dyes
        var texture = (DyeColor) message.texture;
        var decal = (DyeColor) message.decal;
        var decal2 = (DyeColor) message.decal2;
        DyeMapper.ApplyAllDye(item, texture, decal, decal2);

        // If the item is equipped, update the client's local item cache with the
        // new colors IN PLACE using MSG_EQUIPMENTBEHAVIOR_EQUIPITEM. 
        if (isEquipped) {
            SendEquippedItemRefresh(wizard, item, template);
        }

        // Confirm success to the client.
        var msgConfirm = new WIZARD_12_PROTOCOL.MSG_DYECONFIRM {
            Failure = 0,
            itemGID = message.itemGlobalID,
            firstLayer = message.texture,
            secondLayer = message.decal,
            thirdLayer = message.decal2
        };
        SendToSocket(msgConfirm);

        ResummonIfEquippedPet(wizard, item);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETRENAMEREQUEST))]
    private void ReceivePetRenameRequest(WIZARD_12_PROTOCOL.MSG_PETRENAMEREQUEST message) {
        var wizard = GetActiveWizard();

        // The dye shop's pet name tab lists backpack and equipped pets alike.
        var pet = wizard.InventoryBehavior.GetItem(message.itemGlobalID)
            ?? wizard.EquipmentBehavior.GetItem(message.itemGlobalID);
        if (pet is null || !CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out _)) {
            Logger.Warning("Player tried to rename item {0}, which is not one of their pets.",
                Logger.Args(message.itemGlobalID));

            SendPetRenameDeny();

            return;
        }

        if (!WizardNameBank.IsValidPetName(message.petName)) {
            Logger.Warning("Player tried to rename pet {0} with name keys {1}, which are not in the pet name tables.",
                Logger.Args(pet.m_globalID, message.petName));

            SendPetRenameDeny();

            return;
        }

        var renameCost = PriceModifiersConfig.GetPetRenameCost();
        if (renameCost > wizard.GameStats.m_currentGold) {
            SendPetRenameDeny();

            return;
        }

        if (!WizardItemCollection.ApplyPetName(pet, message.petName)) {
            Logger.Error("Failed to save name keys {0} for pet {1}",
                Logger.Args(message.petName, pet.m_globalID));

            SendPetRenameDeny();

            return;
        }

        PetFactory.TrySetPetName(pet, message.petName);

        wizard.RemoveGold(renameCost);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });

        // The client renames its own copy of the pet on success; the confirm carries no name.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETRENAMECONFIRM { Failure = 0 });

        Logger.Information("Pet {0} renamed to {1} for {2} gold.",
            Logger.Args(pet.m_globalID, WizardNameBank.GetPetEnglishName(message.petName), renameCost));

        ResummonIfEquippedPet(wizard, pet);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_DONESHOPPING))]
    private void ReceiveDoneShopping() {
        // A wizard has complete shopping and is leaving the shop.
        var wizard = GetActiveWizard();

        if (wizard.Zone.Contains("Phantom")) {
            return;
        }

        // Reenable player movement
        var enableMovementStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE() {
            GameObjectID = wizard.GameObjectID,
            State = 1685237158,
            Data = "",
            IgnoreIfCurrentStateIsOff = 0
        };
        SendToSocket(enableMovementStateMsg);

        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG() {
            GameObjectID = wizard.GameObjectID,
            WizBangID = (uint) WizBangs.None
        };
        ZoneBroadcast(wizBangMsg, false);
    }

    private void ProcessSuccessfulSale(Wizard wizard, ulong itemID, int goldValue) {
        wizard.AddGold(goldValue);

        // Inform the game client of the successful sale.
        var updateGoldMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(updateGoldMsg);

        // Inform the game client of the successful sale.
        var removeItemMsg = new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = itemID
        };
        SendToSocket(removeItemMsg);
    }

    private void SellReagent(Wizard wizard, ClientReagentItem reagent, int quantity) {
        var reagentId = reagent.m_globalID.Full;
        var template = CoreObjectFactory.GetCoreTemplate(reagent.m_templateID) as WizItemTemplate;
        var count = Math.Min(quantity, reagent.m_quantity);
        // Take all but one straight off the stack, then the last through the bag so it saves once.
        if (count < 1) {
            ProcessFailedSale();

            return;
        }

        reagent.m_quantity -= count - 1;
        if (!wizard.RemoveReagent(reagentId, out var left)) {
            reagent.m_quantity += count - 1;
            ProcessFailedSale();

            return;
        }

        if (left is not null && left.m_quantity > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity });
        }
        else {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = reagentId });
        }

        FinishStackSale(wizard, reagentId, BagSellValue(template) * count);
    }

    private void SellSnack(Wizard wizard, ClientPetSnackItem snack, int quantity) {
        var snackId = snack.m_globalID.Full;
        var template = CoreObjectFactory.GetCoreTemplate(snack.m_templateID) as WizItemTemplate;
        var count = Math.Min(quantity, snack.m_quantity);
        if (count < 1 || !wizard.RemoveSnack(snackId, count, out var left)) {
            ProcessFailedSale();

            return;
        }

        if (left is not null && left.m_quantity > 0) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity });
        }
        else {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKREMOVE { GlobalID = wizard.GameObjectID, ItemID = snackId });
        }

        FinishStackSale(wizard, snackId, BagSellValue(template) * count);
    }

    // Reagents and snacks sell for at least 1 gold each.
    private static int BagSellValue(WizItemTemplate template)
        => template is null ? 1 : Math.Max(1, CalculateItemSellValue(template));

    private void FinishStackSale(Wizard wizard, ulong stackId, int gold) {
        wizard.AddGold(gold);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM { GlobalID = stackId, Failure = 0 });
    }

    private void ProcessFailedSale() {
        var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPSELLCONFIRM {
            Failure = 1,
        };
        SendToSocket(shopDenyMsg);
    }

    private void SendPetRenameDeny()
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETRENAMECONFIRM { Failure = 1 });

    private void SendEquippedItemRefresh(Wizard wizard, WizClientObjectItem item, WizItemTemplate template) {
        var slot = ItemHelper.GetItemSlot(template);
        if (slot is null) {
            Logger.Error("Dyed item {0} has no equipment slot", Logger.Args(item.m_globalID));

            return;
        }

        // Serialize the item with new colors for the local player's cache.
        if (!s_itemSerializer.Serialize(item, EquippedItemMask, out var localData)) {
            Logger.Error("Failed to serialize dyed item {0} for local update",
                Logger.Args(item.m_globalID));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_EQUIPITEM {
            GlobalID = wizard.GameObjectID,
            SlotName = slot.SlotType.ToString(),
            IsValid = 1,
            SerializedItem = localData
        });

        // Broadcast updated public appearance so other players see the new colors.
        var pubItem = ItemHelper.GetPublicItem(item);
        if (!s_itemSerializer.Serialize(pubItem, 1, out var pubData)) {
            Logger.Error("Failed to serialize item {0} for dye broadcast",
                Logger.Args(item.m_globalID));

            return;
        }

        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICEQUIPITEM {
            GlobalID = wizard.GameObjectID,
            SerializedInfo = pubData
        }, false);
    }

    private void ResummonIfEquippedPet(Wizard wizard, WizClientObjectItem item) {
        // A summoned pet carries its look and name from when it was summoned.
        if (wizard.EquipmentBehavior.GetEquippedPetId() == item.m_globalID) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = item.m_globalID });
        }
    }

    private static bool IsValidDye(WizClientObjectItem item, WizItemTemplate template, int texture, int decal, int decal2) {
        if (!IsDyeInRange(texture) || !IsDyeInRange(decal) || !IsDyeInRange(decal2)) {
            return false;
        }

        if (!PetFactory.IsPetTemplate(template.m_templateID)) {
            return true;
        }

        // A pet's colors are its template's own texture choices, so no dye beyond them is offered.
        return IsPetLayerDye(texture, item.m_primaryColor, template.m_numPrimaryColors)
            && IsPetLayerDye(decal, item.m_secondaryColor, template.m_numSecondaryColors)
            && IsPetLayerDye(decal2, item.m_pattern, template.m_numPatterns);
    }

    private static bool TryCreatePurchasedPet(Wizard wizard, WizItemTemplate template,
                                              WIZARD_12_PROTOCOL.MSG_SHOPBUYREQUEST message, out WizClientObjectItem pet) {
        pet = PetFactory.CreateHatchedPet(wizard.CharId, template.m_templateID);
        if (pet is null) {
            return false;
        }

        if (!IsValidDye(pet, template, message.texture, message.decal, message.decal2)) {
            Logger.Warning("Rejected dye {0}/{1}/{2} for the purchase of pet template {3}",
                Logger.Args(message.texture, message.decal, message.decal2, template.m_templateID));

            return false;
        }

        pet.m_primaryColor = message.texture;
        pet.m_secondaryColor = message.decal;
        pet.m_pattern = message.decal2;

        // The name keys are zero when the client did not open the naming window, which keeps the factory default.
        if (message.petName == 0) {
            return true;
        }

        if (!WizardNameBank.IsValidPetName(message.petName) || !PetFactory.TrySetPetName(pet, message.petName)) {
            Logger.Warning("Rejected name keys {0} for the purchase of pet template {1}",
                Logger.Args(message.petName, template.m_templateID));

            return false;
        }

        return true;
    }

    private static bool IsDyeInRange(int dye) => dye is >= 0 and <= MaxDye;

    private static bool IsPetLayerDye(int dye, int currentDye, int templateColorCount)
        => dye == currentDye || (templateColorCount > 1 && dye < templateColorCount);

    private void BuyRecipe(uint recipeId, RecipeTemplate recipe, int? price) {
        var wizard = GetActiveWizard();
        var name = Crafting.Crafting.NameOf(recipeId, recipe);
        if (wizard.AlchemyBehavior?.RecipeIds?.Contains(recipeId) == true) {
            name = Crafting.Crafting.ShownNameOf(recipeId, recipe);
            InformGameClient($"You already know the recipe {name}.");
            SendShopDenyMessage();

            return;
        }

        if (Crafting.RecipeChanges.RefusalOf(recipeId, name, wizard.MagicSchoolBehavior?.Level ?? 1) is { } refused) {
            InformGameClient(refused);
            SendShopDenyMessage();

            return;
        }

        if (BadgeGate.Blocks(recipe.m_purchaseRequirements, recipeId, SessionActor.ActorRef, GetActiveGameObject(), wizard, SessionActor.GetZoneActor())) {
            Logger.Warning("{0} tried to buy recipe {1} without the badge it requires.", Logger.Args(wizard.CharId, recipeId));
            SendShopDenyMessage();

            return;
        }

        var cost = Math.Max(0, price ?? Crafting.RecipeChanges.GoldCostOf(recipeId, recipe.m_goldCost));
        if (wizard.GameStats.m_currentGold < cost) {
            SendShopDenyMessage();

            return;
        }

        if (!Crafting.Crafting.Learn(SessionActor.ActorRef, wizard, recipeId)) {
            SendShopDenyMessage();

            return;
        }

        if (cost > 0) {
            wizard.RemoveGold(cost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        }

        Logger.Information("Shop buy: {0} learned recipe {1} ({2}) for {3} gold.", Logger.Args(wizard.CharId, recipeId, name, cost));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
    }

    private void SendShopDenyMessage() {
        var shopDenyMsg = new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM {
            Failure = 1,
            WebFailure = 0,
            Credits = 0
        };
        SendToSocket(shopDenyMsg);
    }

    // The most copies one Buy More request buys.
    private const uint MAX_COPIES = 99;

    /// <summary>
    /// Buys a snack into the snack bag or a reagent into the reagent bag, the same messages the bazaar sends.
    /// </summary>
    private void BuyIntoBag(Wizard wizard, WizItemTemplate template, int goldCost, uint quantity) {
        var templateId = (ulong) template.m_templateID;
        var copies = (int) Math.Clamp(quantity, 1u, MAX_COPIES);
        var bought = 0;
        while (bought < copies && wizard.GameStats.m_currentGold >= goldCost) {
            if (template is PetSnackItemTemplate) {
                if (!wizard.AddSnack(templateId, out var snack) || !s_itemSerializer.Serialize(snack, 24, out var snackData)) {
                    break;
                }

                SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD { GlobalID = wizard.GameObjectID, Data = snackData });
                SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = wizard.GameObjectID, ItemID = snack.m_globalID, Quantity = snack.m_quantity });
                if (bought == 0) {
                    SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION { ItemGlobalID = snack.m_globalID, ItemTemplateID = (uint) templateId, ItemLocation = 1 });
                }
            }
            else {
                if (!wizard.AddReagent(templateId, out var reagent) || !s_itemSerializer.Serialize(reagent, 27, out var reagentData)) {
                    break;
                }

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTADD { GlobalID = wizard.GameObjectID, Data = reagentData });
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = reagent.m_globalID, Quantity = reagent.m_quantity });
                if (bought == 0) {
                    SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION { ItemGlobalID = reagent.m_globalID, ItemTemplateID = (uint) templateId, ItemLocation = 1 });
                }
            }

            wizard.RemoveGold(goldCost);
            bought++;
        }

        if (bought == 0) {
            SendShopDenyMessage();

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD { Gold = wizard.GameStats.m_currentGold, MaxGold = wizard.GameStats.m_baseGoldPouch });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
        if (bought < copies) {
            InformGameClient($"Bought {bought} of {copies}: not enough gold or room for the rest.");
        }
    }

    private bool ProcessSuccessfulPurchase(Wizard playerWizard, WizClientObjectItem item, uint itemTemplateID, int goldCost,
                                           bool isPet, bool confirm = true) {
        var added = isPet ? playerWizard.AddPetToInventory(item) : playerWizard.AddItemToInventory(item);
        if (!added) {
            Logger.Warning("Could not add purchased item {0} to the inventory of {1}.",
                Logger.Args(itemTemplateID, playerWizard.CharId));
            if (confirm) {
                SendShopDenyMessage();
            }

            return false;
        }

        // Inform the game client that a new item has been added to the player's inventory.
        if (!ItemSync.SendAdded(SessionActor.ActorRef, playerWizard, item)) {
            playerWizard.RemoveItemFromInventory(item.m_globalID);
            WizardItemCollection.RemoveItem(playerWizard.CharId, item.m_globalID);
            if (confirm) {
                SendShopDenyMessage();
            }

            return false;
        }

        playerWizard.RemoveGold(goldCost);

        // Inform the game client that the player has acquired a new item.
        var itemAcqMsg = new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = item.m_globalID,
            ItemTemplateID = itemTemplateID,
            ItemLocation = 1,
        };
        SendToSocket(itemAcqMsg);

        // Inform the game client that the player's gold has been updated.
        var goldUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = playerWizard.GameStats.m_currentGold,
            MaxGold = playerWizard.GameStats.m_baseGoldPouch
        };
        SendToSocket(goldUpdateMsg);

        // Inform the game client that the purchase was successful.
        if (confirm) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOPBUYCONFIRM());
        }

        return true;
    }

    private void HandleIllegalPurchaseAttempt(uint itemTemplateID, ulong interactedObjectGID) {
        var account = GetActiveAccount();
        SendShopDenyMessage();

        var infractionText = $"Player tried to purchase item {itemTemplateID} from NPC {interactedObjectGID} that is not in its inventory!";
        account.AddInfraction(WizardData.Models.Misc.InfractionType.SuspiciousBehavior, infractionText);

        Logger.Warning("Player tried to purchase item {0} from an NPC that it did not have in its inventory. "
            + "This has been logged as suspicious behavior.",
            Logger.Args(itemTemplateID));
    }

    private static int CalculateItemSellValue(WizItemTemplate template) {
        var goldValue = (int) Math.Ceiling(template.m_baseCost * SELL_MODIFIER);
        if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
            goldValue = (int) Math.Ceiling(goldValue * DYED_ITEM_COST_MULTIPLIER) + 1;
        }

        return goldValue;
    }

}
