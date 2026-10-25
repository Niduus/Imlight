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
 * INTERACT CRAFTING STATION
 * ========================================================================
 *
 * PURPOSE:
 * Makes the world's crafting stations and anvils, and the NPCs that craft (the recipe vendors, the guild crafters),
 * offer "Craft". Choosing it opens the game's crafting window with the recipes the station allows (Game/Crafting).
 *
 * USAGE EXAMPLE:
 * Auto-registered by reflection like every IServiceComponent; it attaches to a template with an
 * AlchemyStationBehavior.
 *
 * NOTE:
 * Crafting stations that are house furniture are not zone objects; their clicks go to CraftingService.
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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Crafting;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractCraftingStationComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private BasicNPCServiceBehaviorTemplate Service
        => (Entity.Template as GameObjectTemplate)?.m_behaviors?.OfType<BasicNPCServiceBehaviorTemplate>().FirstOrDefault();

    public string ServiceName     => "AlchemyStation";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => Service?.m_interactTitleOverride is { Length: > 0 } title ? title : null;
    public string NpcTextKey      => Service?.m_interactTextOverride is { Length: > 0 } text ? text : null;
    // A person who trades by crafting (Madam Magda's Fortune Coin Trading) gets the vendor's bag over her head; a
    // station or anvil gets none.
    public WizBangs WizBang        => Crafting.Crafting.Enabled && IsPerson ? WizBangs.Shopping : WizBangs.None;
    public string StateName       => null;

    private bool IsPerson => (Entity.Template as GameObjectTemplate)?.m_behaviors?.OfType<NPCBehaviorTemplate>().Any() == true;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_Crafting";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject
        && gameObject.m_behaviors?.Any(behavior => behavior is AlchemyStationBehaviorTemplate) == true;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => !Crafting.Crafting.Enabled ? [] : [
            new AlchemyStationOption {
                m_displayKey = DisplayKey,
                m_iconKey = "Crafting",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (CraftingStation.Of(Entity.Template, Entity.ActiveGameObject.m_globalID.Full) is { } station) {
            Crafting.Crafting.OpenStation(playerActor, playerCharacter, station);
        }
    }

}
