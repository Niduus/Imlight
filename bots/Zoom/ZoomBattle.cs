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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Resources;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How Zoom, the quest scout, fights. Niduus, 2026-11-22: "give Zoom all vanilla spells for storm and enchants. His main
/// tactic should either be temp + Epic to clear or Glowbugs + Epic to clear." His hand is dealt each round as True Form's is
/// (TrueFormBattle), from the spells a player can have at the top level (no dev spells): Epic (the best damage enchantment
/// there is when the game has no Epic), Tempest and Glowbug Squall first, then his biggest real hits. He casts Tempest when he
/// can pay for it, else Glowbug Squall, and the director puts Epic on it first (UltimusTactics.EnchantFor), so each cast
/// clears the room. Only when neither can be cast does he use the biggest hit he can pay for, else he waits for pips.
/// </summary>
internal static class ZoomBattle {

    private const int HAND = TrueFormBattle.HAND;

    /// <summary>His two clearing spells, best first (Niduus, 2026-11-22; per scout's school, ZoomSpells.Clearers).</summary>
    private static IReadOnlyList<string> s_clearers => ZoomSpells.Clearers;

    private const string EPIC = "Epic";

    private static int Top => Math.Clamp(MagicLevelsConfig.MaxLevel, 1, 255);

    // A spell he has at the top level by its exact shown name (no dev spells).
    private static TrueFormPool.Entry Named(string name)
        => TrueFormPool.Versions(Top, dev: false).Where(e => !e.Dev && !e.Treasure && string.Equals(e.Shown, name, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(e => e.Level).FirstOrDefault()
           ?? TrueFormPool.Versions(Top, dev: false).Where(e => !e.Dev && string.Equals(e.Shown, name, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(e => e.Level).FirstOrDefault();

    // Tempest and Glowbug Squall, best first (the ones the game has).
    private static List<TrueFormPool.Entry> Clearers() => [.. s_clearers.Select(Named).Where(e => e is not null && SpellFactory.GetSpell(e.Id) is not null)];

    // Epic; when the game has none, the biggest damage enchantment a player can have.
    private static TrueFormPool.Entry Enchant() {
        if (Named(EPIC) is { } epic && SpellFactory.GetSpell(epic.Id) is not null) {
            return epic;
        }

        return TrueFormPool.Versions(Top, dev: false)
            .Where(e => !e.Dev && SpellFactory.GetSpell(e.Id) is not null && BotEnchants.Of(e.Id) is { Kind: BotEnchants.DAMAGE })
            .OrderByDescending(e => BotEnchants.Of(e.Id).Amount).FirstOrDefault();
    }

    // His real hits at the top level (no damage-per-pip spells, which do nothing with few pips, no dev or treasure cards).
    private static List<TrueFormPool.Entry> RealHits()
        => [.. TrueFormPool.At(Top, dev: false).Where(e => !e.Dev && e.Card.Hit && e.Card.PerPip == 0 && !e.Treasure && !BotCards.NotVanilla(e.Card))];

    /// <summary>
    /// His seven cards for this round in place of the hand he drew, or null when there is nothing to deal yet (his spells
    /// are still being read). Says what was dealt.
    /// </summary>
    internal static string Deal(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, int round, CombatResolver resolver, ulong duel) {
        if (me?._combatDeck is not { } deck || !TrueFormPool.Ready) {
            return null;
        }

        // Bosses with cheats: what went off since his last look is written down, and while he studies them his hand holds
        // the cards that set them off (ZoomCheats) and no Epic (a study hit must not end the fight).
        ZoomCheats.Round(duel, me, circles, resolver, round);
        var studying = ZoomCheats.Studying(duel);

        var pips = RewrittenAI.PipsOf(me);
        var ids = new List<uint>();
        void Add(IEnumerable<TrueFormPool.Entry> list, int most, bool again = false) {
            foreach (var e in list.Take(most)) {
                if (ids.Count < HAND && (again || !ids.Contains(e.Id))) {
                    ids.Add(e.Id);
                }
            }
        }

        if (studying) {
            Add(StudyCards(), HAND - 2);
        }

        // Epic twice (one for each clearing spell), then Tempest and Glowbug Squall.
        if (!studying && Enchant() is { } epic) {
            Add([epic, epic], 2, again: true);
        }

        Add(Clearers(), 2);
        var hits = RealHits();
        Add(hits.Where(e => e.Card.Rank <= pips && e.Card.HitsAll).OrderByDescending(e => e.Card.BaseDamage), 1);
        Add(hits.Where(e => e.Card.Rank <= pips && !e.Card.HitsAll).OrderByDescending(e => e.Card.BaseDamage), 1);
        Add(hits.OrderByDescending(e => e.Card.BaseDamage), HAND);
        if (ids.Count == 0) {
            return null;
        }

        deck.ReturnHandToDeck();
        var shown = new List<string>();
        foreach (var id in ids) {
            if (SpellFactory.GetSpell(id) is not { } spell) {
                continue;
            }

            deck.AddExtraCardToHand(spell);
            shown.Add(TrueFormPool.Of(id)?.Shown ?? RewrittenAI.ReadTemplate(id)?.Shown ?? id.ToString());
        }

        return $"{UltimusSeat.Current.Name} is dealt his {(studying ? "cheat study" : "clearing")} hand ({pips} pips): {string.Join(", ", shown)}";
    }

    /// <summary>
    /// His pick for the round: Tempest, else Glowbug Squall (the director puts Epic on it first); only when neither can be paid
    /// for, the biggest hit he can pay for, at the boss or the creature with the most health.
    /// </summary>
    internal static UltimusTactics.Choice Decide(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver, int round, ulong duel) {
        ZoomCheats.Round(duel, me, circles, resolver, round);
        var choice = ZoomCheats.Move(duel, me, UltimusTactics.Hand(me).Where(h => h.Castable).ToList(), round) ?? Clear(me, circles);
        ZoomCheats.Done(duel, me, circles, choice, round);
        return choice;
    }

    // One of each kind of card a cheat answers (the smallest of each, so a study move does not end the fight): a single hit,
    // a blade, a heal, a shield, a trap and a weakness, then Tempest for when the study is done.
    private static List<TrueFormPool.Entry> StudyCards() {
        var pool = TrueFormPool.At(Top, dev: false).Where(e => !e.Dev && !e.Treasure && e.Card.PerPip == 0 && !BotCards.NotVanilla(e.Card)
                                                              && SpellFactory.GetSpell(e.Id) is not null).ToList();
        var list = new List<TrueFormPool.Entry>();
        void One(Func<RewrittenAI.Card, bool> kind) {
            if (pool.Where(e => kind(e.Card)).OrderBy(e => e.Card.Rank).ThenBy(e => e.Card.BaseDamage).FirstOrDefault() is { } e) {
                list.Add(e);
            }
        }

        One(c => c.Hit && !c.HitsAll);
        One(c => c.Blade && !c.Hit && !c.BladeOnlyTeammate);
        One(c => c.Heal && !c.Hit);
        One(c => c.Shield && !c.Hit && !c.Heal);
        One(c => c.Trap && !c.Hit);
        One(c => c.Weakness && !c.Hit);
        list.AddRange(Clearers().Take(1));
        return list;
    }

    // His clearing pick (no cheats left to study).
    private static UltimusTactics.Choice Clear(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles) {
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return new(UltimusTactics.Move.Pass, 0, -1, "no enemy is left");
        }

        var target = enemies.OrderByDescending(e => RewrittenAI.IsBoss(e) ? 1 : 0).ThenByDescending(RewrittenAI.Health).First();
        var hand = UltimusTactics.Hand(me).Where(h => h.Castable).ToList();
        foreach (var name in s_clearers) {
            if (hand.FirstOrDefault(h => string.Equals(h.Card.Shown, name, StringComparison.OrdinalIgnoreCase)) is { } clear) {
                return new(UltimusTactics.Move.Cast, clear.Index, clear.Card.HitsAll ? -1 : target.SlotIndex,
                    $"clears the room with {clear.Card.Shown} (main tactic: {name} + Epic)");
            }
        }

        var hits = hand.Where(h => h.Card.Hit).ToList();
        var wide = enemies.Count > 1 ? hits.Where(h => h.Card.HitsAll).OrderByDescending(h => h.Card.BaseDamage).FirstOrDefault() : null;
        if (wide is not null) {
            return new(UltimusTactics.Move.Cast, wide.Index, -1, $"no pips for {string.Join(" or ", s_clearers)} yet: hits every enemy with {wide.Card.Shown}");
        }

        var big = hits.OrderByDescending(h => h.Card.BaseDamage).FirstOrDefault();
        if (big is not null) {
            return new(UltimusTactics.Move.Cast, big.Index, big.Card.HitsAll ? -1 : target.SlotIndex,
                $"no pips for {string.Join(" or ", s_clearers)} yet: hits {(big.Card.HitsAll ? "every enemy" : UltimusTactics.Name(target))} with {big.Card.Shown}");
        }

        return new(UltimusTactics.Move.Pass, 0, -1, "builds pips for Tempest + Epic");
    }

}
