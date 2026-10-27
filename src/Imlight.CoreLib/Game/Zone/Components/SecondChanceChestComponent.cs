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
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A Second Chance Chest a cheating boss left (SecondChanceChests.cs): using it opens the game's paid loot roll window
/// for the wizards of that battle, and it vanishes after [SecondChanceChest] Seconds.
/// </summary>
internal sealed class SecondChanceChestComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    private const string EXPIRE_TIMER_KEY = "SecondChanceChestExpire";

    public string ServiceName => "PaidLootRollService";
    public string NpcIcon => "GUI/QuestButtons/Use_Monster_Chest.dds";
    // "Second Chance Chest" and "Press X to Open".
    public string NpcNameKey => "GUI2_00000423";
    public string NpcTextKey => "GUI_ChestInteract";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => "";
    public string InteractWizBang => "";
    public string DisplayKey => "";

    public ITimerScheduler Timers { get; set; }

    private ulong ChestId => Entity.ActiveGameObject?.m_globalID.Full ?? 0;

    // The game's monster chests, which a cheating boss's chest is made from.
    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
           && goTemplate.m_objectName.ToString() is var name
           && (name.Contains("MonsterChest") || name.Contains("BossChest"));

    public override void OnStart() {
        if (SecondChanceChests.IsChest(ChestId)) {
            Timers.StartSingleTimer(EXPIRE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT(), SecondChanceChests.Lifetime);
        }
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        var open = playerCharacter is null
            ? SecondChanceChests.IsChest(ChestId)
            : SecondChanceChests.UsesLeft(ChestId, playerCharacter.CharId) > 0;

        return open
            ? [new InteractableOption { m_serviceName = ServiceName, m_displayKey = DisplayKey, m_iconKey = NpcIcon }]
            : [];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (playerCharacter is not null) {
            SecondChanceChests.Prompt(playerActor, playerCharacter, ChestId);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT))]
    private void ReceiveExpired(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT _) {
        SecondChanceChests.Remove(ChestId);
        Entity.DeleteObject();
    }

}
