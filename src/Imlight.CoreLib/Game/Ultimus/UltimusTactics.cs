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

using Imlight.CoreLib.Game.BotShared;
using InHand = Imlight.CoreLib.Game.BotShared.BotCore.InHand;

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


    // When it heals (its playbook, Battle.HealAt and Battle.BigHealAt; Niduus can change them, live).
    // His mood moves them: careful he heals sooner, bold a little later (UltimusMood).
    private static float HURT => (float) UltimusPlaybook.Value("Battle.HealAt") + UltimusMood.HealShift;
    private static float DANGER => (float) UltimusPlaybook.Value("Battle.BigHealAt") + (UltimusMood.HealShift / 2);

    internal static Choice Decide(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver, int round = 1) {
        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return new(Move.Pass, 0, -1, "no enemy is left");
        }

        var whole = Hand(me);
        var castable = whole.Where(h => h.Castable).ToList();
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

        // The team's target: the one a teammate is already hitting, else one that heals often (the shared brain's habits:
        // a fight drags on while it heals), else the weakest.
        var healer = enemies.Select(e => (Who: e, Habit: HabitOf(e)))
            .Where(x => x.Habit is not null && x.Habit.Casts >= 4 && x.Habit.Chance(BotKnowledge.HEAL) >= UltimusPlaybook.Value("Battle.HealerFirst"))
            .OrderByDescending(x => x.Habit.Chance(BotKnowledge.HEAL)).Select(x => x.Who).FirstOrDefault();
        var focus = hits.Select(h => h.On).FirstOrDefault(e => e.IsAlive) ?? healer ?? enemies.OrderBy(RewrittenAI.Health).First();
        var focusWhy = healer is not null && focus == healer
            ? $" ({Name(healer)} heals {HabitOf(healer).Chance(BotKnowledge.HEAL):P0} of its casts, so it goes first)"
            : "";
        var hurt = allies.Where(a => !teamHealed && !healed.Contains(a) && RewrittenAI.HealthShare(a) < HURT)
            .OrderBy(RewrittenAI.HealthShare).ToList();

        // What the enemies can hit with, read from their decks (UltimusScout).
        var threat = UltimusScout.InBattle(enemies);

        // A player asked him to help in their fight (group helper, UltimusDirector.Helper.cs): their role first, while he is
        // not in danger himself.
        if (UltimusHelp.Now is { } help && RewrittenAI.HealthShare(me) >= DANGER
            && allies.FirstOrDefault(a => a.IsHumanPlayer && a.ParticipantObject?.m_globalID.Full == help.GlobalId) is { } friend
            && Helping(me, castable, enemies, friend, help, threat, picks.FirstOrDefault(p => p.Who == friend && p.Card.Hit).On ?? focus) is { } helped
            && BotBossPlaybook.HoldBack(BotBossPlaybook.Watch(enemies), castable.First(h => h.Index == helped.Card).Card, me,
                helped.Target >= 0 ? circles.FirstOrDefault(c => c.SlotIndex == helped.Target) : null, Math.Max(1, round), false) is null) {
            return helped;
        }

        // The boss playbooks of the creatures here, read from their cheat files (BotBossPlaybook): a card one of them punishes
        // is held back, a timed free hit gets a heal or shield first, and a pass it punishes becomes a move.
        var watch = BotBossPlaybook.Watch(enemies);
        return watch.Count == 0
            ? Solo(me, castable, whole, enemies, focus, hurt, threat, Math.Max(1, round), focusWhy)
            : Guarded(me, circles, castable, whole, enemies, focus, hurt, threat, Math.Max(1, round), focusWhy, watch);
    }

    // ---- helping a player who asked (healer or hitter) ----

    private static Choice Helping(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> enemies, CombatDuelSubCircle friend,
                                  UltimusHelp.Ask help, UltimusScout.Threat threat, CombatDuelSubCircle theirTarget) {
        var name = Name(friend);
        if (help.Role == BotShared.BotAsks.HelpRole.Healer) {
            var share = RewrittenAI.HealthShare(friend);
            var pick = (share < 0.75f ? Heal(me, hand, [friend]) : null) ?? (share < 0.9f ? Shield(me, hand, [friend], threat) : null);
            return pick is null ? null : pick with { Why = $"{pick.Why} ({name} asked me to be their healer)" };
        }

        var target = theirTarget is { IsAlive: true } && theirTarget.OccupiedTeam != me.OccupiedTeam ? theirTarget : null;
        var hit = Finish(me, hand, enemies) ?? (target is null ? null : Hit(me, hand, target, enemies, $"hits what {name} hits"));
        return hit is null || hit.Move != Move.Cast ? null : hit with { Why = $"{hit.Why} ({name} asked me to be their hitter)" };
    }

    // ---- against a creature with cheats (its playbook) ----

    private static Choice Guarded(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, List<InHand> castable, List<InHand> whole,
                                  List<CombatDuelSubCircle> enemies, CombatDuelSubCircle focus, List<CombatDuelSubCircle> hurt, UltimusScout.Threat threat,
                                  int round, string focusWhy, List<(CombatDuelSubCircle Who, BotBossPlaybook.Plan Plan)> watch) {
        var mine = RewrittenAI.HealthShare(me);
        var inDanger = mine < DANGER;
        var hand = new List<InHand>(castable);
        var all = new List<InHand>(whole);
        var held = new List<string>();

        string Hold(Choice choice) {
            if (choice.Move != Move.Cast || hand.FirstOrDefault(h => h.Index == choice.Card) is not { } picked) {
                return null;
            }

            var many = picked.Card.HitsAll || picked.Card.HealWholeTeam || picked.Card.ShieldWholeTeam || picked.Card.BladeWholeTeam || picked.Card.TrapAll;
            var on = choice.Target >= 0 ? circles.FirstOrDefault(c => c.SlotIndex == choice.Target) : many ? null : me;
            return BotBossPlaybook.HoldBack(watch, picked.Card, me, on, round, inDanger);
        }

        Choice Said(Choice choice) => held.Count == 0 ? choice
            : choice with { Why = $"{choice.Why} (held back: {string.Join("; ", held.Distinct().Take(2))})" };

        // A free hit due before his next move: a heal or shield first while he can't take it well.
        if (BotBossPlaybook.HitComing(watch, round) is { } coming && mine < HURT + 0.2f
            && (Heal(me, hand, [me]) ?? Shield(me, hand, [me], threat)) is { } guard && Hold(guard) is null) {
            return guard with { Why = $"{guard.Why}: {coming}, so I get ready for it" };
        }

        for (var tries = 0; tries < 8; tries++) {
            var choice = Solo(me, hand, all, enemies, focus, hurt, threat, round, focusWhy);
            if (choice.Move != Move.Cast) {
                // A pass it punishes: something harmless instead (a blade on himself, a shield, a hit).
                if (BotBossPlaybook.PunishesPass(watch) is { } passing
                    && (SelfBlade(me, hand) ?? Shield(me, hand, [me], threat) ?? Hit(me, hand, focus, enemies, "plays rather than pass")) is { } instead
                    && Hold(instead) is null) {
                    return Said(instead with { Why = $"{instead.Why} ({passing}, so I don't pass)" });
                }

                return Said(choice);
            }

            if (Hold(choice) is not { } reason) {
                return Said(choice);
            }

            held.Add(reason);
            hand.RemoveAll(h => h.Index == choice.Card);
            all.RemoveAll(h => h.Index == choice.Card);
        }

        return Said(Pass("every card I hold sets off a cheat right now"));
    }

    // What the bots' shared brain knows of a creature's habits (the shared engine, BotCore).
    private static BotKnowledge.Habit HabitOf(CombatDuelSubCircle enemy) => BotCore.HabitOf(enemy);

    /// <summary>
    /// A shield that guards against none of what the enemies here can hit with (an Elemental Shield against a Myth
    /// creature): useless in this fight, so it is not cast, and it may be discarded.
    /// </summary>
    internal static bool UselessShield(Card card, UltimusScout.Threat threat)
        => card.Shield && !card.Hit && !card.Heal && threat.Known && threat.Covered(card.ShieldSchools) < MIN_COVER;

    private const double MIN_COVER = 0.25;

    // ---- alone (with any players who joined) ----

    private static Choice Solo(CombatDuelSubCircle me, List<InHand> hand, List<InHand> whole, List<CombatDuelSubCircle> enemies,
                               CombatDuelSubCircle focus, List<CombatDuelSubCircle> hurt, UltimusScout.Threat threat, int round, string focusWhy) {
        // The style of this fight, from his own results for its kind (UltimusFightStyles), picked on his first turn.
        var style = UltimusFightStyles.StyleFor(me, enemies);
        var mine = RewrittenAI.HealthShare(me);
        var myHurt = mine < HURT ? new List<CombatDuelSubCircle> { me } : [];
        var playersInDanger = hurt.Where(h => h != me && RewrittenAI.HealthShare(h) < DANGER).ToList();

        // What the creatures' habits say they take from me this round (the shared brain): a heal or a shield first when
        // that would take me under my heal line.
        var (incoming, hardest) = Incoming(enemies, round);
        var foreseen = mine >= HURT && mine - incoming < DANGER
            ? Heal(me, hand, [me]) ?? Shield(me, hand, [me], threat)
            : null;
        if (foreseen is not null) {
            foreseen = foreseen with {
                Why = $"{foreseen.Why}: {hardest} tends to hit on round {round} for about {incoming:P0} of my health together, which would take me " +
                      $"from {mine:P0} under {DANGER:P0}, so I play round it first",
            };
        }

        return (mine < DANGER ? Heal(me, hand, myHurt) : null)
               ?? Finish(me, hand, enemies)
               ?? Heal(me, hand, playersInDanger)
               ?? foreseen
               ?? (mine < HURT ? Heal(me, hand, myHurt) ?? Shield(me, hand, myHurt, threat) : null)
               ?? (style == UltimusFightStyles.HIT_NOW
                   ? Hit(me, hand, focus, enemies, $"hits at once (\"{UltimusFightStyles.HIT_NOW}\" wins this kind of fight for me){focusWhy}")
                   : null)
               ?? TwoRounds(me, hand, whole, focus, enemies, round, mine - incoming < HURT, focusWhy)
               ?? Pass("saves its pips for a bigger hit");
    }

    // The share of my health the enemies' habits say they take this round, and who hits hardest (BotCore).
    private static (double Share, string Hardest) Incoming(List<CombatDuelSubCircle> enemies, int round) => BotCore.Incoming(enemies, round, Name);

    // Two rounds ahead: the shared engine's planner (BotShared.BotCore.TwoRounds), with his playbook's numbers.
    private static Choice TwoRounds(CombatDuelSubCircle me, List<InHand> hand, List<InHand> whole, CombatDuelSubCircle focus,
                                    List<CombatDuelSubCircle> enemies, int round, bool risky, string focusWhy) {
        var plan = BotCore.TwoRounds(me, hand, whole, focus, enemies, round, risky, Name, new BotCore.Lookahead(
            UltimusPlaybook.Value("Battle.LookAheadDelay"), UltimusPlaybook.Value("Battle.LookAheadRisky"), (float) UltimusPlaybook.Value("Battle.TrapGuess")));
        if (plan is null) {
            return null;
        }

        var why = plan.Why + focusWhy;
        if (plan.First is null) {
            return Pass(why);
        }

        var reason = plan.First.Card.Hit ? BalanceResistReason(me, plan.First.Card, plan.First.Card.HitsAll ? enemies : [focus]) : "";

        return new(Move.Cast, plan.First.Index, plan.Target, why + reason);
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
            var finisher = hand.Where(h => h.Card.Hit && !h.Card.Natural && !h.Card.HitsAll && Estimate(h.Card, me, enemy) >= RewrittenAI.Health(enemy))
                .OrderBy(h => h.Card.Rank).FirstOrDefault();
            if (finisher is not null) {
                return new(Move.Cast, finisher.Index, enemy.SlotIndex, $"finishes off {Name(enemy)} with {finisher.Card.Shown}");
            }
        }

        // A hit on everyone that finishes two or more.
        var all = hand.Where(h => h.Card.Hit && h.Card.HitsAll && !h.Card.Natural)
            .FirstOrDefault(h => enemies.Count(e => Estimate(h.Card, me, e) >= RewrittenAI.Health(e)) >= 2);

        return all is null ? null : new(Move.Cast, all.Index, -1, $"finishes off several with {all.Card.Shown}");
    }

    private static Choice Hit(CombatDuelSubCircle me, List<InHand> hand, CombatDuelSubCircle focus, List<CombatDuelSubCircle> enemies, string why) {
        var best = hand.Where(h => h.Card.Hit && !h.Card.Natural)
            .Select(h => (Hand: h, Damage: h.Card.HitsAll
                ? enemies.Sum(e => Math.Min(RewrittenAI.Health(e), Estimate(h.Card, me, e)))
                : Estimate(h.Card, me, focus)))
            .OrderByDescending(x => x.Damage).FirstOrDefault();
        if (best.Hand is null) {
            return null;
        }

        var reason = BalanceResistReason(me, best.Hand.Card, best.Hand.Card.HitsAll ? enemies : [focus]);

        return best.Hand.Card.HitsAll
            ? new(Move.Cast, best.Hand.Index, -1, $"{why}: every enemy with {best.Hand.Card.Shown} (about {best.Damage}){reason}")
            : new(Move.Cast, best.Hand.Index, focus.SlotIndex, $"{why}: {Name(focus)} with {best.Hand.Card.Shown} (about {best.Damage}){reason}");
    }

    // A hit as the game lands it, pierce counted (BotCore).
    private static int Estimate(Card card, CombatDuelSubCircle me, CombatDuelSubCircle target, float blades = 0, float traps = 0)
        => BotCore.Estimate(card, me, target, blades, traps);

    private static float PierceOf(CombatDuelSubCircle circle, string school) => BotCore.PierceOf(circle, school);

    /// <summary>
    /// Against creatures that resist Balance (Niduus, 2026-11-14: Balance Prism is not to be had, so "Ultimus would have to
    /// choose to either go full pierce in a battle or to rely on offbrand damage (IE Hydra for the Ice, Fire, Storm as a
    /// Balance Spell or offbrand Trained spells)"): the reason for the choice, by their resist, its pierce and its hand.
    /// </summary>
    private static string BalanceResistReason(CombatDuelSubCircle me, Card card, List<CombatDuelSubCircle> targets) {
        var resist = targets.Select(t => RewrittenAI.ResistOf(t, "Balance")).DefaultIfEmpty(0).Max();
        if (resist < UltimusPlaybook.Value("Battle.BalanceResistNotice")) {
            return "";
        }

        var pierce = PierceOf(me, "Balance");
        var balanceHit = RewrittenAI.Same(card.DamageSchool, "Balance");
        var left = Math.Max(0, resist - pierce);

        return balanceHit
            ? $". They resist Balance {RewrittenAI.Percent(resist)}%, but my {RewrittenAI.Percent(pierce)}% pierce leaves only {RewrittenAI.Percent(left)}%, so I go full pierce with Balance (no Balance Prism: no player can get one)"
            : $". They resist Balance {RewrittenAI.Percent(resist)}% and my {RewrittenAI.Percent(pierce)}% pierce only takes it to {RewrittenAI.Percent(left)}%, so I hit as {card.DamageSchool} instead (a split hit like Hydra, or a spell I trained), which they resist less";
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

    internal static List<InHand> Hand(CombatDuelSubCircle me) {
        var hand = new List<InHand>();
        for (byte i = 0; i < 24; i++) {
            if (me.GetSpellFromLastHand(i) is not { } spell || RewrittenAI.Read(spell) is not { } card
                || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
                || CombatEnchantments.IsEnchantment(template) || DivideCards.IsPickCard(template)) {
                continue;
            }

            // Never a card no player can get by vanilla means (Balance Prism).
            if (BotShared.BotCards.NotVanilla(card)) {
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
    internal static string Name(CombatDuelSubCircle circle) {
        if (circle.IsHumanPlayer) {
            return UltimusDirector.RoleOf(circle.ParticipantObject?.m_globalID.Full ?? 0) is not null ? "itself"
                : circle.ParticipantObject?.m_debugName is { Length: > 0 } wizard ? wizard : $"slot {circle.SlotIndex + 1}";
        }

        return CombatEffectApplicator.TargetName(circle);
    }

}
