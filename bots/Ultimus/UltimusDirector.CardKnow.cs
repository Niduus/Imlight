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
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What Ultimus knows about treasure cards (Niduus, 2026-11-17: "advance Ultimus' thinking so he is better equipped with
/// knowledge of Treasure cards and what to use, when and where to get them"). From the real game's data only:
/// <list type="bullet">
/// <item>what each card is for and when it is worth playing (TcUse), so the best ones for the fights ahead go into his
/// deck first and the ones that can't help stay out;</item>
/// <item>where they are sold: the Bazaar, which stocks every card (UltimusDirector.CardPlan.cs, Niduus 2026-11-17: "Also
/// able to buy them from bazaar"), and the game's own treasure card vendors of each world (VendorGuide, never a test shop
/// or a custom vendor), so with a hard fight ahead and few cards left he goes to the nearest one in his world before it,
/// marks his spot and recalls back (RestockTrip, when the Bazaar is off);</item>
/// <item>all of it written down for Niduus in treasure-cards.txt beside his other notes.</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    /// <summary>What a treasure card is for and how much he wants it now; null when it can't help him (too high a level, broken).</summary>
    internal sealed record TcUseOf(string Kind, int Score, string When);

    // The kind of an enchantment card: "enchant-damage", "enchant-accuracy" and so on.
    internal const string ENCHANT_KIND = "enchant-";

    internal static TcUseOf TcUse(uint templateId, string school, int level, IReadOnlyCollection<string> foes) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not SpellTemplate template || template.m_levelRestriction > level
            || BotCards.IsDevSpell(templateId) || BrokenCards.Skip(templateId) || SpellFactory.GetSpell(templateId) is not { } spell) {
            return null;
        }

        // An enchantment (Giant, Gargantuan, Sniper, Tough...): played on another card in battle (UltimusTactics.EnchantFor).
        // Damage ones come first and the strongest wins (Niduus, 2026-11-17: "use/buy the strongest, being Vanilla Gargantuan").
        if (BotEnchants.Of(template) is { } enchant) {
            return enchant.Kind == BotEnchants.OTHER ? null
                : new TcUseOf(ENCHANT_KIND + enchant.Kind, enchant.Worth, $"an enchantment: {enchant.Plain}" + (enchant.Kind == BotEnchants.DAMAGE
                    ? "; I play it on my biggest hit, best on one that strikes every enemy" : ""));
        }

        if (RewrittenAI.Read(spell) is not { } card || BotCards.NotVanilla(card)) {
            return null;
        }

        foes ??= [];
        var cardSchool = (string) template.m_sMagicSchoolName ?? "";
        var own = string.Equals(cardSchool, school, StringComparison.OrdinalIgnoreCase);
        var weakTo = foes.Select(RewrittenAI.Opposite).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hitsWeak = weakTo.Contains(card.DamageSchool) || weakTo.Contains(cardSchool);
        // What the creatures ahead hit with: their own school, or Myth for a shield read as "All".
        var resisted = foes.Any(f => string.Equals(f, card.DamageSchool, StringComparison.OrdinalIgnoreCase));

        if (card.Hit) {
            var score = card.BaseDamage * (hitsWeak ? 2 : 1) * (resisted ? 1 : 2) / 2 + (own ? 40 : 0) + (card.HitsAll ? 60 : 0);
            var when = card.HitsAll
                ? "with two or more enemies up, once a blade is on; it saves my own hits for the boss"
                : "on the boss or the toughest enemy, best after a blade, when I have the pips and it can finish or nearly finish it";
            return new TcUseOf("hit", Math.Max(1, score), when + (hitsWeak ? $" (they are weak to {card.DamageSchool})" : "")
                                                       + (resisted ? $" (careful: they resist {card.DamageSchool})" : ""));
        }

        if (card.Heal && !card.HealTakesFromEnemy) {
            return new TcUseOf("heal", 80 + (card.HealAmount / 4) + (card.HealWholeTeam ? 30 : 0),
                "when I am under half health and a big hit is coming, before it lands; it keeps my own heals for later");
        }

        // A shield he can cast on a friend can be cast on himself too (Myth Shield, Fire Shield): it was read as "no use".
        if (card.Shield) {
            var matches = foes.Count == 0 ? 0
                : card.ShieldSchools.Length == 0 ? foes.Count(f => string.Equals(f, card.ShieldSchool, StringComparison.OrdinalIgnoreCase))
                : card.ShieldSchools.Count(x => x == "All" || foes.Contains(x, StringComparer.OrdinalIgnoreCase));
            return matches == 0 ? new TcUseOf("shield", 10, "only against creatures that hit with " + DescribeShield(card))
                : new TcUseOf("shield", 70 + (matches * 20), "the round before their big hit lands, against " + DescribeShield(card));
        }

        if (card.Blade && !card.BladeOnlyTeammate) {
            var mine = string.IsNullOrEmpty(card.BladeSchool) || string.Equals(card.BladeSchool, school, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(card.BladeSchool, "All", StringComparison.OrdinalIgnoreCase);
            return new TcUseOf("blade", mine ? 50 + card.BladePercent : 5,
                mine ? "the round before my big hit, while I gather pips" : $"not for me: it only boosts {card.BladeSchool}");
        }

        if (card.Trap) {
            return new TcUseOf("trap", 35 + card.TrapPercent / 2, "on the boss the round before my big hit");
        }

        if (card.Weakness || card.Stun || card.RemovesCharms) {
            return new TcUseOf("control", 45, card.Stun ? "to stop the boss for a round when its big hit is coming"
                : card.Weakness ? "on the enemy that hits hardest, before its big hit" : "on an enemy that has stacked blades");
        }

        return null;
    }

    private static string DescribeShield(RewrittenAI.Card card)
        => card.ShieldSchools.Length > 0 ? string.Join(", ", card.ShieldSchools) : card.ShieldSchool is { Length: > 0 } s ? s : "any school";

    // The treasure cards he has, best for the fights ahead first; the ones that can't help are left out.
    private List<uint> CardsForDeck(Wizard wizard, IEnumerable<uint> owned) {
        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var foes = FoeSchools();

        var cards = owned.Select(id => (Id: id, Use: TcUse(id, school, level, foes)))
            .Where(c => c.Use is { Score: > 10 }).ToList();
        // Of the enchantments, only the strongest of each kind goes in (Giant before Strong, Sniper before Keen Eyes).
        var strongest = cards.Where(c => c.Use.Kind.StartsWith(ENCHANT_KIND, StringComparison.Ordinal))
            .GroupBy(c => c.Use.Kind).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Use.Score).First().Id);

        return cards.Where(c => !strongest.TryGetValue(c.Use.Kind, out var best) || best == c.Id)
            .OrderByDescending(c => c.Use.Score)
            .Select(c => c.Id).ToList();
    }

    // A vanilla treasure card vendor: never the Bazaar (it stocks every card), a test shop or a custom vendor.
    private static bool VanillaTcVendor(VendorGuide.Npc npc)
        => npc.TreasureCards is { Count: > 0 } && npc.Test.Length == 0 && !string.IsNullOrEmpty(npc.Object)
           && !npc.Zones.Any(z => z.Length > 0 && z[0].Contains("Bazaar", StringComparison.OrdinalIgnoreCase))
           && !npc.Name.Contains("Abner", StringComparison.OrdinalIgnoreCase);

    // The cards a vendor sells that would help him (best first), with their prices.
    private static List<(string Name, int Score, int Price)> UsefulAt(VendorGuide.Npc npc, string school, int level, IReadOnlyCollection<string> foes) {
        var list = new List<(string Name, int Score, int Price)>();
        SpiralDB.TreasureCardInventories.TryGetValue(npc.Id, out var stock);
        foreach (var code in npc.TreasureCards) {
            if (SpellFactory.GetSpell(code) is not { } spell || TcUse(spell.m_templateID, school, level, foes) is not { Score: > 10 } use) {
                continue;
            }

            var price = stock?.TreasureCards?.FirstOrDefault(c => c.SpellName == code)?.Price ?? 0;
            list.Add((CardName(spell.m_templateID), use.Score, price));
        }

        return [.. list.OrderByDescending(c => c.Score)];
    }

    // How many cards he restocks to before a hard fight; below RESTOCK_BELOW he goes out of his way for them.
    private const int RESTOCK_BELOW = 4;
    private const int RESTOCK_GOLD = 500;
    private string _tcTripZone;
    private DateTime _tcTripAt = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _tcZoneTried = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan s_tcZoneRest = TimeSpan.FromHours(3);

    /// <summary>
    /// A hard fight ahead: his next step goes into a dungeon, a creature beat him or hurt him badly lately, or his step's
    /// fight is a boss. Then treasure cards are worth a trip.
    /// </summary>
    private bool HardFightAhead(UltimusMember.Status leader) {
        var step = _target ?? _leaderLastTarget;
        if (step is not null && !string.IsNullOrEmpty(step.To) && OverlayQuestRoute.IsDungeon(step.To)) {
            return true;
        }

        if (step is not null && BossNext(leader, step)) {
            return true;
        }

        return UltimusFoes.All().Any(f => f.Tough && DateTime.UtcNow - f.Last < TimeSpan.FromHours(12));
    }

    /// <summary>
    /// The trip to the nearest area of his world with a vanilla treasure card vendor that sells cards he can use, when he
    /// is low on cards with a hard fight ahead and gold past his reserve. Null when none is needed or known.
    /// </summary>
    private UltimusPlanner.Target RestockTrip(UltimusMember.Status leader, int owned, long spend) {
        if (owned >= RESTOCK_BELOW || spend < RESTOCK_GOLD || leader.Wizard is not { } wizard || _leaderLastTarget?.Key?.StartsWith(PARTY_KEY, StringComparison.Ordinal) == true) {
            return null;
        }

        // Arrived in the trip's area and nothing here sold: not again for a while.
        if (_tcTripZone is not null && OverlayQuestTracker.SameZone(_tcTripZone, leader.Zone)) {
            _tcZoneTried[_tcTripZone] = DateTime.UtcNow;
            Think($"I came to {UltimusText.Zone(_tcTripZone)} for treasure cards, but its vendor has nothing for me now. I leave it for a while.",
                "tc-none|" + _tcTripZone);
            _tcTripZone = null;
            return null;
        }

        if (!HardFightAhead(leader)) {
            return null;
        }

        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var foes = FoeSchools();
        var world = (leader.Zone ?? "").Split('/')[0];
        var vendors = VendorGuide.All().Where(VanillaTcVendor)
            .Where(n => n.Zones.Any(z => z.Length > 0 && z[0].StartsWith(world + "/", StringComparison.OrdinalIgnoreCase)))
            .Where(n => UsefulAt(n, school, level, foes).Count > 0).ToList();
        if (vendors.Count == 0) {
            return null;
        }

        var objects = vendors.Select(n => n.Object).ToHashSet(StringComparer.Ordinal);
        var areas = HealZones($"tcvendor|{school}|{level / 5}|{leader.Zone}", leader.Zone, objects.Contains);
        if (areas?.FirstOrDefault(a => a.Hops > 0 && !(_tcZoneTried.TryGetValue(a.Zone, out var at) && DateTime.UtcNow - at < s_tcZoneRest)) is not { Zone: not null } best) {
            return null;
        }

        var vendor = vendors.FirstOrDefault(n => n.Zones.Any(z => z.Length > 0 && OverlayQuestTracker.SameZone(z[0], best.Zone)));
        var cards = vendor is null ? [] : UsefulAt(vendor, school, level, foes);
        var why = "for treasure cards before the hard fights ahead";
        if (_tcTripZone != best.Zone) {
            _tcTripZone = best.Zone;
            _tcTripAt = DateTime.UtcNow;
            Think($"I have only {owned} treasure card(s) and hard fights ahead" + (foes.Count > 0 ? $" (creatures of {string.Join(", ", foes)})" : "") +
                  $". The nearest vendor of this world with cards I can use is {vendor?.Name ?? "a treasure card vendor"} in {UltimusText.Zone(best.Zone)}" +
                  (cards.Count > 0 ? $" ({string.Join(", ", cards.Take(4).Select(c => c.Price > 0 ? $"{c.Name} {c.Price:N0} gold" : c.Name))})" : "") +
                  ". I mark my spot, stock up there and Recall back, as a player does before a dungeon.", "tc-trip|" + best.Zone);
        }

        return UltimusPlanner.Travel(leader, best.Zone, why, "tcvendorzone|" + best.Zone);
    }

    private DateTime _tcBookWritten = DateTime.MinValue;

    /// <summary>
    /// His treasure card notes for Niduus, treasure-cards.txt beside his other notes: the cards he holds and when he plays
    /// each, and the vanilla vendors of his world with the cards there that would help him. At most twice an hour.
    /// </summary>
    private void WriteCardBook(UltimusMember.Status leader) {
        if (DateTime.UtcNow - _tcBookWritten < TimeSpan.FromMinutes(30) || leader.Wizard is not { } wizard) {
            return;
        }

        _tcBookWritten = DateTime.UtcNow;
        try {
            var school = BotGear.SchoolOf(wizard);
            var level = BotGear.LevelOf(wizard);
            var foes = FoeSchools();
            var lines = new List<string> {
                $"TREASURE CARDS of {leader.Name} ({school}, level {level}), {DateTime.Now:yyyy-MM-dd HH:mm}. From the real game's data only.",
                foes.Count > 0 ? $"Fights ahead: creatures of {string.Join(", ", foes)}." : "Fights ahead: none known yet.",
                "",
                "CARDS I HOLD, and when I play them:",
            };
            foreach (var (id, copies) in OwnedCopies(wizard).OrderByDescending(c => TcUse(c.Key, school, level, foes)?.Score ?? 0)) {
                var use = TcUse(id, school, level, foes);
                var needs = CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t && t.m_levelRestriction > level ? t.m_levelRestriction : 0;
                lines.Add(use is null ? needs > 0 ? $"  {CardName(id)} x{copies}: needs level {needs} (I am {level}): kept in my book, out of my deck, until then."
                        : $"  {CardName(id)} x{copies}: no use to me now (not a card I can play, or not a real card)."
                    : $"  {CardName(id)} x{copies} ({use.Kind}, worth {use.Score}): {use.When}.");
            }

            lines.Add("");
            lines.Add($"MY PLAN for the fights ahead ({PlanRoom(wizard)} treasure card slots), and what I still need:");
            foreach (var (card, missing) in CardPlanNow(wizard, out _)) {
                lines.Add($"  {card.Name} x{card.Copies} ({card.Kind}, worth {card.Score}): {card.When}." + (missing > 0 ? $" Need {missing} more." : " Have them."));
            }

            lines.Add("  With a hard fight ahead and 3 or more missing, I buy them at the Bazaar in Olde Town (it stocks every card).");
            var world = (leader.Zone ?? "").Split('/')[0];
            lines.Add("");
            lines.Add($"WHERE ELSE TO GET THEM in {world} (the game's own vendors):");
            foreach (var npc in VendorGuide.All().Where(VanillaTcVendor)
                         .Where(n => n.Zones.Any(z => z.Length > 0 && z[0].StartsWith(world + "/", StringComparison.OrdinalIgnoreCase)))) {
                var useful = UsefulAt(npc, school, level, foes);
                var where = string.Join(", ", npc.Zones.Where(z => z.Length > 1).Select(z => z[1]).Distinct());
                lines.Add($"  {npc.Name} ({where}): " + (useful.Count == 0 ? "nothing for me now."
                    : string.Join(", ", useful.Take(6).Select(c => c.Price > 0 ? $"{c.Name} {c.Price:N0} gold" : c.Name)) + "."));
            }

            File.WriteAllLines(Path.Combine(UltimusSeat.Current.Folder, "treasure-cards.txt"), lines);
        }
        catch (Exception) {
            // Notes only: never in the way of play.
        }
    }

}
