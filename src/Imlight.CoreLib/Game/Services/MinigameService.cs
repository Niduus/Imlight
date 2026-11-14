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
 * MINIGAME SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages minigame interactions, including process initialization, 
 * message routing, and zone transitions for minigame experiences.
 * 
 * USAGE EXAMPLE:
 * Internal service handling minigame-related messages within the 
 * game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Minigames;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using MinigameInfo = Imcodec.ObjectProperty.TypeCache.MinigameInfo;

namespace Imlight.CoreLib.Game.Services;

internal class MinigameService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const ushort MAGIC_HEADER = (ushort) 0xF00Du;
    private const int FrameLengthOverhead = 5;
    private const int MaxProcessPayloadLength = ushort.MaxValue - FrameLengthOverhead;

    private const string START_CHECK = "minigame-start-check";
    private static readonly TimeSpan s_firstStartCheck = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_lastStartCheck = TimeSpan.FromSeconds(20);

    private IActorRef _minigameJob;
    private uint _minigameProcessId;
    private ChestGameContext _activeChest;
    private GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS _minigameStart;
    private bool _processAnswered;

    /// <summary>
    /// A look, a while after the game was told to start the mini game's script, whether the script ever spoke.
    /// </summary>
    private sealed class StartCheck {
        public uint ProcessId;
        public int Try;
    }

    // Akka.net props
    protected static Props Props(SessionActor parentActor) 
        => Akka.Actor.Props.Create(() => new MinigameService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete() {
        var wizard = GetActiveWizard();
        var wizardZone = wizard.Zone;
        var hasPendingChest = PendingChestGames.TryTake(wizard.CharId, out var pendingChest);

        // If the player's zone is a minigame zone, start the appropriate minigame script.
        if (!MinigameConfig.IsMinigameZone(wizardZone)) {
            if (hasPendingChest) {
                AbortChestGame(pendingChest);
            }

            return;
        }

        // Minigame script format looks like 'SkullRiders/Client.lua'
        var minigameScript = MinigameConfig.GetMinigameScript(wizardZone);
        var minigameIndex = MinigameConfig.GetMinigameIndex(wizardZone);
        var minigameName = minigameScript.Split('/')[0];

        if (hasPendingChest) {
            if (pendingChest.MinigameName == minigameName) {
                _activeChest = pendingChest;
            } else {
                Logger.Warning("{Wizard} entered {Zone} with a chest game of {Minigame} pending",
                    Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), wizardZone, pendingChest.MinigameName));
                AbortChestGame(pendingChest);
            }
        }

        // Ask the server for a new minigame process.
        var msg = new PROCESS_107_PROTOCOL.MSG_NEW_MINIGAME_PROCESS {
            MinigameIndex = minigameIndex,
            MinigameName = minigameName,
            Owner = SessionActor.ActorRef,
            ChestGame = _activeChest
        };
        var reply = AskServer<PROCESS_107_PROTOCOL.MSG_PROCESS_DETAILS>(msg);
        _minigameJob = reply.ProcessActorRef;
        _minigameProcessId = reply.ProcessId;

        var minigameStartProcess = new GAME_5_PROTOCOL.MSG_START_CLIENT_PROCESS {
            OwnerGID = wizard.GameObjectID,
            JobID = reply.ProcessId,
            ScriptName = minigameScript
        };

        // The session holds the start until the game says it has loaded the zone (SessionActor.HeldUntilZoned): sent
        // during the loading screen, it was lost and the player stood frozen in the mini game's empty room.
        SendToSocket(minigameStartProcess);
        _minigameStart = minigameStartProcess;
        _processAnswered = false;
        Timers.StartSingleTimer(START_CHECK, new StartCheck { ProcessId = reply.ProcessId, Try = 1 }, s_firstStartCheck);

        Logger.Information("{Wizard} is in the mini game room {Zone}: process {ProcessId} runs {Script}{Chest}; the game starts it once the room has loaded.",
            Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), wizardZone, reply.ProcessId, minigameScript,
                _activeChest is null ? "" : " (a chest game)"));
    }

    [MessageHandler(typeof(StartCheck))]
    private void ReceiveStartCheck(StartCheck check) {
        if (_minigameJob is null || check.ProcessId != _minigameProcessId || _processAnswered || _minigameStart is null) {
            return;
        }

        var wizardName = GetActiveWizard()?.PlayerNameBehavior.GetWizardName();
        if (check.Try == 1) {
            // Every mini game script speaks right away (its start window asks for the high scores), so silence means
            // the game never got the start. Ask once more.
            Logger.Warning("{Wizard}: the game has not started {Script} (process {ProcessId}) after {Seconds} s; asking again.",
                Logger.Args(wizardName, _minigameStart.ScriptName, check.ProcessId, s_firstStartCheck.TotalSeconds));
            SendToSocket(_minigameStart);
            Timers.StartSingleTimer(START_CHECK, new StartCheck { ProcessId = check.ProcessId, Try = 2 }, s_lastStartCheck);

            return;
        }

        // Still nothing: the player would stand frozen in an empty room. Send them back so they can try again.
        Logger.Warning("{Wizard}: the game never started {Script} (process {ProcessId}); sending the player back.",
            Logger.Args(wizardName, _minigameStart.ScriptName, check.ProcessId));
        InformGameClient("The mini game did not start. You were sent back, so you can try again.", true);
        _minigameJob.Tell(PoisonPill.Instance);
        LeaveMinigame();
    }

    private void NoteProcessAnswered() {
        if (_processAnswered) {
            return;
        }

        _processAnswered = true;
        Timers.Cancel(START_CHECK);
        Logger.Information("{Wizard}: the game started {Script} (process {ProcessId}).",
            Logger.Args(GetActiveWizard()?.PlayerNameBehavior.GetWizardName(), _minigameStart?.ScriptName, _minigameProcessId));
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_MINIGAMESELECT))]
    private void ReceiveMinigameSelect(WIZARD_12_PROTOCOL.MSG_MINIGAMESELECT message) {
        var minigameIndex = message.Index;
        var minigameInfo = MinigameConfig.GetMinigameInfo(minigameIndex);
        var wizard = GetActiveWizard();
        var wizardName = wizard.PlayerNameBehavior.GetWizardName();

        if (minigameInfo == null) {
            Logger.Error("{0} tried to start up minigame at non-existing index {1}", 
                Logger.Args(wizardName, minigameIndex));

            return;
        }

        // Ensure that the player picked the minigame at a kiosk in the zone.
        var kiosk = GetZoneObject(message.npcGlobalID);
        if (kiosk?.GetComponentOfType<InteractMinigameComponent>() is null) {
            Logger.Warning("{Wizard} tried to start minigame {Index} from {Object}, which is not a kiosk in the zone",
                Logger.Args(wizardName, minigameIndex, message.npcGlobalID));

            return;
        }

        if (MinigameConfig.IsMinigameZone(wizard.Zone)) {
            Logger.Warning("{Wizard} tried to start minigame {Index} while already in minigame zone {Zone}",
                Logger.Args(wizardName, minigameIndex, wizard.Zone));

            return;
        }

        if (PendingChestGames.TryTake(wizard.CharId, out var pendingChest)) {
            AbortChestGame(pendingChest);
        }

        BeginMinigame(minigameInfo);
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_START_CHEST_MINIGAME))]
    private void ReceiveStartChestMinigame(SERVICE_101_PROTOCOL.MSG_START_CHEST_MINIGAME message) {
        var chest = message.Context;
        var wizard = GetActiveWizard();
        var wizardName = wizard.PlayerNameBehavior.GetWizardName();

        string refusal = null;
        MinigameInfo minigameInfo = null;
        if (chest.CharId != wizard.CharId) {
            refusal = "the chest game belongs to another character";
        } else if (!MinigameConfig.TryGetMinigameByName(chest.MinigameName, out minigameInfo)) {
            refusal = "the minigame does not exist";
        } else if (_minigameJob is not null || MinigameConfig.IsMinigameZone(wizard.Zone)) {
            refusal = "a minigame is already running";
        } else if (!PendingChestGames.TryAdd(chest)) {
            refusal = "a chest game is already starting";
        }

        if (refusal is not null) {
            Logger.Warning("{Wizard} could not start the chest game {Minigame}: {Reason}",
                Logger.Args(wizardName, chest.MinigameName, refusal));
            AbortChestGame(chest);

            return;
        }

        BeginMinigame(minigameInfo);
    }

    private void BeginMinigame(MinigameInfo minigameInfo) {
        var msg = new WIZARD_12_PROTOCOL.MSG_ENTERMINIGAME();
        SendToSocket(msg);

        Logger.Debug("{0} started minigame {1}",
            Logger.Args(GetActiveWizard().PlayerNameBehavior.GetWizardName(), minigameInfo.m_name));

        Teleport(
            destinationZone: minigameInfo.m_zone,
            destinationLocation: "Start",
            makePrivate: true
        );
    }

    private void AbortChestGame(ChestGameContext chest) {
        var aborted = new ZONE_102_PROTOCOL.MSG_CHEST_GAME_ABORTED { CharId = chest.CharId };
        chest.ChestRef?.Tell(aborted, SessionActor.ActorRef);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS))]
    private void ReceiveMessageProcess(GAME_5_PROTOCOL.MSG_MESSAGE_PROCESS message) {
        // The raw message is just like any other message sent by the client.
        // However, it does not include the magic header or body.
        // It immediately begins with the DML header, detailing the service ID, message ID, length, and finally the payload.
        // Find more @ https://revive101.github.io/Imlight-docs/internals/systems/dml/serialization.html#dml-header.
        if (_minigameJob is null || message.JobID != _minigameProcessId) {
            Logger.Warning("Dropped a minigame message for process {JobId}, the active process is {ProcessId}",
                Logger.Args(message.JobID, _minigameProcessId));

            return;
        }

        NoteProcessAnswered();

        byte[] messageRawBytes = message.Message;
        if (messageRawBytes is null || messageRawBytes.Length == 0 || messageRawBytes.Length > MaxProcessPayloadLength) {
            Logger.Warning("Dropped a minigame message of {Length} bytes for process {ProcessId}",
                Logger.Args(messageRawBytes?.Length ?? 0, _minigameProcessId));

            return;
        }

        var frame = BuildProcessFrame(messageRawBytes);

        // Deserialize using the MessageSerializer.
        IReadOnlyCollection<IMessage> deserializedMsg;
        try {
            deserializedMsg = MessageEncoder.Decode(frame);
        } catch (Exception ex) {
            Logger.Warning("Failed to decode a minigame message of {Length} bytes: {Reason}",
                Logger.Args(messageRawBytes.Length, ex.Message));

            return;
        }

        if (deserializedMsg == null || deserializedMsg.Count <= 0) {
            var hexStringRaw = BitConverter.ToString(messageRawBytes).Replace("-", " ");
            var hexStringAdditions = BitConverter.ToString(frame).Replace("-", " ");
            Logger.Error("Failed to deserialize message process {0} (manually set as {1})", 
                Logger.Args(hexStringRaw, hexStringAdditions));

            return;
        }

        foreach (var msg in deserializedMsg) {
            // Forward the message to the minigame job.
            _minigameJob.Tell(msg, SessionActor.ActorRef);
        }
    }

    private static byte[] BuildProcessFrame(byte[] messageRawBytes) {
        // Our message serializer doesn't quite support the bare DML header yet, so we'll be using a shortcut:
        // Add the magic header and body manually, then send it to the message serializer.
        var writer = new BitWriter();

        // The length counts everything after the magic header and the length itself, trailing null included.
        writer.WriteUInt16(MAGIC_HEADER);
        writer.WriteUInt16((ushort) (messageRawBytes.Length + FrameLengthOverhead));

        // Write the body.
        writer.WriteUInt8(0);  // IsControl
        writer.WriteUInt8(0);  // OpCode
        writer.WriteUInt16(0); // Padding

        // Write payload.
        writer.WriteBytes(messageRawBytes);
        writer.WriteUInt8(0);  // Trailing null

        return writer.GetData();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_PROCESS_TERMINATED))]
    private void ReceiveClientKilledProcess(GAME_5_PROTOCOL.MSG_CLIENT_PROCESS_TERMINATED message) {
        // The client has terminated the minigame process.
        if (_minigameJob is not null && message.JobID == _minigameProcessId) {
            NoteProcessAnswered();
            _minigameJob.Tell(message, SessionActor.ActorRef);
        }
    }

    [MessageHandler(typeof(PROCESS_107_PROTOCOL.MSG_PROCESS_KILLED))]
    private void ReceiveProcessKilled(PROCESS_107_PROTOCOL.MSG_PROCESS_KILLED message) {
        if (_minigameJob is not null && message.ProcessId == _minigameProcessId) {
            // Our minigame job has been killed.
            LeaveMinigame();
        }
    }

    /// <summary>
    /// Ends the mini game on the game's side and sends the player back where they came from.
    /// </summary>
    private void LeaveMinigame() {
        var processId = _minigameProcessId;

        // Forgotten, so a later chest game in this session is not refused as "a minigame is already running".
        _minigameJob = null;
        _minigameProcessId = 0;
        _minigameStart = null;
        Timers.Cancel(START_CHECK);

        // Inform the client the process has been terminated.
        var terminateMsg = new GAME_5_PROTOCOL.MSG_KILL_CLIENT_PROCESS { JobID = processId };
        SendToSocket(terminateMsg);

        var leaveMinigameMsg = new WIZARD_12_PROTOCOL.MSG_LEAVEMINIGAME();
        SendToSocket(leaveMinigameMsg);

        if (_activeChest is not null) {
            var chest = _activeChest;
            _activeChest = null;
            Teleport(
                destinationZone: chest.ReturnZone,
                destinationLocation: chest.ReturnLocation,
                makePrivate: chest.ReturnPrivate
            );

            return;
        }

        var wizard = GetActiveWizard();
        Teleport(
            destinationZone: wizard.PreviousZone,
            destinationLocation: "Start",
            makePrivate: true
        );
    }

}