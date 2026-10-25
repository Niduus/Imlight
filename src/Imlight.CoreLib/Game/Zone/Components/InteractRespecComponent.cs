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
 * Gives Mr. Lincoln a "reset training points" option (the game's RespecOption), and only him (Niduus 2026-10-24). Picking it opens the game's reset window; TrainService takes its answer and TrainingReset does the reset.
 *
 * NOTE:
 * Only Mr. Lincoln, while [Trainers] RespecAtTrainers is on. He is no trainer any more (Niduus 2026-10-25: his spell
 * list showed empty), so the reset is his only choice.
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

    // The game's own keys its other menu options follow (GUI_DyeShop, GUI_PotionShop...), then lines with the words.
    private static readonly string[] s_displayKeys = [
        "GUI_Respec", "GUI_RespecService", "GUI_RespecCrowns", "GUI_RespecOption", "GUI_ResetTrainingPoints",
        "GUI_TrainingPointReset", "GUI_TrainingReset",
    ];

    // The game's own line for the option. The client shows a blank button for a key it does not have (Niduus
    // 2026-10-25), so this always ends on a line the game has.
    private static readonly System.Lazy<string> s_displayKey = new(() => {
        var gui = Locale.Table("GUI");
        var respecKeys = gui is null ? [] : gui.Keys.Where(k => k.Contains("Respec", System.StringComparison.OrdinalIgnoreCase))
            .Select(k => $"GUI_{k} = {gui[k]}").ToList();
        Logger.Information("Respec | GUI lines named Respec: {0}", Logger.Args(respecKeys.Count > 0 ? string.Join(" | ", respecKeys) : "none"));

        var key = s_displayKeys.FirstOrDefault(k => Locale.GetEnglishName(k).Length > 0)
            ?? new[] { "Reset Training Points", "Training Point Reset", "Reset Training", "Respec" }.Select(Locale.KeyOf).FirstOrDefault(k => k is not null)
            ?? Locale.Search("reset training", 20).Where(l => l.Text.Length <= 40).Select(l => l.Key).FirstOrDefault()
            ?? Locale.KeyOf("Reset")
            ?? "GUI_00000979";
        Logger.Information("Respec | Mr. Lincoln's reset option shows {0} (\"{1}\").", Logger.Args(key, Locale.GetEnglishName(key)));

        return key;
    });

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => TrainingReset.AtTrainers
        && template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && InteractTrainerComponent.IsLincoln(goTemplate);

    /// <summary>Whether this NPC offers a wizard the reset right now: Mr. Lincoln always does.</summary>
    internal static bool Offers(ZoneEntity entity, Wizard wizard)
        => wizard is not null && entity.GetComponentOfType<InteractRespecComponent>() is not null;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (!Offers(Entity, wizard)) {
            return [];
        }

        return [new RespecOption {
            m_displayKey = DisplayKey,
            m_iconKey = "RespecService",
            m_serviceName = ServiceName,
        }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (playerCharacter is null) {
            return;
        }

        if (TrainingReset.PointsBack(playerCharacter) <= 0) {
            TrainingReset.Tell(playerActor, "You have no training points to get back. Spells of your own school, free spells and spells from quests stay.");

            return;
        }

        TrainingReset.OpenWindow(playerActor, playerCharacter, Entity.ActiveGameObject.m_globalID.Full);
    }

}
