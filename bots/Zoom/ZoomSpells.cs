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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Zoom's spellbook and deck (Niduus, 2026-11-22: "give Zoom all vanilla spells for storm and enchants. His main tactic should
/// either be temp + Epic to clear or Glowbugs + Epic to clear"). His book holds every Storm spell and every enchantment a
/// player can have at the top level (no dev, monster or boss spells); fighting spells of the other schools he picked up
/// training come out. His deck is his tactic: Epic, Tempest and Glowbug Squall as many copies as the deck allows, then his
/// biggest Storm hits. The game's spell list is read in the background after a bots update (up to several minutes), so this
/// is checked again every half minute until it is done, instead of giving up when he logs in (his log, 2026-11-22 00:49:
/// "his spells were not read in time", so he kept the 31 spells he had trained and Ultimus's deck).
/// </summary>
internal static class ZoomSpells {

    internal const string EPIC = "Epic";
    private static readonly string[] s_stormClearers = ["Tempest", "Glowbug Squall"];
    private static readonly Dictionary<MagicSchool, string[]> s_clearers = [];

    /// <summary>
    /// The scout's two clearing spells, best first. Zoom (Storm): Tempest and Glowbug Squall (Niduus, 2026-11-22). Any other
    /// school's scout (Savitar, Fire): the two hardest-hitting all-enemy spells of his school a player can train.
    /// </summary>
    internal static IReadOnlyList<string> Clearers {
        get {
            var school = UltimusSeat.Current.School;
            if (school == MagicSchool.Storm) {
                return s_stormClearers;
            }

            lock (s_clearers) {
                if (s_clearers.TryGetValue(school, out var known)) {
                    return known;
                }

                if (!TrueFormPool.Ready) {
                    return [];
                }

                var picked = Wanted().Where(e => Mine(e) && e.Card is { Hit: true, HitsAll: true, PerPip: 0 })
                                     .OrderByDescending(Damage).ThenByDescending(e => e.Level).Take(2).Select(e => e.Shown.Trim()).ToArray();
                if (picked.Length > 0) {
                    s_clearers[school] = picked;
                }

                return picked;
            }
        }
    }

    internal static string DeckWords => $"his tactic: Epic on {string.Join(" or ", Clearers.DefaultIfEmpty("his biggest all-enemy hit"))} to clear";

    private static int Top => Math.Clamp(MagicLevelsConfig.MaxLevel, 1, 255);

    private static bool IsEnchant(uint id) => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t && CombatEnchantments.IsEnchantment(t);

    // A spell of the scout's own school (Zoom: Storm; Savitar: Fire).
    private static bool Mine(TrueFormPool.Entry e) => e.School.Equals(UltimusSeat.Current.School.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool Storm(TrueFormPool.Entry e) => Mine(e);

    // His spells: every Storm spell and every enchantment a player can train at the top level.
    private static List<TrueFormPool.Entry> Wanted()
        => [.. TrueFormPool.Versions(Top, dev: false)
            .Where(e => !e.Dev && !e.Treasure && !TrueFormPool.NotForPlayers(e.Id) && !BotCards.NotVanilla(e.Card)
                        && SpellFactory.GetSpell(e.Id) is not null && (Storm(e) || IsEnchant(e.Id)))
            .GroupBy(e => e.Shown.Trim(), StringComparer.OrdinalIgnoreCase).Select(OnePerName)];

    // One card per spell name (Niduus's panel, 01:43: five Hurricanes, five Storm Lords, the game's other copies of the same
    // spell): the one the spell list puts first for the top level, else the highest level, else the lowest id.
    // Niduus, 2026-11-22: "Just make it only 1 copy of each that does the highest damage": the version that hits hardest
    // (an enchantment: adds the most), then the one the spell list puts first, then the highest level.
    private static TrueFormPool.Entry OnePerName(IEnumerable<TrueFormPool.Entry> same) {
        var heads = TrueFormPool.At(Top, dev: false).Select(e => e.Id).ToHashSet();
        return same.OrderByDescending(Damage).ThenByDescending(e => heads.Contains(e.Id)).ThenByDescending(e => e.Level).ThenBy(e => e.Id).First();
    }

    private static int Damage(TrueFormPool.Entry e)
        => Math.Max(Math.Max(e.Card?.BaseDamage ?? 0, e.Card?.Tiers?.Select(t => t.Damage).DefaultIfEmpty(0).Max() ?? 0), BotEnchants.Of(e.Id)?.Amount ?? 0);

    // His treasure cards: every enchantment and every Storm card a player can have (no dev, monster or boss cards).
    private static List<TrueFormPool.Entry> WantedCards()
        => [.. TrueFormPool.Versions(Top, dev: false)
            .Where(e => !e.Dev && e.Treasure && !TrueFormPool.NotForPlayers(e.Id) && !BotCards.NotVanilla(e.Card)
                        && SpellFactory.GetSpell(e.Id) is not null && (Storm(e) || IsEnchant(e.Id)))
            .GroupBy(e => e.Shown.Trim(), StringComparer.OrdinalIgnoreCase).Select(OnePerName)];

    private static bool IsTreasure(uint id) => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate { m_Treasure: true };

    // The best damage enchantment first: Epic, then by how much it adds.
    private static int EnchantOrder(TrueFormPool.Entry e)
        => e.Shown.Equals(EPIC, StringComparison.OrdinalIgnoreCase) ? int.MaxValue : BotEnchants.Of(e.Id) is { Kind: BotEnchants.DAMAGE } d ? d.Amount : 0;

    /// <summary>
    /// Makes his spellbook exactly what Niduus asked (2026-11-22: "I only wanted ALL VANILLA enchants and ALL VANILLA STORM. NOT
    /// EVERY VANILLA CARD"): trained spells are every Storm spell and enchantment and nothing else (every other spell he has,
    /// other schools, fishing, potions, comes out); treasure cards are enchantments and Storm cards only, every other one
    /// comes out (True Form's maker gave him every card in the game before, 01:12: 570 spells, 285 cards). Then his deck is
    /// written: Epic, Tempest and Glowbug Squall at the most copies, his biggest Storm hits, and Epic (then the next best
    /// damage enchantments) in the treasure card slots. False while the game's spells are still being read.
    /// </summary>
    internal static bool Ensure(Wizard wizard) {
        if (wizard?.SpellbookBehavior is not { } book || !TrueFormPool.Ready) {
            return false;
        }

        try {
            var wanted = Wanted();
            if (wanted.Count == 0) {
                return false;
            }

            var keep = wanted.Select(e => e.Id).ToHashSet();
            var cards = WantedCards();
            var keepCards = cards.Select(e => e.Id).ToHashSet();
            var learned = (book.LearnedSpellTemplateIds ?? []).ToList();
            var add = keep.Where(id => !learned.Contains(id)).ToList();
            var drop = learned.Where(id => !keep.Contains(id)).ToHashSet();
            var ownedCards = book.TreasureCardTemplateIds ?? [];
            var dropCards = ownedCards.Where(id => !keepCards.Contains(id)).ToList();
            // Two of each enchantment card in his book (Epic: enough for his deck), one of each Storm card.
            var giveCards = new List<uint>();
            foreach (var card in cards) {
                var want = card.Shown.Equals(EPIC, StringComparison.OrdinalIgnoreCase) ? 40 : IsEnchant(card.Id) ? 2 : 1;
                var have = ownedCards.Count(id => id == card.Id);
                giveCards.AddRange(Enumerable.Repeat(card.Id, Math.Max(0, want - have)));
            }

            book.LearnedSpellTemplateIds ??= [];
            book.LearnedSpellTemplateIds.RemoveAll(drop.Contains);
            book.LearnedSpellTemplateIds.AddRange(add);
            book.TreasureCardTemplateIds ??= [];
            book.TreasureCardTemplateIds.RemoveAll(id => !keepCards.Contains(id));
            book.TreasureCardTemplateIds.AddRange(giveCards);
            WizardCollection.UpdateCharacter(wizard.CharId, saved => {
                var list = saved.SpellbookBehavior.LearnedSpellTemplateIds ??= [];
                list.RemoveAll(id => !keep.Contains(id));
                var known = list.ToHashSet();
                list.AddRange(add.Where(known.Add));
                var savedCards = saved.SpellbookBehavior.TreasureCardTemplateIds ??= [];
                savedCards.RemoveAll(id => !keepCards.Contains(id));
                savedCards.AddRange(giveCards);
            });

            WriteDeck(wizard, wanted, cards);
            var found = (string name) => wanted.Concat(cards).Any(e => e.Shown.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "in" : "NOT FOUND";
            Logger.Information("{9}: his spellbook is {0} {10} spells and {1} enchantments, nothing else ({2} added, {3} other spells taken out); "
                               + "treasure cards: {4} enchantment and {10} cards ({5} others taken out); clearers {11} {6} / {7}, Epic {8}.",
                Logger.Args(wanted.Count(Storm), wanted.Count(e => IsEnchant(e.Id)), add.Count, drop.Count, cards.Count, dropCards.Count,
                    found(Clearers.ElementAtOrDefault(0) ?? "-"), found(Clearers.ElementAtOrDefault(1) ?? "-"), found(EPIC),
                    UltimusSeat.Current.Name, UltimusSeat.Current.School, string.Join(", ", Clearers)));
            return true;
        }
        catch (Exception e) {
            Logger.Warning("{0}: could not fill his spellbook: {1}", Logger.Args(UltimusSeat.Current.Name, e.Message));
            return false;
        }
    }

    // His deck written straight to his deck item, as True Form's is (TrueFormSpells.FillDeck): the spells from Deck, and the
    // treasure card slots Epic first, then the next best damage enchantments; every other card comes out.
    private static void WriteDeck(Wizard wizard, List<TrueFormPool.Entry> wanted, List<TrueFormPool.Entry> cards) {
        if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck) is not { } deckItem
            || !CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deck)
            || ItemHelper.GetItemTemplate(deckItem)?.m_behaviors?.OfType<DeckBehaviorTemplate>().FirstOrDefault() is not { } rules) {
            Logger.Warning("Zoom: he wears no deck to fill.");
            return;
        }

        var book = wizard.SpellbookBehavior;
        var spells = Deck(wizard, Math.Max(0, (int) rules.m_maxSpells), Math.Max(1, (int) rules.m_schoolMaxInstances), Math.Max(1, (int) rules.m_genericMaxInstances)) ?? [];
        var cardRoom = Math.Max(0, (int) rules.m_maxTreasureCards);
        var tcs = new Dictionary<uint, int>();
        foreach (var card in cards.Where(c => BotEnchants.Of(c.Id) is { Kind: BotEnchants.DAMAGE }).OrderByDescending(EnchantOrder)) {
            var n = Math.Min(cardRoom - tcs.Values.Sum(), card.Shown.Equals(EPIC, StringComparison.OrdinalIgnoreCase) ? cardRoom : 4);
            if (n > 0) {
                tcs[card.Id] = n;
            }
        }

        // Treasure cards going into the deck leave his book; those coming out of it go back.
        var oldCards = (deck.m_spellList ?? []).Where(s => IsTreasure(s.m_templateID)).ToList();
        var leaving = oldCards.Where(s => cards.Any(c => c.Id == s.m_templateID))
            .SelectMany(s => Enumerable.Repeat(s.m_templateID, Math.Max(0, (int) s.m_quantity - tcs.GetValueOrDefault(s.m_templateID)))).ToList();
        var coming = tcs.SelectMany(t => Enumerable.Repeat(t.Key, Math.Max(0, t.Value - (int) (oldCards.FirstOrDefault(s => s.m_templateID == t.Key)?.m_quantity ?? 0)))).ToList();
        book.TreasureCardTemplateIds ??= [];
        book.TreasureCardTemplateIds.AddRange(leaving);
        foreach (var id in coming) {
            book.TreasureCardTemplateIds.Remove(id);
        }

        WizardCollection.UpdateCharacter(wizard.CharId, saved => {
            var list = saved.SpellbookBehavior.TreasureCardTemplateIds ??= [];
            list.AddRange(leaving);
            foreach (var id in coming) {
                list.Remove(id);
            }
        });

        deck.m_spellList = [.. spells.Concat(tcs).Select(x => new SpellData { m_templateID = x.Key, m_enchantment = 0, m_quantity = (uint) x.Value })];
        if (book is Imlight.CoreLib.Shared.Behaviors.ServerWizSpellbookBehavior wiz) {
            wiz.EquipDeck(ItemHelper.GetItemTemplate(deckItem), deck);
        }

        WizardItemCollection.SaveItemBehaviors(deckItem);
        Logger.Information("Zoom: his deck: {0}; treasure cards: {1}.",
            Logger.Args(string.Join(", ", spells.Select(x => $"{TrueFormPool.Of(x.Key)?.Shown ?? x.Key.ToString()} x{x.Value}")),
                string.Join(", ", tcs.Select(x => $"{TrueFormPool.Of(x.Key)?.Shown ?? x.Key.ToString()} x{x.Value}"))));
    }

    /// <summary>
    /// His deck, by spell and copies: Epic, Tempest and Glowbug Squall as many as the deck allows, then his biggest Storm hits
    /// (one of each), then his other Storm spells, until it is full. Only spells in his book. Null while the spells are read.
    /// </summary>
    internal static Dictionary<uint, int> Deck(Wizard wizard, int room, int schoolMost, int genericMost) {
        if (!TrueFormPool.Ready || wizard?.SpellbookBehavior is not { } book || room <= 0) {
            return null;
        }

        var learned = (book.LearnedSpellTemplateIds ?? []).Select(id => (uint) id).ToHashSet();
        var mine = Wanted().Where(e => learned.Contains(e.Id)).ToList();
        var deck = new Dictionary<uint, int>();
        void Put(TrueFormPool.Entry e, int copies) {
            if (e is null || deck.ContainsKey(e.Id)) {
                return;
            }

            var most = Storm(e) ? schoolMost : genericMost;
            var n = Math.Min(Math.Min(copies, Math.Max(1, most)), room - deck.Values.Sum());
            if (n > 0) {
                deck[e.Id] = n;
            }
        }

        TrueFormPool.Entry Named(string name) => mine.Where(e => e.Shown.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Level).FirstOrDefault();

        // The best damage enchantment when the game has no Epic.
        Put(Named(EPIC) ?? mine.Where(e => BotEnchants.Of(e.Id) is { Kind: BotEnchants.DAMAGE }).OrderByDescending(e => BotEnchants.Of(e.Id).Amount).FirstOrDefault(), 99);
        foreach (var name in Clearers) {
            Put(Named(name), 99);
        }

        foreach (var hit in mine.Where(e => Storm(e) && e.Card.Hit && e.Card.PerPip == 0).OrderByDescending(e => e.Card.HitsAll).ThenByDescending(e => e.Card.BaseDamage)) {
            Put(hit, 1);
        }

        foreach (var other in mine.Where(e => Storm(e)).OrderByDescending(e => e.Level)) {
            Put(other, 1);
        }

        return deck;
    }

}
