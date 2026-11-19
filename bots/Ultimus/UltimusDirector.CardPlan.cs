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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Bazaar;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// A treasure card plan for the fights ahead, and treasure cards bought at the Bazaar (Niduus, 2026-11-17: "Ultimus must
/// make his deck better. Have him have better knowledge and planning of treasure cards. Also able to buy them from
/// bazaar"). He lost to Meowiarty in Big Ben five times out of six with 76,000 gold and two treasure cards.
/// <list type="bullet">
/// <item>The plan (PlanCards): for the creatures ahead (their schools, from the game's data), the treasure cards a player
/// would bring, by kind: the best hits (what they are weak to hits twice as hard), a blade and a trap for the big hit, a
/// shield against what they hit with, a heal, and a card that stops a big hit; stacked a few copies each, as many as his
/// deck's treasure card slots hold.</item>
/// <item>The Bazaar (Elik's shop in Olde Town) stocks every treasure card. With a hard fight ahead (a dungeon, a boss, a
/// rival that beat him) and at least three plan cards missing, he marks his spot, goes there, buys what the plan is
/// missing with gold past his reserve, puts them in his deck and recalls back. Only real game cards; never a test card.</item>
/// <item>After a lost fight, "cards" is a remedy he weighs (UltimusDirector.Losses.cs).</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // The type bits the game puts on a treasure card's id in the Bazaar (AuctionHouseService).
    private const ulong BAZAAR_CARD = 0x60UL << 40;
    // At least this many plan cards missing before a trip to the Bazaar is worth it.
    private const int BAZAAR_CARDS_MIN = 3;
    // The most treasure cards the plan holds when the deck's slots are not known.
    private const int PLAN_MAX = 16;

    private DateTime _nextBazaarCardLook = DateTime.MinValue;
    private UltimusPlanner.Target _bazaarCardTrip;
    private string _bazaarCardTripZone;

    // When he set off for the Bazaar for cards: the trip holds until he is there (or this long), however the next area
    // reads his fights. Ultimus, 2026-11-18 20:32-22:26: from Hametsu Village he set off, in Jade Palace on the way the
    // trip was weighed again, no hard fight there, so he Recalled back to Hametsu, where it was weighed again and he set
    // off again: every 6 seconds for two hours, until the watchdog stopped him.
    private DateTime _bazaarCardTripSince = DateTime.MinValue;
    private static readonly TimeSpan s_bazaarCardTripHold = TimeSpan.FromMinutes(20);
    private readonly Dictionary<uint, DateTime> _bazaarCardSkipped = [];
    private static readonly TimeSpan s_bazaarCardSkip = TimeSpan.FromHours(1);
    private string _cardPlanSaid = "";
    private static List<uint> s_treasureCards;

    /// <summary>One card of the plan: what it is for, how many copies he wants, and when he plays it.</summary>
    internal sealed record PlannedCard(uint Id, string Name, string Kind, int Score, int Copies, string When);

    /// <summary>
    /// Every real treasure card of the game (its TreasureCards folder), one per name and school, the same one the Bazaar
    /// sells (the plain "&lt;name&gt; TC" first).
    /// </summary>
    private static List<uint> AllTreasureCards() {
        if (s_treasureCards is not null) {
            return s_treasureCards;
        }

        var seen = new HashSet<string>();
        var list = new List<uint>();
        var spells = SpellFactory.AllSpellTemplates()
            .OrderBy(pair => ((string) pair.Template.m_name)?.EndsWith(" TC", StringComparison.Ordinal) == true ? 0 : 1)
            .ThenBy(pair => ((string) pair.Template.m_name)?.Length ?? int.MaxValue)
            .ThenBy(pair => (string) pair.Template.m_name, StringComparer.Ordinal);
        foreach (var (path, template) in spells) {
            string name = template.m_name;
            if (string.IsNullOrEmpty(name) || !template.m_Treasure
                || path?.Replace('\\', '/').Contains("TreasureCards/", StringComparison.OrdinalIgnoreCase) != true
                || string.IsNullOrEmpty((string) template.m_sMagicSchoolName) || string.IsNullOrEmpty((string) template.m_displayName)
                || !seen.Add($"{template.m_displayName}|{template.m_sMagicSchoolName}")
                || SpellFactory.GetSpell(name) is not { } spell) {
                continue;
            }

            list.Add((uint) spell.m_templateID);
        }

        return s_treasureCards = list;
    }

    // How many different cards of each kind the plan holds, and how many copies of each, in the order slots are filled.
    private static readonly (string Kind, int Cards, int Copies)[] s_planShape = [
        ("hit", 2, 3), (ENCHANT_KIND + BotEnchants.DAMAGE, 1, 4), ("blade", 1, 2), ("heal", 1, 2), ("shield", 1, 2), ("trap", 1, 2), ("control", 1, 1),
    ];

    /// <summary>
    /// The treasure cards he wants for the fights ahead, best first, filling <paramref name="room"/> slots: one of each
    /// kind first (a hit, a blade, a heal, a shield, a trap, a stop), then more copies, as a player stacks a boss deck.
    /// </summary>
    internal static List<PlannedCard> PlanCards(string school, int level, IReadOnlyCollection<string> foes, int room, IEnumerable<uint> pool) {
        // One per name: two versions of a card with the same name (Storm Hound twice in his plan, 2026-11-17) are one card.
        var scored = pool.Distinct().Select(id => (Id: id, Use: TcUse(id, school, level, foes)))
            .Where(c => c.Use is { Score: > 10 })
            .GroupBy(c => CardName(c.Id), StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(c => c.Use.Score).First()).ToList();
        var picks = new List<(uint Id, string Kind, int Score, int Copies, string When)>();
        foreach (var (kind, cards, copies) in s_planShape) {
            foreach (var c in scored.Where(c => c.Use.Kind == kind).OrderByDescending(c => c.Use.Score).Take(cards)) {
                picks.Add((c.Id, kind, c.Use.Score, copies, c.Use.When));
            }
        }

        var counts = new Dictionary<uint, int>();
        var total = 0;
        for (var round = 0; round < s_planShape.Max(x => x.Copies) && total < room; round++) {
            foreach (var p in picks) {
                if (total >= room || counts.GetValueOrDefault(p.Id) >= p.Copies || round >= p.Copies) {
                    continue;
                }

                counts[p.Id] = counts.GetValueOrDefault(p.Id) + 1;
                total++;
            }
        }

        return [.. picks.Where(p => counts.ContainsKey(p.Id))
            .Select(p => new PlannedCard(p.Id, CardName(p.Id), p.Kind, p.Score, counts[p.Id], p.When))];
    }

    // How many treasure card slots his plan fills: the deck's own slots, else a player's usual stack.
    private static int PlanRoom(Wizard wizard) {
        var slots = wizard.SpellbookBehavior?.MaxTreasureCards ?? 0;
        return slots > 0 ? Math.Min(slots, PLAN_MAX) : TREASURE_CARDS_KEPT;
    }

    /// <summary>The plan for his fights ahead now, with what he still lacks of each card.</summary>
    private List<(PlannedCard Card, int Missing)> CardPlanNow(Wizard wizard, out IReadOnlyCollection<string> foes) {
        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        foes = FoeSchools();
        var plan = PlanCards(school, level, foes, PlanRoom(wizard), AllTreasureCards());
        var owned = OwnedCopies(wizard);

        return [.. plan.Select(c => (c, Math.Max(0, c.Copies - owned.GetValueOrDefault(c.Id))))];
    }

    // The plan said in his thoughts when it changes, so Niduus can read why each card is there.
    private void SayCardPlan(List<(PlannedCard Card, int Missing)> plan, IReadOnlyCollection<string> foes) {
        var key = string.Join(",", plan.Select(p => $"{p.Card.Id}x{p.Card.Copies}"));
        if (plan.Count == 0 || key == _cardPlanSaid) {
            return;
        }

        _cardPlanSaid = key;
        Think("My treasure card plan for the fights ahead" + (foes.Count > 0 ? $" (creatures of {string.Join(", ", foes)})" : "") + ": " +
              string.Join("; ", plan.Select(p => $"{p.Card.Name} x{p.Card.Copies} ({p.Card.Kind}: {p.Card.When})")) + ".", "tc-plan|" + key);
    }

    /// <summary>
    /// The Bazaar trip for treasure cards: with a hard fight ahead (or a fight lost lately) and at least three cards of his
    /// plan missing, the way to Elik in Olde Town, or the buy once he is there. Null when not needed, or the Bazaar is off.
    /// </summary>
    private UltimusPlanner.Target BazaarCardsTarget(List<UltimusMember.Status> group) {
        var leader = group[0];
        var tripElsewhere = _bazaarCardTrip is not null && !string.Equals(_bazaarCardTripZone, leader.Zone, StringComparison.OrdinalIgnoreCase);
        // On the way: only the way on from this area is worked out again, not whether to go.
        if (tripElsewhere && DateTime.UtcNow - _bazaarCardTripSince < s_bazaarCardTripHold
            && !OverlayQuestTracker.SameZone(leader.Zone ?? "", BAZAAR_ZONE)) {
            _bazaarCardTripZone = leader.Zone;
            _bazaarCardTrip = UltimusPlanner.Travel(leader, BAZAAR_ZONE, "the Bazaar, for treasure cards", "bazaarcards");
            _nextBazaarCardLook = DateTime.UtcNow.AddSeconds(10);
            return _bazaarCardTrip;
        }

        if (DateTime.UtcNow < _nextBazaarCardLook && !tripElsewhere) {
            return _bazaarCardTrip;
        }

        _nextBazaarCardLook = DateTime.UtcNow.AddMinutes(5);
        _bazaarCardTrip = null;
        _bazaarCardTripSince = DateTime.MinValue;
        _bazaarCardTripZone = leader.Zone;
        if (leader.Wizard is not { } wizard || wizard.ZoneIsInstance || _leaderLastTarget?.Key?.StartsWith(PARTY_KEY, StringComparison.Ordinal) == true) {
            return null;
        }

        var spend = (wizard.GameStats?.m_currentGold ?? 0) - GoldReserve(leader);
        if (spend <= 0 || !(HardFightAhead(leader) || UltimusMood.LostRecently)) {
            return null;
        }

        var plan = CardPlanNow(wizard, out var foes);
        SayCardPlan(plan, foes);
        var missing = plan.Where(p => p.Missing > 0
                                      && !(_bazaarCardSkipped.TryGetValue(p.Card.Id, out var at) && DateTime.UtcNow - at < s_bazaarCardSkip)).ToList();
        if (missing.Sum(p => p.Missing) < BAZAAR_CARDS_MIN) {
            return null;
        }

        switch (BazaarLink.Visit()) {
            case BazaarState.Disabled:
                Think("The Bazaar is turned off on this server, so I get my treasure cards from the game's own vendors.", "bazaar|off");
                return null;
            case BazaarState.Opening:
                // It wakes in about a minute, and the walk there takes longer: he sets off now and looks at its stock when
                // he gets there. Waiting for it, he walked into Big Ben first and the plan was never bought (2026-11-17 16:20).
                if (BazaarBook.Mode != BazaarMode.Unlimited) {
                    _nextBazaarCardLook = DateTime.UtcNow.AddSeconds(75);
                    return null;
                }

                Think($"I have hard fights ahead and my treasure card plan is missing {missing.Sum(p => p.Missing)} card(s) " +
                      $"({string.Join(", ", missing.Select(p => $"{p.Card.Name} x{p.Missing}"))}). The Bazaar in Olde Town stocks every card, so I mark my " +
                      "spot, buy them there and Recall back, as a player does before a boss.", "tc-bazaar|" + string.Join(",", missing.Select(p => p.Card.Id)));
                _bazaarCardTrip = UltimusPlanner.Travel(leader, BAZAAR_ZONE, "the Bazaar, for treasure cards", "bazaarcards");
                _bazaarCardTripSince = DateTime.UtcNow;
                _nextBazaarCardLook = DateTime.UtcNow.AddSeconds(10);
                return _bazaarCardTrip;
            case BazaarState.Closed:
                return null;
        }

        var buys = new List<(uint Id, int Count, int Price)>();
        long left = spend;
        foreach (var (card, need) in missing) {
            BazaarQuote quote;
            try {
                quote = BazaarLink.Find(card.Id | BAZAAR_CARD);
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: could not ask the Bazaar: {0}", Logger.Args(e.Message));
                return null;
            }

            if (quote is not { Stock: > 0, BuyPrice: > 0 }) {
                _bazaarCardSkipped[card.Id] = DateTime.UtcNow;
                continue;
            }

            var count = (int) Math.Min(Math.Min(need, quote.Stock), left / quote.BuyPrice);
            if (count <= 0) {
                continue;
            }

            buys.Add((card.Id, count, quote.BuyPrice));
            left -= (long) count * quote.BuyPrice;
        }

        if (buys.Sum(b => b.Count) < BAZAAR_CARDS_MIN) {
            return null;
        }

        var cost = buys.Sum(b => b.Count * b.Price);
        var keeper = MinimapIndex.In(leader.ZoneActor).FirstOrDefault(m => m.Entity?.GetComponentOfType<InteractAuctionHouseComponent>() is not null);
        if (keeper is not null) {
            _bazaarCardTrip = new UltimusPlanner.Target(UltimusPlanner.SHOP, keeper.Object.m_location.X, keeper.Object.m_location.Y,
                $"{leader.Name} buys treasure cards at the Bazaar", Template: keeper.Template, GlobalId: keeper.Object.m_globalID.Full,
                Key: $"bazaarcards|{leader.Role}|{string.Join(",", buys.Select(b => $"{b.Id}:{b.Count}"))}");
        }
        else {
            Think($"I have hard fights ahead and my treasure card plan is missing {missing.Sum(p => p.Missing)} card(s). The Bazaar in Olde Town has " +
                  $"them: {string.Join(", ", buys.Select(b => $"{CardName(b.Id)} x{b.Count}"))}, {cost:N0} gold in all, and my reserve stays. " +
                  "I mark my spot, buy them there and Recall back, as a player does before a boss.", "tc-bazaar|" + string.Join(",", buys.Select(b => b.Id)));
            _bazaarCardTrip = UltimusPlanner.Travel(leader, BAZAAR_ZONE, "the Bazaar, for treasure cards", "bazaarcards");
            _bazaarCardTripSince = DateTime.UtcNow;
        }

        if (_bazaarCardTrip is not null) {
            _nextBazaarCardLook = DateTime.UtcNow.AddSeconds(10);
        }

        return _bazaarCardTrip;
    }

    /// <summary>At Elik's counter: he opens the Bazaar and buys each card of the list, as a player clicks Buy.</summary>
    private void BazaarCardsHere(UltimusPlanner.Target target) {
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
                Note("Ultimus", "could not buy treasure cards at the Bazaar.");
                _bazaarCardTrip = null;
                _bazaarCardTripSince = DateTime.MinValue;
                _nextBazaarCardLook = DateTime.UtcNow.AddMinutes(15);
            }

            return;
        }

        var state = BazaarLink.Visit();
        if (state == BazaarState.Opening) {
            return;
        }

        if (state != BazaarState.Open) {
            Note("Ultimus", $"the Bazaar is not open for treasure cards ({BazaarLink.Describe()}).");
            _bazaarCardTrip = null;
            _bazaarCardTripSince = DateTime.MinValue;
            return;
        }

        var list = parts[2].Split(',').Select(x => x.Split(':'))
            .Where(x => x.Length == 2 && uint.TryParse(x[0], out _) && int.TryParse(x[1], out var n) && n > 0)
            .Select(x => (Id: uint.Parse(x[0]), Count: int.Parse(x[1]))).ToList();
        if (list.Count == 0) {
            _bazaarCardTrip = null;
            _bazaarCardTripSince = DateTime.MinValue;
            return;
        }

        Clicked(target.Key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "AuctionHouseService") is { Service: not null } open) {
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "the Bazaar"));
        }

        HoldRecall(10);
        var what = string.Join(", ", list.Select(c => $"{CardName(c.Id)} x{c.Count}"));
        Think($"At the Bazaar I buy the treasure cards my plan is missing: {what}. They go in my deck for the fights ahead.", "tc-bazaar-buy|" + parts[2]);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0],
            new UltimusMember.BuyCardsAtBazaar(target.GlobalId, [.. list.Select(c => ((ulong) c.Id | BAZAAR_CARD, c.Count))], what), Self);
        foreach (var (id, _) in list) {
            _bazaarCardSkipped[id] = DateTime.UtcNow;
        }

        Count("treasure cards bought", list.Sum(c => c.Count));
        _bazaarCardTrip = null;
        _bazaarCardTripSince = DateTime.MinValue;
        // A few seconds later the new cards go into his deck (UltimusDirector.Cards.cs).
        _nextCardLook = DateTime.UtcNow.AddSeconds(6);
        _nextBazaarCardLook = DateTime.UtcNow.AddMinutes(2);
    }

    /// <summary>The "cards" remedy after a lost fight: the plan's cards bought at once, even in push mode.</summary>
    private void CardsRemedy() {
        _nextBazaarCardLook = DateTime.UtcNow;
        _nextCardLook = DateTime.UtcNow;
        _bazaarCardSkipped.Clear();
        _gearPushUntil = DateTime.UtcNow.AddMinutes(30);
    }

    /// <summary>How many of his treasure cards would help in the fights ahead (the "cards" remedy weighs it).</summary>
    private int UsefulCardsOwned(Wizard wizard) {
        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var foes = FoeSchools();

        return OwnedCopies(wizard).Where(c => TcUse(c.Key, school, level, foes) is { Score: > 10 }).Sum(c => c.Value);
    }

}
