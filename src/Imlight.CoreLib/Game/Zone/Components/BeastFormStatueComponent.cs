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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Zone.Components;

/*
 * The beast statues in a Beastmoon arena's base (BG_PolyStatue_Cyclops and the others): a BasicNPCServiceBehavior
 * with a BGPolymorphSelectBehaviorTemplate naming the beast (m_polymorphCategory "Cyclops"). Choosing the statue's
 * option tells the game MSG_BGOPENPOLYMORPHSELECT, and the game opens its Beast Form Select window
 * (BGPolymorphSelect.gui) on that beast, with the forms of the match the queue named (Battlegrounds.QueueMessage). The
 * window asks the server about each form and sends the choice (Game/Services/BattlegroundService.cs).
 * PolymorphCategoryAvailable says whether the beast is free: in PvP a team has one wizard of each beast; here every
 * beast is free.
 */
internal sealed class BeastFormStatueComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "BGPolymorphSelectService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => null;
    public string InteractWizBang => null;
    // GUI_00008988 is what the statue's own BasicNPCServiceBehaviorTemplate shows (m_interactTextOverride).
    public string DisplayKey      => "GUI_00008988";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject && gameObject.m_behaviors?.Any(b => b is BGPolymorphSelectBehaviorTemplate) == true;

    private string Category
        => (Entity.Template as GameObjectTemplate)?.m_behaviors?.OfType<BGPolymorphSelectBehaviorTemplate>().FirstOrDefault()?.m_polymorphCategory.ToString() ?? "";

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new BGPolymorphSelectOption {
                m_displayKey = DisplayKey,
                m_iconKey = "BGPolymorphSelect",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!BG.Enabled) {
            return;
        }

        playerActor.Tell(new WIZARD2_53_PROTOCOL.MSG_BGOPENPOLYMORPHSELECT {
            npcGlobalID = Entity.ActiveGameObject.m_globalID,
            PolymorphCategory = Category,
            PolymorphCategoryAvailable = 1,
        });
    }

}
