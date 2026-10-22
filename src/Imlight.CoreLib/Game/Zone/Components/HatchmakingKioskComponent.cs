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
 * HATCHMAKING KIOSK
 * ========================================================================
 *
 * PURPOSE:
 * Offers one service option on a hatchmaking kiosk; using it opens the kiosk window, where pets
 * are listed and hatched (see HatchService). Niduus, 2026-10-20.
 *
 * USAGE EXAMPLE:
 * Attached by the component registry to an object whose template has a HatchmakingKioskBehavior.
 *
 * NOTE:
 * Same shape as PvPKioskComponent. The window is opened by sending MSG_REQUESTHATCHMAKING from the
 * player's session.
 *
 * TODO:
 * - Which service option class and display key did the live kiosk use? KioskOption and the kiosk's
 *   own name for now.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class HatchmakingKioskComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const float INTERACTION_TOLERANCE = 2.0f;

    public string ServiceName     => "HatchmakingKioskService";
    public string NpcIcon         => null;
    public string NpcNameKey      => Entity.Template is GameObjectTemplate template ? (string) template.m_displayName : null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => NpcNameKey;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors?.Any(x => x is HatchmakingKioskBehaviorTemplate) == true;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => [
            new KioskOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Kiosk",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!IsInHorizontalRadius(playerObject, ((IServiceComponent) this).DEFAULT_INTERACTION_RADIUS * INTERACTION_TOLERANCE)) {
            Logger.Warning("{CharId} used the hatchmaking kiosk {Kiosk} from too far away.",
                Logger.Args(playerCharacter.CharId, Entity.ActiveGameObject.m_debugName));

            return;
        }

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_OPENHATCHKIOSK { KioskGID = Entity.ActiveGameObject.m_globalID.Full });
    }

}
