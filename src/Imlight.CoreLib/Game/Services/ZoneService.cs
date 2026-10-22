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
 * ZONE SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages zone interactions, player transfers, and zone-specific 
 * mechanics within the game server session.
 * 
 * USAGE EXAMPLE:
 * Internal service handling complex zone transition, spawning, 
 * and player management processes.
 * 
 * NOTE:
 * - Home goes to the dorm, or to the equipped house when there is one; from inside any house but the dorm it goes to
 *   the dorm (HouseDeeds.ResolveEquippedHouse).
 * - A house teleport request the client does not answer within a few seconds is followed by a plain zone transfer
 *   request for the same house, so a visitor is not left on the loading screen.
 *
 * TODO:
 *
 * Created by: Jooty, Jeff
 * Version: KALI 1.0
 * Last Updated: 10/22/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class ZoneService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const int ZONE_REMOVAL_WAIT_TIME_IN_SECONDS = 8;
    private const int ZONE_HEAL_TICK_INTERVAL_IN_SECONDS = 5;
    private const float TELEPORT_EFFECTS_TIME = 2.0f;
    private const string ENTER_ZONE_EVENT_NAME = "EnterZone";
    private const string DIALOG_ENTRY_EVENT_COMPLETION = "ENTRY";

    // The official server sends MSG_SERVERTRANSFER about 400 ms after the ACK; saving the wizard adds about 50 ms.
    private const int ZoneTransferDelayMs = 350;

    private const string DeedZoneTimeoutKey = "deedzone-timeout";
    private const string HouseTeleportFallbackKey = "house-teleport-fallback";
    private static readonly TimeSpan HouseTeleportFallbackTime = TimeSpan.FromSeconds(8);
    private const string BackpackFullForHouseText = "Your backpack is full. Make room for your house deed and try again.";

    public IActorRef ZoneActor;

    private readonly TimeSpan _zoneRemovalWaitTime = TimeSpan.FromSeconds(ZONE_REMOVAL_WAIT_TIME_IN_SECONDS);
    private readonly bool _randomBackflips
        = ConfigurationManager.Settings["April Fools.RandomBackFlips"].AsBool();
    private readonly ZoneTransferGate _transferGate = new();
    private uint _currentDynamicZoneId;
    private HouseEntry _pendingHouseEntry;

    private const string SIGIL_ENTER_TIMER_KEY = "sigilenter";
    private const float SIGIL_COUNTDOWN_SECONDS = 10.0f;
    private const float SIGIL_YAW_ERROR_COMPENSATION = 1.58f; // Gamebryo yaw compensation
    private ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY _activeSigilEntry;
    private DateTime _sigilEntryStarted;
    // After the countdown starts the player is moved onto the pad; their position may lag behind for a moment.
    // Moves while the player is being put on their circle are not theirs; after that any move stops the countdown
    // (Niduus, 2026-10-21: "Sigil cancellation should be from any movement").
    private static readonly TimeSpan SIGIL_SETTLE_TIME = TimeSpan.FromSeconds(2.5);
    private const float SIGIL_MOVE_TOLERANCE = 10.0f;
    private DateTime _sigilSettleUntil;
    private Imcodec.Math.Vector3 _sigilAnchor;

    private readonly CoreObjectSerializer _effectSerializer = new(
        behaviors: SerializerFlags.None
    );
    private readonly CoreObjectSerializer _zoneObjectSerializer = new(
        versionable: false,
        behaviors: SerializerFlags.None
    );

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ZoneService(parentActor));

    protected override void OnPreDispose() {
        var gameObj = GetActiveGameObject();
        if (gameObj is null) {
            base.OnPreDispose();
            return;
        }

        var globalId = gameObj.m_globalID;

        // If the zone reference is not null, we'll tell the zone to remove the player.
        ZoneActor?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER() {
            PlayerActor = SessionActor.ActorRef,
            GlobalId = globalId,
            MobileId = gameObj.m_nMobileID,
        });
        ZoneActor = null;

        // Remove the player from the online player collection.
        OnlinePlayerCollection.RemoveOnlinePlayer(SessionActor.SessionID);

        Timers.Cancel("sync-crowns");

        base.OnPreDispose();
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceivePostAttach(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        // Send immediate effects.
        var wizard = GetActiveWizard();
        var timeHomeLastClicked = DateTimeOffset.FromUnixTimeSeconds(wizard.TimeHomeLastClicked);
        var timeDifference = DateTimeOffset.UtcNow.Subtract(timeHomeLastClicked);

        if (timeDifference.TotalSeconds < 30) {
            SendCantGoHomeEffect(timeHomeLastClicked);
        }

        var postEventMsg = new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = ENTER_ZONE_EVENT_NAME,
            PlayerActor = SessionActor.ActorRef,
            PlayerGameObject = GetActiveGameObject()
        };
        ZoneActor.Tell(postEventMsg);

        // Sync crowns. Send an immediate sync, then periodic sync so that whenever the
        // character stats page or Crown Shop is opened, the crowns display correctly.
        var crownMsg = new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
            Failure = 0,
            TotalCrowns = wizard.Account.Crowns,
            CharacterID = wizard.CharId,
            CacheBalanceForCSSegmentation = 1
        };
        SendToSocket(crownMsg);
        Timers.StartPeriodicTimer("sync-crowns", crownMsg, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

        return;
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_CROWNBALANCE))]
    private void ReceiveCrownBalanceTimer(WIZARD_12_PROTOCOL.MSG_CROWNBALANCE message) {
        var wizard = GetActiveWizard();
        if (wizard != null) {
            message.TotalCrowns = wizard.Account.Crowns;
            message.CharacterID = wizard.CharId;
            SendToSocket(message);
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_POSTZONEEVENTFROMCLIENT))]
    private void ReceivePostZoneEventFromClient(GAME_5_PROTOCOL.MSG_POSTZONEEVENTFROMCLIENT message) {
        // An event the client's own scripting posts (e.g. the end of a cutscene): quest goals that list it
        // in their generic events complete for every player in the zone.
        if (string.IsNullOrEmpty(message.EventName) || ZoneActor is null) {
            return;
        }

        ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_ZONEEVENTFORQUESTS { EventName = message.EventName }],
            Targets = ZoneBroadcastTarget.Players,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG))]
    private void ReceiveCompleteDialog(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG message) {
        // A dialog entry with an m_dialogEvent is reported as CompletionType "ENTRY" when the client reaches
        // it. Live posts that event to the zone, where triggers act on it (e.g. Bartleby's mouth opening).
        string completionType = message.CompletionType;
        string entryEvent = message.EntryEvent;
        if (!string.Equals(completionType, DIALOG_ENTRY_EVENT_COMPLETION, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(entryEvent) || ZoneActor is null) {
            return;
        }

        ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = entryEvent,
            PlayerActor = SessionActor.ActorRef,
            PlayerGameObject = GetActiveGameObject()
        });
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONETRANSFER))]
    private void ReceiveZoneTransferRequest(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message)
        => RequestZoneTransfer(message);

    /// <summary>
    /// Starts a transfer. Returns the game world's answer, or null when the transfer was not asked for (a transfer
    /// already queued, or a house that cannot be entered).
    /// </summary>
    private ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP RequestZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        if (_transferGate.IsQueued) {
            return null;
        }

        // A trigger or a script that teleports into a house zone names no lot, so the player's own house in that zone is used.
        if (message.SendToClient && message.HouseEntry is null && HouseZoneIndex.IsHouseZone(message.DestinationZone)
            && !TryUseOwnHouse(message)) {
            return null;
        }

        // The client loads a house zone from the entry, so every way into a house has to bring one for that zone.
        if (message.SendToClient && !IsValidHouseTransfer(message)) {
            Logger.Warning("Session {SessionId} refused a transfer to {Zone}: the house entry {Entry} does not fit.",
                Logger.Args(SessionActor.SessionID, message.DestinationZone, message.HouseEntry?.Zone));
            InformGameClient("You cannot travel to that house.", true);

            return null;
        }

        // Sending the server transfer request to the server will allocate and load the zone.
        var zoneDetails = AskServer<ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP>(message);
        if (message.SendToClient && zoneDetails.ErrorCode == 0) {
            // Check if the destination zone is the same as the current zone. If so, just teleport the player.
            var wizard = GetActiveWizard();
            if (message.DestinationZone == wizard.Zone && message.HouseLotGid == wizard.AttachedLotGid) {
                DoTeleport(message.DestinationLocation);

                return zoneDetails;
            }

            ReadyClientForZoneTransfer(message);
        }
        else if (zoneDetails.ErrorCode != 0) {
            // The server has returned an error code. This means the zone transfer failed.
            InformGameClient("Failed to transfer to zone: " + zoneDetails.ErrorMessage, true);
        }
        else {
            // If we're not sending this message to the client, it means the zone is being loaded
            // for MSG_ATTACH. In which case, the client is already prepared for the zone transfer.
            SetZone(zoneDetails.ZoneActorRef);
            _currentDynamicZoneId = zoneDetails.DynamicZoneId;
        }

        Sender.Tell(zoneDetails);

        return zoneDetails;
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ZONETRANSFERACK))]
    private void ReceiveZoneTransferAck(GAME_5_PROTOCOL.MSG_ZONETRANSFERACK message) {
        // The client has accepted the zone transfer. We can now send the server transfer message.
        var previous = _transferGate.State;
        var accepted = _transferGate.TryAck();
        LogTransferStep("ZONETRANSFERACK received", previous, accepted);
        if (accepted) {
            // The answer to a house teleport's fallback request: the house handshake is over.
            if (_pendingHouseEntry is not null) {
                Timers.Cancel(DeedZoneTimeoutKey);
                Timers.Cancel(HouseTeleportFallbackKey);
                _pendingHouseEntry = null;
            }

            DoZoneTransfer();
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_ZONETRANSFERNACK))]
    private void ReceiveZoneTransferNack(GAME_5_PROTOCOL.MSG_ZONETRANSFERNACK message) {
        // The client has denied the zone transfer.
        Logger.Debug("Client was not OK with zone transfer!");
        var previous = _transferGate.State;
        var accepted = _transferGate.TryNack();
        LogTransferStep("ZONETRANSFERNACK received", previous, accepted);
        if (accepted && _pendingHouseEntry is not null) {
            Timers.Cancel(DeedZoneTimeoutKey);
            Timers.Cancel(HouseTeleportFallbackKey);
            _pendingHouseEntry = null;
            GetActiveWizard().QueuedLotGid = 0;
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY))]
    private void ReceiveStartSigilEntry(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY message) {
        if (message.ContinueCountdown && _activeSigilEntry is not null && !_transferGate.IsQueued) {
            // Same countdown, new destination (the partner stepped off, so this player goes alone when it ends).
            Logger.Information("{0}: the countdown goes on with a new destination (instance owner {1}).",
                Logger.Args(SigilLogName(message), message.InstanceOwnerCharId));
            _activeSigilEntry.InstanceOwnerCharId = message.InstanceOwnerCharId;
            _activeSigilEntry.KeepInstance = message.KeepInstance;
            _activeSigilEntry.OnFailed = message.OnFailed ?? _activeSigilEntry.OnFailed;

            return;
        }

        // A player already counting down stays where they stand when the countdown restarts (Niduus, 2026-10-21: the
        // players moved when a new one came on).
        var restarted = false;
        if (message.Restart && _activeSigilEntry is not null && !_transferGate.IsQueued) {
            // No timer end on screen: the new start below restarts the countdown the player already sees.
            Logger.Information("{0}: countdown restarted for the pair.", Logger.Args(SigilLogName(message)));
            Timers.Cancel(SIGIL_ENTER_TIMER_KEY);
            _activeSigilEntry = null;
            restarted = true;
        }

        if (_transferGate.IsQueued || _activeSigilEntry is not null) {
            Logger.Information("{0}: a countdown or transfer is already running; '{1}' ignored.",
                Logger.Args(SigilLogName(message), message.DestinationZone));
            TellSigilFailed(message, CHARACTER_103_PROTOCOL.HatchTripFailure.Busy, "a countdown or transfer is already running");

            return;
        }

        if (string.IsNullOrEmpty(message.DestinationZone)) {
            TellSigilFailed(message, CHARACTER_103_PROTOCOL.HatchTripFailure.TransferFailed, "no destination");

            return;
        }

        // Force a dismount.
        SessionActor.ActorRef.Tell(
            new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT { Force = true }
        );

        _activeSigilEntry = message;
        _sigilEntryStarted = DateTime.UtcNow;
        var countdown = message.CountdownSeconds > 0 ? message.CountdownSeconds : SIGIL_COUNTDOWN_SECONDS;

        if (!message.NoSigil && !restarted && (!message.NoSnap || message.SnapToCentre)) {
            SnapPlayerToSigilFace(message);
        }

        if (!restarted) {
            _sigilSettleUntil = DateTime.UtcNow + SIGIL_SETTLE_TIME;
            _sigilAnchor = GetActiveGameObject()?.m_location ?? default;
        }

        if (!message.NoSigil && !message.NoTimerBroadcast) {
            SendSigilTimer(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMERSTART {
                Time = countdown,
                SigilGID = message.SigilGID,
                Teleport = 0,
            });
        }

        Timers.StartSingleTimer(
            SIGIL_ENTER_TIMER_KEY,
            new ZONE_102_PROTOCOL.MSG_SIGILENTER(),
            TimeSpan.FromSeconds(countdown));

        Logger.Information("{0} countdown started -> '{1}' (instance owner {2}, face {3})",
            Logger.Args(SigilLogName(message), message.DestinationZone, message.InstanceOwnerCharId, message.FaceSlot));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILENTER))]
    private void ReceiveSigilEnter(ZONE_102_PROTOCOL.MSG_SIGILENTER message) {
        if (_activeSigilEntry is null) {
            return;
        }

        var wizard = GetActiveWizard();
        var gameObj = GetActiveGameObject();
        if (wizard is null || gameObj is null) {
            return;
        }

        // If the player walked off the pad during the countdown and the client's walk-off cancel never
        // arrived, drop the transfer instead of yanking them from across the street.
        if (IsOffSigilPad(wizard, _activeSigilEntry)) {
            CancelSigilCountdown();

            return;
        }

        var entry = _activeSigilEntry;
        _activeSigilEntry = null;

        // A pair shares the instance of the first player (InstanceOwnerCharId); only that player rebuilds it.
        var owner = entry.InstanceOwnerCharId != 0 ? entry.InstanceOwnerCharId : wizard.CharId;
        Logger.Information("{0}: entering instance '{1}' (player {2}, owner {3}, fresh {4})",
            Logger.Args(SigilLogName(entry), entry.DestinationZone, wizard.CharId, owner, !entry.KeepInstance));

        // The game attaches again once it is in the zone, and the attach must land in the same copy: the owner's for a
        // player who went with a dungeon sigil's team, their own otherwise. The hatching trips keep their own record.
        if (entry.OnFailed is not CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED) {
            if (owner != wizard.CharId) {
                ZoneInstanceOwners.Assign(wizard.CharId, entry.DestinationZone, owner);
            }
            else {
                ZoneInstanceOwners.Clear(wizard.CharId, entry.DestinationZone);
            }
        }

        var tpMsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.DestinationZone,
            DestinationLocation = entry.DestinationLoc,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = owner,
            ResetInstance = !entry.KeepInstance,
            Variant = entry.Variant,
        };

        SendTeleportEffects();
        ZONE_102_PROTOCOL.MSG_ZONETRANSFERRSP answer;
        try {
            answer = RequestZoneTransfer(tpMsg);
        }
        catch (Exception ex) {
            Logger.Error("{0}: asking for '{1}' failed. {2}", Logger.Args(SigilLogName(entry), entry.DestinationZone, ex));
            answer = null;
        }

        if (answer is null || answer.ErrorCode != 0) {
            Logger.Warning("{0}: the transfer to '{1}' did not go through ({2}).",
                Logger.Args(SigilLogName(entry), entry.DestinationZone, answer is null ? "not started" : answer.ErrorMessage ?? answer.ErrorCode.ToString()));
            TellSigilFailed(entry, CHARACTER_103_PROTOCOL.HatchTripFailure.TransferFailed,
                answer is null ? "the transfer was not started" : $"error {answer.ErrorCode} {answer.ErrorMessage}");
        }
    }

    /// <summary>
    /// The countdown is drawn by the pad itself and counts down in each game on its own, so everyone in the zone is
    /// told, not only the player on it (Niduus, 2026-10-21: the timer should show whenever a player is on the sigil).
    /// </summary>
    private void SendSigilTimer(Imcodec.MessageLayer.IMessage timerMessage) {
        if (ZoneActor is null) {
            SendToSocket(timerMessage);

            return;
        }

        ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = timerMessage,
            Selfless = false,
            Targets = ZoneBroadcastTarget.Players,
        });
    }

    private static string SigilLogName(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry)
        => string.IsNullOrEmpty(entry?.LogName) ? "Dungeon sigil" : entry.LogName;

    private void TellSigilFailed(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry, CHARACTER_103_PROTOCOL.HatchTripFailure failure, string detail) {
        if (entry?.OnFailed is CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED hatch) {
            hatch.Failure = failure;
            hatch.Detail = detail;
        }

        if (entry?.OnFailed is not null) {
            TellOtherServices(entry.OnFailed);
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_LEAVESIGILTIMERWAITING))]
    private void ReceiveLeaveSigilTimerWaiting(WIZARD_12_PROTOCOL.MSG_LEAVESIGILTIMERWAITING message) {
        if (_activeSigilEntry is null) {
            return;
        }

        // The small hatchery pads are not sigils the game counts the player as standing on, so it sends this the
        // moment the countdown starts (Niduus, 2026-10-20); the distance check at the end of the countdown stands in.
        // The hatchery pads too: with two players on a duo pad the game sent this a few seconds into the countdown
        // although both stood still (Niduus, 2026-10-20). The distance check at the end of the countdown stands in.
        if (_activeSigilEntry.NoSnap || _activeSigilEntry.OnFailed is CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED) {
            Logger.Information("{0}: the game says the player left the pad; ignored for this pad.", Logger.Args(SigilLogName(_activeSigilEntry)));

            return;
        }

        Logger.Information("Client left the dungeon sigil pad — cancelling countdown.");
        CancelSigilCountdown();
    }


    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PATCHINGBLOCKED))]
    private void ReceivePatchingBlocked(WIZARD_12_PROTOCOL.MSG_PATCHINGBLOCKED message) {
        var previous = _transferGate.State;
        var accepted = _transferGate.TryNack();
        LogTransferStep("PATCHINGBLOCKED received", previous, accepted);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_RETRYTELEPORT))]
    private void ReceiveRetryTeleport(GAME_5_PROTOCOL.MSG_RETRYTELEPORT message) {
        var previous = _transferGate.State;
        var accepted = _transferGate.TryRetry();
        LogTransferStep("RETRYTELEPORT received", previous, accepted);
        if (accepted) {
            DoZoneTransfer();
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_ZONEHOP))]
    private void ReceiveZoneHop(WIZARD2_53_PROTOCOL.MSG_ZONEHOP message) {
        // This message is sent when the client has enabled classic mode and wants to reload their current zone.
        var character = GetActiveWizard();

        var previous = _transferGate.State;
        if (!_transferGate.TryRequest()) {
            LogTransferStep("ZONEHOP ignored", previous, false);

            return;
        }

        var zoneTransferRequestMessage = new GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST {
            ZoneName = character.Zone,
            SendAck = 0
        };
        SendToSocket(zoneTransferRequestMessage);

        character.QueuedZoneName = character.Zone;
        character.QueuedZoneLocation = Util.GetCompactStringFromVector(character.Location, character.Orientation);
        character.QueuedLotGid = character.AttachedLotGid;
        LogTransferStep("ZONETRANSFERREQUEST sent", previous, true);
    }

    // This button and the GotoDorm button are locked client-side until level 2.
    // jooty, again? cmon man
    internal const string COMMONS_ZONE = "WizardCity/WC_Hub";

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_GOHOME))]
    private void ReceiveGoHome(WIZARD_12_PROTOCOL.MSG_GOHOME message) {
        // this teleports the wizard to the world hub, NOT their home/dorm. for that you want MSG_GOTODORM. goofy ahh naming scheme
        // Niduus's server: this button always goes to the Commons, from any world.
        var wizard = GetActiveWizard();
        Logger.Information("{0} pressed the Commons button in {1}.", Logger.Args(wizard.CharId, wizard.Zone));
        SendTeleportEffects();

        // A visitor the owner sends away answers with this message too, so a house zone leaves the way its exit
        // trigger does. Anywhere else, the button goes to the Commons.
        if (!HouseExits.TryResolve(wizard.Zone, out var destinationZone, out var destinationLocation)) {
            var zoneMap = WorldHubZones.GetHubForZone("WizardCity");
            destinationZone = COMMONS_ZONE;
            destinationLocation = zoneMap is not null && (string) zoneMap.m_hubZone == COMMONS_ZONE ? (string) zoneMap.m_location : "Start";
        }

        var tpmsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = destinationZone,
            DestinationLocation = destinationLocation,
            SendToClient = true,
            OwnerCharId = wizard.CharId,
        };

        wizard.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var delay = TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME);
        Timers.StartSingleTimer("zonetransfer", tpmsg, delay);
    }

    // This button and the GoHome button are locked client-side until level 2.
    // jooty, again? cmon man
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_GOTODORM))]
    private void ReceiveGotoHouse(WIZARD_12_PROTOCOL.MSG_GOTODORM message) {
        if (_transferGate.IsQueued || Timers.IsTimerActive("zonetransfer")) {
            return;
        }

        var wizard = GetActiveWizard();
        HouseEntry entry;
        try {
            if (!TryPrepareHomeButton(wizard, out entry)) {
                return;
            }
        }
        catch (Exception ex) {
            Logger.Error("Home button of character {CharId} in {Zone} failed: {Error}", Logger.Args(wizard.CharId, wizard.Zone, ex));
            InformGameClient("Your house is not available right now. Please try again later.", true);

            return;
        }

        SendTeleportEffects();

        var tpmsg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.Zone,
            DestinationLocation = HouseDeeds.EntryLocation,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = entry.OwnerCharId,
            HouseEntry = entry,
        };

        wizard.SetTimeHomeLastClicked(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var delay = TimeSpan.FromSeconds(TELEPORT_EFFECTS_TIME);
        Timers.StartSingleTimer("zonetransfer", tpmsg, delay);
    }

    /// <summary>
    /// Picks the house the Home button goes to and gets it ready (Niduus, 2026-10-22): the dorm when no house is
    /// equipped, the equipped house when one is, and the dorm from inside any house that is not the dorm.
    /// </summary>
    /// <param name="wizard">The character who pressed Home.</param>
    /// <param name="entry">The house entry for the transfer when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the player stays where they are; the player has been told why when it matters.</returns>
    private bool TryPrepareHomeButton(Wizard wizard, out HouseEntry entry) {
        entry = null;

        // The client has no in-house check for its Home button, and a same-zone teleport cannot name a house location.
        var dorm = HouseDeeds.ResolveHome(wizard);
        var equipped = HouseDeeds.ResolveEquippedHouse(wizard);
        var inHouse = wizard.AttachedLotGid != 0;
        var inDorm = inHouse && dorm.Lot != 0 && wizard.AttachedLotGid == dorm.Lot;

        // Inside any house but the dorm (an equipped castle, the trainer hall, a friend's house), Home leads to the dorm.
        var toDorm = (inHouse && !inDorm) || equipped == 0;
        Logger.Information("{0} pressed the Home button in {1} (lot {2}): equipped house {3}, dorm lot {4}, going to {5}.",
            Logger.Args(wizard.CharId, wizard.Zone, wizard.AttachedLotGid, equipped, dorm.Lot,
                toDorm ? (inDorm ? "nowhere (already in the dorm)" : "the dorm") : $"the equipped house {equipped}"));
        if (toDorm && inDorm) {
            return false;
        }

        HouseGrantFailure failure;
        if (!toDorm) {
            if (HouseDeeds.TryPrepareHouse(wizard, equipped, out entry, out failure)) {
                return true;
            }

            Logger.Warning("The equipped house {0} of character {1} could not be prepared ({2}); Home goes to the dorm.",
                Logger.Args(equipped, wizard.CharId, failure));
            if (failure == HouseGrantFailure.BackpackFull) {
                InformGameClient(BackpackFullForHouseText, true);

                return false;
            }

            if (inDorm) {
                InformGameClient("Your house is not available right now. Please try again later.", true);

                return false;
            }
        }

        if (!HouseDeeds.TryPrepareHome(wizard, out entry, out failure)) {
            InformGameClient(failure == HouseGrantFailure.BackpackFull
                ? BackpackFullForHouseText
                : "Your house is not available right now. Please try again later.", true);

            return false;
        }

        return true;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_HOUSEDOOR))]
    private void ReceiveHouseDoor(ZONE_102_PROTOCOL.MSG_HOUSEDOOR message) {
        if (_transferGate.IsQueued || Timers.IsTimerActive("zonetransfer")) {
            return;
        }

        // The door sits in a house zone, and the house the player is in decides what is on the other side.
        if (!HouseTravel.TryEnterByDoor(GetActiveWizard(), message.DoorGid, out var entry, out var failure)) {
            if (failure == HouseTravelFailure.BackpackFull) {
                InformGameClient(BackpackFullForHouseText, true);
            }

            return;
        }

        ReceiveZoneTransferRequest(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.Zone,
            DestinationLocation = string.IsNullOrEmpty(message.LocationName) ? HouseDeeds.EntryLocation : message.LocationName,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = entry.OwnerCharId,
            HouseEntry = entry,
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTREQUEST))]
    private void ReceiveWorldTeleportRequest(WIZARD_12_PROTOCOL.MSG_WORLDTELEPORTREQUEST message) {
        if (message.World.Length == 0) { // user clicked "exit", remove the wizbang
            var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG() {
                GameObjectID = GetActiveWizard().GameObjectID,
                WizBangID = (uint) WizBangs.None
            };

            ZoneBroadcast(wizBangMsg, false);

            return;
        }

        var zoneMap = WorldHubZones.GetHubForZone(message.World);
        if (zoneMap is null) {
            Logger.Error("{0} tried to teleport to an invalid world: {1}",
                Logger.Args(GetActiveWizard().CharId, message.World));

            return;
        }

        var zoneName = zoneMap.m_universeTPZone;
        var zoneLocation = zoneMap.m_universeTPLocation;

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER() {
            DestinationZone = zoneName,
            DestinationLocation = zoneLocation,
            SendToClient = true
        };
        ReceiveZoneTransferRequest(msg);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYER))]
    private void ReceiveAddPlayer(ZONE_102_PROTOCOL.MSG_ADDPLAYER message) {
        // This is an internal message from MSG_ATTACH to add the player to the zone.
        if (ZoneActor is null) {
            throw new NullReferenceException(nameof(ZoneActor));
        }

        ZoneActor.Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP))]
    private void ReceiveAddPlayerRsp(ZONE_102_PROTOCOL.MSG_ADDPLAYERRSP message) {
        // I've just been added to a zone. I need to spawn myself for all the other players.
        SpawnMyself();

        // Dismount in no-mount zones, re-equip on leaving (EquipmentService owns the reconcile).
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT());

        if (_randomBackflips) {
            var wizard = GetActiveWizard();
            Timers.StartPeriodicTimer("backflip", new ZONE_102_PROTOCOL.MSG_RANDOMFLIPS {
                ZoneName = wizard.Zone,
                SenderCharID = wizard.CharId
            },
            TimeSpan.FromSeconds(25));
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE))]
    private void ReceiveNewPlayerAddedToZone(ZONE_102_PROTOCOL.MSG_PLAYERADDEDTOZONE message) {
        // A new player has been added to the zone. We need to spawn them.
        // Skip if this is myself.
        if (message.PlayerActor == SessionActor.ActorRef) {
            Logger.Error("{0} {1} received {2} for self.",
                Logger.Args(SessionActor.ActorRef, SessionActor.SessionID, message.GetType()));

            return;
        }

        // Spawn myself for the new player.
        SpawnMyselfFor(message.PlayerActor);
    }

    // Every 25 seconds, a random player in your zone will do a backflip
    // They will only be backflipping for you, nobody else, not even for themselves
    // If you look at the first letter of each variable in this function, it actually spells out the word "gaslit"
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RANDOMFLIPS))]
    private void ReceiveRandomFlips(ZONE_102_PROTOCOL.MSG_RANDOMFLIPS message) {
        var rand = new Random();
        var players = OnlinePlayerCollection.GetPlayersInZone(message.ZoneName);
        players = players.Where(p => p.CharacterId != message.SenderCharID).ToArray();
        if (players.Length < 1) {
            return;
        }

        players = players.Where(p => p.CharacterId != message.SenderCharID).ToArray();

        var randomPlayerIndex = rand.Next(0, players.Length - 1);
        var randomPlayer = players[randomPlayerIndex];
        var castEffect = new CANTRIPSMESSAGES_57_PROTOCOL.MSG_CASTEFFECT {
            GameObjectID = Wizard.GetGameObjectId(randomPlayer.CharacterId),
            SpellTemplateID = 1521398842,
            AnimationName = "P_B_Cantrip_Emote_Backflip"
        };

        SendToSocket(castEffect);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERREMOVEDFROMZONE))]
    private void ReceivePlayerRemovedFromZone(ZONE_102_PROTOCOL.MSG_PLAYERREMOVEDFROMZONE message) {
        // A player has been removed from the zone. We need to remove them.
        // Skip if this is myself.
        if (message.PlayerActor == SessionActor.ActorRef) {
            Logger.Error("{0} {1} received {2} for self.",
                Logger.Args(SessionActor.ActorRef, SessionActor.SessionID, message.GetType()));

            return;
        }

        // Remove the player from the zone.
        var removeMsg = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT {
            GameObjectID = message.GlobalId
        };

        SendToSocket(removeMsg);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST))]
    private void ReceiveZoneBroadcast(ZONE_102_PROTOCOL.MSG_ZONEBROADCAST message) {
        if (ZoneActor is null) {
            var messageType = message.Message?.GetType() ?? message.Messages?.FirstOrDefault()?.GetType();
            Logger.Debug("Session {SessionId} dropped a zone broadcast of {MessageType}: it has no zone.",
                Logger.Args(SessionActor.SessionID, messageType?.Name));

            return;
        }

        ZoneActor.Tell(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PLAYERMOVE))]
    private void ReceiveZoneInteraction(ZONE_102_PROTOCOL.MSG_PLAYERMOVE message) {
        // This is an exception. Sometimes the MoveService interval happens as we zone transfer.
        if (ZoneActor is null) {
            return;
        }

        // Stepping off the pad stops the countdown right away, not only when it runs out (Niduus, 2026-10-21: the
        // countdown went on after stepping off). The game's own walk-off message is not sent for every pad.
        if (_activeSigilEntry is { NoSigil: false } entry && GetActiveWizard() is { } wizard) {
            var here = wizard.Location;
            if (DateTime.UtcNow < _sigilSettleUntil) {
                _sigilAnchor = here;
            }
            else if (IsOffSigilPad(wizard, entry) || SigilPadCheck.IsOff(here.X - _sigilAnchor.X, here.Y - _sigilAnchor.Y, SIGIL_MOVE_TOLERANCE / 1.25f)) {
                CancelSigilCountdown();
            }
        }

        ZoneActor.Forward(message);
    }

    private static bool IsOffSigilPad(Wizard wizard, ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry) {
        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);
        var pos = wizard.Location;

        return SigilPadCheck.IsOff(pad.X - pos.X, pad.Y - pos.Y, entry.Radius);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY))]
    private void ReceiveQueryZoneObject(ZONE_102_PROTOCOL.MSG_QUERYZONEENTITY message) {
        if (ZoneActor is null) {
            Logger.Debug("Session {SessionId} answered a zone entity query with nothing: it has no zone.",
                Logger.Args(SessionActor.SessionID));
            Sender.Tell(new ZONE_102_PROTOCOL.MSG_QUERYZONEENTITYRSP { Found = false });

            return;
        }

        ZoneActor.Forward(message);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SENDTOHUB))]
    private void ReceiveBootToHub(ZONE_102_PROTOCOL.MSG_SENDTOHUB message) {
        var wizard = GetActiveWizard();
        var zoneName = wizard.Zone;

        // A private room's exit (the duel component found it in the zone's copy) beats the world's hub.
        if (!string.IsNullOrEmpty(message.DestinationZone)) {
            Logger.Information("Leaving {0} by its exit after a battle: {1} {2}",
                Logger.Args(zoneName, message.DestinationZone, message.DestinationLocation));
            ReceiveZoneTransferRequest(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = message.DestinationZone,
                DestinationLocation = message.DestinationLocation ?? "",
                SendToClient = true,
                OwnerCharId = wizard.CharId // as the room's exit doors send it
            });

            return;
        }

        var worldHubMap = WorldHubZones.GetHubForZone(zoneName);
        if (worldHubMap is null) {
            Logger.Error("Could not find world hub mapping for zone {0}",
                Logger.Args(zoneName));

            return;
        }

        var destinationZoneName = worldHubMap.m_hubZone;
        var destinationZoneLocation = worldHubMap.m_location;

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER() {
            DestinationZone = destinationZoneName,
            DestinationLocation = destinationZoneLocation,
            SendToClient = true
        };

        ReceiveZoneTransferRequest(msg);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS))]
    private void ReceiveTeleportEffects(CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS message) {
        SendTeleportEffects();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEHEALTICK))]
    private void ReceiveZoneHealTick(ZONE_102_PROTOCOL.MSG_ZONEHEALTICK message) {
        var wizard = GetActiveWizard();
        var currentWizardHealth = wizard.GameStats.m_currentHitpoints;
        var maxWizardHealth = wizard.GameStats.m_baseHitpoints;

        // If this wizard is max health, skip.
        if (currentWizardHealth >= maxWizardHealth) {
            return;
        }

        // Update our Wizard server side.
        var healPercent = message.MaxHealthPercent;
        float healAmount = healPercent / 100 * maxWizardHealth;
        var newHealth = Math.Min(currentWizardHealth + (int) healAmount, maxWizardHealth);

        wizard.UpdateHealth(newHealth);

        // Inform the client about the new health changes.
        // The client has a max health increase effect applied, so sending it here would double the health client side.
        var magicSchool = wizard.MagicSchoolBehavior.MagicSchool;
        var level = wizard.MagicSchoolBehavior.Level;
        var baseStats = MagicLevelsConfig.GetPlayerLevelInfo(magicSchool, level);
        var normMaxHealth = baseStats.m_hitpoints;

        var networkMessage = new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH() {
            CharacterID = wizard.GameObject.m_globalID,
            NewHealth = newHealth,
            NewHealthMax = normMaxHealth,
            DisplayDiff = 1,
        };
        SendToSocket(networkMessage);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY))]
    private void ReceiveRealmInfoQuery(GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY message) {
        // Query the LoginServer's GameServerPool for the realm list.
        var realmListMsg = new SERVER_100_PROTOCOL.MSG_REALMLIST();
        var realmList = AskServer<SERVER_100_PROTOCOL.MSG_REALMLIST>(realmListMsg);

        var currentRealm = "Imlight";
        var currentZone = GetActiveWizard()?.Zone ?? "";

        // Query our own game server for the current realm name.
        try {
            var serverInfo = AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(
                new SERVER_100_PROTOCOL.MSG_QUERYSERVER());
            currentRealm = serverInfo.RealmName ?? currentRealm;
        }
        catch { }

        // Serialize the realm list as a RealmInfoList PropertyClass blob.
        // The client expects this exact type; we cannot fabricate the format.
        var realmInfoList = new RealmInfoList {
            m_infoList = []
        };
        for (int i = 0; i < realmList.RealmNames.Length; i++) {
            realmInfoList.m_infoList.Add(new RealmInfo {
                m_realmName = realmList.RealmNames[i],
                m_displayName = realmList.RealmNames[i],
                m_realmPopulation = realmList.PlayerCounts[i]
            });
        }

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(realmInfoList, (PropertyFlags) 31, out var realmInfoBlob)) {
            Logger.Error("Failed to serialize RealmInfoList for MSG_REALM_INFO_QUERY.");

            return;
        }

        // Serialize an empty instance list; the client requires a valid
        // InstanceInfoList PropertyClass blob, not an empty string.
        var instanceInfoList = new InstanceInfoList {
            m_instanceList = new List<InstanceInfo>()
        };
        if (!serializer.Serialize(instanceInfoList, (PropertyFlags) 31, out var instanceInfoBlob)) {
            Logger.Error("Failed to serialize InstanceInfoList for MSG_REALM_INFO_QUERY.");

            return;
        }

        var rsp = new GAME_5_PROTOCOL.MSG_REALM_INFO_QUERY {
            RealmInfoList = realmInfoBlob,
            CurrentRealm = currentRealm,
            InstanceInfoList = instanceInfoBlob,
            CurrentZone = currentZone
        };
        SendToSocket(rsp);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_TRANSFER_REALMS))]
    private void ReceiveTransferRealms(GAME_5_PROTOCOL.MSG_TRANSFER_REALMS message) {
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();

        if (wizard is null || account is null) {
            return;
        }

        // Ask the LoginServer to create a session key on the target realm's game server.
        var createKeyMsg = new SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEY {
            Account = account,
            TargetRealmName = message.RealmName
        };

        SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEYRSP keyRsp;
        try {
            keyRsp = AskServer<SERVER_100_PROTOCOL.MSG_CREATEPLAYERKEYRSP>(createKeyMsg);
        }
        catch {
            Logger.Error("Failed to create player key for realm transfer to {Realm}.",
                Logger.Args(message.RealmName));

            return;
        }

        if (!keyRsp.Success) {
            Logger.Warning("Realm transfer to {Realm} failed; realm not found.",
                Logger.Args(message.RealmName));

            return;
        }

        // Send MSG_SERVERTRANSFER to redirect the client to the new game server.
        var transferIp = ClientAddress.For(SessionActor, keyRsp.IP);
        var serverTransfer = new GAME_5_PROTOCOL.MSG_SERVERTRANSFER {
            IP = transferIp,
            TCPPort = keyRsp.Port,
            UDPPort = keyRsp.Port,
            Key = 0, // Session key is already stored on the target server.
            UserID = account.AccountId,
            CharID = wizard.CharId,
            ZoneName = wizard.Zone,
            ZoneID = new Imcodec.Types.GID((ulong) keyRsp.Port),
            Location = Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation),
            Slot = 0,
            SessionSlot = 0,
            SessionID = 0,
            TargetPlayerID = wizard.CharId,
            TransitionID = 1,
            FallbackIP = transferIp,
            FallbackTCPPort = keyRsp.Port,
            FallbackUDPPort = keyRsp.Port,
            FallbackZone = wizard.Zone,
            FallbackZoneID = new Imcodec.Types.GID((ulong) keyRsp.Port)
        };
        SendToSocket(serverTransfer);
    }

    [MessageHandler(typeof(GAME2_55_PROTOCOL.MSG_CURRENTREALM))]
    private void ReceiveCurrentRealm(GAME2_55_PROTOCOL.MSG_CURRENTREALM message) {
        var wizard = GetActiveWizard();
        var currentZone = wizard?.Zone ?? "";

        var currentRealm = "Imlight";
        try {
            var serverInfo = AskServer<SERVER_100_PROTOCOL.MSG_SERVERINFO>(
                new SERVER_100_PROTOCOL.MSG_QUERYSERVER());
            currentRealm = serverInfo.RealmName ?? currentRealm;
        }
        catch { }

        var rsp = new GAME2_55_PROTOCOL.MSG_CURRENTREALM {
            CurrentRealm = currentRealm,
            CurrentZone = currentZone
        };
        SendToSocket(rsp);
    }

    private void SetZone(IActorRef actorRef) {
        ZoneActor = actorRef;
    }

    private void ReadyClientForZoneTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        var character = GetActiveWizard();
        var previous = _transferGate.State;
        if (!_transferGate.TryRequest()) {
            LogTransferStep("ZONETRANSFERREQUEST not sent", previous, false);

            return;
        }

        character.QueuedZoneName = message.DestinationZone;
        character.QueuedZoneLocation = message.DestinationLocation;
        character.QueuedLotGid = message.HouseLotGid;
        _pendingHouseEntry = message.HouseEntry;

        if (message.HouseEntry is not null) {
            SendHouseZoneRequest(character, message.HouseEntry, message.DestinationLocation);
            LogTransferStep($"house zone request ({message.HouseEntry.Kind}) sent", previous, true);

            return;
        }

        // Ask the client if it's okay with being transferred.
        var msg = new GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST {
            ZoneName = message.DestinationZone,
            SendAck = 1
        };
        SendToSocket(msg);

        LogTransferStep("ZONETRANSFERREQUEST sent", previous, true);
    }

    private bool TryUseOwnHouse(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        if (HouseTravel.TryEnterZone(GetActiveWizard(), message.DestinationZone, out var entry, out var failure)) {
            message.HouseEntry = entry;
            message.IsPrivate = true;
            message.OwnerCharId = entry.OwnerCharId;

            return true;
        }

        Logger.Information("Session {SessionId} cannot teleport into house zone {Zone}: {Failure}.",
            Logger.Args(SessionActor.SessionID, message.DestinationZone, failure));
        InformGameClient(failure == HouseTravelFailure.BackpackFull ? BackpackFullForHouseText : "You cannot travel to that house.", true);

        return false;
    }

    private bool IsValidHouseTransfer(ZONE_102_PROTOCOL.MSG_ZONETRANSFER message) {
        var entry = message.HouseEntry;
        if (entry is null) {
            return !HouseZoneIndex.IsHouseZone(message.DestinationZone);
        }

        return entry.LotGid != 0 && string.Equals(entry.Zone, message.DestinationZone, StringComparison.OrdinalIgnoreCase);
    }

    private void SendHouseZoneRequest(Wizard character, HouseEntry entry, string location) {
        // A house transfer has no ZONETRANSFERREQUEST: the client's echo of this message is its answer.
        foreach (var itemGid in entry.NewItemGids) {
            var item = character.InventoryBehavior.GetItem(itemGid);
            if (item is null || !ItemSync.SendAdded(SessionActor.ActorRef, character, item)) {
                Logger.Error("Session {SessionId} could not send the new house item {ItemId} to the client.",
                    Logger.Args(SessionActor.SessionID, itemGid));
            }
        }

        // A friend's spot arrives as coordinates, but the teleport request names a location in the house zone. Both
        // visits that sent coordinates here hung on the loading screen (Niduus, 2026-10-22); the transfer itself still
        // takes the player to the coordinates.
        var requestLocation = entry.Kind == HouseEntryKind.Teleport && IsCoordinates(location) ? HouseDeeds.EntryLocation : location;
        SendToSocket(entry.CreateRequest(requestLocation));

        StartDeedZoneTimeout();
        if (entry.Kind == HouseEntryKind.Teleport) {
            Timers.StartSingleTimer(HouseTeleportFallbackKey, new SERVICE_101_PROTOCOL.MSG_HOUSETELEPORT_FALLBACK(), HouseTeleportFallbackTime);
        }
    }

    private static bool IsCoordinates(string location)
        => !string.IsNullOrEmpty(location) && location.Split(',').Length == 4;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_HOUSETELEPORT_FALLBACK))]
    private void OnHouseTeleportFallback(SERVICE_101_PROTOCOL.MSG_HOUSETELEPORT_FALLBACK _) {
        if (_pendingHouseEntry is not { Kind: HouseEntryKind.Teleport } || _transferGate.State != ZoneTransferState.Requested) {
            return;
        }

        // The client did not answer the house teleport request, which is what a visitor on the loading screen sees
        // forever. A plain zone transfer request loads the same zone; the lot and the owner stay queued, so the attach
        // still lands in the owner's house.
        try {
            var character = GetActiveWizard();
            Logger.Information("Session {SessionId}: no answer to the house teleport request for {Zone} (lot {Lot}) after {Seconds} s; "
                    + "sending a plain zone transfer request instead.",
                Logger.Args(SessionActor.SessionID, character.QueuedZoneName, character.QueuedLotGid, HouseTeleportFallbackTime.TotalSeconds));
            SendToSocket(new GAME_5_PROTOCOL.MSG_ZONETRANSFERREQUEST {
                ZoneName = character.QueuedZoneName,
                SendAck = 1
            });
        }
        catch (Exception ex) {
            Logger.Error("Session {SessionId} could not send the house teleport fallback: {Error}", Logger.Args(SessionActor.SessionID, ex));
            AbortDeedZoneTransfer("house teleport fallback failed");
        }
    }

    private void StartDeedZoneTimeout() {
        Timers.StartSingleTimer(DeedZoneTimeoutKey, new SERVICE_101_PROTOCOL.MSG_DEEDZONE_TIMEOUT(), HousingSettings.DeedZoneTimeout);
    }

    private void AbortDeedZoneTransfer(string step) {
        Timers.Cancel(DeedZoneTimeoutKey);
        Timers.Cancel(HouseTeleportFallbackKey);
        var previous = _transferGate.State;
        var accepted = _transferGate.TryNack();
        LogTransferStep(step, previous, accepted);

        _pendingHouseEntry = null;
        GetActiveWizard().QueuedLotGid = 0;
        InformGameClient("The house could not be prepared. Please try again.", true);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTDEEDZONE))]
    private void ReceiveDeedZoneEcho(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTDEEDZONE message)
        => HandleHouseZoneEcho(_pendingHouseEntry?.Read(message) ?? HouseEchoStatus.NotForThisEntry, "REQUESTDEEDZONE");

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONETELEPORT))]
    private void ReceiveHousingZoneTeleportEcho(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONETELEPORT message)
        => HandleHouseZoneEcho(_pendingHouseEntry?.Read(message) ?? HouseEchoStatus.NotForThisEntry, "REQUESTHOUSINGZONETELEPORT");

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONE))]
    private void ReceiveHousingZoneEcho(WIZARDHOUSING_50_PROTOCOL.MSG_REQUESTHOUSINGZONE message)
        => HandleHouseZoneEcho(_pendingHouseEntry?.Read(message) ?? HouseEchoStatus.NotForThisEntry, "REQUESTHOUSINGZONE");

    private void HandleHouseZoneEcho(HouseEchoStatus status, string messageName) {
        // Only the echo of the request we sent counts. Anything else the client sends under these names is ignored.
        if (status == HouseEchoStatus.NotForThisEntry || _transferGate.State != ZoneTransferState.Requested) {
            return;
        }

        switch (status) {
            case HouseEchoStatus.Ready:
                Timers.Cancel(DeedZoneTimeoutKey);
                Timers.Cancel(HouseTeleportFallbackKey);
                var previous = _transferGate.State;
                var accepted = _transferGate.TryAck();
                LogTransferStep($"{messageName} echo received", previous, accepted);
                if (accepted) {
                    _pendingHouseEntry = null;
                    DoZoneTransfer();
                }

                break;
            case HouseEchoStatus.Patching:
                // The client is still patching the house zone archive.
                StartDeedZoneTimeout();

                break;
            default:
                AbortDeedZoneTransfer($"{messageName} refused by the client");

                break;
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_DEEDZONE_TIMEOUT))]
    private void OnDeedZoneTimeout(SERVICE_101_PROTOCOL.MSG_DEEDZONE_TIMEOUT _) {
        if (_pendingHouseEntry is null || _transferGate.State != ZoneTransferState.Requested) {
            return;
        }

        AbortDeedZoneTransfer("house zone request timed out");
    }

    private void LogTransferStep(string step, ZoneTransferState previous, bool accepted) {
        Logger.Information("Session {SessionId} {Step} -> {Zone}, gate {Previous} -> {Current}, accepted={Accepted}",
            Logger.Args(SessionActor.SessionID, step, GetActiveWizard()?.QueuedZoneName, previous, _transferGate.State, accepted));
    }

    private void DoZoneTransfer() {
        // Remove the player from their current zone. We're awaiting a reply so the zone can properly clean up
        // before we continue on potentially a different thread.
        try {
            var removePlayerMsg = new ZONE_102_PROTOCOL.MSG_REMOVEPLAYER() {
                PlayerActor = SessionActor.ActorRef,
                GlobalId = GetActiveGameObject().m_globalID,
                IsPlayerStillConnected = true,
                MobileId = GetActiveGameObject().m_nMobileID
            };
            _ = ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_REMOVEPLAYERRSP>(removePlayerMsg, _zoneRemovalWaitTime).Result;

            // Remove the player from the online player collection.
            OnlinePlayerCollection.RemoveOnlinePlayer(SessionActor.SessionID);
        }
        catch {
            Logger.Warning("Zone removal timeout of {0} seconds exceeded.", Logger.Args(ZONE_REMOVAL_WAIT_TIME_IN_SECONDS));
        }

        // Defer the server transfer so the client can finish tearing down zone objects.
        Timers.StartSingleTimer("zone-transfer-delay", new SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY(),
                                TimeSpan.FromMilliseconds(ZoneTransferDelayMs));
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY))]
    private void OnZoneTransferDelay(SERVICE_101_PROTOCOL.MSG_ZONETRANSFER_DELAY _) {
        var character = GetActiveWizard();
        if (character.QueuedZoneName is null) {
            LogTransferStep("transfer delay elapsed with no queued zone", _transferGate.State, false);

            return;
        }

        var previous = _transferGate.State;
        var accepted = _transferGate.TrySend();
        LogTransferStep("transfer delay elapsed", previous, accepted);
        if (!accepted) {
            return;
        }

        var account = GetSocketAccount();

        // Persist the current zone and location as explicit fallback data so the
        // Wizard record reflects where the player should return if the new attach fails.
        WizardCollection.UpdateCharacterZone(character, character.Zone, character.ZoneDisplayName);
        WizardCollection.UpdateCharacterLocation(character, character.Location, character.Orientation.Z);

        var transferIp = ClientAddress.For(SessionActor, character.GameServerIp);
        // A failed transfer out of a house falls back into it, and the house is named by its lot.
        var fallbackZoneId = character.AttachedLotGid != 0 ? character.AttachedLotGid : _currentDynamicZoneId;
        var serverTransfer = new GAME_5_PROTOCOL.MSG_SERVERTRANSFER() {
            IP = transferIp,
            TCPPort = character.GameServerPort,
            UDPPort = character.GameServerPort,
            UserID = account.AccountId,
            CharID = character.CharId,
            ZoneName = character.QueuedZoneName,
            ZoneID = character.QueuedLotGid,
            Location = character.QueuedZoneLocation,
            Slot = 0,
            SessionSlot = 0,
            SessionID = 0,
            TargetPlayerID = character.CharId,
            TransitionID = 1,
            FallbackIP = transferIp,
            FallbackTCPPort = character.GameServerPort,
            FallbackUDPPort = character.GameServerPort,
            FallbackZone = character.Zone,
            FallbackZoneID = fallbackZoneId
        };
        SendToSocket(serverTransfer);
        Logger.Information("Session {SessionId} SERVERTRANSFER sent -> {Zone} {Ip}:{Port}",
            Logger.Args(SessionActor.SessionID, character.QueuedZoneName, transferIp, character.GameServerPort));

        // Register fallback data on the GameServer so the new session can
        // proactively recover if MSG_ATTACH never arrives.
        SessionActor.ServerRef.Tell(new SERVICE_101_PROTOCOL.MSG_REGISTER_FALLBACK {
            RemoteIp = SessionActor.RemoteIp,
            UserId = account.AccountId,
            CharId = character.CharId,
            FallbackZone = character.Zone,
            FallbackZoneId = fallbackZoneId,
            FallbackLocation = Util.GetCompactStringFromVector(character.Location, character.Orientation),
            GameServerIp = character.GameServerIp,
            GameServerPort = character.GameServerPort
        });
    }

    private void DoTeleport(string location) {
        var coords = Util.GetVectorFromCompactString(location);
        var compressedCoords = coords / 4;

        var directionYaw = coords.W % (2 * Math.PI);
        if (directionYaw < 0) {
            directionYaw += 2 * Math.PI;
        }

        var serverTele = new GAME_5_PROTOCOL.MSG_SERVERTELEPORT() {
            LocationX = (ushort) compressedCoords.X,
            LocationY = (ushort) compressedCoords.Y,
            LocationZ = (ushort) compressedCoords.Z,
            Direction = (byte) Math.Round(directionYaw / (2 * Math.PI) * 250),
            MobileID = GetActiveGameObject().m_nMobileID,
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = serverTele,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastMsg);
    }

    private void SpawnMyself() {
        var wizard = GetActiveWizard();
        var properGameObj = WizardObjectLoader.GetPlayerGameObject(wizard);

        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(properGameObj, flags, out var gameObjData)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addMsg = new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = gameObjData,
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = addMsg
        };

        ZoneActor.Tell(broadcastMsg);
    }

    private void SpawnMyselfFor(IActorRef actorRef) {
        var wizard = GetActiveWizard();
        var properGameObj = WizardObjectLoader.GetPlayerGameObject(wizard);

        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(properGameObj, flags, out var gameObjData)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addMsg = new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = gameObjData,
        };

        actorRef.Tell(addMsg);
    }

    private void SendTeleportEffects() {
        var wizard = GetActiveWizard();
        var now = DateTimeOffset.UtcNow;

        SendCantGoHomeEffect(now);

        // what does this do? who knows! its probably important.
        var enterState = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = wizard.GameObject.m_globalID,
            State = StringHash.Compute("Teleport"),
        };
        var broadcastWrapper = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = enterState,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastWrapper);

        SendRecallHomeEffect(now);
    }

    private void SendCantGoHomeEffect(DateTimeOffset unixTimeStart) {
        var wizard = GetActiveWizard();
        NamedEffect effect = new NamedEffect {
            m_effectNameID = StringHash.Compute("CantGoHome"),
            m_endTime = (uint) unixTimeStart.AddSeconds(30).ToUnixTimeSeconds(),
            m_internalID = wizard.NextEffectId(),
        };

        wizard.GameEffects.Add(effect);

        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_zoneObjectSerializer.Serialize(effect, flags, out var serializedEffect)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addEffect = new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectData = serializedEffect
        };

        SendToSocket(addEffect);
    }

    private void SendRecallHomeEffect(DateTimeOffset time) {
        var wizard = GetActiveWizard();

        // on live servers, the end time is 200 seconds from the time gohome is sent. i still have no clue why.
        // also on live servers, when teleporting in zone, it will send the effects like 3 times. i also have no clue on this either.
        var effect = new NamedEffect {
            m_effectNameID = StringHash.Compute("RecallHome"),
            m_endTime = (uint) time.AddSeconds(2).ToUnixTimeSeconds(),
            m_internalID = wizard.NextEffectId(),
        };

        wizard.GameEffects.Add(effect);

        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_effectSerializer.Serialize(effect, flags, out var serializedEffect)) {
            Logger.Error("Failed to serialize game object for {0}",
                Logger.Args(wizard.CharId));

            return;
        }

        var addEffect = new GAME_5_PROTOCOL.MSG_ADDEFFECT {
            GameObjectID = wizard.GameObject.m_globalID,
            EffectData = serializedEffect
        };
        var broadcastWrapper = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = addEffect,
            Selfless = false,
        };
        ReceiveZoneBroadcast(broadcastWrapper);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_CANCELSIGILENTRY))]
    private void ReceiveCancelSigilEntry(ZONE_102_PROTOCOL.MSG_CANCELSIGILENTRY message)
        => CancelSigilCountdown(silent: true);

    private void CancelSigilCountdown(bool silent = false) {
        var entry = _activeSigilEntry;
        _activeSigilEntry = null;
        Timers.Cancel(SIGIL_ENTER_TIMER_KEY);

        if (entry is null) {
            return;
        }

        // A hatchery pad keeps counting down while anyone is on it; the pad ends it when the last player leaves.
        if (!entry.NoSigil && entry.OnFailed is not CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED) {
            SendSigilTimer(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMEREND { SigilGID = entry.SigilGID });
        }

        // Give the player their mount back.
        SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT());

        if (silent) {
            Logger.Information("{0} countdown cancelled to start another.", Logger.Args(SigilLogName(entry)));

            return;
        }

        Logger.Information("{0} countdown cancelled (stepped off pad)", Logger.Args(SigilLogName(entry)));
        TellSigilFailed(entry, CHARACTER_103_PROTOCOL.HatchTripFailure.Cancelled, "stepped off the pad");
    }

    private void SnapPlayerToSigilFace(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry) {
        var gameObj = GetActiveGameObject();
        var wizard = GetActiveWizard();
        if (gameObj is null || wizard is null) {
            return;
        }

        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);
        float faceX, faceY, faceZ = pad.Z, faceYaw;
        if (entry.SnapToCentre) {
            faceX = pad.X;
            faceY = pad.Y;
            faceYaw = gameObj.m_orientation.Z;
        }
        else if (TryGetSigilFaceSlot(entry, out var slotPos, out var slotYaw)) {
            faceX = slotPos.X;
            faceY = slotPos.Y;
            faceZ = slotPos.Z;
            faceYaw = slotYaw;
        }
        else {
            // Fallback: offset off the pad centre toward where the player approached from, facing centre.
            var here = wizard.Location;
            double dx = here.X - pad.X, dy = here.Y - pad.Y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            const double SIGIL_FACE_OFFSET = 220.0;
            double ox, oy;
            if (len > 1.0) {
                ox = dx / len * SIGIL_FACE_OFFSET;
                oy = dy / len * SIGIL_FACE_OFFSET;
            }
            else {
                ox = -Math.Cos(pad.W) * SIGIL_FACE_OFFSET;
                oy = -Math.Sin(pad.W) * SIGIL_FACE_OFFSET;
            }

            faceX = (float) (pad.X + ox);
            faceY = (float) (pad.Y + oy);
            double yaw = Math.Atan2(-oy, -ox);
            yaw = (2 * Math.PI) - yaw - SIGIL_YAW_ERROR_COMPENSATION;
            if (yaw < 0) {
                yaw += 2 * Math.PI;
            }

            faceYaw = (float) yaw;
        }

        // Broadcast the slide (Selfless=false so the player sees their own drag-in) + set authoritative pos.
        ReceiveZoneBroadcast(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new WIZARD_12_PROTOCOL.MSG_AGGRO {
                GlobalID = gameObj.m_globalID,
                LocX = faceX,
                LocY = faceY,
                LocZ = faceZ,
                Yaw = faceYaw,
            },
            Selfless = false,
        });
        gameObj.m_location = new Imcodec.Math.Vector3(faceX, faceY, faceZ);
        gameObj.m_orientation = new Imcodec.Math.Vector3(0, 0, faceYaw);
    }

    private static bool TryGetSigilFaceSlot(ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY entry,
                                            out Imcodec.Math.Vector3 pos, out float yaw) {
        pos = default;
        yaw = 0f;
        if (string.IsNullOrEmpty(entry.SigilType) || string.IsNullOrEmpty(entry.SigilLoc)) {
            return false;
        }

        var template = SigilFactory.GetSigilTemplate(entry.SigilType);
        var subCircles = template?.m_subCircles;
        if (subCircles is null || subCircles.Count == 0) {
            return false;
        }

        // Player faces only (dungeon-entry sigils shouldn't carry monster circles, but filter defensively).
        var playerSlots = new List<SigilSubCircle>();
        foreach (var sc in subCircles) {
            if (sc is null || sc.m_locationType == "MonsterCircle") {
                continue;
            }

            playerSlots.Add(sc);
        }
        if (playerSlots.Count == 0) {
            return false;
        }

        var slot = playerSlots[Math.Clamp(entry.FaceSlot, 0, playerSlots.Count - 1)];
        var pad = Util.GetVectorFromCompactString(entry.SigilLoc);

        double sigilRotation = pad.W;
        if (sigilRotation < 0) {
            sigilRotation += 2 * Math.PI;
        }

        double rotationRadians = slot.m_rotation * (Math.PI / 180.0);
        double sx = pad.X + (slot.m_radius * Math.Cos(rotationRadians - sigilRotation));
        double sy = pad.Y + (slot.m_radius * Math.Sin(rotationRadians - sigilRotation));

        double faceYaw = Math.Atan2(pad.Y - sy, pad.X - sx);
        faceYaw = (2 * Math.PI) - faceYaw - SIGIL_YAW_ERROR_COMPENSATION;
        if (faceYaw < 0) {
            faceYaw += 2 * Math.PI;
        }

        pos = new Imcodec.Math.Vector3((float) sx, (float) sy, pad.Z);
        yaw = (float) faceYaw;

        return true;
    }

}
