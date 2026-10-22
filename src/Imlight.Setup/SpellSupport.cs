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
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.Setup;

/// <summary>Whether the server does what a spell card says.</summary>
internal enum SpellStatus {

    /// <summary>Every part of the spell is done by the server.</summary>
    Works,

    /// <summary>The spell casts, but the server skips some of its parts.</summary>
    Partly,

    /// <summary>The server does not do the spell's main effect, or the spell can't be cast at all.</summary>
    Broken,

}

/// <summary>What <see cref="SpellSupport.Classify"/> found: the status, why, and the parts the server skips.</summary>
internal sealed record SpellSupportResult(SpellStatus Status, string Reason, IReadOnlyList<string> Skipped) {

    internal static readonly SpellSupportResult Unknown = new(SpellStatus.Works, "", []);

    /// <summary>One line for a tooltip: why the card is broken, or which parts are skipped.</summary>
    internal string Summary => Status switch {
        SpellStatus.Broken => Reason,
        SpellStatus.Partly => "Works, but the server skips: " + string.Join("; ", Skipped) + ".",
        _ => "The server does everything this card says.",
    };

}

/// <summary>
/// Reads a spell's effects (through effect lists, random, X-pip, conditional, target-count and the rest) and says
/// whether Imlight's server does them, and writes a plain-English description from them for cards whose game text is
/// blank (Niduus, 2026-10-22: "mark cards that don't work as BROKEN ... give more explanation to the cards that have
/// blank descriptions").
/// </summary>
/// <remarks>
/// KEEP IN SYNC with the server. The lists below copy what the server's combat code handles today:
/// <list type="bullet">
/// <item>src/Imlight.CoreLib/Game/Combat/CombatActionResolver.cs, Resolve(): which effect classes are walked
/// (random, X-pip, conditional, target-count, effect lists, afterlife) and which are skipped (hanging conversion,
/// delay, count-based, shadow pact), and that effects aimed at a card (kSpell, kSpecificSpells) are not applied.</item>
/// <item>src/Imlight.CoreLib/Game/Combat/CombatEffectApplicator.cs: s_hangingEffects (stored on a participant),
/// s_handledElsewhere, and the switch in ApplyEffect (done straight away). Anything else falls to its default case,
/// "not done by the server; only the game shows it".</item>
/// <item>CombatCharms.cs, CombatWards.cs, CombatCriticals.cs, CombatResolver.cs and CombatEffectApplicator.cs read
/// the stored effects back; a stored effect none of them reads (s_storedOnly here) is put on but does nothing.</item>
/// <item>CombatEffectApplicator.GetGlobalEffect and friends: the bubble (kGlobal) types whose numbers are used.</item>
/// <item>CombatEnchantments.cs, IsCardModifier: the card changes an enchantment can make.</item>
/// </list>
/// When the server learns a new effect, move it here from "not done" to the right list.
/// </remarks>
internal static partial class SpellSupport {

    // CombatEffectApplicator.ApplyEffect's switch: done straight away on the targets.
    private static readonly HashSet<kSpellEffects> s_applied = [
        kSpellEffects.kDamage, kSpellEffects.kDamagePerTotalPipPower, kSpellEffects.kDamageNoCrit, kSpellEffects.kDivideDamage,
        kSpellEffects.kDamageOverTime, kSpellEffects.kDeferredDamage, kSpellEffects.kHeal, kSpellEffects.kHealOverTime,
        kSpellEffects.kHealPercent, kSpellEffects.kMaxHealthHeal, kSpellEffects.kSetHealPercent, kSpellEffects.kStealHealth,
        kSpellEffects.kStun, kSpellEffects.kSummonCreature, kSpellEffects.kSpawnCreature, kSpellEffects.kInstantKill,
        kSpellEffects.kKillCreature, kSpellEffects.kModifyPips, kSpellEffects.kModifyPowerPips, kSpellEffects.kModifyShadowPips,
        kSpellEffects.kModifySchoolPips, kSpellEffects.kMaxHealthDamage, kSpellEffects.kReshuffle, kSpellEffects.kRemoveCharm,
        kSpellEffects.kRemoveWard, kSpellEffects.kStealCharm, kSpellEffects.kStealWard, kSpellEffects.kDetonateOverTime,
        kSpellEffects.kRemoveOverTime, kSpellEffects.kRemoveAura, kSpellEffects.kPushCharm, kSpellEffects.kPushWard,
        kSpellEffects.kPushOverTime, kSpellEffects.kStealOverTime, kSpellEffects.kSwapCharm, kSpellEffects.kSwapWard,
        kSpellEffects.kSwapOverTime, kSpellEffects.kSwapAll, kSpellEffects.kModifyOverTimeDuration,
    ];

    // CombatEffectApplicator.s_hangingEffects that something in the combat code reads back when it matters.
    private static readonly HashSet<kSpellEffects> s_storedAndUsed = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyIncomingDamage, kSpellEffects.kDispel,
        kSpellEffects.kModifyAccuracy, kSpellEffects.kModifyOutgoingHeal, kSpellEffects.kModifyOutgoingHealFlat,
        kSpellEffects.kModifyIncomingHeal, kSpellEffects.kModifyIncomingHealFlat, kSpellEffects.kModifyIncomingDamageType,
        kSpellEffects.kAbsorbDamage, kSpellEffects.kCritBoost, kSpellEffects.kCritBlock, kSpellEffects.kModifyOutgoingArmorPiercing,
        kSpellEffects.kModifyOutgoingDamageFlat, kSpellEffects.kModifyIncomingDamageFlat, kSpellEffects.kStunBlock,
        kSpellEffects.kMaximumIncomingDamage,
    ];

    // CombatEffectApplicator.s_hangingEffects that are put on the participant (the game shows them, and they can be
    // removed or stolen) but that no server code reads, so their number changes nothing.
    private static readonly HashSet<kSpellEffects> s_storedOnly = [
        kSpellEffects.kModifyIncomingArmorPiercing, kSpellEffects.kModifyPowerPipChance, kSpellEffects.kPipConversion,
        kSpellEffects.kStunResist, kSpellEffects.kDispelBlock, kSpellEffects.kConfusionBlock,
        kSpellEffects.kModifyIncomingDamageOverTime, kSpellEffects.kModifyIncomingHealOverTime,
        kSpellEffects.kModifyOutgoingStealHealth, kSpellEffects.kModifyIncomingStealHealth,
        kSpellEffects.kCloakedCharm, kSpellEffects.kCloakedWard, kSpellEffects.kCloakedWardNoRemove,
    ];

    // CombatEffectApplicator.s_handledElsewhere (less kInvalidSpellEffect, which does nothing on its own, and the card
    // changes, which are below).
    private static readonly HashSet<kSpellEffects> s_elsewhere = [
        kSpellEffects.kPacify, kSpellEffects.kTaunt, kSpellEffects.kModifyHate, kSpellEffects.kPolymorph, kSpellEffects.kAfterlife,
    ];

    // CombatEnchantments.IsCardModifier: what an enchantment does to the card it is played on.
    private static readonly HashSet<kSpellEffects> s_cardChanges = [
        kSpellEffects.kModifyCardCloak, kSpellEffects.kModifyCardDamage, kSpellEffects.kModifyCardAccuracy,
        kSpellEffects.kModifyCardMutation, kSpellEffects.kModifyCardRank, kSpellEffects.kModifyCardArmorPiercing,
        kSpellEffects.kModifyCardHeal, kSpellEffects.kModifyCardCharm, kSpellEffects.kModifyCardWard,
        kSpellEffects.kModifyCardOutgoingDamage, kSpellEffects.kModifyCardOutgoingAccuracy, kSpellEffects.kModifyCardOutgoingHeal,
        kSpellEffects.kModifyCardOutgoingArmorPiercing, kSpellEffects.kModifyCardIncomingDamage,
        kSpellEffects.kModifyCardAbsorbDamage, kSpellEffects.kModifyCardDamagebyRank,
        kSpellEffects.kProtectCardBeneficial, kSpellEffects.kProtectCardHarmful,
    ];

    // Bubbles (kGlobal) whose numbers the server reads: GetGlobalEffectDamageModifier, the pierce in
    // ApplyFlatDamageEffect, GetGlobalEffectHealingModifier, CapDamage, and the accuracy in CombatResolver.
    private static readonly HashSet<kSpellEffects> s_bubblesUsed = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyOutgoingArmorPiercing, kSpellEffects.kModifyOutgoingHeal,
        kSpellEffects.kMaximumIncomingDamage, kSpellEffects.kModifyAccuracy,
    ];

    // Done, but not the way the game does it.
    private static readonly Dictionary<kSpellEffects, string> s_approximate = new() {
        [kSpellEffects.kDamagePerTotalPipPower] = "damage for each pip (done as plain damage)",
    };

    // Spell kinds the server has no code for at all (cantrips have CantripService, and combat cards the combat code).
    private static readonly Dictionary<string, string> s_noServerCode = new(StringComparer.Ordinal) {
        ["GardenSpellTemplate"] = "A gardening spell. The server does not do gardening, so it can't be cast.",
        ["FishingSpellTemplate"] = "A fishing spell. The server does not do fishing, so it can't be cast.",
        ["CastleMagicSpellTemplate"] = "A Castle Magic spell. The server does not do Castle Magic, so it can't be cast.",
        ["WhirlyBurlySpellTemplate"] = "A Whirly Burly game card. The server does not run Whirly Burly, so it can't be played.",
    };

    [GeneratedRegex(@"(?i)\b(test|tests|testing|fortesting|qa|placeholder|dummy|debug|unused|deprecated|do not use|dnu|tbd|todo|temp)\b")]
    private static partial Regex TestWords();

    // ---------------------------------------------------------------------
    // Works, partly or broken
    // ---------------------------------------------------------------------

    private sealed class Tally {
        internal int Done;
        internal readonly List<string> Skipped = [];
        internal int Nothing;
    }

    /// <summary>
    /// Whether the server does this spell: Works, Partly (with the parts it skips) or Broken (with why).
    /// </summary>
    internal static SpellSupportResult Classify(SpellTemplate spell, string code, string file) {
        var family = spell.GetType().Name;
        if (s_noServerCode.TryGetValue(family, out var none)) {
            return new SpellSupportResult(SpellStatus.Broken, none, []);
        }

        if (family == "CantripsSpellTemplate") {
            return new SpellSupportResult(SpellStatus.Works, "", []);
        }

        if (TestWords().IsMatch(code) || TestWords().IsMatch(file)) {
            return new SpellSupportResult(SpellStatus.Broken,
                "A test or placeholder spell (its name in the game files says so). It is not a real card.", []);
        }

        if (spell.m_alwaysFizzle) {
            return new SpellSupportResult(SpellStatus.Broken, "The game file marks it to always fizzle, so it never does anything.", []);
        }

        var top = (spell.m_effects ?? []).Where(e => e is not null).ToList();
        if (top.Count == 0) {
            return new SpellSupportResult(SpellStatus.Broken, "It has no effects, so casting it does nothing.", []);
        }

        var all = new Tally();
        Tally? main = null;
        foreach (var effect in top) {
            var tally = new Tally();
            Walk(effect, tally, 0, enchantment: false);
            all.Done += tally.Done;
            all.Nothing += tally.Nothing;
            all.Skipped.AddRange(tally.Skipped);
            // The first part that does something is the main one; a polymorph's "end any polymorph first" doesn't count.
            var prefix = effect.GetType() == typeof(SpellEffect) && effect.m_effectType == kSpellEffects.kUnPolymorph && top.Count > 1;
            if (main is null && !prefix && (tally.Done > 0 || tally.Skipped.Count > 0)) {
                main = tally;
            }
        }

        var skipped = all.Skipped.Distinct(StringComparer.Ordinal).ToList();
        if (main is null) {
            return new SpellSupportResult(SpellStatus.Broken,
                "It only plays an animation: its effects are blank in the game files, so it does nothing in battle.", []);
        }

        if (all.Done == 0) {
            return new SpellSupportResult(SpellStatus.Broken,
                "The server does none of what it says: " + string.Join("; ", skipped) + ".", skipped);
        }

        if (main.Done == 0) {
            return new SpellSupportResult(SpellStatus.Broken,
                "The server does not do its main effect: " + string.Join("; ", main.Skipped.Distinct()) + ".", skipped);
        }

        return skipped.Count > 0
            ? new SpellSupportResult(SpellStatus.Partly, "", skipped)
            : new SpellSupportResult(SpellStatus.Works, "", []);
    }

    // As CombatActionResolver.Resolve walks a spell.
    private static void Walk(SpellEffect? effect, Tally tally, int depth, bool enchantment) {
        if (effect is null || depth > 10) {
            return;
        }

        switch (effect) {
            // Applied straight away for each target (ApplyRandomPerTargetEffect), without looking inside.
            case RandomPerTargetSpellEffect perTarget when perTarget.m_effectList is { Count: > 0 }:
                foreach (var child in perTarget.m_effectList) {
                    Leaf(child, tally);
                }

                return;
            case RandomSpellEffect random:
                if (random.m_effectList is not { Count: > 0 }) {
                    tally.Skipped.Add("a random effect with nothing to pick from");
                }

                foreach (var child in random.m_effectList ?? []) {
                    Walk(child, tally, depth + 1, enchantment);
                }

                return;
            case VariableSpellEffect variable:
                if (variable.m_effectList is not { Count: > 0 }) {
                    tally.Skipped.Add("an X-pip effect with no pip tiers");
                }

                foreach (var child in variable.m_effectList ?? []) {
                    Walk(child, tally, depth + 1, enchantment);
                }

                return;
            case ConditionalSpellEffect conditional:
                var elements = (conditional.m_elements ?? []).Where(e => e?.m_pEffect is not null).ToList();
                if (elements.Count == 0) {
                    tally.Skipped.Add("an \"if\" effect with nothing in it");
                }

                foreach (var element in elements) {
                    Walk(element.m_pEffect, tally, depth + 1, enchantment);
                }

                return;
            case TargetCountSpellEffect targetCount:
                var lists = (targetCount.m_effectLists ?? []).Where(l => l is not null).ToList();
                if (lists.Count == 0) {
                    tally.Skipped.Add("a \"by number of enemies\" effect with nothing in it");
                }

                foreach (var list in lists) {
                    Walk(list, tally, depth + 1, enchantment);
                }

                return;
            case HangingConversionSpellEffect:
                tally.Skipped.Add("turning charms or wards into other effects (hanging conversion)");
                return;
            case DelaySpellEffect:
                tally.Skipped.Add("a delayed effect that goes off on a later round");
                return;
            case CountBasedSpellEffect:
                tally.Skipped.Add("an effect that counts cards or effects first (count-based)");
                return;
            case ShadowPactSpellEffect:
                tally.Skipped.Add("the shadow pact (shadow creature)");
                return;
        }

        if (effect.m_effectType == kSpellEffects.kAfterlife) {
            tally.Done++;
            return;
        }

        if (effect is EffectListSpellEffect effects) {
            foreach (var inner in effects.m_effectList ?? []) {
                Walk(inner, tally, depth + 1, enchantment);
            }

            return;
        }

        Leaf(effect, tally);
    }

    private static void Leaf(SpellEffect? effect, Tally tally) {
        if (effect is null) {
            return;
        }

        var type = effect.m_effectType;
        if (type == kSpellEffects.kInvalidSpellEffect) {
            tally.Nothing++;
            return;
        }

        // A card change: an enchantment does it when played onto a card (CombatEnchantments); cast on its own the
        // resolver skips it.
        if (effect.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells || s_cardChanges.Contains(type)) {
            if (s_cardChanges.Contains(type)) {
                tally.Done++;
            }
            else {
                tally.Skipped.Add($"{Name(type)} (aimed at a card, which the server skips)");
            }

            return;
        }

        if (effect.m_effectTarget == kEffectTarget.kGlobal) {
            if (s_bubblesUsed.Contains(type)) {
                tally.Done++;
            }
            else {
                tally.Skipped.Add($"{Name(type)} as a bubble (the game shows it, but the server ignores its number)");
            }

            return;
        }

        if (s_approximate.TryGetValue(type, out var rough)) {
            tally.Done++;
            tally.Skipped.Add(rough);
            return;
        }

        if (s_applied.Contains(type) || s_storedAndUsed.Contains(type) || s_elsewhere.Contains(type)) {
            tally.Done++;
            return;
        }

        tally.Skipped.Add(s_storedOnly.Contains(type)
            ? $"{Name(type)} (it is put on, but the server never uses it)"
            : Name(type));
    }

    /// <summary>A plain name for an effect type, for the skipped-parts list.</summary>
    private static string Name(kSpellEffects type) => type switch {
        kSpellEffects.kConfusion => "confusion",
        kSpellEffects.kMindControl => "beguile (taking control of an enemy)",
        kSpellEffects.kAddSpellToHand => "adding a card to the hand",
        kSpellEffects.kAddSpellToDeck => "adding a card to the deck",
        kSpellEffects.kIntercept => "intercept",
        kSpellEffects.kShadowSelf => "shadow self",
        kSpellEffects.kShadowCreature => "a shadow creature",
        kSpellEffects.kReduceOverTime => "cooldown (shortening over-time effects)",
        kSpellEffects.kCritBoostSchoolSpecific => "a school's critical boost",
        kSpellEffects.kCollectEssence => "collecting essence",
        kSpellEffects.kAddCombatTriggerList => "battle triggers",
        kSpellEffects.kRemoveCombatTriggerList => "removing battle triggers",
        kSpellEffects.kModifyOutgoingDamageType => "changing an attack's school",
        kSpellEffects.kDampen => "dampen",
        kSpellEffects.kModifyPipRoundRate => "pips gained each round",
        kSpellEffects.kUnPolymorph => "ending a polymorph",
        kSpellEffects.kMinionRankDamage => "damage by minion rank",
        kSpellEffects.kUntargetable or kSpellEffects.kMakeTargetable or kSpellEffects.kForceTargetable
            or kSpellEffects.kRemoveTargetRestriction => "who can be targeted",
        kSpellEffects.kVanish or kSpellEffects.kStopVanish => "vanishing",
        kSpellEffects.kAutoPass or kSpellEffects.kStopAutoPass => "passing turns by itself",
        kSpellEffects.kSuspendPips or kSpellEffects.kResumePips => "stopping pips",
        kSpellEffects.kHealByWard => "healing by wards",
        kSpellEffects.kBacklashDamage or kSpellEffects.kModifyBacklash => "backlash",
        kSpellEffects.kModifyIncomingArmorPiercing => "a pierce trap",
        kSpellEffects.kModifyPowerPipChance => "power pip chance",
        kSpellEffects.kPipConversion or kSpellEffects.kPowerPipConversion => "pip conversion",
        kSpellEffects.kStunResist => "stun resist",
        kSpellEffects.kDispelBlock => "dispel block",
        kSpellEffects.kConfusionBlock => "confusion block",
        kSpellEffects.kModifyIncomingDamageOverTime => "a damage-over-time shield",
        kSpellEffects.kModifyIncomingHealOverTime => "a heal-over-time change",
        kSpellEffects.kModifyOutgoingStealHealth or kSpellEffects.kModifyIncomingStealHealth => "a drain change",
        kSpellEffects.kCloakedCharm => "a cloaked charm",
        kSpellEffects.kCloakedWard or kSpellEffects.kCloakedWardNoRemove => "a cloaked ward",
        kSpellEffects.kExitCombat => "leaving the battle",
        kSpellEffects.kTeleportPlayer => "teleporting",
        kSpellEffects.kDelayCast => "a delayed cast",
        kSpellEffects.kAbsorbHeal => "absorbing heals",
        kSpellEffects.kRevealCloak => "revealing cloaked effects",
        kSpellEffects.kClue => "a clue",
        _ => Words(type.ToString()).ToLowerInvariant(),
    };

    /// <summary>"kModifyIncomingDamage" as "Modify Incoming Damage".</summary>
    internal static string Words(string name) {
        if (name.Length > 1 && name[0] == 'k' && char.IsUpper(name[1])) {
            name = name[1..];
        }

        return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()))
            .Replace('_', ' ');
    }

}
