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
using System.Globalization;
using System.Linq;
using System.Text;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.Setup;

/// <summary>
/// Plain-English descriptions written from a spell's effects, such as "Deals 450-520 Fire damage to one enemy, then
/// 120 Fire damage over 3 rounds", for cards whose game text is blank.
/// </summary>
internal static partial class SpellSupport {

    // One part of a description: the words, and the "to one enemy" ending that a following part with the same
    // ending leaves out.
    private sealed record Clause(string Text, string Suffix = "");

    private const int MAX_LISTED = 5;
    private const int MAX_LENGTH = 420;

    /// <summary>
    /// What the spell does, in plain words, from its effects; "" when there is nothing to say.
    /// </summary>
    internal static string Describe(SpellTemplate spell, string school) {
        var family = spell.GetType().Name;
        var top = (spell.m_effects ?? []).Where(e => e is not null).ToList();
        if (top.Count == 0) {
            return family switch {
                "GardenSpellTemplate" => "A gardening spell, cast on the plants in a house garden.",
                "FishingSpellTemplate" => "A fishing spell, cast while fishing.",
                "CastleMagicSpellTemplate" => "A Castle Magic spell, cast in a house.",
                "CantripsSpellTemplate" => "A cantrip: a spell used outside battle.",
                "WhirlyBurlySpellTemplate" => "A Whirly Burly game card.",
                _ => "Does nothing in battle: the card has no effects.",
            };
        }

        var clauses = new List<Clause>();
        foreach (var effect in top) {
            clauses.AddRange(Clauses(effect, school, 0));
        }

        var text = Join(clauses);
        if (text.Length > MAX_LENGTH) {
            // Boss spells can go on for pages; the first parts say enough.
            var cut = text.LastIndexOf(", then ", MAX_LENGTH, StringComparison.Ordinal);
            text = (cut > 40 ? text[..cut] : text[..MAX_LENGTH].TrimEnd()) + ", and more";
        }

        if (text.Length == 0) {
            return "Plays an animation only: its effects are blank in the game files, so it does nothing in battle.";
        }

        var enchantment = Leaves(top).ToList() is { Count: > 0 } leaves && leaves.All(l => s_cardChanges.Contains(l.m_effectType));

        return (enchantment ? "Played on another card: " + Lower(text) : text) + ".";
    }

    private static IEnumerable<SpellEffect> Leaves(IEnumerable<SpellEffect?> effects) {
        foreach (var effect in effects) {
            var inside = Inside(effect);
            if (inside is null) {
                if (effect is not null && effect.m_effectType != kSpellEffects.kInvalidSpellEffect) {
                    yield return effect;
                }

                continue;
            }

            foreach (var leaf in Leaves(inside)) {
                yield return leaf;
            }
        }
    }

    private static IEnumerable<SpellEffect?>? Inside(SpellEffect? effect) => effect switch {
        RandomSpellEffect random => random.m_effectList,
        VariableSpellEffect variable => variable.m_effectList,
        EffectListSpellEffect list => list.m_effectList,
        CountBasedSpellEffect count => count.m_effectList,
        ConditionalSpellEffect conditional => conditional.m_elements?.Select(e => e?.m_pEffect),
        TargetCountSpellEffect targets => targets.m_effectLists,
        HangingConversionSpellEffect conversion => conversion.m_outputEffect,
        _ => null,
    };

    private static string Join(List<Clause> clauses) {
        // The same part twice or more in a row ("calls a creature into the battle" three times) reads once, counted.
        var grouped = new List<Clause>();
        var repeats = 1;
        foreach (var clause in clauses.Where(c => c.Text.Length > 0).Append(new Clause(""))) {
            if (grouped.Count > 0 && clause == grouped[^1]) {
                repeats++;
                continue;
            }

            if (repeats > 1) {
                grouped[^1] = new Clause(grouped[^1].Text + grouped[^1].Suffix + $" ({repeats} times)");
            }

            repeats = 1;
            if (clause.Text.Length > 0) {
                grouped.Add(clause);
            }
        }

        var text = new StringBuilder();
        Clause? last = null;
        foreach (var clause in grouped) {
            var words = clause.Text;
            if (last is not null) {
                // "Deals 450 Fire damage to one enemy, then 120 Fire damage over 3 rounds".
                if (words.StartsWith("Deals ", StringComparison.Ordinal) && last.Text.StartsWith("Deals ", StringComparison.Ordinal)) {
                    words = words[6..];
                }

                text.Append(", then ").Append(Lower(words));
            }
            else {
                text.Append(words);
            }

            if (clause.Suffix.Length > 0 && clause.Suffix != last?.Suffix) {
                text.Append(clause.Suffix);
            }

            last = clause;
        }

        return text.ToString();
    }

    private static string Lower(string text)
        => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static string Upper(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static IEnumerable<Clause> Clauses(SpellEffect? effect, string school, int depth) {
        if (effect is null || depth > 8) {
            return [];
        }

        switch (effect) {
            case RandomPerTargetSpellEffect perTarget when perTarget.m_effectList is { Count: > 0 }:
                return Choices(perTarget.m_effectList, school, depth, "each target gets one of these at random: ");
            case RandomSpellEffect random:
                return Choices(random.m_effectList ?? [], school, depth, "one of these at random: ");
            case VariableSpellEffect variable:
                return PerPip(variable.m_effectList ?? [], school, depth);
            case ConditionalSpellEffect conditional:
                return Conditional(conditional, school, depth);
            case TargetCountSpellEffect targetCount:
                return ByEnemies(targetCount, school, depth);
            case HangingConversionSpellEffect:
                return [new Clause("Turns charms or wards into other effects")];
            case DelaySpellEffect:
                return [new Clause("Goes off a few rounds later")];
            case ShadowPactSpellEffect:
                return [new Clause("Calls on a shadow creature")];
            case CountBasedSpellEffect count:
                var inside = (count.m_effectList ?? []).SelectMany(e => Clauses(e, school, depth + 1)).ToList();

                return inside.Count == 0 ? [] : [new Clause("Depending on how many there are: " + Lower(Join(inside)))];
            case EffectListSpellEffect list:
                return (list.m_effectList ?? []).SelectMany(e => Clauses(e, school, depth + 1)).ToList();
        }

        return Phrase(effect, effect.m_effectParam, effect.m_effectParam, school) is { } clause ? [clause] : [];
    }

    // Plain effects that differ only in their number (Fire Cat's 80, 90 ... 120) read as one, with a range.
    private static bool SameButNumber(IReadOnlyList<SpellEffect?> effects)
        => effects.Count > 0 && effects.All(e => e is not null && e.GetType() == typeof(SpellEffect))
           && effects.Select(e => (e!.m_effectType, e.m_effectTarget, e.m_sDamageType.ToString(), e.m_numRounds, e.m_disposition, e.m_healModifier))
               .Distinct().Count() == 1;

    private static IEnumerable<Clause> Choices(IReadOnlyList<SpellEffect?> options, string school, int depth, string lead) {
        if (options.Count == 0) {
            return [];
        }

        if (SameButNumber(options)) {
            var first = options[0]!;
            var low = options.Min(e => e!.m_effectParam);
            var high = options.Max(e => e!.m_effectParam);

            return Phrase(first, low, high, school) is { } clause ? [clause] : [];
        }

        var each = options.Select(o => Join([.. Clauses(o, school, depth + 1)])).Where(t => t.Length > 0).Distinct().ToList();
        if (each.Count == 0) {
            return [];
        }

        if (each.Count == 1) {
            return [new Clause(each[0])];
        }

        var listed = string.Join("; or ", each.Take(MAX_LISTED).Select(Lower));
        if (each.Count > MAX_LISTED) {
            listed += $"; or one of {each.Count - MAX_LISTED} more";
        }

        return [new Clause(Upper(lead) + listed)];
    }

    private static IEnumerable<Clause> PerPip(IReadOnlyList<SpellEffect?> tiers, string school, int depth) {
        var sorted = tiers.Where(t => t is not null).OrderBy(t => t!.m_pipNum).ToList();
        if (sorted.Count == 0) {
            return [];
        }

        if (SameButNumber(sorted)) {
            var first = sorted[0]!;
            var pips = sorted.Select((t, i) => t!.m_pipNum > 0 ? t.m_pipNum : i + 1).ToList();
            var amounts = sorted.Select(t => t!.m_effectParam).ToList();
            var perPip = amounts[0] / Math.Max(1, pips[0]);
            var maxPips = pips[^1];

            // A summon or the like shows no number, so more pips change nothing it says.
            if (Phrase(first, amounts.Min(), amounts.Min(), school) == Phrase(first, amounts.Max(), amounts.Max(), school)) {
                return Phrase(first, amounts[0], amounts[0], school) is { } same ? [same] : [];
            }
            if (perPip != 0 && amounts.Select((a, i) => a == perPip * pips[i]).All(x => x) && Phrase(first, perPip, perPip, school) is { } each) {
                return [new Clause(each.Text + each.Suffix + $" for each pip spent (up to {maxPips} pips)")];
            }

            if (Phrase(first, amounts.Min(), amounts.Max(), school) is { } range) {
                return [new Clause(range.Text + range.Suffix + $" (more for more pips, up to {maxPips})")];
            }

            return [];
        }

        var lines = sorted.Select((t, i) => (Pips: t!.m_pipNum > 0 ? t.m_pipNum : i + 1, Text: Join([.. Clauses(t, school, depth + 1)])))
            .Where(l => l.Text.Length > 0).ToList();
        if (lines.Count == 0) {
            return [];
        }

        var shown = lines.Count <= MAX_LISTED ? lines : [lines[0], lines[^1]];

        return [new Clause("With " + string.Join("; with ", shown.Select(l => $"{Pips(l.Pips)}: {Lower(l.Text)}")))];
    }

    private static string Pips(int count) => count == 1 ? "1 pip" : $"{count} pips";

    private static IEnumerable<Clause> Conditional(ConditionalSpellEffect conditional, string school, int depth) {
        // Cases that do the same thing read as one ("if the target is Fire or the target is Ice: ...").
        var cases = new List<(List<string> Conditions, string Does)>();
        foreach (var element in conditional.m_elements ?? []) {
            if (element?.m_pEffect is null) {
                continue;
            }

            var does = Join([.. Clauses(element.m_pEffect, school, depth + 1)]);
            if (does.Length == 0) {
                continue;
            }

            var condition = Requirement(element.m_pReqs);
            if (cases.Count > 0 && cases[^1].Does == does && condition.Length > 0 && cases[^1].Conditions.Count > 0) {
                cases[^1].Conditions.Add(condition);
                continue;
            }

            cases.Add((condition.Length == 0 ? [] : [condition], does));
        }

        var parts = new List<string>();
        foreach (var (conditions, does) in cases.Take(MAX_LISTED)) {
            var shown = conditions.Distinct().ToList();
            var when = shown.Count > 3 ? string.Join(" or ", shown.Take(3)) + $" (or {shown.Count - 3} more)" : string.Join(" or ", shown);
            parts.Add(when.Length == 0
                ? (parts.Count == 0 ? does : "otherwise " + Lower(does))
                : $"if {when}: {Lower(does)}");
        }

        if (cases.Count > MAX_LISTED) {
            parts.Add($"and {cases.Count - MAX_LISTED} more cases");
        }

        return parts.Count == 0 ? [] : [new Clause(Upper(string.Join(". ", parts.Select((p, i) => i == 0 ? p : Upper(p)))))];
    }

    private static IEnumerable<Clause> ByEnemies(TargetCountSpellEffect targetCount, string school, int depth) {
        var lists = (targetCount.m_effectLists ?? []).Where(l => l is not null).ToList();
        var lines = lists.Select((l, i) => (Count: i + 1, Text: Join([.. Clauses(l, school, depth + 1)]))).Where(l => l.Text.Length > 0).ToList();
        if (lines.Count == 0) {
            return [];
        }

        if (lines.Select(l => l.Text).Distinct().Count() == 1) {
            return [new Clause(lines[0].Text)];
        }

        var shown = lines.Count <= 4 ? lines : [lines[0], lines[^1]];

        return [new Clause("With " + string.Join("; with ", shown.Select(l =>
            $"{l.Count} {(l.Count == 1 ? "enemy" : "enemies")} standing{(l.Count == lists.Count && l.Count > 1 ? " or more" : "")}: {Lower(l.Text)}")))];
    }

    // ---------------------------------------------------------------------
    // Requirements ("if the target is Fire")
    // ---------------------------------------------------------------------

    private static string Requirement(Requirement? requirement) {
        if (requirement is null) {
            return "";
        }

        if (requirement is RequirementList list) {
            var text = new StringBuilder();
            var said = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inner in list.m_requirements ?? []) {
                var words = Requirement(inner);
                if (words.Length == 0 || !said.Add(words)) {
                    continue;
                }

                if (text.Length > 0) {
                    text.Append(inner!.m_operator == Operator.ROP_OR ? " or " : " and ");
                }

                text.Append(words);
            }

            return list.m_applyNOT && text.Length > 0 ? $"not ({text})" : text.ToString();
        }

        var not = requirement.m_applyNOT;
        var you = requirement is ConditionalSpellEffectRequirement { m_targetType: RequirementTarget.RT_Caster };
        var who = you ? "you" : "the target";
        var has = you ? (not ? "you have no" : "you have") : (not ? "the target has no" : "the target has");
        string Count(int min, int max, string one, string many)
            => min > 1 && max >= min && max != min ? $"{min} to {max} {many}" : min > 1 ? $"at least {min} {many}" : (not ? many : "a " + one);

        return requirement switch {
            ReqIsSchool isSchool => $"{who} {(you ? "are" : "is")}{(not ? " not" : "")} {isSchool.m_magicSchoolName}",
            ReqIsSecondarySchool => $"{who} {(you ? "have" : "has")}{(not ? " not" : "")} the right second school",
            ReqHangingWard ward => $"{has} {Count(ward.m_minCount, ward.m_maxCount, Kind(ward.m_disposition, "shield", "trap", "ward"), Kind(ward.m_disposition, "shields", "traps", "wards"))}",
            ReqHangingCharm charm => $"{has} {Count(charm.m_minCount, charm.m_maxCount, Kind(charm.m_disposition, "blade", "weakness", "charm"), Kind(charm.m_disposition, "blades", "weaknesses", "charms"))}",
            ReqHangingOverTime overTime => $"{has} {Count(overTime.m_minCount, overTime.m_maxCount, Kind(overTime.m_disposition, "heal over time", "damage over time", "over-time effect"), Kind(overTime.m_disposition, "heals over time", "damage-over-time effects", "over-time effects"))}",
            ReqHangingAura aura => aura.m_globalEffect ? (not ? "no bubble is up" : "a bubble is up") : $"{has} {(not ? "aura" : "an aura")}",
            ReqHangingEffectType effectType => $"{has} {EffectKind(effectType.m_effectType, not)}",
            ReqPvPCombat => not ? "not in PvP" : "in PvP",
            ReqMinion minion => minion.m_minionType switch {
                MinionType.Is_Minion => $"{who} {(you ? "are" : "is")}{(not ? " not" : "")} a minion",
                MinionType.On_Team => not ? "your team has no minion" : "your team has a minion",
                MinionType.On_Other_Team => not ? "the enemies have no minion" : "the enemies have a minion",
                _ => $"{has} {(not ? "minion" : "a minion")}",
            },
            ReqCombatHealth health => $"{who}{(you ? "r" : "'s")} health is{(not ? " not" : "")} between {health.m_fMinPercent * 100:0}% and {health.m_fMaxPercent * 100:0}%",
            ReqCombatStatus status => $"{who} {(you ? "are" : "is")}{(not ? " not" : "")} {(status.m_status == kStatusEffect.kConfused ? "confused" : "stunned")}",
            ReqPipCount pips => $"{who} {(you ? "have" : "has")}{(not ? " not" : "")} {pips.m_minPips}{(pips.m_maxPips > pips.m_minPips ? $" to {pips.m_maxPips}" : " or more")} pips",
            ReqShadowPipCount => $"{who} {(you ? "have" : "has")}{(not ? " no" : "")} shadow pips",
            _ => "a condition is met",
        };
    }

    private static string EffectKind(kSpellEffects type, bool none) {
        var kind = type switch {
            kSpellEffects.kDamageOverTime => "damage over time",
            kSpellEffects.kHealOverTime => "heal over time",
            kSpellEffects.kModifyIncomingDamage => "shield or trap",
            kSpellEffects.kModifyOutgoingDamage => "blade or weakness",
            kSpellEffects.kAbsorbDamage => "absorb shield",
            kSpellEffects.kStunBlock => "stun block",
            kSpellEffects.kModifyAccuracy => "accuracy charm",
            kSpellEffects.kModifyOutgoingArmorPiercing => "pierce charm",
            kSpellEffects.kDeferredDamage => "bomb",
            _ => "effect of the right kind",
        };

        return none ? kind : (kind[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + kind;
    }

    private static string Kind(kHangingDisposition disposition, string good, string bad, string either) => disposition switch {
        kHangingDisposition.kBeneficial => good,
        kHangingDisposition.kHarmful => bad,
        _ => either,
    };

    // ---------------------------------------------------------------------
    // One effect in words
    // ---------------------------------------------------------------------

    private static string Who(kEffectTarget target) => target switch {
        kEffectTarget.kEnemySingle or kEffectTarget.kPreselectedEnemySingle or kEffectTarget.kAtLeastOneEnemy => "one enemy",
        kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce => "all enemies",
        kEffectTarget.kMultiTargetEnemy => "several enemies",
        kEffectTarget.kFriendlySingle => "one ally",
        kEffectTarget.kFriendlySingleNotMe => "one ally (not you)",
        kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce => "your whole team",
        kEffectTarget.kMultiTargetFriendly => "several allies",
        kEffectTarget.kMinion or kEffectTarget.kFriendlyMinion or kEffectTarget.kCasterMinion => "your minion",
        kEffectTarget.kEnemyMinion => "an enemy minion",
        kEffectTarget.kTargetMinion => "a minion",
        kEffectTarget.kGlobal => "everyone",
        kEffectTarget.kSpell or kEffectTarget.kSpecificSpells => "the card",
        _ => "you",
    };

    private static string Whose(kEffectTarget target) => target switch {
        kEffectTarget.kEnemySingle or kEffectTarget.kPreselectedEnemySingle or kEffectTarget.kAtLeastOneEnemy => "an enemy's",
        kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kMultiTargetEnemy => "every enemy's",
        kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe => "an ally's",
        kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kMultiTargetFriendly => "your whole team's",
        kEffectTarget.kMinion or kEffectTarget.kFriendlyMinion or kEffectTarget.kCasterMinion => "your minion's",
        kEffectTarget.kEnemyMinion or kEffectTarget.kTargetMinion => "a minion's",
        kEffectTarget.kGlobal => "everyone's",
        kEffectTarget.kSpell or kEffectTarget.kSpecificSpells => "the card's",
        _ => "your",
    };

    private static string Number(int low, int high) {
        low = Math.Abs(low);
        high = Math.Abs(high);
        if (low > high) {
            (low, high) = (high, low);
        }

        return low == high ? low.ToString(CultureInfo.InvariantCulture) : $"{low}-{high}";
    }

    private static string Signed(int low, int high) => (high < 0 || (high == 0 && low < 0) ? "-" : "+") + Number(low, high);

    private static string Rounds(int rounds) => rounds == 1 ? "1 round" : $"{rounds} rounds";

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static Clause? Phrase(SpellEffect effect, int low, int high, string spellSchool) {
        var type = effect.m_effectType;
        if (type == kSpellEffects.kInvalidSpellEffect) {
            return null;
        }

        var target = effect.m_effectTarget;
        var who = Who(target);
        var whose = Whose(target);
        var to = " to " + who;
        var on = " on " + who;
        var raw = effect.m_sDamageType.ToString() ?? "";
        var all = raw.Length == 0 || raw.Equals("All", StringComparison.OrdinalIgnoreCase) || raw.Equals("Universal", StringComparison.OrdinalIgnoreCase)
                  || raw.Equals("None", StringComparison.OrdinalIgnoreCase);
        // A school named on the effect, or the card's own for an attack or heal that names none.
        var damageSchool = all ? (raw.Length == 0 ? spellSchool : "") : raw;
        var hit = all ? "" : raw + " ";
        var n = Number(low, high);
        var sign = Signed(low, high);
        var rounds = effect.m_numRounds;
        var forRounds = rounds > 0 ? $" for {Rounds(rounds)}" : "";
        var bubble = target == kEffectTarget.kGlobal;
        var dmg = damageSchool.Length > 0 ? $"{damageSchool} damage" : "damage";
        // An aura (it lasts some rounds) changes every spell for its rounds, a charm only the next one.
        var next = rounds > 0 ? "" : "next ";
        var many = rounds > 0 ? "s" : "";
        var their = who == "you" ? "your" : "their";

        Clause Say(string text, string suffix = "") => new(text, suffix);

        switch (type) {
            case kSpellEffects.kDamage:
                return Say($"Deals {n} {dmg}", to);
            case kSpellEffects.kDamageNoCrit:
                return Say($"Deals {n} {dmg} that can't be a critical hit", to);
            case kSpellEffects.kDamagePerTotalPipPower:
                return Say($"Deals {n} {dmg} for each of your pips", to);
            case kSpellEffects.kDivideDamage:
                return Say($"Deals {n} {dmg}, split among the targets", to);
            case kSpellEffects.kDamageOverTime:
                return Say($"Deals {n} {dmg} over {Rounds(Math.Max(1, rounds))}", to);
            case kSpellEffects.kDeferredDamage:
                return Say($"Plants a bomb that deals {n} {dmg} after {Rounds(Math.Max(1, rounds))}", on);
            case kSpellEffects.kMaxHealthDamage:
                return Say($"Deals {n}% of the target's max health as damage", to);
            case kSpellEffects.kMinionRankDamage:
                return Say($"Deals {n} {dmg} for each rank of your minion", to);
            case kSpellEffects.kStealHealth:
                var share = effect.m_healModifier > 0 ? $"{effect.m_healModifier * 100:0}% of it" : "some of it";

                return Say($"Deals {n} {dmg}{to} and heals you for {share}");
            case kSpellEffects.kHeal:
                return Say($"Heals {who} for {n} health");
            case kSpellEffects.kHealOverTime:
                return Say($"Heals {who} for {n} health over {Rounds(Math.Max(1, rounds))}");
            case kSpellEffects.kHealPercent:
                return Say($"Heals {who} for {n}% of {their} max health");
            case kSpellEffects.kMaxHealthHeal:
                return Say(high > 0 ? $"Heals {who} for {n}% of {their} max health" : $"Heals {who} to full health");
            case kSpellEffects.kSetHealPercent:
                return Say($"Sets {whose} health to {n}%");
            case kSpellEffects.kAbsorbDamage:
                return Say($"Gives {who} a shield that soaks up {n} damage{forRounds}");
            case kSpellEffects.kMaximumIncomingDamage:
                return Say(bubble ? $"Bubble: no one can take more than {n} damage from one hit" : $"{Upper(who)} can't take more than {n} damage from one hit{forRounds}");
            case kSpellEffects.kModifyOutgoingDamage:
                if (bubble) {
                    return Say($"Bubble: {sign}% to all {hit}damage");
                }

                return Say(rounds > 0 ? $"{sign}% to {whose} {hit}damage for {Rounds(rounds)}" : $"{sign}% to {whose} next {hit}attack");
            case kSpellEffects.kModifyOutgoingDamageFlat:
                return Say($"{sign} damage on {whose} {next}{hit}attack{many}{forRounds}");
            case kSpellEffects.kModifyIncomingDamage:
                if (rounds > 0) {
                    return Say($"{sign}% to the {hit}damage {who} take{(who == "you" ? "" : "s")} for {Rounds(rounds)}");
                }

                return Say(high < 0 || low < 0 ? $"{sign}% to the next {hit}attack{on}" : $"{sign}% to the next {hit}hit{on}");
            case kSpellEffects.kModifyIncomingDamageFlat:
                return Say($"{sign} damage to the next {hit}hit{on}{forRounds}");
            case kSpellEffects.kModifyIncomingDamageType:
                return Say($"Turns the next {hit}attack{on} into the opposite school");
            case kSpellEffects.kModifyOutgoingDamageType:
                return Say($"Turns {whose} next attack into {(all ? "another school" : raw)}");
            case kSpellEffects.kModifyAccuracy:
                return Say(bubble ? $"Bubble: {sign}% accuracy on all {hit}spells" : $"{sign}% accuracy on {whose} {next}{hit}spell{many}{forRounds}");
            case kSpellEffects.kModifyOutgoingArmorPiercing:
                return Say(bubble ? $"Bubble: {sign}% pierce on all {hit}attacks" : $"{sign}% pierce on {whose} {next}{hit}attack{many}{forRounds}");
            case kSpellEffects.kModifyIncomingArmorPiercing:
                return Say($"{sign}% pierce for attacks{on}{forRounds}");
            case kSpellEffects.kModifyOutgoingHeal:
                return Say(bubble ? $"Bubble: {sign}% to all heals" : $"{sign}% to {whose} {next}heal{many}{forRounds}");
            case kSpellEffects.kModifyOutgoingHealFlat:
                return Say($"{sign} health on {whose} {next}heal{many}{forRounds}");
            case kSpellEffects.kModifyIncomingHeal:
                return Say($"{sign}% to the {next}heal{many}{on}{forRounds}");
            case kSpellEffects.kModifyIncomingHealFlat:
                return Say($"{sign} health on the {next}heal{many}{on}{forRounds}");
            case kSpellEffects.kModifyIncomingDamageOverTime:
                return Say($"{sign}% to damage over time{on}{forRounds}");
            case kSpellEffects.kModifyIncomingHealOverTime:
                return Say($"{sign}% to heals over time{on}{forRounds}");
            case kSpellEffects.kModifyOutgoingStealHealth:
            case kSpellEffects.kModifyIncomingStealHealth:
                return Say($"{sign}% to health drained{on}{forRounds}");
            case kSpellEffects.kCritBoost:
                return Say($"{sign}% critical on {whose} {next}{hit}spell{many}{forRounds}");
            case kSpellEffects.kCritBlock:
                return Say($"{sign}% critical block for {who}{forRounds}");
            case kSpellEffects.kCritBoostSchoolSpecific:
                return Say($"{sign}% critical for {hit}spells{forRounds}");
            case kSpellEffects.kDispel:
                return Say($"Makes {whose} next {hit}spell fizzle");
            case kSpellEffects.kStun:
                var stunRounds = high > 0 ? high : Math.Max(1, rounds);

                return Say($"Stuns {who} for {Rounds(stunRounds)}");
            case kSpellEffects.kStunBlock:
                return Say($"Gives {who} {Plural(Math.Max(1, high), "stun block", "stun blocks")}");
            case kSpellEffects.kStunResist:
                return Say($"{sign}% stun resist for {who}{forRounds}");
            case kSpellEffects.kModifyPips:
                return Say(high >= 0 ? $"Gives {who} {n} pip{(n == "1" ? "" : "s")}" : $"Takes {n} pip{(n == "1" ? "" : "s")} from {who}");
            case kSpellEffects.kModifyPowerPips:
                return Say(high >= 0 ? $"Gives {who} {n} power pip{(n == "1" ? "" : "s")}" : $"Takes {n} power pip{(n == "1" ? "" : "s")} from {who}");
            case kSpellEffects.kModifyShadowPips:
                return Say(high >= 0 ? $"Gives {who} {n} shadow pip{(n == "1" ? "" : "s")}" : $"Takes {n} shadow pip{(n == "1" ? "" : "s")} from {who}");
            case kSpellEffects.kModifySchoolPips:
                return Say(high >= 0 ? $"Gives {who} {n} {hit}school pip{(n == "1" ? "" : "s")}" : $"Takes {n} {hit}school pip{(n == "1" ? "" : "s")} from {who}");
            case kSpellEffects.kModifyPowerPipChance:
                return Say($"{sign}% power pip chance for {who}{forRounds}");
            case kSpellEffects.kPipConversion:
            case kSpellEffects.kPowerPipConversion:
                return Say($"Turns {whose} pips into other pips");
            case kSpellEffects.kRemoveCharm:
                return Say($"Removes {Plural(Math.Max(1, high), Kind(effect.m_disposition, "blade", "weakness", "charm"), Kind(effect.m_disposition, "blades", "weaknesses", "charms"))} from {who}");
            case kSpellEffects.kRemoveWard:
                return Say($"Removes {Plural(Math.Max(1, high), Kind(effect.m_disposition, "shield", "trap", "ward"), Kind(effect.m_disposition, "shields", "traps", "wards"))} from {who}");
            case kSpellEffects.kRemoveOverTime:
                return Say($"Removes {Plural(Math.Max(1, high), Kind(effect.m_disposition, "heal over time", "damage-over-time effect", "over-time effect"), Kind(effect.m_disposition, "heals over time", "damage-over-time effects", "over-time effects"))} from {who}");
            case kSpellEffects.kRemoveAura:
                return Say($"Removes {whose} aura");
            case kSpellEffects.kStealCharm:
                return Say($"Steals {Kind(effect.m_disposition, "a blade", "a weakness", "a charm")} from {who}");
            case kSpellEffects.kStealWard:
                return Say($"Steals {Kind(effect.m_disposition, "a shield", "a trap", "a ward")} from {who}");
            case kSpellEffects.kStealOverTime:
                return Say($"Steals {Kind(effect.m_disposition, "a heal over time", "a damage-over-time effect", "an over-time effect")} from {who}");
            case kSpellEffects.kPushCharm:
                return Say($"Moves {Kind(effect.m_disposition, "a blade", "a weakness", "a charm")} from you onto {who}");
            case kSpellEffects.kPushWard:
                return Say($"Moves {Kind(effect.m_disposition, "a shield", "a trap", "a ward")} from you onto {who}");
            case kSpellEffects.kPushOverTime:
                return Say($"Moves {Kind(effect.m_disposition, "a heal over time", "a damage-over-time effect", "an over-time effect")} from you onto {who}");
            case kSpellEffects.kSwapCharm:
                return Say($"Swaps charms between you and {who}");
            case kSpellEffects.kSwapWard:
                return Say($"Swaps wards between you and {who}");
            case kSpellEffects.kSwapOverTime:
                return Say($"Swaps over-time effects between you and {who}");
            case kSpellEffects.kSwapAll:
                return Say($"Swaps all charms, wards and over-time effects between you and {who}");
            case kSpellEffects.kDetonateOverTime:
                return Say($"Sets off {whose} damage-over-time effects all at once");
            case kSpellEffects.kModifyOverTimeDuration:
                return Say($"{(high >= 0 ? "Adds" : "Takes")} {Plural(Math.Abs(high), "round", "rounds")} {(high >= 0 ? "to" : "from")} {whose} over-time effects");
            case kSpellEffects.kReduceOverTime:
                return Say($"Shortens {whose} over-time effects");
            case kSpellEffects.kSummonCreature:
                return Say("Summons a minion to fight on your side");
            case kSpellEffects.kSpawnCreature:
                return Say("Calls a creature into the battle");
            case kSpellEffects.kInstantKill:
                return Say($"Defeats {who} at once");
            case kSpellEffects.kKillCreature:
                return Say("Defeats a creature at once");
            case kSpellEffects.kReshuffle:
                return Say($"Shuffles {whose} used cards back into the deck");
            case kSpellEffects.kPolymorph:
                return Say($"Turns {who} into a creature for the battle");
            case kSpellEffects.kUnPolymorph:
                return Say($"Turns {who} back from a polymorph");
            case kSpellEffects.kPacify:
                return Say($"Pacifies {who}: no attacks{forRounds}");
            case kSpellEffects.kTaunt:
                return Say($"Makes the enemies attack {who}{forRounds}");
            case kSpellEffects.kModifyHate:
                return Say($"Changes how much monsters go after {who}");
            case kSpellEffects.kAfterlife:
                return Say($"If {who} {(who == "you" ? "are" : "is")} defeated{(rounds > 0 ? $" in the next {Rounds(rounds)}" : "")}, brings them back");
            case kSpellEffects.kConfusion:
                return Say($"Confuses {who}{forRounds}, so spells may hit the wrong side");
            case kSpellEffects.kMindControl:
                return Say($"Beguiles {who} so it fights for your side{forRounds}");
            case kSpellEffects.kAddSpellToHand:
                return Say($"Adds a card to {whose} hand");
            case kSpellEffects.kAddSpellToDeck:
                return Say($"Adds a card to {whose} deck");
            case kSpellEffects.kIntercept:
                return Say($"Takes hits meant for {who}{forRounds}");
            case kSpellEffects.kShadowSelf:
                return Say($"Gives {who} a shadow self{forRounds}");
            case kSpellEffects.kCloakedCharm:
                return Say($"Puts a hidden charm on {who}");
            case kSpellEffects.kCloakedWard:
            case kSpellEffects.kCloakedWardNoRemove:
                return Say($"Puts a hidden ward on {who}");
            case kSpellEffects.kDispelBlock:
                return Say($"Protects {who} from dispels{forRounds}");
            case kSpellEffects.kConfusionBlock:
                return Say($"Protects {who} from confusion{forRounds}");
            case kSpellEffects.kModifyPipRoundRate:
                return Say($"{(high >= 0 ? "+" : "-")}{n} pip{(n == "1" ? "" : "s")} each round for {who}{forRounds}");
            case kSpellEffects.kHealByWard:
                return Say($"Heals {who} for {n} health for each ward on them{forRounds}");
            case kSpellEffects.kAddCombatTriggerList:
                return Say($"Sets up battle triggers on {who}");
            case kSpellEffects.kRemoveCombatTriggerList:
                return Say($"Removes battle triggers from {who}");
            case kSpellEffects.kRemoveStunBlock:
                return Say($"Removes {whose} stun blocks");
            case kSpellEffects.kUntargetable:
                return Say($"Makes {who} untargetable{forRounds}");
            case kSpellEffects.kVanish:
                return Say($"Makes {who} vanish from the battle{forRounds}");
            case kSpellEffects.kAutoPass:
                return Say($"Makes {who} pass {(who == "you" ? "your" : "their")} turns{forRounds}");
            case kSpellEffects.kExitCombat:
                return Say($"Takes {who} out of the battle");
            case kSpellEffects.kCollectEssence:
                return Say("Collects an essence from a defeated enemy");
            case kSpellEffects.kModifyCardDamage:
                return Say($"{sign} damage on the card");
            case kSpellEffects.kModifyCardDamagebyRank:
                return Say($"{sign} damage on the card for each pip it costs");
            case kSpellEffects.kModifyCardAccuracy:
            case kSpellEffects.kModifyCardOutgoingAccuracy:
                return Say($"{sign}% accuracy on the card");
            case kSpellEffects.kModifyCardArmorPiercing:
            case kSpellEffects.kModifyCardOutgoingArmorPiercing:
                return Say($"{sign}% pierce on the card");
            case kSpellEffects.kModifyCardHeal:
            case kSpellEffects.kModifyCardOutgoingHeal:
                return Say($"{sign} healing on the card");
            case kSpellEffects.kModifyCardCharm:
            case kSpellEffects.kModifyCardOutgoingDamage:
                return Say($"{sign}% on the blade or charm card");
            case kSpellEffects.kModifyCardWard:
            case kSpellEffects.kModifyCardIncomingDamage:
                return Say($"{sign}% on the shield or trap card");
            case kSpellEffects.kModifyCardAbsorbDamage:
                return Say($"{sign} on the absorb card");
            case kSpellEffects.kModifyCardCloak:
                return Say("Cloaks the card, so the enemy can't see it");
            case kSpellEffects.kModifyCardRank:
                return Say($"{sign} pip{(n == "1" ? "" : "s")} on the card's rank");
            case kSpellEffects.kModifyCardMutation:
                return Say("Changes the card into a stronger one");
            case kSpellEffects.kProtectCardBeneficial:
            case kSpellEffects.kProtectCardHarmful:
                return Say("Protects what the card puts on from being removed");
        }

        var name = Words(type.ToString());
        var amount = high != 0 || low != 0 ? $" {n}" : "";

        return Say($"{name}{amount}{on}{forRounds}");
    }

}
