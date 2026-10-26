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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// The spell the game is shown when a cheat's free cast puts a spell on someone its card cannot aim at (Niduus,
/// 2026-10-26: "Can't cast self spells like Brace on others"): a self spell on another (FreeCast OnTarget), or a
/// spell for one's own side on the other side. The game works a cast out from the card it is told was cast, so a
/// self spell sent at an ally was drawn on the caster or not at all, while the server put it on the ally. This finds
/// a spell of the game whose effects are the same kind, school and sign, aimed the way this cast goes (Khonsu - Brace
/// for Brace on an ally), its own animation first; the server still puts on the real spell's effects.
/// </summary>
public static class SideLookalikes {

    private static readonly HashSet<kEffectTarget> s_friend = [kEffectTarget.kFriendlySingle, kEffectTarget.kFriendlySingleNotMe];
    private static readonly HashSet<kEffectTarget> s_enemy = [kEffectTarget.kEnemySingle, kEffectTarget.kPreselectedEnemySingle];

    private static bool Aimed(kEffectTarget target) => target == kEffectTarget.kSelf || s_friend.Contains(target) || s_enemy.Contains(target);

    private static List<SpellEffect> Plain(SpellTemplate spell)
        => spell?.m_effects is { Count: > 0 } effects && effects.All(e => e is not null && e.GetType() == typeof(SpellEffect))
            ? [.. effects.OrderBy(e => e.m_effectType).ThenBy(e => ((string) e.m_sDamageType ?? "").ToLowerInvariant())]
            : null;

    /// <summary>
    /// Whether the game cannot draw this spell where it goes: an effect on its caster (kSelf) going on someone else, one
    /// for its own side going on the other side, or one for the other side going on its own.
    /// </summary>
    public static bool Needed(SpellTemplate spell, bool onOwnSide, bool onCaster) {
        foreach (var effect in Plain(spell) ?? spell?.m_effects?.Where(e => e is not null).ToList() ?? []) {
            var target = effect.m_effectTarget;
            if (target == kEffectTarget.kSelf && !onCaster || s_friend.Contains(target) && !onOwnSide || s_enemy.Contains(target) && onOwnSide) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The id of the game's spell to show for <paramref name="real"/> going on someone of the caster's side
    /// (<paramref name="onOwnSide"/>) or of the other side, or 0 when the game has none (the real one is shown then).
    /// </summary>
    public static uint Pick(IEnumerable<(SpellTemplate Spell, uint Id)> spells, SpellTemplate real, bool onOwnSide, bool onCaster) {
        var wanted = Plain(real);
        if (wanted is null) {
            return 0;
        }

        HashSet<kEffectTarget> side = onOwnSide ? onCaster ? [kEffectTarget.kFriendlySingle] : s_friend : s_enemy;
        string realBase = real.m_spellBase;
        var best = (Score: double.MaxValue, Id: 0u);
        foreach (var (spell, id) in spells) {
            var effects = Plain(spell);
            if (effects is null || effects.Count != wanted.Count || ReferenceEquals(spell, real)) {
                continue;
            }

            var score = 0.0;
            var fits = true;
            for (var i = 0; i < effects.Count && fits; i++) {
                var (e, w) = (effects[i], wanted[i]);
                fits = e.m_effectType == w.m_effectType
                       && string.Equals((string) e.m_sDamageType, (string) w.m_sDamageType, StringComparison.OrdinalIgnoreCase)
                       && Math.Sign(e.m_effectParam) == Math.Sign(w.m_effectParam)
                       && e.m_numRounds > 0 == w.m_numRounds > 0
                       && (Aimed(w.m_effectTarget) ? side.Contains(e.m_effectTarget) : e.m_effectTarget == w.m_effectTarget);
                score += Math.Abs(e.m_effectParam - w.m_effectParam) + 3 * Math.Abs(e.m_numRounds - w.m_numRounds);
            }

            if (!fits) {
                continue;
            }

            // Its own animation first, then a player's spell before a boss's or a test's.
            score -= string.Equals((string) spell.m_spellBase, realBase, StringComparison.OrdinalIgnoreCase) ? 1000 : 0;
            score += SpellLookalikes.IsCopy("", spell.m_name) || ((string) spell.m_name ?? "").StartsWith("BG_", StringComparison.Ordinal) ? 5 : 0;
            if (score < best.Score) {
                best = (score, id);
            }
        }

        return best.Id;
    }

}
