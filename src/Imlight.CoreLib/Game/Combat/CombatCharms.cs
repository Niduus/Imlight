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
 * COMBAT OFFENSIVE MODIFIER SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages damage and healing boosts (charms) that modify outgoing spell effects
 * based on magic school and effect type.
 * 
 * USAGE EXAMPLE:
 * var charms = CombatCharms.FindAppliedCharms(caster, effects);
 * int modifiedDamage = CombatCharms.GetOutgoingDamageFromCharms(charms, initialDamage);
 * 
 * NOTE:
 * Distinguishes between beneficial and harmful charms, ensuring proper
 * application order and stacking behavior for damage and healing modifiers.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using Imcodec.ObjectProperty.TypeCache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Provides utility methods for handling offensive combat modifiers (charms) in combat.
/// </summary>
/// <remarks>
/// Manages the selection and application of outgoing damage and healing modifiers, determining
/// which charms affect specific spells based on the caster, target, and spell school. Handles
/// both beneficial (damage/healing boosting) and harmful (damage reducing) charms.
/// </remarks>
internal static class CombatCharms {

    /// <summary>The effects that deal damage, which blades, weaknesses and damage auras change.</summary>
    internal static readonly HashSet<kSpellEffects> DamageEffects = [
        kSpellEffects.kDamage, kSpellEffects.kDamageNoCrit, kSpellEffects.kDamageOverTime, kSpellEffects.kDamagePerTotalPipPower,
        kSpellEffects.kDivideDamage, kSpellEffects.kStealHealth, kSpellEffects.kDeferredDamage, kSpellEffects.kMinionRankDamage,
    ];

    /// <summary>The effects that heal, which heal charms (Guiding Light, Infection) change.</summary>
    internal static readonly HashSet<kSpellEffects> HealEffects = [kSpellEffects.kHeal, kSpellEffects.kHealOverTime];

    /// <summary>
    /// The charms (and auras) on the caster that change <paramref name="effect"/>: for damage the blades and weaknesses
    /// of its school or of all schools, for a heal the heal charms. Only one of each spell counts, as in the game (two
    /// Fire Blades give one boost).
    /// </summary>
    /// <remarks>
    /// This used to look only at the last effect of the spell, missed heal over time (it checked damage over time
    /// instead), and kept one charm in all: charms carried no spell id, so "one of each spell" kept just the first one.
    /// </remarks>
    internal static List<SpellEffect> ForEffect(CombatDuelSubCircle caster, SpellEffect effect) {
        if (caster?._hangingEffects is not { } hanging || effect is null) {
            return [];
        }

        if (DamageEffects.Contains(effect.m_effectType)) {
            return OnePerSpell(hanging.Where(x => x is not null
                && x.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat
                && SchoolMatches(x.m_sDamageType, effect.m_sDamageType)));
        }

        if (HealEffects.Contains(effect.m_effectType)) {
            return OnePerSpell(hanging.Where(x => x is not null
                && x.m_effectType is kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingHealFlat));
        }

        return [];
    }

    /// <summary>The caster's pierce charms (spears) and pierce auras for a damage effect's school.</summary>
    internal static List<SpellEffect> PierceFor(CombatDuelSubCircle caster, SpellEffect effect) {
        if (caster?._hangingEffects is not { } hanging || effect is null || !DamageEffects.Contains(effect.m_effectType)) {
            return [];
        }

        return OnePerSpell(hanging.Where(x => x is not null && x.m_effectType == kSpellEffects.kModifyOutgoingArmorPiercing
                                              && SchoolMatches(x.m_sDamageType, effect.m_sDamageType)));
    }

    /// <summary>A charm or ward of <paramref name="modifierSchool"/> changes a spell of <paramref name="school"/>.</summary>
    internal static bool SchoolMatches(string modifierSchool, string school)
        => string.Equals(modifierSchool, "All", StringComparison.OrdinalIgnoreCase)
           || string.Equals(modifierSchool, school, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the first of each spell's modifiers. One with no spell id (from before ids were kept) counts as its own.
    /// </summary>
    internal static List<SpellEffect> OnePerSpell(IEnumerable<SpellEffect> effects) {
        var seen = new HashSet<uint>();
        var kept = new List<SpellEffect>();
        foreach (var effect in effects) {
            if (effect.m_spellTemplateID == 0 || seen.Add(effect.m_spellTemplateID)) {
                kept.Add(effect);
            }
        }

        return kept;
    }

    /// <summary>
    /// The outgoing damage once the charms are applied: each percent charm multiplies, then flat ones add.
    /// </summary>
    internal static int GetOutgoingDamageFromCharms(SpellEffect[] charms, int initialDamage = 0) {
        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamage)) {
            var damageChange = charm.m_effectParam / 100.0f;
            initialDamage = (int) Math.Floor(initialDamage * (1 + damageChange));
        }

        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamageFlat)) {
            initialDamage += charm.m_effectParam;
        }

        return Math.Max(0, initialDamage);
    }

    /// <summary>
    /// The outgoing heal once the heal charms are applied: percent ones multiply, then flat ones add.
    /// </summary>
    internal static int GetOutgoingHealFromCharms(SpellEffect[] charms, int heal) {
        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingHeal)) {
            heal = (int) Math.Floor(heal * (1 + charm.m_effectParam / 100.0f));
        }

        foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingHealFlat)) {
            heal += charm.m_effectParam;
        }

        return Math.Max(0, heal);
    }

    /// <summary>
    /// Every charm on the participant (not auras) of a disposition, newest first: what a removal or steal takes.
    /// </summary>
    /// <remarks>
    /// Removals used to ask for "the charms that change this participant's damage effects", which found nothing unless
    /// the participant had a damage over time on them, so Enfeeble, Earthquake's charm removal and charm steals did
    /// nothing.
    /// </remarks>
    internal static List<SpellEffect> All(CombatDuelSubCircle target, kHangingDisposition disposition = kHangingDisposition.kBoth)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => x is not null && CombatRequirements.IsCharm(x) && Disposed(x, disposition)).Reverse()]
            : [];

    internal static bool Disposed(SpellEffect effect, kHangingDisposition disposition) => disposition switch {
        kHangingDisposition.kBeneficial => CombatRequirements.IsBeneficial(effect),
        kHangingDisposition.kHarmful => !CombatRequirements.IsBeneficial(effect),
        _ => true,
    };

    /// <summary>
    /// Takes these exact effects off the participant (by reference, one entry each). Comparing by value took off
    /// every copy: two Tower Shields went at once when one was used.
    /// </summary>
    internal static void RemoveExact(List<SpellEffect> hanging, IEnumerable<SpellEffect> effects) {
        if (hanging is null) {
            return;
        }

        foreach (var effect in effects.ToList()) {
            var index = hanging.FindIndex(x => ReferenceEquals(x, effect));
            if (index >= 0) {
                hanging.RemoveAt(index);
            }
        }
    }

}
