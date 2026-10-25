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
 * CRAFTING SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * The client's crafting messages: crafting a recipe in the crafting window (MSG_USERECIPE), equipping a recipe in
 * the recipe book (MSG_EQUIPRECIPE) and clicking a crafting station that is not a zone object (MSG_INTERACTOBJECT,
 * house furniture). Frees crafting slots when their cooldown ends.
 *
 * USAGE EXAMPLE:
 * Registered in GameServiceFactory; the work is in Game/Crafting/Crafting.
 *
 * NOTE:
 * The client's error numbers for MSG_USERECIPE are not known, so a failed craft is said in the chat window instead.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Crafting;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

internal class CraftingService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string SLOT_TIMER_KEY = "CraftingSlotDue";

    internal sealed record CraftingSlotDue;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CraftingService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || !Crafting.Crafting.Enabled) {
                return;
            }

            Crafting.Crafting.ExpireSlots(SessionActor.ActorRef, wizard);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTCOUNT { GlobalID = wizard.GameObjectID, BonusSlots = Crafting.Crafting.BonusSlots });
            ScheduleNextSlot();
        }
        catch (Exception exception) {
            Logger.Error("Crafting: setting up the crafting slots failed: {0}", Logger.Args(exception));
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_USERECIPE))]
    private void ReceiveUseRecipe(WIZARD_12_PROTOCOL.MSG_USERECIPE message) {
        string recipeName = message.RecipeName;
        var quantity = (int) Math.Clamp(message.Quantity, 1u, 999u);
        CraftResult result;
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            Logger.Information("Crafting: {0} asks to craft {1} x{2}.", Logger.Args(wizard.CharId, recipeName, message.Quantity));
            result = Crafting.Crafting.Craft(SessionActor.ActorRef, wizard, GetActiveGameObject(), recipeName, quantity);
        }
        catch (Exception exception) {
            Logger.Error("Crafting: crafting {0} failed: {1}", Logger.Args(recipeName, exception));
            result = new CraftResult(CraftError.NothingMade, "Crafting failed on the server; the log says why.");
        }

        // The client shows a failed craft's error number as an Info box, and it has no text for the server's numbers
        // (Niduus, 2026-10-24: "Info is blank"). So a failed craft is only said in the chat window, unless
        // [Crafting] AnswerFailedCrafts asks for the old answer (if the window ever waits forever after a failed craft).
        if (result.Error == CraftError.None || Crafting.Crafting.AnswerFailedCrafts) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_USERECIPE {
                RecipeName = recipeName ?? "",
                FinalItemID = result.MadeItemId,
                Error = (uint) result.Error,
                Quantity = (uint) (result.Error == CraftError.None ? Math.Max(1, result.Crafted) : 0),
            });
        }

        if (result.Error != CraftError.None) {
            Logger.Information("Crafting: {0} could not be crafted: {1} ({2}).", Logger.Args(recipeName, result.Error, result.Message));
            InformGameClient(result.Message);

            return;
        }

        if (result.Crafted < quantity) {
            InformGameClient($"Made {result.Crafted} of the {quantity} asked for: each one fills a crafting slot, and only {result.Crafted} were free.");
        }

        Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, GetActiveWizard(), Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_CraftItem, Math.Max(1, result.Crafted));

        if (result.Slot is not null) {
            ScheduleNextSlot();
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE))]
    private void ReceiveEquipRecipe(WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE message) {
        var wizard = GetActiveWizard();
        if (wizard?.AlchemyBehavior is not { } alchemy) {
            return;
        }

        alchemy.EquippedRecipes ??= [];
        var known = Crafting.Crafting.KnownIdOf(alchemy, message.RecipeID);
        if (known == 0) {
            Logger.Warning("Crafting: {0} equipped recipe {1}, which they do not know.", Logger.Args(wizard.CharId, message.RecipeID));
        }

        var changed = message.Equip != 0
            ? !alchemy.EquippedRecipes.Contains(message.RecipeID) && known != 0
            : alchemy.EquippedRecipes.Remove(message.RecipeID);
        if (message.Equip != 0 && changed) {
            alchemy.EquippedRecipes.Add(message.RecipeID);
        }

        if (changed) {
            Imlight.CoreLib.WizardData.Collections.WizardCollection.UpdateCharacterItems(wizard);
        }

        Logger.Information("Crafting: {0} {1} recipe {2} ({3} equipped).",
            Logger.Args(wizard.CharId, message.Equip != 0 ? "equipped" : "unequipped", message.RecipeID, alchemy.EquippedRecipes.Count));
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPRECIPE {
            GlobalID = wizard.GameObjectID,
            RecipeID = message.RecipeID,
            Equip = (byte) (alchemy.EquippedRecipes.Contains(message.RecipeID) ? 1 : 0),
        });
    }

    // Clicking an object: a crafting station that is furniture has no zone object, so its click comes here.
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_INTERACTOBJECT))]
    private void ReceiveInteractObject(GAME_5_PROTOCOL.MSG_INTERACTOBJECT message) {
        try {
            var wizard = GetActiveWizard();
            var templateId = (uint) message.TemplateID;
            var template = CoreObjectFactory.GetCoreTemplate(templateId) ?? CoreObjectFactory.GetCoreTemplate(new GID(message.TemplateID).MParts.TemplateId);
            Logger.Information("Crafting: {0} clicked object {1} (template {2}).", Logger.Args(wizard?.CharId, message.GlobalID, message.TemplateID));
            if (wizard is null || template is null || CraftingStation.Of(template, message.GlobalID) is not { } station) {
                return;
            }

            Crafting.Crafting.OpenStation(SessionActor.ActorRef, wizard, station);
        }
        catch (Exception exception) {
            Logger.Error("Crafting: opening the clicked station failed: {0}", Logger.Args(exception));
        }
    }

    [InternalMessageHandler(typeof(CraftingSlotDue))]
    private void ReceiveSlotDue(CraftingSlotDue message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            Crafting.Crafting.ExpireSlots(SessionActor.ActorRef, wizard);
            ScheduleNextSlot();
        }
        catch (Exception exception) {
            Logger.Error("Crafting: freeing crafting slots failed: {0}", Logger.Args(exception));
        }
    }

    // Wakes up when the next crafting slot is due, so the client sees it freed while the wizard plays.
    private void ScheduleNextSlot() {
        var next = GetActiveWizard()?.AlchemyBehavior?.Cooldowns?.Select(slot => slot.FinishedAt).DefaultIfEmpty(0).Min() ?? 0;
        if (next <= 0) {
            Timers.Cancel(SLOT_TIMER_KEY);

            return;
        }

        var wait = TimeSpan.FromSeconds(Math.Clamp(next - DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1, 1, 24 * 60 * 60));
        Timers.StartSingleTimer(SLOT_TIMER_KEY, new CraftingSlotDue(), wait);
    }

}
