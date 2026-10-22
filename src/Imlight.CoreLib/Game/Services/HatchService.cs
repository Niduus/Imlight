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
 * HATCH SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Pet hatching with another player (in person, from the player's right-click menu) and at the
 * hatchmaking kiosk (Niduus, 2026-10-20: "work on hatching. Solo and player to player."). The egg
 * itself is made by PetService (MSG_BREEDPETS), which owns the egg timers.
 *
 * USAGE EXAMPLE:
 * Attached to every game session by GameServiceFactory.
 *
 * NOTE:
 * The hatchery's pet morphing sigils come here too (MSG_HATCHSIGIL from InteractPetMorphSigilComponent):
 * pressing X on a solo sigil, or on a duo sigil alone, starts the sigil countdown (ZoneService,
 * MSG_STARTSIGILENTRY) to a new private instance of the pet morph zone ThePhantomZoneWorld/PetGameMorph;
 * a duo sigil with two players sends both to one shared instance. There the game is sent the morph
 * window (MSG_PETGAMEINIT "PetGameMorph"), picks arrive as MSG_PETMORPHSET and the hatch as
 * MSG_PETMORPHREADY; after the egg (or a cancel, or [Hatching] MorphZoneMinutes) the player goes back
 * to the pad. The player arrives in a new session, so PetMorphZone keeps the trip. The hatchery steps
 * log "Hatching sigil", the zone's "Hatching zone" (with every message the game sends there).
 * In person, each side's session keeps its own state and tells the other's session what its player
 * did (MSG_HATCHPEER), as TradeService does through its directory. When both are ready, each session
 * breeds its own egg: its player's pet with the partner's. Every message the game sends here is
 * logged with all its fields, because what the game sends and expects is not known yet.
 *
 * TODO:
 * - The status values (join -1/0/1, ready 0/1/2, result codes) are copied from trading; unverified.
 * - The morph window (read from the game's code, untested in game): the game handles the pet game
 *   messages only once it has the zone's PetGameMorph object (sent by RenderComponent). MSG_PETGAMEINIT
 *   opens the window and the game answers MSG_PETGAMEREADY; MSG_PETGAMESTART then carries the partner's
 *   PetGameMorphPartner (character id 0 alone: both sides pick from the player's own pets, 2 needed).
 *   INIT is sent again every 3 s until READY comes. [Hatching] SigilWindow false keeps players in the
 *   hatchery with the chat list.
 * - The kiosk's PetData blobs are guesses: HatchmakingPetsList, HatchmakingPetTypesList and
 *   HatchmakingPetGIDList written like the pet game data, and the pet item for PETDATA.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class HatchService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const int INVITE_TIMEOUT_SECONDS = 60;
    private const string INVITE_TIMER = "hatchInvite";

    // Copied from trading (TradeParticipant): -1 asked but not answered, 0 left or declined, 1 joined.
    private const int JOIN_PENDING = -1;
    private const int JOIN_LEFT = 0;
    private const int JOIN_JOINED = 1;

    // The in-person hatch this session is part of; _peerCharId is 0 when there is none.
    private ulong _peerCharId;
    private IActorRef _peerSession;
    private string _peerPath;
    private bool _isRequester;
    private int _myJoin;
    private int _peerJoin;
    private int _myReady;
    private int _peerReady;
    private ulong _myPetId;
    private PetParent _peerPet;
    private bool _bred;

    // The hatching sigil this session's player hatches two of their own pets from; _soloSigilGid is 0 when there is none.
    private const uint MORPH_FLAGS = (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit);
    // The game reads a u32 of serializer flags first (SerializerCoreObjects), so they are written (Niduus, 2026-10-20:
    // "Failed to load pet morph partner data" without them).
    private static readonly CoreObjectSerializer s_morphSerializer = new(behaviors: SerializerFlags.SerializeFlags);
    private ulong _soloSigilGid;
    private readonly List<ulong> _soloPicks = [];
    // The pets on the hatch window's two sides (side 0 and 1), alone in the morph zone.
    private readonly ulong[] _windowSides = new ulong[2];

    // The pet morph zone (see PetMorphZone). _pendingTrip is the trip counting down in the hatchery; _morph is the
    // visit this session's player is on in the zone, null anywhere else.
    private const float MORPH_TRIP_MIN_RADIUS = 400.0f;
    private const string MORPH_INIT_TIMER = "hatchZoneInit";
    private const string MORPH_PARTNER_TIMER = "hatchZonePartner";
    private const string MORPH_TIMEOUT_TIMER = "hatchZoneTimeout";
    private const string MORPH_RETURN_TIMER = "hatchZoneReturn";
    private static readonly TimeSpan MORPH_INIT_DELAY = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MORPH_INIT_RETRY = TimeSpan.FromSeconds(3);
    private const int MORPH_INIT_TRIES = 4;
    private static readonly TimeSpan MORPH_CLOSE_DELAY = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan MORPH_PARTNER_WAIT = TimeSpan.FromSeconds(60);
    // Long enough to see the egg in the hatch window; closing the window leaves sooner.
    private static readonly TimeSpan MORPH_RETURN_DELAY = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PARTNER_LEFT_RETURN_DELAY = TimeSpan.FromSeconds(3);
    // A lender goes back once shown the partner's egg; this is how long that may take before they go back anyway.
    private static readonly TimeSpan LEND_RETURN_WAIT = TimeSpan.FromSeconds(20);
    // Long enough to read the lender's pop-up; closing the hatch window leaves sooner.
    // The game does not tell the server when OK is clicked on the pop-up, so the lender leaves soon after it shows.
    private static readonly TimeSpan LEND_POPUP_RETURN_DELAY = TimeSpan.FromSeconds(1);
    private const float SOLO_SIGIL_COUNTDOWN = 1.0f;
    // From the in-person hatch window: the window shows "get ready to port" over a dark screen, then the trip.
    private const float HATCH_WINDOW_DELAY = 1.0f;
    // A trip without a sigil is never cancelled for walking off: the player is where they stood.
    private const float NO_SIGIL_RADIUS = 1_000_000.0f;
    private PetMorphVisit _pendingTrip;

    // A lone countdown was replaced by the pair's; the next trip restarts it instead of starting a second one.
    private bool _restartCountdown;
    private DateTime _pendingTripStarted;
    // Longer than the countdown and the zone load; a trip older than this is not still under way.
    private static readonly TimeSpan PENDING_TRIP_MAX = TimeSpan.FromSeconds(30);
    private PetMorphVisit _morph;
    private bool _morphClientReady;
    private int _morphInitTries;
    private bool _morphPeerSeen;
    // The "Hatch or Lend Pet" screen of a pair: 0 not chosen yet, 1 Hatch, 2 Lend (this player's and the partner's).
    private const int CHOICE_HATCH = 1;
    private const int CHOICE_LEND = 2;
    private int _myChoice;
    private int _peerChoice;
    private bool _morphLeaving;
    private ByteString _morphOwnData;
    private ByteString _morphPeerData;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new HatchService(parentActor));

    #region In person: the game

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETHATCHCREATE))]
    private void ReceivePetHatchCreate(WIZARD_12_PROTOCOL.MSG_PETHATCHCREATE message) {
        Logger.Information("Hatching: game sent MSG_PETHATCHCREATE TargetGID={0}.", Logger.Args(message.TargetGID));
        try {
            StartInPerson(message.TargetGID);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_PETHATCHCREATE failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS))]
    private void ReceivePetHatchJoinStatus(WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS message) {
        Logger.Information("Hatching: game sent MSG_PETHATCHJOINSTATUS TargetGID={0} TargetStatus={1} PlayerStatus={2}.",
            Logger.Args(message.TargetGID, message.TargetStatus, message.PlayerStatus));
        try {
            ChangeJoin(message.TargetGID, message.PlayerStatus);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_PETHATCHJOINSTATUS failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETHATCHREADYSTATUS))]
    private void ReceivePetHatchReadyStatus(WIZARD_12_PROTOCOL.MSG_PETHATCHREADYSTATUS message) {
        Logger.Information("Hatching: game sent MSG_PETHATCHREADYSTATUS TargetGID={0} TargetStatus={1} PlayerStatus={2}.",
            Logger.Args(message.TargetGID, message.TargetStatus, message.PlayerStatus));
        try {
            ChangeReady(message.TargetGID, message.PlayerStatus);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_PETHATCHREADYSTATUS failed. {0}", Logger.Args(ex));
        }
    }

    // Only listed as sent by the server; handled in case the game sends them too.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETHATCHREQUEST))]
    private void ReceivePetHatchRequest(WIZARD_12_PROTOCOL.MSG_PETHATCHREQUEST message)
        => Logger.Information("Hatching: game sent MSG_PETHATCHREQUEST TargetGID={0}. Not used.", Logger.Args(message.TargetGID));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT))]
    private void ReceivePetHatchResult(WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT message)
        => Logger.Information("Hatching: game sent MSG_PETHATCHRESULT TargetGID={0} Status={1}. Not used.",
            Logger.Args(message.TargetGID, message.Status));

    /// <summary>
    /// The player picked another of their pets in the hatch window, if that is how the game says it.
    /// </summary>
    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETMORPHSET))]
    private void ReceivePetMorphSet(PET_9_PROTOCOL.MSG_PETMORPHSET message) {
        Logger.Information("Hatching: game sent MSG_PETMORPHSET PetID={0}.", Logger.Args(message.PetID));
        try {
            if (_soloSigilGid != 0 && _peerCharId == 0) {
                PickSoloPet(message.PetID);

                return;
            }

            ChoosePet(message.PetID);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_PETMORPHSET failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETMORPHREADY))]
    private void ReceivePetMorphReady(PET_9_PROTOCOL.MSG_PETMORPHREADY message) {
        Logger.Information("Hatching: game sent MSG_PETMORPHREADY Confirmed={0}.", Logger.Args(message.Confirmed));
        try {
            if (_soloSigilGid != 0 && _peerCharId == 0) {
                if (message.Confirmed > 0) {
                    SoloHatch();
                }

                return;
            }

            if (_peerCharId != 0) {
                ChangeReady(Wizard.GetGameObjectId(_peerCharId), message.Confirmed);
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_PETMORPHREADY failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_INPERSONHATCHINGCHECK))]
    private void ReceiveInPersonHatchingCheck(WIZARD3_56_PROTOCOL.MSG_INPERSONHATCHINGCHECK message) {
        Logger.Information("Hatching: game sent MSG_INPERSONHATCHINGCHECK Status={0} OwnerGID={1} PetGID={2} PlayerGID={3} PackedName={4} PetName={5}.",
            Logger.Args(message.Status, message.OwnerGID, message.PetGID, message.PlayerGID, Hex(message.PackedName), message.PetName));
        try {
            // A guess: the game asks whether the pet's owner allows in-person hatching, and the answer is the same
            // message with Status 1 (allowed) or 0.
            var allowed = Wizard.TryGetCharacterId(message.OwnerGID, out var ownerCharId)
                && TryGetOnlinePlayer(ownerCharId, out _)
                && WizardCollection.GetCharacterUnloaded(ownerCharId)?.PrivacyOptions?.AllowHatchRequest != 0;
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_INPERSONHATCHINGCHECK {
                Status = (byte) (allowed && PetHatching.Enabled ? 1 : 0),
                OwnerGID = message.OwnerGID,
                PetGID = message.PetGID,
                PlayerGID = message.PlayerGID,
                PackedName = message.PackedName,
                PetName = message.PetName,
            });
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_INPERSONHATCHINGCHECK failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTINPERSONHATCHINGCHANGE))]
    private void ReceiveRequestInPersonHatchingChange(WIZARD3_56_PROTOCOL.MSG_REQUESTINPERSONHATCHINGCHANGE message) {
        Logger.Information("Hatching: game sent MSG_REQUESTINPERSONHATCHINGCHANGE Status={0}. Echoed back.", Logger.Args(message.Status));
        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTINPERSONHATCHINGCHANGE { Status = message.Status });
    }

    #endregion

    #region In person: the partner's session

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHPEER))]
    private void ReceiveHatchPeer(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        try {
            OnPeer(message);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: partner message {0} from {1} failed. {2}", Logger.Args(message.Kind, message.FromCharId, ex));
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHINVITETIMEOUT))]
    private void ReceiveInviteTimeout(CHARACTER_103_PROTOCOL.MSG_HATCHINVITETIMEOUT message) {
        if (_peerCharId != message.PeerCharId || _peerJoin == JOIN_JOINED) {
            return;
        }

        Logger.Information("Hatching: {0} did not answer the hatch request in time.", Logger.Args(_peerCharId));
        InformGameClient("Your hatch request was not answered.");
        End(tellPeer: true, peerJoinShown: JOIN_LEFT);
    }

    #endregion

    #region In person: steps

    private void StartInPerson(ulong targetGid) {
        var wizard = GetActiveWizard();
        if (wizard is null || !Wizard.TryGetCharacterId(targetGid, out var targetCharId) || targetCharId == 0 || targetCharId == wizard.CharId) {
            return;
        }

        if (!PetHatching.Enabled) {
            Refuse(targetGid, HatchStatus.CannotHatchNow, "Hatching is turned off on this server.");

            return;
        }

        if (_peerCharId != 0 && _peerCharId != targetCharId) {
            Refuse(targetGid, HatchStatus.AlreadyHatching, "You are already hatching with someone.");

            return;
        }

        if (wizard.IsInDuel) {
            Refuse(targetGid, HatchStatus.CannotHatchNow, "You cannot hatch during a battle.");

            return;
        }

        if (!TryGetOnlinePlayer(targetCharId, out var target)) {
            Refuse(targetGid, HatchStatus.TargetNotFound, "That player is not online.");

            return;
        }

        if (target.CurrentZone != wizard.Zone) {
            Refuse(targetGid, HatchStatus.TargetNotInZone, "That player is not in this area.");

            return;
        }

        var pet = MyPet(wizard);
        var problem = PetHatching.ParentProblem(pet);
        if (problem is not null) {
            Refuse(targetGid, HatchStatus.Failed, pet is null ? "Wear the pet you want to hatch with first." : problem);

            return;
        }

        _peerCharId = targetCharId;
        _peerSession = null;
        _peerPath = target.ActorPath;
        _isRequester = true;
        _myJoin = JOIN_JOINED;
        _peerJoin = JOIN_PENDING;
        _myReady = 0;
        _peerReady = 0;
        _myPetId = pet.ItemId;
        _peerPet = null;
        _bred = false;

        Logger.Information("Hatching: {0} asks {1} to hatch, with {2}.", Logger.Args(wizard.CharId, targetCharId, PetHatching.Describe(pet)));
        TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Invite, pet: pet));
        Timers.StartSingleTimer(INVITE_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHINVITETIMEOUT { PeerCharId = targetCharId },
            TimeSpan.FromSeconds(INVITE_TIMEOUT_SECONDS));
    }

    private void ChangeJoin(ulong targetGid, int status) {
        if (!IsPeer(targetGid)) {
            Logger.Information("Hatching: join status for {0}, who this player is not hatching with; ignored.", Logger.Args(targetGid));

            return;
        }

        if (status != JOIN_JOINED) {
            Logger.Information("Hatching: {0} left or declined the hatch with {1} (status {2}).",
                Logger.Args(GetActiveWizard()?.CharId, _peerCharId, status));
            _myJoin = JOIN_LEFT;
            SendJoinStatus();
            End(tellPeer: true, peerJoinShown: null);

            return;
        }

        var wizard = GetActiveWizard();
        var pet = MyPet(wizard);
        var problem = PetHatching.ParentProblem(pet);
        if (problem is not null) {
            InformGameClient(pet is null ? "Wear the pet you want to hatch with first." : problem);
            _myJoin = JOIN_LEFT;
            SendJoinStatus();
            End(tellPeer: true, peerJoinShown: null);

            return;
        }

        _myJoin = JOIN_JOINED;
        _myPetId = pet.ItemId;
        if (!_isRequester) {
            Timers.Cancel(INVITE_TIMER);
        }

        TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Join, _myJoin, pet));
        SendJoinStatus();
        if (_peerJoin == JOIN_JOINED) {
            ShowPartnerPet();
        }
    }

    private void ChangeReady(ulong targetGid, int status) {
        if (!IsPeer(targetGid)) {
            Logger.Information("Hatching: ready status for {0}, who this player is not hatching with; ignored.", Logger.Args(targetGid));

            return;
        }

        if (_myJoin != JOIN_JOINED || _peerJoin != JOIN_JOINED) {
            SendResult(HatchStatus.NotJoined);

            return;
        }

        _myReady = status;
        TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Ready, status));
        SendReadyStatus();
        BreedIfBothReady();
    }

    private void ChoosePet(ulong petId) {
        if (_peerCharId == 0) {
            return;
        }

        var wizard = GetActiveWizard();
        var pet = PetHatching.Snapshot(PetCare.FindPet(wizard, petId), wizard.CharId);
        var problem = PetHatching.ParentProblem(pet);
        if (problem is not null) {
            InformGameClient(problem);

            return;
        }

        // A new pet takes back both ready marks, like a changed trade offer.
        _myPetId = pet.ItemId;
        _myReady = 0;
        _peerReady = 0;
        TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.PetChosen, pet: pet));
        SendReadyStatus();
    }

    private void OnPeer(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        Logger.Information("Hatching: partner {0} says {1} (value {2}, pet {3}).",
            Logger.Args(message.FromCharId, message.Kind, message.Value, PetHatching.Describe(message.Pet)));

        if (message.Kind == CHARACTER_103_PROTOCOL.HatchPeerKind.Invite) {
            ScreenInvite(message);

            return;
        }

        // The pairing is let go once the egg is made, so this is checked against the hatching room's partner.
        if (message.Kind == CHARACTER_103_PROTOCOL.HatchPeerKind.EggMade) {
            OnPartnerEggMade(message);

            return;
        }

        // The pair's countdown runs before the pairing is set up, so this is checked against the trip's partner.
        if (message.Kind == CHARACTER_103_PROTOCOL.HatchPeerKind.TripCancelled) {
            OnPartnerTripCancelled(message);

            return;
        }

        if (message.FromCharId != _peerCharId || _peerCharId == 0) {
            return;
        }

        _peerSession ??= message.FromSession;
        var peerGid = Wizard.GetGameObjectId(_peerCharId);
        switch (message.Kind) {
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Refused:
                Timers.Cancel(INVITE_TIMER);
                SendResult((HatchStatus) message.Value);
                InformGameClient(message.Value == (int) HatchStatus.NotAccepting
                    ? "That player is not accepting hatch requests."
                    : "That player cannot hatch right now.");
                Reset();
                break;
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Join:
                _peerJoin = message.Value;
                _peerPet = message.Pet ?? _peerPet;
                if (_peerJoin != JOIN_JOINED) {
                    SendJoinStatus();
                    End(tellPeer: false, peerJoinShown: null);

                    return;
                }

                Timers.Cancel(INVITE_TIMER);
                SendJoinStatus();
                if (_morph is not null) {
                    OnMorphPeerJoin(message);
                }

                if (_myJoin == JOIN_JOINED) {
                    ShowPartnerPet();
                }

                break;
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Ready:
                _peerReady = message.Value;
                SendReadyStatus();
                // A guess: the pet window's "partner changed readiness".
                SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHREADY { Confirmed = (sbyte) Math.Clamp(message.Value, sbyte.MinValue, sbyte.MaxValue) });
                BreedIfBothReady();
                break;
            case CHARACTER_103_PROTOCOL.HatchPeerKind.PetChosen:
                _peerPet = message.Pet ?? _peerPet;
                _myReady = 0;
                _peerReady = 0;
                SendReadyStatus();
                ShowPartnerPet();
                break;
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Choice:
                OnPeerChoice(message.Value);
                break;
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Left:
                Timers.Cancel(INVITE_TIMER);
                _peerJoin = JOIN_LEFT;
                if (_morph is null) {
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS {
                        TargetGID = peerGid,
                        TargetStatus = JOIN_LEFT,
                        // The asked player's request tip is dismissed with -1, as for trades.
                        PlayerStatus = _isRequester || _myJoin == JOIN_JOINED ? _myJoin : JOIN_PENDING,
                    });
                }

                if (_morph is not null) {
                    // In the hatching room the other player is gone (left or lost connection), so this one is sent
                    // back shortly (Niduus, 2026-10-21). A pop-up, like the lend one: the room hides plain notices.
                    InformGameClient($"{_morph.PartnerName ?? "Your partner"} has left.", isImportant: true);
                }
                else if (!_bred) {
                    InformGameClient("The hatch was cancelled.");
                }

                Reset();
                if (_morph is not null && !_bred) {
                    ScheduleMorphReturn("the partner left", PARTNER_LEFT_RETURN_DELAY);
                }

                break;
        }
    }

    private void ScreenInvite(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        var wizard = GetActiveWizard();
        var refusal = wizard is null ? HatchStatus.Failed
            : !PetHatching.Enabled ? HatchStatus.CannotHatchNow
            : wizard.PrivacyOptions.AllowHatchRequest == 0 ? HatchStatus.NotAccepting
            : BuddyRelationshipCollection.HasBlocked(wizard.CharId, message.FromCharId) ? HatchStatus.NotAccepting
            : _peerCharId != 0 && _peerCharId != message.FromCharId ? HatchStatus.AlreadyHatching
            : wizard.IsInDuel ? HatchStatus.CannotHatchNow
            : message.FromZone is not null && !message.FromZone.Equals(SessionActor.GetZoneActor()) ? HatchStatus.TargetNotInZone
            : HatchStatus.Success;

        if (refusal != HatchStatus.Success) {
            Logger.Information("Hatching: {0} refused the hatch request of {1}: {2}.", Logger.Args(wizard?.CharId, message.FromCharId, refusal));
            message.FromSession?.Tell(new CHARACTER_103_PROTOCOL.MSG_HATCHPEER {
                Kind = CHARACTER_103_PROTOCOL.HatchPeerKind.Refused,
                FromCharId = wizard?.CharId ?? 0,
                FromSession = SessionActor.ActorRef,
                Value = (int) refusal,
            });

            return;
        }

        _peerCharId = message.FromCharId;
        _peerSession = message.FromSession;
        _peerPath = null;
        _isRequester = false;
        _myJoin = JOIN_PENDING;
        _peerJoin = JOIN_JOINED;
        _myReady = 0;
        _peerReady = 0;
        _myPetId = 0;
        _peerPet = message.Pet;
        _bred = false;

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHREQUEST { TargetGID = Wizard.GetGameObjectId(message.FromCharId) });
    }

    private void BreedIfBothReady() {
        var needed = PetHatching.ReadyToHatch;
        if (_bred || _myJoin != JOIN_JOINED || _peerJoin != JOIN_JOINED || _myReady < needed || _peerReady < needed) {
            return;
        }

        var wizard = GetActiveWizard();

        // The in-person window does not make the egg: once both are ready the game waits to be sent to the hatching
        // room ("get ready to port") and the egg is made there, in the hatch window (Niduus, 2026-10-21: both
        // accepted and were stuck). Each side sends itself; the requester's instance is the shared one.
        if (_morph is null && TryHatchWindowTrip(wizard)) {
            return;
        }

        // A lender gets no egg; the partner hatches with the lent pet (Niduus, 2026-10-21).
        if (_morph is not null && _myChoice == CHOICE_LEND) {
            _bred = true;
            Logger.Information("Hatching zone: {0} lent their pet to {1}; no egg on this side.", Logger.Args(wizard?.CharId, _peerCharId));
            InformGameClient("You lent your pet. Your partner's egg is being made.");
            Reset();
            // The lender goes back once the partner's egg is made (EggMade; Niduus, 2026-10-21: the lender had no way
            // out). This is in case that never comes.
            ScheduleMorphReturn("the pet was lent and the partner's egg did not come", LEND_RETURN_WAIT);

            return;
        }

        var pet = PetHatching.Snapshot(PetCare.FindPet(wizard, _myPetId), wizard.CharId);
        if (pet is null || _peerPet is null) {
            Logger.Warning("Hatching: both ready, but a pet is missing (mine {0}, partner's {1}).",
                Logger.Args(_myPetId, _peerPet?.ItemId));
            SendResult(HatchStatus.Failed);

            return;
        }

        // Each side makes its own egg: this player's pet with the partner's.
        _bred = true;
        TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BREEDPETS {
            ParentA = pet,
            ParentB = _peerPet,
            Source = _morph is not null ? HatchSource.Sigil : HatchSource.InPerson,
            PartnerGameObjectId = Wizard.GetGameObjectId(_peerCharId),
        });

        // The window closes with the result; both sides end on their own.
        Reset();
    }

    private bool TryHatchWindowTrip(Wizard wizard) {
        if (wizard is null || !PetHatching.SigilWindow || !PetMorphZone.IsAvailable(out _)) {
            return false;
        }

        var peer = _peerCharId;
        var role = _isRequester ? CHARACTER_103_PROTOCOL.HatchSigilRole.Requester : CHARACTER_103_PROTOCOL.HatchSigilRole.Partner;
        var peerName = WizardCollection.GetCharacterUnloaded(peer)?.PlayerNameBehavior?.GetWizardName() ?? "your partner";
        var here = Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation);
        Logger.Information("Hatching: {0} and {1} are both ready in the hatch window; going to the hatching room as {2}.",
            Logger.Args(wizard.CharId, peer, role));

        // The trip starts a fresh pair in the hatching room, so the window's pairing is let go first.
        _bred = true;
        Reset();
        _bred = false;
        var started = TryStartTrip(new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
            Role = role,
            SigilGID = 0,
            SigilName = "hatch window",
            PartnerCharId = peer,
            PartnerGameObjectId = Wizard.GetGameObjectId(peer),
            PartnerName = peerName,
            SigilLoc = here,
            SigilType = "",
            Radius = NO_SIGIL_RADIUS,
            ZoneName = wizard.Zone,
            FaceSlot = 0,
            FromHatchWindow = true,
        }, pair: true);
        return started;
    }

    private void ShowPartnerPet() {
        if (_peerPet is null) {
            return;
        }

        // Guesses: "partner has chosen a different pet" and whether this player can pay.
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHSET { PetID = _peerPet.ItemId });
        var wizard = GetActiveWizard();
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD {
            CanAfford = (sbyte) (wizard is not null && wizard.GameStats.m_currentGold >= PetHatching.GoldCost ? 1 : 0)
        });
    }

    private void End(bool tellPeer, int? peerJoinShown) {
        Timers.Cancel(INVITE_TIMER);
        if (tellPeer) {
            TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Left));
        }

        if (peerJoinShown is not null) {
            _peerJoin = peerJoinShown.Value;
            SendJoinStatus();
        }

        Reset();
    }

    private void Reset() {
        _peerCharId = 0;
        _peerSession = null;
        _peerPath = null;
        _isRequester = false;
        _myJoin = 0;
        _peerJoin = 0;
        _myReady = 0;
        _peerReady = 0;
        _myPetId = 0;
        _peerPet = null;
    }

    #endregion

    #region Hatching sigils

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL))]
    private void ReceiveHatchSigil(CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL message) {
        Logger.Information("Hatching sigil: {0} on {1} ({2}) in {3} at {4} (sigil type \"{5}\", radius {6}), partner {7} ({8}).",
            Logger.Args(message.Role, message.SigilName, message.SigilGID, message.ZoneName, message.SigilLoc, message.SigilType,
                message.Radius, message.PartnerName, message.PartnerCharId));
        try {
            switch (message.Role) {
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Solo:
                    if (!TryStartTrip(message, pair: false)) {
                        StartSolo(message.SigilGID);
                    }

                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Requester:
                    DropAloneTrip();
                    EndSolo();
                    if (TryStartTrip(message, pair: true)) {
                        break;
                    }

                    InformGameClient($"Asking {message.PartnerName} to hatch with you. Wear the pet you want to hatch with; "
                        + $"if no hatch window opens, use the solo sigils or .hatch with two of your own pets.");
                    StartInPerson(Wizard.GetGameObjectId(message.PartnerCharId));
                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Partner:
                    DropAloneTrip();
                    EndSolo();
                    if (TryStartTrip(message, pair: true)) {
                        break;
                    }

                    InformGameClient($"{message.PartnerName} wants to hatch pets with you on the sigil. Wear the pet you want "
                        + "to hatch with and accept the hatch request.");
                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.WaitingForPartner:
                    // The player hops on and counts down as if alone; if the other player presses X in time, both go
                    // together (Niduus, 2026-10-21: players should hop on the sigil regardless).
                    if (TryStartTrip(message, pair: false)) {
                        InformGameClient($"If {message.PartnerName} presses X before the countdown ends, you go together. "
                            + "If not, you hatch on your own.");
                    }
                    else {
                        StartSolo(message.SigilGID);
                    }

                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Alone:
                    // With nobody else on it, the duo sigil hatches two of the player's own pets, as live does (Niduus, 2026-10-20).
                    if (!TryStartTrip(message, pair: false)) {
                        StartSolo(message.SigilGID);
                    }

                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching sigil: {0} failed. {1}", Logger.Args(message.Role, ex));
        }
        finally {
            CancelUnusedRestart();
        }
    }

    /// <summary>
    /// A player counting down alone on a duo pad is paired when someone joins them: the lone countdown stops (without
    /// a failure, which would end the pair's trip) so the pair's starts (Niduus, 2026-10-20: one went alone, the other
    /// stayed behind).
    /// </summary>
    private void DropAloneTrip() {
        var trip = _pendingTrip;
        if (trip is null || trip.IsPair || _morph is not null) {
            return;
        }

        Logger.Information("Hatching sigil: {0} was counting down alone on {1}; restarting it to hatch as a pair.",
            Logger.Args(trip.CharId, trip.PadName));
        _pendingTrip = null;
        PetMorphZone.End(trip.CharId);
        // The pair's countdown replaces this one on screen (no end then start, which could leave no countdown
        // showing); CancelUnusedRestart stops it if the pair's trip does not start.
        _restartCountdown = true;
    }

    private void CancelUnusedRestart() {
        if (!_restartCountdown) {
            return;
        }

        _restartCountdown = false;
        TellOtherServices(new ZONE_102_PROTOCOL.MSG_CANCELSIGILENTRY());
    }

    /// <summary>
    /// Sends the player through the sigil countdown to the pet morph zone. Returns false when the zone is not to be
    /// used (turned off, or it cannot be loaded), so the caller falls back to the chat list or the in-person hatch;
    /// true when the trip started or the player was told why not.
    /// </summary>
    private bool TryStartTrip(CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL message, bool pair) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return true;
        }

        // A second X press during the countdown must not replace the trip under way: its "busy" failure used to end
        // that trip, so the player arrived with no hatch to do and was sent straight back (Niduus, 2026-10-20).
        if (_morph is not null || _pendingTrip is not null && DateTime.UtcNow - _pendingTripStarted < PENDING_TRIP_MAX) {
            Logger.Information("Hatching sigil: {0} is already on the way to (or in) the hatching room; ignored.", Logger.Args(wizard.CharId));

            return true;
        }

        if (!PetHatching.SigilWindow) {
            Logger.Information("Hatching sigil: the morph zone is turned off ([Hatching] SigilWindow); no teleport.");

            return false;
        }

        if (!PetHatching.Enabled) {
            InformGameClient("Hatching is turned off on this server.");

            return true;
        }

        if (_peerCharId != 0) {
            Logger.Information("Hatching sigil: {0} is already hatching with {1}.", Logger.Args(wizard.CharId, _peerCharId));
            InformGameClient("You are already hatching with someone.");

            return true;
        }

        if (wizard.IsInDuel) {
            InformGameClient("You cannot hatch during a battle.");

            return true;
        }

        var pets = PetHatching.OwnPets(wizard);
        Logger.Information("Hatching sigil: {0} has {1} hatched pets: {2}.",
            Logger.Args(wizard.CharId, pets.Count, string.Join(", ", pets.Select(p => $"{PetHatching.Label(p)} ({p.m_globalID.Full})"))));
        if (!pair && pets.Count < 2) {
            InformGameClient("You need two hatched pets to hatch on your own. Hatch an egg or get another pet, then come back.");

            return true;
        }

        if (pair && pets.Count < 1) {
            InformGameClient($"You need a hatched pet to hatch with {message.PartnerName}.");

            return true;
        }

        if (!PetMorphZone.IsAvailable(out var why)) {
            Logger.Warning("Hatching sigil: cannot send {0} to the morph zone: {1}. Using the chat list instead.", Logger.Args(wizard.CharId, why));

            return false;
        }

        // A pair shares the instance of the player who pressed the sigil, who is the partner's partner.
        var isPartner = message.Role == CHARACTER_103_PROTOCOL.HatchSigilRole.Partner;
        var owner = isPartner ? message.PartnerCharId : wizard.CharId;
        var visit = new PetMorphVisit(
            wizard.CharId,
            message.SigilGID,
            message.SigilName,
            string.IsNullOrEmpty(message.ZoneName) ? PetMorphZone.HATCHERY_ZONE : message.ZoneName,
            TripReturnLocation(wizard, message.SigilLoc),
            owner,
            pair ? message.PartnerCharId : 0,
            pair ? message.PartnerName : null);
        PetMorphZone.Begin(visit);
        _pendingTrip = visit;
        _pendingTripStarted = DateTime.UtcNow;

        // The chat list goes first, so `.hatch` is there whatever the window does.
        if (message.FromHatchWindow) {
            InformGameClient($"Going to the hatching room with {message.PartnerName}.");
        }
        else if (pair) {
            InformGameClient($"Stay on the sigil to go to the hatching room with {message.PartnerName}.");
        }
        else {
            InformGameClient("Stay on the sigil to go to the hatching room. If no hatch window opens there, type .hatch <pet 1> <pet 2> "
                + "with numbers from this list: " + string.Join("; ", pets.Select((p, i) => $"{i + 1}) {PetHatching.Label(p)}")));
        }

        Logger.Information("Hatching sigil: {0} counts down on {1} to {2} (instance owner {3}, {4}, face {5}).",
            Logger.Args(wizard.CharId, message.SigilName, PetMorphZone.ZONE, owner,
                pair ? $"with {message.PartnerName} ({message.PartnerCharId})" : "alone", message.FaceSlot));
        TellOtherServices(new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilLoc = message.SigilLoc,
            SigilGID = message.SigilGID,
            SigilType = message.SigilType ?? "",
            // The pad's own radius, so the countdown and the pad agree on who is on it (Niduus, 2026-10-21).
            Radius = message.Radius > 0 ? message.Radius : MORPH_TRIP_MIN_RADIUS,
            DestinationZone = PetMorphZone.ZONE,
            DestinationLoc = PetMorphZone.LOCATION,
            InstanceOwnerCharId = owner,
            KeepInstance = isPartner,
            FaceSlot = message.FaceSlot,
            // Sliding onto the circle took players off the small solo pads (Niduus, 2026-10-20: "drags you").
            NoSnap = message.Role == CHARACTER_103_PROTOCOL.HatchSigilRole.Solo,
            SnapToCentre = message.Role == CHARACTER_103_PROTOCOL.HatchSigilRole.Solo,
            // The one-player sigil goes after 1 s (Niduus, 2026-10-21).
            CountdownSeconds = message.FromHatchWindow ? HATCH_WINDOW_DELAY
                : message.Role == CHARACTER_103_PROTOCOL.HatchSigilRole.Solo ? SOLO_SIGIL_COUNTDOWN
                : message.CountdownSeconds,
            NoSigil = message.FromHatchWindow,
            // One countdown on the pad: the requester's start shows it to the whole zone, and a player hopping back on
            // joins the one already shown. Each extra start redrew the numbers, which flashed (Niduus, 2026-10-21).
            NoTimerBroadcast = message.KeepCountdown || message.Role == CHARACTER_103_PROTOCOL.HatchSigilRole.Partner,
            // The pad's countdown is reset only once, when the second player first joins; after that a pair forming
            // again keeps the countdown already running (Niduus, 2026-10-21).
            Restart = _restartCountdown && !message.KeepCountdown,
            ContinueCountdown = _restartCountdown && message.KeepCountdown,
            OnFailed = new CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED(),
            LogName = "Hatching sigil",
        });
        _restartCountdown = false;

        return true;
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED))]
    private void ReceiveHatchTripFailed(CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED message) {
        // Busy: another countdown or transfer is running, which is the trip that is still under way.
        if (message.Failure == CHARACTER_103_PROTOCOL.HatchTripFailure.Busy) {
            Logger.Information("Hatching sigil: a second trip was refused while one is running ({0}); the first goes on.", Logger.Args(message.Detail));

            return;
        }

        var trip = _pendingTrip;
        _pendingTrip = null;
        var wizard = GetActiveWizard();
        Logger.Information("Hatching sigil: the trip of {0} to the morph zone did not happen: {1} ({2}).",
            Logger.Args(wizard?.CharId, message.Failure, message.Detail));
        if (wizard is null) {
            return;
        }

        PetMorphZone.End(wizard.CharId);
        try {
            switch (message.Failure) {
                case CHARACTER_103_PROTOCOL.HatchTripFailure.Cancelled:
                    InformGameClient("You moved, so your sigil countdown stopped. Press X on the sigil to hop back on.");
                    if (trip is { IsPair: true } && TryGetOnlinePlayer(trip.PartnerCharId, out var partner)) {
                        Context.ActorSelection(partner.ActorPath).Tell(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.TripCancelled));
                    }

                    break;
                case CHARACTER_103_PROTOCOL.HatchTripFailure.TransferFailed when trip is { IsPair: false }:
                    InformGameClient("The hatching room could not be opened.");
                    StartSolo(trip.SigilGid);
                    break;
                case CHARACTER_103_PROTOCOL.HatchTripFailure.TransferFailed when trip is not null && trip.InstanceOwnerCharId == wizard.CharId:
                    InformGameClient($"The hatching room could not be opened; asking {trip.PartnerName} to hatch in person instead.");
                    StartInPerson(Wizard.GetGameObjectId(trip.PartnerCharId));
                    break;
                default:
                    InformGameClient("The hatching room could not be opened. Try again in a moment.");
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching sigil: the fallback after the failed trip failed. {0}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// Lists the player's pets in chat for `.hatch`: the way that is known to work, kept for when the morph zone is
    /// turned off or cannot be loaded.
    /// </summary>
    private void StartSolo(ulong sigilGid) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        if (!PetHatching.Enabled) {
            InformGameClient("Hatching is turned off on this server.");

            return;
        }

        if (_peerCharId != 0) {
            Logger.Information("Hatching sigil: {0} is already hatching with {1}.", Logger.Args(wizard.CharId, _peerCharId));
            InformGameClient("You are already hatching with someone.");

            return;
        }

        if (wizard.IsInDuel) {
            InformGameClient("You cannot hatch during a battle.");

            return;
        }

        var pets = PetHatching.OwnPets(wizard);
        Logger.Information("Hatching sigil: chat list for {0}, who has {1} hatched pets: {2}.",
            Logger.Args(wizard.CharId, pets.Count, string.Join(", ", pets.Select(p => $"{PetHatching.Label(p)} ({p.m_globalID.Full})"))));
        if (pets.Count < 2) {
            InformGameClient("You need two hatched pets to hatch on your own. Hatch an egg or get another pet, then come back.");

            return;
        }

        _soloSigilGid = sigilGid;
        _soloPicks.Clear();

        InformGameClient("Pick two of your pets to hatch: type .hatch <pet 1> <pet 2> with numbers from "
            + "this list: " + string.Join("; ", pets.Select((p, i) => $"{i + 1}) {PetHatching.Label(p)}")));
    }

    private void PickSoloPet(ulong petId) {
        var wizard = GetActiveWizard();
        var pet = PetHatching.Snapshot(PetCare.FindPet(wizard, petId), wizard.CharId);
        var problem = PetHatching.ParentProblem(pet);
        if (problem is not null) {
            Logger.Information("Hatching sigil: {0} picked {1}, which cannot hatch: {2}", Logger.Args(wizard.CharId, petId, problem));
            InformGameClient(problem);

            return;
        }

        if (!_soloPicks.Contains(pet.ItemId)) {
            _soloPicks.Add(pet.ItemId);
            if (_soloPicks.Count > 2) {
                _soloPicks.RemoveAt(0);
            }
        }

        Logger.Information("Hatching sigil: {0} picked {1}; picks now {2}.",
            Logger.Args(wizard.CharId, PetHatching.Describe(pet), string.Join(", ", _soloPicks)));
    }

    private void SoloHatch() {
        var wizard = GetActiveWizard();
        if (_soloPicks.Count < 2) {
            Logger.Information("Hatching sigil: {0} is ready with {1} pets picked; two are needed.", Logger.Args(wizard?.CharId, _soloPicks.Count));
            InformGameClient("Pick two different pets first.");

            return;
        }

        var parentA = PetHatching.Snapshot(PetCare.FindPet(wizard, _soloPicks[0]), wizard.CharId);
        var parentB = PetHatching.Snapshot(PetCare.FindPet(wizard, _soloPicks[1]), wizard.CharId);
        var problem = parentA is null || parentB is null ? "One of the picked pets is no longer in your backpack." : PetHatching.PairProblem(parentA, parentB);
        if (problem is not null) {
            Logger.Information("Hatching sigil: solo hatch of {0} refused: {1}", Logger.Args(string.Join(", ", _soloPicks), problem));
            InformGameClient(problem);

            return;
        }

        Logger.Information("Hatching sigil: {0} hatches {1} with {2}.",
            Logger.Args(wizard.CharId, PetHatching.Describe(parentA), PetHatching.Describe(parentB)));
        TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BREEDPETS {
            ParentA = parentA,
            ParentB = parentB,
            Source = _morph is not null ? HatchSource.Sigil : HatchSource.Command,
        });
        EndSolo();
    }

    private void EndSolo() {
        _soloSigilGid = 0;
        _soloPicks.Clear();
    }

    private static bool IsMorphGame(ByteString game)
        => string.Equals(game.ToString(), PetMorphZone.GAME, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Hatching zone

    // Before the game is told it is in the zone, so the log has every message it sends while loading the zone too.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PRELOGIN))]
    private void ReceivePreLogin(ZONE_102_PROTOCOL.MSG_PRELOGIN message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is not null && PetMorphZone.IsMorphZone(wizard.Zone)) {
                SessionActor.ClientMessageLogPrefix = "Hatching zone";
                Logger.Information("Hatching zone: {0} is attaching to {1}; logging every message the game sends.",
                    Logger.Args(wizard.CharId, wizard.Zone));
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching zone: reading the zone at login failed. {0}", Logger.Args(ex));
        }
    }

    // Every arrival in a zone: in the pet morph zone the hatch window is started; anywhere else a trip is over.
    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            var hasVisit = PetMorphZone.TryGet(wizard.CharId, out var visit);
            if (!PetMorphZone.IsMorphZone(wizard.Zone)) {

                if (hasVisit) {
                    Logger.Information("Hatching zone: {0} arrived in {1}, not the morph zone; the trip from {2} is over.",
                        Logger.Args(wizard.CharId, wizard.Zone, visit.PadName));
                    PetMorphZone.End(wizard.CharId);
                }

                return;
            }

            SessionActor.ClientMessageLogPrefix = "Hatching zone";
            if (!hasVisit) {
                // A reattach, or a teleport here by hand: nothing to hatch, so back to the hatchery.
                Logger.Information("Hatching zone: {0} ({1}) arrived in {2} with no hatch to do; sending them to the hatchery.",
                    Logger.Args(Name(wizard), wizard.CharId, wizard.Zone));
                _morph = new PetMorphVisit(wizard.CharId, 0, "none", PetMorphZone.HATCHERY_ZONE, PetMorphZone.LOCATION, wizard.CharId, 0, null);
                ScheduleMorphReturn("no hatch to do", MORPH_RETURN_DELAY);

                return;
            }

            ArriveInMorphZone(wizard, visit);
        }
        catch (Exception ex) {
            Logger.Error("Hatching zone: handling the arrival failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER))]
    private void ReceiveHatchZoneTimer(CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER message) {
        if (_morph is null) {
            return;
        }

        try {
            switch (message.Kind) {
                case CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Init:
                    SendMorphInit();
                    break;
                case CHARACTER_103_PROTOCOL.HatchZoneTimerKind.PartnerWait:
                    HatchAloneWithoutPartner();
                    break;
                case CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Timeout:
                    Logger.Information("Hatching zone: {0} has been in the morph zone for {1}; time is up.",
                        Logger.Args(_morph.CharId, PetMorphZone.Timeout));
                    InformGameClient("Time is up in the hatching room.");
                    ReturnFromMorphZone("time is up");
                    break;
                case CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Return:
                    ReturnFromMorphZone(message.Reason);
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching zone: timer {0} failed. {1}", Logger.Args(message.Kind, ex));
        }
    }

    /// <summary>
    /// The morph window, if it is a pet game like the others, may join and say it is ready.
    /// </summary>
    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN))]
    private void ReceivePetGameJoin(PET_9_PROTOCOL.MSG_PETGAMEJOIN message) {
        if (!IsMorphGame(message.Game)) {
            return;
        }

        var success = _morph is not null || _soloSigilGid != 0;
        Logger.Information("Hatching zone: the game sent MSG_PETGAMEJOIN Game={0} Track={1} (in the morph zone {2}); "
            + "answering MSG_PETGAMEJOINRSP Success={3}.", Logger.Args(message.Game, message.Track, _morph is not null, success ? 1 : 0));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = PetMorphZone.GAME, Success = (byte) (success ? 1 : 0) });
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY))]
    private void ReceivePetGameReady(PET_9_PROTOCOL.MSG_PETGAMEREADY message) {
        Logger.Information("Hatching zone: the game sent MSG_PETGAMEREADY (in the morph zone {0}).", Logger.Args(_morph is not null));
        if (_morph is null) {
            return;
        }

        _morphClientReady = true;
        Timers.Cancel(MORPH_INIT_TIMER);
        SendMorphStart();
    }

    // The hatch window's picks and its Hatch button, as text (Niduus's log, 2026-10-20):
    // "set:side=0;id=<pet item id>" when a side's pet changes, "ready:confirmed=1" on Hatch.
    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEDATA))]
    private void ReceivePetGameData(PET_9_PROTOCOL.MSG_PETGAMEDATA message) {
        if (_morph is null || !IsMorphGame(message.Game)) {
            return;
        }

        try {
            var text = message.Data.ToString() ?? "";
            var colon = text.IndexOf(':');
            var verb = colon < 0 ? text : text[..colon];
            var fields = (colon < 0 ? "" : text[(colon + 1)..])
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Split('=', 2))
                .Where(f => f.Length == 2)
                .ToDictionary(f => f[0].Trim(), f => f[1].Trim(), StringComparer.OrdinalIgnoreCase);

            switch (verb.Trim().ToLowerInvariant()) {
                case "set" when fields.TryGetValue("id", out var idText) && ulong.TryParse(idText, out var petId):
                    var side = fields.TryGetValue("side", out var sideText) && int.TryParse(sideText, out var n) ? n : 0;
                    if (_morph.IsPair) {
                        ChoosePet(petId);
                    }
                    else if (side is 0 or 1) {
                        _windowSides[side] = petId;
                    }

                    break;
                // "Hatch or Lend Pet": the game only asks; the screen moves on when the server answers both choices.
                case "hatch" when _morph.IsPair:
                case "lend" when _morph.IsPair:
                    ChooseHatchOrLend(verb.Trim().Equals("lend", StringComparison.OrdinalIgnoreCase) ? CHOICE_LEND : CHOICE_HATCH);
                    break;
                case "ready":
                    var confirmed = fields.TryGetValue("confirmed", out var c) && int.TryParse(c, out var v) ? v : 1;
                    if (_morph.IsPair) {
                        if (_peerCharId != 0) {
                            ChangeReady(Wizard.GetGameObjectId(_peerCharId), confirmed);
                        }

                        break;
                    }

                    if (confirmed > 0) {
                        HatchWindowSides();
                    }

                    break;
                default:
                    Logger.Information("Hatching zone: hatch window data \"{0}\" is not used.", Logger.Args(text));
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching zone: MSG_PETGAMEDATA failed. {0}", Logger.Args(ex));
        }
    }

    private void ChooseHatchOrLend(int choice) {
        _myChoice = choice;
        Logger.Information("Hatching zone: {0} chose {1} (partner {2}).",
            Logger.Args(_morph?.CharId, ChoiceName(choice), ChoiceName(_peerChoice)));
        TellPeer(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Choice, choice));
        SendChoices();
    }

    private void OnPeerChoice(int choice) {
        _peerChoice = choice;
        if (_myChoice != 0) {
            SendChoices();
        }
    }

    // What the window understands: "Hatch:Wait" while the partner has not chosen, then this player's choice and the
    // partner's: "Hatch:Hatch", "Hatch:Lend", "Lend:Hatch" or "Lend:DoubleLend" (read from the game, 2026-10-21).
    private void SendChoices() {
        string data;
        if (_peerChoice == 0) {
            data = "Hatch:Wait";
        }
        else if (_myChoice == CHOICE_LEND && _peerChoice == CHOICE_LEND) {
            data = "Lend:DoubleLend";
        }
        else {
            data = $"{ChoiceName(_myChoice)}:{ChoiceName(_peerChoice)}";
        }

        Logger.Information("Hatching zone: telling {0} \"{1}\".", Logger.Args(_morph?.CharId, data));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEDATA { Game = PetMorphZone.GAME, Data = data });

        // Two lenders: the window shows a note and goes back to the choice, so both choose again.
        if (data == "Lend:DoubleLend") {
            _myChoice = 0;
            _peerChoice = 0;
        }
    }

    private static string ChoiceName(int choice)
        => choice == CHOICE_LEND ? "Lend" : choice == CHOICE_HATCH ? "Hatch" : "none";

    // Alone, the window's two sides are both the player's own pets.
    private void HatchWindowSides() {
        if (_windowSides[0] == 0 || _windowSides[1] == 0 || _windowSides[0] == _windowSides[1]) {
            Logger.Information("Hatching zone: Hatch pressed with sides {0} and {1}.", Logger.Args(_windowSides[0], _windowSides[1]));
            InformGameClient("Pick two different pets first.");

            return;
        }

        _soloSigilGid = _morph.SigilGid;
        _soloPicks.Clear();
        _soloPicks.Add(_windowSides[0]);
        _soloPicks.Add(_windowSides[1]);
        SoloHatch();
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEEND))]
    private void ReceivePetGameEnd(PET_9_PROTOCOL.MSG_PETGAMEEND message) {
        Logger.Information("Hatching zone: the game sent MSG_PETGAMEEND Game={0} Data={1} (in the morph zone {2}).",
            Logger.Args(message.Game, Hex(message.Data), _morph is not null));
        if (_morph is not null && IsMorphGame(message.Game)) {
            ReturnFromMorphZone("the game ended the hatch window");
        }
    }

    // Listed as sent by the server; handled in case the game sends it when the window is closed.
    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEENDING))]
    private void ReceivePetGameEnding(PET_9_PROTOCOL.MSG_PETGAMEENDING message) {
        Logger.Information("Hatching zone: the game sent MSG_PETGAMEENDING Game={0} GlobalID={1} (in the morph zone {2}).",
            Logger.Args(message.Game, message.GlobalID, _morph is not null));
        // The game sends it with no fields when the player closes the hatch window (its exit buttons).
        if (_morph is not null) {
            // Sooner than the wait after the egg, which may already be running.
            Timers.Cancel(MORPH_RETURN_TIMER);
            _morphLeaving = false;
            ScheduleMorphReturn("the player closed the hatch window", MORPH_CLOSE_DELAY);
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_HATCHBRED))]
    private void ReceiveHatchBred(CHARACTER_103_PROTOCOL.MSG_HATCHBRED message) {
        if (_morph is null) {
            return;
        }

        Logger.Information("Hatching zone: the egg of {0} was {1}.", Logger.Args(_morph.CharId, message.Success ? "made" : "not made"));
        if (message.Success) {
            InformGameClient("Your egg is in your backpack. Back to the hatchery in a moment.");
            ScheduleMorphReturn("the egg is made", MORPH_RETURN_DELAY);
            TellMorphPartnerEggMade();

            return;
        }

        // Alone, the player may pick again; a pair's hatch is over on this side.
        if (_morph.IsPair) {
            ScheduleMorphReturn("the hatch failed", MORPH_RETURN_DELAY);

            return;
        }

        _soloSigilGid = _morph.SigilGid;
    }

    // A partner who lent their pet goes back too once the egg is made.
    private void TellMorphPartnerEggMade() {
        if (_morph is not { IsPair: true } || !TryGetOnlinePlayer(_morph.PartnerCharId, out var partner)) {
            return;
        }

        Context.ActorSelection(partner.ActorPath).Tell(HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.EggMade));
    }

    private void OnPartnerTripCancelled(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        var trip = _pendingTrip;
        if (trip is null || _morph is not null || trip.PartnerCharId != message.FromCharId) {
            return;
        }

        var partnerName = trip.PartnerName ?? "Your partner";
        var wizard = GetActiveWizard();
        var pets = wizard is null ? [] : PetHatching.OwnPets(wizard);
        PetMorphZone.End(trip.CharId);
        if (wizard is null || pets.Count < 2) {
            Logger.Information("Hatching sigil: {0} stepped off {1}; {2} cannot hatch alone, so their countdown stops.",
                Logger.Args(message.FromCharId, trip.PadName, trip.CharId));
            _pendingTrip = null;
            TellOtherServices(new ZONE_102_PROTOCOL.MSG_CANCELSIGILENTRY());
            InformGameClient($"{partnerName} stepped off the sigil. You need two hatched pets to hatch on your own.");

            return;
        }

        // The countdown goes on until everyone has left the pad (Niduus, 2026-10-21); this player now goes alone.
        Logger.Information("Hatching sigil: {0} stepped off {1}; {2} keeps counting down and goes alone.",
            Logger.Args(message.FromCharId, trip.PadName, trip.CharId));
        var alone = new PetMorphVisit(trip.CharId, trip.SigilGid, trip.PadName, trip.ReturnZone, trip.ReturnLocation,
            trip.CharId, 0, null);
        PetMorphZone.Begin(alone);
        _pendingTrip = alone;
        TellOtherServices(new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
            SigilGID = trip.SigilGid,
            DestinationZone = PetMorphZone.ZONE,
            DestinationLoc = PetMorphZone.LOCATION,
            InstanceOwnerCharId = trip.CharId,
            KeepInstance = false,
            ContinueCountdown = true,
            OnFailed = new CHARACTER_103_PROTOCOL.MSG_HATCHTRIPFAILED(),
            LogName = "Hatching sigil",
        });
        InformGameClient($"{partnerName} stepped off the sigil. Stay on it to hatch on your own.");
    }

    private void OnPartnerEggMade(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        if (_morph is null || message.FromCharId != _morph.PartnerCharId || _myChoice != CHOICE_LEND) {
            return;
        }

        // No egg screen: it read as the lender getting an egg (Niduus, 2026-10-21: lending should not give an egg).
        Logger.Information("Hatching zone: {0} lent their pet and {1}'s egg is made.", Logger.Args(_morph.CharId, message.FromCharId));
        var partnerName = _morph.PartnerName ?? "Your partner";
        // A pop-up as soon as the hatch is over (Niduus, 2026-10-21: the lender should get a "congratulations" notice
        // right away, not only back in the hatchery).
        InformGameClient($"Congratulations on lending your pet! {partnerName} got an egg thanks to you.", isImportant: true);
        Timers.Cancel(MORPH_RETURN_TIMER);
        _morphLeaving = false;
        _returnQuietly = true;
        ScheduleMorphReturn("the partner's egg is made", LEND_POPUP_RETURN_DELAY);
    }

    private void ArriveInMorphZone(Wizard wizard, PetMorphVisit visit) {
        _pendingTrip = null;
        _morph = visit;
        _morphClientReady = false;
        _windowSides[0] = 0;
        _windowSides[1] = 0;
        _morphInitTries = 0;
        _morphPeerSeen = false;
        _morphLeaving = false;
        _morphOwnData = default;
        _morphPeerData = default;
        _myChoice = 0;
        _peerChoice = 0;
        Logger.Information("Hatching zone: {0} ({1}) arrived from {2} ({3}), {4}, instance owner {5}, {6:0.0} s after pressing X.",
            Logger.Args(Name(wizard), wizard.CharId, visit.PadName, visit.SigilGid,
                visit.IsPair ? $"hatching with {visit.PartnerName} ({visit.PartnerCharId})" : "hatching alone",
                visit.InstanceOwnerCharId, (DateTimeOffset.UtcNow - visit.Started).TotalSeconds));

        Timers.StartSingleTimer(MORPH_TIMEOUT_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER {
            Kind = CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Timeout
        }, PetMorphZone.Timeout);
        // The game is still loading the zone when it attaches; a message sent now may be lost.
        Timers.StartSingleTimer(MORPH_INIT_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER {
            Kind = CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Init
        }, MORPH_INIT_DELAY);

        WriteOwnMorphData(wizard);
        if (!visit.IsPair) {
            _soloSigilGid = visit.SigilGid;
            _soloPicks.Clear();

            return;
        }

        // A pair hatches like in person (each side breeds its own egg), with the morph window's messages.
        var pet = PetHatching.Snapshot(PetCare.GetWornPet(wizard), wizard.CharId);
        if (PetHatching.ParentProblem(pet) is not null) {
            pet = PetHatching.OwnPets(wizard).Select(p => PetHatching.Snapshot(p, wizard.CharId)).FirstOrDefault(p => PetHatching.ParentProblem(p) is null);
        }

        _peerCharId = visit.PartnerCharId;
        _peerSession = null;
        _peerPath = TryGetOnlinePlayer(visit.PartnerCharId, out var partner) ? partner.ActorPath : null;
        _isRequester = visit.InstanceOwnerCharId == wizard.CharId;
        _myJoin = JOIN_JOINED;
        _peerJoin = JOIN_JOINED;
        _myReady = 0;
        _peerReady = 0;
        _myPetId = pet?.ItemId ?? 0;
        _peerPet = null;
        _bred = false;

        Logger.Information("Hatching zone: {0} starts with {1}; telling {2} (session {3}).",
            Logger.Args(wizard.CharId, PetHatching.Describe(pet), visit.PartnerCharId, _peerPath ?? "not here yet"));
        TellPeer(MorphJoin(pet));
        Timers.StartSingleTimer(MORPH_PARTNER_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER {
            Kind = CHARACTER_103_PROTOCOL.HatchZoneTimerKind.PartnerWait
        }, MORPH_PARTNER_WAIT);
    }

    // The partner's Join in the morph zone, answered once so whoever arrives first still learns the other's pets.
    private void OnMorphPeerJoin(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        _morphPeerData = message.MorphData;
        if (_morphPeerSeen) {
            return;
        }

        _morphPeerSeen = true;
        Timers.Cancel(MORPH_PARTNER_TIMER);
        Logger.Information("Hatching zone: {0} sees partner {1} in the morph zone ({2} bytes of partner data).",
            Logger.Args(_morph?.CharId, message.FromCharId, ((byte[]) message.MorphData)?.Length ?? 0));
        TellPeer(MorphJoin(MyPet(GetActiveWizard())));
        if (_morphClientReady) {
            SendMorphStart();
        }
    }

    private CHARACTER_103_PROTOCOL.MSG_HATCHPEER MorphJoin(PetParent pet) {
        var join = HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind.Join, JOIN_JOINED, pet);
        join.MorphData = _morphOwnData;

        return join;
    }

    private void HatchAloneWithoutPartner() {
        if (_morphPeerSeen || _morph is null) {
            return;
        }

        Logger.Information("Hatching zone: {0} did not arrive within {1} s; {2} hatches alone.",
            Logger.Args(_morph.PartnerCharId, MORPH_PARTNER_WAIT.TotalSeconds, _morph.CharId));
        Reset();
        _morph = _morph with { PartnerCharId = 0, PartnerName = null };
        var wizard = GetActiveWizard();
        if (PetHatching.OwnPets(wizard).Count < 2) {
            InformGameClient("Your partner did not come, and you need two hatched pets to hatch on your own.");
            ScheduleMorphReturn("partner did not come", MORPH_RETURN_DELAY);

            return;
        }

        InformGameClient("Your partner did not come; pick two of your own pets to hatch.");
        _soloSigilGid = _morph.SigilGid;
        _soloPicks.Clear();
    }

    private void WriteOwnMorphData(Wizard wizard) {
        var pets = PetHatching.OwnPets(wizard);
        var partner = new PetGameMorphPartner {
            m_characterId = wizard.CharId,
            m_pets = [.. pets.Cast<CoreObject>()],
        };
        if (!s_morphSerializer.Serialize(partner, MORPH_FLAGS, out var data)) {
            Logger.Error("Hatching zone: could not write the PetGameMorphPartner of {0}.", Logger.Args(wizard.CharId));
            _morphOwnData = default;

            return;
        }

        _morphOwnData = data;
        Logger.Information("Hatching zone: PetGameMorphPartner of {0}: {1} pets ({2}), {3} bytes.",
            Logger.Args(wizard.CharId, pets.Count, string.Join(", ", pets.Select(p => $"{PetHatching.Label(p)} {p.m_globalID.Full}")), data.Length));
    }

    // A guess: the game's pet game start, its data the player's own PetGameMorphPartner.
    private void SendMorphInit() {
        var wizard = GetActiveWizard();
        if (_morph is null || wizard is null || _morphClientReady) {
            return;
        }

        // The game answers with MSG_PETGAMEREADY once it has the zone's PetGameMorph object; until then the message
        // is dropped, so it goes again a few times.
        if (++_morphInitTries < MORPH_INIT_TRIES) {
            Timers.StartSingleTimer(MORPH_INIT_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER {
                Kind = CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Init
            }, MORPH_INIT_RETRY);
        }

        if (_morphOwnData.Length == 0) {
            WriteOwnMorphData(wizard);
        }

        var canAfford = (sbyte) (wizard.GameStats.m_currentGold >= PetHatching.GoldCost ? 1 : 0);
        Logger.Information("Hatching zone: sending {0} MSG_PETGAMEINIT Game={1} ({2} bytes) MinLevel=0 Track={5}, then MSG_PETMORPHCANAFFORD CanAfford={3} (try {4}).",
            Logger.Args(wizard.CharId, PetMorphZone.GAME, _morphOwnData.Length, canAfford, _morphInitTries, Math.Clamp(PetHatching.LendsPerDay, 0, 255)));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = PetMorphZone.GAME, Data = _morphOwnData, MinLevel = 0,
            // "Lends Available Today" (a byte, so 255 at most); 0 greys out Lend Pet.
            Track = (byte) Math.Clamp(PetHatching.LendsPerDay, 0, 255) });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD { CanAfford = canAfford });
        if (_morph.IsPair && _peerPet is not null) {
            Logger.Information("Hatching zone: telling {0} the partner's pick {1}.", Logger.Args(wizard.CharId, PetHatching.Describe(_peerPet)));
            ShowPartnerPet();
        }
    }

    // A guess: the start carries the partner's PetGameMorphPartner in a pair, the player's own alone.
    private void SendMorphStart() {
        if (_morph is null) {
            return;
        }

        // The window takes the player's own pets from the player; the start names the partner and the partner's pets.
        // Alone, the partner is empty (character id 0), and both sides pick from the player's own pets.
        var partnerData = _morph.IsPair && _morphPeerData.Length > 0;
        var data = partnerData ? _morphPeerData : NoPartnerData();
        Logger.Information("Hatching zone: sending {0} MSG_PETGAMESTART Game={1} with {2} ({3} bytes).",
            Logger.Args(_morph.CharId, PetMorphZone.GAME, partnerData ? "the partner's data" : "no partner (alone)", data.Length));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = PetMorphZone.GAME, Data = data });
    }

    private static ByteString NoPartnerData() {
        var partner = new PetGameMorphPartner { m_characterId = 0, m_pets = [] };

        return s_morphSerializer.Serialize(partner, MORPH_FLAGS, out var data) ? data : default;
    }

    private void ScheduleMorphReturn(string reason, TimeSpan delay) {
        if (_morph is null || _morphLeaving) {
            return;
        }

        _morphLeaving = true;
        Logger.Information("Hatching zone: {0} goes back to {1} in {2} s ({3}).",
            Logger.Args(_morph.CharId, _morph.ReturnZone, delay.TotalSeconds, reason));
        Timers.StartSingleTimer(MORPH_RETURN_TIMER, new CHARACTER_103_PROTOCOL.MSG_HATCHZONETIMER {
            Kind = CHARACTER_103_PROTOCOL.HatchZoneTimerKind.Return,
            Reason = reason,
        }, delay);
    }

    /// <summary>
    /// Sends the player back to the pad they came from (the zone's HomeToFallback adjective suggests the game expects
    /// to go back where it came from).
    /// </summary>
    private void ReturnFromMorphZone(string reason) {
        var visit = _morph;
        if (visit is null) {
            return;
        }

        var wizard = GetActiveWizard();
        Logger.Information("Hatching zone: sending {0} back to {1} at {2} ({3}).", Logger.Args(visit.CharId, visit.ReturnZone, visit.ReturnLocation, reason));
        if (_peerCharId != 0) {
            End(tellPeer: true, peerJoinShown: null);
        }

        EndSolo();
        ForgetMorph(visit);

        // No MSG_PETGAMEENDING to the game: it takes that message's GlobalID as the player's own object id, and the
        // player could not move after coming back (Niduus, 2026-10-20). The zone change closes the window.

        // The lender just moves back, without the teleport effect (Niduus, 2026-10-21: "Not TP out, but move them out").
        var effects = !_returnQuietly;
        _returnQuietly = false;
        Teleport(visit.ReturnZone, doTeleportEffects: effects, destinationLocation: visit.ReturnLocation);
    }

    private bool _returnQuietly;

    // Where the player stood, facing the same way, when the trip started (before the sigil snaps them), so they come
    // back there and not at the pad's centre (Niduus, 2026-10-20).
    private static string TripReturnLocation(Wizard wizard, string sigilLoc) {
        var at = wizard.Location;
        if (at.X != 0 || at.Y != 0 || at.Z != 0) {
            return Util.GetCompactStringFromVector(at, wizard.Orientation);
        }

        return string.IsNullOrEmpty(sigilLoc) ? PetMorphZone.LOCATION : sigilLoc;
    }

    private void ForgetMorph(PetMorphVisit visit) {
        Timers.Cancel(MORPH_INIT_TIMER);
        Timers.Cancel(MORPH_PARTNER_TIMER);
        Timers.Cancel(MORPH_TIMEOUT_TIMER);
        Timers.Cancel(MORPH_RETURN_TIMER);
        PetMorphZone.End(visit.CharId);
        SessionActor.ClientMessageLogPrefix = null;
        _morph = null;
        _morphClientReady = false;
        _morphPeerSeen = false;
        _morphLeaving = false;
        _morphOwnData = default;
        _morphPeerData = default;
    }

    // Logging out (or losing the connection) in the morph zone: the next login starts at the pad. The login already
    // skips minigame zones for the previous zone, which is the hatchery.
    private void LeaveMorphZoneOnDeparture() {
        var visit = _morph;
        if (visit is null) {
            return;
        }

        var wizard = GetActiveWizard();
        Logger.Information("Hatching zone: {0} left the game in the morph zone; the next login starts at {1}.",
            Logger.Args(visit.CharId, visit.ReturnZone));
        if (wizard is not null && string.Equals(wizard.PreviousZone, visit.ReturnZone, StringComparison.OrdinalIgnoreCase)
            && visit.ReturnLocation.Contains(',')) {
            var pad = Util.GetVectorFromCompactString(visit.ReturnLocation);
            wizard.SetPersistentLocation(new Imcodec.Math.Vector3(pad.X, pad.Y, pad.Z));
            wizard.SetPersistentOrientation(pad.W);
        }

        ForgetMorph(visit);
    }

    #endregion

    #region Hatchmaking kiosk

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_OPENHATCHKIOSK))]
    private void ReceiveOpenHatchKiosk(CHARACTER_103_PROTOCOL.MSG_OPENHATCHKIOSK message) {
        Logger.Information("Hatching: kiosk {0} used; sending MSG_REQUESTHATCHMAKING.", Logger.Args(message.KioskGID));
        try {
            SendKioskInfo(message.KioskGID);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: opening the kiosk failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKING))]
    private void ReceiveRequestHatchmaking(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKING message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKING GlobalID={0} PetData={1} CrownCost={2} HatchDayCode={3} NoInPersonHatching={4}.",
            Logger.Args(message.GlobalID, Hex(message.PetData), message.CrownCost, message.HatchDayCode, message.NoInPersonHatching));
        try {
            SendKioskInfo(message.GlobalID);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_REQUESTHATCHMAKING failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGADDPET))]
    private void ReceiveAddPet(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGADDPET message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGADDPET PetID={0}.", Logger.Args(message.PetID));
        try {
            var wizard = GetActiveWizard();
            var item = PetCare.FindPet(wizard, message.PetID);
            var pet = PetHatching.Snapshot(item, wizard.CharId);
            var problem = PetHatching.ParentProblem(pet);
            if (problem is not null) {
                InformGameClient(problem);
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSTATUS { ErrorCode = 1 });

                return;
            }

            HatchmakingListings.Add(Listing(item, pet));
            Logger.Information("Hatching: {0} listed {1} at the kiosk.", Logger.Args(wizard.CharId, PetHatching.Describe(pet)));
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSTATUS { ErrorCode = 0 });
            InformGameClient($"{pet.Label} is now at the hatchmaking kiosk.");
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_REQUESTHATCHMAKINGADDPET failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGREMOVEPET))]
    private void ReceiveRemovePet(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGREMOVEPET message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGREMOVEPET PetID={0}.", Logger.Args(message.PetID));
        var listing = HatchmakingListings.Find(message.PetID);
        if (listing is not null && listing.Parent.OwnerCharId == GetActiveWizard()?.CharId) {
            HatchmakingListings.Remove(message.PetID);
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSTATUS { ErrorCode = 0 });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETTYPES))]
    private void ReceivePetTypes(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETTYPES message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGPETTYPES PetData={0}.", Logger.Args(Hex(message.PetData)));
        var types = new HatchmakingPetTypesList {
            m_hatchmakingPetTypesList = [.. HatchmakingListings.All().Select(l => l.Parent.TemplateId).Distinct()],
        };
        if (TryWrite(types, out var data)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETTYPES { PetData = data });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETS))]
    private void ReceivePets(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETS message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGPETS PetData={0} PetTemplateID={1} Skip={2}.",
            Logger.Args(Hex(message.PetData), message.PetTemplateID, message.Skip));
        var listings = HatchmakingListings.All()
            .Where(l => message.PetTemplateID == 0 || l.Parent.TemplateId == message.PetTemplateID)
            .Skip((int) Math.Min(message.Skip, int.MaxValue))
            .ToList();
        var list = new HatchmakingPetGIDList {
            m_skip = message.Skip,
            m_petTemplateID = message.PetTemplateID,
            m_hatchmakingPetGIDList = [.. listings.Select(l => (GID) l.Parent.ItemId)],
            m_hatchmakingPlayerGIDList = [.. listings.Select(l => (GID) Wizard.GetGameObjectId(l.Parent.OwnerCharId))],
        };
        if (TryWrite(list, out var data)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETS {
                PetData = data,
                PetTemplateID = message.PetTemplateID,
                Skip = message.Skip,
            });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETDATA))]
    private void ReceivePetData(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETDATA message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGPETDATA PetData={0} PetID={1} IsInPetTome={2}.",
            Logger.Args(Hex(message.PetData), message.PetID, message.IsInPetTome));
        try {
            var wizard = GetActiveWizard();
            var own = PetCare.FindPet(wizard, message.PetID);
            var listing = own is null ? HatchmakingListings.Find(message.PetID) : null;
            ByteString data = default;
            uint templateId;
            if (own is not null && ItemSync.TrySerialize(own, out var ownData)) {
                data = ownData;
                templateId = (uint) own.m_templateID;
            }
            else if (listing?.ItemData is { Length: > 0 }) {
                data = Convert.FromBase64String(listing.ItemData);
                templateId = listing.Parent.TemplateId;
            }
            else {
                Logger.Information("Hatching: no pet {0} to show.", Logger.Args(message.PetID));

                return;
            }

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGPETDATA {
                PetData = data,
                PetID = message.PetID,
                IsInPetTome = (byte) (wizard.OwnedPets.ContainsValue(templateId) ? 1 : 0),
            });
        }
        catch (Exception ex) {
            Logger.Error("Hatching: MSG_REQUESTHATCHMAKINGPETDATA failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGSEARCHPETS))]
    private void ReceiveSearchPets(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGSEARCHPETS message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGSEARCHPETS GlobalID={0} Data={1} SearchType={2} InPerson={3}.",
            Logger.Args(message.GlobalID, Hex(message.Data), message.SearchType, message.InPerson));
        if (TryWrite(PetsList(HatchmakingListings.All()), out var data)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGSEARCHPETS {
                GlobalID = message.GlobalID,
                Data = data,
                SearchType = message.SearchType,
                InPerson = message.InPerson,
            });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGFRIENDPETS))]
    private void ReceiveFriendPets(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGFRIENDPETS message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGFRIENDPETS GlobalID={0} Data={1} InPerson={2}.",
            Logger.Args(message.GlobalID, Hex(message.Data), message.InPerson));
        // Every listed pet for now, the player's own included.
        if (TryWrite(PetsList(HatchmakingListings.All()), out var data)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGFRIENDPETS {
                GlobalID = message.GlobalID,
                Data = data,
                InPerson = message.InPerson,
            });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCH))]
    private void ReceiveKioskHatch(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCH message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGHATCH PetID1={0} PetID2={1}.", Logger.Args(message.PetID1, message.PetID2));
        KioskHatch(message.PetID1, message.PetID2);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCH2))]
    private void ReceiveKioskHatch2(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCH2 message) {
        Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGHATCH2 PetID1={0} PetID2={1} PetIndex={2}.",
            Logger.Args(message.PetID1, message.PetID2, message.PetIndex));
        KioskHatch(message.PetID1, message.PetID2);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCHEDPET))]
    private void ReceiveHatchedPet(WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKINGHATCHEDPET message)
        => Logger.Information("Hatching: game sent MSG_REQUESTHATCHMAKINGHATCHEDPET PetID={0} PetIndex={1} PetData={2} IsInPetTome={3}. Not answered yet.",
            Logger.Args(message.PetID, message.PetIndex, Hex(message.PetData), message.IsInPetTome));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGMESSAGE))]
    private void ReceiveHatchmakingMessage(WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGMESSAGE message)
        => Logger.Information("Hatching: game sent MSG_HATCHMAKINGMESSAGE MessageType={0} MyPetTemplateID={1} MyPetName={2} TheirPetTemplateID={3} HatchPepperCount={4}. Not answered yet.",
            Logger.Args(message.MessageType, message.MyPetTemplateID, message.MyPetName, message.TheirPetTemplateID, message.HatchPepperCount));

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSREQUESTPREMIUMPETEXEMPTIONS))]
    private void ReceivePremiumPetExemptions(WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSREQUESTPREMIUMPETEXEMPTIONS message) {
        Logger.Information("Hatching: game sent MSG_HATCHMAKINGSREQUESTPREMIUMPETEXEMPTIONS Buffer={0} PremiumPetExceptionCode={1}.",
            Logger.Args(Hex(message.Buffer), message.PremiumPetExceptionCode));
        if (TryWrite(new HatchmakingPremiumPetsList { m_addedPremiumPets = [], m_removedPremiumPets = [] }, out var data)) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSREQUESTPREMIUMPETEXEMPTIONS {
                Buffer = data,
                PremiumPetExceptionCode = message.PremiumPetExceptionCode,
            });
        }
    }

    /// <summary>
    /// Hatches at the kiosk. The first pet is the player's; the second is the player's own (so a player alone can
    /// hatch two of their pets, Niduus 2026-10-20) or a pet someone listed at the kiosk.
    /// </summary>
    private void KioskHatch(ulong petId1, ulong petId2) {
        try {
            var wizard = GetActiveWizard();
            var mine1 = PetCare.FindPet(wizard, petId1);
            var mine2 = PetCare.FindPet(wizard, petId2);

            // The game may name the kiosk pet first.
            if (mine1 is null && mine2 is not null) {
                (petId1, petId2) = (petId2, petId1);
                (mine1, mine2) = (mine2, mine1);
            }

            var parentA = PetHatching.Snapshot(mine1, wizard.CharId);
            var parentB = mine2 is not null ? PetHatching.Snapshot(mine2, wizard.CharId) : HatchmakingListings.Find(petId2)?.Parent;
            if (parentA is null || parentB is null) {
                Logger.Information("Hatching: kiosk hatch of {0} and {1}: {2}.",
                    Logger.Args(petId1, petId2, parentA is null ? "neither pet is this player's" : "the second pet is not this player's or at the kiosk"));
                InformGameClient("Could not find both pets for this hatch.");
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_HATCHMAKINGSTATUS { ErrorCode = 1 });

                return;
            }

            if (mine2 is null) {
                HatchmakingListings.CountHatch(petId2);
            }

            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BREEDPETS { ParentA = parentA, ParentB = parentB, Source = HatchSource.Kiosk });
        }
        catch (Exception ex) {
            Logger.Error("Hatching: kiosk hatch of {0} and {1} failed. {2}", Logger.Args(petId1, petId2, ex));
        }
    }

    private void SendKioskInfo(ulong globalId) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        // The copies of this player's listed pets are brought up to date, and pets no longer owned are taken off.
        foreach (var listing in HatchmakingListings.Of(wizard.CharId)) {
            var item = PetCare.FindPet(wizard, listing.Parent.ItemId);
            if (item is null) {
                HatchmakingListings.Remove(listing.Parent.ItemId);
            }
            else {
                HatchmakingListings.Add(Listing(item, PetHatching.Snapshot(item, wizard.CharId)));
            }
        }

        if (!TryWrite(PetsList(HatchmakingListings.Of(wizard.CharId)), out var data)) {
            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTHATCHMAKING {
            GlobalID = globalId,
            PetData = data,
            CrownCost = 0,
            HatchDayCode = 0,
            NoInPersonHatching = (byte) (PetHatching.Enabled ? 0 : 1),
        });
    }

    private static HatchmakingListing Listing(WizClientObjectItem item, PetParent pet) => new() {
        Parent = pet,
        NameKeys = CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(item, out var name) ? name.m_nameKeys : 0,
        ItemData = ItemSync.TrySerialize(item, out var data) ? Convert.ToBase64String(data) : null,
    };

    private static HatchmakingPetsList PetsList(System.Collections.Generic.IEnumerable<HatchmakingListing> listings) => new() {
        m_hatchmakingPetsList = [.. listings.Select(l => new HatchmakingPet {
            m_petGID = l.Parent.ItemId,
            m_petItemGID = l.Parent.ItemId,
            m_petTemplateID = l.Parent.TemplateId,
            m_nameKey = l.NameKeys,
            m_hatchCount = l.HatchCount,
        })],
        m_disableKiosk = !PetHatching.Enabled,
        m_disableOffer = false,
        m_premiumPetExceptionCode = 0,
    };

    // Written like the pet game data: these classes' properties are Save|Copy|Public, none Transmit.
    private static bool TryWrite(PropertyClass value, out ByteString data) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (serializer.Serialize(value, PropertyFlags.Prop_Public, out data)) {
            return true;
        }

        Logger.Error("Hatching: could not write {0}.", Logger.Args(value.GetType().Name));

        return false;
    }

    #endregion

    #region Lifecycle

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) => LeaveOnDeparture(isLogout: true);

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) => LeaveOnDeparture(isLogout: true);

    protected override void OnPreDispose() {
        try {
            LeaveOnDeparture(isLogout: false);
        }
        catch (Exception ex) {
            Logger.Error("Hatching: leaving the hatch while the session ended failed. {0}", Logger.Args(ex));
        }
        finally {
            base.OnPreDispose();
        }
    }

    // The session also ends after every zone transfer, so only a logout moves the saved position back to the pad.
    private void LeaveOnDeparture(bool isLogout) {
        EndSolo();
        if (_peerCharId != 0) {
            End(tellPeer: true, peerJoinShown: null);
        }

        if (isLogout) {
            LeaveMorphZoneOnDeparture();
        }
        else if (_morph is not null) {
            Logger.Information("Hatching zone: the session of {0} ended in the morph zone.", Logger.Args(_morph.CharId));
            ForgetMorph(_morph);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The pet this player hatches with: the one picked in the window, else the one worn.
    /// </summary>
    private PetParent MyPet(Wizard wizard) {
        if (wizard is null) {
            return null;
        }

        var item = (_myPetId != 0 ? PetCare.FindPet(wizard, _myPetId) : null) ?? PetCare.GetWornPet(wizard);

        return PetHatching.Snapshot(item, wizard.CharId);
    }

    private bool IsPeer(ulong targetGid)
        => _peerCharId != 0 && Wizard.TryGetCharacterId(targetGid, out var charId) && charId == _peerCharId;

    private CHARACTER_103_PROTOCOL.MSG_HATCHPEER HatchPeer(CHARACTER_103_PROTOCOL.HatchPeerKind kind, int value = 0, PetParent pet = null) => new() {
        Kind = kind,
        FromCharId = GetActiveWizard()?.CharId ?? 0,
        FromSession = SessionActor.ActorRef,
        FromZone = SessionActor.GetZoneActor(),
        Value = value,
        Pet = pet,
    };

    private void TellPeer(CHARACTER_103_PROTOCOL.MSG_HATCHPEER message) {
        if (_peerSession is not null) {
            _peerSession.Tell(message);
        }
        else if (!string.IsNullOrEmpty(_peerPath)) {
            Context.ActorSelection(_peerPath).Tell(message);
        }
    }

    // In the morph zone the in-person window's messages are not sent: its own window is up.
    private void SendJoinStatus() {
        if (_peerCharId == 0 || _morph is not null) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS {
            TargetGID = Wizard.GetGameObjectId(_peerCharId),
            TargetStatus = _peerJoin,
            PlayerStatus = _myJoin,
        });
    }

    private void SendReadyStatus() {
        if (_peerCharId == 0 || _morph is not null) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHREADYSTATUS {
            TargetGID = Wizard.GetGameObjectId(_peerCharId),
            TargetStatus = _peerReady,
            PlayerStatus = _myReady,
        });
    }

    private void SendResult(HatchStatus status) {
        if (_peerCharId != 0 && _morph is null) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT { TargetGID = Wizard.GetGameObjectId(_peerCharId), Status = (uint) status });
        }
    }

    private void Refuse(ulong targetGid, HatchStatus status, string reason) {
        Logger.Information("Hatching: request to hatch with {0} refused: {1}", Logger.Args(targetGid, reason));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT { TargetGID = targetGid, Status = (uint) status });
        InformGameClient(reason);
    }

    private static string Name(Wizard wizard)
        => wizard?.PlayerNameBehavior?.GetWizardName() ?? wizard?.CharId.ToString() ?? "?";

    private static string Hex(ByteString data) {
        byte[] bytes = data;

        return bytes is null || bytes.Length == 0 ? "none" : Convert.ToHexString(bytes);
    }

    #endregion

}
