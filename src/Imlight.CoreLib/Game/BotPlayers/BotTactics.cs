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
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// How a bot plays its turn (Niduus, 2026-11-12), read with the Rewritten AI's tools (RewrittenAI.cs). It looks at the
/// cards the real players on its side have picked this round and plays around them:
/// 1. A teammate in danger gets a heal, real players first.
/// 2. An enemy it can finish off, it finishes, unless a player's hit already does.
/// 3. It helps a player's hit: a blade on the player, or a trap on the player's target only when that turns the hit into
///    a defeat (it does not feint a mob for nothing).
/// 4. It hits its own target and stays on it. It does not hit an enemy a player is about to hit through traps (that
///    would use up the player's traps), unless its own hit defeats that enemy.
/// 5. It builds up (a blade on itself, a trap on its target), or passes.
/// Out of mana with nothing to cast, it flees.
/// </summary>
internal static class BotTactics {

    internal enum Move { Cast, Pass, Flee }

    internal sealed record Choice(Move Move, int Card, int Target, string Why, int TargetSlotKept = -1);

    private sealed record PlayerPlan(CombatDuelSubCircle Player, Card Card, CombatDuelSubCircle Target, int Damage);

    // Health shares under which a teammate needs a heal: a real player sooner than a bot.
    private const float PLAYER_HEAL_AT = 0.5f;
    private const float BOT_HEAL_AT = 0.35f;

    /// <summary>
    /// The bot's move. <paramref name="kept"/> is the enemy it hit last (its slot, or -1). <paramref name="isBot"/> tells
    /// a bot from a real player.
    /// </summary>
    internal static Choice Decide(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver,
                                  int kept, Func<CombatDuelSubCircle, bool> isBot) {
        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return new(Move.Pass, 0, -1, "no enemy is left");
        }

        var hand = Hand(me);
        var castable = hand.Where(h => h.Castable).ToList();
        if (castable.Count == 0) {
            var mana = me.ParticipantGameStats?.m_currentMana ?? 0;
            return mana < 2 && hand.Any(h => h.Card.Rank > 0)
                ? new(Move.Flee, 0, -1, "out of mana")
                : new(Move.Pass, 0, -1, "nothing it can cast yet");
        }

        var plans = allies.Where(a => a != me && a.IsHumanPlayer && !isBot(a))
            .Select(p => resolver.GetQueuedAction(p) is { Spell: { } spell } queued && RewrittenAI.Read(spell) is { } card
                ? new PlayerPlan(p, card, queued.SelectedTarget, card.Hit ? RewrittenAI.Estimate(card, p, queued.SelectedTarget) : 0)
                : null)
            .Where(p => p is not null).ToList();
        var healedThisRound = circles.Select(c => resolver.GetQueuedAction(c))
            .Where(q => q?.Spell is not null && q.SpellCaster != me && RewrittenAI.Read(q.Spell) is { Heal: true, Hit: false })
            .Select(q => q.SelectedTarget).ToHashSet();

        // 1. A heal for whoever needs it most; a real player counts for more.
        var hurt = allies.Where(a => !healedThisRound.Contains(a)
                                     && RewrittenAI.HealthShare(a) < (a.IsHumanPlayer && !isBot(a) ? PLAYER_HEAL_AT : BOT_HEAL_AT))
            .OrderBy(a => RewrittenAI.HealthShare(a) * (a.IsHumanPlayer && !isBot(a) ? 0.75f : 1f)).FirstOrDefault();
        if (hurt is not null) {
            var heal = castable.Where(h => h.Card.Heal && !h.Card.Hit && (hurt == me || h.Card.HealForTeammate || h.Card.HealWholeTeam))
                .OrderByDescending(h => h.Card.HealAmount).FirstOrDefault();
            if (heal is not null) {
                return new(Move.Cast, heal.Index, heal.Card.HealWholeTeam || hurt == me && !heal.Card.HealForTeammate ? -1 : hurt.SlotIndex,
                    $"heals {Who(hurt, isBot)} ({RewrittenAI.Percent(RewrittenAI.HealthShare(hurt))}% health) with {heal.Card.Shown}", kept);
            }
        }

        var attacks = castable.Where(h => h.Card.Hit && !h.Card.Natural).ToList();
        var killedByPlayers = plans.Where(p => p.Card.Hit && p.Target is not null && p.Damage >= RewrittenAI.Health(p.Target))
            .Select(p => p.Target).ToHashSet();

        // 2. A defeat it can make, on an enemy no player is already finishing.
        foreach (var enemy in enemies.Where(e => !killedByPlayers.Contains(e)).OrderBy(RewrittenAI.Health)) {
            var finisher = attacks.Where(h => !h.Card.HitsAll && RewrittenAI.Estimate(h.Card, me, enemy) >= RewrittenAI.Health(enemy))
                .OrderBy(h => h.Card.Rank).FirstOrDefault();
            if (finisher is not null) {
                return new(Move.Cast, finisher.Index, enemy.SlotIndex, $"finishes off {Name(enemy)} with {finisher.Card.Shown}", enemy.SlotIndex);
            }
        }

        // 3. Help a player's hit that does not already defeat its target.
        foreach (var plan in plans.Where(p => p.Card.Hit && p.Target is not null && !killedByPlayers.Contains(p.Target)).OrderByDescending(p => p.Damage)) {
            var blade = castable.FirstOrDefault(h => h.Card.Blade && (h.Card.BladeForTeammate || h.Card.BladeWholeTeam)
                                                     && FitsSchool(h.Card.BladeSchool, plan.Card.DamageSchool));
            if (blade is not null) {
                return new(Move.Cast, blade.Index, blade.Card.BladeWholeTeam ? -1 : plan.Player.SlotIndex,
                    $"blades {Name(plan.Player)} for their {plan.Card.Shown}", kept);
            }

            var trap = castable.FirstOrDefault(h => h.Card.Trap && FitsSchool(h.Card.TrapSchool, plan.Card.DamageSchool)
                                                    && RewrittenAI.Estimate(plan.Card, plan.Player, plan.Target, extraTraps: TrapPercent(h))
                                                    >= RewrittenAI.Health(plan.Target));
            if (trap is not null) {
                return new(Move.Cast, trap.Index, trap.Card.TrapAll ? -1 : plan.Target.SlotIndex,
                    $"traps {Name(plan.Target)} so {Name(plan.Player)}'s {plan.Card.Shown} defeats it", kept);
            }
        }

        // 4. Its own target, kept while it stands. An enemy a player is about to hit through traps is left to that player.
        var playerTargets = plans.Where(p => p.Card.Hit && p.Target is not null).Select(p => p.Target).ToHashSet();
        bool LeaveAlone(CombatDuelSubCircle enemy, Card card)
            => playerTargets.Contains(enemy) && RewrittenAI.TrapsOn(enemy, card.DamageSchool) > 0
               && RewrittenAI.Estimate(card, me, enemy) < RewrittenAI.Health(enemy);

        var target = enemies.FirstOrDefault(e => e.SlotIndex == kept)
                     ?? enemies.Where(e => playerTargets.Contains(e)).OrderBy(RewrittenAI.Health).FirstOrDefault()
                     ?? enemies.OrderBy(RewrittenAI.Health).First();
        var hits = attacks
            .Select(h => (Hand: h, On: h.Card.HitsAll ? null : PickTarget(h.Card, target, enemies, me, LeaveAlone)))
            .Where(x => x.Hand.Card.HitsAll ? !enemies.Any(e => LeaveAlone(e, x.Hand.Card)) : x.On is not null)
            .Select(x => (x.Hand, x.On, Damage: x.Hand.Card.HitsAll
                ? enemies.Sum(e => Math.Min(RewrittenAI.Health(e), RewrittenAI.Estimate(x.Hand.Card, me, e)))
                : RewrittenAI.Estimate(x.Hand.Card, me, x.On)))
            .OrderByDescending(x => x.Damage).ToList();
        if (hits.Count > 0) {
            var (best, on, damage) = hits[0];

            return on is null
                ? new(Move.Cast, best.Index, -1, $"hits every enemy with {best.Card.Shown} (about {damage})", kept)
                : new(Move.Cast, best.Index, on.SlotIndex, $"hits {Name(on)} with {best.Card.Shown} (about {damage})", on.SlotIndex);
        }

        // 5. Build up for next round.
        var selfBlade = castable.FirstOrDefault(h => h.Card.Blade && !h.Card.BladeOnlyTeammate
                                                     && FitsSchool(h.Card.BladeSchool, RewrittenAI.SchoolOf(me))
                                                     && !RewrittenAI.HasEffectOfSpell(me, h.Card.Id));
        if (selfBlade is not null) {
            return new(Move.Cast, selfBlade.Index, selfBlade.Card.BladeWholeTeam ? -1 : me.SlotIndex, $"blades itself with {selfBlade.Card.Shown}", kept);
        }

        var ownTrap = castable.FirstOrDefault(h => h.Card.Trap && FitsSchool(h.Card.TrapSchool, RewrittenAI.SchoolOf(me))
                                                   && !playerTargets.Contains(target) && !RewrittenAI.HasEffectOfSpell(target, h.Card.Id));
        if (ownTrap is not null) {
            return new(Move.Cast, ownTrap.Index, ownTrap.Card.TrapAll ? -1 : target.SlotIndex, $"traps {Name(target)} with {ownTrap.Card.Shown}", kept);
        }

        var shield = castable.FirstOrDefault(h => h.Card.Shield && h.Card.ShieldForTeammate);
        var shaky = allies.Where(a => a.IsHumanPlayer && !isBot(a) && RewrittenAI.HealthShare(a) < 0.7f).OrderBy(RewrittenAI.HealthShare).FirstOrDefault();
        if (shield is not null && shaky is not null) {
            return new(Move.Cast, shield.Index, shaky.SlotIndex, $"shields {Name(shaky)} with {shield.Card.Shown}", kept);
        }

        return new(Move.Pass, 0, -1, "saves its pips", kept);
    }

    private sealed record InHand(int Index, Card Card, bool Castable);

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

    // The kept target when the card may hit it; else another enemy it may hit.
    private static CombatDuelSubCircle PickTarget(Card card, CombatDuelSubCircle kept, List<CombatDuelSubCircle> enemies,
                                                  CombatDuelSubCircle me, Func<CombatDuelSubCircle, Card, bool> leaveAlone)
        => !leaveAlone(kept, card) ? kept
            : enemies.Where(e => !leaveAlone(e, card)).OrderByDescending(e => RewrittenAI.Estimate(card, me, e)).FirstOrDefault();

    private static float TrapPercent(InHand trap) {
        if (CoreObjectFactory.GetCoreTemplate(trap.Card.Id) is not SpellTemplate template) {
            return 0;
        }

        return CombatEnchantments.Leaves(template.m_effects ?? [])
            .Where(e => e is not null && e.m_effectType == kSpellEffects.kModifyIncomingDamage && e.m_effectParam > 0)
            .Select(e => (float) e.m_effectParam).DefaultIfEmpty(0).Max();
    }

    // A blade or trap of no school (universal) fits every hit; a school one only that school's.
    private static bool FitsSchool(string charmSchool, string hitSchool)
        => string.IsNullOrEmpty(charmSchool) || charmSchool.Equals("All", StringComparison.OrdinalIgnoreCase)
           || RewrittenAI.Same(charmSchool, hitSchool);

    private static string Name(CombatDuelSubCircle circle)
        => circle.ParticipantObject?.m_debugName is { Length: > 0 } name ? name : $"slot {circle.SlotIndex}";

    private static string Who(CombatDuelSubCircle circle, Func<CombatDuelSubCircle, bool> isBot)
        => circle.IsHumanPlayer && !isBot(circle) ? "a player" : Name(circle);

}
