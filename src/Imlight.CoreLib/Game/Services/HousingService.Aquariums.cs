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
 * HOUSING SERVICE: AQUARIUMS
 * ========================================================================
 *
 * PURPOSE:
 * Does what the game's aquarium window asks: moves a fish from the fish basket into an aquarium placed in the house
 * (MSG_ADDFISHTOAQUARIUM) and back (MSG_REMOVEFISHFROMAQUARIUM).
 *
 * NOTE:
 * - The window (HousingAquariumWindow) opens when the tank is clicked and lists the basket's fish that fit: the tank's
 *   type and size class (Game/Housing/HouseVaults). It sends the tank's global id, the fish template and its size, and
 *   waits for the tank's MSG_PATCHAQUARIUM, which every player in the house gets and which shows the fish swimming.
 * - Every move is one write: the house with the tank and the owner's fish basket together (HouseCollection.SaveWithFishing).
 *   The basket changes in memory first and is put back when the write fails, so a fish is never lost or doubled.
 * - Only the owner of the house uses its aquariums, like its vaults. A tank keeps its fish when it is picked up.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Fishing;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class HousingService {

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_ADDFISHTOAQUARIUM))]
    private void ReceiveAddFishToAquarium(WIZARDHOUSING_50_PROTOCOL.MSG_ADDFISHTOAQUARIUM message) {
        Logger.Information("Aquariums: character {CharId} asks to put fish {TemplateId} size {Size} in aquarium {TankGid}.",
            Logger.Args(GetActiveWizard().CharId, message.FishTemplateID, message.Size, message.GlobalID));
        RunVaultRequest("put a fish in an aquarium", () => MoveFishIn(message.GlobalID, message.FishTemplateID, message.Size));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REMOVEFISHFROMAQUARIUM))]
    private void ReceiveRemoveFishFromAquarium(WIZARDHOUSING_50_PROTOCOL.MSG_REMOVEFISHFROMAQUARIUM message) {
        Logger.Information("Aquariums: character {CharId} asks to take fish {TemplateId} size {Size} out of aquarium {TankGid}.",
            Logger.Args(GetActiveWizard().CharId, message.FishTemplateID, message.Size, message.GlobalID));
        RunVaultRequest("take a fish out of an aquarium", () => MoveFishOut(message.GlobalID, message.FishTemplateID, message.Size));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGFISH))]
    private void ReceivePlaceHousingFish(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGFISH message)
        => Logger.Information("Aquariums: character {CharId} asks to place fish {TemplateId} size {Size} at ({X:0},{Y:0},{Z:0}) in the house; "
                              + "fish are kept in aquariums, so nothing is placed.",
            Logger.Args(GetActiveWizard().CharId, message.FishTemplateID, message.FishSize, message.LocX, message.LocY, message.LocZ));

    private void MoveFishIn(ulong tankGid, uint templateId, float size) {
        if (!TryGetVault(VaultKind.Aquarium, tankGid, out var house, out var tankItem)) {
            return;
        }

        var wizard = GetActiveWizard();
        var tank = HouseVaults.AquariumOf(tankItem.TemplateId);
        var fishTemplate = FishingData.FishTemplate(templateId);
        if (tank is null || fishTemplate is null) {
            Logger.Warning("Aquariums: refused fish {TemplateId} for tank template {TankTemplate} of character {CharId}: {Reason}.",
                Logger.Args(templateId, tankItem.TemplateId, wizard.CharId, tank is null ? "the tank has no AquariumBehavior" : "that is not a fish"));
            InformGameClient("That fish cannot go in this aquarium.");

            return;
        }

        var basket = wizard.FishingBehavior ??= new ServerFishingBehavior();
        HouseChange change;
        var full = false;
        BasketFish taken;
        lock (HouseDeeds.BackpackLock(wizard)) {
            taken = basket.Peek(templateId, size);
            if (taken is null) {
                Logger.Warning("Aquariums: character {CharId} has no fish {TemplateId} size {Size} in the basket.",
                    Logger.Args(wizard.CharId, templateId, size));
                InformGameClient("That fish is not in your fish basket anymore.");
                SendToSocket(FishingService.BasketMessage(wizard));

                return;
            }

            var sizeClass = HouseVaults.SizeClassOf(fishTemplate, taken.Size);
            if (fishTemplate.m_fishtankType != tank.m_fishtankType || sizeClass != tank.m_fishtankSize) {
                Logger.Warning("Aquariums: fish {TemplateId} (tank type {FishType}, size class {SizeClass}) does not fit tank {TankTemplate} (type {TankType}, size {TankSize}).",
                    Logger.Args(templateId, fishTemplate.m_fishtankType, sizeClass, tankItem.TemplateId, tank.m_fishtankType, tank.m_fishtankSize));
                InformGameClient("That fish does not fit in this aquarium.");

                return;
            }

            var fish = new VaultFish { TemplateId = taken.TemplateId, Size = taken.Size };
            if (!VaultFits(house, tankItem, (tankItem.Vault ?? HouseVault.Empty) with { Fish = [.. tankItem.Vault?.Fish ?? [], fish] })) {
                InformGameClient("This house cannot hold any more in its aquariums.");

                return;
            }

            basket.Basket.Remove(taken);
            change = house.TryUpdate(tankItem.Id, stored => {
                var vault = stored.Vault ?? HouseVault.Empty;
                if ((vault.Fish?.Count ?? 0) >= Math.Max(tank.m_numberOfFish, 1)) {
                    full = true;

                    return null;
                }

                return stored with { Vault = vault with { Fish = [.. vault.Fish ?? [], fish] } };
            }, document => HouseCollection.SaveWithFishing(document, wizard));
            if (!change.Done) {
                basket.Basket.Add(taken);
            }
        }

        SendToSocket(FishingService.BasketMessage(wizard));
        if (!change.Done) {
            RefuseVaultChange(VaultKind.Aquarium, tankGid, full, change.Result);

            return;
        }

        Logger.Information("Aquariums: character {CharId} put {Fish} (size {Size:0.00}) in aquarium {TankGid}, which holds {Held} of {Room}.",
            Logger.Args(wizard.CharId, FishingData.Fish(templateId)?.Name ?? templateId.ToString(), taken.Size, tankGid,
                change.Item.Vault.Fish.Count, tank.m_numberOfFish));
        BroadcastVault(house, VaultKind.Aquarium, change);
    }

    private void MoveFishOut(ulong tankGid, uint templateId, float size) {
        if (!TryGetVault(VaultKind.Aquarium, tankGid, out var house, out var tankItem)) {
            return;
        }

        var wizard = GetActiveWizard();
        var basket = wizard.FishingBehavior ??= new ServerFishingBehavior();
        HouseChange change;
        var missing = false;
        VaultFish fish = null;
        BasketFish added = null;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if ((basket.Basket?.Count ?? 0) >= FishingSettings.BasketSize) {
                InformGameClient($"Your fish basket is full ({FishingSettings.BasketSize} fish). Sell some at Lucky Hookline first.");

                return;
            }

            change = house.TryUpdate(tankItem.Id, stored => {
                var vault = stored.Vault ?? HouseVault.Empty;
                var kept = new List<VaultFish>(vault.Fish ?? []);
                fish = kept.Where(one => one.TemplateId == templateId).OrderBy(one => Math.Abs(one.Size - size)).FirstOrDefault();
                if (fish is null) {
                    missing = true;

                    return null;
                }

                kept.Remove(fish);
                if (added is not null) {
                    basket.Basket.Remove(added);
                }

                // The basket changes with the write, so it is taken back when the write fails.
                added = new BasketFish { TemplateId = fish.TemplateId, Size = fish.Size };
                basket.Basket ??= [];
                basket.Basket.Add(added);

                return stored with { Vault = vault with { Fish = kept } };
            }, document => HouseCollection.SaveWithFishing(document, wizard));
            if (!change.Done && added is not null) {
                basket.Basket.Remove(added);
            }
        }

        SendToSocket(FishingService.BasketMessage(wizard));
        if (!change.Done) {
            if (missing) {
                Logger.Warning("Aquariums: aquarium {TankGid} of character {CharId} holds no fish {TemplateId}.",
                    Logger.Args(tankGid, wizard.CharId, templateId));
                InformGameClient("That fish is not in this aquarium anymore.");
                SendBlob(house);

                return;
            }

            RefuseVaultChange(VaultKind.Aquarium, tankGid, full: false, change.Result);

            return;
        }

        Logger.Information("Aquariums: character {CharId} took {Fish} (size {Size:0.00}) out of aquarium {TankGid}, which holds {Held}.",
            Logger.Args(wizard.CharId, FishingData.Fish(templateId)?.Name ?? templateId.ToString(), fish.Size, tankGid, change.Item.Vault.Fish.Count));
        BroadcastVault(house, VaultKind.Aquarium, change);
    }

}
