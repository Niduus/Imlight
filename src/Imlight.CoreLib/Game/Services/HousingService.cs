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
 * HOUSING SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Sends a house's furniture to the client when the player enters it, and keeps the client's copy in step
 * with the house while the player decorates.
 *
 * USAGE EXAMPLE:
 * Internal service handling housing messages within the game server session.
 *
 * NOTE:
 * - Furniture is not a zone entity. The client builds every placed object from a blob the server sends, and
 *   later edits arrive as small versioned patches. The blob and the patches are built here, from the stored
 *   items, with the generated Imcodec types; the house is in Game/Housing.
 * - The service counts the player into the house at MSG_PRELOGIN and out when the session ends.
 * - A blob request that names the attic is left to AtticService.
 * - Only the owner decorates. A visitor sees the same blob and patches, and every edit they send is refused as forged.
 * - Picking up every object moves them into the owner's attic, so the service is a viewer of the attic as well. The house
 *   and the attic are written in one transaction by Game/Housing/HousePickUpAll.
 * - Music players and teleporters keep their state as the custom data of their blob entry (Game/Housing/HouseMusic,
 *   HouseTeleporters). A change is stored on the placed item and sent to everyone in the house as a patch.
 * - A music player that is picked up gives its musical scrolls back to the backpack.
 * - The moves of the house's vaults, sheds and posters are in HousingService.Vaults. A vault that holds items is not picked up.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Housing.Families;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class HousingService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string BlobType = HousingBlobs.BlobType;
    private const string ItemSubType = "Item";

    private HouseState _house;
    private AtticState _attic;
    private ulong _houseCharId;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new HousingService(parentActor));

    protected override void OnPreDispose() {
        LeaveHouse();
        if (_attic is not null) {
            AtticRegistry.Release(_attic);
            _attic = null;
        }

        base.OnPreDispose();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        LeaveHouse();

        var wizard = GetActiveWizard();
        if (wizard.AttachedLotGid == 0) {
            return;
        }

        if (!HouseRegistry.TryEnter(wizard.AttachedLotGid, out var house)) {
            Logger.Warning("Character {CharId} attached to lot {LotGid}, which is not a house.",
                Logger.Args(wizard.CharId, wizard.AttachedLotGid));

            return;
        }

        _house = house;
        _houseCharId = wizard.CharId;

        // Trainer tomes bring their trainers; a visitor entering a house already showing them adds none.
        HouseTrainers.SpawnAll(SessionActor.GetZoneActor(), house);
        HouseVendors.SpawnAll(SessionActor.GetZoneActor(), house);
        HouseBanks.SpawnAll(SessionActor.GetZoneActor(), house);

        // The client compares this with its cached copy and asks for the blob only when they differ.
        var snapshot = house.Snapshot();
        var request = new BlobRequest {
            m_type = BlobType,
            m_associatedGID = house.LotGid,
            m_blobRequestObjectList = [
                new BlobRequestObject {
                    m_subType = ItemSubType,
                    m_versionNumber = snapshot.Revision,
                    m_packageNumber = 0,
                    m_userData = snapshot.GidBase,
                    m_objectCount = (uint) snapshot.LiveCount,
                }
            ],
        };
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS { Data = HousingBlobs.Serialize(request) });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS))]
    private void ReceiveRequestBlobs(WIZARDHOUSING_50_PROTOCOL.MSG_REQUEST_BLOBS message) {
        var wizard = GetActiveWizard();
        if (!HousingBlobs.TryParseRequest(message.Data, out var request)) {
            Logger.Warning("Character {CharId} sent a malformed blob request.", Logger.Args(wizard.CharId));

            return;
        }

        // The attic has blobs of its own, which AtticService answers.
        if (wizard.AtticContainerGid != 0 && request.m_associatedGID == wizard.AtticContainerGid) {
            return;
        }

        var house = _house;
        if (house is null) {
            Logger.Warning("Character {CharId} requested house blobs outside a house.", Logger.Args(wizard.CharId));

            return;
        }

        // The version, count and user data in the request are the client's cache state, or 0 in a resync. Neither
        // decides what is sent.
        if (!RequestsItemBlob(request, house.LotGid)) {
            Logger.Warning("Character {CharId} requested blobs that are not the item blob of lot {LotGid}.",
                Logger.Args(wizard.CharId, house.LotGid));

            return;
        }

        SendBlob(house);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT))]
    private void ReceivePlaceHousingObject(WIZARDHOUSING_50_PROTOCOL.MSG_PLACEHOUSINGOBJECT message) {
        if (!TryGetHouse("place an object", out var house)) {
            return;
        }

        // The item and its template come from the server's own inventory, never from the client.
        var wizard = GetActiveWizard();
        var item = wizard.InventoryBehavior.GetItem(message.ObjectID);
        if (item is null) {
            HandleForgedRequest($"place item {message.ObjectID} that is not in the backpack");

            return;
        }

        var templateId = (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);
        var placement = HousingItems.Check(templateId, out var template, out var family);
        if (placement == HousingPlacement.Refused) {
            HandleForgedRequest($"place item {message.ObjectID}, template {templateId}, which is not placeable furniture");

            return;
        }

        var switchesBlock = message.SwitchCastleBlock != 0 && !HousingFamilies.CastleBlock.Enabled;
        if (placement == HousingPlacement.NotEnabled || switchesBlock) {
            Logger.Information("Character {CharId} tried to place item {ItemId} of the {Family} family, which is not enabled.",
                Logger.Args(wizard.CharId, message.ObjectID, placement == HousingPlacement.NotEnabled ? family.Id : HousingFamilies.CastleBlock.Id));
            InformGameClient("That kind of object cannot be placed in a house yet.");

            return;
        }

        var placed = new PlacedItem {
            ItemGid = item.m_globalID,
            TemplateId = templateId,
            X = message.LocX,
            Y = message.LocY,
            Z = message.LocZ,
            Yaw = message.Yaw,
        }.Normalize();
        if (placed is null) {
            HandleForgedRequest($"place item {message.ObjectID} at a position that is not valid");

            return;
        }

        if (house.LiveCount >= house.ItemLimit
            || HousingItems.TemplateCopyLimitReached(template, house.CountTemplate(templateId))) {
            InformGameClient("This house cannot hold any more of that.");

            return;
        }

        // The backpack changes in memory only and is written together with the house, so a crash between
        // the two cannot lose the item or leave it in both places.
        var itemId = item.m_globalID;
        HouseChange change;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if (!wizard.InventoryBehavior.RemoveItem(item)) {
                return;
            }

            change = house.TryAdd(placed, document => HouseCollection.SaveWithBackpack(document, wizard));
            if (!change.Done && !wizard.InventoryBehavior.AddItem(item)) {
                Logger.Error("Could not return item {ItemId} to the backpack of character {CharId} after it failed to be placed.",
                    Logger.Args(itemId, wizard.CharId));
            }
        }

        if (!change.Done) {
            if (change.Result == HouseEdit.NeedsResync) {
                SendBlob(house);
            }

            InformGameClient("The object could not be placed.");

            return;
        }

        ItemSync.SendRemoved(SessionActor.ActorRef, wizard, itemId);
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHADDHOUSINGOBJECT {
            ObjectID = templateId,
            LocX = change.Item.X,
            LocY = change.Item.Y,
            LocZ = change.Item.Z,
            Yaw = change.Item.Yaw,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = change.Revision,
            GIDID = change.GidId,
            ColorBits = (sbyte) change.Item.Tint,
        }, isSelfless: false, ZoneBroadcastTarget.Players);

        HouseTrainers.Spawn(SessionActor.GetZoneActor(), _house, change.GidId, change.Item);
        HouseVendors.Spawn(SessionActor.GetZoneActor(), change.GidId, change.Item);
        HouseBanks.Spawn(SessionActor.GetZoneActor(), change.GidId, change.Item);
        DeleteStoredItem(wizard.CharId, itemId);

        // The add patch makes a plain entry, and the client takes vault patches only for a custom one.
        if (HouseVaults.KindOf(templateId) != VaultKind.None) {
            SendBlob(house);
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEHOUSINGOBJECT))]
    private void ReceiveUpdateHousingObject(WIZARDHOUSING_50_PROTOCOL.MSG_UPDATEHOUSINGOBJECT message) {
        if (!TryGetHouse("move an object", out var house)) {
            return;
        }

        if (message.UseExtendedYaw is not (0 or 1) || !house.TryFindByGid(message.ObjectGID, out var current, out _)) {
            HandleForgedRequest($"move object {message.ObjectGID}, which is not an object of this house");

            return;
        }

        if (current is null) {
            // The object is gone. The client moved it already, so give it the real state.
            SendBlob(house);

            return;
        }

        if (message.SwitchCastleBlock != 0 && !HousingFamilies.CastleBlock.Enabled) {
            InformGameClient("That kind of object cannot be changed in a house yet.");
            SendBlob(house);

            return;
        }

        var invalid = false;
        var change = house.TryUpdate(current.Id, stored => {
            var replacement = (stored with {
                X = message.LocX,
                Y = message.LocY,
                Z = message.LocZ,
                Yaw = message.Yaw,
                ExactYaw = message.UseExtendedYaw == 1,
                Scale = message.Scale,
                Brightness = message.Brightness,
            }).Normalize();
            invalid = replacement is null;

            return replacement;
        });
        if (invalid) {
            HandleForgedRequest($"move object {message.ObjectGID} to a position, scale or brightness that is not valid");

            return;
        }

        if (!change.Done) {
            // The object is gone or the write failed. The client moved it already, so give it the real state.
            SendBlob(house);

            return;
        }

        // A moved trainer tome takes its trainer along.
        if (HouseTrainers.IsTrainerItem(change.Item.TemplateId)) {
            HouseTrainers.Remove(SessionActor.GetZoneActor(), change.GidId);
            HouseTrainers.Spawn(SessionActor.GetZoneActor(), _house, change.GidId, change.Item);
        }

        if (HouseVendors.IsVendorItem(change.Item.TemplateId)) {
            HouseVendors.Remove(SessionActor.GetZoneActor(), change.GidId, change.Item.TemplateId);
            HouseVendors.Spawn(SessionActor.GetZoneActor(), change.GidId, change.Item);
        }

        // A moved bank chest takes its bank along.
        if (HouseBanks.IsBankItem(change.Item.TemplateId)) {
            HouseBanks.Remove(SessionActor.GetZoneActor(), change.GidId);
            HouseBanks.Spawn(SessionActor.GetZoneActor(), change.GidId, change.Item);
        }

        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHUPDATEHOUSINGOBJECT {
            LocX = change.Item.X,
            LocY = change.Item.Y,
            LocZ = change.Item.Z,
            Yaw = change.Item.Yaw,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = change.Revision,
            GIDID = change.GidId,
            ObjectID = change.Item.TemplateId,
            SwitchTemplateID = 0,
            UseExtendedYaw = (sbyte) (change.Item.ExactYaw ? 1 : 0),
            Scale = change.Item.Scale,
            Brightness = (sbyte) change.Item.Brightness,
        }, isSelfless: false, ZoneBroadcastTarget.Players);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_PICKUPOBJECT))]
    private void ReceivePickUpObject(GAME_5_PROTOCOL.MSG_PICKUPOBJECT message) {
        // Other features use this message too, so a GID that is not one of this house's objects is not ours.
        var house = _house;
        if (house is null || !house.TryFindByGid(message.GameObjectID, out var placedItem, out _)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (house.OwnerCharId != wizard.CharId) {
            HandleForgedRequest("pick up an object in a house that is not theirs");

            return;
        }

        // What a vault holds is not carried by the backpack item, so a vault that holds anything stays.
        if (HouseVaults.HoldsItems(placedItem)) {
            Logger.Information("Vaults: character {CharId} tried to pick up a {Vault} that still holds items.",
                Logger.Args(wizard.CharId, HouseVaults.NameOf(HouseVaults.KindOf(placedItem.TemplateId))));
            InformGameClient($"Empty the {HouseVaults.NameOf(HouseVaults.KindOf(placedItem.TemplateId))} before you pick it up.");
            RefusePickUp(house);

            return;
        }

        WizClientObjectItem item;
        HouseChange change;
        List<WizClientObjectItem> added;
        lock (HouseDeeds.BackpackLock(wizard)) {
            // A music player gives back the scrolls it holds, so the backpack needs room for them as well.
            var scrolls = HouseMusic.Scrolls(placedItem);
            if (placedItem is null
                || CoreObjectFactory.GetCoreTemplate(placedItem.TemplateId) is not WizItemTemplate template
                || wizard.InventoryBehavior.FreeSlots <= scrolls.Count
                || HousingItems.TemplateCopyLimitReached(template, HousingItems.CountInBackpack(wizard, placedItem.TemplateId))) {
                if (scrolls.Count > 0) {
                    InformGameClient("Make room in your backpack for the music player and its musical scrolls.");
                }

                RefusePickUp(house);

                return;
            }

            item = HousingItems.CreateItem(wizard, placedItem.TemplateId, placedItem.ItemGid);
            added = item is not null && HouseMusic.TryCreateScrolls(wizard, scrolls, out var scrollItems) ? [item, .. scrollItems] : [];
            if (added.Count == 0 || !TryAddAll(wizard, added)) {
                RefusePickUp(house);

                return;
            }

            // The new items and the emptied slot are written in one transaction, so a crash between them cannot lose an item.
            change = house.TryRemove(placedItem.Id, document => HouseCollection.SaveWithBackpack(document, wizard, [.. added]));
            if (!change.Done) {
                RemoveAll(wizard, added);
                RefusePickUp(house);

                return;
            }
        }

        foreach (var addedItem in added) {
            if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, addedItem)) {
                Logger.Error("Could not tell the client about picked up item {ItemId} of character {CharId}.",
                    Logger.Args(addedItem.m_globalID, wizard.CharId));
            }
        }

        if (added.Count > 1) {
            Logger.Information("Music: character {CharId} picked up music player {TemplateId} and got back {Count} musical scrolls.",
                Logger.Args(wizard.CharId, placedItem.TemplateId, added.Count - 1));
        }

        if (HouseTrainers.IsTrainerItem(placedItem.TemplateId)) {
            HouseTrainers.Remove(SessionActor.GetZoneActor(), change.GidId);
        }

        HouseVendors.Remove(SessionActor.GetZoneActor(), change.GidId, placedItem.TemplateId);
        HouseBanks.Remove(SessionActor.GetZoneActor(), change.GidId);

        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHDELETEHOUSINGOBJECT {
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = change.Revision,
            GIDID = change.GidId,
        }, isSelfless: false, ZoneBroadcastTarget.Players);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL))]
    private void ReceivePickUpAll(WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL message) {
        if (!TryGetHouse("pick up every object", out var house)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (!HousingBlobs.TryParseItemList(message.Exceptions, out var kept)) {
            // Which objects stay is not known, so none is moved.
            Logger.Warning("Character {CharId} sent a list of objects to keep that could not be read ({Length} bytes).",
                Logger.Args(wizard.CharId, ((byte[]) message.Exceptions)?.Length ?? 0));
            InformGameClient("The objects could not be picked up.");

            return;
        }

        if (!TryGetAttic(out var attic)) {
            return;
        }

        if (house.Snapshot().Slots.Any(HouseVaults.HoldsItems)) {
            InformGameClient("Vaults that still hold items stay in the house.");
        }

        var result = HousePickUpAll.Run(house, attic, kept);
        if (result.Edit == HouseEdit.NeedsResync) {
            // The attic blobs were numbered again. The client needs them before any patch, and the run goes on with the room they made.
            foreach (var subType in result.Renumbered) {
                if (AtticMessages.BlobMessage(attic, subType) is { } blob) {
                    SendToSocket(blob);
                }
            }

            result = HousePickUpAll.Run(house, attic, kept);
        }

        if (result.Edit is not (HouseEdit.Done or HouseEdit.LimitReached)) {
            Logger.Warning("Character {CharId} could not pick up every object of lot {LotGid}: {Edit}.",
                Logger.Args(wizard.CharId, house.LotGid, result.Edit));
            InformGameClient("The objects could not be picked up.");
            SendBlob(house);

            return;
        }

        foreach (var change in result.Added) {
            SendToSocket(AtticMessages.AddPatch(attic, change));
        }

        foreach (var change in result.Removed) {
            HouseTrainers.Remove(SessionActor.GetZoneActor(), change.GidId);
            HouseVendors.Remove(SessionActor.GetZoneActor(), change.GidId, change.Item?.TemplateId ?? 0);
            HouseBanks.Remove(SessionActor.GetZoneActor(), change.GidId);
            ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHDELETEHOUSINGOBJECT {
                SubType = ItemSubType,
                PackageNumber = 0,
                VersionNumber = change.Revision,
                GIDID = change.GidId,
            }, isSelfless: false, ZoneBroadcastTarget.Players);
        }

        if (result.Added.Count > 0) {
            SendToSocket(AtticMessages.Count(wizard, attic));
        }

        // Any error code makes the client say that the attic is too full, which is the one reason objects stay.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_PICKUPALL {
            ItemCount = (uint) result.Removed.Count,
            ErrorCode = (sbyte) (result.Edit == HouseEdit.LimitReached || result.Left > 0 ? 1 : 0),
            Exceptions = string.Empty,
        });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_EQUIPHOUSEMUSIC))]
    private void ReceiveEquipHouseMusic(WIZARDHOUSING_50_PROTOCOL.MSG_EQUIPHOUSEMUSIC message) {
        Logger.Information("Music: character {CharId} puts scroll item {ItemId} into music player {PlayerGid}.",
            Logger.Args(GetActiveWizard().CharId, message.MusicGID, message.MusicPlayerGID));
        try {
            EquipHouseMusic(message);
        } catch (Exception exception) {
            Logger.Error("Music: putting scroll item {ItemId} into music player {PlayerGid} failed: {Error}",
                Logger.Args(message.MusicGID, message.MusicPlayerGID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REMOVEHOUSEMUSIC))]
    private void ReceiveRemoveHouseMusic(WIZARDHOUSING_50_PROTOCOL.MSG_REMOVEHOUSEMUSIC message) {
        Logger.Information("Music: character {CharId} takes scroll {TemplateId} out of music player {PlayerGid}.",
            Logger.Args(GetActiveWizard().CharId, message.MusicTemplateID, message.MusicPlayerGID));
        try {
            RemoveHouseMusic(message);
        } catch (Exception exception) {
            Logger.Error("Music: taking scroll {TemplateId} out of music player {PlayerGid} failed: {Error}",
                Logger.Args(message.MusicTemplateID, message.MusicPlayerGID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_SELECTHOUSEMUSIC))]
    private void ReceiveSelectHouseMusic(WIZARDHOUSING_50_PROTOCOL.MSG_SELECTHOUSEMUSIC message) {
        Logger.Information("Music: character {CharId} selects scroll {TemplateId} on music player {PlayerGid}.",
            Logger.Args(GetActiveWizard().CharId, message.MusicTemplateID, message.MusicPlayerGID));
        try {
            EditMusicPlayer("select the music of a music player", message.MusicPlayerGID, player => {
                if (message.MusicTemplateID != 0 && !player.m_playlist.Contains(message.MusicTemplateID)) {
                    return "That musical scroll is not in this music player.";
                }

                player.m_currentSelection = message.MusicTemplateID;

                return null;
            });
        } catch (Exception exception) {
            Logger.Error("Music: selecting scroll {TemplateId} on music player {PlayerGid} failed: {Error}",
                Logger.Args(message.MusicTemplateID, message.MusicPlayerGID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_SELECTMUSICSTYLE))]
    private void ReceiveSelectMusicStyle(WIZARDHOUSING_50_PROTOCOL.MSG_SELECTMUSICSTYLE message) {
        Logger.Information("Music: character {CharId} selects play style {Style} on music player {PlayerGid}.",
            Logger.Args(GetActiveWizard().CharId, message.MusicStyle, message.MusicPlayerGID));
        try {
            EditMusicPlayer("select the play style of a music player", message.MusicPlayerGID, player => {
                if (message.MusicStyle > HouseMusic.MaxPlayStyle) {
                    return "That play style is not known.";
                }

                player.m_playStyle = message.MusicStyle;

                return null;
            });
        } catch (Exception exception) {
            Logger.Error("Music: selecting play style {Style} on music player {PlayerGid} failed: {Error}",
                Logger.Args(message.MusicStyle, message.MusicPlayerGID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_SETHOUSETELEPORTER))]
    private void ReceiveSetHouseTeleporter(WIZARDHOUSING_50_PROTOCOL.MSG_SETHOUSETELEPORTER message) {
        Logger.Information("Teleporter: character {CharId} sets the destination of teleporter {ObjectGid} to {X}, {Y}, {Z}, yaw {Yaw}.",
            Logger.Args(GetActiveWizard().CharId, message.ObjectID, message.LocX, message.LocY, message.LocZ, message.Yaw));
        try {
            SetHouseTeleporter(message);
        } catch (Exception exception) {
            Logger.Error("Teleporter: setting the destination of teleporter {ObjectGid} failed: {Error}",
                Logger.Args(message.ObjectID, exception));
        }
    }

    // The client sends these back when the server asked it to load another house or to confirm a Castle Tours teleport,
    // which the server does not do yet. They are logged so a test log shows when the game sends one anyway.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTTELEPORTERHOUSINGZONE))]
    private void ReceiveTeleporterHousingZone(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTTELEPORTERHOUSINGZONE message)
        => Logger.Information("Teleporter: character {CharId} sent MSG_REQUESTTELEPORTERHOUSINGZONE (object {GlobalId}, status {Status}, "
                + "template {TemplateGid}, structure {StructureGid}), which the server does not use yet.",
            Logger.Args(GetActiveWizard().CharId, message.GlobalID, message.Status, message.TemplateGID, message.StructureGID));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTTELEPORTERHOUSINGCONFIRM))]
    private void ReceiveTeleporterHousingConfirm(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTTELEPORTERHOUSINGCONFIRM message)
        => Logger.Information("Teleporter: character {CharId} sent MSG_REQUESTTELEPORTERHOUSINGCONFIRM (object {GlobalId}), which the server does not use yet.",
            Logger.Args(GetActiveWizard().CharId, message.GlobalID));

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERMOVE))]
    private void ReceivePlayerMove(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        // Furniture is not a zone object, so the step onto a teleporter is found from the position the move service reports.
        var house = _house;
        if (house is null || message.PlayerWizard is not { } wizard) {
            return;
        }

        try {
            if (HouseTeleporters.OnMove(house, wizard.CharId, wizard.Location) is not { } pad) {
                return;
            }

            Logger.Information("Teleporter: character {CharId} stepped onto teleporter {GidId} of lot {LotGid} and goes to {X}, {Y}, {Z}.",
                Logger.Args(wizard.CharId, pad.GidId, house.LotGid, pad.Target.X, pad.Target.Y, pad.Target.Z));
            TeleportInHouse(wizard, pad.Target, pad.TargetYaw);
        } catch (Exception exception) {
            Logger.Error("Teleporter: checking the teleporters of lot {LotGid} for character {CharId} failed: {Error}",
                Logger.Args(house.LotGid, wizard.CharId, exception));
        }
    }

    private void EquipHouseMusic(WIZARDHOUSING_50_PROTOCOL.MSG_EQUIPHOUSEMUSIC message) {
        if (!TryGetMusicPlayer("put a musical scroll into a music player", message.MusicPlayerGID, out var house, out var current)) {
            return;
        }

        // The scroll comes from the server's own backpack, never from the client.
        var wizard = GetActiveWizard();
        var scroll = wizard.InventoryBehavior.GetItem(message.MusicGID);
        var scrollTemplateId = scroll is null ? 0 : (uint) Math.Min((ulong) scroll.m_templateID, uint.MaxValue);
        if (scroll is null || !HouseMusic.IsScroll(scrollTemplateId)) {
            HandleForgedRequest($"put item {message.MusicGID}, which is not a musical scroll in the backpack, into a music player");

            return;
        }

        string refusal = null;
        byte[] data = [];
        HouseChange change;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if (!wizard.InventoryBehavior.RemoveItem(scroll)) {
                return;
            }

            // The backpack changes in memory only and is written together with the house, so the scroll is never lost.
            change = house.TryUpdate(current.Id, stored => WithMusic(stored, player => {
                if (player.m_playlist.Count >= HouseMusic.MaxScrolls) {
                    return "This music player cannot hold any more musical scrolls.";
                }

                if (player.m_playlist.Contains(scrollTemplateId)) {
                    return "This music player already holds that musical scroll.";
                }

                player.m_playlist.Add(scrollTemplateId);
                if (player.m_currentSelection == 0) {
                    player.m_currentSelection = scrollTemplateId;
                }

                return null;
            }, out refusal, out data), document => HouseCollection.SaveWithBackpack(document, wizard));
            if (!change.Done && !wizard.InventoryBehavior.AddItem(scroll)) {
                Logger.Error("Could not return item {ItemId} to the backpack of character {CharId} after it failed to go into a music player.",
                    Logger.Args(scroll.m_globalID, wizard.CharId));
            }
        }

        if (!FinishMusicChange(house, change, refusal, data)) {
            return;
        }

        ItemSync.SendRemoved(SessionActor.ActorRef, wizard, scroll.m_globalID);
        DeleteStoredItem(wizard.CharId, scroll.m_globalID);
    }

    private void RemoveHouseMusic(WIZARDHOUSING_50_PROTOCOL.MSG_REMOVEHOUSEMUSIC message) {
        if (!TryGetMusicPlayer("take a musical scroll out of a music player", message.MusicPlayerGID, out var house, out var current)) {
            return;
        }

        var wizard = GetActiveWizard();
        var scrollTemplateId = message.MusicTemplateID;
        if (!HouseMusic.Read(current).m_playlist.Contains(scrollTemplateId)) {
            InformGameClient("That musical scroll is not in this music player.");
            SendBlob(house);

            return;
        }

        string refusal = null;
        byte[] data = [];
        WizClientObjectItem scroll;
        HouseChange change;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if (CoreObjectFactory.GetCoreTemplate(scrollTemplateId) is not WizItemTemplate template
                || wizard.InventoryBehavior.FreeSlots <= 0
                || HousingItems.TemplateCopyLimitReached(template, HousingItems.CountInBackpack(wizard, scrollTemplateId))) {
                InformGameClient("Your backpack cannot hold that musical scroll.");

                return;
            }

            scroll = HousingItems.CreateItem(wizard, scrollTemplateId);
            if (scroll is null || !wizard.InventoryBehavior.AddItem(scroll)) {
                InformGameClient("The musical scroll could not be taken out.");

                return;
            }

            // The new scroll and the player are written in one transaction, so a crash between them cannot lose the scroll.
            change = house.TryUpdate(current.Id, stored => WithMusic(stored, player => {
                if (!player.m_playlist.Remove(scrollTemplateId)) {
                    return "That musical scroll is not in this music player.";
                }

                if (player.m_currentSelection == scrollTemplateId) {
                    player.m_currentSelection = player.m_playlist.FirstOrDefault();
                }

                return null;
            }, out refusal, out data), document => HouseCollection.SaveWithBackpack(document, wizard, scroll));
            if (!change.Done) {
                wizard.InventoryBehavior.RemoveItem(scroll);
            }
        }

        if (!FinishMusicChange(house, change, refusal, data)) {
            return;
        }

        if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, scroll)) {
            Logger.Error("Could not tell the client about musical scroll {ItemId} taken out of a music player by character {CharId}.",
                Logger.Args(scroll.m_globalID, wizard.CharId));
        }
    }

    private void EditMusicPlayer(string request, ulong playerGid, Func<HousingMusicPlayerBehavior, string> edit) {
        if (!TryGetMusicPlayer(request, playerGid, out var house, out var current)) {
            return;
        }

        string refusal = null;
        byte[] data = [];
        var change = house.TryUpdate(current.Id, stored => WithMusic(stored, edit, out refusal, out data));
        FinishMusicChange(house, change, refusal, data);
    }

    // Applies an edit to the music player's state and gives back the item with the new custom data, or null when the
    // edit is refused or the state cannot be written.
    private static PlacedItem WithMusic(PlacedItem stored, Func<HousingMusicPlayerBehavior, string> edit, out string refusal, out byte[] data) {
        data = [];
        var player = HouseMusic.Read(stored);
        refusal = edit(player);
        if (refusal is not null) {
            return null;
        }

        var style = player.m_playStyle;
        if (HouseMusic.KeepLooping(player)) {
            Logger.Information("Music: music player {TemplateId} (item {ItemId}) holds one scroll, so play style {Style} becomes {LoopStyle}.",
                Logger.Args(stored.TemplateId, stored.Id, style, player.m_playStyle));
        }

        if (!HouseMusic.TryWrite(player, out data)) {
            refusal = "The music player could not be changed.";

            return null;
        }

        Logger.Information("Music: music player {TemplateId} (item {ItemId}) now has {State}.",
            Logger.Args(stored.TemplateId, stored.Id, HouseMusic.Describe(player)));

        return stored with { Custom = PlacedItem.EncodeCustom(data) };
    }

    private bool FinishMusicChange(HouseState house, HouseChange change, string refusal, byte[] data) {
        if (!change.Done) {
            // The client's music window may already show the change, so it gets the real state back.
            InformGameClient(refusal ?? "The music player could not be changed.");
            SendBlob(house);

            return false;
        }

        // The patch tells every client's music manager, which plays the selected music.
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHHOUSINGMUSICOBJECT {
            Data = data,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = change.Revision,
            GIDID = change.GidId,
        }, isSelfless: false, ZoneBroadcastTarget.Players);

        return true;
    }

    private bool TryGetMusicPlayer(string request, ulong playerGid, out HouseState house, out PlacedItem current) {
        current = null;
        if (!TryGetHouse(request, out house)) {
            return false;
        }

        if (!house.TryFindByGid(playerGid, out current, out _)) {
            HandleForgedRequest($"{request} with object {playerGid}, which is not an object of this house");

            return false;
        }

        if (current is null) {
            // The player is gone. The client acted on an older blob, so give it the real state.
            SendBlob(house);

            return false;
        }

        if (!HouseMusic.IsPlayer(current.TemplateId)) {
            HandleForgedRequest($"{request} with object {playerGid}, template {current.TemplateId}, which is not a music player");

            return false;
        }

        return true;
    }

    private void SetHouseTeleporter(WIZARDHOUSING_50_PROTOCOL.MSG_SETHOUSETELEPORTER message) {
        if (!TryGetHouse("set a teleporter's destination", out var house)) {
            return;
        }

        // The targeting names the teleporter by the global id of the backpack item it was placed from, not by its
        // housing object GID as the music window does, so both are looked up.
        if (!house.TryFindByItemGid(message.ObjectID, out var current, out _)
            && !house.TryFindByGid(message.ObjectID, out current, out _)) {
            // The targeting can be started in one house of a residence and finished in the other, which is not used yet.
            Logger.Information("Teleporter: object {ObjectGid} is not an object of lot {LotGid}; its destination is not set.",
                Logger.Args(message.ObjectID, house.LotGid));
            InformGameClient("A teleporter can only send to a spot in its own house.");

            return;
        }

        if (current is null) {
            SendBlob(house);

            return;
        }

        if (!HouseTeleporters.IsTeleporter(current.TemplateId, out var isPet)) {
            HandleForgedRequest($"set the destination of object {message.ObjectID}, template {current.TemplateId}, which is not a teleporter");

            return;
        }

        if (new PlacedItem { TemplateId = current.TemplateId, X = message.LocX, Y = message.LocY, Z = message.LocZ, Yaw = message.Yaw }.Normalize() is null) {
            HandleForgedRequest($"set the destination of teleporter {message.ObjectID} to a position that is not valid");

            return;
        }

        if (!HouseTeleporters.TryLink(house, message.LocX, message.LocY, message.LocZ, message.Yaw, out var data)) {
            InformGameClient("The teleporter could not be set.");

            return;
        }

        var custom = PlacedItem.EncodeCustom(data);
        var change = house.TryUpdate(current.Id, stored => stored with { Custom = custom });
        if (!change.Done) {
            InformGameClient("The teleporter could not be set.");
            SendBlob(house);

            return;
        }

        Logger.Information("Teleporter: {Kind} {TemplateId} (object {ObjectGid}, house object {GidId}) of lot {LotGid} now sends to {X}, {Y}, {Z}.",
            Logger.Args(isPet ? "pet teleporter" : "teleporter", current.TemplateId, message.ObjectID, change.GidId, house.LotGid,
                message.LocX, message.LocY, message.LocZ));
        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHHOUSINGTELEPORTEROBJECT {
            Data = data,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = change.Revision,
            GIDID = change.GidId,
        }, isSelfless: false, ZoneBroadcastTarget.Players);

        // The client has a handler for the same message, which sets the destination on the teleporter it shows.
        SendToSocket(message);
    }

    private void TeleportInHouse(Wizard wizard, Vector3 target, float yaw) {
        // MSG_SERVERTELEPORT carries the position divided by 4 in signed 16-bit fields, as the client's moves do.
        var direction = yaw % (2 * MathF.PI);
        if (direction < 0) {
            direction += 2 * MathF.PI;
        }

        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
            LocationX = unchecked((ushort) (short) MathF.Round(target.X / 4)),
            LocationY = unchecked((ushort) (short) MathF.Round(target.Y / 4)),
            LocationZ = unchecked((ushort) (short) MathF.Round(target.Z / 4)),
            Direction = (byte) Math.Round(direction / (2 * MathF.PI) * 250),
            MobileID = GetActiveGameObject().m_nMobileID,
        }, isSelfless: false);
        wizard.Location = target;
    }

    private static bool TryAddAll(Wizard wizard, IReadOnlyList<WizClientObjectItem> items) {
        for (var index = 0; index < items.Count; index++) {
            if (!wizard.InventoryBehavior.AddItem(items[index])) {
                RemoveAll(wizard, items.Take(index));

                return false;
            }
        }

        return true;
    }

    private static void RemoveAll(Wizard wizard, IEnumerable<WizClientObjectItem> items) {
        foreach (var item in items) {
            wizard.InventoryBehavior.RemoveItem(item);
        }
    }

    private bool TryGetHouse(string request, out HouseState house) {
        house = _house;
        if (house is null) {
            HandleForgedRequest($"{request} outside a house");

            return false;
        }

        // Only the owner decorates. The client hides the controls from a visitor, so a request is forged.
        if (house.OwnerCharId != GetActiveWizard().CharId) {
            house = null;
            HandleForgedRequest($"{request} in a house that is not theirs");

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
                Logger.Warning("Placed item {ItemId} of character {CharId} has no stored item to delete.", Logger.Args(itemId, charId));
            }
        } catch (Exception exception) {
            Logger.Warning("Could not delete the stored item {ItemId} of character {CharId}: {Reason}",
                Logger.Args(itemId, charId, exception.Message));
        }
    }

    private void RefusePickUp(HouseState house) {
        // The client has faded the object already and the refusal does not bring it back, so the blob rebuilds it.
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_HOUSINGOBJECTNOPICKUP());
        SendBlob(house);
    }

    private void LeaveHouse() {
        if (_house is null) {
            return;
        }

        HouseTeleporters.Forget(_houseCharId);
        HouseRegistry.Release(_house);
        _house = null;
    }

    private void SendBlob(HouseState house) {
        var snapshot = house.Snapshot();
        try {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_SEND_BLOB {
                Data = HousingBlobs.Serialize(BuildBlob(house, snapshot)),
                UserData = snapshot.GidBase,
            });
        } catch (InvalidOperationException exception) {
            Logger.Error("Could not build the blob of lot {LotGid}: {Reason}", Logger.Args(house.LotGid, exception.Message));
        }
    }

    private static Blob BuildBlob(HouseState house, HouseSnapshot snapshot, Func<PlacedItem, PlacedItem> map = null)
        // A removed slot is an empty entry, which the client numbers and does not count.
        => new() {
            m_type = BlobType,
            m_subType = ItemSubType,
            m_versionNumber = snapshot.Revision,
            m_packageNumber = 0,
            m_associatedGID = house.LotGid,
            m_epochDays = HousingBlobs.EpochDaysNow(),
            m_data = new HousingBlob {
                m_housingBlobObjectList = [.. snapshot.Slots.Select(item => item is null
                    ? new HousingBlobObject()
                    : HouseVaults.ToBlobObject(map?.Invoke(item) ?? item))],
            },
        };

    private static bool RequestsItemBlob(BlobRequest request, ulong lotGid)
        => request.m_type == BlobType && request.m_associatedGID == lotGid
            && request.m_blobRequestObjectList?.Any(entry => entry is not null
                && entry.m_subType == ItemSubType && entry.m_packageNumber == 0) == true;

}
