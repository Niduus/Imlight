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
 * INTERACT RESPEC
 * ========================================================================
 *
 * PURPOSE:
 * Gives the game's spell trainers a "reset training points" option (the game's RespecOption) beside their spell
 * list. Picking it opens the game's reset window; TrainService takes its answer and TrainingReset does the reset.
 *
 * NOTE:
 * Only the game's own trainers, while [Trainers] RespecAtTrainers is on, and only for a wizard who has points to
 * get back. While it is offered the trainer shows one "Spell List" choice first (as it does beside a quest).
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractRespecComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "Respec";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Training;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => s_displayKey.Value;

    // The game's own line for the option, if it has one; else the words themselves.
    private static readonly System.Lazy<string> s_displayKey = new(() => {
        foreach (var text in new[] { "Reset Training Points", "Training Point Reset", "Reset Training Points?", "Respec" }) {
            if (Locale.KeyOf(text) is { } key) {
                Logger.Information("Respec | The trainers' reset option shows {0} ({1}).", Logger.Args(key, text));

                return key;
            }
        }

        Logger.Information("Respec | The game has no line for the reset option; lines that came close: {0}",
            Logger.Args(string.Join(" | ", Locale.Search("training points", 8).Select(l => $"{l.Key} = {l.Text}"))));

        return "Reset Training Points";
    });

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => TrainingReset.AtTrainers
        && template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
        && InteractTrainerComponent.TestTrainerKindOf(goTemplate) is null;

    /// <summary>Whether this NPC offers a wizard the reset right now.</summary>
    internal static bool Offers(ZoneEntity entity, Wizard wizard)
        => wizard is not null
        && entity.GetComponentOfType<InteractRespecComponent>() is not null
        && entity.GetComponentOfType<InteractTrainerComponent>() is not { TeachesAsListed: true }
        && TrainingReset.PointsBack(wizard) > 0;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        // Shown with the spell list, never in place of it.
        if (!Offers(Entity, wizard) || InteractTrainerComponent.IsShowingSpellOptions(Entity, wizard)) {
            return [];
        }

        return [new RespecOption {
            m_displayKey = DisplayKey,
            m_iconKey = "RespecOption",
            m_serviceName = ServiceName,
        }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (playerCharacter is null) {
            return;
        }

        TrainingReset.OpenWindow(playerActor, playerCharacter, Entity.ActiveGameObject.m_globalID.Full);
    }

}
