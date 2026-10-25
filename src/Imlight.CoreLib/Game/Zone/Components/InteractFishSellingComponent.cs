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
 * INTERACT FISH SELLING
 * ========================================================================
 *
 * PURPOSE:
 * Gives Lucky Hookline, and every shop vendor while [Fishing] SellFishAtAllShops is on (off by default), the game's "Sell Fish" option
 * (FishSellingOption). Picking it opens the game's fish seller window; FishingService answers the window's basket
 * request and its Sell button (MSG_SELLFISHREQUEST).
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Fishing;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractFishSellingComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "FishSellingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.SellFish;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_SellFish";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => FishingSettings.Enabled
        && template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && (IsFishingNpc(goTemplate) || (FishingSettings.SellAtShops && InteractVendorComponent.ShouldAttachToEntity(goTemplate)));

    /// <summary>Lucky Hookline, the fisherman in the Commons (and at the party room).</summary>
    internal static bool IsFishingNpc(GameObjectTemplate template)
        => InteractTrainerComponent.TestTrainerKindOf(template) == InteractTrainerComponent.TestTrainerKind.Fishing;

    /// <summary>Whether this NPC offers fish selling to a wizard right now: always beside a trainer's spell list, never inside it.</summary>
    internal static bool Offers(ZoneEntity entity, Wizard wizard)
        => wizard is not null && entity.GetComponentOfType<InteractFishSellingComponent>() is not null;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (InteractTrainerComponent.IsShowingSpellOptions(Entity, wizard)) {
            return [];
        }

        return [new FishSellingOption {
            m_displayKey = DisplayKey,
            m_iconKey = NpcIcon,
            m_serviceName = ServiceName,
        }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (playerCharacter is null) {
            return;
        }

        playerActor.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_SELLFISHOPEN {
            GlobalID = Entity.ActiveGameObject.m_globalID.Full,
            FishTemplateID = 0,
            CatchOfTheDayBonus = 0,
        });
        // The window lists the basket the game was last sent, so it gets the basket as it is now.
        playerActor.Tell(FishingService.BasketMessage(playerCharacter));
    }

}
