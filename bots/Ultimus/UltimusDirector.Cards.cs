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
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Treasure cards (Niduus, 2026-11-15: "Let him buy a few treasure cards and play them in tough fights. - Yes, let him").
/// From a game's own treasure card vendor in the area he is in (the Bazaar trip is UltimusDirector.CardPlan.cs), he buys a few
/// treasure cards of his school that he can cast (two hits and a heal, the best for his level), with gold past his
/// reserve, and puts them in his deck as a player does in the deck window. In a tough fight he presses Draw for one (CombatDuelComponent.Ultimus.cs).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // How many treasure cards he keeps (stacked: up to COPIES of one card), as a player keeps a stack in the deck's treasure
    // card slots (Niduus, 2026-11-17: "He is to adapt to his battles ahead. Able to purchase, stack and use Treasure Cards").
    private const int TREASURE_CARDS_KEPT = 8;
    private const int COPIES = 3;
    private DateTime _nextCardLook = DateTime.MinValue;
    private UltimusPlanner.Target _cardTrip;
    // Cards just bought (or that would not buy), left alone for a while: he restocks them later, once used.
    private readonly Dictionary<uint, DateTime> _cardSkipped = [];
    private static readonly TimeSpan s_cardSkip = TimeSpan.FromMinutes(20);

    // How many treasure cards he has: in his deck plus in his book.
    private static int TreasureCardsOwned(Wizard wizard) {
        var book = wizard.SpellbookBehavior;
        var inDeck = (book?.SpellList ?? []).Where(s => s is not null && IsTreasure(s.m_templateID)).Sum(s => (int) s.m_quantity);
        return inDeck + (book?.TreasureCardTemplateIds?.Count ?? 0);
    }

    private static bool IsTreasure(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is SpellTemplate t && (t.m_Treasure || ((string) t.m_name)?.EndsWith(" TC", StringComparison.Ordinal) == true);

    /// <summary>
    /// His deck's treasure card slots hold his best cards for the fights ahead (out of battle), as a player sets them in the
    /// deck window: his card plan first (UltimusDirector.CardPlan.cs), then the next best he owns. A card he can't play takes
    /// no slot: every enchantment treasure card of the game needs level 50 or more (Tough and Giant 52, Gargantuan 58), so
    /// at level 26 the Tough, Giant, Sniper and Keen Eyes in his deck were dead slots while Storm Hound and Monsoon Hound
    /// sat in his book (Niduus's deck window, 2026-11-17). Those go back to his book and the best ones come in.
    /// </summary>
    private void TreasureCardsIntoDeck(UltimusMember.Status member) {
        if (member.Wizard is not { SpellbookBehavior: { } book } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)) {
            return;
        }

        var deckItem = wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck);
        if (deckItem is null) {
            return;
        }

        var inDeck = new Dictionary<uint, int>();
        foreach (var s in (book.SpellList ?? []).Where(s => s is not null && IsTreasure(s.m_templateID))) {
            inDeck[s.m_templateID] = inDeck.GetValueOrDefault(s.m_templateID) + (int) s.m_quantity;
        }

        var want = DeckTreasureCards(wizard, book.MaxTreasureCards > 0 ? book.MaxTreasureCards : TREASURE_CARDS_KEPT);
        var outOfDeck = inDeck.Select(c => (Id: c.Key, Count: c.Value - want.GetValueOrDefault(c.Key))).Where(c => c.Count > 0).ToList();
        var intoDeck = want.Select(c => (Id: c.Key, Count: c.Value - inDeck.GetValueOrDefault(c.Key))).Where(c => c.Count > 0).ToList();
        if (intoDeck.Count == 0 && outOfDeck.Count == 0) {
            return;
        }

        static uint Hash(uint id) => (CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate)?.m_name is { } n ? StringHash.Compute((string) n) : 0u;
        static string Say(List<(uint Id, int Count)> cards) => string.Join(", ", cards.Select(c => $"{CardName(c.Id)} x{c.Count}"));
        if (outOfDeck.Count > 0) {
            Note("Ultimus", $"takes treasure cards out of his deck, back to his book, to make room for better ones: {Say(outOfDeck)}.");
        }

        if (intoDeck.Count > 0) {
            Note("Ultimus", $"puts {intoDeck.Sum(c => c.Count)} treasure card(s) in his deck: {Say(intoDeck)}.");
        }

        // Out first, so the slots are free when the new ones go in (the game handles them in the order sent).
        _actors[0].Tell(new UltimusMember.TreasureDeckChange(deckItem.m_globalID,
            [.. outOfDeck.SelectMany(c => Enumerable.Repeat(Hash(c.Id), c.Count)).Where(h => h != 0)],
            [.. intoDeck.SelectMany(c => Enumerable.Repeat(Hash(c.Id), c.Count)).Where(h => h != 0)]));
    }

    /// <summary>
    /// The treasure cards his deck should hold, and how many of each: the cards of his plan he owns (up to the plan's
    /// copies), then the next best he owns for the fights ahead (CardsForDeck, up to COPIES of each), until the slots are
    /// full. Only cards he can play at his level, never a broken one.
    /// </summary>
    private Dictionary<uint, int> DeckTreasureCards(Wizard wizard, int room) {
        var owned = OwnedCopies(wizard);
        // Treasure cards he has held in many fights and never cast stay in his book (UltimusCardUse; Niduus, 2026-11-18).
        var folder = UltimusSeat.Current.Folder;
        var level = BotGear.LevelOf(wizard);
        var usable = CardsForDeck(wizard, owned.Keys.Where(id => !BrokenCards.Skip(id) && UltimusCardUse.Rarely(folder, id, level) is null)).ToList();
        var want = new Dictionary<uint, int>();
        void Take(uint id, int copies) {
            var left = room - want.Values.Sum();
            var n = Math.Min(Math.Min(copies, left), owned.GetValueOrDefault(id) - want.GetValueOrDefault(id));
            if (n > 0) {
                want[id] = want.GetValueOrDefault(id) + n;
            }
        }

        foreach (var (card, _) in CardPlanNow(wizard, out _)) {
            if (usable.Contains(card.Id)) {
                Take(card.Id, card.Copies);
            }
        }

        foreach (var id in usable) {
            Take(id, COPIES - want.GetValueOrDefault(id));
        }

        return want;
    }

    private static string CardName(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t ? Locale.GetEnglishName((string) t.m_displayName) is { Length: > 0 } e ? e : (string) t.m_name : id.ToString();

    // The ones he wants from this vendor's own list, for the fights ahead (FoeSchools): hits of his school or of the school
    // those creatures are weak to, a shield against what they hit with, and a heal; castable at his level, never a test
    // card, each paid for past his reserve. Stacked: the best of each kind bought more than once, up to COPIES of it owned.
    private List<(uint Id, uint Hash, string Name, int Price)> CardsToBuy(Wizard wizard, InteractTreasureVendorComponent vendor,
                                                                          NpcTreasureCardInventory sold, int need, long spend,
                                                                          IReadOnlyCollection<string> foes) {
        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var weakTo = foes.Select(RewrittenAI.Opposite).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picks = new List<(uint Id, uint Hash, string Name, int Price, string Kind, int Score)>();
        foreach (var entry in sold.TreasureCards ?? []) {
            if (string.IsNullOrEmpty(entry.SpellName) || SpellFactory.GetSpell(entry.SpellName) is not { } spell
                || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template || template.m_levelRestriction > level
                || (_cardSkipped.TryGetValue(spell.m_templateID, out var skipped) && DateTime.UtcNow - skipped < s_cardSkip)
                || RewrittenAI.Read(spell) is not { } card || BotCards.NotVanilla(card)
                || BotCards.IsDevSpell(spell.m_templateID) || BrokenCards.Skip(spell.m_templateID)) {
                continue;
            }

            var cardSchool = (string) template.m_sMagicSchoolName ?? "";
            var own = string.Equals(cardSchool, school, StringComparison.OrdinalIgnoreCase);
            string kind;
            int score;
            if (card.Hit && (own || weakTo.Contains(card.DamageSchool) || weakTo.Contains(cardSchool))) {
                kind = "hit";
                // What the creatures ahead are weak to hits them harder.
                score = card.BaseDamage * (weakTo.Contains(card.DamageSchool) || weakTo.Contains(cardSchool) ? 2 : 1);
            }
            else if (card.Shield && !card.ShieldForTeammate && foes.Count > 0
                     && (card.ShieldSchools.Length == 0 ? foes.Any(f => string.Equals(f, card.ShieldSchool, StringComparison.OrdinalIgnoreCase))
                         : card.ShieldSchools.Any(x => x == "All" || foes.Contains(x, StringComparer.OrdinalIgnoreCase)))) {
                kind = "shield";
                score = 100;
            }
            else if (card.Heal && !card.Hit) {
                kind = "heal";
                score = card.HealAmount;
            }
            else {
                continue;
            }

            var hash = StringHash.Compute((string) template.m_name);
            if (!vendor.HasSpell(hash)) {
                continue;
            }

            picks.Add((spell.m_templateID, hash, card.Shown, vendor.GetSpellPrice(hash), kind, score));
        }

        var best = picks.Where(p => p.Kind == "hit").OrderByDescending(p => p.Score).Take(2)
            .Concat(picks.Where(p => p.Kind == "shield").OrderByDescending(p => p.Score).Take(1))
            .Concat(picks.Where(p => p.Kind == "heal").OrderByDescending(p => p.Score).Take(1)).ToList();
        var owned = OwnedCopies(wizard);
        var chosen = new List<(uint Id, uint Hash, string Name, int Price)>();
        // One of each first, then more copies of each, round by round, while he needs more and can pay.
        for (var round = 0; round < COPIES && chosen.Count < need; round++) {
            foreach (var pick in best) {
                if (chosen.Count >= need || owned.GetValueOrDefault(pick.Id) + chosen.Count(c => c.Id == pick.Id) >= COPIES) {
                    continue;
                }

                if (pick.Price <= 0 || pick.Price > spend - chosen.Sum(c => c.Price)) {
                    continue;
                }

                chosen.Add((pick.Id, pick.Hash, pick.Name, pick.Price));
            }
        }

        return chosen;
    }

    // Copies of each treasure card he has (deck and book).
    private static Dictionary<uint, int> OwnedCopies(Wizard wizard) {
        var book = wizard.SpellbookBehavior;
        var owned = new Dictionary<uint, int>();
        foreach (var s in (book?.SpellList ?? []).Where(s => s is not null && IsTreasure(s.m_templateID))) {
            owned[s.m_templateID] = owned.GetValueOrDefault(s.m_templateID) + (int) s.m_quantity;
        }

        foreach (var id in book?.TreasureCardTemplateIds ?? []) {
            owned[id] = owned.GetValueOrDefault(id) + 1;
        }

        return owned;
    }

    /// <summary>
    /// The schools of the creatures ahead, to buy for: his next fight, the boss his story step names, and the creatures that
    /// beat him or hurt him most lately (UltimusFoes). Empty when none is known.
    /// </summary>
    private List<string> FoeSchools() {
        var templates = new List<ulong>();
        if (_target is { Kind: UltimusPlanner.FIGHT, Template: not 0 } fight) {
            templates.Add(fight.Template);
        }

        templates.AddRange(UltimusFoes.All().Where(f => f.Tough && DateTime.UtcNow - f.Last < TimeSpan.FromDays(1))
            .OrderByDescending(f => f.Last).Take(4).Select(f => (ulong) f.Template));
        var schools = new List<string>();
        foreach (var t in templates.Distinct()) {
            if (CoreObjectFactory.GetCoreTemplate(t) is GameObjectTemplate g
                && g.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault()?.m_schoolOfFocus is { } focus
                && ((string) focus) is { Length: > 0 } school && !schools.Contains(school, StringComparer.OrdinalIgnoreCase)) {
                schools.Add(school);
            }
        }

        return schools;
    }

    /// <summary>
    /// A few treasure cards from a game's own treasure card vendor in the area he is in, with gold past his reserve: the walk
    /// to them, or the buy. Never a custom or event vendor, never the test shop. The Bazaar (allowed since Niduus, 2026-11-17:
    /// "Also able to buy them from bazaar") is its own trip, for his card plan: UltimusDirector.CardPlan.cs.
    /// </summary>
    private UltimusPlanner.Target TreasureCardTarget(List<UltimusMember.Status> group) {
        if (DateTime.UtcNow < _nextCardLook) {
            return _cardTrip;
        }

        _nextCardLook = DateTime.UtcNow.AddMinutes(5);
        _cardTrip = null;
        var leader = group[0];
        if (leader.Wizard is not { } wizard || wizard.ZoneIsInstance) {
            return null;
        }

        // Cards in his book first go in his deck (no trip for it).
        TreasureCardsIntoDeck(leader);
        WriteCardBook(leader);
        var need = TREASURE_CARDS_KEPT - TreasureCardsOwned(wizard);
        var spend = (wizard.GameStats?.m_currentGold ?? 0) - GoldReserve(leader);
        if (need <= 0 || spend <= 0) {
            return null;
        }

        foreach (var npc in MinimapIndex.In(leader.ZoneActor)) {
            if (npc.Entity?.GetComponentOfType<InteractTreasureVendorComponent>() is not { } vendor
                || CustomNpcs.For(npc.Entity.Info?.m_zoneTag) is not null
                || !NpcTreasureCardInventoryCollection.TryGetInventory(npc.Template, out var sold)
                || (CoreObjectFactory.GetCoreTemplate(npc.Template) is GameObjectTemplate t && InteractTreasureVendorComponent.IsAbner(t))) {
                continue;
            }

            var foes = FoeSchools();
            var cards = CardsToBuy(wizard, vendor, sold, need, spend, foes);
            if (cards.Count == 0) {
                continue;
            }

            Think($"A treasure card vendor is right here: I stock up on treasure cards for the fights ahead" +
                  (foes.Count > 0 ? $" (creatures of {string.Join(", ", foes)})" : "") + $" ({string.Join(", ", cards.Select(c => c.Name))}, " +
                  $"{cards.Sum(c => c.Price):N0} gold in all). My gold reserve stays.", "tc-buy|" + string.Join(",", cards.Select(c => c.Id)));
            _cardTrip = new UltimusPlanner.Target(UltimusPlanner.SHOP, npc.Object.m_location.X, npc.Object.m_location.Y,
                $"{leader.Name} buys treasure cards", Template: npc.Template, GlobalId: npc.Object.m_globalID.Full,
                Key: $"tcvendor|{leader.Role}|{string.Join(",", cards.Select(c => $"{c.Id}:{c.Hash}"))}");
            _nextCardLook = DateTime.UtcNow.AddSeconds(10);
            _tcTripZone = null;
            return _cardTrip;
        }

        // None here: with few cards left and a hard fight ahead, the nearest vendor of this world that sells cards he can use.
        if (RestockTrip(leader, TreasureCardsOwned(wizard), spend) is { } restock) {
            _cardTrip = restock;
            _nextCardLook = DateTime.UtcNow.AddSeconds(10);
            return _cardTrip;
        }

        return null;
    }

    /// <summary>At a treasure card vendor: he opens their shop and buys each card once, as a player clicks Buy.</summary>
    private void BuyCardsHere(UltimusPlanner.Target target) {
        var parts = target.Key.Split('|');
        var member = _team[0];
        if (parts.Length < 3) {
            return;
        }

        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            WalkTo(0, target);
            return;
        }

        if (!CanClick(target.Key)) {
            if (TimesClicked(target.Key) >= 2) {
                Note("Ultimus", "could not buy treasure cards from the vendor.");
                _cardTrip = null;
                _nextCardLook = DateTime.UtcNow.AddMinutes(15);
            }

            return;
        }

        Clicked(target.Key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "TreasureShopService") is { Service: not null } open) {
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "the treasure card shop"));
        }

        HoldRecall(10);
        var list = parts[2].Split(',').Select(x => x.Split(':')).Where(x => x.Length == 2 && uint.TryParse(x[0], out _) && uint.TryParse(x[1], out _))
            .Select(x => (Id: uint.Parse(x[0]), Hash: uint.Parse(x[1]))).ToList();
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0],
            new UltimusMember.BuyTreasureCards(target.GlobalId, [.. list.Select(c => c.Hash)], string.Join(", ", list.Select(c => CardName(c.Id)))), Self);
        foreach (var (id, _) in list) {
            _cardSkipped[id] = DateTime.UtcNow;
        }

        Count("treasure cards bought", list.Count);
        _cardTrip = null;
        // A few seconds later the new cards go into his deck.
        _nextCardLook = DateTime.UtcNow.AddSeconds(6);
    }

}
