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
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The Monstrodome in a house (Game/MonsterMagic/Monstrodome): the owner's "Set Monsters" window, which takes Summon
/// Monster treasure cards, and the click on the Monstrodome that goes into its arena.
/// </summary>
internal partial class HousingService {

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MONSTERMAGICADDMONSTER))]
    private void ReceiveMonstrodomeAddMonster(WIZARDHOUSING_50_PROTOCOL.MSG_MONSTERMAGICADDMONSTER message) {
        Logger.Information("Monstrodome: character {0} adds the Summon card {1} to object {2}.",
            Logger.Args(GetActiveWizard()?.CharId, message.SpellTemplateID, message.GlobalID));
        try {
            AddMonstrodomeMonster(message.GlobalID, message.SpellTemplateID);
        }
        catch (Exception e) {
            Logger.Error("Monstrodome: adding card {0} to object {1} failed: {2}", Logger.Args(message.SpellTemplateID, message.GlobalID, e));
        }
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MONSTERMAGICERASEMONSTER))]
    private void ReceiveMonstrodomeEraseMonster(WIZARDHOUSING_50_PROTOCOL.MSG_MONSTERMAGICERASEMONSTER message) {
        Logger.Information("Monstrodome: character {0} erases monster {1} from object {2}.",
            Logger.Args(GetActiveWizard()?.CharId, message.MobTemplateID, message.GlobalID));
        try {
            ChangeMonstrodome(message.GlobalID, "erase a Monstrodome monster", monsters => {
                var at = monsters.IndexOf(message.MobTemplateID);
                if (at < 0) {
                    return null;
                }

                monsters.RemoveAt(at);

                return monsters;
            });
        }
        catch (Exception e) {
            Logger.Error("Monstrodome: erasing monster {0} from object {1} failed: {2}", Logger.Args(message.MobTemplateID, message.GlobalID, e));
        }
    }

    private void AddMonstrodomeMonster(ulong objectGid, uint spellId) {
        var wizard = GetActiveWizard();
        if (!Monstrodome.TryReadCard(spellId, out var cardTemplateId, out var monster)) {
            HandleForgedRequest($"add card {spellId} to a Monstrodome, which is not a Summon Monster card");

            return;
        }

        var card = CoreObjectFactory.GetCoreTemplate(cardTemplateId) as SpellTemplate;
        if ((wizard.SpellbookBehavior?.TreasureCardCount(cardTemplateId) ?? 0) == 0) {
            InformGameClient("You have no such Summon Monster card.");

            return;
        }

        var changed = ChangeMonstrodome(objectGid, "add a Monstrodome monster", monsters => {
            if (monsters.Count >= Monstrodome.MAX_MONSTERS) {
                InformGameClient($"The Monstrodome already has {Monstrodome.MAX_MONSTERS} monsters.");

                return null;
            }

            monsters.Add(monster);

            return monsters;
        });
        if (!changed) {
            return;
        }

        // The card goes into the Monstrodome; the window says it cannot be had back.
        if (wizard.SpellbookBehavior.RemoveTreasureCard(cardTemplateId)) {
            WizardCollection.RemoveTreasureCard(wizard, cardTemplateId);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK {
                SpellID = (int) StringHash.Compute((string) card?.m_name ?? ""),
                EnchantmentID = 0,
            });
        }

        Logger.Information("Monstrodome: character {0} put {1} ({2}) in their Monstrodome with card {3}.",
            Logger.Args(wizard.CharId, Monstrodome.NameOf(monster), monster, (string) card?.m_name));
    }

    // Changes a Monstrodome's monsters for its owner and sends the new list to everyone in the house. Returns whether it changed.
    private bool ChangeMonstrodome(ulong objectGid, string request, Func<List<uint>, List<uint>> change) {
        if (!Monstrodome.Enabled) {
            InformGameClient("The Monstrodome is switched off on this server.");

            return false;
        }

        if (!TryGetHouse(request, out var house)) {
            return false;
        }

        if (!house.TryFindByGid(objectGid, out var current, out _)) {
            HandleForgedRequest($"{request} on object {objectGid}, which is not an object of this house");

            return false;
        }

        if (current is null) {
            SendBlob(house);

            return false;
        }

        if (!Monstrodome.IsArena(current.TemplateId, out _)) {
            HandleForgedRequest($"{request} on object {objectGid}, template {current.TemplateId}, which is not a Monstrodome");

            return false;
        }

        var hadEntry = current.Custom is not null;
        byte[] data = [];
        var result = house.TryUpdate(current.Id, stored => {
            var next = change(Monstrodome.Decode(PlacedItem.DecodeCustom(stored.Custom)));
            if (next is null) {
                return null;
            }

            data = Monstrodome.Encode(next);

            return stored with { Custom = PlacedItem.EncodeCustom(data) };
        });
        if (!result.Done) {
            SendBlob(house);

            return false;
        }

        Logger.Information("Monstrodome: object {0} (house object {1}) of lot {2} now holds {3}.",
            Logger.Args(current.TemplateId, result.GidId, house.LotGid, string.Join(", ", Monstrodome.Decode(data))));

        // As with Castle Magic: the client takes the patch only for a custom entry, so an older object gets one first.
        var revision = result.Revision;
        if (!hadEntry) {
            BroadcastBlob(house);
            var bumped = house.TryUpdate(current.Id, stored => stored with { });
            if (!bumped.Done) {
                return true;
            }

            revision = bumped.Revision;
        }

        ZoneBroadcast(new WIZARDHOUSING_50_PROTOCOL.MSG_PATCHMONSTERARENA {
            Data = data,
            SubType = ItemSubType,
            PackageNumber = 0,
            VersionNumber = revision,
            GIDID = result.GidId,
        }, isSelfless: false, ZoneBroadcastTarget.Players);

        return true;
    }

    // A click on a placed Monstrodome goes into its arena, where its monsters wait. Every player in the house may go;
    // the arena is the owner's own copy, which starts fresh once everyone has left ([Instance] ResetWhenEmpty).
    private bool TryEnterMonstrodome(HouseState house, PlacedItem item) {
        if (!Monstrodome.IsArena(item.TemplateId, out var zone)) {
            return false;
        }

        var wizard = GetActiveWizard();
        if (!Monstrodome.Enabled) {
            InformGameClient("The Monstrodome is switched off on this server.");

            return true;
        }

        var monsters = Monstrodome.Decode(PlacedItem.DecodeCustom(item.Custom));
        Monstrodome.RememberReturn(wizard.CharId, house.OwnerCharId, house.Template.TemplateId);
        Logger.Information("Monstrodome: character {0} goes into {1} of lot {2} with {3} monster(s): {4}.",
            Logger.Args(wizard.CharId, zone, house.LotGid, monsters.Count, string.Join(", ", monsters)));
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = Monstrodome.START_LOCATION,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = house.OwnerCharId,
            Variant = Monstrodome.VariantFor(monsters),
        });

        return true;
    }

}
