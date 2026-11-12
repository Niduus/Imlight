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
 * SPELL EFFECT FILTERING SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Provides static utility methods for filtering spells based on their effect types
 * and targeting characteristics.
 * 
 * USAGE EXAMPLE:
 * var damageSpells = SpellEffectFilter.FilterSpellsByOutgoingDamage(spellList);
 * var healingSpells = SpellEffectFilter.FilterSpellsByHealing(spellList);
 * 
 * NOTE:
 * Supports filtering for damage, healing, buffs, and debuffs.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Provides static methods for filtering spells based on their effect characteristics.
/// </summary>
internal static class SpellEffectFilter {

    /// <summary>
    /// Filters the given list of spells by their effects 
    /// and returns a new list of spells that deal damage to the enemy team.
    /// </summary>
    /// <param name="spells">The list of spells to filter.</param>
    /// <returns>A new list of spells that deal damage to the enemy team.</returns>
    internal static List<Spell> FilterSpellsByOutgoingDamage(List<Spell> spells) {
        var filteredSpells = new List<Spell>();
        foreach (var spell in spells) {
            // The spell type doesn't carry spell effects. That instead comes from the spell template.
            var spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
            if (spellTemplate == null) {
                continue;
            }

            // Filter spells that deal damage to the enemy team. The damage of an X pip spell (Judgement, Heck Hound) sits
            // inside its VariableSpellEffect, whose own target is not an enemy, so the inner effects are looked at too.
            if (Lands(spellTemplate, e => IsEnemyTarget(e) && IsDamageEffect(e))) {
                filteredSpells.Add(spell);
            }
        }

        return filteredSpells;
    }

    /// <summary>
    /// Filters the given list of spells by their effects
    /// and returns a new list of spells that heal the caster or their team.
    /// </summary>
    /// <param name="spells">The list of spells to filter.</param>
    /// <returns>A new list of spells that heal the caster or their team.</returns>
    internal static List<Spell> FilterSpellsByHealing(List<Spell> spells) {
        var filteredSpells = new List<Spell>();
        foreach (var spell in spells) {
            // The spell type doesn't carry spell effects. That instead comes from the spell template.
            var spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
            if (spellTemplate == null) {
                continue;
            }

            // Filter spells that heal the caster or their team.
            if (Lands(spellTemplate, IsHealingEffect)) {
                filteredSpells.Add(spell);
            }
        }

        return filteredSpells;
    }

    /// <summary>
    /// Filters the given list of spells by their effects
    /// and returns a new list of spells that buff the caster or their team.
    /// </summary>
    /// <param name="spells">The list of spells to filter.</param>
    /// <returns>A new list of spells that buff the caster or their team.</returns>
    internal static List<Spell> FilterSpellsByBuff(List<Spell> spells) {
        var filteredSpells = new List<Spell>();
        foreach (var spell in spells) {
            // The spell type doesn't carry spell effects. That instead comes from the spell template.
            var spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
            if (spellTemplate == null) {
                continue;
            }

            // Filter spells that buff the caster or their team.
            if (Lands(spellTemplate, e => IsBuffEffect(e) && IsFriendlyTarget(e))) {
                filteredSpells.Add(spell);
            }
        }

        return filteredSpells;
    }

    /// <summary>
    /// Filters the given list of spells by their effects
    /// and returns a new list of spells that debuff the enemy team.
    /// </summary>
    /// <param name="spells">The list of spells to filter.</param>
    /// <returns>A new list of spells that debuff the enemy team.</returns>
    internal static List<Spell> FilterSpellsByDebuff(List<Spell> spells) {
        var filteredSpells = new List<Spell>();
        foreach (var spell in spells) {
            // The spell type doesn't carry spell effects. That instead comes from the spell template.
            var spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
            if (spellTemplate == null) {
                continue;
            }

            // Filter spells that debuff the enemy team (an X pip trap such as Backdraft keeps it inside).
            if (Lands(spellTemplate, e => IsBuffEffect(e) && IsEnemyTarget(e))) {
                filteredSpells.Add(spell);
            }
        }

        return filteredSpells;
    }

    /// <summary>
    /// Whether any of the spell's effects lands on the other side.
    /// </summary>
    /// The inner effects count too: an X pip spell (Judgement, Heck Hound, Backdraft) keeps its enemy part inside a
    /// VariableSpellEffect whose own target is not an enemy, so monsters aimed those cards at themselves (a player,
    /// 2026-11-12: "Creatures are systematically sending X per pip spells on themselves").
    internal static bool TargetsEnemy(Spell spell)
        => CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate template
        && Lands(template, IsEnemyTarget);

    // Whether a top effect, or one inside it (random, X pip tier, list, count based, conditional), matches. A spell used
    // to be listed once for each matching effect, which made a card with two hits twice as likely in a random pick.
    private static bool Lands(SpellTemplate template, System.Func<SpellEffect, bool> match) {
        var top = (template.m_effects ?? []).Where(e => e is not null).ToList();

        return top.Any(match) || CombatEnchantments.Leaves(top).Any(e => e is not null && match(e));
    }

    private static bool IsEnemyTarget(SpellEffect effect)
        => effect.m_effectTarget is kEffectTarget.kAtLeastOneEnemy
                 or kEffectTarget.kEnemyTeam
                 or kEffectTarget.kEnemyTeamAllAtOnce
                 or kEffectTarget.kEnemySingle
                 or kEffectTarget.kMultiTargetEnemy
                 or kEffectTarget.kAtLeastOneEnemy
                 or kEffectTarget.kPreselectedEnemySingle;

    private static bool IsFriendlyTarget(SpellEffect effect)
        => effect.m_effectTarget is kEffectTarget.kSelf
                 or kEffectTarget.kFriendlyMinion
                 or kEffectTarget.kFriendlyTeam
                 or kEffectTarget.kFriendlyTeamAllAtOnce
                 or kEffectTarget.kFriendlySingle
                 or kEffectTarget.kMultiTargetFriendly
                 or kEffectTarget.kFriendlySingleNotMe;

    private static bool IsDamageEffect(SpellEffect effect) {
        var t = effect.m_effectType;

        // If this is a random damage effect, enter the internal effect list of that and see
        // if any of them deal damage.
        if (effect is RandomSpellEffect rse) {
            var isRandomDamage = false;
            foreach (var e in rse.m_effectList) {
                if (IsDamageEffect(e)) {
                    isRandomDamage = true;
                    break;
                }
            }

            return isRandomDamage;
        }

        return t is kSpellEffects.kDamage
                 or kSpellEffects.kDamageNoCrit
                 or kSpellEffects.kDamageOverTime
                 or kSpellEffects.kDamagePerTotalPipPower;
    }

    private static bool IsHealingEffect(SpellEffect effect) =>
        effect.m_effectType is kSpellEffects.kHeal
                 or kSpellEffects.kHealOverTime
                 or kSpellEffects.kHealByWard
                 or kSpellEffects.kHealPercent
                 or kSpellEffects.kMaxHealthHeal
                 or kSpellEffects.kSetHealPercent;

    private static bool IsBuffEffect(SpellEffect effect) =>
        effect.m_effectType is kSpellEffects.kModifyIncomingDamage
                 or kSpellEffects.kModifyIncomingDamageFlat
                 or kSpellEffects.kModifyIncomingDamageOverTime
                 or kSpellEffects.kModifyIncomingHeal
                 or kSpellEffects.kModifyIncomingHealFlat
                 or kSpellEffects.kModifyIncomingHealOverTime
                 or kSpellEffects.kModifyIncomingArmorPiercing
                 or kSpellEffects.kModifyOutgoingDamage
                 or kSpellEffects.kModifyOutgoingDamageFlat
                 or kSpellEffects.kModifyOutgoingHeal
                 or kSpellEffects.kModifyOutgoingHealFlat
                 or kSpellEffects.kModifyOutgoingArmorPiercing
                 or kSpellEffects.kModifyAccuracy
                 or kSpellEffects.kAbsorbDamage
                 or kSpellEffects.kStunBlock
                 or kSpellEffects.kStunResist;

}
