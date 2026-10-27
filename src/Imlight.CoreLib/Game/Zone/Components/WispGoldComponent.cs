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

using System;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A gold wisp gives gold to the first wizard who walks into it, more for higher levels.
/// </summary>
internal sealed class WispGoldComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory {

    // StateData\PlayerMobileStates.xml; the effect and sound the game's WC_WispGold template names (same as health).
    private static readonly uint RESET_STATE_ID = StringHash.Compute("Unremarkable");
    private static readonly uint WISP_STATE_ID = StringHash.Compute("ActionEmoting");
    private const string PARTICLE_ASSET = "Character/FX_WispRed_Dsppr.nif";
    private const string SOUND_ASSET = "Sound/GUI/ui_health_powerup_01.wav";

    private readonly WispPickup _pickup = new();
    private bool _collected;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_objectName.ToString().Contains("Wisp")
        && goTemplate.m_objectName.ToString().Contains("Gold");

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) => _pickup.Forget(id);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_collected || playerWizard is null || !_pickup.Reached(playerObj, Entity.ActiveGameObject)) {
            return;
        }

        _collected = true;
        var level = Math.Max(1, playerWizard.MagicSchoolBehavior.Level);
        var goldBefore = playerWizard.GameStats.m_currentGold;
        playerWizard.AddGold(Random.Shared.Next(level * 2, level * 5 + 1) + 10);
        var goldGained = playerWizard.GameStats.m_currentGold - goldBefore;
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = playerWizard.GameStats.m_currentGold,
            MaxGold = playerWizard.GameStats.m_baseGoldPouch
        });

        // The game's own gold wisp (WC_WispGold) plays the same pickup sparkle and sound as a health wisp. Nothing
        // showed or played, and nothing said how much it gave (Niduus, 2026-10-27).
        SendPlayerStateChange(playerWizard);
        var said = $"You picked up {goldGained} gold.";

        // [World] GoldWispsGiveCrowns (Niduus, 2026-10-27: "Gold Wisps give players GOLD (With a config in manager to
        // gain crowns)"): on top of the gold, GoldWispCrowns crowns go to the wizard's account. Read live.
        var crowns = Math.Clamp(ConfigurationManager.GetValue("World.GoldWispCrowns", 10), 0, 1_000_000);
        if (ConfigurationManager.GetValue("World.GoldWispsGiveCrowns", false) && crowns > 0
            && playerWizard.Account is { } account) {
            account.SetCrowns((int) Math.Min((long) account.Crowns + crowns, int.MaxValue));
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                Failure = 0,
                TotalCrowns = account.Crowns,
                CharacterID = playerWizard.CharId,
                CacheBalanceForCSSegmentation = 1
            });
            said = $"You picked up {goldGained} gold and {crowns} crowns.";
        }

        playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = said, Modal = 0 });
        Entity.DeleteObject("WispDespawn", playerWizard.GameObjectID);
    }

    private void SendPlayerStateChange(Wizard playerWizard) {
        var playerGid = playerWizard.GameObjectID;

        // Already in ActionEmoting (another wisp just now): Unremarkable first, so this one plays too.
        if (playerWizard.CurrentAnimationState == WISP_STATE_ID) {
            PlayerBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = playerGid,
                State = RESET_STATE_ID,
                Data = new ByteString(),
                IgnoreIfCurrentStateIsOff = 0,
            });
            playerWizard.CurrentAnimationState = RESET_STATE_ID;
        }

        var state = new EmoteStateOverrideInfo {
            m_stateNameID = WISP_STATE_ID,
            m_emoteName = "",
            m_particleAsset = PARTICLE_ASSET,
            m_loop = false,
            m_particleNode = "",
            m_soundAsset = SOUND_ASSET,
            m_wizBangID = 0
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (serializer.Serialize(state, 1, out var emoteData)) {
            PlayerBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = playerGid,
                State = WISP_STATE_ID,
                Data = emoteData,
                IgnoreIfCurrentStateIsOff = 0,
            });
            playerWizard.CurrentAnimationState = WISP_STATE_ID;
        }
    }

}
