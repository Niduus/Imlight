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
 *
 * ========================================================================
 * COMBAT CRITICAL STRIKE SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Owns the critical hit / block stat lookups, the level-scaled rating to
 * chance conversion (the stat page), and the crit roll of each cast.
 * 
 * USAGE EXAMPLE:
 * CombatActionResolver starts a CastCrits per cast; CombatEffectApplicator
 * asks it for each target's multiplier as damage and heals land.
 * 
 * NOTE:
 * Live model (the 2020 crit/block rebalance): one roll per cast, checked
 * against each target. Chance = levelFactor * 3C / (3C + B), where C is the
 * caster's crit rating, B the target's block rating and levelFactor =
 * min(level, 100) / 100. A landed crit multiplies by 1 + add * (1 - 3B / (C + 3B))
 * (add = m_criticalDamageAddPercent / m_criticalHealAddPercent, 1 in the
 * shipped data): x2 with no block, about x1.25 when block matches crit. A roll
 * that would have crit with no block but missed because of it shows as
 * blocked. Vengeance and Conviction add to their side of the roll, then are
 * consumed.
 * 
 * HARD RULE (Niduus, 2026-10-28, not a setting): only an ENEMY can block a
 * crit. A crit from a teammate (a crit heal on an ally, for players and
 * creatures alike) rolls as if the receiver had no block, lands the full
 * multiplier and never uses up the receiver's Conviction. Live lets the
 * receiving teammate block it; Ahziael never does.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Effects;

namespace Imlight.CoreLib.Game.Combat;

internal static class CombatCriticals {

    private const float MAX_CHANCE = 0.95f; // Client has a soft cap.
    private const float RATING_K_MIN = 200f;
    private const float RATING_K_MAX = 400f;
    private const float RATING_K_SLOPE = 1.25f;
    private const int RATING_K_BASE_LEVEL = 10;
    private const int FULL_CRIT_LEVEL = 100; // The level factor stops growing here.
    private const float BLOCK_WEIGHT = 3f;

    internal static float RatingToChance(float rating, int level) {
        if (rating <= 0f) {
            return 0f;
        }

        return Math.Min(MAX_CHANCE, rating / (rating + GetRatingK(level)));
    }

    /// <summary>What one target got from a cast's crit roll.</summary>
    internal readonly record struct Outcome(bool Crit, float Multiplier, bool Blocked) {
        internal static readonly Outcome None = new(false, 1f, false);
    }

    /// <summary>
    /// The crit roll of one cast: rolled once, checked against each target as its damage or heal lands.
    /// </summary>
    internal sealed class CastCrits {

        private readonly CombatDuelSubCircle _caster;
        private readonly string _school;
        private readonly float _roll = (float) Random.Shared.NextDouble();
        private bool _cheatRuleTaken;
        private string _cheatRule;
        private float? _boost;
        private readonly Dictionary<CombatDuelSubCircle, Outcome> _byTarget = [];

        internal CastCrits(CombatDuelSubCircle caster, string school) {
            _caster = caster;
            _school = school;
        }

        /// <summary>The outcomes so far, by the target's slot.</summary>
        internal Outcome ForSlot(int slot) {
            foreach (var (target, outcome) in _byTarget) {
                if (target.SlotIndex == slot) {
                    return outcome;
                }
            }

            return Outcome.None;
        }

        internal bool AnyCrit => _byTarget.Values.Any(x => x.Crit);

        /// <summary>
        /// The target's outcome. A teammate can only be critted by a heal, and never blocks it.
        /// </summary>
        internal Outcome For(CombatDuelSubCircle target, bool isHeal) {
            if (target is null) {
                return Outcome.None;
            }

            if (_byTarget.TryGetValue(target, out var known)) {
                return known;
            }

            var outcome = Roll(target, isHeal);
            _byTarget[target] = outcome;

            return outcome;
        }

        private Outcome Roll(CombatDuelSubCircle target, bool isHeal) {
            var enemy = target.OccupiedTeam != _caster.FightsFor;
            if (!enemy && !isHeal) {
                return Outcome.None;
            }

            // A cheat's "always crit" / "never crit" (CombatResolver.ExtraCheats.cs), used up once per cast.
            if (!_cheatRuleTaken) {
                _cheatRuleTaken = true;
                _cheatRule = CombatDuelSubCircle.UseRule(ref _caster.CheatCritRule, ref _caster.CheatCritLeft);
            }

            if (_cheatRule == "NoCrit") {
                return Outcome.None;
            }

            var crit = GetCriticalRating(_caster, _school);

            // Only an enemy's block counts (HARD RULE above).
            var block = enemy ? Math.Max(0f, GetBlockRating(target, _school)) : 0f;
            var multiplier = Multiplier(crit, block, isHeal);
            if (_cheatRule == "Crit") {
                return new Outcome(true, multiplier, false);
            }

            if (crit <= 0f) {
                return Outcome.None;
            }

            var levelFactor = Math.Clamp(GetLevel(_caster) / (float) FULL_CRIT_LEVEL, 0f, 1f);
            _boost ??= GetCritBoostPercent(_caster);
            var unblocked = Math.Clamp(levelFactor + _boost.Value, 0f, MAX_CHANCE);
            var chance = levelFactor * BLOCK_WEIGHT * crit / (BLOCK_WEIGHT * crit + block) + _boost.Value;
            if (enemy) {
                chance -= GetCritBlockPercent(target);
            }

            chance = Math.Clamp(chance, 0f, MAX_CHANCE);
            if (_roll < chance) {
                return new Outcome(true, multiplier, false);
            }

            // It would have crit against no block: the enemy blocked it.
            return new Outcome(false, 1f, enemy && _roll < unblocked);
        }

    }

    private static float Multiplier(float crit, float block, bool isHeal) {
        var config = WizStatisticEffectConfigLoader.Config;
        var add = isHeal ? config?.m_criticalHealAddPercent ?? 1f : config?.m_criticalDamageAddPercent ?? 1f;
        if (add <= 0f) {
            add = 1f;
        }

        var denominator = crit + BLOCK_WEIGHT * block;
        var share = denominator <= 0f ? 1f : 1f - BLOCK_WEIGHT * block / denominator;

        return 1f + add * share;
    }

    private static float GetCritBoostPercent(CombatDuelSubCircle caster) {
        var boost = caster._hangingEffects.FirstOrDefault(x => x.m_effectType == kSpellEffects.kCritBoost);
        if (boost is null) {
            return 0f;
        }

        caster._hangingEffects.Remove(boost);

        return boost.m_effectParam / 100f;
    }

    private static float GetCritBlockPercent(CombatDuelSubCircle target) {
        var block = target._hangingEffects.FirstOrDefault(x => x.m_effectType == kSpellEffects.kCritBlock);
        if (block is null) {
            return 0f;
        }

        target._hangingEffects.Remove(block);

        return block.m_effectParam / 100f;
    }

    private static float GetCriticalRating(CombatDuelSubCircle sc, string school) {
        var stats = sc.ParticipantGameStats;
        if (stats is null) {
            return 0f;
        }

        return stats.m_criticalHitRatingAll + sc.GetStatBySchool(stats.m_criticalHitRatingBySchool, school);
    }

    private static float GetBlockRating(CombatDuelSubCircle sc, string school) {
        var stats = sc.ParticipantGameStats;
        if (stats is null) {
            return 0f;
        }

        return stats.m_blockRatingAll + sc.GetStatBySchool(stats.m_blockRatingBySchool, school);
    }

    private static int GetLevel(CombatDuelSubCircle sc) => sc.ParticipantGameStats?.Level ?? 0;

    private static float GetRatingK(int level) {
        // The level-scaled K: the client's crit divisor (100 + 3 * level in the
        // shipped config), clamped into the 200-400 band. Falls back to a linear
        // curve when the config is missing.
        var divisor = WizStatisticEffectConfigLoader.GetCritDivisor(level);
        if (divisor <= 0f) {
            divisor = RATING_K_MIN + (level - RATING_K_BASE_LEVEL) * RATING_K_SLOPE;
        }

        return Math.Clamp(divisor, RATING_K_MIN, RATING_K_MAX);
    }

}
