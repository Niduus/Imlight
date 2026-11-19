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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;
using InHand = Imlight.CoreLib.Game.BotShared.BotCore.InHand;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form in a battle. He has every spell once (TrueFormPool), so he does not draw a random hand from a deck: as each round's
/// planning begins he takes the seven cards the moment calls for from all his spells, the way a player who owned them all
/// would pick a deck for that very round, and plays one of them. Which seven, and which one he plays, follow how he plays:
///
/// Attack: his best hit on the weakest enemy now, his best hit on all of them, a bigger hit for next round, a blade and a trap
/// that fit that hit, the strongest damage enchantment, and a team heal to be safe; he plays them with Ultimus's own battle brain
/// (two rounds ahead, finishing blows, hits on all of them when two or more are up).
/// Defend: team and single shields against the schools the enemies hit with, a weakness, a stun, a taunt, a heal and a hit.
/// Support: team and single heals, a cleanse when a teammate carries a weakness or the like, blades for the group's head and the
/// team, a team shield and a hit.
/// Auto (unless the group asks for one): support when a teammate is in danger, defend when the enemies' habits say a big hit is
/// coming, attack otherwise, decided again each round.
/// Evil (asked for): a funny troll. He stays on his team and only plays moves the game allows, but they are the wrong ones: a weak
/// hit of the wrong school that sets off the traps his team laid, Doom and Gloom when his team needs heals, a heal on whoever is
/// already full, or nothing at all, with a cheeky line in chat.
///
/// His spells match the level of his group's head (TrueForm.SpellLevel); dev spells come only in a group with a player, and
/// count a little less than a real spell, so he leans on vanilla spells. A spell a player asked for by name (".trueform use
/// Medusa") is dealt every round while it fits.
/// </summary>
internal static class TrueFormBattle {

    internal const int HAND = 7;

    // How much less a dev spell counts than a real one (Niduus: "Relying more on Vanilla spells").
    private const double DEV_WEIGHT = 0.7;

    private sealed record Kinds(List<TrueFormPool.Entry> All, List<TrueFormPool.Entry> Hits, List<TrueFormPool.Entry> AllHits,
        List<TrueFormPool.Entry> Blades, List<TrueFormPool.Entry> Traps, List<TrueFormPool.Entry> Heals, List<TrueFormPool.Entry> Shields,
        List<TrueFormPool.Entry> Weak, List<TrueFormPool.Entry> Stuns, List<TrueFormPool.Entry> Cleanses, List<TrueFormPool.Entry> Taunts,
        List<TrueFormPool.Entry> Enchants, List<TrueFormPool.Entry> Globals);

    private static readonly ConcurrentDictionary<(int Level, bool Dev), Kinds> s_kinds = new();

    private static Kinds KindsAt(int level, bool dev) => s_kinds.GetOrAdd((level, dev), key => {
        var all = TrueFormPool.At(key.Level, key.Dev);
        bool Has(TrueFormPool.Entry e, Func<SpellEffect, bool> test) {
            try {
                return CoreObjectFactory.GetCoreTemplate(e.Id) is SpellTemplate t && CombatEnchantments.Leaves(t.m_effects ?? []).Any(x => x is not null && test(x));
            }
            catch (Exception) {
                return false;
            }
        }

        bool Friendly(kEffectTarget t) => t is kEffectTarget.kSelf or kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe
            or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kMultiTargetFriendly;

        bool IsEnchant(TrueFormPool.Entry e) => CoreObjectFactory.GetCoreTemplate(e.Id) is SpellTemplate t && CombatEnchantments.IsEnchantment(t);
        // Enchantments from every version he may have, so the strongest comes up (Niduus, 2026-11-19: he used Monstrous, +275,
        // at top level, where Epic is +315; a family read by name can hide a stronger one behind its first version).
        var enchants = TrueFormPool.Versions(key.Level, key.Dev).Where(IsEnchant)
            // A dev enchantment far over the game's best (+315) would be a dev one-shot by the back door.
            .Where(e => !e.Dev || (BotEnchants.Of(e.Id)?.Amount ?? 0) <= 400)
            .GroupBy(e => e.Id).Select(g => g.First()).ToList();
        var enchantIds = all.Where(IsEnchant).Select(e => e.Id).Concat(enchants.Select(e => e.Id)).ToHashSet();
        var plain = all.Where(e => !enchantIds.Contains(e.Id) && !BotCards.NotVanilla(e.Card)).ToList();

        return new Kinds(plain,
            [.. plain.Where(e => e.Card.Hit && !e.Card.HitsAll)],
            [.. plain.Where(e => e.Card.Hit && e.Card.HitsAll)],
            [.. plain.Where(e => e.Card.Blade && !e.Card.Hit)],
            [.. plain.Where(e => e.Card.Trap && !e.Card.Hit)],
            [.. plain.Where(e => e.Card.Heal && !e.Card.Hit)],
            [.. plain.Where(e => e.Card.Shield && !e.Card.Hit && !e.Card.Heal)],
            [.. plain.Where(e => e.Card.Weakness && !e.Card.Hit)],
            [.. plain.Where(e => e.Card.Stun && !e.Card.Hit)],
            [.. plain.Where(e => !e.Card.Hit && Has(e, x => Friendly(x.m_effectTarget) && x.m_effectType is kSpellEffects.kRemoveCharm or kSpellEffects.kRemoveOverTime))],
            [.. plain.Where(e => !e.Card.Hit && Has(e, x => x.m_effectType == kSpellEffects.kTaunt))],
            [.. enchants.Where(e => BotEnchants.Of(e.Id) is { Kind: BotEnchants.DAMAGE or BotEnchants.PIERCE })],
            [.. plain.Where(e => !e.Card.Hit && Has(e, x => x.m_effectTarget == kEffectTarget.kGlobal))]);
    });

    /// <summary>The spells he has now (his group head's level, dev spells in a group), all kinds.</summary>
    internal static int SpellCount() => TrueFormPool.Ready ? KindsAt(TrueForm.SpellLevel(), TrueForm.DevSpellsNow).All.Count : 0;

    // ---------------------------------------------------------------------
    // How he plays this round
    // ---------------------------------------------------------------------

    /// <summary>The way he plays this round: the mode asked, or for Auto, what the fight needs now.</summary>
    internal static TrueForm.Mode RoundMode(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, int round, out string why) {
        var mode = TrueForm.Now;
        why = TrueForm.Asked ? "my group asked for it" : "";
        if (mode != TrueForm.Mode.Auto) {
            return mode;
        }

        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        var low = allies.Where(a => RewrittenAI.HealthShare(a) < 0.45f).OrderBy(RewrittenAI.HealthShare).FirstOrDefault();
        if (low is not null || (allies.Count > 1 && allies.Average(a => RewrittenAI.HealthShare(a)) < 0.6f)) {
            why = low is null ? "the team is worn down" : $"{UltimusTactics.Name(low)} is at {RewrittenAI.Percent(RewrittenAI.HealthShare(low))}% health";
            return TrueForm.Mode.Support;
        }

        var (incoming, hardest) = BotCore.Incoming(enemies, round, UltimusTactics.Name);
        if (incoming >= 0.45 || allies.Any(a => a != me && RewrittenAI.HealthShare(a) < 0.65f && incoming >= 0.25)) {
            why = $"{hardest} tends to hit hard on round {round}";
            return TrueForm.Mode.Defend;
        }

        why = "the team is safe";
        return TrueForm.Mode.Attack;
    }

    // ---------------------------------------------------------------------
    // The seven cards
    // ---------------------------------------------------------------------

    /// <summary>
    /// His hand for this round: the seven cards the round calls for, put in his hand in place of whatever was drawn. Returns
    /// what was dealt, for his log; null when nothing was dealt (his spells are still being read: the drawn hand stays).
    /// </summary>
    internal static string Deal(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, int round) {
        if (me?._combatDeck is not { } deck || !TrueFormPool.Ready) {
            return null;
        }

        var level = TrueForm.SpellLevel();
        var dev = TrueForm.DevSpellsNow;
        var kinds = KindsAt(level, dev);
        if (kinds.All.Count == 0) {
            return null;
        }

        var mode = RoundMode(me, circles, round, out var why);
        var ids = Pick(me, circles, kinds, mode);
        // Never a weaker aura or global over a stronger one already up (Niduus, 2026-11-19: he put a 100% aura for 4 rounds
        // over a 666% one with 66 rounds left).
        var weaker = ids.Where(id => WorseThanUp(me, id, out _)).ToList();
        ids.RemoveAll(weaker.Contains);
        // The group leader's orders waiting (UltimusOrders): a card for each goes in first, so "blade me" or "Legion Shield
        // us" is in his hand to cast (Niduus, 2026-11-19: "BLADE ME" got "no blade card in his hand").
        foreach (var wanted in ForOrders(me, circles, kinds).AsEnumerable().Reverse()) {
            ids.Remove(wanted);
            ids.Insert(0, wanted);
        }

        while (ids.Count > HAND) {
            ids.RemoveAt(ids.Count - 1);
        }

        foreach (var asked in TrueForm.Requested) {
            if (ids.Count >= HAND) {
                ids.RemoveAt(ids.Count - 1);
            }

            if (!ids.Contains(asked)) {
                ids.Insert(0, asked);
            }
        }

        if (ids.Count == 0) {
            return null;
        }

        deck.ReturnHandToDeck();
        var shown = new List<string>();
        foreach (var id in ids.Take(HAND)) {
            if (SpellFactory.GetSpell(id) is not { } spell) {
                continue;
            }

            deck.AddExtraCardToHand(spell);
            shown.Add(TrueFormPool.Of(id)?.Shown ?? RewrittenAI.ReadTemplate(id)?.Shown ?? id.ToString());
        }

        return $"plays {mode.ToString().ToLowerInvariant()}{(why.Length > 0 ? $" ({why})" : "")} with spells of level {level}{(dev ? " and dev spells" : "")}: " +
               string.Join(", ", shown);
    }

    /// <summary>Whether his hand can be dealt for this order (a card of its kind, or the card named).</summary>
    internal static bool CanDealFor(UltimusOrders.Order order) => order.Kind is UltimusOrders.Kind.Heal or UltimusOrders.Kind.Shield
        or UltimusOrders.Kind.Blade or UltimusOrders.Kind.Trap or UltimusOrders.Kind.Attack or UltimusOrders.Kind.Focus
        || (order.Kind is UltimusOrders.Kind.Cast or UltimusOrders.Kind.Suggest && TrueFormPool.Ready
            && TrueFormPool.Find(order.Card, TrueForm.SpellLevel(), TrueForm.DevSpellsNow) is not null);

    // One card for each order waiting, the best of its kind he can cast now (else the best of its kind), fit for whom it is
    // meant: a teammate ("blade me": the leader), the whole team ("shield us"), every enemy ("kill them all").
    private static List<uint> ForOrders(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, Kinds k) {
        var ids = new List<uint>();
        var pending = UltimusOrders.Waiting();
        if (pending.Count == 0) {
            return ids;
        }

        var pips = RewrittenAI.PipsOf(me);
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        var focus = enemies.OrderBy(RewrittenAI.Health).FirstOrDefault();
        TrueFormPool.Entry Best(IEnumerable<TrueFormPool.Entry> list, Func<TrueFormPool.Entry, double> score) {
            var all = list.ToList();
            var now = all.Where(e => e.Card.Rank <= pips).ToList();
            return (now.Count > 0 ? now : all).OrderByDescending(e => score(e) * (e.Dev ? DEV_WEIGHT : 1)).FirstOrDefault();
        }

        foreach (var p in pending) {
            var o = p.Order;
            var said = (o.Target ?? "").Trim().ToLowerInvariant();
            var team = said is "us" or "team" or "the team" or "everyone" or "all" or "all of us" or "everybody";
            var self = said is "" or "you" or "yourself" or "urself" or "self";
            TrueFormPool.Entry e = o.Kind switch {
                UltimusOrders.Kind.Heal => Best(k.Heals.Where(x => team ? x.Card.HealWholeTeam : self || x.Card.HealForTeammate || x.Card.HealWholeTeam), x => x.Card.HealAmount + 1),
                UltimusOrders.Kind.Shield => Best(k.Shields.Where(x => team ? x.Card.ShieldWholeTeam : self || x.Card.ShieldForTeammate || x.Card.ShieldWholeTeam),
                    x => 1 + (x.Card.ShieldWholeTeam ? 0.1 : 0)),
                UltimusOrders.Kind.Blade => Best(k.Blades.Where(x => team ? x.Card.BladeWholeTeam
                    : self ? !x.Card.BladeOnlyTeammate : x.Card.BladeForTeammate || x.Card.BladeWholeTeam || x.Card.BladeOnlyTeammate), x => x.Card.BladePercent),
                UltimusOrders.Kind.Trap => Best(k.Traps.Where(x => o.Target != UltimusOrders.ALL_ENEMIES || x.Card.TrapAll), x => x.Card.TrapPercent),
                UltimusOrders.Kind.Attack when o.Target == UltimusOrders.ALL_ENEMIES && enemies.Count > 0
                    => Best(k.AllHits, x => enemies.Sum(en => Math.Min(RewrittenAI.Health(en), BotCore.Estimate(x.Card, me, en)))),
                UltimusOrders.Kind.Attack or UltimusOrders.Kind.Focus when focus is not null => Best(k.Hits, x => BotCore.Estimate(x.Card, me, focus)),
                UltimusOrders.Kind.Cast or UltimusOrders.Kind.Suggest => TrueFormPool.Find(o.Card, TrueForm.SpellLevel(), TrueForm.DevSpellsNow),
                _ => null,
            };

            if (e is not null && !ids.Contains(e.Id)) {
                ids.Add(e.Id);
            }
        }

        return ids;
    }

    private static List<uint> Pick(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, Kinds k, TrueForm.Mode mode) {
        var hand = new List<uint>();
        void Add(TrueFormPool.Entry e) {
            if (e is not null && hand.Count < HAND && !hand.Contains(e.Id)) {
                hand.Add(e.Id);
            }
        }

        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return hand;
        }

        var pips = RewrittenAI.PipsOf(me);
        var focus = enemies.OrderBy(RewrittenAI.Health).First();
        var threat = UltimusScout.InBattle(enemies);
        double W(TrueFormPool.Entry e) => e.Dev ? DEV_WEIGHT : 1;
        bool Now(TrueFormPool.Entry e) => e.Card.Rank <= pips;
        TrueFormPool.Entry Best(IEnumerable<TrueFormPool.Entry> list, Func<TrueFormPool.Entry, double> score)
            => list.Select(e => (Entry: e, Score: score(e) * W(e))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Entry).FirstOrDefault();

        var head = HeadCircle(me, allies);
        var bestHit = Best(k.Hits.Where(Now), e => BotCore.Estimate(e.Card, me, focus));
        var bestAll = enemies.Count >= 2 ? Best(k.AllHits.Where(Now), e => enemies.Sum(x => Math.Min(RewrittenAI.Health(x), BotCore.Estimate(e.Card, me, x)))) : null;
        var hitSchool = bestHit?.Card.DamageSchool ?? RewrittenAI.SchoolOf(me);
        var teamHeal = Best(k.Heals.Where(e => e.Card.HealWholeTeam && Now(e)), e => e.Card.HealAmount + 1);
        var oneHeal = Best(k.Heals.Where(e => e.Card.HealForTeammate && Now(e)), e => e.Card.HealAmount + 1);
        var teamShield = Best(k.Shields.Where(e => e.Card.ShieldWholeTeam && Now(e)), e => Cover(threat, e.Card));
        var oneShield = Best(k.Shields.Where(e => e.Card.ShieldForTeammate && Now(e)), e => Cover(threat, e.Card));
        var hurt = allies.Any(a => RewrittenAI.HealthShare(a) < 0.7f);

        switch (mode) {
            case TrueForm.Mode.Attack:
                Add(bestHit);
                Add(bestAll);
                Add(Best(k.Hits.Where(e => e.Card.Rank <= pips + 2 && e.Id != bestHit?.Id), e => BotCore.Estimate(e.Card, me, focus) * (e.Card.Rank > pips ? 0.9 : 0.5)));
                Add(Best(k.Blades.Where(e => !e.Card.BladeOnlyTeammate && BotCore.Fits(e.Card.BladeSchool, hitSchool) && !RewrittenAI.HasEffectOfSpell(me, e.Id) && Now(e)),
                    e => e.Card.BladePercent + (e.Card.BladeWholeTeam ? 5 : 0)));
                Add(Best(k.Traps.Where(e => BotCore.Fits(e.Card.TrapSchool, hitSchool) && !RewrittenAI.HasEffectOfSpell(focus, e.Id) && Now(e)),
                    e => e.Card.TrapPercent + (e.Card.TrapAll ? 5 : 0)));
                Add(Best(k.Enchants, e => BotEnchants.Of(e.Id) is { } en ? en.Worth : 0));
                Add(hurt ? teamHeal ?? oneHeal : teamShield ?? teamHeal);
                break;

            case TrueForm.Mode.Defend:
                Add(teamShield);
                Add(oneShield);
                Add(Best(k.Weak.Where(Now), e => 1 + (CoreObjectFactory.GetCoreTemplate(e.Id) is SpellTemplate t && t.m_effects?.Count > 1 ? 1 : 0)));
                Add(Best(k.Stuns.Where(Now), e => e.Card.HitsAll || enemies.Count == 1 ? 2 : 1));
                Add(Best(k.Taunts.Where(Now), _ => 1));
                Add(teamHeal ?? oneHeal);
                Add(bestAll ?? bestHit);
                break;

            case TrueForm.Mode.Support: {
                Add(teamHeal);
                Add(oneHeal);
                if (allies.Any(NeedsCleanse)) {
                    Add(Best(k.Cleanses.Where(Now), e => CoreObjectFactory.GetCoreTemplate(e.Id) is SpellTemplate t
                        && CombatEnchantments.Leaves(t.m_effects ?? []).Any(x => x?.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce) ? 2 : 1));
                }

                var headSchool = head is null ? hitSchool : RewrittenAI.SchoolOf(head);
                Add(Best(k.Blades.Where(e => (e.Card.BladeForTeammate || e.Card.BladeWholeTeam) && BotCore.Fits(e.Card.BladeSchool, headSchool)
                                             && (head is null || !RewrittenAI.HasEffectOfSpell(head, e.Id)) && Now(e)), e => e.Card.BladePercent));
                Add(Best(k.Blades.Where(e => e.Card.BladeWholeTeam && Now(e)), e => e.Card.BladePercent));
                Add(teamShield ?? oneShield);
                Add(bestHit ?? bestAll);
                break;
            }

            case TrueForm.Mode.Evil: {
                // The weakest hit of a school the enemy shields or resists least well against... the wrong one: it sets off
                // the traps his team laid on the enemy before their big hit lands.
                Add(k.Hits.Where(Now).OrderBy(e => e.Card.BaseDamage).ThenBy(e => e.Card.Rank).FirstOrDefault());
                Add(k.Globals.FirstOrDefault(e => e.Shown.Contains("Doom and Gloom", StringComparison.OrdinalIgnoreCase)));
                Add(k.Heals.Where(e => e.Card.HealForTeammate && Now(e)).OrderBy(e => e.Card.HealAmount).FirstOrDefault());
                Add(k.Blades.Where(e => !e.Card.BladeOnlyTeammate && !BotCore.Fits(e.Card.BladeSchool, RewrittenAI.SchoolOf(me)) && Now(e))
                    .OrderBy(e => e.Card.BladePercent).FirstOrDefault());
                Add(k.All.FirstOrDefault(e => e.Shown.EndsWith("Prism", StringComparison.OrdinalIgnoreCase) && Now(e)));
                Add(teamHeal);
                Add(bestHit);
                break;
            }
        }

        // Room left: the best of what else helps, in this order.
        foreach (var more in new[] { bestHit, bestAll, teamHeal, teamShield, oneHeal }) {
            Add(more);
        }

        return hand;
    }

    private static double Cover(UltimusScout.Threat threat, Card card) {
        var covers = threat.Known ? threat.Covered(card.ShieldSchools) : 0.5;
        return covers < 0.25 ? 0 : covers + (card.ShieldWholeTeam ? 0.1 : 0);
    }

    /// <summary>A teammate with a weakness, infection or a damage over time on them.</summary>
    internal static bool NeedsCleanse(CombatDuelSubCircle ally) {
        if (ally?._hangingEffects is not { } hanging) {
            return false;
        }

        lock (hanging) {
            return hanging.Any(x => x is not null && ((x.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingHeal
                                                           or kSpellEffects.kModifyAccuracy && x.m_effectParam < 0)
                                                      || x.m_effectType == kSpellEffects.kDamageOverTime));
        }
    }

    /// <summary>His group head's circle in this battle, else the first player beside him, else null.</summary>
    internal static CombatDuelSubCircle HeadCircle(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> allies) {
        var head = TrueForm.Head();
        var gid = head == 0 ? 0 : WizardData.Models.Player.Wizard.GetGameObjectId(head);
        return allies.FirstOrDefault(a => a != me && gid != 0 && a.ParticipantObject?.m_globalID.Full == gid)
               ?? allies.FirstOrDefault(a => a != me && a.IsHumanPlayer && UltimusDirector.RoleOf(a.ParticipantObject?.m_globalID.Full ?? 0) is null);
    }

    // ---------------------------------------------------------------------
    // The card he plays
    // ---------------------------------------------------------------------

    /// <summary>What a troll says when he plays a troll move (Evil), or null.</summary>
    internal static string Quip { get; private set; }

    /// <summary>
    /// Whether casting this spell would put a weaker aura on him, or a weaker global on the battle, in place of the one up
    /// now: each counted as its effects' amounts times the rounds they last (the one up now, by the rounds it has left).
    /// </summary>
    internal static bool WorseThanUp(CombatDuelSubCircle me, uint id, out string why) {
        why = "";
        if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate t) {
            return false;
        }

        var leaves = CombatEnchantments.Leaves(t.m_effects ?? []).Where(e => e is not null).ToList();
        static double Worth(IEnumerable<SpellEffect> effects)
            => effects.Sum(e => Math.Abs((double) e.m_effectParam) * Math.Max(1, e.m_numRounds));

        var newGlobal = leaves.Where(e => e.m_effectTarget == kEffectTarget.kGlobal).ToList();
        if (newGlobal.Count > 0) {
            var up = me?._duelActor?.Duel?.m_duelModifier?.m_battlefieldEffects?.Where(e => e is not null).ToList() ?? [];
            if (up.Count > 0 && Worth(up) >= Worth(newGlobal)) {
                why = "a stronger global is already up";
                return true;
            }

            return false;
        }

        var newAura = leaves.Where(CombatRequirements.IsAura).ToList();
        if (newAura.Count == 0 || me?._hangingEffects is not { } hanging) {
            return false;
        }

        List<SpellEffect> mine;
        lock (hanging) {
            mine = [.. hanging.Where(e => e is not null && CombatRequirements.IsAura(e) && !CombatResolver.IsShadowSelf(e) && !CombatResolver.StaysWithNewAura(e))];
        }

        if (mine.Count > 0 && Worth(mine) >= Worth(newAura)) {
            why = "a stronger aura is already on him";
            return true;
        }

        return false;
    }

    /// <summary>His pick for this round from the hand he was dealt.</summary>
    internal static UltimusTactics.Choice Decide(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver, int round) {
        Quip = null;
        var mode = RoundMode(me, circles, round, out var modeWhy);
        var allies = circles.Where(c => c.OccupiedTeam == me.OccupiedTeam && c.IsAlive).ToList();
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return new(UltimusTactics.Move.Pass, 0, -1, "no enemy is left");
        }

        var hand = UltimusTactics.Hand(me).Where(h => h.Castable).ToList();
        var head = HeadCircle(me, allies);
        var tag = $" ({mode.ToString().ToLowerInvariant()}{(modeWhy.Length > 0 ? ": " + modeWhy : "")})";
        UltimusTactics.Choice Said(UltimusTactics.Choice c) => c with { Why = c.Why + tag };

        switch (mode) {
            case TrueForm.Mode.Support:
                if (Heal(me, hand, allies) is { } heal) {
                    return Said(heal);
                }

                if (allies.FirstOrDefault(NeedsCleanse) is { } dirty
                    && hand.FirstOrDefault(h => TrueFormPool.Of(h.Card.Id) is { } e && KindsHas(e, k => k.Cleanses)) is { } cleanse) {
                    var team = Wide(cleanse.Card.Id);
                    return Said(new(UltimusTactics.Move.Cast, cleanse.Index, team ? -1 : dirty.SlotIndex,
                        $"cleanses {(team ? "the team" : UltimusTactics.Name(dirty))} with {cleanse.Card.Shown}"));
                }

                if (Blade(me, hand, head) is { } blade) {
                    return Said(blade);
                }

                break;

            case TrueForm.Mode.Defend:
                if (Heal(me, hand, allies, 0.5f) is { } urgent) {
                    return Said(urgent);
                }

                if (Shield(me, hand, allies, enemies) is { } shield) {
                    return Said(shield);
                }

                var strongest = enemies.OrderByDescending(e => RewrittenAI.IsBoss(e) ? 1 : 0).ThenByDescending(RewrittenAI.Health).First();
                if (hand.FirstOrDefault(h => h.Card.Weakness && !RewrittenAI.HasEffectOfSpell(strongest, h.Card.Id)) is { } weak) {
                    return Said(new(UltimusTactics.Move.Cast, weak.Index, weak.Card.HitsAll ? -1 : strongest.SlotIndex,
                        $"weakens {UltimusTactics.Name(strongest)} with {weak.Card.Shown}"));
                }

                if (hand.FirstOrDefault(h => h.Card.Stun && !h.Card.Hit) is { } stun) {
                    return Said(new(UltimusTactics.Move.Cast, stun.Index, stun.Card.HitsAll ? -1 : strongest.SlotIndex,
                        $"stuns {UltimusTactics.Name(strongest)} with {stun.Card.Shown}"));
                }

                if (hand.FirstOrDefault(h => TrueFormPool.Of(h.Card.Id) is { } e && KindsHas(e, k => k.Taunts)) is { } taunt) {
                    return Said(new(UltimusTactics.Move.Cast, taunt.Index, Wide(taunt.Card.Id) ? -1 : strongest.SlotIndex,
                        $"draws {UltimusTactics.Name(strongest)}'s hits onto himself with {taunt.Card.Shown}"));
                }

                break;

            case TrueForm.Mode.Evil:
                // A troll who only ever passed would be no fun either: half the time, nothing silly at hand, he really plays.
                if (Troll(me, hand, allies, enemies, head, resolver) is { } troll) {
                    return Said(troll);
                }

                if (Random.Shared.NextDouble() < 0.5) {
                    Quip = Pick("I'm thinking... I'm thinking...", "Nah.", "I'll sit this one out.", "Have you tried winning?");
                    return Said(new(UltimusTactics.Move.Pass, 0, -1, "does nothing at all (evil)"));
                }

                break;
        }

        // Attack, or nothing of the mode's own to do: Ultimus's own battle brain on this hand. A weaker aura or global over
        // a stronger one is never his pick: he passes instead (the dealt hand leaves those out; a spell asked for may not).
        var pick = UltimusTactics.Decide(me, circles, resolver, round);
        if (pick.Move == UltimusTactics.Move.Cast && me.GetSpellFromLastHand((byte) pick.Card) is { } spell
            && WorseThanUp(me, spell.m_templateID, out var worse)) {
            return Said(new(UltimusTactics.Move.Pass, 0, -1, $"keeps what is up ({worse}) instead of casting {RewrittenAI.ReadTemplate(spell.m_templateID)?.Shown ?? "a weaker one"}"));
        }

        return Said(pick);
    }

    private static bool KindsHas(TrueFormPool.Entry e, Func<Kinds, List<TrueFormPool.Entry>> list)
        => list(KindsAt(TrueForm.SpellLevel(), TrueForm.DevSpellsNow)).Any(x => x.Id == e.Id);

    // A spell on the whole team (its effects aim at the whole friendly side).
    private static bool Wide(uint id) => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t
        && CombatEnchantments.Leaves(t.m_effects ?? []).Any(x => x?.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce
                                                                 or kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce);

    private static UltimusTactics.Choice Heal(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, float under = 0.7f) {
        var hurt = allies.Where(a => RewrittenAI.HealthShare(a) < under).OrderBy(RewrittenAI.HealthShare).ToList();
        if (hurt.Count == 0) {
            return null;
        }

        var who = hurt[0];
        var heal = hand.Where(h => h.Card.Heal && !h.Card.Hit && (h.Card.HealWholeTeam || who == me || h.Card.HealForTeammate))
            .OrderByDescending(h => hurt.Count >= 2 && h.Card.HealWholeTeam ? 1 : 0).ThenByDescending(h => h.Card.HealAmount).FirstOrDefault();
        if (heal is null) {
            return null;
        }

        var target = heal.Card.HealWholeTeam || (who == me && !heal.Card.HealForTeammate) ? -1 : who.SlotIndex;
        return new(UltimusTactics.Move.Cast, heal.Index, target, heal.Card.HealWholeTeam
            ? $"heals the team with {heal.Card.Shown}"
            : $"heals {UltimusTactics.Name(who)} ({RewrittenAI.Percent(RewrittenAI.HealthShare(who))}% health) with {heal.Card.Shown}");
    }

    private static UltimusTactics.Choice Blade(CombatDuelSubCircle me, List<InHand> hand, CombatDuelSubCircle head) {
        var on = head ?? me;
        var school = RewrittenAI.SchoolOf(on);
        var blade = hand.Where(h => h.Card.Blade && !h.Card.Hit && BotCore.Fits(h.Card.BladeSchool, school) && !RewrittenAI.HasEffectOfSpell(on, h.Card.Id)
                                    && (h.Card.BladeWholeTeam || h.Card.BladeForTeammate || on == me))
            .OrderByDescending(h => h.Card.BladePercent).FirstOrDefault();
        if (blade is null) {
            return null;
        }

        var target = blade.Card.BladeWholeTeam ? -1 : on.SlotIndex;
        return new(UltimusTactics.Move.Cast, blade.Index, target, blade.Card.BladeWholeTeam
            ? $"blades the whole team with {blade.Card.Shown}"
            : $"blades {(on == me ? "himself" : UltimusTactics.Name(on))} with {blade.Card.Shown} (+{blade.Card.BladePercent}%)");
    }

    private static UltimusTactics.Choice Shield(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> enemies) {
        var threat = UltimusScout.InBattle(enemies);
        var who = allies.OrderBy(a => a.IsHumanPlayer && a != me ? 0 : 1).ThenBy(RewrittenAI.HealthShare)
            .FirstOrDefault(a => !hand.Any(s => s.Card.Shield && RewrittenAI.HasEffectOfSpell(a, s.Card.Id)));
        if (who is null) {
            return null;
        }

        var shield = hand.Where(h => h.Card.Shield && !h.Card.Hit && !h.Card.Heal && (h.Card.ShieldWholeTeam || h.Card.ShieldForTeammate || who == me))
            .Select(h => (Hand: h, Covers: Cover(threat, h.Card))).Where(x => x.Covers > 0)
            .OrderByDescending(x => x.Hand.Card.ShieldWholeTeam && allies.Count > 1 ? 1 : 0).ThenByDescending(x => x.Covers).Select(x => x.Hand).FirstOrDefault();
        if (shield is null) {
            return null;
        }

        return new(UltimusTactics.Move.Cast, shield.Index, shield.Card.ShieldWholeTeam ? -1 : who.SlotIndex,
            shield.Card.ShieldWholeTeam ? $"shields the team with {shield.Card.Shown}" : $"shields {UltimusTactics.Name(who)} with {shield.Card.Shown}");
    }

    // Evil: the wrong move, as a joke, never one the game does not allow.
    private static UltimusTactics.Choice Troll(CombatDuelSubCircle me, List<InHand> hand, List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> enemies,
                                               CombatDuelSubCircle head, CombatResolver resolver) {
        // An enemy his team laid traps on: a tiny hit first sets them off before the big one lands.
        var trapped = enemies.Where(e => RewrittenAI.TrapsOn(e, "") > 0 || (e._hangingEffects is { } h && HasTrap(e)))
            .OrderByDescending(e => RewrittenAI.IsBoss(e) ? 1 : 0).FirstOrDefault();
        var tiny = hand.Where(h => h.Card.Hit && !h.Card.HitsAll).OrderBy(h => h.Card.BaseDamage).FirstOrDefault();
        if (trapped is not null && tiny is not null) {
            Quip = Pick("Oops! Did someone need those traps?", "I'll just pop these for you. You're welcome!", "Traps? What traps?");
            return new(UltimusTactics.Move.Cast, tiny.Index, trapped.SlotIndex, $"pops the team's traps on {UltimusTactics.Name(trapped)} with a tiny {tiny.Card.Shown} (evil)");
        }

        // His team is hurt: Doom and Gloom, so every heal heals less.
        if (allies.Any(a => RewrittenAI.HealthShare(a) < 0.7f)
            && hand.FirstOrDefault(h => h.Card.Shown.Contains("Doom and Gloom", StringComparison.OrdinalIgnoreCase)) is { } doom) {
            Quip = Pick("Healing is overrated anyway.", "Let's make this interesting!", "Who wanted heals? Nobody? Great.");
            return new(UltimusTactics.Move.Cast, doom.Index, -1, $"casts {doom.Card.Shown} while his team needs heals (evil)");
        }

        // A heal on whoever is already full.
        var full = allies.OrderByDescending(RewrittenAI.HealthShare).FirstOrDefault(a => RewrittenAI.HealthShare(a) >= 0.98f);
        if (full is not null && hand.FirstOrDefault(h => h.Card.Heal && !h.Card.Hit && h.Card.HealForTeammate) is { } waste) {
            Quip = Pick($"You looked a little pale, {UltimusTactics.Name(full)}.", "A heal for the healthiest! Very important.", "Topping you off. You're welcome.");
            return new(UltimusTactics.Move.Cast, waste.Index, full.SlotIndex, $"heals {UltimusTactics.Name(full)}, who is at full health (evil)");
        }

        return null;
    }

    private static bool HasTrap(CombatDuelSubCircle enemy) {
        lock (enemy._hangingEffects) {
            return enemy._hangingEffects.Any(x => x is not null && x.m_effectType == kSpellEffects.kModifyIncomingDamage && x.m_effectParam > 0);
        }
    }

    private static string Pick(params string[] lines) => lines[Random.Shared.Next(lines.Length)];

}
