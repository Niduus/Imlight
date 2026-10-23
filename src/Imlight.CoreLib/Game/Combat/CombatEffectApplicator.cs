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
 * COMBAT EFFECT APPLICATION SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Applies spell effects to targets with appropriate stat-based modifications,
 * handling damage calculation, healing, and status effects.
 * 
 * USAGE EXAMPLE:
 * float cinematicTime = CombatEffectApplicator.ApplyEffect(
 *     effect, charms, caster, targets);
 * 
 * NOTE:
 * 
 * TODO:
 * - Implement correct damage percent max as a limit function
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/14/2026
 */

using Imcodec.ObjectProperty.TypeCache;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Applies the effects of spells to targets in combat, handling damage, healing, and status effects.
/// </summary>
/// <remarks>
/// This static utility class processes all spell effect types by applying appropriate modifiers based on
/// caster and target stats, hanging effects, and global duel modifiers. It handles complex calculations
/// for damage scaling, resistances, healing, and various utility effects like stuns, steals, and wards.
/// </remarks>
internal static class CombatEffectApplicator {

    private const float HANGING_EFFECT_CONSUME_TIME = 1.0f;

    // Charms, wards, auras and other effects that sit on a participant until a spell uses or removes them.
    private static readonly HashSet<kSpellEffects> s_hangingEffects = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyIncomingDamage, kSpellEffects.kDispel,
        kSpellEffects.kModifyAccuracy, kSpellEffects.kModifyOutgoingHeal, kSpellEffects.kModifyOutgoingHealFlat,
        kSpellEffects.kModifyIncomingHeal, kSpellEffects.kModifyIncomingHealFlat, kSpellEffects.kModifyIncomingDamageType,
        kSpellEffects.kAbsorbDamage, kSpellEffects.kCritBoost, kSpellEffects.kCritBlock,
        // Before this audit these were dropped, so spears, Stun Block, flat blades and traps, Equalize-style caps,
        // pierce auras and the like did nothing on the server.
        kSpellEffects.kModifyOutgoingArmorPiercing, kSpellEffects.kModifyIncomingArmorPiercing,
        kSpellEffects.kModifyOutgoingDamageFlat, kSpellEffects.kModifyIncomingDamageFlat, kSpellEffects.kStunBlock,
        kSpellEffects.kMaximumIncomingDamage, kSpellEffects.kModifyPowerPipChance, kSpellEffects.kPipConversion,
        kSpellEffects.kStunResist, kSpellEffects.kDispelBlock, kSpellEffects.kConfusionBlock,
        kSpellEffects.kModifyIncomingDamageOverTime, kSpellEffects.kModifyIncomingHealOverTime,
        kSpellEffects.kModifyOutgoingStealHealth, kSpellEffects.kModifyIncomingStealHealth,
        kSpellEffects.kCloakedCharm, kSpellEffects.kCloakedWard, kSpellEffects.kCloakedWardNoRemove,
    ];

    // Effect types that are handled elsewhere (the duel, the AI, the enchanting of a card) and need nothing here.
    private static readonly HashSet<kSpellEffects> s_handledElsewhere = [
        kSpellEffects.kPacify, kSpellEffects.kTaunt, kSpellEffects.kModifyHate, kSpellEffects.kPolymorph,
        kSpellEffects.kAfterlife, kSpellEffects.kInvalidSpellEffect,
        kSpellEffects.kModifyCardCloak, kSpellEffects.kModifyCardDamage, kSpellEffects.kModifyCardAccuracy,
        kSpellEffects.kModifyCardMutation, kSpellEffects.kModifyCardRank, kSpellEffects.kModifyCardArmorPiercing,
        kSpellEffects.kModifyCardHeal, kSpellEffects.kModifyCardCharm, kSpellEffects.kModifyCardWard,
        kSpellEffects.kModifyCardOutgoingDamage, kSpellEffects.kModifyCardOutgoingAccuracy, kSpellEffects.kModifyCardOutgoingHeal,
        kSpellEffects.kModifyCardOutgoingArmorPiercing, kSpellEffects.kModifyCardIncomingDamage,
        kSpellEffects.kModifyCardAbsorbDamage, kSpellEffects.kModifyCardDamagebyRank,
        kSpellEffects.kProtectCardBeneficial, kSpellEffects.kProtectCardHarmful,
    ];

    /// <summary>
    /// Applies the specified spell effect to the targets, modifying their stats and applying damage or healing as necessary.
    /// </summary>
    /// <param name="effect">The spell effect to apply.</param>
    /// <param name="charms">The charms on the caster that change this effect (from CombatCharms.ForEffect/PierceFor).</param>
    /// <param name="caster">The caster of the spell.</param>
    /// <param name="targets">The targets of the spell effect.</param>
    /// <param name="spellTemplateId">The spell cast, kept on the charms and wards it leaves so "one of each spell" works.</param>
    /// <returns>The cinematic time required for the effect to be applied.</returns>
    internal static float ApplyEffect(SpellEffect effect,
                                      SpellEffect[] charms,
                                      CombatDuelSubCircle caster,
                                      CombatDuelSubCircle[] targets,
                                      float critMultiplier = 1,
                                      uint spellTemplateId = 0) {
        var cinematicTime = 0.0f;
        charms ??= [];
        targets ??= [];

        if (effect.m_effectTarget == kEffectTarget.kGlobal) {
            ApplyGlobalEffect(effect, caster._duelActor.Duel, spellTemplateId);

            return cinematicTime;
        }

        if (s_hangingEffects.Contains(effect.m_effectType)) {
            ApplyHangingEffect(effect, targets, spellTemplateId);

            return cinematicTime;
        }

        switch (effect.m_effectType) {
            case kSpellEffects.kDamage:
            case kSpellEffects.kDamagePerTotalPipPower:
                cinematicTime += ApplyFlatDamageEffect(effect, charms, caster, targets, critMultiplier);
                break;
            // No critical (Dark Pact, Voodoo and the like).
            case kSpellEffects.kDamageNoCrit:
                cinematicTime += ApplyFlatDamageEffect(effect, charms, caster, targets);
                break;
            // The damage is shared out among the targets (Qismah's Curse, Scorching Scimitars).
            case kSpellEffects.kDivideDamage: {
                var hit = targets.Where(t => t is not null && t.IsAlive).ToArray();
                if (hit.Length > 0) {
                    var share = effect with { m_effectParam = effect.m_effectParam / hit.Length };
                    cinematicTime += ApplyFlatDamageEffect(share, charms, caster, hit, critMultiplier);
                }

                break;
            }
            case kSpellEffects.kDamageOverTime:
                cinematicTime += ApplyDamageOverTime(effect, charms, caster, targets, spellTemplateId);
                break;
            // A bomb: its damage goes off in one hit once its rounds run out (Fire Elf PA, Timebomb).
            case kSpellEffects.kDeferredDamage:
                cinematicTime += ApplyDamageOverTime(effect, charms, caster, targets, spellTemplateId, deferred: true);
                break;
            case kSpellEffects.kHeal:
                cinematicTime += ApplyFlatHealEffect(effect, charms, caster, targets, critMultiplier);
                break;
            case kSpellEffects.kHealOverTime:
                cinematicTime += ApplyHealOverTime(effect, charms, caster, targets, spellTemplateId);
                break;
            // Heals by a share of the target's maximum health (m_effectParam percent).
            case kSpellEffects.kHealPercent:
            case kSpellEffects.kMaxHealthHeal:
                foreach (var target in targets.Where(t => t?.ParticipantGameStats is not null)) {
                    DoHealToTarget(target, (int) Math.Ceiling(target.ParticipantGameStats.m_baseHitpoints * effect.m_effectParam / 100.0));
                }

                break;
            // Brings the target's health up to m_effectParam percent of its maximum (Blessing).
            case kSpellEffects.kSetHealPercent:
                foreach (var target in targets.Where(t => t?.ParticipantGameStats is not null && t.IsAlive)) {
                    var wanted = (int) Math.Ceiling(target.ParticipantGameStats.m_baseHitpoints * effect.m_effectParam / 100.0);
                    var missing = wanted - target.ParticipantGameStats.m_currentHitpoints;
                    if (missing > 0) {
                        target.HealParticipant(missing);
                    }
                }

                break;
            case kSpellEffects.kStealHealth:
                cinematicTime += ApplyStealHealthEffect(effect, charms, caster, targets, critMultiplier);
                break;
            case kSpellEffects.kStun:
                cinematicTime += ApplyStunEffect(effect, targets);
                break;
            case kSpellEffects.kSummonCreature:
            case kSpellEffects.kSpawnCreature:
                ApplySummonCreature(effect, caster);
                break;
            case kSpellEffects.kInstantKill:
            case kSpellEffects.kKillCreature:
                cinematicTime += ApplyKillCreatureEffect(effect, targets);
                break;
            case kSpellEffects.kModifyPips:
                ApplyModifyPipsEffect(effect, targets);
                break;
            case kSpellEffects.kModifyPowerPips:
                foreach (var target in targets.Where(t => t is not null && t.IsAlive)) {
                    target.ChangePips(null, effect.m_effectParam);
                }

                break;
            case kSpellEffects.kModifyShadowPips:
                foreach (var target in targets.Where(t => t is not null && t.IsAlive)) {
                    target.ChangePips(null, effect.m_effectParam, shadow: true);
                }

                break;
            // A school pip of the effect's school (Arch Master, Empower - School Pip).
            case kSpellEffects.kModifySchoolPips:
                if (Enum.TryParse<MagicSchool>((string) effect.m_sDamageType, true, out var pipSchool)) {
                    foreach (var target in targets.Where(t => t is not null && t.IsAlive)) {
                        target.ChangePips(pipSchool, effect.m_effectParam);
                    }
                }

                break;
            case kSpellEffects.kMaxHealthDamage:
                cinematicTime += ApplyMaxHealthDamageEffect(effect, targets);
                break;
            case kSpellEffects.kReshuffle:
                foreach (var target in targets.Where(t => t?._combatDeck is not null)) {
                    ApplyReshuffleEffect(target);
                }

                break;
            case kSpellEffects.kRemoveCharm:
                cinematicTime += ApplyRemoveCharmEffect(effect, targets);
                break;
            case kSpellEffects.kRemoveWard:
                cinematicTime += ApplyRemoveWardEffect(effect, targets);
                break;
            case kSpellEffects.kStealCharm:
                cinematicTime += ApplyStealCharmEffect(effect, caster, targets);
                break;
            case kSpellEffects.kStealWard:
                cinematicTime += ApplyStealWardEffect(effect, caster, targets);
                break;
            case kSpellEffects.kDetonateOverTime:
                cinematicTime += ApplyDetonateEffect(effect, targets);
                break;
            // Cleanses: Triage, Mass Triage, Cleanse, and taking an enemy's heal over time.
            case kSpellEffects.kRemoveOverTime:
                cinematicTime += Remove(targets, t => OverTimes(t, effect.m_disposition), effect);
                break;
            case kSpellEffects.kRemoveAura:
                cinematicTime += Remove(targets, t => Auras(t, effect.m_disposition), effect);
                break;
            // Push: the caster's own effects go onto the target (Hot Potato, Push Charm).
            case kSpellEffects.kPushCharm:
                cinematicTime += Move(caster, targets, effect, CombatCharms.All, toTarget: true);
                break;
            case kSpellEffects.kPushWard:
                cinematicTime += Move(caster, targets, effect, CombatWards.All, toTarget: true);
                break;
            case kSpellEffects.kPushOverTime:
                cinematicTime += Move(caster, targets, effect, OverTimes, toTarget: true);
                break;
            case kSpellEffects.kStealOverTime:
                cinematicTime += Move(caster, targets, effect, OverTimes, toTarget: false);
                break;
            case kSpellEffects.kSwapCharm:
                cinematicTime += Swap(caster, targets, effect, CombatCharms.All);
                break;
            case kSpellEffects.kSwapWard:
                cinematicTime += Swap(caster, targets, effect, CombatWards.All);
                break;
            case kSpellEffects.kSwapOverTime:
                cinematicTime += Swap(caster, targets, effect, OverTimes);
                break;
            case kSpellEffects.kSwapAll:
                cinematicTime += Swap(caster, targets, effect, CombatCharms.All);
                cinematicTime += Swap(caster, targets, effect, CombatWards.All);
                cinematicTime += Swap(caster, targets, effect, OverTimes);
                break;
            // More (or fewer) rounds on the target's damage or heal over time effects.
            case kSpellEffects.kModifyOverTimeDuration:
                foreach (var target in targets.Where(t => t?._hangingEffects is not null)) {
                    foreach (var overTime in OverTimes(target, effect.m_disposition)) {
                        var index = target._hangingEffects.FindIndex(x => ReferenceEquals(x, overTime));
                        if (index >= 0) {
                            target._hangingEffects[index] = overTime with {
                                m_numRounds = Math.Max(1, overTime.m_numRounds + effect.m_effectParam),
                            };
                        }
                    }
                }

                break;
            default:
                if (!s_handledElsewhere.Contains(effect.m_effectType)) {
                    Logger.Debug("Spell {0}: effect {1} is not done by the server; only the game shows it.",
                        Logger.Args(spellTemplateId, effect.m_effectType));
                }

                break;
        }

        return cinematicTime;
    }

    private static float ApplyFlatDamageEffect(SpellEffect effect,
                                               SpellEffect[] charms,
                                               CombatDuelSubCircle caster,
                                               CombatDuelSubCircle[] targets,
                                               float critMultiplier = 1) {
        var (cinematicTime, _) = ApplyDamageEffect(effect, charms, caster, targets, critMultiplier);

        return cinematicTime;
    }

    // The damage a spell effect deals before the target's own wards and resist: caster stats, charms and the bubble.
    private static int OutgoingDamage(SpellEffect effect, SpellEffect[] charms, CombatDuelSubCircle caster) {
        int damageFromCaster = effect.m_effectParam;

        // Calculate damage increase from caster stats.
        var damageFlatIncrease = GetFlatDamageIncrease(caster, effect.m_sDamageType);
        var damagePercentIncrease = 1 + GetPercentDamageIncrease(caster, effect.m_sDamageType);
        damageFromCaster = (int) Math.Floor((damageFromCaster * damagePercentIncrease) + damageFlatIncrease);

        // Calculate damage changes from hanging effects.
        damageFromCaster = CombatCharms.GetOutgoingDamageFromCharms(charms, damageFromCaster);

        var duel = caster._duelActor.Duel;
        var bubbleDamageIncrease = GetGlobalEffectDamageModifier(duel, effect.m_sDamageType);

        return (int) Math.Floor(damageFromCaster * (1 + bubbleDamageIncrease));
    }

    /// <summary>
    /// The caster's pierce for a damage effect, as a fraction: gear, pierce charms and auras (spears, Infallible), the
    /// card's own pierce (Extraordinary) and a pierce bubble (Age of Reckoning). Only gear counted before.
    /// </summary>
    private static float OutgoingPierce(SpellEffect effect, SpellEffect[] charms, CombatDuelSubCircle caster) {
        var pierce = GetPierce(caster, effect.m_sDamageType);
        pierce += charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingArmorPiercing).Sum(c => c.m_effectParam) / 100f;
        pierce += effect.m_armorPiercingParam / 100f;
        pierce += GetGlobalEffect(caster._duelActor.Duel, kSpellEffects.kModifyOutgoingArmorPiercing, effect.m_sDamageType) / 100f;

        return Math.Clamp(pierce, 0f, 1f);
    }

    private static float ApplyDamageOverTime(SpellEffect effect,
                                             SpellEffect[] charms,
                                             CombatDuelSubCircle caster,
                                             CombatDuelSubCircle[] targets,
                                             uint spellTemplateId,
                                             bool deferred = false) {
        var cinematicTime = charms.Length * HANGING_EFFECT_CONSUME_TIME;
        var damageFromCaster = OutgoingDamage(effect, charms, caster);
        var pierce = OutgoingPierce(effect, charms, caster);
        var rounds = Math.Max(1, effect.m_numRounds);

        foreach (var target in targets) {
            if (target?._hangingEffects is null || !target.IsAlive) {
                continue;
            }

            // Calculate damage reduction from target stats, less the caster's pierce.
            var damageReductionFlat = GetFlatDamageReduction(target, effect.m_sDamageType);
            var damageReductionPercent = Pierced(GetPercentDamageReduction(target, effect.m_sDamageType), pierce);
            var reducedDamage = (int) Math.Floor(damageFromCaster * (1 - damageReductionPercent) - damageReductionFlat);

            // Ensure that damage isn't negative.
            reducedDamage = Math.Max(reducedDamage, 0);

            // A copy for each target, so a tick never changes the spell itself.
            target._hangingEffects.Add(effect with {
                m_numRounds = rounds,
                // A bomb keeps all of its damage for the one hit at the end.
                m_paramPerRound = deferred ? reducedDamage : reducedDamage / rounds,
                m_spellTemplateID = spellTemplateId != 0 ? spellTemplateId : effect.m_spellTemplateID,
            });
        }

        return cinematicTime;
    }

    private static float ApplyHealOverTime(SpellEffect effect,
                                           SpellEffect[] charms,
                                           CombatDuelSubCircle caster,
                                           CombatDuelSubCircle[] targets,
                                           uint spellTemplateId) {
        var healFromCaster = effect.m_effectParam;
        var cinematicTime = 0.0f;
        var rounds = Math.Max(1, effect.m_numRounds);

        // Calculate heal increase from gear
        var percentOutgoingHealIncrease = GetPercentOutgoingHealIncrease(caster);
        healFromCaster = (int) Math.Ceiling(healFromCaster * (1 + percentOutgoingHealIncrease));

        // Calculate heal increase/decrease from hanging effects on caster
        cinematicTime += charms.Length * HANGING_EFFECT_CONSUME_TIME;
        healFromCaster = CombatCharms.GetOutgoingHealFromCharms(charms, healFromCaster);

        // Calculate heal increase from current bubble
        var duel = caster._duelActor.Duel;
        var bubbleHealIncrease = GetGlobalEffectHealingModifier(duel);
        healFromCaster = (int) Math.Ceiling(healFromCaster * (1 + bubbleHealIncrease));

        foreach (var target in targets) {
            if (target?._hangingEffects is null || !target.IsAlive) {
                continue;
            }

            // Calculate heal increase from target stats
            var percentIncomingHealIncrease = GetPercentIncomingHealIncrease(target);
            var healPerTarget = (int) Math.Ceiling(healFromCaster * (1 + percentIncomingHealIncrease));

            target._hangingEffects.Add(effect with {
                m_numRounds = rounds,
                m_paramPerRound = healPerTarget / rounds,
                m_spellTemplateID = spellTemplateId != 0 ? spellTemplateId : effect.m_spellTemplateID,
            });
        }

        return cinematicTime;
    }

    private static float ApplyStealHealthEffect(SpellEffect effect,
                                                SpellEffect[] charms,
                                                CombatDuelSubCircle caster,
                                                CombatDuelSubCircle[] targets,
                                                float critMultiplier) {
        var (cinematicTime, damageDealt) = ApplyDamageEffect(effect, charms, caster, targets, critMultiplier);
        var casterHealTotal = (int) Math.Floor(damageDealt * (effect.m_healModifier > 0 ? effect.m_healModifier : 0.5f));
        DoHealToTarget(caster, casterHealTotal);

        return cinematicTime;
    }

    private static (float cinematicTime, int damageDealt) ApplyDamageEffect(SpellEffect effect,
                                                                            SpellEffect[] charms,
                                                                            CombatDuelSubCircle caster,
                                                                            CombatDuelSubCircle[] targets,
                                                                            float critMultiplier = 1) {
        var cinematicTime = charms.Length * HANGING_EFFECT_CONSUME_TIME;
        var damageFromCaster = OutgoingDamage(effect, charms, caster);

        // A landed critical multiplies the fully-boosted outgoing damage by a variable factor;
        // wards and resist still reduce it.
        damageFromCaster = (int) Math.Floor(damageFromCaster * critMultiplier);
        var casterStats = caster?.ParticipantGameStats;
        Logger.Debug("Damage from slot {0}: effect {1} base {2}, after boosts and charms {3} (crit x{4}). Raw stats: damage {5} school + {6} all,"
                     + " flat {7}, pierce {8} school + {9} all, charms [{10}].",
            Logger.Args(caster?.SlotIndex, effect.m_effectType, effect.m_effectParam, damageFromCaster, critMultiplier,
                caster?.GetStatBySchool(casterStats?.m_dmgBonusPercent, effect.m_sDamageType), casterStats?.m_dmgBonusPercentAll,
                caster is null ? 0 : GetFlatDamageIncrease(caster, effect.m_sDamageType),
                caster?.GetStatBySchool(casterStats?.m_apBonusPercent, effect.m_sDamageType), casterStats?.m_apBonusPercentAll,
                string.Join(", ", charms.Select(c => $"{c.m_effectType} {c.m_effectParam}"))));

        // Pierce (armor piercing) goes through the target's shields first, then what is left through its resist.
        var pierce = OutgoingPierce(effect, charms, caster);

        // Apply damage to each target
        var damageDealt = 0;
        var hits = new List<string>();
        foreach (var target in targets) {
            if (target?._hangingEffects is null) {
                continue;
            }

            var damage = damageFromCaster;
            var pierceLeft = pierce;

            // Pierce traps and the like on the target (kModifyIncomingArmorPiercing): more (or, when negative, less)
            // pierce for hits against it. A trap goes once used; an aura stays.
            var incomingPierce = CombatCharms.OnePerSpell(target._hangingEffects.Where(x => x?.m_effectType == kSpellEffects.kModifyIncomingArmorPiercing
                && (string.IsNullOrEmpty(x.m_sDamageType) || CombatCharms.SchoolMatches(x.m_sDamageType, effect.m_sDamageType))));
            if (incomingPierce.Count > 0) {
                pierceLeft = Math.Clamp(pierceLeft + incomingPierce.Sum(x => x.m_effectParam) / 100f, 0f, 1f);
                CombatWards.Consume(target, incomingPierce);
            }

            // Calculate damage changes from target hanging effects.
            var wards = CombatWards.FindAppliedWards(target, effect);
            wards = CombatWards.GetWardsBySchool([.. wards], effect.m_sDamageType, out var finalSchool);
            cinematicTime += wards.Count * HANGING_EFFECT_CONSUME_TIME;
            damage = CombatWards.GetIncomingDamageFromWards(wards, damage, ref pierceLeft);

            var dealt = DoDamageToTarget(target, damage, finalSchool, pierceLeft);
            damageDealt += dealt;
            hits.Add($"{TargetName(target)}: dealt {dealt}");

            // The shields, traps and prism this hit used go; an absorb stays until it is used up.
            CombatWards.Consume(target, wards);
        }

        TellRealDamage(caster, hits);

        return (cinematicTime, damageDealt);
    }

    // The game works out its own damage number with its diminishing returns, so it can show less than the server
    // dealt. The caster gets the real amount per enemy in chat (Niduus, 2026-10-23).
    private static void TellRealDamage(CombatDuelSubCircle caster, List<string> hits) {
        if (hits.Count == 0 || caster is not { IsHumanPlayer: true, ParticipantActor: not null }) {
            return;
        }

        // One line per enemy, so an AoE lists each one (Niduus: "[enemy name]: dealt [damage]").
        foreach (var hit in hits) {
            caster.ParticipantActor.Tell(new Imcodec.MessageLayer.Generated.EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = hit, Modal = 0 });
        }
    }

    private static readonly Dictionary<ulong, string> s_creatureNames = [];

    // The enemy's name as the game shows it: a custom's own name, else the creature's from the ID catalog.
    private static string TargetName(CombatDuelSubCircle target) {
        var creature = target.ParticipantObject;
        if (target.IsHumanPlayer || creature is null) {
            return $"Slot {target.SlotIndex + 1}";
        }

        if (Customs.NameOf(creature.m_globalID.Full) is { } custom) {
            return custom;
        }

        var templateId = (ulong) creature.m_templateID.Full;
        lock (s_creatureNames) {
            if (!s_creatureNames.TryGetValue(templateId, out var name)) {
                name = Imlight.CoreLib.Shared.Resources.IdCatalog.IsReady
                    ? Imlight.CoreLib.Shared.Resources.IdCatalog.Search(Imlight.CoreLib.Shared.Resources.IdCatalogKind.Creature,
                        templateId.ToString(), 1).FirstOrDefault()?.Name
                    : null;
                if (name is not null) {
                    s_creatureNames[templateId] = name;
                }
            }

            return name ?? $"Slot {target.SlotIndex + 1}";
        }
    }

    /// <summary>
    /// A damage over time tick (or a bomb going off) on <paramref name="target"/>: the target's shields, traps and
    /// prism for its school change it and are used up, as in the game. Before, every ward of every school changed each
    /// tick and none was ever used.
    /// </summary>
    /// <returns>How many wards the tick used.</returns>
    internal static int TickDamage(CombatDuelSubCircle target, SpellEffect overTime, int damage) {
        var wards = CombatWards.FindAppliedWards(target, overTime);
        wards = CombatWards.GetWardsBySchool([.. wards], overTime.m_sDamageType, out _);
        damage = CombatWards.GetIncomingDamageFromWards(wards, damage);
        damage = CapDamage(target, damage);
        target.DamageParticipant(Math.Max(0, damage));
        CombatWards.Consume(target, wards);

        return wards.Count;
    }

    private static float ApplyFlatHealEffect(SpellEffect effect,
                                             SpellEffect[] charms,
                                             CombatDuelSubCircle caster,
                                             CombatDuelSubCircle[] targets,
                                             float critMultiplier = 1) {
        int healFromCaster = effect.m_effectParam;
        var cinematicTime = 0.0f;

        // Calculate heal increase from gear
        var percentOutgoingHealIncrease = GetPercentOutgoingHealIncrease(caster);
        healFromCaster = (int) Math.Ceiling(healFromCaster * (1 + percentOutgoingHealIncrease));

        // Calculate heal increase/decrease from hanging effects on caster
        cinematicTime += charms.Length * HANGING_EFFECT_CONSUME_TIME;
        healFromCaster = CombatCharms.GetOutgoingHealFromCharms(charms, healFromCaster);

        // Calculate heal increase from current bubble
        var duel = caster._duelActor.Duel;
        var bubbleHealIncrease = GetGlobalEffectHealingModifier(duel);
        healFromCaster = (int) Math.Ceiling(healFromCaster * (1 + bubbleHealIncrease));

        // A landed critical doubles the fully-boosted outgoing heal; incoming heal boosts still apply.
        healFromCaster = (int) Math.Ceiling(healFromCaster * critMultiplier);

        // Apply heal to each target
        foreach (var target in targets) {
            if (target is not null) {
                _ = DoHealToTarget(target, healFromCaster);
            }
        }

        return cinematicTime;
    }

    private static void ApplyReshuffleEffect(CombatDuelSubCircle caster)
        => caster.Reshuffle();

    /// <summary>
    /// Aegis and Indemnity: a protected charm or ward survives one removal or steal and loses its protection.
    /// Returns the chosen effects that really go.
    /// </summary>
    private static List<SpellEffect> Unprotected(CombatDuelSubCircle target, SpellEffect remover, IEnumerable<SpellEffect> chosen) {
        var going = new List<SpellEffect>();
        foreach (var hanging in chosen.ToList()) {
            var index = target._hangingEffects.FindIndex(x => ReferenceEquals(x, hanging));
            if (hanging.m_protected && !remover.m_bypassProtection && index >= 0) {
                target._hangingEffects[index] = hanging with { m_protected = false };
            }
            else {
                going.Add(hanging);
            }
        }

        return going;
    }

    // How many a removal or steal takes: -1 (and anything below 1) means all of them.
    private static int HowMany(SpellEffect effect) => effect.m_effectParam < 1 ? int.MaxValue : effect.m_effectParam;

    private static float ApplyRemoveCharmEffect(SpellEffect effect, CombatDuelSubCircle[] targets)
        => Remove(targets, t => CombatCharms.All(t, effect.m_disposition), effect);

    // Stun blocks stay: a ward removal does not take them.
    private static float ApplyRemoveWardEffect(SpellEffect effect, CombatDuelSubCircle[] targets)
        => Remove(targets, t => [.. CombatWards.All(t, effect.m_disposition).Where(x => x.m_effectType != kSpellEffects.kStunBlock)], effect);

    private static float Remove(CombatDuelSubCircle[] targets, Func<CombatDuelSubCircle, List<SpellEffect>> find, SpellEffect effect) {
        var cinematicTime = 0.0f;
        foreach (var target in targets) {
            if (target?._hangingEffects is null) {
                continue;
            }

            var going = Unprotected(target, effect, find(target).Take(HowMany(effect)));
            cinematicTime += going.Count * HANGING_EFFECT_CONSUME_TIME;
            CombatCharms.RemoveExact(target._hangingEffects, going);
        }

        return cinematicTime;
    }

    private static float ApplyStealCharmEffect(SpellEffect effect, CombatDuelSubCircle caster, CombatDuelSubCircle[] targets)
        => Move(caster, targets, effect, CombatCharms.All, toTarget: false);

    private static float ApplyStealWardEffect(SpellEffect effect, CombatDuelSubCircle caster, CombatDuelSubCircle[] targets)
        => Move(caster, targets, effect, (t, d) => [.. CombatWards.All(t, d).Where(x => x.m_effectType != kSpellEffects.kStunBlock)],
                toTarget: false);

    // Moves effects between the caster and each target: a push gives the caster's own, a steal takes the target's.
    private static float Move(CombatDuelSubCircle caster, CombatDuelSubCircle[] targets, SpellEffect effect,
                              Func<CombatDuelSubCircle, kHangingDisposition, List<SpellEffect>> find, bool toTarget) {
        var cinematicTime = 0.0f;
        foreach (var target in targets) {
            if (target?._hangingEffects is null || caster?._hangingEffects is null || ReferenceEquals(target, caster)) {
                continue;
            }

            var from = toTarget ? caster : target;
            var to = toTarget ? target : caster;
            var going = Unprotected(from, effect, find(from, effect.m_disposition).Take(HowMany(effect)));
            cinematicTime += going.Count * HANGING_EFFECT_CONSUME_TIME;
            CombatCharms.RemoveExact(from._hangingEffects, going);
            to._hangingEffects.AddRange(going);
        }

        return cinematicTime;
    }

    // Swaps the effects of a kind between the caster and each target.
    private static float Swap(CombatDuelSubCircle caster, CombatDuelSubCircle[] targets, SpellEffect effect,
                              Func<CombatDuelSubCircle, kHangingDisposition, List<SpellEffect>> find) {
        var cinematicTime = 0.0f;
        foreach (var target in targets) {
            if (target?._hangingEffects is null || caster?._hangingEffects is null || ReferenceEquals(target, caster)) {
                continue;
            }

            var mine = find(caster, effect.m_disposition).Take(HowMany(effect)).ToList();
            var theirs = find(target, effect.m_disposition).Take(HowMany(effect)).ToList();
            CombatCharms.RemoveExact(caster._hangingEffects, mine);
            CombatCharms.RemoveExact(target._hangingEffects, theirs);
            caster._hangingEffects.AddRange(theirs);
            target._hangingEffects.AddRange(mine);
            cinematicTime += (mine.Count + theirs.Count) * HANGING_EFFECT_CONSUME_TIME;
        }

        return cinematicTime;
    }

    // A participant's damage over time (harmful) and heal over time (beneficial) effects, newest first.
    private static List<SpellEffect> OverTimes(CombatDuelSubCircle target, kHangingDisposition disposition)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => x?.m_effectType is kSpellEffects.kDamageOverTime or kSpellEffects.kHealOverTime
                                      or kSpellEffects.kDeferredDamage
                                     && CombatCharms.Disposed(x, disposition)).Reverse()]
            : [];

    private static List<SpellEffect> Auras(CombatDuelSubCircle target, kHangingDisposition disposition)
        => target?._hangingEffects is { } hanging
            ? [.. hanging.Where(x => CombatRequirements.IsAura(x) && CombatCharms.Disposed(x, disposition)).Reverse()]
            : [];

    /// <summary>
    /// Puts a charm, ward or aura on each target: a copy each, carrying the spell's id. Every target used to get the
    /// spell's own effect object, so an absorb shared one pool among all its targets, using it changed the spell for
    /// the rest of the battle, and with no spell id only one charm and one ward ever counted on a hit.
    /// </summary>
    private static void ApplyHangingEffect(SpellEffect effect, CombatDuelSubCircle[] targets, uint spellTemplateId) {
        var id = spellTemplateId != 0 ? spellTemplateId : effect.m_spellTemplateID;
        foreach (var target in targets) {
            if (target?._hangingEffects is not { } hanging) {
                continue;
            }

            var copy = effect with {
                m_spellTemplateID = id,
                m_paramPerRound = effect.m_effectType == kSpellEffects.kAbsorbDamage ? effect.m_effectParam : effect.m_paramPerRound,
            };

            // A participant has one aura at a time: a new one (Fortify, Amplify, Frenzy...) takes the old one's place.
            if (CombatRequirements.IsAura(copy)) {
                hanging.RemoveAll(x => CombatRequirements.IsAura(x) && (x.m_spellTemplateID != id || id == 0));
            }

            hanging.Add(copy);
        }
    }

    // Deals a percentage of the target's maximum health; m_effectParam is the percent.
    private static float ApplyMaxHealthDamageEffect(SpellEffect effect, CombatDuelSubCircle[] targets) {
        var cinematicTime = 0.0f;
        foreach (var target in targets) {
            if (target is null || !target.IsAlive) {
                continue;
            }

            var maxHealth = target.ParticipantGameStats.m_baseHitpoints;
            var damage = (int) Math.Floor(maxHealth * (effect.m_effectParam / 100.0f));
            target.DamageParticipant(damage);
        }

        return cinematicTime;
    }

    // Adds or removes generic pips (signed m_effectParam). Gains respect the 7-pip cap; steals
    // only take generic pips, so a power-pip-only target keeps them.
    private static void ApplyModifyPipsEffect(SpellEffect effect, CombatDuelSubCircle[] targets) {
        foreach (var target in targets) {
            if (target is null || !target.IsAlive) {
                continue;
            }

            var pipCount = target.CombatParticipant.m_pipCount;
            var genericPips = pipCount.m_genericPips;
            if (effect.m_effectParam >= 0) {
                pipCount.m_genericPips = (byte) Math.Min(genericPips + effect.m_effectParam,
                                                        CombatDuelSubCircle.MAX_PIP_COUNT - (CombatDuelSubCircle.TotalPips(pipCount) - genericPips));
            }
            else {
                pipCount.m_genericPips = (byte) Math.Max(genericPips + effect.m_effectParam, 0);
            }
        }
    }

    // Sets off the targets' damage (or heal) over time effects at once: each deals what it has left, times
    // m_healModifier (2 for "Detonate 200"). m_effectParam is how many to set off, below 0 for all of them.
    private static float ApplyDetonateEffect(SpellEffect effect, CombatDuelSubCircle[] targets) {
        var cinematicTime = 0.0f;
        var heals = effect.m_disposition == kHangingDisposition.kBeneficial;
        var kind = heals ? kSpellEffects.kHealOverTime : kSpellEffects.kDamageOverTime;
        var multiplier = effect.m_healModifier > 0 ? effect.m_healModifier : 1f;

        foreach (var target in targets) {
            if (target is null || target.ParticipantObject is null || !target.IsAlive) {
                continue;
            }

            var overTime = target._hangingEffects.Where(x => x.m_effectType == kind && x.m_numRounds > 0).ToList();
            if (effect.m_effectParam >= 0) {
                overTime = [.. overTime.Take(effect.m_effectParam)];
            }

            if (overTime.Count == 0) {
                continue;
            }

            var total = (int) Math.Floor(overTime.Sum(x => (long) x.m_paramPerRound * x.m_numRounds) * multiplier);
            CombatCharms.RemoveExact(target._hangingEffects, overTime);
            cinematicTime += overTime.Count * HANGING_EFFECT_CONSUME_TIME;

            if (heals) {
                target.HealParticipant(total);
            }
            else {
                target.DamageParticipant(total);
            }

            Logger.Information("Detonate on slot {0}: {1} over time effects, {2} {3}.",
                               Logger.Args(target.SlotIndex, overTime.Count, total, heals ? "healing" : "damage"));
        }

        return cinematicTime;
    }

    // Kills the effect's targets outright; the round cleanup sends MSG_COMBATDEATH for the corpse.
    // kKillCreature only kills the named creature: m_effectParam is the template id to match.
    private static float ApplyKillCreatureEffect(SpellEffect effect, CombatDuelSubCircle[] targets) {
        foreach (var target in targets) {
            if (target is null || target.ParticipantObject is null || !target.IsAlive) {
                continue;
            }

            if (effect.m_effectType == kSpellEffects.kKillCreature
                && target.ParticipantObject.m_templateID.Full != (ulong) effect.m_effectParam) {
                continue;
            }

            target.DamageParticipant(target.ParticipantGameStats.m_currentHitpoints);
        }

        return 0.0f;
    }

    // m_effectParam is the creature template id to spawn.
    private static void ApplySummonCreature(SpellEffect effect, CombatDuelSubCircle caster) {
        if (effect.m_effectParam <= 0 || caster?._duelActor is null) {
            return;
        }

        caster._duelActor.SummonMinion((uint) effect.m_effectParam, caster);
    }

    private static float ApplyStunEffect(SpellEffect effect, CombatDuelSubCircle[] targets) {
        var cinematicTime = 0.0f;

        foreach (var target in targets) {
            // If this target is dead, do nothing.
            if (target is null || !target.IsAlive) {
                continue;
            }

            // Check to see if this target has a stun block.
            var spellBlock = target._hangingEffects.FirstOrDefault(x => x.m_effectType == kSpellEffects.kStunBlock);
            if (spellBlock is not null) {
                // Remove the stun block and do nothing else.
                CombatCharms.RemoveExact(target._hangingEffects, [spellBlock]);
                cinematicTime += HANGING_EFFECT_CONSUME_TIME;

                continue;
            }

            // Stun Resistance (a fraction, 1.45 for 145%) is the chance to shrug the stun off; 100% or more always does.
            var stunResist = target._wizard is not null ? target.ParticipantGameStats?.m_stunResistancePercent ?? 0f : 0f;
            if (stunResist > 0 && Random.Shared.NextDouble() < stunResist) {
                Logger.Information("{0} resisted a stun (Stun Resistance {1:P0}).", Logger.Args(target.SlotName, stunResist));

                continue;
            }

            Logger.Information("Stun on {0}: Stun Resistance {1:P0}, not resisted.", Logger.Args(target.SlotName, stunResist));
            if (target.TryStun()) {
                // A stun of more than one round (m_effectParam) keeps the target stunned for that long.
                if (effect.m_effectParam > 1) {
                    target.CombatParticipant.m_stunned = Math.Max(target.CombatParticipant.m_stunned, effect.m_effectParam);
                }

                // Creature was stunned. Add a stun block hanging effect.
                var stunBlockEffect = new SpellEffect {
                    m_effectType = kSpellEffects.kStunBlock,
                    m_spellTemplateID = effect.m_spellTemplateID,
                };

                target._hangingEffects.Add(stunBlockEffect);
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// Puts a global (a bubble) on the battlefield. One bubble at a time: a new spell's bubble takes the old one's
    /// place, but a bubble with two effects (Age of Reckoning: pierce and critical) keeps both.
    /// </summary>
    private static void ApplyGlobalEffect(SpellEffect spellEffect, Duel duel, uint spellTemplateId) {
        var effects = duel.m_duelModifier.m_battlefieldEffects;
        var id = spellTemplateId != 0 ? spellTemplateId : spellEffect.m_spellTemplateID;
        effects.RemoveAll(x => x is null || id == 0 || x.m_spellTemplateID != id);
        effects.Add(spellEffect with { m_spellTemplateID = id });
    }

    private static int DoDamageToTarget(CombatDuelSubCircle target, int damage, string damageType, float pierce = 0) {
        // Calculate damage reduction from target stats, less the pierce the shields left.
        var damageReductionFlat = GetFlatDamageReduction(target, damageType);
        var damageReductionPercent = Pierced(GetPercentDamageReduction(target, damageType), pierce);
        var reducedDamage = (int) Math.Floor(damage * (1 - damageReductionPercent) - damageReductionFlat);

        // Ensure that damage isn't negative.
        var beforeCap = Math.Max(reducedDamage, 0);
        reducedDamage = CapDamage(target, beforeCap);

        // The game works a hit out on its own; when it gets more than this, it shows a creature dying that the server
        // keeps alive (Niduus, 2026-10-23: Draconians lost their health circle until the next round). Logged to compare.
        Logger.Debug("Damage to slot {0}: {1} {2} in, resist {3:P0} (after pierce {4:P0}), flat {5}, {6} after resist{7}, health {8} -> {9}.",
            Logger.Args(target.SlotIndex, damage, damageType, GetPercentDamageReduction(target, damageType), damageReductionPercent,
                damageReductionFlat, beforeCap, reducedDamage < beforeCap ? $", capped to {reducedDamage}" : "",
                target.ParticipantGameStats?.m_currentHitpoints, (target.ParticipantGameStats?.m_currentHitpoints ?? 0) - reducedDamage));

        target.DamageParticipant(reducedDamage);

        return reducedDamage;
    }

    // Equalize and the like: a bubble or ward that caps the damage of one hit.
    private static int CapDamage(CombatDuelSubCircle target, int damage) {
        var caps = (target._duelActor?.Duel?.m_duelModifier?.m_battlefieldEffects ?? [])
            .Concat(target._hangingEffects ?? [])
            .Where(x => x?.m_effectType == kSpellEffects.kMaximumIncomingDamage && x.m_effectParam > 0)
            .Select(x => x.m_effectParam)
            .ToList();

        return caps.Count > 0 ? Math.Min(damage, caps.Min()) : damage;
    }

    /// <summary>
    /// A heal landing on the target: its heal wards (Infection-style -heal, or +heal) change it and are used up, then
    /// the target's incoming heal stat. Heal wards were kept but never read.
    /// </summary>
    private static int DoHealToTarget(CombatDuelSubCircle target, int heal) {
        if (target?.ParticipantGameStats is null) {
            return 0;
        }

        var wards = CombatCharms.OnePerSpell((target._hangingEffects ?? [])
            .Where(x => x?.m_effectType is kSpellEffects.kModifyIncomingHeal or kSpellEffects.kModifyIncomingHealFlat)).ToList();
        foreach (var ward in wards) {
            heal = ward.m_effectType == kSpellEffects.kModifyIncomingHeal
                ? (int) Math.Floor(heal * (1 + ward.m_effectParam / 100.0f))
                : heal + ward.m_effectParam;
        }

        CombatCharms.RemoveExact(target._hangingEffects, wards.Where(x => !CombatRequirements.IsAura(x)));

        var percentIncomingHealIncrease = GetPercentIncomingHealIncrease(target);
        heal = Math.Max(0, (int) Math.Ceiling(heal * (1 + percentIncomingHealIncrease)));
        target.HealParticipant(heal);

        return heal;
    }

    private static float GetFlatDamageIncrease(CombatDuelSubCircle caster, string damageType) {
        var damageFlatIncrease = caster.GetStatBySchool(caster.ParticipantGameStats.m_dmgBonusFlat, damageType);

        return damageFlatIncrease + caster.ParticipantGameStats.m_dmgBonusFlatAll;
    }

    /// <summary>
    /// Damage, resist, pierce and accuracy stats are all fractions (0.29 for 29%): gear, jewels, pets and creatures alike,
    /// as the "Stat added" log lines show. This used to read any total above 1.5 as percent and divide it by 100, so
    /// The One Ring's +300% damage turned 440% into 4% and the server's Tempest did a third of what the game showed
    /// (Niduus, 2026-10-23: "could it be because it sees the 3rd value?"). Kept as the one place that says what unit
    /// a stat is in.
    /// </summary>
    internal static float AsFraction(float value)
        => value;

    private static float GetPercentDamageIncrease(CombatDuelSubCircle caster, string damageType) {
        var damagePercentIncrease = caster.GetStatBySchool(caster.ParticipantGameStats.m_dmgBonusPercent, damageType);
        damagePercentIncrease = AsFraction(damagePercentIncrease + caster.ParticipantGameStats.m_dmgBonusPercentAll);

        return damagePercentIncrease;
    }

    /// <summary>
    /// The caster's pierce (armor piercing) for a school as a fraction, 0 to 1. Gear, jewels and the Bosses tab give it
    /// (m_apBonusPercent); before this nothing in battle read it (Niduus, 2026-10-21: "I want specifics covered").
    /// </summary>
    internal static float GetPierce(CombatDuelSubCircle caster, string damageType) {
        var stats = caster.ParticipantGameStats;
        if (stats is null || MagicSchools.GetMagicSchool(damageType) is null) {
            return 0f;
        }

        return Math.Clamp(AsFraction(caster.GetStatBySchool(stats.m_apBonusPercent, damageType) + stats.m_apBonusPercentAll), 0f, 1f);
    }

    // Pierce lowers resist down to zero, never below: a weakness (resist under zero) stays as it is.
    private static float Pierced(float resist, float pierce)
        => resist > 0 ? Math.Max(0f, resist - pierce) : resist;

    private static float GetFlatDamageReduction(CombatDuelSubCircle target, string damageType) {
        var damageReductionFlat = target.GetStatBySchool(target.ParticipantGameStats.m_dmgReduceFlat, damageType);

        return damageReductionFlat + target.ParticipantGameStats.m_dmgReduceFlatAll;
    }

    private static float GetPercentDamageReduction(CombatDuelSubCircle target, string damageType) {
        var damageReductionPercent = target.GetStatBySchool(target.ParticipantGameStats.m_dmgReducePercent, damageType);

        return AsFraction(damageReductionPercent + target.ParticipantGameStats.m_dmgReducePercentAll);
    }

    private static float GetPercentOutgoingHealIncrease(CombatDuelSubCircle caster)
        => caster.ParticipantGameStats.m_healBonusPercentAll;

    private static float GetPercentIncomingHealIncrease(CombatDuelSubCircle target)
        => target.ParticipantGameStats.m_healIncBonusPercentAll;

    // The bubble's damage change for a school: a school bubble (Inferno Salamander's) or an all-school one (Power Play
    // gives +25% to all, sDamageType "All", and was never matched).
    private static float GetGlobalEffectDamageModifier(Duel duel, string damageType)
        => GetGlobalEffect(duel, kSpellEffects.kModifyOutgoingDamage, damageType) / 100.0f;

    /// <summary>The sum of the battlefield's effects of a type for a school (or for all schools).</summary>
    internal static int GetGlobalEffect(Duel duel, kSpellEffects type, string school) {
        var effects = duel?.m_duelModifier?.m_battlefieldEffects;
        if (effects is null) {
            return 0;
        }

        return effects.Where(x => x is not null && x.m_effectType == type
                                  && (school is null || CombatCharms.SchoolMatches(x.m_sDamageType, school)))
            .Sum(x => x.m_effectParam);
    }

    private static float GetGlobalEffectHealingModifier(Duel duel) {
        var healBubble = duel.m_duelModifier.m_battlefieldEffects
            .FirstOrDefault(x => x?.m_effectType == kSpellEffects.kModifyOutgoingHeal);

        if (healBubble is not null) {
            return healBubble.m_effectParam / 100.0f;
        }

        return 0.0f;
    }

}
