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
 * TRADE SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * Receives the client's trade requests (create, join, ready, offer) and forwards them to the
 * TradeDirectory with this session's identity. Also screens the invites sent to this player, locks and
 * releases the player's movement for a trade, and runs the escrow steps that move treasure cards in and
 * out of this player's treasure book.
 *
 * USAGE EXAMPLE:
 * Attached to every game session by GameServiceFactory.
 *
 * NOTE:
 * The client never names itself in a trade request, so the session's own wizard is always the source.
 * Hold, claim and refund run here because only the owning session may change its wizard: each changes the
 * stored spellbook and the TradeEscrow document in one save, then mirrors the same cards in memory, once.
 * The client moves the cards itself when the swap succeeds, so no treasure book message is sent. A step
 * left half done when the session ends is settled in OnPreDispose.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class TradeService(SessionActor sessionActor) : MessageService(sessionActor) {

    private static readonly uint s_shopState = StringHash.Compute("Shop");
    private static readonly uint s_notShoppingState = StringHash.Compute("NotShopping");

    private ulong _charId;
    private bool _isInvolved;
    private bool _isLocked;
    private bool _isLoggingOut;
    private Guid _escrowTradeId;
    private Wizard _escrowWizard;
    private readonly HashSet<(Guid TradeId, TradeStep Step)> _mirroredSteps = [];

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TradeService(parentActor));

    #region Client requests

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CREATE))]
    private void ReceiveTradeCreate(WIZARD_12_PROTOCOL.MSG_TRADE_CREATE message) {
        var wizard = GetActiveWizard();
        if (wizard is null || !Wizard.TryGetCharacterId(message.TargetGID, out var targetCharId)
            || targetCharId == 0 || targetCharId == wizard.CharId) {
            return;
        }

        // The client opens its window before the server answers, so every refusal is sent as a RESULT.
        var zone = SessionActor.GetZoneActor();
        if (wizard.IsInDuel || zone is null) {
            SendResult(message.TargetGID, TradeError.CannotTradeNow);

            return;
        }

        if (!TryGetOnlinePlayer(targetCharId, out var target)) {
            SendResult(message.TargetGID, TradeError.TargetNotFound);

            return;
        }

        if (target.CurrentZone != wizard.Zone) {
            SendResult(message.TargetGID, TradeError.TargetNotInZone);

            return;
        }

        _charId = wizard.CharId;
        _isInvolved = true;

        TellDirectory(new TRADE_110_PROTOCOL.MSG_OPENTRADE {
            Session = SessionActor.ActorRef,
            Zone = zone,
            RequesterCharId = wizard.CharId,
            TargetCharId = targetCharId,
            TargetSessionPath = target.ActorPath
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS))]
    private void ReceiveTradeJoinStatus(WIZARD_12_PROTOCOL.MSG_TRADE_JOIN_STATUS message) {
        // Accepting is a 1 and declining or cancelling a 0; the client sends nothing else.
        if (message.PlayerStatus is not (0 or 1) || !TryGetPeer(message.TargetGID, out var wizard, out var peerCharId)) {
            return;
        }

        TellDirectory(new TRADE_110_PROTOCOL.MSG_JOINCHANGED {
            CharId = wizard.CharId,
            Session = SessionActor.ActorRef,
            Zone = SessionActor.GetZoneActor(),
            PeerCharId = peerCharId,
            Join = message.PlayerStatus
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS))]
    private void ReceiveTradeReadyStatus(WIZARD_12_PROTOCOL.MSG_TRADE_READY_STATUS message) {
        if (!TryGetPeer(message.TargetGID, out var wizard, out var peerCharId)) {
            return;
        }

        TellDirectory(new TRADE_110_PROTOCOL.MSG_READYCHANGED {
            CharId = wizard.CharId,
            PeerCharId = peerCharId,
            Ready = message.PlayerStatus
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM))]
    private void ReceiveTradeChangeItem(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_ITEM message) {
        if (!TryGetPeer(message.TargetGID, out var wizard, out var peerCharId)) {
            return;
        }

        TellDirectory(new TRADE_110_PROTOCOL.MSG_OFFERCHANGED {
            CharId = wizard.CharId,
            PeerCharId = peerCharId,
            TemplateId = message.ItemTemplate,
            Enchant = message.ItemEnchant,
            Action = message.Action,
            OwnedCount = wizard.SpellbookBehavior.TreasureCardCount(message.ItemTemplate)
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_MONEY))]
    private void ReceiveTradeChangeMoney(WIZARD_12_PROTOCOL.MSG_TRADE_CHANGE_MONEY message)
        => Logger.Warning("Character {CharId} sent a trade gold change of {Amount}. Trades carry treasure cards only.",
            Logger.Args(GetActiveWizard()?.CharId, message.Amount));

    #endregion

    #region Directory messages

    [MessageHandler(typeof(TRADE_110_PROTOCOL.MSG_TRADEINVITE))]
    private void ReceiveTradeInvite(TRADE_110_PROTOCOL.MSG_TRADEINVITE message) {
        var wizard = GetActiveWizard();
        var zone = SessionActor.GetZoneActor();
        var screened = new TRADE_110_PROTOCOL.MSG_INVITESCREENED {
            TradeId = message.TradeId,
            RequesterCharId = message.RequesterCharId,
            Session = SessionActor.ActorRef,
            Zone = zone
        };

        if (wizard is null) {
            screened.ErrorCode = (int) TradeError.CannotTradeNow;
            TellDirectory(screened);

            return;
        }

        var error = ScreenInvite(wizard, message, zone);
        if (error != TradeError.None) {
            screened.ErrorCode = (int) error;
            TellDirectory(screened);

            return;
        }

        if (BuddyRelationshipCollection.HasBlocked(wizard.CharId, message.RequesterCharId)) {
            // A blocked requester sees the same cancelled trade as when the player declines.
            TellDirectory(screened);

            return;
        }

        _charId = wizard.CharId;
        _isInvolved = true;

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_TRADE_REQUEST { TargetGID = Wizard.GetGameObjectId(message.RequesterCharId) });

        screened.Delivered = true;
        TellDirectory(screened);
    }

    [MessageHandler(typeof(TRADE_110_PROTOCOL.MSG_SETTRADESTATE))]
    private void ReceiveSetTradeState(TRADE_110_PROTOCOL.MSG_SETTRADESTATE message) {
        if (message.Trading) {
            _isInvolved = true;
            if (!_isLocked) {
                _isLocked = true;
                BroadcastState(s_shopState);
            }

            return;
        }

        // The directory has ended the trade, so there is nothing left to announce when this session ends.
        var wasLocked = _isLocked;
        _isLocked = false;
        _isInvolved = false;
        if (wasLocked) {
            BroadcastState(s_notShoppingState);
        }
    }

    [MessageHandler(typeof(TRADE_110_PROTOCOL.MSG_TRADESTEP))]
    private void ReceiveTradeStep(TRADE_110_PROTOCOL.MSG_TRADESTEP message) {
        var wizard = GetActiveWizard();
        var done = new TRADE_110_PROTOCOL.MSG_TRADESTEPDONE {
            TradeId = message.TradeId,
            CharId = wizard?.CharId ?? 0,
            Step = message.Step
        };

        if (wizard is null) {
            done.ErrorCode = (int) TradeError.Failed;
            TellDirectory(done);

            return;
        }

        _charId = wizard.CharId;
        _escrowTradeId = message.TradeId;
        _escrowWizard = wizard;

        done.ErrorCode = (int) RunStep(wizard, message.TradeId, message.Step);
        done.Success = done.ErrorCode == (int) TradeError.None;
        TellDirectory(done);
    }

    #endregion

    #region Lifecycle

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message)
        => AnnounceDeparture(isLogout: true, release: true);

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message)
        => AnnounceDeparture(isLogout: true, release: true);

    protected override void OnPreDispose() {
        try {
            AnnounceDeparture(_isLoggingOut, release: false);
            SettleEscrow();
        } finally {
            base.OnPreDispose();
        }
    }

    private void AnnounceDeparture(bool isLogout, bool release) {
        _isLoggingOut |= isLogout;
        if (!_isInvolved) {
            return;
        }

        // A logout the player backs out of must not leave the player standing in the Shop state.
        if (release && _isLocked) {
            BroadcastState(s_notShoppingState);
        }

        _isInvolved = false;
        _isLocked = false;

        TellDirectory(new TRADE_110_PROTOCOL.MSG_PARTICIPANTDEPARTED {
            CharId = _charId,
            Session = SessionActor.ActorRef,
            IsLogout = isLogout
        });
    }

    private void SettleEscrow() {
        if (_escrowTradeId == Guid.Empty || _escrowWizard is null) {
            return;
        }

        // The directory may have given up on this swap, so this player's side is finished from here:
        // claimed when both sides held, refunded otherwise. The memory copy is brought in line before any
        // other save of the spellbook can run.
        var result = TradeEscrowCollection.Settle(_escrowTradeId, _charId);
        if (result.Outcome == EscrowOutcome.Done
            && !_mirroredSteps.Contains((_escrowTradeId, TradeStep.Claim)) && !_mirroredSteps.Contains((_escrowTradeId, TradeStep.Refund))) {
            // A side ends in a claim or a refund, never both, so a step that already mirrored its end is not added again.
            Mirror(_escrowWizard, _escrowTradeId, TradeStep.Claim, result.Cards, result.Given);
        }

        // Whoever finishes last removes the document.
        _ = TradeEscrowCollection.DeleteIfSettled(_escrowTradeId);

        Logger.Debug("Trade {TradeId} settled for character {CharId} while the session ended: {Outcome}.",
            Logger.Args(_escrowTradeId, _charId, result.Outcome));
    }

    #endregion

    #region Steps

    private TradeError RunStep(Wizard wizard, Guid tradeId, TradeStep step) {
        if (step == TradeStep.Hold) {
            var refusal = CheckHold(wizard, tradeId);
            if (refusal != TradeError.None) {
                return refusal;
            }
        }

        var result = step switch {
            TradeStep.Hold => TradeEscrowCollection.Hold(tradeId, wizard.CharId),
            TradeStep.Claim => TradeEscrowCollection.Claim(tradeId, wizard.CharId),
            _ => TradeEscrowCollection.Refund(tradeId, wizard.CharId),
        };

        switch (result.Outcome) {
            case EscrowOutcome.Done:
            case EscrowOutcome.AlreadyDone:
                // A repeated step mirrors too: the call that did the work may have landed without its reply.
                Mirror(wizard, tradeId, step, result.Cards, result.Given);

                return TradeError.None;
            case EscrowOutcome.Failed:
                // The save may have landed even though the call gave up, and then the memory copy has to follow.
                return LandedAnyway(tradeId, wizard.CharId, step, out var landed, out var given) && Mirror(wizard, tradeId, step, landed, given)
                    ? TradeError.None
                    : TradeError.Failed;
            default:
                return TradeError.Failed;
        }
    }

    private static TradeError CheckHold(Wizard wizard, Guid tradeId) {
        // Checked in memory before anything is written, so a player who cannot hold the swap is refused cleanly.
        if (!TryPeek(tradeId, out var escrow) || (escrow.CharIdA != wizard.CharId && escrow.CharIdB != wizard.CharId)) {
            return TradeError.Failed;
        }

        var isA = escrow.CharIdA == wizard.CharId;
        var give = isA ? escrow.CardsFromA : escrow.CardsFromB;
        var receive = isA ? escrow.CardsFromB : escrow.CardsFromA;
        var book = wizard.SpellbookBehavior.TreasureCardTemplateIds ?? [];

        var owned = new Dictionary<uint, int>();
        foreach (var card in book) {
            owned[card] = owned.GetValueOrDefault(card) + 1;
        }

        foreach (var card in give) {
            if (!owned.TryGetValue(card, out var copies) || copies == 0) {
                return TradeError.Failed;
            }

            owned[card] = copies - 1;
        }

        var given = TreasureCardTradePolicy.CountCapacityUsed(give);
        var received = TreasureCardTradePolicy.CountCapacityUsed(receive);
        var projected = TreasureCardTradePolicy.CountCapacityUsed(book) - given + received;

        // A book above the limit (loot and shops do not enforce it) can still trade as long as it does not grow.
        return projected > TradeLimits.TreasureBookCapacity && received > given ? TradeError.CannotHold : TradeError.None;
    }

    private bool Mirror(Wizard wizard, Guid tradeId, TradeStep step, IReadOnlyList<uint> cards, IReadOnlyList<uint> given = null) {
        // A claim or refund can only follow a hold that landed, so a hold this session never heard about is taken out first.
        if (step != TradeStep.Hold && given is { Count: > 0 }) {
            Mirror(wizard, tradeId, TradeStep.Hold, given);
        }

        if (!_mirroredSteps.Add((tradeId, step))) {
            return true;
        }

        var book = wizard.SpellbookBehavior;
        if (step != TradeStep.Hold) {
            book.AddTreasureCards(cards);

            return true;
        }

        if (!book.TryRemoveTreasureCards([.. cards])) {
            // The stored document is the truth; the next login reads it.
            Logger.Error("Trade {TradeId}: the memory copy of character {CharId} did not hold the cards the hold took.",
                Logger.Args(tradeId, wizard.CharId));
        }

        return true;
    }

    private static bool TryPeek(Guid tradeId, out TradeEscrow escrow) {
        try {
            escrow = TradeEscrowCollection.Peek(tradeId);

            return escrow is not null;
        } catch (Exception ex) {
            // An unreadable document answers the step as failed, so the directory decides at once.
            Logger.Error("Trade {TradeId}: the escrow document could not be read. {Exception}", Logger.Args(tradeId, ex));
            escrow = null;

            return false;
        }
    }

    private static bool LandedAnyway(Guid tradeId, ulong charId, TradeStep step, out List<uint> cards, out List<uint> given) {
        cards = [];
        given = [];
        if (!TryPeek(tradeId, out var escrow)) {
            return false;
        }

        var isA = escrow.CharIdA == charId;
        var landed = step switch {
            TradeStep.Hold => isA ? escrow.HeldA : escrow.HeldB,
            TradeStep.Claim => isA ? escrow.ClaimedA : escrow.ClaimedB,
            _ => isA ? escrow.RefundedA : escrow.RefundedB,
        };
        if (!landed) {
            return false;
        }

        var own = isA ? escrow.CardsFromA : escrow.CardsFromB;
        var other = isA ? escrow.CardsFromB : escrow.CardsFromA;
        cards = [.. step == TradeStep.Claim ? other : own];
        given = step == TradeStep.Hold ? [] : [.. own];

        return true;
    }

    #endregion

    #region Helpers

    private TradeError ScreenInvite(Wizard wizard, TRADE_110_PROTOCOL.MSG_TRADEINVITE message, IActorRef zone) {
        if (wizard.PrivacyOptions.AllowTradeRequest == 0) {
            return TradeError.NotAccepting;
        }

        if (wizard.IsInDuel) {
            return TradeError.CannotTradeNow;
        }

        if (_isLocked) {
            return TradeError.AlreadyTrading;
        }

        if (zone is null || !zone.Equals(message.RequesterZone)) {
            return TradeError.TargetNotInZone;
        }

        return TradeError.None;
    }

    private bool TryGetPeer(ulong targetGameObjectId, out Wizard wizard, out ulong peerCharId) {
        peerCharId = 0;
        wizard = GetActiveWizard();

        return wizard is not null
            && Wizard.TryGetCharacterId(targetGameObjectId, out peerCharId)
            && peerCharId != 0
            && peerCharId != wizard.CharId;
    }

    private void SendResult(ulong targetGameObjectId, TradeError status)
        => SendToSocket(new WIZARD_12_PROTOCOL.MSG_TRADE_RESULT { TargetGID = targetGameObjectId, Status = (uint) status });

    private void BroadcastState(uint state)
        => ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = GetActiveWizard().GameObjectID,
            State = state,
            Data = "",
            IgnoreIfCurrentStateIsOff = 0
        }, isSelfless: false);

    private static void TellDirectory(IServerMessage message)
        => TradeDirectory.Instance?.Tell(message);

    #endregion

}
