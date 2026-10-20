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
 * hatchmaking kiosk (Justin, 2026-10-20: "work on hatching. Solo and player to player."). The egg
 * itself is made by PetService (MSG_BREEDPETS), which owns the egg timers.
 *
 * USAGE EXAMPLE:
 * Attached to every game session by GameServiceFactory.
 *
 * NOTE:
 * The hatchery's pet morphing sigils come here too (MSG_HATCHSIGIL from InteractPetMorphSigilComponent):
 * a solo sigil tries the game's pet morphing window and lists the player's pets for `.hatch`; a duo
 * sigil starts the in-person hatch with the other player on it. Those steps log "Hatching sigil".
 * In person, each side's session keeps its own state and tells the other's session what its player
 * did (MSG_HATCHPEER), as TradeService does through its directory. When both are ready, each session
 * breeds its own egg: its player's pet with the partner's. Every message the game sends here is
 * logged with all its fields, because what the game sends and expects is not known yet.
 *
 * TODO:
 * - The status values (join -1/0/1, ready 0/1/2, result codes) are copied from trading; unverified.
 * - The solo sigil's window is a guess: the game's pet game "PetGameMorph" (PetGames.xml) opened with
 *   MSG_PETGAMEINIT, its data a PetGameMorphPartner (character id and pets); picks arrive as
 *   MSG_PETMORPHSET and the hatch as MSG_PETMORPHREADY. [Hatching] SigilWindow false turns it off.
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

    // The solo hatching sigil this session's player is using; _soloSigilGid is 0 when there is none.
    private const string MORPH_GAME = "PetGameMorph";
    private const uint MORPH_FLAGS = (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit);
    private static readonly CoreObjectSerializer s_morphSerializer = new(behaviors: SerializerFlags.None);
    private ulong _soloSigilGid;
    private readonly List<ulong> _soloPicks = [];
    private ByteString _soloMorphData;

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
            case CHARACTER_103_PROTOCOL.HatchPeerKind.Left:
                Timers.Cancel(INVITE_TIMER);
                _peerJoin = JOIN_LEFT;
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS {
                    TargetGID = peerGid,
                    TargetStatus = JOIN_LEFT,
                    // The asked player's request tip is dismissed with -1, as for trades.
                    PlayerStatus = _isRequester || _myJoin == JOIN_JOINED ? _myJoin : JOIN_PENDING,
                });
                if (!_bred) {
                    InformGameClient("The hatch was cancelled.");
                }

                Reset();
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
            Source = HatchSource.InPerson,
            PartnerGameObjectId = Wizard.GetGameObjectId(_peerCharId),
        });

        // The window closes with the result; both sides end on their own.
        Reset();
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
        Logger.Information("Hatching sigil: {0} on {1} ({2}), partner {3} ({4}).",
            Logger.Args(message.Role, message.SigilName, message.SigilGID, message.PartnerName, message.PartnerCharId));
        try {
            switch (message.Role) {
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Solo:
                    StartSolo(message.SigilGID);
                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Requester:
                    EndSolo();
                    InformGameClient($"Asking {message.PartnerName} to hatch with you. Wear the pet you want to hatch with; "
                        + $"if no hatch window opens, use the solo sigils or .hatch with two of your own pets.");
                    StartInPerson(Wizard.GetGameObjectId(message.PartnerCharId));
                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Partner:
                    EndSolo();
                    InformGameClient($"{message.PartnerName} wants to hatch pets with you on the sigil. Wear the pet you want "
                        + "to hatch with and accept the hatch request.");
                    break;
                case CHARACTER_103_PROTOCOL.HatchSigilRole.Alone:
                    // With nobody else on it, the duo sigil hatches two of the player's own pets, as live does (Justin, 2026-10-20).
                    StartSolo(message.SigilGID);
                    break;
            }
        }
        catch (Exception ex) {
            Logger.Error("Hatching sigil: {0} failed. {1}", Logger.Args(message.Role, ex));
        }
    }

    /// <summary>
    /// The morph window's pet game, if it is one, may join and say it is ready like the other pet games.
    /// </summary>
    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEJOIN))]
    private void ReceivePetGameJoin(PET_9_PROTOCOL.MSG_PETGAMEJOIN message) {
        if (!IsMorphGame(message.Game)) {
            return;
        }

        Logger.Information("Hatching sigil: game sent MSG_PETGAMEJOIN Game={0} Track={1} (solo sigil {2}).",
            Logger.Args(message.Game, message.Track, _soloSigilGid));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEJOINRSP { Game = MORPH_GAME, Success = (byte) (_soloSigilGid != 0 ? 1 : 0) });
    }

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETGAMEREADY))]
    private void ReceivePetGameReady(PET_9_PROTOCOL.MSG_PETGAMEREADY message) {
        Logger.Information("Hatching sigil: game sent MSG_PETGAMEREADY (solo sigil {0}).", Logger.Args(_soloSigilGid));
        if (_soloSigilGid == 0 || _soloMorphData.Length == 0) {
            return;
        }

        Logger.Information("Hatching sigil: sending MSG_PETGAMESTART for {0}.", Logger.Args(MORPH_GAME));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMESTART { Game = MORPH_GAME, Data = _soloMorphData });
    }

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
        Logger.Information("Hatching sigil: {0} has {1} hatched pets: {2}.",
            Logger.Args(wizard.CharId, pets.Count, string.Join(", ", pets.Select(p => $"{PetHatching.Label(p)} ({p.m_globalID.Full})"))));
        if (pets.Count < 2) {
            InformGameClient("You need two hatched pets to hatch on your own. Hatch an egg or get another pet, then come back.");

            return;
        }

        _soloSigilGid = sigilGid;
        _soloPicks.Clear();
        _soloMorphData = default;

        // The way that is known to work goes first, so it is there even if the window does not open.
        InformGameClient("Pick two of your pets to hatch. If no hatch window opens, type .hatch <pet 1> <pet 2> with numbers from "
            + "this list: " + string.Join("; ", pets.Select((p, i) => $"{i + 1}) {PetHatching.Label(p)}")));

        if (!PetHatching.SigilWindow) {
            Logger.Information("Hatching sigil: the morph window is turned off ([Hatching] SigilWindow); chat list only.");

            return;
        }

        var partner = new PetGameMorphPartner {
            m_characterId = wizard.CharId,
            m_pets = [.. pets.Cast<CoreObject>()],
        };
        if (!s_morphSerializer.Serialize(partner, MORPH_FLAGS, out var data)) {
            Logger.Error("Hatching sigil: could not write the PetGameMorphPartner; the morph window was not tried.");

            return;
        }

        _soloMorphData = data;
        Logger.Information("Hatching sigil: sending MSG_PETGAMEINIT Game={0} with {1} pets ({2} bytes) to open the morph window.",
            Logger.Args(MORPH_GAME, pets.Count, data.Length));
        SendToSocket(new PET_9_PROTOCOL.MSG_PETGAMEINIT { Game = MORPH_GAME, Data = data, MinLevel = 0, Track = 0 });
        SendToSocket(new PET_9_PROTOCOL.MSG_PETMORPHCANAFFORD {
            CanAfford = (sbyte) (wizard.GameStats.m_currentGold >= PetHatching.GoldCost ? 1 : 0)
        });
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
        TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BREEDPETS { ParentA = parentA, ParentB = parentB, Source = HatchSource.Command });
        EndSolo();
    }

    private void EndSolo() {
        _soloSigilGid = 0;
        _soloPicks.Clear();
        _soloMorphData = default;
    }

    private static bool IsMorphGame(ByteString game)
        => string.Equals(game.ToString(), MORPH_GAME, StringComparison.OrdinalIgnoreCase);

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
    /// hatch two of their pets, Justin 2026-10-20) or a pet someone listed at the kiosk.
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
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) => LeaveOnDeparture();

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) => LeaveOnDeparture();

    protected override void OnPreDispose() {
        try {
            LeaveOnDeparture();
        }
        catch (Exception ex) {
            Logger.Error("Hatching: leaving the hatch while the session ended failed. {0}", Logger.Args(ex));
        }
        finally {
            base.OnPreDispose();
        }
    }

    private void LeaveOnDeparture() {
        EndSolo();
        if (_peerCharId != 0) {
            End(tellPeer: true, peerJoinShown: null);
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

    private void SendJoinStatus() {
        if (_peerCharId == 0) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHJOINSTATUS {
            TargetGID = Wizard.GetGameObjectId(_peerCharId),
            TargetStatus = _peerJoin,
            PlayerStatus = _myJoin,
        });
    }

    private void SendReadyStatus() {
        if (_peerCharId == 0) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHREADYSTATUS {
            TargetGID = Wizard.GetGameObjectId(_peerCharId),
            TargetStatus = _peerReady,
            PlayerStatus = _myReady,
        });
    }

    private void SendResult(HatchStatus status) {
        if (_peerCharId != 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT { TargetGID = Wizard.GetGameObjectId(_peerCharId), Status = (uint) status });
        }
    }

    private void Refuse(ulong targetGid, HatchStatus status, string reason) {
        Logger.Information("Hatching: request to hatch with {0} refused: {1}", Logger.Args(targetGid, reason));
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PETHATCHRESULT { TargetGID = targetGid, Status = (uint) status });
        InformGameClient(reason);
    }

    private static string Hex(ByteString data) {
        byte[] bytes = data;

        return bytes is null || bytes.Length == 0 ? "none" : Convert.ToHexString(bytes);
    }

    #endregion

}
