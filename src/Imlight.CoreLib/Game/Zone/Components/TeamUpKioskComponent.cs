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
 * TEAM UP KIOSK
 * ========================================================================
 *
 * PURPOSE:
 * The Join A Team kiosk: using it opens the Join a Team window, the teams players asked for at dungeon sigils
 * (Game/TeamUp).
 *
 * USAGE EXAMPLE:
 * Attached by the component registry to a TeamUpHelperKiosk (1335049) or its house version (1431040).
 *
 * NOTE:
 * No zone of the game places the kiosk, so it is one placed in the Customs tab or a house. `.teamup` opens the same
 * window anywhere.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class TeamUpKioskComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint KIOSK_TEMPLATE_ID = 1335049;
    private const uint HOUSE_KIOSK_TEMPLATE_ID = 1431040;

    public string ServiceName     => "TeamUpKioskService";
    public string NpcIcon         => null;
    public string NpcNameKey      => Entity.Template is GameObjectTemplate template ? (string) template.m_displayName : null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    // "Join A Team"
    public string DisplayKey      => "GUI2_00000013";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate { m_templateID: KIOSK_TEMPLATE_ID or HOUSE_KIOSK_TEMPLATE_ID };

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => [
            new KioskOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Kiosk",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex)
        => playerActor.Tell(new OpenTeamHelp());

}
