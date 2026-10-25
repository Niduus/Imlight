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
 * WOODEN CHEST COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * One use of a wooden chest in the world opens it: the Open animation plays once, the gold is added to the wizard's
 * (never set), and the chest gives the basic drops of its world (WoodenChestLoot.cs, Imlight.ini [WoodenChests]).
 *
 * NOTE:
 * The chest's template also has an Open state option; InteractObjectStateComponent leaves wooden chests to this
 * component, or the first use only played the animation and a second one paid out.
 *
 * Created by: Phill
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class WoodenChestComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    private static readonly uint[] s_treasureChestTemplateIDs = [1233729, 77825, 77884, 77826, 77827, 1562742, 77823, 470099];
    private static readonly uint s_openSoundTemplateID = 1374674914;

    // How long the chest stays after it opens, so the Open animation plays out (Imlight.ini [WoodenChests]).
    private static TimeSpan OpenAnimationTime
        => TimeSpan.FromSeconds(Math.Clamp(ConfigurationManager.GetValue("WoodenChests.OpenSeconds", 1.5), 0.1, 30));

    public string ServiceName => "Interact";
    public string NpcIcon => "GUI/QuestButtons/Art_Quest_Chest_Wood.dds";
    public string NpcNameKey => "WizardGameObjects_00000054";
    public string NpcTextKey => "GUI_ChestInteract";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => "Open";
    public string InteractWizBang => "";
    public string DisplayKey => "";

    public ITimerScheduler Timers { get; set; }

    private bool _hasInteracted = false;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && (s_treasureChestTemplateIDs.Contains(goTemplate.m_templateID) || WoodenChestLoot.IsWoodenChest(goTemplate.m_templateID));

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => _hasInteracted
            ? []
            : [
                new ServiceOptionBase() {
                    m_displayKey = DisplayKey,
                    m_forceInteract = false,
                    m_iconKey = NpcIcon,
                    m_serviceName = ServiceName,
                    m_serviceIndex = 0
                }
            ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (_hasInteracted) {
            return;
        }

        _hasInteracted = true;

        try {
            var loot = WoodenChestLoot.Roll(Entity.ActiveGameObject.m_templateID, Zone?.ZonePath);
            FitGoldToPouch(loot, playerCharacter);

            // Adds the gold (and tells the client the new total), puts the drops away and shows the loot popup.
            LootGranter.GrantAndDisplay(playerActor, playerCharacter, loot);
        }
        catch (Exception ex) {
            Logger.Error("Wooden chest {0} could not give its loot: {1}", Logger.Args(Entity.ActiveGameObject.m_templateID, ex));
        }

        PlaySound(playerActor);
        TriggerChestAnimation();
        PostQuestEvents(playerActor, playerObject);

        var delayedDeleteMsg = new ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT();
        Timers.StartSingleTimer("deletechestobject", delayedDeleteMsg, OpenAnimationTime);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT))]
    private void ReceivedDelayedDeleteMessage(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT _) 
        => Entity.DeleteObject();

    private void TriggerChestAnimation() {
        var enterState = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            Data = "",
            GameObjectID = Entity.ActiveGameObject.m_globalID,
            IgnoreIfCurrentStateIsOff = 0,
            State = StringHash.Compute(StateName)
        };

        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = enterState,
            Selfless = false,
        });
    }

    // What the template's own Open option tells the zone (quests and triggers that wait on a chest).
    private void PostQuestEvents(IActorRef playerActor, CoreObject playerObject) {
        if (Entity.Template is not GameObjectTemplate template) {
            return;
        }

        foreach (var eventName in InteractObjectStateComponent.GetQuestEvents(template)) {
            ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = eventName,
                PlayerActor = playerActor,
                PlayerGameObject = playerObject,
            });
        }
    }

    private static void FitGoldToPouch(Imlight.CoreLib.WizardData.Models.World.DropTableResult loot, Wizard wizard) {
        var room = Math.Max(0, wizard.GameStats.m_baseGoldPouch - wizard.GameStats.m_currentGold);
        loot.GoldAmount = Math.Min(loot.GoldAmount, room);
    }

    private static void PlaySound(IActorRef playerActor) {
        var soundId = new GID(s_openSoundTemplateID);
        var playSound = new GAME_5_PROTOCOL.MSG_PLAYSOUND {
            SoundID = soundId,
            ReinteractTime = 0,
            SoundFilename = "",
            StartDelay = 0,
            PlayAtMusicVolume = 0,
        };

        playerActor.Tell(playSound);
    }

}
