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
 * ATTIC SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Keeps a character's attic: housing items put away from the backpack, listed in the decorating window and taken back
 * out. The client shows the attic from blobs, and later changes arrive as small versioned patches.
 *
 * USAGE EXAMPLE:
 * Internal service handling attic messages within the game server session.
 *
 * NOTE:
 * - The attic is a stored list of items in Game/Housing, one blob per family's attic subtype. AtticMessages builds the
 *   blobs and the patches from the stored items with the generated Imcodec types; HousingService sends them too when
 *   "Pick Up All Items" fills the attic.
 * - The attic document is created the first time the character needs it, and the client is told its container id then.
 * - The blobs are sent when the window is opened and whenever a client that cannot apply a patch asks for one.
 * - An item keeps its global id in the attic, so it comes back as the same item. Only furniture of a supported family
 *   goes in, because the attic keeps no state beyond the template, the colour and what a vault holds.
 * - A vault keeps what it holds in the attic item, and one that holds anything is not deleted (Niduus, 2026-10-23).
 *
 * TODO:
 * - Does the live server list the attic blobs when the player enters the game, or only when the window opens?
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Housing.Families;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class AtticService(SessionActor sessionActor) : MessageService(sessionActor) {

    private AtticState _attic;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new AtticService(parentActor));

    protected override void OnPreDispose() {
        if (_attic is not null) {
            AtticRegistry.Release(_attic);
            _attic = null;
        }

        base.OnPreDispose();
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC))]
    private void ReceiveRequestAttic(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC message) {
        var wizard = GetActiveWizard();
        if (message.GlobalID != wizard.GameObjectID) {
            HandleForgedRequest($"open the attic of object {message.GlobalID}");

            return;
        }

        if (!TryGetAttic(out var attic)) {
            return;
        }

        // The window lists what the blobs hold, so they go first.
        foreach (var subType in attic.SubTypes().Append(HousingFamilies.Plain.AtticSubType).Distinct()) {
            SendBlob(attic, subType);
        }

        SendCount(wizard, attic);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTATTIC { GlobalID = message.GlobalID });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS))]
    private void ReceiveRequestBlobs(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS message) {
        var wizard = GetActiveWizard();

        // Only requests that name the attic are ours; the house answers the others.
        if (wizard.AtticContainerGid == 0 || !HousingBlobs.TryParseRequest(message.Data, out var request)
            || request.m_associatedGID != wizard.AtticContainerGid) {
            return;
        }

        if (request.m_type != HousingBlobs.BlobType || !TryGetAttic(out var attic)) {
            Logger.Warning("Character {CharId} sent a blob request for the attic that is not a housing request.", Logger.Args(wizard.CharId));

            return;
        }

        // The version, count and user data in the request are the client's cache state, or 0 in a resync. Neither
        // decides what is sent.
        var subTypes = (request.m_blobRequestObjectList ?? [])
            .Where(entry => entry is not null && entry.m_packageNumber == 0 && IsAtticSubType(entry.m_subType))
            .Select(entry => (string) entry.m_subType)
            .Distinct();
        foreach (var subType in subTypes) {
            SendBlob(attic, subType);
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETOATTIC))]
    private void ReceiveMoveToAttic(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETOATTIC message) {
        var wizard = GetActiveWizard();
        if (message.GlobalID != wizard.GameObjectID) {
            HandleForgedRequest($"move item {message.ItemID} to the attic of object {message.GlobalID}");

            return;
        }

        // The item and its template come from the server's own inventory, never from the client.
        var item = wizard.InventoryBehavior.GetItem(message.ItemID);
        if (item is null) {
            HandleForgedRequest($"move item {message.ItemID}, which is not in the backpack, to the attic");

            return;
        }

        var templateId = (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);
        var placement = HousingItems.Check(templateId, out _, out var family);
        if (placement == HousingPlacement.Refused) {
            HandleForgedRequest($"move item {message.ItemID}, template {templateId}, which is not housing furniture, to the attic");

            return;
        }

        if (placement == HousingPlacement.NotEnabled || family.AtticSubType is null) {
            Logger.Information("Character {CharId} tried to put item {ItemId} of the {Family} family in the attic, which is not supported.",
                Logger.Args(wizard.CharId, message.ItemID, family.Id));
            InformGameClient("That kind of object cannot be put in the attic yet.");

            return;
        }

        if (!TryGetAttic(out var attic)) {
            return;
        }

        if (attic.LiveCount >= attic.Limit) {
            InformGameClient("Your attic is full.");

            return;
        }

        // A pet or mount keeps its stored document in the attic, and its entry the look the house shows.
        var kept = HouseCreatures.IsKept(templateId);
        var custom = kept ? PlacedItem.EncodeCustom(HouseCreatures.BuildCustom(item)) : null;

        // The backpack changes in memory only and is written together with the attic, so a crash between the
        // two cannot lose the item or leave it in both places.
        var itemId = item.m_globalID;
        AtticChange change;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if (!wizard.InventoryBehavior.RemoveItem(item)) {
                return;
            }

            // A vault takes what it holds into the attic.
            var packed = HouseVaults.TakePacked(wizard, itemId);
            change = attic.TryAdd(new AtticItem { ItemGid = itemId, TemplateId = templateId, Family = family.Id, Custom = custom, Vault = packed }, family.AtticSubType,
                document => AtticCollection.SaveWithBackpack(document, wizard));
            if (!change.Done) {
                HouseVaults.Pack(wizard, itemId, packed);
                if (!wizard.InventoryBehavior.AddItem(item)) {
                    Logger.Error("Could not return item {ItemId} to the backpack of character {CharId} after it failed to go in the attic.",
                        Logger.Args(itemId, wizard.CharId));
                }
            } else if (packed is not null) {
                Logger.Information("Vaults: character {CharId} put vault {ItemId} in the attic with {Items} item(s) and {Cards} treasure card(s).",
                    Logger.Args(wizard.CharId, itemId, packed.Items?.Count ?? 0, packed.Cards?.Count ?? 0));
            }
        }

        if (!change.Done) {
            if (change.Result == HouseEdit.NeedsResync) {
                SendBlob(attic, family.AtticSubType);
            }

            InformGameClient(change.Result == HouseEdit.LimitReached ? "Your attic is full." : "The object could not be put in the attic.");

            return;
        }

        ItemSync.SendRemoved(SessionActor.ActorRef, wizard, itemId);
        SendToSocket(AtticMessages.AddPatch(attic, change));
        SendCount(wizard, attic);

        if (kept) {
            Logger.Information("Attic: character {CharId} put {Family} {ItemId}, template {TemplateId}, in the attic; its stored item is kept.",
                Logger.Args(wizard.CharId, family.Id, itemId, templateId));
        } else {
            DeleteStoredItem(wizard.CharId, itemId);
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEFROMATTIC))]
    private void ReceiveMoveFromAttic(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEFROMATTIC message) {
        var wizard = GetActiveWizard();
        if (!TryFindAtticItem(wizard, message.GlobalID, message.ItemID, "move", out var attic, out var stored)) {
            return;
        }

        // The slot was emptied already: the client acted on a blob that is a step behind.
        if (stored is null) {
            return;
        }

        List<WizClientObjectItem> added = [];
        AtticChange change;
        var kept = false;
        lock (HouseDeeds.BackpackLock(wizard)) {
            // A music player gives back the scrolls it holds, so the backpack needs room for them as well.
            var scrolls = HouseMusic.Scrolls(stored.TemplateId, stored.Custom);
            if (CoreObjectFactory.GetCoreTemplate(stored.TemplateId) is not WizItemTemplate template
                || wizard.BackpackFreeSlots <= scrolls.Count
                || HousingItems.TemplateCopyLimitReached(template, HousingItems.CountInBackpack(wizard, stored.TemplateId))) {
                InformGameClient(scrolls.Count > 0
                    ? "Make room in your backpack for the music player and its musical scrolls."
                    : "Your backpack cannot hold that object.");

                return;
            }

            // A pet or mount is its own stored item again, which is already stored.
            var item = HouseCreatures.TakeBack(wizard, stored.TemplateId, stored.ItemGid, out kept);
            if (item is null || !HouseMusic.TryCreateScrolls(wizard, scrolls, out var scrollItems)) {
                InformGameClient("The object could not be taken out of the attic.");

                return;
            }

            List<WizClientObjectItem> items = [item, .. scrollItems];
            foreach (var addedItem in items) {
                if (!wizard.InventoryBehavior.AddItem(addedItem)) {
                    break;
                }

                added.Add(addedItem);
            }

            // A vault keeps what it holds in the backpack item.
            var packed = added.Count == items.Count && HouseVaults.Pack(wizard, item.m_globalID, stored.Vault);

            // The new items and the emptied slot are written in one transaction, so a crash between them cannot lose an item.
            change = added.Count == items.Count
                ? attic.TryRemove(stored.Id, document => AtticCollection.SaveWithBackpack(document, wizard,
                    kept ? added.Where(addedItem => addedItem != item).ToArray() : [.. added]))
                : new AtticChange(HouseEdit.NotFound);
            if (!change.Done) {
                if (packed) {
                    HouseVaults.TakePacked(wizard, item.m_globalID);
                }

                foreach (var addedItem in added) {
                    wizard.InventoryBehavior.RemoveItem(addedItem);
                }

                InformGameClient("The object could not be taken out of the attic.");

                return;
            }
        }

        foreach (var addedItem in added) {
            if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, addedItem)) {
                Logger.Error("Could not tell the client about item {ItemId} taken out of the attic of character {CharId}.",
                    Logger.Args(addedItem.m_globalID, wizard.CharId));
            }
        }

        SendDeletePatch(attic, change);
        SendCount(wizard, attic);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFROMATTIC))]
    private void ReceiveDeleteFromAttic(WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFROMATTIC message) {
        var wizard = GetActiveWizard();
        if (!TryFindAtticItem(wizard, message.GlobalID, message.ItemID, "delete", out var attic, out var stored)) {
            return;
        }

        if (stored is null) {
            return;
        }

        // What a vault holds would be lost with it.
        if (stored.Vault?.HoldsItems == true) {
            Logger.Information("Vaults: character {CharId} tried to delete attic vault {ItemGid}, which holds {Items} item(s) and {Cards} treasure card(s).",
                Logger.Args(wizard.CharId, stored.ItemGid, stored.Vault.Items?.Count ?? 0, stored.Vault.Cards?.Count ?? 0));
            InformGameClient("That vault still holds items. Empty it before you delete it.");
            // The client may have taken the item off its list already, so the blob puts it back.
            if (HousingFamilies.All.FirstOrDefault(family => family.Id == stored.Family)?.AtticSubType is { } subType) {
                SendBlob(attic, subType);
            }

            return;
        }

        var change = attic.TryRemove(stored.Id);
        if (!change.Done) {
            InformGameClient("The object could not be deleted from the attic.");

            return;
        }

        // A pet or mount kept its stored document in the attic, which goes with it.
        if (stored.ItemGid != 0 && HouseCreatures.IsKept(stored.TemplateId)) {
            Logger.Information("Attic: character {CharId} deleted {TemplateId} {ItemGid} from the attic with its stored item.",
                Logger.Args(wizard.CharId, stored.TemplateId, stored.ItemGid));
            DeleteStoredItem(wizard.CharId, stored.ItemGid);
        }

        SendDeletePatch(attic, change);
        SendCount(wizard, attic);
    }

    private bool TryFindAtticItem(Wizard wizard, ulong globalId, ulong itemId, string what, out AtticState attic, out AtticItem stored) {
        stored = null;
        attic = null;
        if (globalId != wizard.GameObjectID) {
            HandleForgedRequest($"{what} attic item {itemId} for object {globalId}");

            return false;
        }

        if (!TryGetAttic(out attic)) {
            return false;
        }

        if (!attic.TryFindByGid(itemId, out stored, out _)) {
            HandleForgedRequest($"{what} attic item {itemId}, which is not an object of their attic");

            return false;
        }

        return true;
    }

    private bool TryGetAttic(out AtticState attic) {
        attic = _attic;
        if (attic is not null) {
            return true;
        }

        var wizard = GetActiveWizard();
        if (!AtticRegistry.TryEnter(wizard, out attic, out var created, out var audit)) {
            Logger.Error("Character {CharId} has an attic that cannot be loaded.", Logger.Args(wizard.CharId));
            InformGameClient("Your attic is not available right now. Please try again later.");

            return false;
        }

        _attic = attic;
        foreach (var message in AtticMessages.Opening(wizard, attic, created, audit)) {
            SendToSocket(message);
        }

        return true;
    }

    private void SendDeletePatch(AtticState attic, AtticChange change)
        => SendToSocket(AtticMessages.DeletePatch(attic, change));

    private void SendCount(Wizard wizard, AtticState attic)
        => SendToSocket(AtticMessages.Count(wizard, attic));

    private void SendBlob(AtticState attic, string subType) {
        if (AtticMessages.BlobMessage(attic, subType) is { } blob) {
            SendToSocket(blob);
        }
    }

    private static bool IsAtticSubType(string subType)
        => HousingFamilies.All.Any(family => family.AtticSubType == subType);

    private void HandleForgedRequest(string request) {
        var wizard = GetActiveWizard();
        wizard.Account?.AddInfraction(InfractionType.SuspiciousBehavior, $"Tried to {request}.");
        Logger.Warning("Character {CharId} tried to {Request}. This has been logged as suspicious behavior.",
            Logger.Args(wizard.CharId, request));
        InformGameClient("That request was refused.");
    }

    private static void DeleteStoredItem(ulong charId, ulong itemId) {
        // The item already left the backpack, so only an unused document is left to delete.
        try {
            if (!WizardItemCollection.RemoveItem(charId, itemId)) {
                Logger.Warning("Item {ItemId} of character {CharId} has no stored document to delete.", Logger.Args(itemId, charId));
            }
        } catch (Exception exception) {
            Logger.Warning("Could not delete the stored item {ItemId} of character {CharId}: {Reason}",
                Logger.Args(itemId, charId, exception.Message));
        }
    }

}
