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


using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The way home in a Monstrodome's arena (the HousingReturn object, template 1397386): pressing X on it goes back to
/// the house the player came from (ZoneService, MSG_LEAVEMONSTRODOME). Its InteractableBehavior names no destination.
/// </summary>
internal sealed class InteractHousingReturnComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const string DOOR_TEXT_KEY = "GUI_EnterDoor";

    public string ServiceName => "Interact";
    public string NpcIcon => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey => (Entity.Template as GameObjectTemplate)?.m_displayName;
    public string NpcTextKey => DOOR_TEXT_KEY;
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate { m_templateID: Monstrodome.HOUSING_RETURN_TEMPLATE };

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter)
        => Monstrodome.IsArenaZone(Entity.Zone?.ZonePath) ? [new InteractableOption { m_serviceName = ServiceName }] : [];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!Monstrodome.IsArenaZone(Entity.Zone?.ZonePath)) {
            return;
        }

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_LEAVEMONSTRODOME());
    }

}
