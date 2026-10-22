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
 * COMBAT SPELL TARGETING SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Determines spell targets and processes the effects chain of combat actions,
 * bridging between the queued actions and their actual application to targets.
 * 
 * USAGE EXAMPLE:
 * bool spellWorthCasting = CombatActionResolver.ProcessedQueuedCombatAction(
 *     action, ref combatAction, ref cinematicTime);
 * 
 * NOTE:
 * Handles random spell selections, X-pip cost calculations, and manages
 * the effect stack for proper client synchronization.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Processes queued combat actions and determines their effects during spell resolution.
/// </summary>
/// <remarks>
/// Walks a spell's effects the way the game does: random effects roll one of their list, X-pip (variable) effects
/// pick the tier the pips pay for, effect lists apply every effect in them, conditional effects apply the first
/// element whose requirements hold, and target-count effects pick the list for how many enemies are standing. The
/// plain effects at the end go to CombatEffectApplicator.
/// </remarks>
internal static class CombatActionResolver {

    // One cast being resolved.
    private sealed class Cast {
        internal QueuedCombatAction Action;
        internal CombatAction CombatAction;
        internal CombatEffectStack Stack = new();
        internal float CinematicTime;
        internal float CritMultiplier = 1f;
        internal readonly List<int> CritSlots = [];
        // The charms this cast used, taken off the caster at the end (auras stay).
        internal readonly List<SpellEffect> UsedCharms = [];
        internal bool Worth;
        internal uint TemplateId;
        internal int DamageEffectCount;
        // Why parts of the spell did nothing, for the log.
        internal readonly List<string> Notes = [];
    }

    /// <summary>
    /// Processes a queued combat action and determines if it is worth casting.
    /// </summary>
    /// <param name="action">The queued combat action to process.</param>
    /// <param name="combatAction">The combat action to be modified.</param>
    /// <param name="cinematicTime">The cinematic time to be updated.</param>
    /// <param name="notes">Why parts of the spell did nothing (empty when everything went off).</param>
    /// <returns>True if the spell is worth casting, otherwise false.</returns>
    internal static bool ProcessedQueuedCombatAction(QueuedCombatAction action, ref CombatAction combatAction, ref float cinematicTime,
                                                     out string notes) {
        var cast = new Cast {
            Action = action,
            CombatAction = combatAction,
            TemplateId = action.Spell?.m_templateID ?? 0,
        };
        var topEffects = (action.SpellTemplate.m_effects ?? []).Where(e => e is not null).ToList();
        combatAction.m_xPipCost = GetXPipCost(action.Spell, action.SpellCaster);
        cast.DamageEffectCount = topEffects.Count(e => CombatEnchantments.Leaves([e]).Any(l => IsFlatAdjusted(l.m_effectType)));

        // One combined roll per cast: the target's block is part of the crit chance
        // (crit vs crit + K * block), so there is no separate block roll. The landed
        // multiplier varies by the crit/block ratio. Heals crit with no block side.
        var landedCrit = false;
        var critPrimaryTarget = action.SelectedTarget;
        if (critPrimaryTarget is not null) {
            var spellSchool = MagicSchools.GetMagicSchool(action.Spell.m_magicSchoolID)?.m_schoolName;
            if (spellSchool is not null) {
                var isHealCast = CombatEnchantments.Leaves(topEffects).Any(x => x.m_effectType == kSpellEffects.kHeal);
                var isOffensiveCast = critPrimaryTarget.OccupiedTeam != action.SpellCaster.OccupiedTeam;
                if (isHealCast || isOffensiveCast) {
                    landedCrit = CombatCriticals.RollsCritical(action.SpellCaster, critPrimaryTarget, spellSchool, isHealCast);
                    if (landedCrit) {
                        cast.CritMultiplier = CombatCriticals.GetCritMultiplier(action.SpellCaster, critPrimaryTarget, spellSchool, isHealCast);
                    }
                }
            }
        }

        foreach (var spellEffect in topEffects) {
            // A broken effect must not take the rest of the spell (or the round) with it: exceptions in the duel's
            // actor vanish, and the round then never plays.
            try {
                Resolve(spellEffect, cast, action.SelectedTarget, null);
            }
            catch (Exception e) {
                cast.Notes.Add($"{spellEffect.GetType().Name} {spellEffect.m_effectType} failed");
                Logger.Error("Spell {0} ({1}): effect {2} {3} failed: {4}",
                    Logger.Args(cast.TemplateId, (string) action.SpellTemplate.m_name, spellEffect.GetType().Name,
                        spellEffect.m_effectType, e));
            }
        }

        cinematicTime += cast.CinematicTime;

        if (cast.CritSlots.Count > 0) {
            combatAction.m_CritHitList = [.. cast.CritSlots.Select(slot => new TargetCritHit {
                m_target = slot,
                m_mult = landedCrit ? cast.CritMultiplier : 0f,
                m_blocked = false,
            })];

            if (landedCrit) {
                combatAction.m_spellHits = (char) 2;
            }
        }

        // The charms this spell used come off the caster, one entry each; an aura stays until its rounds run out.
        CombatCharms.RemoveExact(action.SpellCaster._hangingEffects, cast.UsedCharms.Where(x => !CombatRequirements.IsAura(x)));
        combatAction.m_effectChosen = cast.Stack.GetStackAsUint();
        combatAction.m_handledRandomSpellPerTarget = true;

        CheckForPolarCombatActionTargets(ref combatAction, action.SpellTemplate);

        notes = string.Join("; ", cast.Notes.Distinct());

        return cast.Worth;
    }

    private static bool IsFlatAdjusted(kSpellEffects type) => type is kSpellEffects.kDamage
        or kSpellEffects.kDamageOverTime or kSpellEffects.kDamageNoCrit or kSpellEffects.kDamagePerTotalPipPower
        or kSpellEffects.kHeal or kSpellEffects.kHealOverTime or kSpellEffects.kStealHealth;

    /// <summary>
    /// Resolves one effect of the spell. <paramref name="selected"/> is the target the effect works from (the card's
    /// target, or the participant a conditional was checked against); <paramref name="only"/> keeps a team-wide effect
    /// inside a conditional to the participant that met its requirements.
    /// </summary>
    private static void Resolve(SpellEffect effect, Cast cast, CombatDuelSubCircle selected, CombatDuelSubCircle only) {
        switch (effect) {
            case null:
                return;
            // An effect like Blade Dance's rolls its own effect for each target, not one for all of them.
            case RandomPerTargetSpellEffect perTarget when perTarget.m_effectList is { Count: > 0 }:
                ApplyRandomPerTargetEffect(perTarget, cast, selected, only);
                return;
            case RandomSpellEffect random:
                if (random.m_effectList is not { Count: > 0 }) {
                    cast.Notes.Add("random effect with nothing to pick from");
                    return;
                }

                Resolve(ChooseRandomEffect(random, cast.Stack), cast, selected, only);
                return;
            case VariableSpellEffect variable:
                if (variable.m_effectList is not { Count: > 0 }) {
                    cast.Notes.Add("X-pip effect with no tiers");
                    return;
                }

                Resolve(ChooseVariableEffect(variable, cast.CombatAction.m_xPipCost, cast.Stack), cast, selected, only);
                return;
            case ConditionalSpellEffect conditional:
                ResolveConditional(conditional, cast, selected, only);
                return;
            case TargetCountSpellEffect targetCount:
                ResolveTargetCount(targetCount, cast, selected, only);
                return;
            case HangingConversionSpellEffect:
                cast.Notes.Add("hanging conversion (kConvertHangingEffect) is not done by the server");
                return;
            case DelaySpellEffect:
                cast.Notes.Add("delay effect (DelaySpellEffect) is not done by the server");
                return;
            case CountBasedSpellEffect:
                cast.Notes.Add("count-based effect (CountBasedSpellEffect) is not done by the server");
                return;
            case ShadowPactSpellEffect:
                cast.Notes.Add("shadow pact is not done by the server");
                return;
        }

        // An afterlife waits on its target until they are defeated (CombatResolver.Afterlife.cs); its own effects
        // (Guardian Angel's heal is in its list) do not go off now.
        if (effect.m_effectType == kSpellEffects.kAfterlife) {
            var waitingOn = Restrict(GetEffectTargets(effect, cast.Action.SpellCaster, selected), effect, only);
            cast.Action.SpellCaster._duelActor.CombatResolver.AddAfterlife(waitingOn, effect);
            UpdateCombatActionTargets(ref cast.CombatAction, waitingOn);
            cast.Worth |= waitingOn.Length > 0;

            return;
        }

        // An effect list applies every effect in it. It used to apply one of them, picked by the pips paid (so 0 for
        // most spells): Age of Reckoning's critical, half of Arch Master and many more were lost.
        if (effect is EffectListSpellEffect list) {
            foreach (var inner in list.m_effectList ?? []) {
                Resolve(inner, cast, selected, only);
            }

            return;
        }

        ApplyPlain(effect, cast, selected, only);
    }

    /// <summary>
    /// A conditional effect: for each participant it is checked against, the first element whose requirements hold
    /// goes off (as "if, else if"). Nothing read conditionals before, so spells built on them (Chromatic Shield,
    /// Arch Master, Fusion spells) cast with no effect on the server while the game showed one.
    /// </summary>
    private static void ResolveConditional(ConditionalSpellEffect conditional, Cast cast, CombatDuelSubCircle selected,
                                           CombatDuelSubCircle only) {
        var caster = cast.Action.SpellCaster;
        var elements = (conditional.m_elements ?? []).Where(e => e?.m_pEffect is not null).ToList();
        if (elements.Count == 0) {
            cast.Notes.Add("conditional effect with no elements");
            return;
        }

        // A team-wide conditional checks each participant on its own; a single one checks the card's target.
        var checkedOn = IsGroupTarget(conditional.m_effectTarget)
            ? Restrict(GetEffectTargets(conditional, caster, selected), conditional, only)
            : [conditional.m_effectTarget == kEffectTarget.kSelf ? caster : selected ?? caster];

        foreach (var target in checkedOn) {
            var element = elements.FirstOrDefault(e => CombatRequirements.Met(e.m_pReqs, caster, target));
            if (element is null) {
                cast.Notes.Add($"no condition met on slot {target?.SlotIndex}");
                continue;
            }

            Resolve(element.m_pEffect, cast, target, IsGroupTarget(conditional.m_effectTarget) ? target : only);
        }
    }

    /// <summary>
    /// A target-count effect (Qismah's Curse and the like): its lists are for 1, 2, 3... enemies standing, the more
    /// enemies the less each takes. It was not read before, so the spell did nothing ("not worth casting").
    /// </summary>
    private static void ResolveTargetCount(TargetCountSpellEffect targetCount, Cast cast, CombatDuelSubCircle selected,
                                           CombatDuelSubCircle only) {
        var lists = (targetCount.m_effectLists ?? []).Where(l => l is not null).ToList();
        if (lists.Count == 0) {
            cast.Notes.Add("target-count effect with no lists");
            return;
        }

        var standing = Restrict(GetEffectTargets(targetCount, cast.Action.SpellCaster, selected), targetCount, only)
            .Count(t => t.IsAlive);
        if (standing == 0) {
            cast.Notes.Add("target-count effect found no target");
            return;
        }

        Resolve(lists[Math.Clamp(standing, 1, lists.Count) - 1], cast, selected, only);
    }

    private static bool IsGroupTarget(kEffectTarget target) => target is kEffectTarget.kEnemyTeam
        or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce
        or kEffectTarget.kMultiTargetEnemy or kEffectTarget.kMultiTargetFriendly;

    // Inside a team-wide conditional, a team-wide effect goes only on the participant the condition held for.
    private static CombatDuelSubCircle[] Restrict(CombatDuelSubCircle[] targets, SpellEffect effect, CombatDuelSubCircle only)
        => only is not null && IsGroupTarget(effect.m_effectTarget) ? [.. targets.Where(t => ReferenceEquals(t, only))] : targets;

    // A plain effect: damage, a heal, a charm, a ward and so on.
    private static void ApplyPlain(SpellEffect effect, Cast cast, CombatDuelSubCircle selected, CombatDuelSubCircle only) {
        var action = cast.Action;
        var caster = action.SpellCaster;
        var chosenEffect = effect;

        // If the spell is enchanted with a flat adjustment (+X amount), apply it to damage/heal effects
        if (action.Spell is { m_regularAdjust: not 0 }) {
            // todo: find out when those enchants are applied.

            var isDamageOrHeal = IsFlatAdjusted(chosenEffect.m_effectType)
                                 || chosenEffect.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyIncomingDamage;

            if (isDamageOrHeal) {
                int adjust;
                if (cast.DamageEffectCount > 1) {
                    if (chosenEffect.m_effectType is kSpellEffects.kDamage or kSpellEffects.kDamageNoCrit or kSpellEffects.kHeal) {
                        adjust = (int) Math.Round(action.Spell.m_regularAdjust / 3.0);
                    }
                    else {
                        var initialPart = (int) Math.Round(action.Spell.m_regularAdjust / 3.0);
                        adjust = action.Spell.m_regularAdjust - initialPart;
                    }
                }
                else {
                    adjust = action.Spell.m_regularAdjust;
                }

                chosenEffect = chosenEffect with {
                    m_effectParam = chosenEffect.m_effectParam + adjust
                };
            }
        }

        // A detonate's strength is in its name ("Detonate All 200" deals 200%), unless the effect carries it.
        if (chosenEffect.m_effectType == kSpellEffects.kDetonateOverTime && chosenEffect.m_healModifier <= 0) {
            var percent = System.Text.RegularExpressions.Regex.Match((string) action.SpellTemplate.m_name ?? "", @"\d+");
            chosenEffect = chosenEffect with {
                m_healModifier = percent.Success && int.TryParse(percent.Value, out var p) && p > 0 ? p / 100f : 1f
            };
        }

        // An enchantment's card change: it never goes on a participant.
        if (chosenEffect.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells) {
            cast.Notes.Add($"{chosenEffect.m_effectType} changes a card, not a participant");
            return;
        }

        // A global (a bubble) has no targets: it goes on the battlefield. It used to stop at the empty target list
        // below, so the card was thrown away as "not worth casting" (Paul's Death Rampage, Niduus, 2026-10-22).
        if (chosenEffect.m_effectTarget == kEffectTarget.kGlobal) {
            cast.Worth = true;
            cast.CinematicTime += CombatEffectApplicator.ApplyEffect(chosenEffect, [], caster, [], cast.CritMultiplier, cast.TemplateId);

            return;
        }

        var targets = Restrict(GetEffectTargets(chosenEffect, caster, selected), chosenEffect, only);
        if (targets.Length == 0) {
            cast.Notes.Add($"{chosenEffect.m_effectType} for {chosenEffect.m_effectTarget} found no target");
            return;
        }

        // If the spell has any targets that are alive or on the same team as the caster, it's worth casting.
        if (targets.Any(x => x.IsAlive) || targets.Any(x => x.OccupiedTeam == caster.OccupiedTeam)) {
            cast.Worth = true;
        }

        // Inform each of the targets of this spell that they've been targeted by this effect.
        UpdateCombatActionTargets(ref cast.CombatAction, targets);
        InformDuelParticipantsOfEffect(caster, targets, chosenEffect);

        // A lasting polymorph on a creature (CombatPolymorph.cs): the game swaps the model from the cast itself.
        if (chosenEffect.m_effectType == kSpellEffects.kPolymorph) {
            foreach (var target in targets) {
                CombatPolymorph.Apply(target, chosenEffect, action.Spell.m_templateID);
            }
        }

        // The effect types CombatEffectApplicator applies critMultiplier to.
        if (chosenEffect.m_effectType is kSpellEffects.kDamage or kSpellEffects.kDamagePerTotalPipPower or kSpellEffects.kHeal
                                      or kSpellEffects.kStealHealth or kSpellEffects.kDivideDamage) {
            foreach (var target in targets) {
                if (!cast.CritSlots.Contains(target.SlotIndex)) {
                    cast.CritSlots.Add(target.SlotIndex);
                }
            }
        }

        // The caster's charms for this effect (blades for damage of its school, heal charms for a heal), and spears.
        // A spell with ignore charms (m_ignoreCharms) takes none.
        var charms = action.SpellTemplate.m_ignoreCharms
            ? []
            : CombatCharms.ForEffect(caster, chosenEffect).Concat(CombatCharms.PierceFor(caster, chosenEffect)).ToList();
        foreach (var charm in charms.Where(c => !cast.UsedCharms.Any(u => ReferenceEquals(u, c)))) {
            cast.UsedCharms.Add(charm);
        }

        cast.CinematicTime += CombatEffectApplicator.ApplyEffect(chosenEffect, [.. charms], caster, targets, cast.CritMultiplier,
                                                                cast.TemplateId);
    }

    /// <summary>
    /// Applies a random-per-target effect: each target the effect reaches gets its own roll from the effect list,
    /// and the rolls go to the client in target order so it shows the same choices.
    /// </summary>
    private static void ApplyRandomPerTargetEffect(RandomPerTargetSpellEffect perTarget, Cast cast, CombatDuelSubCircle selected,
                                                   CombatDuelSubCircle only) {
        var caster = cast.Action.SpellCaster;
        var targets = Restrict(GetEffectTargets(perTarget, caster, selected), perTarget, only)
            .Where(target => target is not null && target.IsAlive)
            .ToArray();
        if (targets.Length == 0) {
            cast.Notes.Add("random-per-target effect found no target");
            return;
        }

        cast.CombatAction.m_randomSpellEffectPerTargetRolls ??= [];
        UpdateCombatActionTargets(ref cast.CombatAction, targets);
        cast.Worth = true;

        foreach (var target in targets) {
            var roll = Random.Shared.Next(0, perTarget.m_effectList.Count);
            cast.CombatAction.m_randomSpellEffectPerTargetRolls.Add(roll);

            var chosenEffect = perTarget.m_effectList[roll];
            if (chosenEffect is null) {
                continue;
            }

            InformDuelParticipantsOfEffect(caster, [target], chosenEffect);
            cast.CinematicTime += CombatEffectApplicator.ApplyEffect(chosenEffect, [], caster, [target], 1f, cast.TemplateId);
        }
    }

    private static SpellEffect ChooseRandomEffect(RandomSpellEffect randomSpellEffect, CombatEffectStack effectStack) {
        var count = randomSpellEffect.m_effectList.Count;
        var randomEffectIndex = Random.Shared.Next(0, count);
        var chosenEffect = randomSpellEffect.m_effectList[randomEffectIndex];

        effectStack.PushRandomEffectChoice(randomEffectIndex);

        return chosenEffect;
    }

    private static SpellEffect ChooseVariableEffect(VariableSpellEffect variableSpellEffect, int parameter, CombatEffectStack effectStack) {
        // X-pip spells carry one nested effect per pip tier, tagged by m_pipNum (1-based); resolve
        // the highest tier the pips paid can afford, for any list length.
        var effects = variableSpellEffect.m_effectList;
        var pipsPaid = Math.Max(parameter, 0);
        var chosenEffect = effects
            .Where(e => e is not null && e.m_pipNum > 0 && e.m_pipNum <= pipsPaid)
            .OrderByDescending(e => e.m_pipNum)
            .FirstOrDefault();
        if (chosenEffect is null) {
            // Defensive: no matching tier, clamp by index so a mistagged spell still resolves.
            var idx = Math.Clamp(pipsPaid == 0 ? 0 : pipsPaid - 1, 0, effects.Count - 1);
            chosenEffect = effects[idx];
        }

        // Tell the client which tier resolved; it re-simulates the spell.
        effectStack.PushRandomEffectChoice(effects.IndexOf(chosenEffect));

        return chosenEffect;
    }

    // X-pip spells are detected structurally: the template carries a VariableSpellEffect.
    internal static bool IsXPipSpell(Spell spell) {
        var template = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;

        return template?.m_effects?.Any(e => e is VariableSpellEffect) == true;
    }

    internal static byte GetXPipCost(Spell spell, CombatDuelSubCircle caster) {
        if (!IsXPipSpell(spell)) {
            return 0;
        }

        var pipCount = caster.CombatParticipant.m_pipCount;

        // All pips, capped at the spell's top tier (a minion stops at 4).
        var totalPips = (int) pipCount.m_genericPips;
        var isSpellMastered = caster.HasSchoolMastery(spell.m_magicSchoolID);
        totalPips += isSpellMastered ? pipCount.m_powerPips * 2 : pipCount.m_powerPips;

        return (byte) Math.Min(totalPips, GetMaxXPipTier(spell));
    }

    // Highest pip tier (m_pipNum) an X-pip spell offers; int.MaxValue when there is none.
    private static int GetMaxXPipTier(Spell spell) {
        var template = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;
        var variable = template?.m_effects?.OfType<VariableSpellEffect>().FirstOrDefault();
        if (variable?.m_effectList is null || variable.m_effectList.Count == 0) {
            return int.MaxValue;
        }

        return variable.m_effectList.Max(e => (int) (e?.m_pipNum ?? 0));
    }

    private static void UpdateCombatActionTargets(ref CombatAction combatAction, IEnumerable<CombatDuelSubCircle> targets) {
        foreach (var target in targets) {
            if (!combatAction.m_targetSubcircleList.Contains(target.SlotIndex)) {
                combatAction.m_targetSubcircleList.Add(target.SlotIndex);
            }
        }
    }

    private static void CheckForPolarCombatActionTargets(ref CombatAction combatAction, SpellTemplate spellTemplate) {
        // Spells that target both the caster and target(s) should be treated as targeting the target(s) only.
        // If the caster is left as a target, they will receive the benefit/curse from the spell twice.
        var casterIndex = combatAction.m_spellCaster;
        if (combatAction.m_targetSubcircleList.Count > 1
            && spellTemplate.m_effects.Any(x => x?.m_effectTarget == kEffectTarget.kSelf)) {
            combatAction.m_targetSubcircleList.Remove(casterIndex);
        }
    }

    private static void InformDuelParticipantsOfEffect(CombatDuelSubCircle caster, CombatDuelSubCircle[] targets, SpellEffect effect) {
        var allParticipants = caster._duelActor.ActiveSubCircles.Select(x => x.ParticipantActor).ToArray();
        var msg = new COMBAT_106_PROTOCOL.MSG_COMBATEFFECT {
            Caster = caster,
            Targets = targets,
            Effect = effect,
        };

        foreach (var participant in allParticipants) {
            participant?.Tell(msg, null);
        }
    }

    // The caster's own minion, if one is standing.
    private static CombatDuelSubCircle CasterMinion(CombatDuelSubCircle caster)
        => caster._duelActor.ActiveSubCircles.FirstOrDefault(c => c.IsSummonedMinion && c.IsAlive && c != caster
                                                                  && c.CombatParticipant?.m_minionSubCircle == caster.SlotIndex);

    private static CombatDuelSubCircle[] GetEffectTargets(SpellEffect effect, CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        var targets = Array.Empty<CombatDuelSubCircle>();
        var _activeSubCircles = caster._duelActor.ActiveSubCircles;
        var enemies = _activeSubCircles.Where(x => x.OccupiedTeam != caster.OccupiedTeam && x.IsAlive && x.AddedToDuel).ToArray();
        var friends = _activeSubCircles.Where(x => x.OccupiedTeam == caster.OccupiedTeam && x.IsAlive && x.AddedToDuel).ToArray();

        switch (effect.m_effectTarget) {
            case kEffectTarget.kEnemySingle:
            case kEffectTarget.kFriendlySingle:
                targets = target is null ? [] : [target];
                break;
            // A single friend who is not the caster (Donate Power, some Pacify and Confuse spells). It had no case, so
            // these spells never had a target and were thrown away as "not worth casting".
            case kEffectTarget.kFriendlySingleNotMe:
                targets = target is not null && target != caster && target.OccupiedTeam == caster.OccupiedTeam ? [target] : [];
                break;
            // The card's enemy, or else the first enemy standing (Qismah's Curse tiers, monster AI picks).
            case kEffectTarget.kPreselectedEnemySingle:
            case kEffectTarget.kAtLeastOneEnemy:
                targets = target is not null && target.OccupiedTeam != caster.OccupiedTeam && target.IsAlive
                    ? [target]
                    : [.. enemies.Take(1)];
                break;
            // Every enemy (or friend) standing: the Fusion spells, Qismah's Curse, Scorching Scimitars and the like.
            // These had no case either, so the spells did nothing.
            case kEffectTarget.kMultiTargetEnemy:
                targets = enemies;
                break;
            case kEffectTarget.kMultiTargetFriendly:
                targets = friends;
                break;
            case kEffectTarget.kMinion:
            case kEffectTarget.kFriendlyMinion:
                // The client drives minion selection; validate the pick belongs to the caster's team, or else use the
                // caster's own minion (Drain Health on a card aimed at the caster).
                targets = target is not null && target.IsSummonedMinion && target.OccupiedTeam == caster.OccupiedTeam
                    ? [target]
                    : CasterMinion(caster) is { } own ? [own] : [];
                break;
            // The caster's own minion, whatever the card was aimed at (it used to need the card on the minion).
            case kEffectTarget.kCasterMinion:
                targets = CasterMinion(caster) is { } mine ? [mine] : [];
                break;
            case kEffectTarget.kEnemyMinion:
                targets = target is not null && target.IsSummonedMinion && target.OccupiedTeam != caster.OccupiedTeam
                    ? [target]
                    : [];
                break;
            case kEffectTarget.kTargetMinion:
                targets = target is not null && target.IsSummonedMinion
                    ? [target]
                    : [];
                break;
            case kEffectTarget.kSelf:
            case kEffectTarget.kInvalidTarget:
                targets = [caster];
                break;
            // A whole-team spell leaves out the defeated (Niduus, 2026-10-22: Malistaire's Roar still listed Paul after he
            // was downed, and the game played its hit on him, so he stood up again on his circle while his nameplate
            // said DEFEATED). Only an afterlife (a revive) goes on a defeated teammate.
            case kEffectTarget.kFriendlyTeam:
            case kEffectTarget.kFriendlyTeamAllAtOnce:
                targets = [.. _activeSubCircles.Where(x => x.OccupiedTeam == caster.OccupiedTeam
                                                           && (x.IsAlive || effect.m_effectType == kSpellEffects.kAfterlife))];
                break;
            case kEffectTarget.kEnemyTeam:
            case kEffectTarget.kEnemyTeamAllAtOnce:
                targets = [.. _activeSubCircles.Where(x => x.OccupiedTeam != caster.OccupiedTeam && x.IsAlive)];
                break;
            case kEffectTarget.kGlobal:
            case kEffectTarget.kSpell:
            case kEffectTarget.kSpecificSpells:
                return [];
        }

        return [.. targets.Where(t => t is not null)];
    }

}
