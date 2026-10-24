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
 * INTERACT ITEM FINDER COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Makes a placed Backpack Butler, and the banks while [Bank] ItemFinderAtBanks is on, offer the game's Item Finder.
 * Picking the option opens the window through the player's BankService (MSG_BEGINITEMFINDER).
 *
 * USAGE EXAMPLE:
 * Same IServiceComponent + IComponentFactory shape as InteractBankComponent, found by reflection.
 *
 * NOTE:
 * The Backpack Butler (InventoryFinderNPC 1480003) is furniture the client draws from the house blob, so HouseBanks
 * puts the invisible dorm bank on it with a HouseItemFinder_ tag; that one offers only the Item Finder.
 * The option is the game's own ItemFinderOption as its constructor fills it (client 0x141b6dc70): icon ItemFinder,
 * text GUI2_00001074. Its service name ItemFinderService is the one the client checks (0x14172c676).
 * The client's MSG_OpenItemFinder handler is registered once at start-up, not per object, and does not read
 * GlobalID (0x1410a67e0), so the window opens from any object and from the command alike.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractItemFinderComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "ItemFinderService";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Banking;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI2_00001074";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && !goTemplate.m_objectName.IsEmpty // Some templates carry a null object name.
        && (goTemplate.m_templateID == BankConfig.ChestNpcTemplateId
            || BankConfig.ObjectNames.Contains(goTemplate.m_objectName.ToString()));

    private bool OnButler => HouseBanks.IsItemFinderTag(Entity.Info?.m_zoneTag);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) {
        // A bank offers it next to the bank while [Bank] ItemFinderAtBanks is on; the Butler always does.
        if (!OnButler && !BankConfig.ItemFinderAtBanks) {
            return [];
        }

        return [
            new ItemFinderOption {
                m_displayKey = DisplayKey,
                m_iconKey = "ItemFinder",
                m_serviceName = ServiceName,
            }
        ];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        Logger.Information("Item Finder: {CharId} picked the Item Finder of object {ObjectId}.",
            Logger.Args(playerCharacter?.CharId, Entity.ActiveGameObject.m_globalID.Full));
        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_BEGINITEMFINDER { EntityId = Entity.ActiveGameObject.m_globalID });
    }

}
