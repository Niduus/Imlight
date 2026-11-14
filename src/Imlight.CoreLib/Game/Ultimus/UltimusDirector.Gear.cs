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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's gear and decks (forked from the Quest Team's, Niduus, 2026-11-12): it wears the best gear for its school and level that it
/// has, walks to a shop in the world it is in to buy better gear with its own gold, and changes its deck before each fight
/// for what is coming (Game/BotShared). All of it the way a player does: the backpack's equip, the shop window's buy and
/// the deck window's add and remove.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // When it last changed a piece (its most health moves with it).
    private DateTime _gearChangedAt = DateTime.MinValue;


    // The gold a wizard keeps for potions and the next trainer.
    private const double GOLD_KEPT = 0.25;

    // The most a shop piece may cost for Ahziael to top up its gold for it, and how much better it must be.
    private const int MAX_GEAR_TOP_UP = 60000;
    private const double BIG_GAIN = 8;

    private DateTime _nextGearLook = DateTime.MinValue;
    private readonly HashSet<uint> _unreadNoted = [];
    private DateTime _nextShopping = DateTime.MinValue;
    private readonly Dictionary<string, string> _deckFor = [];
    private readonly Dictionary<string, int> _deckKnown = [];
    private static (List<string[]> Names, Dictionary<uint, int[]> Of)? s_vendorZones;
    private UltimusPlanner.Target _shopTrip;
    private readonly HashSet<uint> _shopSkipped = [];

    // Pieces a shop here was found not to sell after all, kept in a file (audit A3: the same Jewel of Instinct trip five
    // times in one day, as each restart forgot it).
    private const string NOT_SOLD_FILE = "not-sold-here.txt";
    private HashSet<uint> _notSold;

    private HashSet<uint> NotSold() {
        if (_notSold is not null) {
            return _notSold;
        }

        _notSold = [];
        try {
            var path = System.IO.Path.Combine(UltimusWire.Folder, NOT_SOLD_FILE);
            if (System.IO.File.Exists(path)) {
                foreach (var line in System.IO.File.ReadAllLines(path)) {
                    if (uint.TryParse(line.Split(' ')[0], out var id)) {
                        _notSold.Add(id);
                    }
                }
            }
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(NOT_SOLD_FILE, e.Message));
        }

        return _notSold;
    }

    private void NotSoldHere(uint id, string what) {
        if (!NotSold().Add(id)) {
            return;
        }

        try {
            System.IO.File.AppendAllLines(System.IO.Path.Combine(UltimusWire.Folder, NOT_SOLD_FILE), [$"{id} {what}"]);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(NOT_SOLD_FILE, e.Message));
        }
    }
    private readonly HashSet<string> _mountGiven = [];

    // Ultimus fights alone: a balanced deck of hits, heals and shields, and gear for living alone and for the schools its
    // hits land as (BotGear.SoloDamageMix).
    private static BotStyle StyleOf(UltimusMember.Status member) => BotStyle.Solo;

    /// <summary>
    /// The share of its deck's attack damage each damage type carries, the biggest 1: Hydra's three heads count for Fire,
    /// Ice and Storm, Spectral Blast's for each it may land as, Chimera's for Life, Death or Myth, Judgment for Balance.
    /// </summary>
    private static Dictionary<string, double> DamageMix(Wizard wizard) {
        var mix = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var deck = wizard?.SpellbookBehavior?.SpellList?.Where(s => s is not null).Select(s => ((uint) s.m_templateID, Math.Max(1, (int) s.m_quantity))).ToList() ?? [];
        if (deck.Count == 0) {
            deck = [.. (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds?.ToList() ?? []).Select(id => ((uint) id, 1))];
        }

        foreach (var (id, copies) in deck) {
            if (Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(id) is not Imcodec.ObjectProperty.TypeCache.SpellTemplate spell) {
                continue;
            }

            var hits = Combat.CombatEnchantments.Leaves(spell.m_effects ?? [])
                .Where(e => e is not null && Combat.CombatCharms.DamageEffects.Contains(e.m_effectType) && !string.IsNullOrEmpty((string) e.m_sDamageType))
                .ToList();
            // A Balance spell's hits are boosted by Balance damage and pierce whatever they land as (Niduus picked "Balance
            // boost, like the game"); any other spell's by its damage type.
            var balance = string.Equals((string) spell.m_sMagicSchoolName, "Balance", StringComparison.OrdinalIgnoreCase);
            foreach (var hit in hits) {
                // Each way a hit may land shares the card (a random head is one of several).
                var key = balance ? "Balance" : (string) hit.m_sDamageType;
                mix[key] = mix.GetValueOrDefault(key) + (Math.Max(1, hit.m_effectParam) * copies / (double) hits.Count);
            }
        }

        var most = mix.Count == 0 ? 0 : mix.Values.Max();

        return most <= 0 ? mix : mix.ToDictionary(m => m.Key, m => m.Value / most, StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, uint> _badgeAsked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _badgeAskedAt = new(StringComparer.Ordinal);

    private static readonly string[] s_storyBadgeWords = [
        "Quest", "Story", "Wizard City", "Krokotopia", "Marleybone", "MooShu", "Dragonspyre", "DragonSpire", "Celestia", "Zafaria",
        "Avalon", "Azteca", "Khrysalis", "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria", "Novus", "Wallaru", "Selenopolis", "Ahmarra",
    ];

    private static Imlight.CoreLib.WizardData.Models.World.BadgeDefinition NewestStoryBadge(Wizard wizard) {
        var catalog = Imlight.CoreLib.Game.Badges.BadgeCatalog.Current;
        if (wizard.BadgeBehavior?.Earned is not { Count: > 0 } earned || !catalog.IsReady) {
            return null;
        }

        // Badge filters are written "WizardCity" as well as "Wizard City", so spaces do not count.
        static string Squash(string text) => (text ?? "").Replace(" ", "", StringComparison.Ordinal);
        Imlight.CoreLib.WizardData.Models.World.BadgeDefinition anyTitle = null;
        foreach (var (id, _) in earned.OrderByDescending(e => e.Value.AwardSequence)) {
            if (!catalog.TryGet(id, out var badge) || string.IsNullOrWhiteSpace(badge.Title)) {
                continue;
            }

            if (s_storyBadgeWords.Any(w => Squash(badge.Filter).Contains(Squash(w), StringComparison.OrdinalIgnoreCase))) {
                return badge;
            }

            anyTitle ??= badge;
        }

        // No story badge yet (a new wizard): the newest badge it did earn, so a title still shows.
        return anyTitle;
    }

    /// <summary>Out of a battle: better gear from the backpack goes on, and a deck that misses newly learned spells is redone.</summary>
    private void KeepGearAndDecks() {
        if (DateTime.UtcNow < _nextGearLook) {
            return;
        }

        _nextGearLook = DateTime.UtcNow.AddSeconds(6);
        if (_team.Count > 0 && _team[0].Wizard is { } solo) {
            BotGear.SoloDamageMix = DamageMix(solo);
        }

        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            if (member.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)) {
                continue;
            }

            // Short of gold: Ahziael gave it (Niduus, 2026-11-12: "Give them gold to prevent slow downs"). Since he quests as
            // a human (Niduus, 2026-11-14), only when the playbook's Gold.Gifts is 1; by default he earns and plans his gold.
            var level = wizard.MagicSchoolBehavior?.Level ?? 1;
            var floor = 500 + (level * 200);
            if (GoldGifts && wizard.GameStats is { } stats && stats.m_currentGold < floor / 4) {
                var give = floor - stats.m_currentGold;
                wizard.AddGold(give);
                Count("gold given", give);
                Note(member.Role, $"{member.Name} was short of gold; Ahziael gives it {give} gold (now {stats.m_currentGold}).", UltimusLink.COMMAND);
            }

            // A full backpack: he sells the gear he will never wear at a shop himself (UltimusDirector.Life.cs SellTarget).

            // Their school's Ghulture, ridden between quests for its speed and stats (Niduus, 2026-11-12: "they are given
            // the class ghulture mounts for speed and stats").
            if (wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is null && _mountGiven.Add(member.Role)) {
                var mountId = Imlight.CoreLib.Shared.Character.AdeptStarterGear.GhultureFor(wizard.MagicSchoolBehavior?.MagicSchool ?? Imlight.CoreLib.Shared.Behaviors.MagicSchool.Balance);
                var mount = (wizard.InventoryBehavior?.Items ?? []).FirstOrDefault(it => (ulong) it.m_templateID == mountId);
                if (mount is null && wizard.AddItemToInventory(mountId, out mount)) {
                    Note(member.Role, $"{member.Name} has no mount: Ahziael gives it its school's Ghulture.", UltimusLink.COMMAND);
                }

                if (mount is not null) {
                    _actors[i].Tell(new UltimusMember.Equip(mount.m_globalID, "Mount", "its Ghulture"));
                    continue;
                }
            }

            // Developers' and testers' gear never stays on (a Deckathalon deck stays until a fair deck replaces it).
            if (BotGear.ForbiddenWorn(wizard).FirstOrDefault() is { Item: not null } banned) {
                _actors[i].Tell(new UltimusMember.Unequip(banned.Item.m_globalID, banned.Slot, banned.Name));
                continue;
            }

            // One piece at a time: the swap that best brings the whole outfit toward a Balance solo wizard's goals (enough
            // resist, accuracy and health, power pips toward 100%, then damage and pierce), with its reason in his thoughts.
            if (BotGear.BestSwap(wizard, GearGoals(wizard)) is { Item: not null } swap) {
                ThinkAboutSwap(member, swap, GearGoals(wizard));
                _gearChangedAt = DateTime.UtcNow;
                _actors[i].Tell(new UltimusMember.Equip(swap.Item.m_globalID, swap.Slot, swap.Piece.Name));
                continue;
            }

            ReviewOutfit(member, wizard);

            // Gear whose stats it can't all read is left as it is, and said once, so it can be looked into.
            foreach (var item in wizard.InventoryBehavior?.Items ?? []) {
                var id = (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);
                if (BotGear.Read(id) is { } unread && BotGear.PartlyRead(id) && _unreadNoted.Add(id)) {
                    Note(member.Role, $"{member.Name} can't read all the stats of {unread.Name}, so it leaves that piece as it is.", UltimusLink.NOTABLE);
                }
            }

            // Always the Essential badge as their title, picked as a player picks it, and picked again if anything changes
            // it (Niduus, 2026-11-13: "locked to the Essential badge for display"; it was the newest story badge).
            if (BotBadge.ToPick(wizard) is var essential and not 0
                && (_badgeAsked.GetValueOrDefault(member.Role) != essential || DateTime.UtcNow >= _badgeAskedAt.GetValueOrDefault(member.Role))) {
                _badgeAsked[member.Role] = essential;
                _badgeAskedAt[member.Role] = DateTime.UtcNow.AddMinutes(1);
                Note(member.Role, $"{member.Name} wears the {BotBadge.ESSENTIAL_NAME} badge.");
                _actors[i].Tell(new UltimusMember.SelectBadge(essential));
            }

            // New spells learned (or a new deck worn) since the deck was made: the deck again, for the fight it was made for.
            var known = (wizard.SpellbookBehavior?.LearnedSpellTemplateIds?.Count ?? 0) * 1000 + (wizard.SpellbookBehavior?.MaxSpells ?? 0);
            if (_deckKnown.GetValueOrDefault(member.Role) != known) {
                PrepareDeck(i, _target?.Kind == UltimusPlanner.FIGHT ? UltimusScout.SituationOf(_target.Template) : BotCards.Situation.Unknown, force: true);
            }

            KeepItemCards(i);
        }
    }

    // What was last clicked for each wizard's item cards, and when, so the same clicks are not sent again while the server
    // is still saving them.
    private readonly Dictionary<string, (string Clicks, DateTime At)> _itemCardsSent = new(StringComparer.Ordinal);

    /// <summary>
    /// Turns off the item cards its gear gives that it never plays, and turns back on the ones it plays, as a player clicks
    /// them in the deck window (Niduus, 2026-11-13: "They're also able to disable item cards"). Ahziael shows them crossed out.
    /// </summary>
    private void KeepItemCards(int index) {
        var member = _team[index];
        if (member.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)) {
            return;
        }

        var (deckId, clicks) = BotCards.ItemCardClicks(wizard, StyleOf(member));
        if (clicks.Count == 0) {
            _itemCardsSent.Remove(member.Role);
            return;
        }

        var key = deckId + ":" + string.Join(",", clicks.Select(c => $"{c.Id}{(c.Off ? "-" : "+")}"));
        if (_itemCardsSent.TryGetValue(member.Role, out var sent) && sent.Clicks == key && DateTime.UtcNow - sent.At < TimeSpan.FromMinutes(1)) {
            return;
        }

        _itemCardsSent[member.Role] = (key, DateTime.UtcNow);
        var said = clicks.GroupBy(c => (c.Id, c.Off, c.Why)).Select(g =>
            $"{(g.Key.Off ? "off" : "on")}: {BotCards.Describe([(g.Key.Id, g.Count())])} ({g.Key.Why})");
        Note(member.Role, $"{member.Name} turns item cards {string.Join("; ", said)}.");
        _actors[index].Tell(new UltimusMember.ItemCards(deckId, [.. clicks.Select(c => (c.Id, c.Off))]));
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
        _actors[index].Tell(new UltimusMember.EditDeck(deckItem.m_globalID, take, put, "for " + key));
    }

    // ---------------------------------------------------------------------
    // Shopping
    // ---------------------------------------------------------------------

    /// <summary>A trip to a shop in this world that sells better gear one of them can pay for, or null.</summary>
    private UltimusPlanner.Target ShoppingTarget(List<UltimusMember.Status> group) {
        if (DateTime.UtcNow < _nextShopping) {
            return _shopTrip;
        }

        _nextShopping = DateTime.UtcNow.AddSeconds(10);
        _shopTrip = group.Any(m => m.Wizard?.ZoneIsInstance == true) ? null : FindShopTrip(group);
        if (_shopTrip?.Kind is not (UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL)) {
            _tripPiece = 0;
        }

        if (_shopTrip is null) {
            _nextShopping = DateTime.UtcNow.AddMinutes(5);
        }

        return _shopTrip;
    }

    /// <summary>
    /// A shopping trip to another area waits while his story step is right here (Niduus, 2026-11-14: he ran off to The Oasis
    /// for a Jewel of Instinct while the Payback quest giver stood in front of him): a player turns in or finishes what is
    /// in front of them first, and shops when the story takes them away from here anyway. A shop in this area stays fine.
    /// </summary>
    private UltimusPlanner.Target NotMidStep(UltimusPlanner.Target trip) {
        if (trip is null || trip.Kind is not (UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL)
            || _leaderLastTarget is not { } story || string.IsNullOrEmpty(story.Quest)
            || story.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL or UltimusPlanner.NONE or UltimusPlanner.WAIT
            || story.Key?.StartsWith("shop", StringComparison.Ordinal) == true || story.Key?.StartsWith("bazaar", StringComparison.Ordinal) == true) {
            return trip;
        }

        Think($"I could go {trip.What}, but my story step ({story.What}) is right here: I finish it first and shop when the story " +
              "takes me out of this area.", "shop-wait|" + story.Key);

        return null;
    }

    // Trips started to another area for one piece; after two with no sale, that piece is not looked for again (audit A3:
    // the Jewel of Instinct trip again on v0.10.855).
    private readonly Dictionary<uint, int> _tripsFor = [];
    private uint _tripPiece;

    private UltimusPlanner.Target FindShopTrip(List<UltimusMember.Status> group) {
        var leader = group[0];
        var world = (leader.Zone ?? "").Split('/')[0];
        var live = MinimapIndex.In(leader.ZoneActor);
        foreach (var member in group) {
            if (member.Wizard is not { } wizard) {
                continue;
            }

            // What he may spend: past his reserve for the next world's gear (Gold.ReservePerLevel), and a share kept.
            var gold = (int) (Math.Max(0, (wizard.GameStats?.m_currentGold ?? 0) - GoldReserve(member)) * (1 - GOLD_KEPT));
            var owned = (wizard.InventoryBehavior?.Items ?? []).Select(i => (uint) Math.Min((ulong) i.m_templateID, uint.MaxValue)).ToHashSet();
            // What it can pay for; and, when nothing is, a clearly better piece it can't pay for yet (Niduus, 2026-11-13:
            // "why is Ultimus wearing this instead of Adept robe?": the Adept Sorcerer's Robe costs 21,880 gold, more than
            // he had, so he kept a dropped Cloak of the Watchful). Ahziael gives the difference, as it gives gold so
            // shopping never holds the quests up.
            var affordable = BotGear.ShopUpgrades(wizard, StyleOf(member), gold);
            var upgrades = affordable.Count > 0 || !GoldGifts ? affordable
                : BotGear.ShopUpgrades(wizard, StyleOf(member), MAX_GEAR_TOP_UP).Where(u => u.Gain >= BIG_GAIN).ToList();
            foreach (var (piece, vendors, _) in upgrades) {
                if (owned.Contains(piece.Id) || _shopSkipped.Contains(piece.Id) || NotSold().Contains(piece.Id)) {
                    continue;
                }

                // A shopkeeper of it here first: one that really sells it now (Niduus's log, 2026-11-13: Wul'yahm in The
                // Oasis was asked twice each for a Lifebringer's Athame and a Jewel of Instinct the vendor list gives him,
                // and refused both, as he does not sell them).
                var here = live.FirstOrDefault(m => vendors.Contains((uint) m.Template));
                if (here is not null && !Sells(here, piece.Id)) {
                    Note(member.Role, $"{member.Name}: {here.Name} does not sell {piece.Name} after all; it looks elsewhere.");
                    _shopSkipped.Add(piece.Id);
                    NotSoldHere(piece.Id, $"{piece.Name} (not sold by {here.Name})");
                    continue;
                }

                // Ahziael gives what it lacks only when it stands at a shop that sells it.
                if (GoldGifts && here is not null && piece.Gold > gold && wizard.GameStats is { } stats) {
                    var give = (int) Math.Ceiling(piece.Gold / (1 - GOLD_KEPT)) - stats.m_currentGold + 1;
                    if (give > 0) {
                        wizard.AddGold(give);
                        Count("gold given", give);
                        Note(member.Role, $"{member.Name} can't afford {piece.Name} ({piece.Gold} gold), a clear upgrade; Ahziael gives it {give} gold.",
                            UltimusLink.COMMAND);
                    }
                }

                if (here is not null) {
                    return new UltimusPlanner.Target(UltimusPlanner.SHOP, here.Object.m_location.X, here.Object.m_location.Y,
                        $"{member.Name} buys {piece.Name} from {here.Name}", Template: here.Template, GlobalId: here.Object.m_globalID.Full,
                        Key: $"shop|{member.Role}|{piece.Id}");
                }

                // Else one in another area of this world.
                var zone = VendorZones(vendors).FirstOrDefault(z => string.Equals(z.Split('/')[0], world, StringComparison.OrdinalIgnoreCase));
                if (zone is not null && UltimusPlanner.Travel(leader, zone, $"shopping for {member.Name}'s {piece.Name}") is { Problem.Length: 0 } way) {
                    if (_tripsFor.GetValueOrDefault(piece.Id) >= 2) {
                        Note(member.Role, $"{member.Name}: two trips for {piece.Name} and no sale; it stops looking for it.");
                        _shopSkipped.Add(piece.Id);
                        NotSoldHere(piece.Id, $"{piece.Name} (two trips, no sale)");
                        continue;
                    }

                    if (_tripPiece != piece.Id) {
                        _tripPiece = piece.Id;
                        _tripsFor[piece.Id] = _tripsFor.GetValueOrDefault(piece.Id) + 1;
                    }

                    return way;
                }
            }
        }

        return null;
    }

    // Whether the shopkeeper standing here sells the item now (its live shop list, with the Customs tab's changes).
    private static bool Sells(MinimapIndex.Mark vendor, uint itemId) {
        try {
            return vendor.Entity?.GetComponentOfType<Zone.Components.InteractVendorComponent>() is not { } shop
                   || shop.HasItem(new Imcodec.Types.GID((ulong) itemId));
        }
        catch (Exception) {
            return true;
        }
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

    // Gold from Ahziael only when Niduus turns it on (playbook Gold.Gifts = 1); otherwise he earns and plans it as a player.
    private static bool GoldGifts => UltimusPlaybook.Value("Gold.Gifts") >= 0.5;

    private void ShopHere(List<UltimusMember.Status> group, UltimusPlanner.Target target) {
        if (target.Key.StartsWith("sell|", StringComparison.Ordinal)) {
            SellHere(target);
            return;
        }

        if (target.Key.StartsWith("bazaar|", StringComparison.Ordinal)) {
            BazaarHere(target);
            return;
        }

        var parts = target.Key.Split('|');
        var member = _team.FirstOrDefault(m => m.Role == parts[1]);
        if (member is null || !uint.TryParse(parts[2], out var itemId)) {
            return;
        }

        var index = _team.IndexOf(member);
        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
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
            _actors[index].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "shopping"));
            HoldRecall(6);
        }

        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[index],
            new UltimusMember.Buy(target.GlobalId, itemId, BotGear.Read(itemId)?.Name ?? itemId.ToString()), Self);
        // The next one in a moment; the new piece goes on at the next look at the backpack.
        _shopTrip = null;
        _nextShopping = DateTime.UtcNow.AddSeconds(5);
        _nextGearLook = DateTime.UtcNow.AddSeconds(3);
    }

}
