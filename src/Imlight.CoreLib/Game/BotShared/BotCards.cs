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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Decks and discards for the server's own wizards, the Quest Team and Bot Players (Niduus, 2026-11-12: "Allow for discard
/// cards in battle and to adjust decks on the fly to prepare for the next fight needed"). A deck is built from the spells
/// the wizard knows, in the mix its job wants (a hitter mostly hits, a healer mostly heals), and changed before a fight
/// for what is coming: more heals and shields for a boss, hits on everyone for a crowd, shields of the enemy's school and
/// fewer hits of the school the enemy resists. In a battle it throws away the cards it will not use, as players do, to
/// make room for better ones next round.
/// </summary>
internal static class BotCards {

    /// <summary>What the next fight looks like, as far as the wizard knows: the enemy's school, a boss, how many.</summary>
    internal sealed record Situation(string EnemySchool, bool Boss, int Enemies) {

        internal static readonly Situation Unknown = new("", false, 0);

        internal string Describe()
            => Boss ? $"a boss{(EnemySchool.Length > 0 ? $" ({EnemySchool})" : "")}"
                : Enemies >= 3 ? $"{Enemies} enemies{(EnemySchool.Length > 0 ? $" ({EnemySchool})" : "")}"
                : EnemySchool.Length > 0 ? $"{EnemySchool} enemies" : "any fight";

    }

    /// <summary>What a fight against this creature (by template) looks like: its school, and whether it is a boss.</summary>
    internal static Situation SituationOf(ulong creatureTemplate) {
        if (CoreObjectFactory.GetCoreTemplate(creatureTemplate) is not GameObjectTemplate template
            || template.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault() is not { } npc) {
            return Situation.Unknown;
        }

        return new Situation((string) npc.m_schoolOfFocus ?? "", npc.IsBoss(), 0);
    }

    private enum Kind { Hit, Heal, Shield, Blade, Trap, Other }

    // The share of the deck each kind of card gets, by job.
    private static Dictionary<Kind, double> Shares(BotStyle style, Situation situation) {
        var shares = style switch {
            BotStyle.Hitter => new Dictionary<Kind, double> { [Kind.Hit] = 0.65, [Kind.Blade] = 0.2, [Kind.Trap] = 0.1, [Kind.Heal] = 0.05, [Kind.Shield] = 0, [Kind.Other] = 0 },
            BotStyle.Backup => new Dictionary<Kind, double> { [Kind.Hit] = 0.45, [Kind.Trap] = 0.2, [Kind.Blade] = 0.15, [Kind.Heal] = 0.1, [Kind.Shield] = 0.1, [Kind.Other] = 0 },
            BotStyle.Tank => new Dictionary<Kind, double> { [Kind.Shield] = 0.3, [Kind.Hit] = 0.25, [Kind.Trap] = 0.15, [Kind.Blade] = 0.15, [Kind.Heal] = 0.15, [Kind.Other] = 0 },
            _ => new Dictionary<Kind, double> { [Kind.Heal] = 0.45, [Kind.Shield] = 0.15, [Kind.Blade] = 0.15, [Kind.Hit] = 0.2, [Kind.Trap] = 0.05, [Kind.Other] = 0 },
        };

        // A boss: the helpers bring more heals and shields, the hitter more blades.
        if (situation.Boss) {
            if (style == BotStyle.Hitter) {
                shares[Kind.Blade] += 0.05;
                shares[Kind.Hit] -= 0.05;
            }
            else {
                shares[Kind.Heal] += 0.05;
                shares[Kind.Shield] += 0.05;
                shares[Kind.Hit] = Math.Max(0.1, shares[Kind.Hit] - 0.1);
            }
        }

        return shares;
    }

    private static Kind KindOf(Card card)
        => card.Hit ? Kind.Hit : card.Heal ? Kind.Heal : card.Shield ? Kind.Shield : card.Blade ? Kind.Blade : card.Trap ? Kind.Trap : Kind.Other;

    /// <summary>A card of a spell, or null when it is not one that goes in a deck to fight with.</summary>
    internal static Card CardOf(uint spellId) {
        if (spellId == 0 || !SpellFactory.CanGoInDeck(spellId) || CoreObjectFactory.GetCoreTemplate(spellId) is not SpellTemplate template
            || template.m_Treasure || CombatEnchantments.IsEnchantment(template) || DivideCards.IsPickCard(template) || IsDevSpell(spellId)) {
            return null;
        }

        var card = RewrittenAI.Read(SpellFactory.GetSpell(template, spellId));

        return card is null || card.Natural ? null : card;
    }

    /// <summary>
    /// A spell the developers made for testing (the Dev trainer's list: a Test folder, or named Dev, Debug, GM or Admin).
    /// The server's wizards never put one in a deck (Niduus, 2026-11-12: "any Dev gear and spells").
    /// </summary>
    internal static bool IsDevSpell(uint spellId) {
        try {
            return BossDevSpells.KindOf(spellId) == "Dev";
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// The deck a wizard wants for a fight: spell to copies, from the spells it knows, no more than its deck holds (and
    /// no more copies of a spell than the deck allows).
    /// </summary>
    internal static Dictionary<uint, int> Plan(IEnumerable<uint> known, string school, BotStyle style, Situation situation,
                                              int maxSpells, int schoolMaxCopies, int otherMaxCopies) {
        situation ??= Situation.Unknown;
        var cards = known.Distinct().Select(CardOf).Where(c => c is not null).ToList();
        var deck = new Dictionary<uint, int>();
        if (cards.Count == 0 || maxSpells <= 0) {
            return deck;
        }

        int Cap(Card card) => Math.Max(1, string.Equals(card.School, school, StringComparison.OrdinalIgnoreCase) ? schoolMaxCopies : otherMaxCopies);

        var shares = Shares(style, situation);
        var byKind = cards.GroupBy(KindOf).ToDictionary(g => g.Key, g => Order(g.Key, g.ToList(), school, style, situation));
        var left = maxSpells;
        foreach (var (kind, share) in shares.OrderByDescending(s => s.Value)) {
            if (!byKind.TryGetValue(kind, out var ofKind) || share <= 0) {
                continue;
            }

            var slots = Math.Min(left, (int) Math.Round(share * maxSpells));
            left -= Fill(deck, ofKind, slots, Cap);
        }

        // What is left goes to hits, then to anything it has.
        foreach (var kind in new[] { Kind.Hit, Kind.Blade, Kind.Trap, Kind.Shield, Kind.Heal, Kind.Other }) {
            if (left <= 0) {
                break;
            }

            if (style == BotStyle.Hitter && kind is Kind.Shield or Kind.Heal) {
                continue;
            }

            if (byKind.TryGetValue(kind, out var ofKind)) {
                left -= Fill(deck, ofKind, left, Cap);
            }
        }

        return deck;
    }

    // The best few of a kind get the most copies: round after round over them until the slots are used.
    private static int Fill(Dictionary<uint, int> deck, List<Card> ofKind, int slots, Func<Card, int> cap) {
        var used = 0;
        var top = ofKind.Take(5).ToList();
        var progress = true;
        while (used < slots && progress) {
            progress = false;
            foreach (var card in top) {
                if (used >= slots) {
                    break;
                }

                var have = deck.GetValueOrDefault(card.Id);
                if (have >= cap(card)) {
                    continue;
                }

                deck[card.Id] = have + 1;
                used++;
                progress = true;
            }
        }

        return used;
    }

    private static List<Card> Order(Kind kind, List<Card> cards, string school, BotStyle style, Situation situation) {
        var enemy = situation.EnemySchool ?? "";
        return kind switch {
            // Hits: on everyone first against a crowd; the enemy's own school (which it resists) last; newest first, but
            // the cheapest one stays for the first rounds.
            Kind.Hit => [.. cards.OrderByDescending(c => situation.Enemies >= 3 && c.HitsAll ? 1 : 0)
                .ThenBy(c => enemy.Length > 0 && RewrittenAI.Same(c.DamageSchool, enemy) ? 1 : 0)
                .ThenByDescending(c => c == cards.MinBy(x => x.Rank) ? 1 : 0)
                .ThenByDescending(c => c.Rank).ThenByDescending(c => c.BaseDamage)],
            Kind.Heal => [.. cards.OrderByDescending(c => (situation.Boss || style == BotStyle.Healer) && c.HealWholeTeam ? 1 : 0)
                .ThenByDescending(c => c.HealForTeammate || c.HealWholeTeam ? 1 : 0).ThenByDescending(c => c.HealAmount)],
            Kind.Shield => [.. cards.OrderByDescending(c => enemy.Length > 0 && RewrittenAI.Same(c.ShieldSchool, enemy) ? 2 : 0)
                .ThenByDescending(c => string.IsNullOrEmpty(c.ShieldSchool) || c.ShieldSchool.Equals("All", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(c => c.ShieldWholeTeam || c.ShieldForTeammate ? 1 : 0)],
            Kind.Blade => [.. cards.OrderByDescending(c => RewrittenAI.Same(c.BladeSchool, school) || string.IsNullOrEmpty(c.BladeSchool) ? 1 : 0)
                .ThenByDescending(c => c.BladeWholeTeam ? 1 : 0).ThenByDescending(c => c.BladePercent)],
            Kind.Trap => [.. cards.OrderByDescending(c => RewrittenAI.Same(c.TrapSchool, school) || string.IsNullOrEmpty(c.TrapSchool) ? 1 : 0)
                .ThenByDescending(c => c.TrapAll ? 1 : 0)],
            _ => [.. cards.OrderByDescending(c => c.Weakness ? 1 : 0)],
        };
    }

    /// <summary>What changes from one deck to another: the spells to take out (one per copy) and to put in (one per copy).</summary>
    internal static (List<uint> Out, List<uint> In) Changes(IReadOnlyDictionary<uint, int> now, IReadOnlyDictionary<uint, int> wanted) {
        var take = new List<uint>();
        var put = new List<uint>();
        foreach (var (id, count) in now) {
            take.AddRange(Enumerable.Repeat(id, Math.Max(0, count - wanted.GetValueOrDefault(id))));
        }

        foreach (var (id, count) in wanted) {
            put.AddRange(Enumerable.Repeat(id, Math.Max(0, count - now.GetValueOrDefault(id))));
        }

        return (take, put);
    }

    /// <summary>A deck in words for the Quest Team page: "Frost Beetle x4, Ice Blade x2".</summary>
    internal static string Describe(IEnumerable<(uint Id, int Count)> deck)
        => string.Join(", ", deck.Where(d => d.Count > 0).OrderByDescending(d => d.Count)
            .Select(d => $"{CardOf(d.Id)?.Shown ?? SpellName(d.Id)} x{d.Count}"));

    private static string SpellName(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t ? (string) t.m_name ?? id.ToString() : id.ToString();

    // ---------------------------------------------------------------------
    // Bot Players' decks
    // ---------------------------------------------------------------------

    private sealed record BotKnown(WeakReference<Wizard> Wizard, uint[] Spells, int Size);

    private static readonly ConcurrentDictionary<ulong, BotKnown> s_bots = new();

    /// <summary>
    /// A Bot Player's deck from the spells a wizard of its school has at its level (BotDeck.For), in the mix of its job, for
    /// any fight; the bot is remembered (by its object's global id) so a battle can change its deck before it sits down.
    /// </summary>
    internal static List<SpellData> BotDeck(Wizard bot, IReadOnlyList<SpellData> knownSpells) {
        var known = knownSpells.Select(s => (uint) s.m_templateID).Distinct().ToArray();
        var size = Math.Clamp(knownSpells.Sum(s => (int) s.m_quantity), 20, 60);
        s_bots[bot.GameObjectID] = new BotKnown(new WeakReference<Wizard>(bot), known, size);

        return ToSpellData(PlanForBot(bot, known, size, Situation.Unknown));
    }

    /// <summary>A Bot Player about to sit down in a battle: its deck for these enemies. False when it is not a known bot.</summary>
    internal static bool PrepareBot(ulong globalId, Situation situation) {
        if (!s_bots.TryGetValue(globalId, out var known) || !known.Wizard.TryGetTarget(out var bot) || bot.SpellbookBehavior is null) {
            return false;
        }

        bot.SpellbookBehavior.SpellList = ToSpellData(PlanForBot(bot, known.Spells, known.Size, situation));

        return true;
    }

    /// <summary>A Bot Player left: forgotten.</summary>
    internal static void ForgetBot(ulong globalId) => s_bots.TryRemove(globalId, out _);

    private static Dictionary<uint, int> PlanForBot(Wizard bot, uint[] known, int size, Situation situation) {
        var school = BotGear.SchoolOf(bot);

        return Plan(known, school, BotGear.StyleOf(school), situation, size, 4, 3);
    }

    private static List<SpellData> ToSpellData(Dictionary<uint, int> deck)
        => [.. deck.Select(d => new SpellData { m_templateID = d.Key, m_quantity = (byte) Math.Clamp(d.Value, 1, 255) })];

    /// <summary>What a battle's enemies look like.</summary>
    internal static Situation SituationOf(IEnumerable<CombatDuelSubCircle> enemyCircles) {
        var enemies = enemyCircles.Where(c => c is { Occupied: true, IsAlive: true }).ToList();
        var school = enemies.Select(RewrittenAI.SchoolOf).Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";

        return new Situation(school, enemies.Any(RewrittenAI.IsBoss), enemies.Count);
    }

    // ---------------------------------------------------------------------
    // Discarding in a battle
    // ---------------------------------------------------------------------

    /// <summary>
    /// The cards of its hand (by hand slot) a wizard throws away this round, at most two: cards its job never plays (a
    /// hitter's heals and shields), a second copy of a charm already on everyone it can go on, and spare heals while the
    /// whole team is healthy and the hand is full.
    /// </summary>
    internal static List<int> Discards(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, BotStyle style) {
        var hand = new List<(int Index, Card Card)>();
        for (byte i = 0; i < 24; i++) {
            if (me.GetSpellFromLastHand(i) is not { } spell || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
                || CombatEnchantments.IsEnchantment(template) || DivideCards.IsPickCard(template) || RewrittenAI.Read(spell) is not { } card) {
                continue;
            }

            hand.Add((i, card));
        }

        var throwAway = new List<int>();
        if (hand.Count < 4) {
            return throwAway;
        }

        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var healthy = allies.All(a => RewrittenAI.HealthShare(a) >= 0.85f);

        // Cards its job never plays.
        if (style == BotStyle.Hitter) {
            throwAway.AddRange(hand.Where(h => (h.Card.Heal && !h.Card.Hit && !h.Card.HealTakesFromEnemy) || (h.Card.Shield && !h.Card.Hit))
                .Select(h => h.Index));
        }

        // A charm or ward already on every circle it can go on: one copy stays for when it is used up.
        foreach (var group in hand.Where(h => (h.Card.Blade || h.Card.Shield) && !h.Card.Hit).GroupBy(h => h.Card.Id)) {
            var targets = group.First().Card switch {
                { Blade: true, BladeForTeammate: false, BladeWholeTeam: false } or { Shield: true, ShieldForTeammate: false, ShieldWholeTeam: false } => [me],
                _ => allies,
            };
            if (targets.All(t => RewrittenAI.HasEffectOfSpell(t, group.Key))) {
                throwAway.AddRange(group.Skip(1).Select(h => h.Index));
            }
        }

        // Spare heals while everyone is healthy and the hand is full: the best one stays.
        if (healthy && hand.Count >= 7) {
            throwAway.AddRange(hand.Where(h => h.Card.Heal && !h.Card.Hit).OrderByDescending(h => h.Card.HealAmount).Skip(1).Select(h => h.Index));
        }

        return [.. throwAway.Distinct().Take(2)];
    }

}
