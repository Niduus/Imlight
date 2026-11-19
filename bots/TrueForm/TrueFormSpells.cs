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
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's spellbook, deck and treasure cards (Niduus, 2026-11-19: "give him trained spells across the game"; his deck
/// window showed 1 trained spell and an empty deck). In battle he picks from every spell anyway (TrueFormBattle), but now he
/// owns them too, as a player would:
/// <list type="bullet">
/// <item>Trained: every spell of his pool a trainer or quest teaches (TrueFormPool, one per family, every school).</item>
/// <item>Treasure cards: one of each spell that only comes as a treasure card.</item>
/// <item>Deck: one copy each, his own school's spells first (Myth, or the school his group asked for), then the Sun, Moon, Star and Shadow ones, then the other schools',
/// strongest (most pips) first, as many as the deck holds and its rank rules allow; its treasure card slots filled too.</item>
/// </list>
/// Done when he is made ready, after he is dressed (the deck he wears sets how many cards it holds). Kept to his own top level.
/// </summary>
internal static class TrueFormSpells {

    private const string SPELLS_FILE = "spells.txt";

    private static readonly string[] s_astral = ["Sun", "Moon", "Star", "Shadow"];

    internal static void Give(Wizard wizard, int level, string school, BotStyle style = BotStyle.Backup) {
        try {
            for (var i = 0; i < 240 && !TrueFormPool.Ready; i++) {
                System.Threading.Thread.Sleep(500);
            }

            if (!TrueFormPool.Ready) {
                Logger.Warning("True Form: his spells were not read in time; his spellbook stays as it is.");
                return;
            }

            var pool = TrueFormPool.At(level, dev: false);
            var trained = pool.Where(e => !e.Treasure && SpellFactory.GetSpell(e.Id) is not null).ToList();
            var treasure = pool.Where(e => e.Treasure && SpellFactory.GetSpell(e.Id) is not null).ToList();
            var book = wizard.SpellbookBehavior;

            // Monster and boss versions he got before are taken away (Niduus, 2026-11-19: "Cards like this are a nono").
            var badLearned = (book.LearnedSpellTemplateIds ?? []).Where(TrueFormPool.NotForPlayers).ToHashSet();
            var badCards = (book.TreasureCardTemplateIds ?? []).Where(TrueFormPool.NotForPlayers).ToHashSet();
            if (badLearned.Count > 0 || badCards.Count > 0) {
                book.LearnedSpellTemplateIds?.RemoveAll(badLearned.Contains);
                book.TreasureCardTemplateIds?.RemoveAll(badCards.Contains);
                WizardCollection.UpdateCharacter(wizard.CharId, saved => {
                    saved.SpellbookBehavior.LearnedSpellTemplateIds?.RemoveAll(TrueFormPool.NotForPlayers);
                    saved.SpellbookBehavior.TreasureCardTemplateIds?.RemoveAll(TrueFormPool.NotForPlayers);
                });
                Logger.Information("True Form: took away {0} monster or boss spells and {1} such treasure cards.", Logger.Args(badLearned.Count, badCards.Count));
            }

            // Trained spells.
            var learned = (book.LearnedSpellTemplateIds ?? []).ToHashSet();
            var newSpells = trained.Select(e => e.Id).Where(id => !learned.Contains(id)).Distinct().ToList();
            // Treasure cards he has none of (in his book or deck).
            var inDeckNow = DeckBehaviorOf(wizard)?.m_spellList ?? [];
            var ownedCards = (book.TreasureCardTemplateIds ?? []).Concat(inDeckNow.Select(s => s.m_templateID)).ToHashSet();
            var newCards = treasure.Select(e => e.Id).Where(id => !ownedCards.Contains(id)).Distinct().ToList();
            if (newSpells.Count > 0 || newCards.Count > 0) {
                book.LearnedSpellTemplateIds ??= [];
                book.LearnedSpellTemplateIds.AddRange(newSpells);
                book.TreasureCardTemplateIds ??= [];
                book.TreasureCardTemplateIds.AddRange(newCards);
                WizardCollection.UpdateCharacter(wizard.CharId, saved => {
                    var known = (saved.SpellbookBehavior.LearnedSpellTemplateIds ??= []).ToHashSet();
                    saved.SpellbookBehavior.LearnedSpellTemplateIds.AddRange(newSpells.Where(known.Add));
                    (saved.SpellbookBehavior.TreasureCardTemplateIds ??= []).AddRange(newCards);
                });
                Logger.Information("True Form: learned {0} spells and got {1} treasure cards.", Logger.Args(newSpells.Count, newCards.Count));
            }

            FillDeck(wizard, trained, treasure, school, style);
            WriteList(wizard, trained, treasure);
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not give him his spells: {0}", Logger.Args(e.Message));
        }
    }

    private static DeckBehavior DeckBehaviorOf(Wizard wizard)
        => wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck) is { } deck && CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deck, out var behavior)
            ? behavior : null;

    // His deck: one of each, best first, as many as it holds; the treasure card slots from his book.
    private static void FillDeck(Wizard wizard, List<TrueFormPool.Entry> trained, List<TrueFormPool.Entry> treasure, string school, BotStyle style) {
        if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck) is not { } deckItem
            || !CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deck)
            || ItemHelper.GetItemTemplate(deckItem)?.m_behaviors?.OfType<DeckBehaviorTemplate>().FirstOrDefault() is not { } rules) {
            Logger.Warning("True Form: he wears no deck to fill.");
            return;
        }

        var myth = string.Equals((string) rules.m_primarySchoolName, school, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty((string) rules.m_primarySchoolName);
        bool Fits(TrueFormPool.Entry e) {
            var own = e.School.Equals(school, StringComparison.OrdinalIgnoreCase);
            var most = own && myth ? rules.m_schoolMaxRank : rules.m_genericMaxRank;
            return most <= 0 || Rank(e.Id) <= most;
        }

        int Order(TrueFormPool.Entry e) => e.School.Equals(school, StringComparison.OrdinalIgnoreCase) ? 0 : s_astral.Contains(e.School, StringComparer.OrdinalIgnoreCase) ? 1 : 2;

        var spells = Mixed(trained.Where(Fits).OrderBy(Order).ThenByDescending(e => Rank(e.Id)).ThenBy(e => e.Shown, StringComparer.OrdinalIgnoreCase).ToList(),
            Math.Max(0, (int) rules.m_maxSpells), style);
        var book = wizard.SpellbookBehavior;
        var cardRoom = Math.Max(0, (int) rules.m_maxTreasureCards);
        var oldCards = (deck.m_spellList ?? []).Where(s => IsTreasure(s.m_templateID)).ToList();
        var cards = treasure.OrderBy(Order).ThenByDescending(e => Rank(e.Id)).Select(e => e.Id).Distinct()
            .Where(id => oldCards.Any(s => s.m_templateID == id) || (book.TreasureCardTemplateIds?.Contains(id) ?? false)).Take(cardRoom).ToList();

        var want = spells.Concat(cards).ToList();
        var now = (deck.m_spellList ?? []).Select(s => (s.m_templateID, s.m_quantity)).OrderBy(s => s.m_templateID).ToList();
        if (now.Count == want.Count && now.All(s => s.m_quantity == 1) && now.Select(s => s.m_templateID).ToHashSet().SetEquals(want)) {
            return;
        }

        // Treasure cards taken out of the deck go back to his book; the ones put in leave it.
        var leaving = oldCards.Where(s => !cards.Contains(s.m_templateID) && !TrueFormPool.NotForPlayers(s.m_templateID)).SelectMany(s => Enumerable.Repeat(s.m_templateID, (int) s.m_quantity)).ToList();
        var coming = cards.Where(id => !oldCards.Any(s => s.m_templateID == id)).ToList();
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

        deck.m_spellList = [.. want.Select(id => new SpellData { m_templateID = id, m_enchantment = 0, m_quantity = 1 })];
        if (book is Imlight.CoreLib.Shared.Behaviors.ServerWizSpellbookBehavior wiz) {
            wiz.EquipDeck(ItemHelper.GetItemTemplate(deckItem), deck);
        }

        WizardItemCollection.SaveItemBehaviors(deckItem);
        Logger.Information("True Form: his deck holds {0} spells and {1} treasure cards.", Logger.Args(spells.Count, cards.Count));
    }

    // Hits and utility mixed by his role (Niduus, 2026-11-19: "TF's decks are more consistent with hits rather than utility.
    // Adjust this to add some Utility"): hits take their share (half for a hitter, less for backup, tank and healer), the
    // rest goes round the utility kinds in his role's order (blades, traps, shields, heals, weakness, charm removal, stun,
    // the rest: prisms, globals, auras, pips), the best of each first. Room left over is filled in the old order.
    private static List<uint> Mixed(List<TrueFormPool.Entry> ordered, int room, BotStyle style) {
        double hitShare = style switch { BotStyle.GlassCannon => 0.6, BotStyle.Hitter or BotStyle.UniversalHitter => 0.5, BotStyle.Backup => 0.4, BotStyle.Tank => 0.3, BotStyle.FullTank => 0.25, BotStyle.FullHealer => 0.15, _ => 0.25 };
        string[] kinds = style switch {
            BotStyle.Healer or BotStyle.FullHealer => ["heal", "shield", "charm removal", "blade", "other", "stun", "trap", "weakness"],
            BotStyle.Tank or BotStyle.FullTank => ["shield", "weakness", "stun", "heal", "trap", "other", "blade", "charm removal"],
            _ => ["blade", "trap", "other", "shield", "heal", "weakness", "charm removal", "stun"],
        };
        string KindOf(TrueFormPool.Entry e) => e.Card?.Kind ?? "other";
        var picked = new List<uint>();
        var seen = new HashSet<uint>();
        void Take(TrueFormPool.Entry e) {
            if (picked.Count < room && seen.Add(e.Id)) {
                picked.Add(e.Id);
            }
        }

        foreach (var hit in ordered.Where(e => KindOf(e) == "attack").Take((int) Math.Round(room * hitShare))) {
            Take(hit);
        }

        var queues = kinds.Select(k => new Queue<TrueFormPool.Entry>(ordered.Where(e => KindOf(e) == k))).ToList();
        while (picked.Count < room && queues.Any(q => q.Count > 0)) {
            foreach (var queue in queues.Where(q => q.Count > 0)) {
                Take(queue.Dequeue());
            }
        }

        foreach (var rest in ordered) {
            Take(rest);
        }

        return picked;
    }

    private static bool IsTreasure(uint id) => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t && t.m_Treasure;

    private static int Rank(uint id) {
        try {
            return CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t ? Math.Max(0, (int) t.m_spellRank.m_spellRank) : 0;
        }
        catch (Exception) {
            return 0;
        }
    }

    private static void WriteList(Wizard wizard, List<TrueFormPool.Entry> trained, List<TrueFormPool.Entry> treasure) {
        try {
            var deck = DeckBehaviorOf(wizard)?.m_spellList?.Select(s => s.m_templateID).ToHashSet() ?? [];
            var lines = new List<string> { $"{trained.Count} trained spells, {treasure.Count} treasure cards, {deck.Count} cards in his deck", "" };
            foreach (var school in trained.Concat(treasure).GroupBy(e => e.School).OrderBy(g => g.Key)) {
                lines.Add($"{school.Key} ({school.Count()}):");
                lines.AddRange(school.OrderBy(e => e.Shown).Select(e => $"  {e.Shown}{(e.Treasure ? " (treasure card)" : "")}{(deck.Contains(e.Id) ? " [in deck]" : "")}"));
            }

            Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            File.WriteAllLines(Path.Combine(UltimusSeat.TrueForm.Folder, SPELLS_FILE), lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not write his spell list: {0}", Logger.Args(e.Message));
        }
    }

}
