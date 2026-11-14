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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How Ultimus plays its turn alone (forked from the Quest Team's QuestTeamTactics, which plays by team job). A solo Balance
/// wizard has nobody to heal or shield it, so it keeps itself alive first: a heal when it is low, a shield when it is hurt.
/// Then it finishes what it can, and otherwise builds up one big hit (a blade on itself, a trap on its target) before it
/// hits, so a fight takes fewer rounds. Players who joined its battle are welcome: a player in danger gets a heal when it
/// has one, and it hits the enemy the players are hitting. It plays from its own hand and pips, like a player.
/// </summary>
internal static class UltimusTactics {

    internal enum Move { Cast, Pass }

    internal sealed record Choice(Move Move, int Card, int Target, string Why);

    private sealed record InHand(int Index, Card Card, bool Castable);

    // When it heals (its playbook, Battle.HealAt and Battle.BigHealAt; Niduus can change them, live).
    private static float HURT => (float) UltimusPlaybook.Value("Battle.HealAt");
    private static float DANGER => (float) UltimusPlaybook.Value("Battle.BigHealAt");

    internal static Choice Decide(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver) {
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

        // What the enemies can hit with, read from their decks (UltimusScout).
        var threat = UltimusScout.InBattle(enemies);

        return Solo(me, castable, enemies, focus, hurt, threat);
    }

    /// <summary>
    /// A shield that guards against none of what the enemies here can hit with (an Elemental Shield against a Myth
    /// creature): useless in this fight, so it is not cast, and it may be discarded.
    /// </summary>
    internal static bool UselessShield(Card card, UltimusScout.Threat threat)
        => card.Shield && !card.Hit && !card.Heal && threat.Known && threat.Covered(card.ShieldSchools) < MIN_COVER;

    private const double MIN_COVER = 0.25;

    // ---- alone (with any players who joined) ----

    private static Choice Solo(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> enemies, CombatDuelSubCircle focus,
                               List<CombatDuelSubCircle> hurt, UltimusScout.Threat threat) {
        var mine = RewrittenAI.HealthShare(me);
        var myHurt = mine < HURT ? new List<CombatDuelSubCircle> { me } : [];
        var playersInDanger = hurt.Where(h => h != me && RewrittenAI.HealthShare(h) < DANGER).ToList();

        // Its best hit on the target now, to know whether building up is worth a round.
        var bestHit = hand.Where(h => h.Card.Hit && !h.Card.Natural)
            .Select(h => h.Card.HitsAll ? enemies.Sum(e => Math.Min(RewrittenAI.Health(e), RewrittenAI.Estimate(h.Card, me, e))) : RewrittenAI.Estimate(h.Card, me, focus))
            .DefaultIfEmpty(0).Max();
        var finishesFocus = bestHit >= RewrittenAI.Health(focus);

        return (mine < DANGER ? Heal(me, hand, myHurt) : null)
               ?? Finish(me, hand, enemies)
               ?? Heal(me, hand, playersInDanger)
               ?? (mine < HURT ? Heal(me, hand, myHurt) ?? Shield(me, hand, myHurt, threat) : null)
               ?? (finishesFocus ? null : SelfBlade(me, hand))
               ?? (finishesFocus ? null : TrapFor(me, hand, [], focus, RewrittenAI.SchoolOf(me)))
               ?? Hit(me, hand, focus, enemies, enemies.Count >= 2 ? "hits" : "hits its target")
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

    private static Choice Shield(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> hurt, UltimusScout.Threat threat) {
        var who = hurt.FirstOrDefault(h => !hand.Any(s => s.Card.Shield && RewrittenAI.HasEffectOfSpell(h, s.Card.Id)));
        if (who is null) {
            return null;
        }

        // The shield that stops most of what the enemies' decks can hit with; none that stops nothing here.
        var (shield, covers) = hand.Where(h => h.Card.Shield && (h.Card.ShieldWholeTeam || h.Card.ShieldForTeammate || who == me))
            .Select(h => (Hand: h, Covers: threat.Covered(h.Card.ShieldSchools)))
            .Where(x => x.Covers >= MIN_COVER)
            .OrderByDescending(x => x.Covers).ThenByDescending(x => x.Hand.Card.ShieldWholeTeam ? 1 : 0)
            .FirstOrDefault();
        if (shield is null) {
            return null;
        }

        var why = threat.Known && threat.Hits.Count > 0
            ? $" (stops {RewrittenAI.Percent((float) covers)}% of what they hit with: {string.Join(", ", threat.Schools.Take(3))})"
            : "";

        return new(Move.Cast, shield.Index, shield.Card.ShieldWholeTeam ? -1 : who.SlotIndex, $"shields {Name(who)} with {shield.Card.Shown}{why}");
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

    private static Choice SelfBlade(CombatDuelSubCircle me, List<InHand> hand) {
        var blade = hand.FirstOrDefault(h => h.Card.Blade && !h.Card.BladeOnlyTeammate && Fits(h.Card.BladeSchool, RewrittenAI.SchoolOf(me))
                                             && !RewrittenAI.HasEffectOfSpell(me, h.Card.Id));

        return blade is null ? null : new(Move.Cast, blade.Index, blade.Card.BladeWholeTeam ? -1 : me.SlotIndex, $"blades itself with {blade.Card.Shown}");
    }

    private static Choice Pass(string why) => new(Move.Pass, 0, -1, why);

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

    // The name as the game shows it ("Cyclops Legionnaire", not its object's short debug name "0" or "95DD"); a wizard
    // by its name, itself as "itself".
    private static string Name(CombatDuelSubCircle circle) {
        if (circle.IsHumanPlayer) {
            return UltimusDirector.RoleOf(circle.ParticipantObject?.m_globalID.Full ?? 0) is not null ? "itself"
                : circle.ParticipantObject?.m_debugName is { Length: > 0 } wizard ? wizard : $"slot {circle.SlotIndex + 1}";
        }

        return CombatEffectApplicator.TargetName(circle);
    }

}
