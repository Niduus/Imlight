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
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// How each Quest Team wizard plays its turn, by its job (Niduus, 2026-11-12):
/// Ice tanks and supports: shields whoever is hurt, traps (Feint) the enemy the hitters are on, blades the hitters, and
/// only hits when there is nothing better to do or it can finish an enemy.
/// Life heals and supports: heals whoever is hurt (the whole team when several are), shields, blades the hitters.
/// Death is the backup hitter and supports: finishes enemies, traps and blades for Storm, hits the team's target.
/// Storm only hits and buffs itself: its strongest hit on the team's target, a blade on itself when it can't hit yet.
/// Each plays from its own hand and pips, like a player; it sees what the teammates before it picked this round.
/// </summary>
internal static class QuestTeamTactics {

    internal enum Move { Cast, Pass }

    internal sealed record Choice(Move Move, int Card, int Target, string Why);

    private sealed record InHand(int Index, Card Card, bool Castable);

    private const float HURT = 0.6f;
    private const float DANGER = 0.4f;

    internal static Choice Decide(string role, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver) {
        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return new(Move.Pass, 0, -1, "no enemy is left");
        }

        var castable = Hand(me).Where(h => h.Castable).ToList();
        if (castable.Count == 0) {
            return new(Move.Pass, 0, -1, "nothing it can cast yet (saves its pips)");
        }

        // What the others on its side picked this round.
        var picks = allies.Where(a => a != me)
            .Select(a => resolver.GetQueuedAction(a) is { Spell: { } spell } queued && RewrittenAI.Read(spell) is { } card
                ? (Who: a, Card: card, On: queued.SelectedTarget)
                : (Who: a, Card: (Card) null, On: (CombatDuelSubCircle) null))
            .Where(p => p.Card is not null).ToList();
        var healed = picks.Where(p => p.Card.Heal && !p.Card.Hit).Select(p => p.Card.HealWholeTeam ? null : p.On).ToList();
        var teamHealed = healed.Contains(null);
        var hits = picks.Where(p => p.Card.Hit && p.On is not null && p.On.OccupiedTeam != me.OccupiedTeam).ToList();

        // The team's target: the one a teammate is already hitting, else the weakest.
        var focus = hits.Select(h => h.On).FirstOrDefault(e => e.IsAlive) ?? enemies.OrderBy(RewrittenAI.Health).First();
        var hurt = allies.Where(a => !teamHealed && !healed.Contains(a) && RewrittenAI.HealthShare(a) < HURT)
            .OrderBy(RewrittenAI.HealthShare).ToList();

        return role switch {
            "Ice" => Ice(me, castable, allies, enemies, focus, hurt, hits),
            "Life" => Life(me, castable, allies, enemies, focus, hurt, hits),
            "Death" => Death(me, castable, allies, enemies, focus, hurt, hits),
            _ => Storm(me, castable, enemies, focus),
        };
    }

    // ---- the jobs ----

    private static Choice Ice(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> enemies,
                              CombatDuelSubCircle focus, List<CombatDuelSubCircle> hurt, List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)> hits) {
        return Heal(me, hand, hurt.Where(h => RewrittenAI.HealthShare(h) < DANGER).ToList())
               ?? Shield(me, hand, hurt)
               ?? Finish(me, hand, enemies)
               ?? TrapFor(me, hand, hits, focus, "Storm")
               ?? BladeFor(me, hand, allies, hits)
               ?? Hit(me, hand, focus, enemies, "hits (nothing to support)")
               ?? Pass("saves its pips");
    }

    private static Choice Life(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> enemies,
                               CombatDuelSubCircle focus, List<CombatDuelSubCircle> hurt, List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)> hits) {
        return Heal(me, hand, hurt)
               ?? Shield(me, hand, hurt)
               ?? BladeFor(me, hand, allies, hits)
               ?? Finish(me, hand, enemies)
               ?? Hit(me, hand, focus, enemies, "hits (nobody needs healing)")
               ?? Pass("saves its pips for heals");
    }

    private static Choice Death(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> enemies,
                                CombatDuelSubCircle focus, List<CombatDuelSubCircle> hurt, List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)> hits) {
        return Finish(me, hand, enemies)
               ?? TrapFor(me, hand, hits, focus, "Storm")
               ?? BladeFor(me, hand, allies, hits)
               ?? Hit(me, hand, focus, enemies, "backs up the hit")
               ?? Heal(me, hand, hurt.Where(h => RewrittenAI.HealthShare(h) < DANGER).ToList())
               ?? SelfBlade(me, hand)
               ?? Pass("saves its pips");
    }

    private static Choice Storm(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> enemies, CombatDuelSubCircle focus) {
        return Finish(me, hand, enemies)
               ?? Hit(me, hand, focus, enemies, "hits")
               ?? SelfBlade(me, hand)
               ?? Pass("saves its pips for a bigger hit");
    }

    // ---- the moves ----

    private static Choice Heal(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> hurt) {
        if (hurt.Count == 0) {
            return null;
        }

        var many = hurt.Count >= 2;
        var who = hurt[0];
        var heal = hand.Where(h => h.Card.Heal && !h.Card.Hit && (h.Card.HealWholeTeam || who == me || h.Card.HealForTeammate))
            .OrderByDescending(h => many && h.Card.HealWholeTeam ? 1 : 0).ThenByDescending(h => h.Card.HealAmount).FirstOrDefault();
        if (heal is null) {
            return null;
        }

        var target = heal.Card.HealWholeTeam || (who == me && !heal.Card.HealForTeammate) ? -1 : who.SlotIndex;

        return new(Move.Cast, heal.Index, target, heal.Card.HealWholeTeam
            ? $"heals the team with {heal.Card.Shown}"
            : $"heals {Name(who)} ({RewrittenAI.Percent(RewrittenAI.HealthShare(who))}% health) with {heal.Card.Shown}");
    }

    private static Choice Shield(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> hurt) {
        var who = hurt.FirstOrDefault(h => !hand.Any(s => s.Card.Shield && RewrittenAI.HasEffectOfSpell(h, s.Card.Id)));
        if (who is null) {
            return null;
        }

        var shield = hand.Where(h => h.Card.Shield && (h.Card.ShieldWholeTeam || h.Card.ShieldForTeammate || who == me)).FirstOrDefault();
        if (shield is null) {
            return null;
        }

        return new(Move.Cast, shield.Index, shield.Card.ShieldWholeTeam ? -1 : who.SlotIndex, $"shields {Name(who)} with {shield.Card.Shown}");
    }

    private static Choice Finish(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> enemies) {
        foreach (var enemy in enemies.OrderBy(RewrittenAI.Health)) {
            var finisher = hand.Where(h => h.Card.Hit && !h.Card.Natural && !h.Card.HitsAll && RewrittenAI.Estimate(h.Card, me, enemy) >= RewrittenAI.Health(enemy))
                .OrderBy(h => h.Card.Rank).FirstOrDefault();
            if (finisher is not null) {
                return new(Move.Cast, finisher.Index, enemy.SlotIndex, $"finishes off {Name(enemy)} with {finisher.Card.Shown}");
            }
        }

        // A hit on everyone that finishes two or more.
        var all = hand.Where(h => h.Card.Hit && h.Card.HitsAll && !h.Card.Natural)
            .FirstOrDefault(h => enemies.Count(e => RewrittenAI.Estimate(h.Card, me, e) >= RewrittenAI.Health(e)) >= 2);

        return all is null ? null : new(Move.Cast, all.Index, -1, $"finishes off several with {all.Card.Shown}");
    }

    private static Choice Hit(CombatDuelSubCircle me, List<InHand> hand, CombatDuelSubCircle focus, List<CombatDuelSubCircle> enemies, string why) {
        var best = hand.Where(h => h.Card.Hit && !h.Card.Natural)
            .Select(h => (Hand: h, Damage: h.Card.HitsAll
                ? enemies.Sum(e => Math.Min(RewrittenAI.Health(e), RewrittenAI.Estimate(h.Card, me, e)))
                : RewrittenAI.Estimate(h.Card, me, focus)))
            .OrderByDescending(x => x.Damage).FirstOrDefault();
        if (best.Hand is null) {
            return null;
        }

        return best.Hand.Card.HitsAll
            ? new(Move.Cast, best.Hand.Index, -1, $"{why}: every enemy with {best.Hand.Card.Shown} (about {best.Damage})")
            : new(Move.Cast, best.Hand.Index, focus.SlotIndex, $"{why}: {Name(focus)} with {best.Hand.Card.Shown} (about {best.Damage})");
    }

    // A trap on the enemy the hitters are on (for Storm's hit first), when it fits the hit's school.
    private static Choice TrapFor(CombatDuelSubCircle me, List<InHand> hand, List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)> hits,
                                  CombatDuelSubCircle focus, string preferred) {
        var school = hits.Select(h => h.Card.DamageSchool).FirstOrDefault() ?? preferred;
        var on = hits.Select(h => h.On).FirstOrDefault() ?? focus;
        var trap = hand.FirstOrDefault(h => h.Card.Trap && Fits(h.Card.TrapSchool, school) && !RewrittenAI.HasEffectOfSpell(on, h.Card.Id));

        return trap is null ? null : new(Move.Cast, trap.Index, trap.Card.TrapAll ? -1 : on.SlotIndex, $"traps {Name(on)} with {trap.Card.Shown}");
    }

    // A blade on the team's designated hitters only: Storm first, then Death (Niduus, 2026-11-12: "I see them all blading
    // the first player instead of the designated hitters"). Ice and Life are never bladed by a teammate.
    private static Choice BladeFor(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies,
                                   List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)> hits) {
        foreach (var role in new[] { "Storm", "Death" }) {
            var hitter = allies.FirstOrDefault(a => a != me && a.IsAlive
                                                    && QuestTeamDirector.RoleOf(a.ParticipantObject?.m_globalID.Full ?? 0) == role);
            if (hitter is null) {
                continue;
            }

            // The school of the hit it has queued, else its own school.
            var school = hits.Where(h => h.Who == hitter).Select(h => h.Card.DamageSchool).FirstOrDefault() ?? RewrittenAI.SchoolOf(hitter);
            var blade = hand.FirstOrDefault(h => h.Card.Blade && (h.Card.BladeForTeammate || h.Card.BladeWholeTeam)
                                                 && Fits(h.Card.BladeSchool, school) && !RewrittenAI.HasEffectOfSpell(hitter, h.Card.Id));
            if (blade is not null) {
                return new(Move.Cast, blade.Index, blade.Card.BladeWholeTeam ? -1 : hitter.SlotIndex, $"blades {Name(hitter)} ({role}) with {blade.Card.Shown}");
            }
        }

        return null;
    }

    private static Choice SelfBlade(CombatDuelSubCircle me, List<InHand> hand) {
        var blade = hand.FirstOrDefault(h => h.Card.Blade && !h.Card.BladeOnlyTeammate && Fits(h.Card.BladeSchool, RewrittenAI.SchoolOf(me))
                                             && !RewrittenAI.HasEffectOfSpell(me, h.Card.Id));

        return blade is null ? null : new(Move.Cast, blade.Index, blade.Card.BladeWholeTeam ? -1 : me.SlotIndex, $"blades itself with {blade.Card.Shown}");
    }

    private static Choice Pass(string why) => new(Move.Pass, 0, -1, why);

    /// <summary>
    /// While the team holds its hits for more creatures to walk in: a blade or a trap that readies the hit to come, or null
    /// (then it passes).
    /// </summary>
    internal static Choice WhileWaiting(string role, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles) {
        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        var hand = Hand(me).Where(h => h.Castable).ToList();
        if (enemies.Count == 0 || hand.Count == 0) {
            return null;
        }

        var focus = enemies.OrderBy(RewrittenAI.Health).First();
        var none = new List<(CombatDuelSubCircle Who, Card Card, CombatDuelSubCircle On)>();
        var readied = role is "Ice" or "Life" or "Death"
            ? BladeFor(me, hand, allies, none) ?? TrapFor(me, hand, none, focus, "Storm")
            : null;
        readied ??= role is "Death" or "Storm" || allies.Count == 1 ? SelfBlade(me, hand) : null;

        return readied is null ? null : readied with { Why = readied.Why + " while more creatures walk in" };
    }

    private static List<InHand> Hand(CombatDuelSubCircle me) {
        var hand = new List<InHand>();
        for (byte i = 0; i < 24; i++) {
            if (me.GetSpellFromLastHand(i) is not { } spell || RewrittenAI.Read(spell) is not { } card
                || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
                || CombatEnchantments.IsEnchantment(template) || DivideCards.IsPickCard(template)) {
                continue;
            }

            hand.Add(new(i, card, me.HasPipsForSpell(spell)));
        }

        return hand;
    }

    private static bool Fits(string charmSchool, string hitSchool)
        => string.IsNullOrEmpty(charmSchool) || charmSchool.Equals("All", StringComparison.OrdinalIgnoreCase) || RewrittenAI.Same(charmSchool, hitSchool);

    private static string Name(CombatDuelSubCircle circle)
        => circle.ParticipantObject?.m_debugName is { Length: > 0 } name ? name : $"slot {circle.SlotIndex}";

}
