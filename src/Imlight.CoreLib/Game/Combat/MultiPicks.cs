/*
 * Picks for "select your targets" spells (kMultiTargetEnemy: Qismah's Curse, the Fusion spells, the tier B AoEs; and
 * kMultiTargetFriendly) when no wizard clicks them: a monster's AI, and a cheat's Cast or FreeCast with a Pick (Niduus,
 * 2026-10-26). The picks go in the cast's PickedTargets, as a wizard's clicks do, so the spell hits only them and the game
 * is sent only them as the cast's targets, so it plays the spell on those.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Combat;

internal static class MultiPicks {

    // Every effect of a spell, with the ones inside lists, random picks, conditionals and target counts.
    private static IEnumerable<SpellEffect> All(IEnumerable<SpellEffect> effects, int depth = 0) {
        foreach (var effect in effects ?? []) {
            if (effect is null || depth > 6) {
                continue;
            }

            yield return effect;
            IEnumerable<SpellEffect> inner = effect switch {
                ConditionalSpellEffect conditional => conditional.m_elements?.Select(e => e?.m_pEffect),
                TargetCountSpellEffect counted => counted.m_effectLists?.Cast<SpellEffect>(),
                RandomSpellEffect random => random.m_effectList,
                VariableSpellEffect variable => variable.m_effectList,
                EffectListSpellEffect list => list.m_effectList,
                CountBasedSpellEffect countBased => countBased.m_effectList,
                _ => null,
            };
            foreach (var child in All(inner, depth + 1)) {
                yield return child;
            }
        }
    }

    /// <summary>Whether the spell lets its caster pick enemies (kMultiTargetEnemy).</summary>
    internal static bool PicksEnemies(SpellTemplate spell) => All(spell?.m_effects).Any(e => e.m_effectTarget == kEffectTarget.kMultiTargetEnemy);

    /// <summary>Whether the spell lets its caster pick teammates (kMultiTargetFriendly).</summary>
    internal static bool PicksFriends(SpellTemplate spell) => All(spell?.m_effects).Any(e => e.m_effectTarget == kEffectTarget.kMultiTargetFriendly);

    internal static bool IsPickSpell(SpellTemplate spell) => PicksEnemies(spell) || PicksFriends(spell);

    /// <summary>
    /// How many it can pick: a target-count spell's lists (Qismah's Curse: one list for 1, 2, 3 and 4 picked), else no
    /// limit but the number standing.
    /// </summary>
    internal static int MaxPicks(SpellTemplate spell) {
        var counts = All(spell?.m_effects).OfType<TargetCountSpellEffect>().Select(t => t.m_effectLists?.Count(l => l is not null) ?? 0)
            .Where(n => n > 0).ToList();

        return counts.Count > 0 ? counts.Max() : int.MaxValue;
    }

    private static double HealthShare(CombatDuelSubCircle c) {
        var most = c.CombatParticipant?.m_maxPlayerHealth ?? 0;
        if (most <= 0) {
            most = c.ParticipantGameStats.m_baseHitpoints + c.ParticipantGameStats.m_bonusHitpoints;
        }

        return most > 0 ? c.ParticipantGameStats.m_currentHitpoints / (double) most : 1;
    }

    // How much of the spell's school it takes off (its resist), 0 when not known.
    private static double Resist(CombatDuelSubCircle c, SpellTemplate spell) {
        try {
            var stats = c.ParticipantGameStats;
            string school = spell?.m_sMagicSchoolName;

            return stats.m_dmgReducePercentAll + (string.IsNullOrEmpty(school) ? 0 : c.GetStatBySchool(stats.m_dmgReducePercent, school));
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>
    /// A monster's picks, as the game's monsters pick: somewhere from one up to as many as the spell takes (or as many as
    /// stand). Its chosen target goes first; a smart one picks as many as it can, the weakest first (the lowest health,
    /// then the lowest resist to the spell's school), any other at random. A heal or help on its side goes on the most hurt.
    /// </summary>
    internal static List<CombatDuelSubCircle> ForMonster(CombatDuelSubCircle caster, SpellTemplate spell, CombatDuelSubCircle chosen,
                                                         bool smart, Random random) {
        var friendly = !PicksEnemies(spell) && PicksFriends(spell);
        var pool = (caster?._duelActor?.ActiveSubCircles ?? [])
            .Where(c => c is not null && c.IsAlive && c.AddedToDuel && (c.OccupiedTeam == caster.OccupiedTeam) == friendly).ToList();
        if (pool.Count == 0) {
            return [];
        }

        var most = Math.Min(MaxPicks(spell), pool.Count);
        var count = smart ? most : random.Next(1, most + 1);
        var ordered = smart || friendly
            ? pool.OrderBy(HealthShare).ThenBy(c => friendly ? 0 : Resist(c, spell)).ToList()
            : pool.OrderBy(_ => random.Next()).ToList();
        if (chosen is not null && ordered.Remove(chosen)) {
            ordered.Insert(0, chosen);
        }

        return [.. ordered.Take(count).OrderBy(c => c.SlotIndex)];
    }

    /// <summary>
    /// A monster's move with a "select your targets" spell: its picks, sent as a wizard's game sends them (one bit for each
    /// circle in RawSpellTarget, the first one as SpellTarget), so the duel reads them as a wizard's picks.
    /// </summary>
    internal static List<CombatDuelSubCircle> AimMove(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE move, CombatDuelSubCircle[] circles,
                                                      CombatDuelSubCircle caster, SpellTemplate spell, bool smart, Random random) {
        var chosen = move.SpellTarget < circles.Length ? circles[move.SpellTarget] : null;
        var picks = ForMonster(caster, spell, chosen, smart, random);
        if (picks.Count > 0) {
            move.SpellTarget = (uint) picks[0].SlotIndex;
            move.RawSpellTarget = picks.Aggregate(0u, (bits, c) => bits | (1u << c.SlotIndex));
        }

        return picks;
    }

}
