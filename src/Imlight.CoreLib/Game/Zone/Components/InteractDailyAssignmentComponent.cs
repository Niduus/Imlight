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
 * INTERACT DAILY ASSIGNMENT COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * Gives Aegon Statz (WC-RAV-NPC11, Ravenwood) the Daily Assignment option. Picking it opens the game's Daily
 * Assignment window through the player's DailyAssignmentService.
 *
 * USAGE EXAMPLE:
 * Same IServiceComponent + IComponentFactory shape as InteractBankComponent, found by reflection.
 *
 * NOTE:
 * The option class is the game's DailyQuestOption. Its service name and display key are guesses: the name is ours,
 * the key is the game's line reading "Daily Assignment" when it has one. [DailyAssignments] NpcObjectNames lists the
 * NPCs that offer it (comma separated object names).
 *
 * TODO:
 *
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DailyAssignments;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractDailyAssignmentComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private static readonly HashSet<string> s_npcNames = DailyAssignmentData.NpcObjectNames
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string s_displayKey;

    public string ServiceName     => "DailyQuestService";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => s_displayKey ??= Locale.KeyOf("Daily Assignment") ?? Locale.KeyOf("Daily Assignments") ?? "Daily Assignment";

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && !goTemplate.m_objectName.IsEmpty
        && s_npcNames.Contains(goTemplate.m_objectName.ToString())
        && DailyAssignmentData.IsEnabled;

    public override void OnStart()
        => Logger.Information("Daily Assignments: {Name} (object {ObjectId}) offers the Daily Assignment in {Zone}.",
            Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName.ToString(), Entity.ActiveGameObject.m_globalID.Full,
                Entity.Zone?.ZoneName));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (!DailyAssignmentData.CanTakeAssignments(playerCharacter, out _)) {
            return [];
        }

        return [
            new DailyQuestOption {
                m_displayKey = DisplayKey,
                m_iconKey = "DailyQuest",
                m_serviceName = ServiceName,
            }
        ];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        Logger.Information("Daily Assignments: {CharId} picked the Daily Assignment option of object {ObjectId}.",
            Logger.Args(playerCharacter?.CharId, Entity.ActiveGameObject.m_globalID.Full));
        playerActor.Tell(new DailyAssignmentOpen());
    }

}
