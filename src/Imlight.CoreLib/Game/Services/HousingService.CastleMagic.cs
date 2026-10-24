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
 * HOUSING SERVICE: CASTLE MAGIC
 * ========================================================================
 *
 * PURPOSE:
 * Answers the Castle Magic messages of the clients in a house: stores the spells the owner casts onto Castle Magic
 * objects, hands out the house's random numbers, moves the players the magic teleports, and passes the state of the
 * magic between the players in the house.
 *
 * USAGE EXAMPLE:
 * Internal message handlers of HousingService, which holds the house the player is in.
 *
 * NOTE:
 * - What the data looks like and what the client does with it is in Game/Housing/HouseCastleMagic.
 * - Only the owner sets spells. Any player in the house may ask for a teleport, because the magic runs in the client
 *   of whoever sets it off.
 * - A spell the server does not know as a Castle Magic spell is still stored, because the client is the one that reads it;
 *   it is logged so a test log shows it.
 * - Every message is logged with its fields, so a log shows what the game sends.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class HousingService {

    private CastleMagicVisitor _castleMagicVisitor;

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCHANGEACTION))]
    private void ReceiveCastleMagicChangeAction(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCHANGEACTION message) {
        Logger.Information("Castle Magic: character {CharId} sets action {Action} of object {ObjectGid} to spell {SpellId}, target {TargetId} at {X}, {Y}, {Z}.",
            Logger.Args(GetActiveWizard().CharId, message.ActionNumber, message.GlobalID, message.SpellTemplateID, message.TargetTemplateID,
                message.LocX, message.LocY, message.LocZ));
        try {
            ChangeCastleMagicAction(message);
        } catch (Exception exception) {
            Logger.Error("Castle Magic: setting action {Action} of object {ObjectGid} failed: {Error}",
                Logger.Args(message.ActionNumber, message.GlobalID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTRANDOM))]
    private void ReceiveCastleMagicRequestRandom(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTRANDOM message) {
        var house = _house;
        if (house is null) {
            Logger.Information("Castle Magic: character {CharId} asked for random numbers outside a house.", Logger.Args(GetActiveWizard().CharId));

            return;
        }

        Logger.Debug("Castle Magic: character {CharId} gets the random numbers of lot {LotGid}.", Logger.Args(GetActiveWizard().CharId, house.LotGid));
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTRANDOM { Buffer = HouseCastleMagic.RandomNumbers(house.LotGid) });
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCURRENTSTATE))]
    private void ReceiveCastleMagicCurrentState(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCURRENTSTATE message) {
        var house = _house;
        var target = house is null ? null : HouseCastleMagic.Find(house.LotGid, message.TargetPlayer);
        if (target is null) {
            Logger.Information("Castle Magic: character {CharId} sent the state of the magic to {TargetGid}, who is not in the house.",
                Logger.Args(GetActiveWizard().CharId, message.TargetPlayer));

            return;
        }

        byte[] buffer = message.ChangeBuffer;
        Logger.Information("Castle Magic: character {CharId} sends the state of the magic ({Length} bytes, zone time {ZoneTime}, orbs {Orbs}) to character {TargetCharId}.",
            Logger.Args(GetActiveWizard().CharId, buffer?.Length ?? 0, message.ZoneTime, message.OrbFlags, target.Wizard.CharId));
        target.Session.Tell(message);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCLEAR))]
    private void ReceiveCastleMagicClear(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCLEAR message) {
        var house = _house;
        var self = _castleMagicVisitor;
        if (house is null || self is null) {
            return;
        }

        var others = HouseCastleMagic.Others(house.LotGid, self.GameObjectGid);
        Logger.Information("Castle Magic: character {CharId} clears the magic for the {Count} other player(s) in lot {LotGid}.",
            Logger.Args(self.Wizard.CharId, others.Count, house.LotGid));
        foreach (var other in others) {
            other.Session.Tell(message);
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTPLAYERTELEPORT))]
    private void ReceiveCastleMagicRequestPlayerTeleport(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTPLAYERTELEPORT message) {
        Logger.Information("Castle Magic: character {CharId} teleports player {TargetGid} (object {ObjectGid}) to {X}, {Y}, {Z}, yaw {Yaw}.",
            Logger.Args(GetActiveWizard().CharId, message.TargetPlayerGID, message.TargetGID, message.LocX, message.LocY, message.LocZ, message.Yaw));
        try {
            TeleportByCastleMagic(message);
        } catch (Exception exception) {
            Logger.Error("Castle Magic: teleporting player {TargetGid} failed: {Error}", Logger.Args(message.TargetPlayerGID, exception));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICORB))]
    private void ReceiveCastleMagicOrb(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICORB message)
        => Logger.Information("Castle Magic: character {CharId} now carries orbs {Orbs}.", Logger.Args(GetActiveWizard().CharId, message.OrbFlags));

    // These ask the server to change the house zone or to use an object for the player, which the server does not do yet.
    // They are logged so a test log shows when the game sends one.
    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTUSE))]
    private void ReceiveCastleMagicRequestUse(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTUSE message)
        => Logger.Information("Castle Magic: character {CharId} asks to use item {ItemGid}, which the server does not do yet.",
            Logger.Args(GetActiveWizard().CharId, message.ItemGID));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTALLOWMOUNTS))]
    private void ReceiveCastleMagicRequestAllowMounts(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTALLOWMOUNTS message)
        => Logger.Information("Castle Magic: character {CharId} asks to allow mounts ({State}), which the server does not do yet.",
            Logger.Args(GetActiveWizard().CharId, message.State));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTALLOWPVP))]
    private void ReceiveCastleMagicRequestAllowPvp(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTALLOWPVP message)
        => Logger.Information("Castle Magic: character {CharId} asks to allow PvP ({State}), which the server does not do yet.",
            Logger.Args(GetActiveWizard().CharId, message.State));

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTPVPSTATE))]
    private void ReceiveCastleMagicRequestPvpState(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTPVPSTATE message)
        => Logger.Information("Castle Magic: character {CharId} asks for PvP state {State} with modifier {Modifier}, which the server does not do yet.",
            Logger.Args(GetActiveWizard().CharId, message.State, (string) message.ModifierName));

    private void ChangeCastleMagicAction(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICCHANGEACTION message) {
        if (!TryGetHouse("set a Castle Magic spell", out var house)) {
            return;
        }

        if (!house.TryFindByGid(message.GlobalID, out var current, out _)) {
            HandleForgedRequest($"set a Castle Magic spell on object {message.GlobalID}, which is not an object of this house");

            return;
        }

        if (current is null) {
            // The object is gone. The client acted on an older blob, so give it the real state.
            SendBlob(house);

            return;
        }

        if (!HouseCastleMagic.IsObject(current.TemplateId)) {
            HandleForgedRequest($"set a Castle Magic spell on object {message.GlobalID}, template {current.TemplateId}, which is not a Castle Magic object");

            return;
        }

        if (message.SpellTemplateID != 0 && !HouseCastleMagic.IsSpell(message.SpellTemplateID, out _)) {
            Logger.Warning("Castle Magic: spell {SpellId} is not a Castle Magic spell the server knows; it is stored as the client sent it.",
                Logger.Args(message.SpellTemplateID));
        }

        var action = new CastleMagicAction(message.SpellTemplateID, message.TargetTemplateID, message.LocX, message.LocY, message.LocZ);
        var hadEntry = current.Custom is not null;
        byte[] data = [];
        string refusal = null;
        var change = house.TryUpdate(current.Id, stored => HouseCastleMagic.TrySetAction(stored, message.ActionNumber, action, out data, out refusal)
            ? stored with { Custom = PlacedItem.EncodeCustom(data) }
            : null);
        if (!change.Done) {
            if (refusal is not null) {
                HandleForgedRequest($"set {refusal}");
            } else {
                InformGameClient("That Castle Magic spell could not be set.");
            }

            SendBlob(house);

            return;
        }

        var actions = HouseCastleMagic.Decode(data);
        Logger.Information("Castle Magic: object {TemplateId} (house object {GidId}) of lot {LotGid} now has {Spells} spell(s) in {Count} action(s).",
            Logger.Args(current.TemplateId, change.GidId, house.LotGid, HouseCastleMagic.CountSpells(actions), actions.Count));

        // The client takes the patch only for a custom entry. An object placed before it had one gets it from the blob,
        // and the patch a revision later loads the spells into the object the client already built.
        var revision = change.Revision;
        if (!hadEntry) {
            BroadcastBlob(house);
            var bumped = house.TryUpdate(current.Id, stored => stored with { });
            if (!bumped.Done) {
                return;
            }

            revision = bumped.Revision;
        }

        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHCASTLEMAGIC {
            Data = data,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = revision,
            GIDID = change.GidId,
        }, isSelfless: false, ZoneBroadcastTarget.Players);
    }

    private void TeleportByCastleMagic(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLEMAGICREQUESTPLAYERTELEPORT message) {
        var house = _house;
        var self = _castleMagicVisitor;
        if (house is null || self is null) {
            HandleForgedRequest("teleport a player with Castle Magic outside a house");

            return;
        }

        var target = message.TargetPlayerGID == 0 || message.TargetPlayerGID == self.GameObjectGid
            ? self
            : HouseCastleMagic.Find(house.LotGid, message.TargetPlayerGID);
        if (target is null) {
            Logger.Information("Castle Magic: player {TargetGid} is not in lot {LotGid}, so they are not teleported.",
                Logger.Args(message.TargetPlayerGID, house.LotGid));

            return;
        }

        // MSG_SERVERTELEPORT carries a quarter of each coordinate in a signed 16-bit field.
        if (!IsTeleportable(message.LocX) || !IsTeleportable(message.LocY) || !IsTeleportable(message.LocZ) || !float.IsFinite(message.Yaw)) {
            HandleForgedRequest("teleport a player with Castle Magic to a position that is not valid");

            return;
        }

        var mobileId = ReferenceEquals(target, self) ? GetActiveGameObject().m_nMobileID : target.MobileId;
        TeleportInHouse(target.Wizard, mobileId, new Vector3(message.LocX, message.LocY, message.LocZ), message.Yaw);
    }

    private static bool IsTeleportable(float coordinate) => float.IsFinite(coordinate) && MathF.Abs(coordinate) <= short.MaxValue * 4f;

    private void EnterCastleMagic(HouseState house, Wizard wizard) {
        try {
            _castleMagicVisitor = new CastleMagicVisitor(SessionActor.ActorRef, wizard, GetActiveGameObject());
            HouseCastleMagic.Enter(house.LotGid, _castleMagicVisitor);
        } catch (Exception exception) {
            Logger.Error("Castle Magic: character {CharId} could not be counted into lot {LotGid}: {Error}",
                Logger.Args(wizard.CharId, house.LotGid, exception));
        }
    }

    private void LeaveCastleMagic(HouseState house) {
        var visitor = _castleMagicVisitor;
        _castleMagicVisitor = null;
        if (visitor is null || house is null) {
            return;
        }

        try {
            HouseCastleMagic.Leave(house.LotGid, visitor.GameObjectGid);
            if (!visitor.Announced) {
                return;
            }

            foreach (var other in HouseCastleMagic.Others(house.LotGid, visitor.GameObjectGid)) {
                other.Session.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_HOUSINGZONEPLAYER { HousingPlayer = visitor.GameObjectGid, AddPlayer = 0 });
            }
        } catch (Exception exception) {
            Logger.Error("Castle Magic: character {CharId} could not be counted out of lot {LotGid}: {Error}",
                Logger.Args(visitor.Wizard.CharId, house.LotGid, exception));
        }
    }

    // The players already in the house hear of a new one once the new one walks, so the new client has loaded the house
    // by the time the state of the magic comes.
    private void AnnounceCastleMagicVisitor(HouseState house) {
        var visitor = _castleMagicVisitor;
        if (visitor is null || visitor.Announced) {
            return;
        }

        visitor.Announced = true;
        var others = HouseCastleMagic.Others(house.LotGid, visitor.GameObjectGid);
        if (others.Count == 0) {
            return;
        }

        Logger.Information("Castle Magic: the {Count} other player(s) in lot {LotGid} hear that character {CharId} arrived.",
            Logger.Args(others.Count, house.LotGid, visitor.Wizard.CharId));
        foreach (var other in others) {
            other.Session.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_HOUSINGZONEPLAYER { HousingPlayer = visitor.GameObjectGid, AddPlayer = 1 });
        }
    }

}
