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
 * TRADE ESCROW
 * ========================================================================
 * 
 * PURPOSE:
 * The database steps of a treasure card swap. Each step changes one stored spellbook and the TradeEscrow
 * document in a single save, so a crash between any two steps leaves every card in a spellbook or in the
 * document, never in neither and never in both.
 * 
 * USAGE EXAMPLE:
 * var held = TradeEscrowCollection.Hold(tradeId, wizard.CharId);
 * 
 * NOTE:
 * Every method is idempotent: a repeated call reports AlreadyDone and changes nothing. Hold, Claim and
 * Refund run under the wizard's write lane, so the owning session calls them on its own thread. The
 * escrow document is written by both sessions and the directory, so every save forces a concurrency
 * check and the call re-reads and decides again when it loses. Settle and RecoverAll finish a swap whose
 * participant left or whose server stopped.
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
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Imlight.CoreLib.WizardData.Collections;

public static class TradeEscrowCollection {

    public const string CollectionName = "TradeEscrows";
    private const string IdPrefix = CollectionName + "/";
    private const int MaxAttempts = 3;
    private const int RecoverPageSize = 128;

    private static readonly IDocumentStore s_store;

    static TradeEscrowCollection() => s_store = PlayerDatabase.Instance.Store;

    public static string DocumentId(Guid tradeId) => IdPrefix + tradeId.ToString("D");

    /// <summary>
    /// Reads the escrow document without tracking it.
    /// </summary>
    /// <returns>The document, or null when there is none.</returns>
    public static TradeEscrow Peek(Guid tradeId) {
        using var session = s_store.OpenSession();

        return session.Load<TradeEscrow>(DocumentId(tradeId));
    }

    /// <summary>
    /// Stores the document that fixes both offers. Nothing has left a spellbook yet.
    /// </summary>
    public static EscrowResult Create(Guid tradeId, ulong charIdA, ulong charIdB, IEnumerable<uint> cardsA, IEnumerable<uint> cardsB)
        => Run(tradeId, "Create", () => {
            using var session = s_store.OpenSession();

            var id = DocumentId(tradeId);
            if (session.Load<TradeEscrow>(id) is not null) {
                return EscrowResult.Of(EscrowOutcome.AlreadyDone);
            }

            var escrow = new TradeEscrow {
                TradeId = tradeId,
                CharIdA = charIdA,
                CharIdB = charIdB,
                CardsFromA = [.. cardsA],
                CardsFromB = [.. cardsB],
                CreatedUtc = DateTime.UtcNow,
            };

            // An empty change vector means the document must not exist yet.
            session.Store(escrow, string.Empty, id);
            session.Advanced.GetMetadataFor(escrow)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
            session.SaveChanges();

            return EscrowResult.Of(EscrowOutcome.Done);
        });

    /// <summary>
    /// Moves a side's offered cards out of its stored spellbook and into the escrow document.
    /// </summary>
    public static EscrowResult Hold(Guid tradeId, ulong charId)
        => RunOnWizard(tradeId, charId, "Hold", (escrow, wizard, isA) => {
            if (escrow.Cancelled) {
                return EscrowResult.Of(EscrowOutcome.Cancelled);
            }

            var cards = isA ? escrow.CardsFromA : escrow.CardsFromB;
            if (isA ? escrow.HeldA : escrow.HeldB) {
                return new EscrowResult(EscrowOutcome.AlreadyDone, [.. cards]);
            }

            if (!wizard.SpellbookBehavior.TryRemoveTreasureCards(cards)) {
                return EscrowResult.Of(EscrowOutcome.NotOwned);
            }

            if (isA) {
                escrow.HeldA = true;
            } else {
                escrow.HeldB = true;
            }

            return new EscrowResult(EscrowOutcome.Done, [.. cards]);
        });

    /// <summary>
    /// Gives a side the other side's cards. Only once both sides have held.
    /// </summary>
    public static EscrowResult Claim(Guid tradeId, ulong charId)
        => RunOnWizard(tradeId, charId, "Claim", (escrow, wizard, isA) => {
            if (!escrow.IsCommitted) {
                return EscrowResult.Of(EscrowOutcome.NotCommitted);
            }

            var cards = isA ? escrow.CardsFromB : escrow.CardsFromA;
            var given = isA ? escrow.CardsFromA : escrow.CardsFromB;
            if (isA ? escrow.ClaimedA : escrow.ClaimedB) {
                return new EscrowResult(EscrowOutcome.AlreadyDone, [.. cards]) { Given = [.. given] };
            }

            wizard.SpellbookBehavior.AddTreasureCards(cards);

            if (isA) {
                escrow.ClaimedA = true;
            } else {
                escrow.ClaimedB = true;
            }

            return new EscrowResult(EscrowOutcome.Done, [.. cards]) { Given = [.. given] };
        });

    /// <summary>
    /// Gives a side its own cards back after a cancel. A side that never held has nothing to get back.
    /// </summary>
    public static EscrowResult Refund(Guid tradeId, ulong charId)
        => RunOnWizard(tradeId, charId, "Refund", (escrow, wizard, isA) => {
            if (!escrow.Cancelled) {
                return EscrowResult.Of(EscrowOutcome.NotCancelled);
            }

            if (!(isA ? escrow.HeldA : escrow.HeldB)) {
                return EscrowResult.Of(EscrowOutcome.AlreadyDone);
            }

            var cards = isA ? escrow.CardsFromA : escrow.CardsFromB;
            if (isA ? escrow.RefundedA : escrow.RefundedB) {
                return new EscrowResult(EscrowOutcome.AlreadyDone, [.. cards]) { Given = [.. cards] };
            }

            wizard.SpellbookBehavior.AddTreasureCards(cards);

            if (isA) {
                escrow.RefundedA = true;
            } else {
                escrow.RefundedB = true;
            }

            return new EscrowResult(EscrowOutcome.Done, [.. cards]) { Given = [.. cards] };
        });

    /// <summary>
    /// Cancels a swap that is not committed yet. After this, no side can hold.
    /// </summary>
    public static EscrowResult Cancel(Guid tradeId)
        => Run(tradeId, "Cancel", () => {
            using var session = s_store.OpenSession();

            var escrow = session.Load<TradeEscrow>(DocumentId(tradeId));
            if (escrow is null) {
                return EscrowResult.Of(EscrowOutcome.Missing);
            }

            if (escrow.IsCommitted) {
                return EscrowResult.Of(EscrowOutcome.Committed);
            }

            if (escrow.Cancelled) {
                return EscrowResult.Of(EscrowOutcome.AlreadyDone);
            }

            escrow.Cancelled = true;
            ForceConcurrencyCheck(session, escrow, tradeId);
            session.SaveChanges();

            return EscrowResult.Of(EscrowOutcome.Done);
        });

    /// <summary>
    /// Finishes one side of a swap that its participant or the server left half done: a committed swap is
    /// claimed, any other is cancelled and the side's held cards are refunded. The decision is made
    /// again from the stored document on every attempt.
    /// </summary>
    public static EscrowResult Settle(Guid tradeId, ulong charId) {
        for (var attempt = 0; attempt < MaxAttempts; attempt++) {
            var escrow = Peek(tradeId);
            if (escrow is null) {
                return EscrowResult.Of(EscrowOutcome.Missing);
            }

            if (charId != escrow.CharIdA && charId != escrow.CharIdB) {
                return EscrowResult.Of(EscrowOutcome.NotParticipant);
            }

            if (escrow.IsCommitted) {
                return Claim(tradeId, charId);
            }

            var cancel = Cancel(tradeId);
            if (cancel.Outcome == EscrowOutcome.Committed) {
                continue;
            }

            if (cancel.Outcome is EscrowOutcome.Failed or EscrowOutcome.Missing) {
                return cancel;
            }

            return Refund(tradeId, charId);
        }

        return EscrowResult.Of(EscrowOutcome.Failed);
    }

    /// <summary>
    /// Deletes the document once every card is back in a spellbook.
    /// </summary>
    public static EscrowResult DeleteIfSettled(Guid tradeId)
        => Run(tradeId, "DeleteIfSettled", () => {
            using var session = s_store.OpenSession();

            var id = DocumentId(tradeId);
            var escrow = session.Load<TradeEscrow>(id);
            if (escrow is null) {
                return EscrowResult.Of(EscrowOutcome.Missing);
            }

            if (!escrow.IsSettled) {
                return EscrowResult.Of(EscrowOutcome.NotSettled);
            }

            session.Delete(id, session.Advanced.GetChangeVectorFor(escrow));
            session.SaveChanges();

            return EscrowResult.Of(EscrowOutcome.Done);
        });

    /// <summary>
    /// Settles every swap a previous run left in the database. Runs before any session exists.
    /// </summary>
    /// <returns>The number of documents that are still there afterwards.</returns>
    public static int RecoverAll() {
        try {
            var tradeIds = new List<Guid>();
            using (var session = s_store.OpenSession()) {
                while (true) {
                    var page = session.Advanced.LoadStartingWith<TradeEscrow>(IdPrefix, start: tradeIds.Count, pageSize: RecoverPageSize);
                    tradeIds.AddRange(page.Select(x => x.TradeId));
                    if (page.Length < RecoverPageSize) {
                        break;
                    }
                }
            }

            var remaining = 0;
            foreach (var tradeId in tradeIds) {
                var escrow = Peek(tradeId);
                if (escrow is null) {
                    continue;
                }

                Logger.Warning("Settling trade {TradeId} left over from a previous run.", Logger.Args(tradeId));
                _ = Settle(tradeId, escrow.CharIdA);
                _ = Settle(tradeId, escrow.CharIdB);

                if (DeleteIfSettled(tradeId).Outcome is not (EscrowOutcome.Done or EscrowOutcome.Missing)) {
                    remaining++;
                    Logger.Error("Trade {TradeId} could not be settled and stays in the database.", Logger.Args(tradeId));
                }
            }

            return remaining;
        } catch (Exception ex) {
            Logger.Error("Settling leftover trades failed. {Exception}", Logger.Args(ex));

            return -1;
        }
    }

    private static EscrowResult RunOnWizard(Guid tradeId, ulong charId, string step, Func<TradeEscrow, Wizard, bool, EscrowResult> work)
        => Run(tradeId, step, () => {
            var result = EscrowResult.Of(EscrowOutcome.Missing);
            WizardCollection.UpdateCharacterWithSession(charId, (session, wizard) => {
                var escrow = session.Load<TradeEscrow>(DocumentId(tradeId));
                if (escrow is null) {
                    return false;
                }

                if (charId != escrow.CharIdA && charId != escrow.CharIdB) {
                    result = EscrowResult.Of(EscrowOutcome.NotParticipant);

                    return false;
                }

                result = work(escrow, wizard, charId == escrow.CharIdA);
                if (result.Outcome != EscrowOutcome.Done) {
                    return false;
                }

                ForceConcurrencyCheck(session, escrow, tradeId);

                return true;
            });

            return result;
        });

    private static void ForceConcurrencyCheck(IDocumentSession session, TradeEscrow escrow, Guid tradeId)
        => session.Store(escrow, session.Advanced.GetChangeVectorFor(escrow), DocumentId(tradeId));

    private static EscrowResult Run(Guid tradeId, string step, Func<EscrowResult> attempt) {
        // Runs the idempotent step again when it lost a race or when its outcome is unknown. A save that
        // reached the database but not the caller shows up as AlreadyDone on the next attempt and is
        // reported as Done, so the caller still mirrors it.
        var unknownOutcome = false;
        for (var i = 0; i < MaxAttempts; i++) {
            try {
                var result = attempt();
                if (unknownOutcome && (result.Outcome == EscrowOutcome.AlreadyDone
                    || (step == nameof(DeleteIfSettled) && result.Outcome == EscrowOutcome.Missing))) {
                    return result with { Outcome = EscrowOutcome.Done };
                }

                return result;
            } catch (ConcurrencyException) {
                Logger.Debug("Trade {TradeId} step {Step} lost a race, trying again.", Logger.Args(tradeId, step));
            } catch (Exception ex) {
                unknownOutcome = true;
                Logger.Error("Trade {TradeId} step {Step} failed. {Exception}", Logger.Args(tradeId, step, ex));
            }
        }

        Logger.Error("Trade {TradeId} step {Step} gave up after {Attempts} attempts.", Logger.Args(tradeId, step, MaxAttempts));

        return EscrowResult.Of(EscrowOutcome.Failed);
    }

}
