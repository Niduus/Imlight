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
 * INTERACT BANK COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Makes the bank objects offer the bank. Picking the option opens the bank window through the player's BankService.
 *
 * USAGE EXAMPLE:
 * Same IServiceComponent + IComponentFactory shape as InteractDyeShopComponent, found by reflection.
 * The requests of the window are handled by BankService.
 *
 * NOTE:
 * Attaches to any object whose object name is in BankConfig.ObjectNames (the dorm bank WC_RVW-Bank, the house
 * bank chests and [Bank] ObjectNames), NPC or not, and only while both bank capacities are set, so the bank is
 * not offered until the owner sets them. A bank chest placed in a house is furniture the server has no object
 * for, so HouseBanks puts the dorm bank, an invisible NPC, on it.
 * The option's service name is WizBankingService, the name the client checks when the player picks an option
 * (it closes the mail window for WizBankingService and ItemFinderService, client 0x14172c64a).
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractBankComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizBankingService";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Banking;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_Banking";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && !goTemplate.m_objectName.IsEmpty // Some templates carry a null object name.
        && BankConfig.ObjectNames.Contains(goTemplate.m_objectName.ToString())
        && BankConfig.IsEnabled;

    public override void OnStart()
        => Logger.Information("Bank: {Name} (object {ObjectId}) offers the bank in {Zone}.",
            Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName.ToString(), Entity.ActiveGameObject.m_globalID.Full,
                Entity.Zone?.ZoneName));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        // The invisible bank on a Backpack Butler only lends it its Item Finder (InteractItemFinderComponent).
        => HouseBanks.IsItemFinderTag(Entity.Info?.m_zoneTag)
            ? []
            : [
                new WizBankingOption {
                    m_displayKey = DisplayKey,
                    m_iconKey = "Banking",
                    m_serviceName = ServiceName,
                }
            ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        // The bank data lives with the session, which streams it and then opens the window.
        Logger.Information("Bank: {CharId} picked the bank option of object {ObjectId}.",
            Logger.Args(playerCharacter?.CharId, Entity.ActiveGameObject.m_globalID.Full));
        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_BEGINBANKSESSION { EntityId = Entity.ActiveGameObject.m_globalID });
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    /// <summary>
    /// Whether a player stands close enough to the bank NPC to use the bank. This is the radius at which the
    /// client is offered the NPC's options, measured on the ground plane.
    /// </summary>
    /// <param name="playerObject">The player's game object.</param>
    /// <returns>True when the player is within the interaction radius.</returns>
    internal bool IsInServiceRange(CoreObject playerObject)
        => IsInHorizontalRadius(playerObject, ((IServiceComponent) this).DEFAULT_INTERACTION_RADIUS);

    private void SendPlayerIntoWizbang(ulong playerObjID) {
        // Create the wiz bang message, and wrap it in a broadcast message.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) WizBang,
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = wizBangMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

    private void SendPlayerIntoState(ulong playerObjID) {
        // Create the change state message, and wrap it in a broadcast message.
        var changeStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            State = StringHash.Compute(StateName),
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = changeStateMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

}
