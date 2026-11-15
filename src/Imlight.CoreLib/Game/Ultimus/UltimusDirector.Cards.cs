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
/// From a game's own treasure card vendor in the area he is in (never the Bazaar, which stocks every card), he buys a few
/// treasure cards of his school that he can cast (two hits and a heal, the best for his level), with gold past his
/// reserve, and puts them in his deck as a player does in the deck window. In a tough fight he presses Draw for one (CombatDuelComponent.Ultimus.cs).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const int TREASURE_CARDS_KEPT = 3;
    private DateTime _nextCardLook = DateTime.MinValue;
    private UltimusPlanner.Target _cardTrip;
    private readonly HashSet<uint> _cardSkipped = [];

    // How many treasure cards he has: in his deck plus in his book.
    private static int TreasureCardsOwned(Wizard wizard) {
        var book = wizard.SpellbookBehavior;
        var inDeck = (book?.SpellList ?? []).Where(s => s is not null && IsTreasure(s.m_templateID)).Sum(s => (int) s.m_quantity);
        return inDeck + (book?.TreasureCardTemplateIds?.Count ?? 0);
    }

    private static bool IsTreasure(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is SpellTemplate t && (t.m_Treasure || ((string) t.m_name)?.EndsWith(" TC", StringComparison.Ordinal) == true);

    /// <summary>The treasure cards in his book go into his deck (out of battle), as a player drags them in.</summary>
    private void TreasureCardsIntoDeck(UltimusMember.Status member) {
        if (member.Wizard is not { SpellbookBehavior: { } book } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)
            || book.TreasureCardTemplateIds is not { Count: > 0 } owned) {
            return;
        }

        var deckItem = wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck);
        if (deckItem is null) {
            return;
        }

        var ids = owned.Where(id => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate).Take(TREASURE_CARDS_KEPT).ToList();
        if (ids.Count == 0) {
            return;
        }

        Note("Ultimus", $"puts {ids.Count} treasure card(s) in his deck: {string.Join(", ", ids.Select(CardName))}.");
        _actors[0].Tell(new UltimusMember.TreasureIntoDeck(deckItem.m_globalID,
            [.. ids.Select(id => (CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate)?.m_name is { } n ? StringHash.Compute((string) n) : 0u).Where(h => h != 0)]));
    }

    private static string CardName(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t ? Locale.GetEnglishName((string) t.m_displayName) is { Length: > 0 } e ? e : (string) t.m_name : id.ToString();

    // The few he wants from this vendor's own list: of his school, castable at his level, never a test card; two hits (the
    // hardest) and a heal (the biggest), each one he can pay for past his reserve.
    private List<(uint Id, uint Hash, string Name, int Price)> CardsToBuy(Wizard wizard, InteractTreasureVendorComponent vendor,
                                                                          NpcTreasureCardInventory sold, int need, long spend) {
        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var picks = new List<(uint Id, uint Hash, string Name, int Price, bool Heal, int Score)>();
        foreach (var entry in sold.TreasureCards ?? []) {
            if (string.IsNullOrEmpty(entry.SpellName) || SpellFactory.GetSpell(entry.SpellName) is not { } spell
                || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
                || !string.Equals((string) template.m_sMagicSchoolName, school, StringComparison.OrdinalIgnoreCase) || template.m_levelRestriction > level
                || _cardSkipped.Contains(spell.m_templateID) || RewrittenAI.Read(spell) is not { } card || BotCards.NotVanilla(card)
                || BotCards.IsDevSpell(spell.m_templateID) || !(card.Hit || card.Heal)) {
                continue;
            }

            var hash = StringHash.Compute((string) template.m_name);
            if (!vendor.HasSpell(hash)) {
                continue;
            }

            picks.Add((spell.m_templateID, hash, card.Shown, vendor.GetSpellPrice(hash), card.Heal && !card.Hit, card.Hit ? card.BaseDamage : card.HealAmount));
        }

        var wanted = picks.Where(p => !p.Heal).OrderByDescending(p => p.Score).Take(2)
            .Concat(picks.Where(p => p.Heal).OrderByDescending(p => p.Score).Take(1)).ToList();
        var chosen = new List<(uint Id, uint Hash, string Name, int Price)>();
        foreach (var pick in wanted) {
            if (chosen.Count >= need) {
                break;
            }

            if (pick.Price <= 0 || pick.Price > spend - chosen.Sum(c => c.Price)) {
                continue;
            }

            chosen.Add((pick.Id, pick.Hash, pick.Name, pick.Price));
        }

        return chosen;
    }

    /// <summary>
    /// A few treasure cards from a game's own treasure card vendor in the area he is in, with gold past his reserve: the walk
    /// to them, or the buy. Never the Bazaar (Niduus, 2026-11-15: it stocks every treasure card in the game; he may only sell
    /// them there), never a custom or event vendor, never the test shop.
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

            var cards = CardsToBuy(wizard, vendor, sold, need, spend);
            if (cards.Count == 0) {
                continue;
            }

            Think($"A treasure card vendor is right here: I pick up a few treasure cards for tough fights ({string.Join(", ", cards.Select(c => c.Name))}, " +
                  $"{cards.Sum(c => c.Price):N0} gold in all). My gold reserve stays.", "tc-buy|" + string.Join(",", cards.Select(c => c.Id)));
            _cardTrip = new UltimusPlanner.Target(UltimusPlanner.SHOP, npc.Object.m_location.X, npc.Object.m_location.Y,
                $"{leader.Name} buys treasure cards", Template: npc.Template, GlobalId: npc.Object.m_globalID.Full,
                Key: $"tcvendor|{leader.Role}|{string.Join(",", cards.Select(c => $"{c.Id}:{c.Hash}"))}");
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
            _cardSkipped.Add(id);
        }

        Count("treasure cards bought", list.Count);
        _cardTrip = null;
        // A few seconds later the new cards go into his deck.
        _nextCardLook = DateTime.UtcNow.AddSeconds(6);
    }

}
