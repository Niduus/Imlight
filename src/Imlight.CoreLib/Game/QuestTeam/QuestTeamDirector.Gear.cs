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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The Quest Team's gear and decks (Niduus, 2026-11-12): each wizard wears the best gear for its school and level that it
/// has, walks to a shop in the world it is in to buy better gear with its own gold, and changes its deck before each fight
/// for what is coming (Game/BotShared). All of it the way a player does: the backpack's equip, the shop window's buy and
/// the deck window's add and remove.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    // The gold a wizard keeps for potions and the next trainer.
    private const double GOLD_KEPT = 0.25;

    private DateTime _nextGearLook = DateTime.MinValue;
    private DateTime _nextShopping = DateTime.MinValue;
    private readonly Dictionary<string, string> _deckFor = [];
    private readonly Dictionary<string, int> _deckKnown = [];
    private static (List<string[]> Names, Dictionary<uint, int[]> Of)? s_vendorZones;
    private QuestTeamPlanner.Target _shopTrip;
    private readonly HashSet<uint> _shopSkipped = [];
    private readonly HashSet<string> _mountGiven = [];

    // A member alone in a solo instance fights on its own: a balanced deck of hits, heals and shields, whatever its role.
    private BotStyle StyleOf(QuestTeamMember.Status member) => _solo.Contains(member.Role) ? BotStyle.Backup : BotGear.StyleOfRole(member.Role);

    private readonly HashSet<string> _solo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _friendTriedAt = new(StringComparer.Ordinal);

    // Niduus, 2026-11-12: "If they are left in a instance check, they will attempt to teleport to all of their team mates,
    // if busy, it assumes the instance it's in (If shared the same location data) is a solo instance, thus will prep it's
    // deck for a solo fight in place".
    private void KeepSoloInstances() {
        for (var i = 0; i < _team.Count && i < _actors.Count; i++) {
            var member = _team[i];
            var alone = member.Wizard?.ZoneIsInstance == true && member.ZoneActor is not null
                        && !_team.Any(o => o != member && member.ZoneActor.Equals(o.ZoneActor));
            if (!alone) {
                _friendTriedAt.Remove(member.Role);
                if (_solo.Remove(member.Role)) {
                    Note(member.Role, "is with the team again; back to its team deck.");
                    _deckFor.Remove(member.Role);
                    PrepareDeck(i, BotCards.Situation.Unknown, force: true);
                }

                continue;
            }

            if (_solo.Contains(member.Role)) {
                continue;
            }

            if (!_friendTriedAt.TryGetValue(member.Role, out var tried)) {
                _friendTriedAt[member.Role] = DateTime.UtcNow;
                var others = _team.Where(o => o != member).ToList();
                Note(member.Role, $"is alone in an instance ({member.Zone}); tries to go to {string.Join(", ", others.Select(o => o.Role))} through the friends list.");
                for (var k = 0; k < others.Count; k++) {
                    Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1 + (k * 2)), _actors[i],
                        new QuestTeamMember.GoToTeammate(others[k].CharId), Self);
                }

                continue;
            }

            if (DateTime.UtcNow - tried < TimeSpan.FromSeconds(15)) {
                continue;
            }

            // Still alone: the others were busy. Where they all stand in the same place (their own copies), it is a solo instance.
            if (_team.Where(o => o != member).All(o => OverlayQuestTracker.SameZone(o.Zone ?? "", member.Zone ?? ""))) {
                _solo.Add(member.Role);
                Note(member.Role, $"could not reach a teammate and all are in their own copy of {member.Zone}: treats it as a solo instance and readies its deck to fight alone.");
                _deckFor.Remove(member.Role);
                PrepareDeck(i, _target?.Kind == QuestTeamPlanner.FIGHT ? BotCards.SituationOf(_target.Template) : BotCards.Situation.Unknown, force: true);
            }
            else if (DateTime.UtcNow - tried > TimeSpan.FromSeconds(60)) {
                _friendTriedAt.Remove(member.Role);
            }
        }
    }

    /// <summary>Out of a battle: better gear from the backpack goes on, and a deck that misses newly learned spells is redone.</summary>
    private void KeepGearAndDecks() {
        if (DateTime.UtcNow < _nextGearLook) {
            return;
        }

        _nextGearLook = DateTime.UtcNow.AddSeconds(6);
        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            if (member.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)) {
                continue;
            }

            // Short of gold: Ahziael gives it, so shopping and potions never hold the quests up (Niduus, 2026-11-12:
            // "Give them gold to prevent slow downs"), and the log says so.
            var level = wizard.MagicSchoolBehavior?.Level ?? 1;
            var floor = 500 + (level * 200);
            if (wizard.GameStats is { } stats && stats.m_currentGold < floor / 4) {
                var give = floor - stats.m_currentGold;
                wizard.AddGold(give);
                Note(member.Role, $"{member.Name} was short of gold; Ahziael gives it {give} gold (now {stats.m_currentGold}).");
            }

            // A full backpack: the gear it will never wear is sold, as a player quick-sells (Niduus, 2026-11-12: "sell junk
            // when the backpack is full"), logged with what went and the gold it brought.
            if (wizard.BackpackFreeSlots < 3 && BotGear.Junk(wizard, StyleOf(member)) is { Count: > 0 } junk) {
                var sold = wizard.RemoveItemsFromInventory(junk.Select(j => j.Item.m_globalID.Full), "sold by the Quest Team (backpack full)").ToHashSet();
                var gold = junk.Where(j => sold.Contains(j.Item.m_globalID.Full))
                    .Sum(j => (int) Math.Ceiling(((Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(j.Item.m_templateID)
                        as Imcodec.ObjectProperty.TypeCache.WizItemTemplate)?.m_baseCost ?? 0) * 0.05f));
                if (sold.Count > 0) {
                    wizard.AddGold(gold);
                    Note(member.Role, $"backpack full: sells {sold.Count} things it will not wear for {gold} gold " +
                                      $"({string.Join(", ", junk.Where(j => sold.Contains(j.Item.m_globalID.Full)).Select(j => j.Piece.Name).Take(8))}).");
                }
            }

            // Their school's Ghulture, ridden between quests for its speed and stats (Niduus, 2026-11-12: "they are given
            // the class ghulture mounts for speed and stats").
            if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is null && _mountGiven.Add(member.Role)) {
                var mountId = Imlight.CoreLib.Shared.Character.AdeptStarterGear.GhultureFor(wizard.MagicSchoolBehavior?.MagicSchool ?? Imlight.CoreLib.Shared.Behaviors.MagicSchool.Balance);
                var mount = (wizard.InventoryBehavior?.Items ?? []).FirstOrDefault(it => (ulong) it.m_templateID == mountId);
                if (mount is not null || wizard.AddItemToInventory(mountId, out mount)) {
                    _actors[i].Tell(new QuestTeamMember.Equip(mount.m_globalID, "Mount", "its Ghulture"));
                    continue;
                }
            }

            // Developers' and testers' gear never stays on (a Deckathalon deck stays until a fair deck replaces it).
            if (BotGear.ForbiddenWorn(wizard).FirstOrDefault() is { Item: not null } banned) {
                _actors[i].Tell(new QuestTeamMember.Unequip(banned.Item.m_globalID, banned.Slot, banned.Name));
                continue;
            }

            // One piece at a time, the best gain first.
            if (BotGear.BetterInBackpack(wizard, StyleOf(member)).OrderByDescending(b => b.Gain).FirstOrDefault() is { Item: not null } better) {
                _actors[i].Tell(new QuestTeamMember.Equip(better.Item.m_globalID, better.Slot, better.Piece.Name));
                continue;
            }

            // New spells learned (or a new deck worn) since the deck was made: the deck again, for the fight it was made for.
            var known = (wizard.SpellbookBehavior?.LearnedSpellTemplateIds?.Count ?? 0) * 1000 + (wizard.SpellbookBehavior?.MaxSpells ?? 0);
            if (_deckKnown.GetValueOrDefault(member.Role) != known) {
                PrepareDeck(i, _target?.Kind == QuestTeamPlanner.FIGHT ? BotCards.SituationOf(_target.Template) : BotCards.Situation.Unknown, force: true);
            }
        }
    }

    /// <summary>Before a fight: each wizard's deck for what it is about to fight.</summary>
    private void PrepareDecks(BotCards.Situation situation) {
        for (var i = 0; i < _team.Count; i++) {
            PrepareDeck(i, situation, force: false);
        }
    }

    private void PrepareDeck(int index, BotCards.Situation situation, bool force) {
        var member = _team[index];
        if (member.Wizard is not { SpellbookBehavior: { } book } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)) {
            return;
        }

        var key = situation.Describe();
        if (!force && _deckFor.GetValueOrDefault(member.Role) == key) {
            return;
        }

        var deckItem = wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Deck);
        if (deckItem is null || book.MaxSpells <= 0) {
            return;
        }

        var wanted = BotCards.Plan(book.LearnedSpellTemplateIds ?? [], BotGear.SchoolOf(wizard), StyleOf(member), situation,
            book.MaxSpells, Math.Max(1, book.SchoolMaxInstances), Math.Max(1, book.GenericMaxInstances));
        // Treasure cards and anything that is not a fighting card stay as they are.
        var now = (book.SpellList ?? []).Where(s => s is not null && BotCards.CardOf(s.m_templateID) is not null)
            .GroupBy(s => s.m_templateID).ToDictionary(g => (uint) g.Key, g => g.Sum(s => (int) s.m_quantity));

        _deckFor[member.Role] = key;
        _deckKnown[member.Role] = (book.LearnedSpellTemplateIds?.Count ?? 0) * 1000 + book.MaxSpells;
        var (take, put) = BotCards.Changes(now, wanted);
        // Developers' test cards come out, every copy.
        var dev = (book.SpellList ?? []).Where(s => s is not null && BotCards.IsDevSpell((uint) s.m_templateID))
            .SelectMany(s => Enumerable.Repeat((uint) s.m_templateID, Math.Max(1, (int) s.m_quantity))).ToList();
        take.AddRange(dev);
        if (take.Count + put.Count == 0 || (!force && dev.Count == 0 && take.Count + put.Count < 2)) {
            return;
        }

        Note(member.Role, $"{member.Name} makes its deck ready for {key}: {BotCards.Describe(wanted.Select(w => (w.Key, w.Value)))}.");
        _actors[index].Tell(new QuestTeamMember.EditDeck(deckItem.m_globalID, take, put, "for " + key));
    }

    // ---------------------------------------------------------------------
    // Shopping
    // ---------------------------------------------------------------------

    /// <summary>A trip to a shop in this world that sells better gear one of them can pay for, or null.</summary>
    private QuestTeamPlanner.Target ShoppingTarget(List<QuestTeamMember.Status> group) {
        if (DateTime.UtcNow < _nextShopping) {
            return _shopTrip;
        }

        _nextShopping = DateTime.UtcNow.AddSeconds(10);
        _shopTrip = group.Any(m => m.Wizard?.ZoneIsInstance == true) ? null : FindShopTrip(group);
        if (_shopTrip is null) {
            _nextShopping = DateTime.UtcNow.AddMinutes(5);
        }

        return _shopTrip;
    }

    private QuestTeamPlanner.Target FindShopTrip(List<QuestTeamMember.Status> group) {
        var leader = group[0];
        var world = (leader.Zone ?? "").Split('/')[0];
        var live = MinimapIndex.In(leader.ZoneActor);
        foreach (var member in group) {
            if (member.Wizard is not { } wizard) {
                continue;
            }

            var gold = (int) ((wizard.GameStats?.m_currentGold ?? 0) * (1 - GOLD_KEPT));
            var owned = (wizard.InventoryBehavior?.Items ?? []).Select(i => (uint) Math.Min((ulong) i.m_templateID, uint.MaxValue)).ToHashSet();
            foreach (var (piece, vendors, _) in BotGear.ShopUpgrades(wizard, StyleOf(member), gold)) {
                if (owned.Contains(piece.Id) || _shopSkipped.Contains(piece.Id)) {
                    continue;
                }

                // A shopkeeper of it here first.
                var here = live.FirstOrDefault(m => vendors.Contains((uint) m.Template));
                if (here is not null) {
                    return new QuestTeamPlanner.Target(QuestTeamPlanner.SHOP, here.Object.m_location.X, here.Object.m_location.Y,
                        $"{member.Name} buys {piece.Name} from {here.Name}", Template: here.Template, GlobalId: here.Object.m_globalID.Full,
                        Key: $"shop|{member.Role}|{piece.Id}");
                }

                // Else one in another area of this world.
                var zone = VendorZones(vendors).FirstOrDefault(z => string.Equals(z.Split('/')[0], world, StringComparison.OrdinalIgnoreCase));
                if (zone is not null && QuestTeamPlanner.Travel(leader, zone, $"shopping for {member.Name}'s {piece.Name}") is { Problem.Length: 0 } way) {
                    return way;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> VendorZones(IReadOnlyList<uint> vendors) {
        s_vendorZones ??= VendorGuide.LoadZones();
        var (names, of) = s_vendorZones.Value;
        foreach (var vendor in vendors) {
            foreach (var number in of.GetValueOrDefault(vendor) ?? []) {
                if (number >= 0 && number < names.Count && names[number][0].Length > 0) {
                    yield return names[number][0];
                }
            }
        }
    }

    private void ShopHere(List<QuestTeamMember.Status> group, QuestTeamPlanner.Target target) {
        var parts = target.Key.Split('|');
        var member = _team.FirstOrDefault(m => m.Role == parts[1]);
        if (member is null || !uint.TryParse(parts[2], out var itemId)) {
            return;
        }

        var index = _team.IndexOf(member);
        if (QuestTeamPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            WalkTo(index, target with { X = target.X - 40 });

            return;
        }

        var key = $"{target.Key}|{index}";
        if (!CanClick(key)) {
            if (TimesClicked(key) >= 2) {
                // The shop would not sell it: on with the quests, back to shopping later.
                Note(member.Role, $"{member.Name} could not buy {BotGear.Read(itemId)?.Name ?? itemId.ToString()} here.");
                _shopSkipped.Add(itemId);
                _shopTrip = null;
                _nextShopping = DateTime.UtcNow.AddSeconds(10);
            }

            return;
        }

        Clicked(key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "WizShoppingService") is { Service: not null } open) {
            _actors[index].Tell(new QuestTeamMember.Interact(target.GlobalId, open.Service, open.Index, "shopping"));
        }

        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[index],
            new QuestTeamMember.Buy(target.GlobalId, itemId, BotGear.Read(itemId)?.Name ?? itemId.ToString()), Self);
        // The next one in a moment; the new piece goes on at the next look at the backpack.
        _shopTrip = null;
        _nextShopping = DateTime.UtcNow.AddSeconds(5);
        _nextGearLook = DateTime.UtcNow.AddSeconds(3);
    }

}
