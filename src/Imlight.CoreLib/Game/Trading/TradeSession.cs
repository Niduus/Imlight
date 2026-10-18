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
 * The rules of one treasure card trade between two players: who joined, what each side offers, who is
 * ready, and how far the escrow swap has got. It sends nothing; the TradeDirectory turns each result
 * into messages.
 * 
 * USAGE EXAMPLE:
 * if (session.TryChangeOffer(participant, templateId, 0, add: true, ownedCount) == TradeOfferResult.Changed) { ... }
 * 
 * NOTE:
 * Side A is the player who pressed Trade, side B the invitee. Every accepted offer change resets both
 * ready values to 0. ApplyStep names the escrow step the trade is waiting on; from ClaimA on the trade
 * is committed and can no longer fail on ownership.
 * 
 * TODO:
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Trading;

internal enum TradePhase {
    Inviting,
    Open,
    Applying,
    Ended,
}

internal enum TradeApplyStep {
    None,
    HoldA,
    HoldB,
    ClaimA,
    ClaimB,
    Done,
}

internal enum TradeLeaveResult {
    Left,
    Ignored,
}

internal enum TradeReadyResult {
    Applied,
    NotJoined,
    NotReady,
    Invalid,
    Ignored,
}

internal enum TradeOfferResult {
    Changed,
    NotJoined,
    Rejected,
    Ignored,
}

internal sealed class TradeSession {

    private const int ReadyConfirmed = 2;

    internal Guid TradeId { get; }
    internal TradeParticipant A { get; }
    internal TradeParticipant B { get; }
    internal TradePhase Phase { get; private set; } = TradePhase.Inviting;
    internal TradeApplyStep ApplyStep { get; private set; } = TradeApplyStep.None;
    internal TradeError AbortError { get; private set; }

    internal bool IsAborting => AbortError != TradeError.None;
    internal bool IsCommitted => ApplyStep >= TradeApplyStep.ClaimA;
    internal bool BothConfirmed => Phase == TradePhase.Open && A.Ready == ReadyConfirmed && B.Ready == ReadyConfirmed;

    internal TradeParticipant ApplyActor => ApplyStep switch {
        TradeApplyStep.HoldA or TradeApplyStep.ClaimA => A,
        TradeApplyStep.HoldB or TradeApplyStep.ClaimB => B,
        _ => null,
    };

    internal TradeSession(Guid tradeId, TradeParticipant requester, TradeParticipant invitee) {
        TradeId = tradeId;
        A = requester;
        B = invitee;

        // The requester's window is open as soon as it presses Trade.
        A.Join = 1;
        B.Join = -1;
    }

    internal TradeParticipant Find(ulong charId)
        => A.CharId == charId ? A : B.CharId == charId ? B : null;

    internal TradeParticipant PeerOf(TradeParticipant participant)
        => ReferenceEquals(participant, A) ? B : A;

    internal bool TryJoin(TradeParticipant participant) {
        if (Phase != TradePhase.Inviting || !ReferenceEquals(participant, B) || B.Join != -1) {
            return false;
        }

        B.Join = 1;
        Phase = TradePhase.Open;

        return true;
    }

    internal TradeLeaveResult TryLeave(TradeParticipant participant) {
        // Leaving ends the trade for both sides; a committed trade can only finish.
        if (Phase == TradePhase.Ended || IsCommitted) {
            return TradeLeaveResult.Ignored;
        }

        participant.Join = 0;
        Phase = TradePhase.Ended;

        return TradeLeaveResult.Left;
    }

    internal TradeReadyResult TryChangeReady(TradeParticipant participant, int ready) {
        if (Phase is TradePhase.Applying or TradePhase.Ended) {
            return TradeReadyResult.Ignored;
        }

        if (Phase != TradePhase.Open) {
            return TradeReadyResult.NotJoined;
        }

        if (ready is < 0 or > ReadyConfirmed) {
            return TradeReadyResult.Invalid;
        }

        if (ready == ReadyConfirmed && participant.Ready != ReadyConfirmed
            && (participant.Ready != 1 || PeerOf(participant).Ready < 1)) {
            return TradeReadyResult.NotReady;
        }

        participant.Ready = ready;

        return TradeReadyResult.Applied;
    }

    internal TradeOfferResult TryChangeOffer(TradeParticipant participant, uint templateId, uint enchant, bool add, int ownedCount) {
        // ownedCount is the copies in the participant's treasure book right now; an accepted change
        // adds or withdraws one copy and resets both sides to not ready.
        if (Phase is TradePhase.Applying or TradePhase.Ended) {
            return TradeOfferResult.Ignored;
        }

        if (Phase != TradePhase.Open) {
            return TradeOfferResult.NotJoined;
        }

        if (enchant != 0) {
            return TradeOfferResult.Rejected;
        }

        var offer = participant.Offer;
        offer.TryGetValue(templateId, out var copies);

        // The owned and offered counts are checked first, so an id the player does not hold never reaches the template lookup.
        if (add) {
            if (copies + 1 > ownedCount || (copies == 0 && offer.Count >= TradeLimits.MaxDistinctCardsPerSide)
                || !TreasureCardTradePolicy.IsTradable(templateId, enchant)) {
                return TradeOfferResult.Rejected;
            }

            offer[templateId] = copies + 1;
        } else {
            if (copies == 0) {
                return TradeOfferResult.Rejected;
            }

            if (copies == 1) {
                offer.Remove(templateId);
            } else {
                offer[templateId] = copies - 1;
            }
        }

        A.Ready = 0;
        B.Ready = 0;

        return TradeOfferResult.Changed;
    }

    internal static bool TryParseAction(byte action, out bool add) {
        add = action == 1;

        return action is 0 or 1;
    }

    internal bool BeginApply() {
        if (!BothConfirmed) {
            return false;
        }

        Phase = TradePhase.Applying;
        ApplyStep = TradeApplyStep.HoldA;

        return true;
    }

    internal TradeApplyStep AdvanceApply() {
        if (Phase != TradePhase.Applying || ApplyStep == TradeApplyStep.Done) {
            return ApplyStep;
        }

        ApplyStep++;

        return ApplyStep;
    }

    internal void BeginAbort(TradeError error)
        => AbortError = error;

    internal void End()
        => Phase = TradePhase.Ended;

    internal List<uint> GivenCards(TradeParticipant participant)
        => [.. participant.Offer.OrderBy(x => x.Key).SelectMany(x => Enumerable.Repeat(x.Key, x.Value))];

    internal List<uint> ReceivedCards(TradeParticipant participant)
        => GivenCards(PeerOf(participant));

    internal uint ItemsGained(TradeParticipant participant)
        => (uint) ReceivedCards(participant).Count;

    internal uint ItemsLost(TradeParticipant participant)
        => (uint) GivenCards(participant).Count;

}
