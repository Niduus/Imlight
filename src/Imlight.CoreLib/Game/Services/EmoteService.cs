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
 * EMOTE SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player emote interactions and synchronizes emote animations and
 * text messages to other players within the active zone.
 * 
 * USAGE EXAMPLE:
 * Internal service attached to SessionActor for handling emote-related
 * network messages.
 * 
 * NOTE:
 * - Originator players predictively play their emote locally (ExcludeOriginator = 1).
 * - Broadcasts MSG_PIIRADIALMENUPLAYEMOTE (Service 56, Order 73) selflessly
 *   to other players in the zone.
 * 
 * Created by: Revive101
 * Version: KALI 1.0
 * Last Updated: 10/19/2026
 */

using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using System;
using System.Linq;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

internal class EmoteService(SessionActor sessionActor) : MessageService(sessionActor) {

    private static readonly uint ResetStateId = StringHash.Compute("Unremarkable");
    private static readonly uint EmoteStateId = StringHash.Compute("ActionEmoting");

    private DateTime _lastEmoteTime = DateTime.MinValue;
    private string _lastEmoteAnimation = string.Empty;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new EmoteService(parentActor));

    /// <summary>
    /// Handles radial menu emote play requests from the client.
    /// Broadcasts the emote animation and speech text to other players in the zone.
    /// </summary>
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTPIIRADIALMENUPLAYEMOTE))]
    private void ReceiveRequestPIIRadialMenuPlayEmote(WIZARD3_56_PROTOCOL.MSG_REQUESTPIIRADIALMENUPLAYEMOTE message) {
        _lastEmoteTime = DateTime.UtcNow;
        _lastEmoteAnimation = message.EmoteAnimationName;

        var wizard = GetActiveWizard();
        var broadcastMsg = new WIZARD3_56_PROTOCOL.MSG_PIIRADIALMENUPLAYEMOTE {
            SourceID = wizard.GameObjectID,
            EmoteAnimationName = message.EmoteAnimationName,
            EmoteText = message.EmoteText
        };

        ZoneBroadcast(broadcastMsg);
        BroadcastEmoteAnimation(wizard.GameObjectID, message.EmoteAnimationName, true);
    }

    /// <summary>
    /// Handles core radial menu emote requests. If not already broadcast via
    /// <see cref="WIZARD3_56_PROTOCOL.MSG_REQUESTPIIRADIALMENUPLAYEMOTE"/>, synchronizes to zone.
    /// </summary>
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_CORE_PIIRADIALMENUEMOTE))]
    private void ReceiveCorePIIRadialMenuEmote(WIZARD3_56_PROTOCOL.MSG_CORE_PIIRADIALMENUEMOTE message) {
        // Prevent duplicate broadcast if MSG_REQUESTPIIRADIALMENUPLAYEMOTE was just handled.
        if (string.Equals(_lastEmoteAnimation, message.EmoteAnimationName, StringComparison.Ordinal)
&& (DateTime.UtcNow - _lastEmoteTime) < TimeSpan.FromSeconds(1)) {
            return;
        }


        _lastEmoteTime = DateTime.UtcNow;
        _lastEmoteAnimation = message.EmoteAnimationName;

        var wizard = GetActiveWizard();
        var broadcastMsg = new WIZARD3_56_PROTOCOL.MSG_PIIRADIALMENUPLAYEMOTE {
            SourceID = wizard.GameObjectID,
            EmoteAnimationName = message.EmoteAnimationName,
            EmoteText = string.Empty
        };

        ZoneBroadcast(broadcastMsg, isSelfless: true, targets: ZoneBroadcastTarget.Players);
        BroadcastEmoteAnimation(wizard.GameObjectID, message.EmoteAnimationName, isSelfless: message.ExcludeOriginator == 1);
    }

    /// <summary>
    /// Handles classic core emote messages from GAME_5_PROTOCOL.
    /// </summary>
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CORE_EMOTE))]
    private void ReceiveCoreEmote(GAME_5_PROTOCOL.MSG_CORE_EMOTE message) {

        _lastEmoteTime = DateTime.UtcNow;
        _lastEmoteAnimation = message.Name;

        var wizard = GetActiveWizard();
        var broadcastMsg = new WIZARD3_56_PROTOCOL.MSG_PIIRADIALMENUPLAYEMOTE {
            SourceID = wizard.GameObjectID,
            EmoteAnimationName = message.Name,
            EmoteText = string.Empty
        };

        ZoneBroadcast(broadcastMsg);
        BroadcastEmoteAnimation(wizard.GameObjectID, message.Name, message.ExcludeOriginator == 1);
    }

    // How long after a won battle the Victory Dance starts, so it plays in the circle during the victory scene.
    private static readonly TimeSpan VictoryDanceDelay = TimeSpan.FromSeconds(1.5);

    // On (the default): after a won battle the server plays the dance as a radial emote, unless the game asks for the
    // dance first (then the game plays it itself). Niduus, 2026-10-22: with this off in v0.10.145 no dance played, as
    // Imlight ends the battle at once and the game never asked.
    private static readonly bool s_serverPlaysVictoryDance = ConfigurationManager.GetValue("Emotes.ServerVictoryDance", true);

    /// <summary>
    /// At the end of a won battle the game asks for the dance to play (PlayerGID 0). As the real server does in a
    /// packet capture Niduus was given (2026-10-22), this picks one of the worn dances at random (up to five are worn)
    /// and answers with it for everyone in the zone; the game then plays it.
    /// </summary>
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATEAFTERCOMBATDANCE))]
    private void ReceiveUpdateAfterCombatDance(WIZARD2_53_PROTOCOL.MSG_UPDATEAFTERCOMBATDANCE message) {
        try {
            // Only the game's request; the server's own answers carry a player.
            if (message.PlayerGID != 0 || GetActiveWizard() is not { } wizard) {
                return;
            }

            // The game plays this pick itself, so the server's own play is not needed.
            Timers.Cancel("victory-dance");
            var worn = wizard.EquipmentBehavior?.GetVictoryDances().Count ?? 0;
            var templateId = wizard.PickVictoryDance();
            Logger.Information("Victory Dance: {0} won a battle; dance {1} picked from {2} worn.",
                Logger.Args(wizard.CharId, templateId, worn));
            ZoneBroadcast(new WIZARD2_53_PROTOCOL.MSG_UPDATEAFTERCOMBATDANCE {
                PlayerGID = wizard.GameObjectID,
                AfterCombatDance = templateId,
            }, false);
        }
        catch (Exception ex) {
            Logger.Error("Victory Dance pick failed: {0}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// With [Emotes] ServerVictoryDance on, a won battle plays one of the wizard's worn Victory Dances (picked at
    /// random) from the server: the dance is an emote item (CustomEmoteBehaviorTemplate, CE_Emote) whose animation is
    /// played like a radial menu emote, for everyone in the zone.
    /// </summary>
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatWin(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        if (s_serverPlaysVictoryDance && GetActiveWizard() is { } wizard && wizard.PickVictoryDance() > 0) {
            Timers.StartSingleTimer("victory-dance", new SERVICE_101_PROTOCOL.MSG_PLAYVICTORYDANCE(), VictoryDanceDelay);
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_PLAYVICTORYDANCE))]
    private void ReceivePlayVictoryDance(SERVICE_101_PROTOCOL.MSG_PLAYVICTORYDANCE message) {
        try {
            var wizard = GetActiveWizard();
            var templateId = wizard?.WizardAvatar?.m_afterCombatVictoryDance ?? 0;
            var emote = templateId == 0 ? null
                : (CoreObjectFactory.GetCoreTemplate(templateId) as WizItemTemplate)?.m_behaviors?.OfType<CustomEmoteBehaviorTemplate>().FirstOrDefault();
            var animation = emote is null ? "" : (string) emote.m_animation1;
            Logger.Information("Victory Dance: {0} won a battle with dance {1}, playing animation \"{2}\".",
                Logger.Args(wizard?.CharId, templateId, animation));
            if (string.IsNullOrWhiteSpace(animation)) {
                return;
            }

            ZoneBroadcast(new WIZARD3_56_PROTOCOL.MSG_PIIRADIALMENUPLAYEMOTE {
                SourceID = wizard.GameObjectID,
                EmoteAnimationName = animation,
                EmoteText = string.Empty
            });
            BroadcastEmoteAnimation(wizard.GameObjectID, animation, false);
        }
        catch (Exception ex) {
            Logger.Error("Victory Dance failed: {0}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// Broadcasts MSG_ENTERSTATE with EmoteStateOverrideInfo.
    /// Resets the state machine via "Unremarkable" first so consecutive emotes always trigger.
    /// </summary>
    private void BroadcastEmoteAnimation(ulong gameObjectId, string emoteAnimationName, bool isSelfless) {
        if (string.IsNullOrWhiteSpace(emoteAnimationName)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (wizard.CurrentAnimationState == EmoteStateId) {
            var resetMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                GameObjectID = gameObjectId,
                State = ResetStateId,
                Data = new ByteString(),
                IgnoreIfCurrentStateIsOff = 0,
            };
            ZoneBroadcast(resetMsg, isSelfless: isSelfless);
        }

        var stateEmote = new EmoteStateOverrideInfo {
            m_stateNameID = EmoteStateId,
            m_emoteName = emoteAnimationName,
            m_particleAsset = string.Empty,
            m_loop = false,
            m_particleNode = string.Empty,
            m_soundAsset = string.Empty,
            m_wizBangID = 0
        };

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );

        if (!serializer.Serialize(stateEmote, 1, out var emoteData)) {
            Logger.Error("Failed to serialize EmoteStateOverrideInfo for {Anim}",
                Logger.Args(emoteAnimationName));

            return;
        }

        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = gameObjectId,
            State = EmoteStateId,
            Data = emoteData,
            IgnoreIfCurrentStateIsOff = 0,
        }, isSelfless);

        wizard.CurrentAnimationState = EmoteStateId;
    }

    /// <summary>
    /// Handles radial menu layout updates from client. The client sends this message, when the radial menu is closed.
    /// </summary>
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_PIIUPDATEEMOTESRADIALMENU))]
    private void ReceivePIIUpdateEmotesRadialMenu(WIZARD3_56_PROTOCOL.MSG_PIIUPDATEEMOTESRADIALMENU message) {
        var wizard = GetActiveWizard();

        wizard.RadialMenuData = message.RadialMenuData;
        wizard.RadialMenu2Data = message.RadialMenu2Data;
        wizard.RadialMenu3Data = message.RadialMenu3Data;
        WizardCollection.UpdateCharacterRadialMenuData(wizard);

        Logger.Information("Saved updated radial menu data for character {Id}",
            Logger.Args(wizard.CharId));
    }

    /// <summary>
    /// Handles client request to convert emotes to the PII system.
    /// </summary>
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTONDEMANDCONVERTEMOTESTOPIISYSTEM))]
    private void ReceiveRequestOnDemandConvertEmotes(WIZARD3_56_PROTOCOL.MSG_REQUESTONDEMANDCONVERTEMOTESTOPIISYSTEM message) {
        // Client requesting on-demand conversion.
    }

    /// <summary>
    ///  Loads the radial menu from DB
    /// </summary>
    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();
        if (!wizard.RadialMenuData.IsEmpty) {
            var updateMsg = new WIZARD3_56_PROTOCOL.MSG_PIIUPDATEEMOTESRADIALMENU {
                RadialMenuData = wizard.RadialMenuData,
                RadialMenu2Data = wizard.RadialMenu2Data,
                RadialMenu3Data = wizard.RadialMenu3Data
            };
            SendToSocket(updateMsg);
        }

        Logger.Information("[EmoteService] Restored radial menu data to client for character {Id}",
    Logger.Args(wizard.CharId));
    }
}
