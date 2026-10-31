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
 * COMBAT SPELL DECK MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the drawing, discarding, and tracking of spell cards during combat,
 * providing randomized card selection from available spells.
 * 
 * USAGE EXAMPLE:
 * var deck = new CombatDeck(spellDatas, handSize);
 * Hand newHand = deck.GetHand();
 * deck.Discard(spell);
 * 
 * NOTE:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Manages a participant's spell deck during combat, including drawing cards and handling discards.
/// </summary>
internal class CombatDeck {

    internal List<Spell> LastGivenHand { get; private set; }
    internal int TotalCardCount => (int) _spellData.Sum(s => s.Quantity);
    internal int RemainingCardCount => (int) _usedUpSpellData.Sum(s => s.Quantity);
    internal int VaultTotalCount => (int) _treasureVault.Sum(s => s.Quantity);
    internal int VaultRemainingCount => (int) _treasureVaultUsed.Sum(s => s.Quantity);
    internal int TreasureCardsInHand { get; private set; }

    private readonly List<CombatDeckSpellData> _spellData;
    private readonly List<CombatDeckSpellData> _treasureVault;
    private readonly byte _handSize;

    // A cheat's hand size and for how many more hands (-1: the rest of the battle; 0: none).
    internal int CheatHandSize;
    internal int CheatHandRounds;
    private readonly List<CombatDeckSpellData> _usedUpSpellData;
    private readonly List<CombatDeckSpellData> _treasureVaultUsed;
    private readonly List<Spell> _cardsDiscardedThisTurn;
    // Cards put in the hand by .draw: they never came from the deck, so using them must not use up a deck copy.
    // Spells are records, so two copies of one card are "equal": every list here goes by the card itself, not its value.
    private readonly HashSet<Spell> _extraCards = new(ReferenceEqualityComparer.Instance);

    // Takes this very card out of the list. List.Remove took the first equal one, so with two copies of a card in hand,
    // playing one and drawing again took the other one too: a card packed several times showed up only once
    // (Niduus, 2026-10-31, "happens a lot with prisms").
    private static bool RemoveSame(List<Spell> cards, Spell spell) {
        var index = cards.FindIndex(x => ReferenceEquals(x, spell));
        if (index < 0) {
            return false;
        }

        cards.RemoveAt(index);

        return true;
    }

    private static bool HasSame(List<Spell> cards, Spell spell) => cards.Exists(x => ReferenceEquals(x, spell));

    // A random deck entry, weighted by its copies left, as the game draws: a card packed four times is four times as
    // likely as one packed once. It used to pick each different spell alike, so extra copies hardly ever came up.
    private static int WeightedPick(List<CombatDeckSpellData> entries) {
        var total = entries.Sum(e => (long) Math.Max(0, (int) e.Quantity));
        if (total <= 0) {
            return Random.Shared.Next(0, entries.Count);
        }

        var pick = Random.Shared.NextInt64(total);
        for (var i = 0; i < entries.Count; i++) {
            pick -= Math.Max(0, (int) entries[i].Quantity);
            if (pick < 0) {
                return i;
            }
        }

        return entries.Count - 1;
    }

    // ctor
    internal CombatDeck(List<CombatDeckSpellData> spellDatas, List<CombatDeckSpellData> treasureVault, byte handSize) {
        this._spellData = spellDatas;
        this._treasureVault = treasureVault ?? [];
        this._handSize = handSize;
        this.LastGivenHand = [];
        this._cardsDiscardedThisTurn = [];
        this.TreasureCardsInHand = 0;

        // Clone the spell data into used up spell data.
        this._usedUpSpellData = [];
        foreach (var originalSpellData in _spellData) {
            _usedUpSpellData.Add(new CombatDeckSpellData() {
                TemplateId = originalSpellData.TemplateId,
                Quantity = originalSpellData.Quantity,
                IsBattleCard = originalSpellData.IsBattleCard,
                IsItemCard = originalSpellData.IsItemCard,
                IsTreasureCard = false
            });
        }

        // Clone the treasure vault.
        this._treasureVaultUsed = [];
        foreach (var vaultCard in _treasureVault) {
            _treasureVaultUsed.Add(new CombatDeckSpellData() {
                TemplateId = vaultCard.TemplateId,
                Quantity = vaultCard.Quantity,
                IsBattleCard = vaultCard.IsBattleCard,
                IsItemCard = vaultCard.IsItemCard,
                IsTreasureCard = true
            });
        }
    }

    /// <summary>
    /// Gets a new hand of spells, discarding any used or discarded cards.
    /// </summary>
    /// <returns>A new hand of spells.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a spell cannot be created from the template id.</exception>
    internal Hand GetHand() {
        var newCards = new List<Spell>();
        // Discard the cards that were used up or discarded.
        foreach (var spell in _cardsDiscardedThisTurn) {
            RemoveSame(LastGivenHand, spell);
            if (_extraCards.Remove(spell)) {
                continue;
            }

            var spellData = _usedUpSpellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID);
            if (spellData == null) {
                // The spell may not be in this list if the previous hand used them all.
                continue;
            }

            // Decrement the quantity of the spell, or remove it if the quantity is 0.
            if (spellData.Quantity - 1 <= 0) {
                _usedUpSpellData.Remove(spellData);
            }
            else {
                spellData.Quantity--;
            }
        }
        _cardsDiscardedThisTurn.Clear();

        // Refill the hand with new cards (up to a cheat's hand size while it lasts: HandSize, CombatResolver.ExtraCheats.cs).
        var size = (int) _handSize;
        if (CheatHandRounds != 0) {
            size = Math.Clamp(CheatHandSize, 0, 7);
            if (CheatHandRounds > 0) {
                CheatHandRounds--;
            }
        }

        var cardsToRefill = size - LastGivenHand.Count;
        for (var i = 0; i < cardsToRefill; i++) {
            if (RemainingCardCount <= 0) {
                break; // No more spells available.
            }

            var randomIndex = WeightedPick(_usedUpSpellData);
            var spellData = _usedUpSpellData[randomIndex];
            var spellTemplateId = spellData.TemplateId;

            // Create a new spell from the template id
            var spell = SpellFactory.GetSpell(spellTemplateId)
                ?? throw new InvalidOperationException("Spell could not be created from template id.");
            spell.m_itemCard = spellData.IsItemCard;
            spell.m_battleCard = spellData.IsBattleCard;

            newCards.Add(spell);

            // Decrement the quantity of the spell, or remove it if the quantity is 0.
            if (spellData.Quantity - 1 <= 0) {
                _usedUpSpellData.RemoveAt(randomIndex);
            }
            else {
                spellData.Quantity--;
            }
        }

        // Update the LastGivenHand.
        LastGivenHand.AddRange(newCards);

        return new Hand() { m_spellList = LastGivenHand };
    }

    /// <summary>
    /// Discards a spell from the current hand. Treasure cards are returned to the vault.
    /// </summary>
    /// <param name="spell">The spell to discard.</param>
    internal void Discard(Spell spell) {
        if (spell.m_treasureCard) {
            ReturnToVault(spell);
        }
        else {
            _cardsDiscardedThisTurn.Add(spell);

            // Free the slot now so a vault draw can be made this turn; GetHand's removal is a no-op.
            RemoveSame(LastGivenHand, spell);
        }
    }

    /// <summary>
    /// Consumes a card from hand without returning it to the vault
    /// </summary>
    internal void ConsumeCard(Spell spell) {
        if (spell.m_treasureCard) {
            ConsumeFromVault(spell);
            return;
        }

        _cardsDiscardedThisTurn.Add(spell);
        RemoveSame(LastGivenHand, spell);
    }

    /// <summary>
    /// Empties the current hand without touching the deck or the vault.
    /// </summary>
    internal void ClearHand() {
        LastGivenHand.Clear();
        _cardsDiscardedThisTurn.Clear();
        _extraCards.Clear();
        TreasureCardsInHand = 0;
    }

    /// <summary>
    /// Puts the unplayed cards of the current hand back into the deck, so the next draw is a whole new hand.
    /// Cards that never came from the deck (.draw, treasure cards) are dropped.
    /// </summary>
    internal void ReturnHandToDeck() {
        foreach (var spell in LastGivenHand) {
            if (_extraCards.Contains(spell) || spell.m_treasureCard || HasSame(_cardsDiscardedThisTurn, spell)) {
                continue;
            }

            var spellData = _usedUpSpellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID);
            if (spellData is not null) {
                spellData.Quantity++;
                continue;
            }

            // Only cards of the deck go back: one added straight to the hand has no deck entry.
            var original = _spellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID);
            if (original is not null) {
                _usedUpSpellData.Add(new CombatDeckSpellData() {
                    TemplateId = original.TemplateId,
                    Quantity = 1,
                    IsBattleCard = original.IsBattleCard,
                    IsItemCard = original.IsItemCard,
                    IsTreasureCard = false
                });
            }
        }

        LastGivenHand.Clear();
        _extraCards.Clear();
        TreasureCardsInHand = 0;
    }

    /// <summary>
    /// Takes one card out of the hand and puts it back into the deck, unplayed (a cheat's Lock). A card that never came
    /// from the deck (.draw, a treasure card) just leaves the hand.
    /// </summary>
    internal void ReturnCardToDeck(Spell spell) {
        if (!RemoveSame(LastGivenHand, spell)) {
            return;
        }

        if (spell.m_treasureCard) {
            LastGivenHand.Add(spell);
            ReturnToVault(spell);

            return;
        }

        if (_extraCards.Remove(spell)) {
            return;
        }

        var spellData = _usedUpSpellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID);
        if (spellData is not null) {
            spellData.Quantity++;
        }
        else if (_spellData.FirstOrDefault(s => s.TemplateId == spell.m_templateID) is { } original) {
            _usedUpSpellData.Add(new CombatDeckSpellData() {
                TemplateId = original.TemplateId, Quantity = 1, IsBattleCard = original.IsBattleCard, IsItemCard = original.IsItemCard,
                IsTreasureCard = false,
            });
        }
    }

    /// <summary>
    /// Takes a card out of the hand as used up, for a cheat's Discard: a deck card counts as played (it was taken from the
    /// deck when drawn), a treasure card goes back to the vault and an extra card is just gone.
    /// </summary>
    internal void DropFromHand(Spell spell) {
        if (spell.m_treasureCard) {
            ReturnToVault(spell);

            return;
        }

        if (RemoveSame(LastGivenHand, spell)) {
            _extraCards.Remove(spell);
        }
    }

    /// <summary>
    /// Fills the empty places of the hand from the deck with only the cards <paramref name="allowed"/> lets in (a cheat's
    /// Lock: the hand is still a whole hand, without the locked cards).
    /// </summary>
    internal void FillHandFrom(Func<uint, bool> allowed) {
        while (HandRoom > 0) {
            var choices = _usedUpSpellData.Where(s => s.Quantity > 0 && allowed(s.TemplateId)).ToList();
            if (choices.Count == 0) {
                return;
            }

            var pick = Random.Shared.Next(choices.Sum(c => (int) c.Quantity));
            var spellData = choices.First(c => (pick -= (int) c.Quantity) < 0);
            if (SpellFactory.GetSpell(spellData.TemplateId) is not { } spell) {
                return;
            }

            spell.m_itemCard = spellData.IsItemCard;
            spell.m_battleCard = spellData.IsBattleCard;
            LastGivenHand.Add(spell);
            if (--spellData.Quantity == 0) {
                _usedUpSpellData.Remove(spellData);
            }
        }
    }

    /// <summary>
    /// A fusion (SpellFusions.cs): both cards are used up and the fused card, which is not part of the deck, takes the
    /// place of the first one in the hand.
    /// </summary>
    internal void Fuse(Spell first, Spell second, Spell fused) {
        var at = LastGivenHand.FindIndex(x => ReferenceEquals(x, first));
        var secondAt = LastGivenHand.FindIndex(x => ReferenceEquals(x, second));
        if (secondAt >= 0 && secondAt < at) {
            at--;
        }

        ConsumeCard(first);
        ConsumeCard(second);
        _extraCards.Add(fused);
        LastGivenHand.Insert(Math.Clamp(at, 0, LastGivenHand.Count), fused);
    }

    /// <summary>
    /// Adds a spell directly to the current hand without drawing from the deck.
    /// </summary>
    internal void AddCardToHand(Spell spell) {
        LastGivenHand.Add(spell);
    }

    /// <summary>
    /// How many more cards fit in the current hand.
    /// </summary>
    internal int HandRoom => Math.Max(0, _handSize - LastGivenHand.Count);

    /// <summary>
    /// Adds a card that is not part of the deck to the current hand (.draw).
    /// </summary>
    internal void AddExtraCardToHand(Spell spell) {
        _extraCards.Add(spell);
        LastGivenHand.Add(spell);
    }

    /// <summary>
    /// Draws a random treasure card from the vault and adds it to the current hand.
    /// </summary>
    /// <returns>The drawn spell, or null if the vault is empty or hand is full.</returns>
    internal Spell DrawFromVault() {
        if (VaultRemainingCount <= 0) {
            return null;
        }

        if (LastGivenHand.Count >= _handSize) {
            return null;
        }

        // Player must have more total cards available than hand size to draw from vault.
        // This prevents drawing from the sideboard when the regular deck still has cards.
        var totalAvailableCards = RemainingCardCount + VaultRemainingCount + LastGivenHand.Count;
        if (totalAvailableCards <= _handSize) {
            return null;
        }

        var randomIndex = WeightedPick(_treasureVaultUsed);
        var vaultData = _treasureVaultUsed[randomIndex];

        var spell = SpellFactory.GetSpell(vaultData.TemplateId);
        if (spell == null) {
            return null;
        }

        spell.m_treasureCard = true;
        LastGivenHand.Add(spell);
        TreasureCardsInHand++;

        // Decrement or remove from vault.
        if (vaultData.Quantity - 1 <= 0) {
            _treasureVaultUsed.RemoveAt(randomIndex);
        }
        else {
            vaultData.Quantity--;
        }

        return spell;
    }

    /// <summary>
    /// Returns a treasure card to the vault (e.g., on discard or fizzle).
    /// </summary>
    internal void ReturnToVault(Spell spell) {
        if (!spell.m_treasureCard) {
            return;
        }

        // Remove from hand.
        RemoveSame(LastGivenHand, spell);
        if (TreasureCardsInHand > 0) {
            TreasureCardsInHand--;
        }

        // Return to vault used list.
        var existing = _treasureVaultUsed.Find(v => v.TemplateId == spell.m_templateID);
        if (existing != null) {
            existing.Quantity++;
        }
        else {
            _treasureVaultUsed.Add(new CombatDeckSpellData {
                TemplateId = spell.m_templateID,
                Quantity = 1,
                IsTreasureCard = true
            });
        }
    }

    /// <summary>
    /// Permanently consumes a successfully cast treasure card from the vault.
    /// Returns the template ID so the caller can persist the removal.
    /// </summary>
    internal uint ConsumeFromVault(Spell spell) {
        if (!spell.m_treasureCard) {
            return 0;
        }

        // Remove from hand.
        RemoveSame(LastGivenHand, spell);
        if (TreasureCardsInHand > 0) {
            TreasureCardsInHand--;
        }

        // Remove from the persistent vault list (not the used copy; this removes it permanently).
        var vaultEntry = _treasureVault.Find(v => v.TemplateId == spell.m_templateID);
        if (vaultEntry != null) {
            if (vaultEntry.Quantity - 1 <= 0) {
                _treasureVault.Remove(vaultEntry);
            }
            else {
                vaultEntry.Quantity--;
            }
        }

        return spell.m_templateID;
    }

    /// <summary>
    /// Reshuffles the deck, resetting the used up spell data and clearing the discarded cards.
    /// Vault cards are NOT returned to the deck on reshuffle.
    /// </summary>
    internal void Reshuffle() {
        // Copy spell data back to used up spell data.
        _usedUpSpellData.Clear();
        foreach (var originalSpellData in _spellData) {
            _usedUpSpellData.Add(new CombatDeckSpellData() {
                TemplateId = originalSpellData.TemplateId,
                Quantity = originalSpellData.Quantity,
                IsBattleCard = originalSpellData.IsBattleCard,
                IsItemCard = originalSpellData.IsItemCard,
                IsTreasureCard = false
            });
        }

        _cardsDiscardedThisTurn.Clear();
    }
    
}

