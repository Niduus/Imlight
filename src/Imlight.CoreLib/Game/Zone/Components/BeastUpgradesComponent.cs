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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Zone.Components;

/*
 * The Beastmoon Upgrades NPC at the pavilion (a BattlegroundPolymorphImprovementBehavior): opens the game's
 * PolymorphImprovement window, where a form's level and tier go up (Battlegrounds/BeastUpgrades.cs).
 */
internal sealed class BeastUpgradesComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "BattlegroundPolymorphImprovementService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => "GUI_BattlegroundPolymorphImprovement";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObject
           && gameObject.m_behaviors?.Any(b => b is BattlegroundPolymorphImprovementBehaviorTemplate) == true;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new BattlegroundPolymorphImprovementOption {
                m_displayKey = DisplayKey,
                m_iconKey = "BattlegroundPolymorphImprovement",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!BG.Enabled || playerCharacter is null) {
            return;
        }

        // The window reads each form's level from the wizard's progress in the event (BeastTable); without it the game
        // shows a wrong level.
        playerActor.Tell(Events.EventButton.InfoMessage(playerCharacter, 0));
        if (BeastUpgrades.Window(playerCharacter, Entity.ActiveGameObject.m_globalID) is { } window) {
            playerActor.Tell(window);
        }
    }

}
