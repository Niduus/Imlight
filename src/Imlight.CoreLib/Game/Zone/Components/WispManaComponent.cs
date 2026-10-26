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
 * MANA WISP
 * ========================================================================
 * 
 * PURPOSE:
 * Manages mana wisp interactions, providing mana replenishment mechanics 
 * for players within a specific interaction radius.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Supports different mana restoration percentages for starter and non-starter wisps.
 * 
 * TODO:
 * 
 * Created by: Joji
 * Version: KALI 1.0
 * Last Updated: 01.10.2026
 */

using System;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class WispManaComponent : ZoneEntityComponent, IComponentFactory {
    // StateData\PlayerMobileStates.xml
    private readonly uint RESET_STATE_ID = StringHash.Compute("Unremarkable");
    private readonly uint WISP_STATE_ID = StringHash.Compute("ActionEmoting");

    private readonly string PARTICLE_ASSET = "Character/FX_WispBlue_Dsppr.nif";
    private readonly string SOUND_ASSET = "Sound/GUI/ui_health_powerup_01.wav";

    private readonly WispPickup _pickup = new();
    private bool _collected;
    private const float STARTING_WORLD_WISP_MANA = 0.25f;
    private const float WISP_MANA_PERCENT_INCREASE = 0.10f;
    private const int MIN_WISP_MANA = 5;
    private const int MIN_STARTING_WORLD_WISP_MANA = 10;

    private readonly bool _isStarterWisp;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_objectName.ToString().Contains("Wisp")
        && goTemplate.m_objectName.ToString().Contains("Mana");

    // ctor
    public WispManaComponent(ZoneEntity entity) : base(entity) {
        if (entity.Template is GameObjectTemplate goTemplate) {
            var objectName = goTemplate.m_objectName.ToString();
            _isStarterWisp = objectName.Contains("UW");
        }
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) => _pickup.Forget(id);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!_collected && playerWizard is not null && _pickup.Reached(playerObj, Entity.ActiveGameObject)) {
            // Values with gear and effects.
            var baseMana = playerWizard.GameStats.m_baseMana;
            var currentMana = playerWizard.GameStats.m_currentMana;

            if (baseMana == currentMana) { // If mana is full, no need to replenish.
                return;
            }

            // Values before effects are applied.
            var clientGameStats = playerWizard.GameStats.GetClientTypeAlternative();
            var msgBaseMana = clientGameStats.m_baseMana;

            var manaPercentage = _isStarterWisp
                ? STARTING_WORLD_WISP_MANA
                : WISP_MANA_PERCENT_INCREASE;

            // A percentage of a low level wizard's small mana pool rounds down to 1, so a wisp always gives
            // at least a few points and rounds up.
            var manaUpdate = Math.Max(
                (int) Math.Ceiling(baseMana * manaPercentage),
                _isStarterWisp ? MIN_STARTING_WORLD_WISP_MANA : MIN_WISP_MANA); // Check for overflow.
            if (currentMana + manaUpdate > baseMana) {
                manaUpdate = baseMana - currentMana;
            }

            var manaUpdateMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
                Mana = currentMana + manaUpdate,
                MaxMana = msgBaseMana,
                DisplayDiff = 1
            };
            playerActor.Tell(manaUpdateMsg);

            SendPlayerStateChange(playerWizard);

            _collected = true;
            SendDestroy(playerWizard.GameObjectID);

            playerWizard.UpdateMana(currentMana + manaUpdate);
        }
    }

    private void SendPlayerStateChange(Wizard playerWizard) {
        var playerGid = playerWizard.GameObjectID;

        // If the player is already in ActionEmoting, send Unremarkable first to reset!
        if (playerWizard.CurrentAnimationState == WISP_STATE_ID) {
            var resetMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = playerGid,
                State = RESET_STATE_ID,
                Data = new ByteString(),
                IgnoreIfCurrentStateIsOff = 0,
            };

            PlayerBroadcast(resetMsg);
            playerWizard.CurrentAnimationState = RESET_STATE_ID;
        }

        // Enter ActionEmoting with the particle and sound
        // For some reason, the wisp is not doing the "360" around the player when collecting
        var stateMana = new EmoteStateOverrideInfo {
            m_stateNameID = WISP_STATE_ID,
            m_emoteName = "",
            m_particleAsset = PARTICLE_ASSET,
            m_loop = false,
            m_particleNode = "",
            m_soundAsset = SOUND_ASSET,
            m_wizBangID = 0
        };

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );

        if (serializer.Serialize(stateMana, 1, out var emoteData)) {
            var enterMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = playerGid,
                State = WISP_STATE_ID,
                Data = emoteData,
                IgnoreIfCurrentStateIsOff = 0,
            };
            PlayerBroadcast(enterMsg);
            playerWizard.CurrentAnimationState = WISP_STATE_ID;
        }
    }

    private void SendDestroy(ulong killer)
        => Entity.DeleteObject("WispDespawn", killer);

}
