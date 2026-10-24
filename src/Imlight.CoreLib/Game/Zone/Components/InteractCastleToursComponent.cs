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
 * INTERACT CASTLE TOURS COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Makes Myrella Windspar (Housing_CastleTours_NPC in WizardCity/Interiors/WC_CastleTours) and any other NPC with a
 * CastleToursBehavior offer Castle Tours. Picking the option opens the client's Castle Tours window.
 *
 * USAGE EXAMPLE:
 * Same IServiceComponent + IComponentFactory shape as InteractDyeShopComponent, found by reflection.
 * The window's requests are handled by CastleToursService.
 *
 * NOTE:
 * - The client opens the window from MSG_CASTLETOURSREQUEST, which its CastleToursBehavior of the NPC handles
 *   (0x140efa440). HasNominatedHouses shows the Hall of Fame button, which this server does not have.
 * - The window passes GlobalID (the NPC) back in every request.
 * - The player is not put into the Shop state: the window has no close message that would take them out of it.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractCastleToursComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "CastleTours";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.CastleTours;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_CastleTours";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors is not null
        && goTemplate.m_behaviors.Any(behavior => behavior is CastleToursBehaviorTemplate);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new CastleToursOption {
                m_displayKey = DisplayKey,
                m_iconKey = "CastleTours",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        Logger.Information("Castle Tours | {0} opens the Castle Tours window at {1}.",
            Logger.Args(playerCharacter?.CharId, Entity.ActiveGameObject.m_globalID.Full));
        playerActor.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUEST {
            GlobalID = Entity.ActiveGameObject.m_globalID.Full,
            HasNominatedHouses = 0,
        });
    }

}
