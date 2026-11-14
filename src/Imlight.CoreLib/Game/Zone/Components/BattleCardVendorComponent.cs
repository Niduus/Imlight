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
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Zone.Components;

/*
 * The Battle Card Vendor in a Beastmoon arena's base (BG-TCVendor-001A, Actaeon). Its template has only a
 * BasicNPCServiceBehavior, so it is known by its name. Its option opens the game's Battle Card Shop with the cards of
 * the wizard's beast form (Battlegrounds/BattleCards.cs); buying and trashing are Game/Services/BattlegroundService.cs.
 */
internal sealed class BattleCardVendorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "BattleCardShopService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_Shopping";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject
           && gameObject.m_objectName.ToString().StartsWith(BattleCards.VENDOR_PREFIX, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new BattleCardShopOption {
                m_displayKey = DisplayKey,
                m_iconKey = "BattleCardShop",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!BG.Enabled || playerCharacter is null) {
            return;
        }

        var list = BattleCards.ShopList(playerCharacter, Entity.ActiveGameObject.m_globalID, out var why);
        if (list is null) {
            playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = why, Modal = 0 });

            return;
        }

        playerActor.Tell(list);
    }

}
