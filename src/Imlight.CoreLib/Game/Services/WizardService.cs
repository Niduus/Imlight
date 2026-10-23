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
 * WIZARD SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages wizard character state, including location tracking, 
 * level management, and game state synchronization.
 * 
 * USAGE EXAMPLE:
 * Internal service handling wizard-specific interactions within 
 * the game server session.
 * 
 * NOTE:
 * - Updates wizard location and orientation
 * - Manages level-up mechanics and stat updates
 * - Provides internal wizard state management
 * 
 * TODO:
 * - Enhance level-up stat calculation
 * - Review location and orientation compression logic
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Character;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Math;
using Microsoft.Extensions.ObjectPool;
using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Services;

internal class WizardService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const float DIRECTION_STEPS = 250f;
    private static readonly TimeSpan s_locationSaveEvery = TimeSpan.FromSeconds(5);
    private DateTime _lastLocationSave;

    // [Trainers] TrainingPointPerLevel: every level gained also gives TrainingPointsPerLevel training points.
    private static readonly int s_trainingPointsPerLevel
        = ConfigurationManager.GetValue("Trainers.TrainingPointPerLevel", false)
            ? Math.Clamp(ConfigurationManager.GetValue("Trainers.TrainingPointsPerLevel", 1), 0, 100)
            : 0;

    private Wizard _activeWizard;
    private CoreObject _activeWizardGameObject;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new WizardService(parentActor));

    protected override void OnPreDispose() {
        try {
            _activeWizard?.SaveLocation();
        }
        catch (Exception ex) {
            Logger.Error("Failed to save wizard location during disconnect: {0}",
                Logger.Args(ex));
        }
        finally {
            base.OnPreDispose();
        }
    }

    #region Internal Handlers

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveZoneAddPlayerResponse(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message)
        => _activeWizardGameObject = message.WizardGameObject;

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD))]
    private void ReceiveSetActiveWizard(CHARACTER_103_PROTOCOL.MSG_SETACTIVEWIZARD message) {
        _activeWizard = message.Wizard;
        _activeWizard.UpdateLastLoginTime((uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // Player cheats (PlayerCheats.cs) are only for the wizard played on the server's own PC.
        Imlight.CoreLib.Game.Combat.PlayerCheats.NoteWizard(_activeWizard.CharId, CommandService.IsThisPcCached(SessionActor.RemoteIp));
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD))]
    private void ReceiveQueryActiveWIzard(CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD message)
        => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER() {
            Wizard = _activeWizard,
            WizardGameObject = _activeWizardGameObject
        });

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_LEVELUP))]
    private void ReceiveSetLevel(CHARACTER_103_PROTOCOL.MSG_LEVELUP message) {
        // This is the internal level up message. It most likely happened due to a developer command.
        var beforeLevel = _activeWizard.MagicSchoolBehavior.Level;
        var levelUpSuccess = _activeWizard.SetLevel(message.NewLevel);
        if (!levelUpSuccess) {
            return;
        }

        GiveLevelTrainingPoints(_activeWizard.MagicSchoolBehavior.Level - beforeLevel);

        var levelUpMessage = new WIZARD_12_PROTOCOL.MSG_LEVELUP {
            GlobalID = _activeWizard.GameObjectID,
            NewLevel = _activeWizard.MagicSchoolBehavior.Level,
            Data = "0000000000"
        };
        ZoneBroadcast(levelUpMessage, false);

        // Leveling up the player will set their new stats and heal them.
        // We need to do the code below to echo those changes to the client.
        var magicSchool = _activeWizard.MagicSchoolBehavior.MagicSchool;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, message.NewLevel);

        // Update health — heal to full and sync server state with what the client was told.
        _activeWizard.UpdateHealth(_activeWizard.GameStats.m_baseHitpoints);
        var healthMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = _activeWizardGameObject.m_globalID,
            NewHealth = baseStats.m_hitpoints,
            NewHealthMax = baseStats.m_hitpoints,
            DisplayDiff = 1,
        };
        SendToSocket(healthMessage);

        // Update mana.
        _activeWizard.UpdateMana(_activeWizard.GameStats.m_baseMana);
        var manaMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
            Mana = baseStats.m_mana,
            MaxMana = baseStats.m_mana,
            DisplayDiff = 1,
        };
        SendToSocket(manaMessage);

        // Update power pips
        var powerPipsMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP() { PowerPip = baseStats.m_pipChance };
        SendToSocket(powerPipsMessage);

        // Update energy.
        _activeWizard.UpdateEnergy(baseStats.m_petEnergy);
        var petEnergyMessage = new PET_9_PROTOCOL.MSG_PETENERGYMAX() {
            MaxEnergy = baseStats.m_petEnergy
        };
        SendToSocket(petEnergyMessage);

        AnnounceLevelChange();
    }

    /// <summary>
    /// Gives the training points [Trainers] TrainingPointPerLevel grants for the levels just gained.
    /// </summary>
    private void GiveLevelTrainingPoints(int levelsGained) {
        if (s_trainingPointsPerLevel <= 0 || levelsGained <= 0) {
            return;
        }

        var newTrainingPoints = _activeWizard.MagicSchoolBehavior.TrainingPoints + (levelsGained * s_trainingPointsPerLevel);
        _activeWizard.UpdateTrainingPoints(newTrainingPoints);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING() {
            TrainingPoints = newTrainingPoints
        });
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_GAINXP))]
    private void ReceiveGainXP(CHARACTER_103_PROTOCOL.MSG_GAINXP message) {
        var beforeLevel = _activeWizard.MagicSchoolBehavior.Level;
        var beforeXP = _activeWizard.MagicSchoolBehavior.ExperiencePoints;
        var xpGained = message.XP;

        _activeWizard.AddExperiencePoints(xpGained);
        var afterLevel = _activeWizard.MagicSchoolBehavior.Level;

        if (beforeLevel != afterLevel) {
            GiveLevelTrainingPoints(afterLevel - beforeLevel);

            // The player leveled up. AddExperiencePoints already called SetLevel internally,
            // so we just need to broadcast the level-up and send stat updates to the client.
            var levelUpMessage = new WIZARD_12_PROTOCOL.MSG_LEVELUP {
                GlobalID = _activeWizard.GameObjectID,
                NewLevel = afterLevel,
                Data = "0000000000"
            };
            ZoneBroadcast(levelUpMessage, false);

            // Send stat updates to client (health, mana, power pips, energy).
            var magicSchool = _activeWizard.MagicSchoolBehavior.MagicSchool;
            var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, afterLevel);

            // Heal to full and sync server state with what the client was told.
            _activeWizard.UpdateHealth(_activeWizard.GameStats.m_baseHitpoints);
            var healthMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
                CharacterID = _activeWizardGameObject.m_globalID,
                NewHealth = baseStats.m_hitpoints,
                NewHealthMax = baseStats.m_hitpoints,
                DisplayDiff = 1,
            };
            SendToSocket(healthMessage);

            _activeWizard.UpdateMana(_activeWizard.GameStats.m_baseMana);
            var manaMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEMANA() {
                Mana = baseStats.m_mana,
                MaxMana = baseStats.m_mana,
                DisplayDiff = 1,
            };
            SendToSocket(manaMessage);

            var powerPipsMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEPOWERPIP() {
                PowerPip = baseStats.m_pipChance
            };
            SendToSocket(powerPipsMessage);

            _activeWizard.UpdateEnergy(baseStats.m_petEnergy);
            var petEnergyMessage = new PET_9_PROTOCOL.MSG_PETENERGYMAX() {
                MaxEnergy = baseStats.m_petEnergy
            };
            SendToSocket(petEnergyMessage);

            AnnounceLevelChange();
        }

        // Inform the client of the XP change.
        var addXpMsg = new WIZARD_12_PROTOCOL.MSG_UPDATEXP {
            GlobalID = _activeWizard.GameObjectID,
            XP = xpGained,
            OldXP = beforeXP,
        };
        SendToSocket(addXpMsg);
    }

    private void AnnounceLevelChange()
        => TellOtherServices(new GROUP_109_PROTOCOL.MSG_LEVELCHANGED {
            CharId = _activeWizard.CharId,
            NewLevel = (uint) _activeWizard.MagicSchoolBehavior.Level
        });

    #endregion

    #region Game Handlers

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENTMOVE))]
    private void ReceiveClientMove(GAME_5_PROTOCOL.MSG_CLIENTMOVE message) {
        // A game coming back after a disconnect (Reattach=1) sends moves before its wizard is loaded here; those threw
        // a NullReferenceException ("service null L:lambda_method"). The first move after the load is saved.
        if (_activeWizard is null) {
            return;
        }

        // Save the player's location and direction on interval.
        // Restore actual location information, as it is compressed by a factor of 4 and unsigned.
        // Yaw is represented in radians in the client, but transmitted to the server as degrees.
        var position = new Vector3(
            unchecked((short) message.LocationX * 4),
            unchecked((short) message.LocationY * 4),
            unchecked((short) message.LocationZ * 4));
        _activeWizard.Location = position;

        // Direction is a byte: 250 steps make a full turn, as the server packs it for MSG_SERVERTELEPORT.
        var radians = message.Direction / DIRECTION_STEPS * (2 * System.MathF.PI);
        _activeWizard.Orientation = new Vector3(0, 0, radians);

        // Saved every few seconds as well as at logout, so a crash or a stopped server still puts the wizard back
        // where they stood and facing the same way (Niduus, 2026-10-23).
        var now = DateTime.UtcNow;
        if (now - _lastLocationSave >= s_locationSaveEvery) {
            _lastLocationSave = now;
            _activeWizard.SaveLocation();
        }

        // For the Position card in ImlightManager.
        Imlight.CoreLib.Game.Tools.PlayerPositions.Update(_activeWizard.CharId, _activeWizard.PlayerNameBehavior?.GetWizardName(),
            _activeWizard.Zone, position.X, position.Y, position.Z, radians, CommandService.IsThisPcCached(SessionActor.RemoteIp));
    }

    #endregion
    
}
