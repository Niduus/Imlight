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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// "Rewritten AI" (Niduus, 2026-11-11; design doc "Rewritten AI design"): a switch, Off by default, that makes creatures
/// fight with reason. They score every enemy by threat (blades, pips, damage done, healing, spent blades, low health,
/// school matchup, taunts), pick the card that makes sense for the fight, and plan as a team.
/// This file holds the settings ([RewrittenAI] in Imlight.ini, read live), what each battle remembers (the ledger, filled
/// by CombatEffectApplicator and CombatActionResolver whether the switch is on or off) and how a card is read. The
/// choices themselves are in CombatCreatureAIComponent.Rewritten.cs.
/// </summary>
internal static class RewrittenAI {

    private const string SECTION = "RewrittenAI.";

    // ---------------------------------------------------------------- settings (live)

    /// <summary>The main switch.</summary>
    internal static bool Enabled => ConfigurationManager.GetValue(SECTION + "Enabled", false);

    /// <summary>Wizards' minions (and creatures that fight on the wizards' side) think the same way.</summary>
    internal static bool MinionsUseIt => ConfigurationManager.GetValue(SECTION + "MinionsAndPets", true);

    /// <summary>In a boss fight, the creatures know each other's hands.</summary>
    internal static bool BossesKnowHands => ConfigurationManager.GetValue(SECTION + "BossesKnowHands", true);

    /// <summary>One plain line per creature per round in the server log.</summary>
    internal static bool LogReasons => ConfigurationManager.GetValue(SECTION + "LogReasons", true);

    /// <summary>How much of old damage and healing threat is forgotten each round, 0 to 1.</summary>
    internal static float Fade => Math.Clamp(ConfigurationManager.GetValue(SECTION + "ThreatFadePercent", 30f), 0f, 100f) / 100f;

    /// <summary>Share of a spent blade stack's threat the creatures keep, 0 to 1.</summary>
    internal static float SpentBladesKept => Math.Clamp(ConfigurationManager.GetValue(SECTION + "SpentBladesKeptPercent", 50f), 0f, 500f) / 100f;

    /// <summary>Share they keep when the hit that spent the blades defeated one of them, 0 to 1.</summary>
    internal static float SpentBladesKeptOnKill => Math.Clamp(ConfigurationManager.GetValue(SECTION + "SpentBladesKeptOnKillPercent", 100f), 0f, 500f) / 100f;

    /// <summary>Starting chance a side gangs up on one wizard, 0 to 1 (the fight then tips it).</summary>
    internal static float GangUpChance => Math.Clamp(ConfigurationManager.GetValue(SECTION + "GangUpChance", 50f), 0f, 100f) / 100f;

    /// <summary>A hit that takes this share of the target's most health is "big enough": setup stops, 0 to 1.</summary>
    internal static float BigHitShare => Math.Clamp(ConfigurationManager.GetValue(SECTION + "BigHitPercent", 50f), 5f, 100f) / 100f;

    /// <summary>
    /// The weights and limits that count for one creature: its own from the app's Bosses tab, else the server's.
    /// Weights are multipliers (1 = 100%).
    /// </summary>
    internal sealed record Tuning(float Danger, float Damage, float Healing, float LowHealth, float Matchup, float Stubborn,
                                  int MaxSetupRounds, Role Role, HashSet<string> Exclusions, HashSet<string> NeverCast) {
        internal bool Excludes(string what) => Exclusions.Contains(what);
    }

    /// <summary>The part a creature plays in its team.</summary>
    internal enum Role { Automatic, Leader, Protector, Healer, Striker, Support, Free }

    // Exclusions a creature can have (the Bosses tab's ticks).
    internal const string NO_TEAM_SHIELDS = "NoTeamShields";
    internal const string NO_TEAM_HEALS = "NoTeamHeals";
    internal const string NO_TEAM_BLADES = "NoTeamBlades";
    internal const string NO_TRAPS = "NoTraps";
    internal const string NO_MATCHUP = "NoMatchup";
    internal const string NO_GANG_UP = "NoGangUp";
    internal const string NO_HOLD_BACK = "NoHoldBack";
    internal const string NO_SETUP = "NoSetup";

    internal static readonly string[] EXCLUSIONS = [NO_TEAM_SHIELDS, NO_TEAM_HEALS, NO_TEAM_BLADES, NO_TRAPS, NO_MATCHUP, NO_GANG_UP,
                                                    NO_HOLD_BACK, NO_SETUP];

    /// <summary>Whether the rewritten AI decides for this creature: its own Bosses tab choice, else the switch.</summary>
    internal static bool IsOnFor(BossOverrides.BossOverride own, bool onWizardsSide) {
        if (own?.RewrittenAI is { Length: > 0 } mode) {
            if (mode.Equals("On", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return Enabled && (!onWizardsSide || MinionsUseIt);
    }

    internal static Tuning TuningFor(BossOverrides.BossOverride own) {
        var role = Enum.TryParse<Role>(own?.AIRole ?? "", true, out var r) ? r : Role.Automatic;
        var exclusions = new HashSet<string>(own?.AIExclusions ?? [], StringComparer.OrdinalIgnoreCase);
        var never = new HashSet<string>((own?.AINeverCast ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                        StringComparer.OrdinalIgnoreCase);

        return new Tuning(
            Weight(own?.AIDanger, "DangerWeight"),
            Weight(own?.AIDamage, "DamageWeight"),
            Weight(own?.AIHealing, "HealingWeight"),
            Weight(own?.AILowHealth, "LowHealthWeight"),
            Weight(own?.AIMatchup, "MatchupWeight"),
            Math.Clamp((own?.AIStubborn ?? ConfigurationManager.GetValue(SECTION + "StubbornPercent", 20f)) / 100f, 0f, 5f),
            Math.Clamp((int) (own?.AISetupRounds ?? ConfigurationManager.GetValue(SECTION + "MaxSetupRounds", 5f)), 0, 10),
            role, exclusions, never);
    }

    private static float Weight(float? own, string key)
        => Math.Clamp((own ?? ConfigurationManager.GetValue(SECTION + key, 100f)) / 100f, 0f, 10f);

    /// <summary>A share (0 to 1) as a whole percent, for the log.</summary>
    internal static int Percent(float share) => (int) MathF.Round(share * 100f);

    // ---------------------------------------------------------------- schools

    /// <summary>The school a creature or wizard "wants" to fight: its opposite (Fire and Ice, Storm and Myth, Life and Death).</summary>
    internal static string Opposite(string school) => (school ?? "").ToLowerInvariant() switch {
        "fire" => "Ice", "ice" => "Fire",
        "storm" => "Myth", "myth" => "Storm",
        "life" => "Death", "death" => "Life",
        _ => "",
    };

    internal static string SchoolOf(CombatDuelSubCircle circle) {
        try {
            var school = circle?.SchoolNow ?? MagicSchool.None;

            return school == MagicSchool.None ? "" : school.ToString();
        }
        catch (Exception) {
            return "";
        }
    }

    internal static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A charm or ward of this school counts for a spell of that school (or it is for all schools).</summary>
    internal static bool Fits(string modifierSchool, string school)
        => string.IsNullOrEmpty(modifierSchool) || CombatCharms.SchoolMatches(modifierSchool, school ?? "");

    // ---------------------------------------------------------------- what a participant has on it

    /// <summary>Blades on a participant: the sum of its damage charms' percents (one of each spell), for a school or all.</summary>
    internal static float BladesOn(CombatDuelSubCircle circle, string school = null) {
        if (circle?._hangingEffects is not { } hanging) {
            return 0;
        }

        lock (hanging) {
            return CombatCharms.OnePerSpell(hanging.Where(x => x is not null && x.m_effectType == kSpellEffects.kModifyOutgoingDamage
                                                               && x.m_effectParam > 0 && (school is null || Fits(x.m_sDamageType, school))))
                .Sum(x => (float) x.m_effectParam);
        }
    }

    /// <summary>Traps (positive) and shields (negative) on a participant against a school, as percents.</summary>
    internal static List<SpellEffect> WardsOn(CombatDuelSubCircle circle, string school) {
        if (circle?._hangingEffects is not { } hanging) {
            return [];
        }

        lock (hanging) {
            return CombatCharms.OnePerSpell(hanging.Where(x => x is not null
                && x.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kAbsorbDamage
                && Fits(x.m_sDamageType, school))).ToList();
        }
    }

    internal static float TrapsOn(CombatDuelSubCircle circle, string school)
        => WardsOn(circle, school).Where(x => x.m_effectType == kSpellEffects.kModifyIncomingDamage && x.m_effectParam > 0)
            .Sum(x => (float) x.m_effectParam);

    internal static bool HasEffectOfSpell(CombatDuelSubCircle circle, uint spellTemplateId) {
        if (circle?._hangingEffects is not { } hanging || spellTemplateId == 0) {
            return false;
        }

        lock (hanging) {
            return hanging.Any(x => x is not null && x.m_spellTemplateID == spellTemplateId);
        }
    }

    /// <summary>Its pips as the AI counts them: a power pip is worth 2.</summary>
    internal static int PipsOf(CombatDuelSubCircle circle) {
        var pips = circle?.CombatParticipant?.m_pipCount;

        return pips is null ? 0 : pips.m_genericPips + (pips.m_powerPips * 2);
    }

    internal static int Health(CombatDuelSubCircle circle) => Math.Max(0, circle?.ParticipantGameStats?.m_currentHitpoints ?? 0);

    internal static int MostHealth(CombatDuelSubCircle circle) => Math.Max(1, circle?.ParticipantGameStats?.m_baseHitpoints ?? 1);

    internal static float HealthShare(CombatDuelSubCircle circle) => Math.Clamp((float) Health(circle) / MostHealth(circle), 0f, 1f);

    /// <summary>Its resist to a school as a fraction (negative: a boost, it takes more).</summary>
    internal static float ResistOf(CombatDuelSubCircle circle, string school) {
        try {
            var stats = circle?.ParticipantGameStats;
            if (stats is null) {
                return 0;
            }

            return Math.Clamp(circle.GetStatBySchool(stats.m_dmgReducePercent, school) + stats.m_dmgReducePercentAll, -2f, 0.95f);
        }
        catch (Exception) {
            return 0;
        }
    }

    private static float BoostOf(CombatDuelSubCircle circle, string school) {
        try {
            var stats = circle?.ParticipantGameStats;
            if (stats is null) {
                return 0;
            }

            return Math.Clamp(circle.GetStatBySchool(stats.m_dmgBonusPercent, school) + stats.m_dmgBonusPercentAll, -0.9f, 10f);
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>Whether a creature is a boss (BossRank.IsBoss).</summary>
    internal static bool IsBoss(CombatDuelSubCircle circle)
        => circle?.ParticipantObject is { } obj
           && (CoreObjectFactory.GetCoreTemplate(obj.m_templateID) as GameObjectTemplate)?.m_behaviors?
               .OfType<NPCBehaviorTemplate>().Any(npc => npc.IsBoss()) == true;

    // ---------------------------------------------------------------- reading a card

    /// <summary>What a card does, as the AI reads it.</summary>
    internal sealed class Card {
        internal Spell Spell;
        internal uint Id;
        internal string Name = "";
        internal string Shown = "";
        internal int Rank;
        internal string School = "";

        internal bool Hit;
        internal bool HitsAll;
        internal int BaseDamage;
        internal string DamageSchool = "";

        internal bool Blade;
        internal bool BladeForTeammate;
        internal bool BladeOnlyTeammate;
        internal bool BladeWholeTeam;
        internal string BladeSchool = "";
        internal int BladePercent;

        internal bool Trap;
        internal bool TrapAll;
        internal string TrapSchool = "";

        internal bool Shield;
        internal bool ShieldForTeammate;
        internal bool ShieldWholeTeam;
        internal string ShieldSchool = "";

        internal bool Weakness;
        internal bool RemovesCharms;
        internal bool Stun;
        internal bool Heal;
        internal bool HealForTeammate;
        internal bool HealWholeTeam;
        internal bool HealTakesFromEnemy;
        internal int HealAmount;
        internal bool Natural;

        internal string Kind => Hit ? "attack" : Heal ? "heal" : Blade ? "blade" : Trap ? "trap" : Shield ? "shield"
            : Weakness ? "weakness" : RemovesCharms ? "charm removal" : Stun ? "stun" : "other";
    }

    private static readonly ConditionalWeakTable<SpellTemplate, Card> s_cards = new();

    /// <summary>Reads a card of a hand.</summary>
    internal static Card Read(Spell spell) {
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
            return null;
        }

        var id = spell.m_templateID;
        var known = s_cards.GetValue(template, tpl => Build(tpl, id));
        // The template's reading is shared; the hand's own card object goes with it.
        return new Card {
            Spell = spell, Id = spell.m_templateID, Name = known.Name, Shown = known.Shown, Rank = spell.m_pipCost?.m_spellRank ?? known.Rank,
            School = known.School, Hit = known.Hit, HitsAll = known.HitsAll, BaseDamage = known.BaseDamage, DamageSchool = known.DamageSchool,
            Blade = known.Blade, BladeForTeammate = known.BladeForTeammate, BladeOnlyTeammate = known.BladeOnlyTeammate,
            BladeWholeTeam = known.BladeWholeTeam, BladeSchool = known.BladeSchool, BladePercent = known.BladePercent,
            Trap = known.Trap, TrapAll = known.TrapAll, TrapSchool = known.TrapSchool,
            Shield = known.Shield, ShieldForTeammate = known.ShieldForTeammate, ShieldWholeTeam = known.ShieldWholeTeam, ShieldSchool = known.ShieldSchool,
            Weakness = known.Weakness, RemovesCharms = known.RemovesCharms, Stun = known.Stun,
            Heal = known.Heal, HealForTeammate = known.HealForTeammate, HealWholeTeam = known.HealWholeTeam,
            HealTakesFromEnemy = known.HealTakesFromEnemy, HealAmount = known.HealAmount, Natural = known.Natural,
        };
    }

    private static Card Build(SpellTemplate template, uint id) {
        var name = (string) template.m_name ?? "";
        var card = new Card {
            Id = id,
            Name = name,
            Shown = SafeShown(name, id),
            School = (string) template.m_sMagicSchoolName ?? "",
            Natural = name.StartsWith("NA ", StringComparison.Ordinal) || name.EndsWith(" NA", StringComparison.Ordinal),
        };
        var leaves = CombatEnchantments.Leaves(template.m_effects ?? []).Where(e => e is not null).ToList();
        foreach (var e in leaves) {
            var enemy = IsEnemy(e.m_effectTarget);
            var friendly = IsFriendly(e.m_effectTarget);
            switch (e.m_effectType) {
                case var t when CombatCharms.DamageEffects.Contains(t) && enemy:
                    card.Hit = true;
                    card.HitsAll |= e.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kMultiTargetEnemy;
                    if (string.IsNullOrEmpty(card.DamageSchool)) {
                        card.DamageSchool = (string) e.m_sDamageType ?? "";
                    }

                    if (t == kSpellEffects.kStealHealth) {
                        card.HealTakesFromEnemy = true;
                    }

                    break;
                case kSpellEffects.kModifyOutgoingDamage when e.m_effectParam > 0 && friendly:
                    card.Blade = true;
                    card.BladeSchool = (string) e.m_sDamageType ?? "";
                    card.BladePercent = Math.Max(card.BladePercent, e.m_effectParam);
                    card.BladeForTeammate |= e.m_effectTarget is kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe;
                    card.BladeOnlyTeammate |= e.m_effectTarget is kEffectTarget.kFriendlySingleNotMe;
                    card.BladeWholeTeam |= e.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce;
                    break;
                case kSpellEffects.kModifyOutgoingDamage when e.m_effectParam < 0 && enemy:
                    card.Weakness = true;
                    break;
                case kSpellEffects.kModifyIncomingDamage when e.m_effectParam > 0 && enemy:
                    card.Trap = true;
                    card.TrapSchool = (string) e.m_sDamageType ?? "";
                    card.TrapAll |= e.m_effectTarget is kEffectTarget.kEnemyTeam or kEffectTarget.kEnemyTeamAllAtOnce;
                    break;
                case kSpellEffects.kModifyIncomingDamage when e.m_effectParam < 0 && friendly:
                case kSpellEffects.kAbsorbDamage when friendly:
                    card.Shield = true;
                    card.ShieldSchool = (string) e.m_sDamageType ?? "";
                    card.ShieldForTeammate |= e.m_effectTarget is kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe;
                    card.ShieldWholeTeam |= e.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce;
                    break;
                case kSpellEffects.kRemoveCharm or kSpellEffects.kStealCharm or kSpellEffects.kDispel when enemy:
                    card.RemovesCharms = true;
                    break;
                case kSpellEffects.kStun when enemy:
                    card.Stun = true;
                    break;
                case kSpellEffects.kHeal or kSpellEffects.kHealOverTime or kSpellEffects.kHealPercent when friendly:
                    card.Heal = true;
                    card.HealAmount += e.m_effectType == kSpellEffects.kHealPercent ? 0 : e.m_effectParam;
                    card.HealForTeammate |= e.m_effectTarget is kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe;
                    card.HealWholeTeam |= e.m_effectTarget is kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce;
                    break;
            }
        }

        // A card that deals damage is an attack, even when the damage's own target is not one of the enemy targets above
        // (a pet copy such as "Pet - Frost Beetle Storm" also carries a charm on the caster): read as a blade, the Kraken
        // cast its Storm Beetle on itself (Niduus, 2026-11-12).
        if (!card.Hit && leaves.Any(e => CombatCharms.DamageEffects.Contains(e.m_effectType) && !IsFriendly(e.m_effectTarget))) {
            card.Hit = true;
            card.DamageSchool = (string) leaves.First(e => CombatCharms.DamageEffects.Contains(e.m_effectType)
                                                          && !IsFriendly(e.m_effectTarget)).m_sDamageType ?? "";
        }

        if (card.HealTakesFromEnemy) {
            card.Heal = card.Heal || card.Hit;
        }

        card.BaseDamage = (int) (template.m_effects ?? []).Where(e => e is not null).Sum(DamageOf);
        if (card.Hit && string.IsNullOrEmpty(card.DamageSchool)) {
            card.DamageSchool = card.School;
        }

        return card;
    }

    private static string SafeShown(string name, uint id) {
        try {
            return PetWillCasts.CardName(name, id);
        }
        catch (Exception) {
            return name;
        }
    }

    // A card's base damage: random effects count as their average, a choice of tiers (X pips) or conditions as the best.
    private static float DamageOf(SpellEffect effect) => effect switch {
        null => 0,
        RandomSpellEffect random => random.m_effectList is { Count: > 0 } list ? list.Average(DamageOf) : 0,
        VariableSpellEffect variable => variable.m_effectList is { Count: > 0 } list ? list.Max(DamageOf) : 0,
        ConditionalSpellEffect conditional => conditional.m_elements?.Where(x => x?.m_pEffect is not null).Select(x => DamageOf(x.m_pEffect))
            .DefaultIfEmpty(0).Max() ?? 0,
        EffectListSpellEffect list => list.m_effectList?.Sum(DamageOf) ?? 0,
        CountBasedSpellEffect counted => counted.m_effectList?.Sum(DamageOf) ?? 0,
        _ when !IsEnemy(effect.m_effectTarget) => 0,
        _ when effect.m_effectType == kSpellEffects.kDamagePerTotalPipPower => effect.m_effectParam * 4,
        _ when CombatCharms.DamageEffects.Contains(effect.m_effectType) => Math.Max(0, effect.m_effectParam),
        _ => 0,
    };

    private static bool IsEnemy(kEffectTarget target) => target is kEffectTarget.kAtLeastOneEnemy or kEffectTarget.kEnemyTeam
        or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kEnemySingle or kEffectTarget.kMultiTargetEnemy
        or kEffectTarget.kPreselectedEnemySingle;

    private static bool IsFriendly(kEffectTarget target) => target is kEffectTarget.kSelf or kEffectTarget.kFriendlyTeam
        or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kFriendlySingle or kEffectTarget.kMultiTargetFriendly
        or kEffectTarget.kFriendlySingleNotMe or kEffectTarget.kFriendlyMinion;

    /// <summary>
    /// About how much a hit would take from the target now: the caster's boost and blades (plus extra percent), the target's
    /// traps and shields (plus extra traps), absorbs and resist.
    /// </summary>
    internal static int Estimate(Card card, CombatDuelSubCircle caster, CombatDuelSubCircle target, float extraBlades = 0, float extraTraps = 0,
                                 bool ignoreWards = false) {
        if (card is null || !card.Hit || target is null) {
            return 0;
        }

        var school = card.DamageSchool;
        double damage = Math.Max(0, card.BaseDamage);
        damage *= 1 + BoostOf(caster, school);
        if (caster?._hangingEffects is { } own) {
            List<SpellEffect> charms;
            lock (own) {
                charms = CombatCharms.OnePerSpell(own.Where(x => x is not null
                    && x.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat
                    && Fits(x.m_sDamageType, school))).ToList();
            }

            foreach (var charm in charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamage)) {
                damage *= 1 + (charm.m_effectParam / 100.0);
            }

            damage += charms.Where(c => c.m_effectType == kSpellEffects.kModifyOutgoingDamageFlat).Sum(c => c.m_effectParam);
        }

        damage *= 1 + (extraBlades / 100.0);
        if (!ignoreWards) {
            foreach (var ward in WardsOn(target, school)) {
                if (ward.m_effectType == kSpellEffects.kModifyIncomingDamage) {
                    damage *= 1 + (ward.m_effectParam / 100.0);
                }
                else {
                    damage -= Math.Max(0, ward.m_effectParam);
                }
            }
        }

        damage *= 1 + (extraTraps / 100.0);
        damage *= 1 - ResistOf(target, school);

        return (int) Math.Max(0, damage);
    }

    // ---------------------------------------------------------------- what each battle remembers

    /// <summary>A move a creature has planned for this round, which the creatures deciding after it can see.</summary>
    internal sealed record PlannedMove(int Slot, string Kind, int Target, string School, int Damage, uint SpellId);

    /// <summary>What a battle remembers for the rewritten AI. Every read and write holds Lock.</summary>
    internal sealed class Ledger {
        internal readonly object Lock = new();
        private int _round = int.MinValue;

        // Threat points: 1 point is about 1% (of a blade, or of the health of the one who was hit or healed).
        internal readonly Dictionary<int, Dictionary<int, float>> DamageTo = [];   // hurt slot -> attacker slot -> points
        internal readonly Dictionary<int, float> Healing = [];                     // healer slot -> points
        internal readonly Dictionary<int, float> SpentBlades = [];                 // attacker slot -> points
        internal readonly Dictionary<int, Dictionary<int, float>> TrapsLaid = [];  // trapped slot -> trapper slot -> percent
        internal readonly Dictionary<int, (int Round, HashSet<int> Feeders)> FedBy = [];
        internal readonly Dictionary<CombatTeam, (int Round, List<PlannedMove> Moves)> Plans = [];
        internal readonly Dictionary<CombatTeam, bool> GangUp = [];
        internal int Round => _round;

        /// <summary>The start of a round's planning: old threat fades once a round.</summary>
        internal void BeginRound(int round) {
            if (round == _round) {
                return;
            }

            var keep = 1f - Fade;
            foreach (var row in DamageTo.Values) {
                Scale(row, keep);
            }

            foreach (var row in TrapsLaid.Values) {
                Scale(row, keep);
            }

            Scale(Healing, keep);
            Scale(SpentBlades, keep);
            _round = round;
        }

        private static void Scale(Dictionary<int, float> row, float keep) {
            foreach (var key in row.Keys.ToList()) {
                row[key] *= keep;
            }
        }

        internal float Get(Dictionary<int, Dictionary<int, float>> table, int a, int b)
            => table.TryGetValue(a, out var row) && row.TryGetValue(b, out var v) ? v : 0;

        internal static void Add(Dictionary<int, Dictionary<int, float>> table, int a, int b, float points) {
            if (!table.TryGetValue(a, out var row)) {
                table[a] = row = [];
            }

            row[b] = row.GetValueOrDefault(b) + points;
        }

        /// <summary>The moves the side has planned this round so far.</summary>
        internal List<PlannedMove> PlanOf(CombatTeam side, int round) {
            if (!Plans.TryGetValue(side, out var plan) || plan.Round != round) {
                Plans[side] = plan = (round, []);
            }

            return plan.Moves;
        }

        /// <summary>The wizards (slots) that 2 or more teammates bladed this round or last round: a feed.</summary>
        internal List<int> Fed(int round)
            => [.. FedBy.Where(f => f.Value.Round >= round - 1 && f.Value.Feeders.Count >= 2).Select(f => f.Key)];
    }

    private static readonly ConditionalWeakTable<CombatDuelComponent, Ledger> s_ledgers = new();

    internal static Ledger For(CombatDuelComponent duel) => duel is null ? null : s_ledgers.GetValue(duel, _ => new Ledger());

    private static int RoundOf(CombatDuelSubCircle circle) {
        try {
            return circle?._duelActor?.Duel?.m_roundNum ?? 0;
        }
        catch (Exception) {
            return 0;
        }
    }

    // ---------------------------------------------------------------- the battle tells the ledger (always, on or off)

    /// <summary>A hit landed (CombatEffectApplicator): the attacker gets threat from the one it hurt.</summary>
    internal static void OnDamage(CombatDuelSubCircle caster, CombatDuelSubCircle target, int dealt) {
        if (dealt <= 0 || caster is null || target is null || caster == target || caster.OccupiedTeam == target.OccupiedTeam) {
            return;
        }

        Guard(caster, ledger => Ledger.Add(ledger.DamageTo, target.SlotIndex, caster.SlotIndex, dealt * 100f / MostHealth(target)));
    }

    /// <summary>A heal landed on the healer's own side.</summary>
    internal static void OnHeal(CombatDuelSubCircle caster, CombatDuelSubCircle target, int healed) {
        if (healed <= 0 || caster is null || target is null || caster.OccupiedTeam != target.OccupiedTeam) {
            return;
        }

        Guard(caster, ledger => ledger.Healing[caster.SlotIndex] = ledger.Healing.GetValueOrDefault(caster.SlotIndex) + (healed * 100f / MostHealth(target)));
    }

    /// <summary>
    /// An effect went on its targets (CombatActionResolver): blades a teammate gave (a feed when two or more feed the same
    /// one) and traps laid on the other side. A pet's May Cast or Will Cast is cast as its owner, so the owner gets it.
    /// </summary>
    internal static void OnEffect(CombatDuelSubCircle caster, CombatDuelSubCircle[] targets, SpellEffect effect) {
        if (caster is null || targets is null || effect is null) {
            return;
        }

        if (effect.m_effectType == kSpellEffects.kModifyOutgoingDamage && effect.m_effectParam > 0) {
            var fed = targets.Where(t => t is not null && t != caster && t.OccupiedTeam == caster.OccupiedTeam).ToList();
            if (fed.Count > 0) {
                var round = RoundOf(caster);
                Guard(caster, ledger => {
                    foreach (var target in fed) {
                        if (!ledger.FedBy.TryGetValue(target.SlotIndex, out var feed) || feed.Round != round) {
                            feed = (round, []);
                        }

                        feed.Feeders.Add(caster.SlotIndex);
                        ledger.FedBy[target.SlotIndex] = feed;
                    }
                });
            }
        }
        else if (effect.m_effectType == kSpellEffects.kModifyIncomingDamage && effect.m_effectParam > 0) {
            var trapped = targets.Where(t => t is not null && t.OccupiedTeam != caster.OccupiedTeam).ToList();
            if (trapped.Count > 0) {
                Guard(caster, ledger => {
                    foreach (var target in trapped) {
                        Ledger.Add(ledger.TrapsLaid, target.SlotIndex, caster.SlotIndex, effect.m_effectParam);
                    }
                });
            }
        }
    }

    /// <summary>
    /// A cast ended (CombatActionResolver): blades it used up leave threat behind, half of it, or all of it when the cast
    /// defeated someone on the other side ([RewrittenAI] SpentBladesKeptPercent and SpentBladesKeptOnKillPercent).
    /// </summary>
    internal static void OnCastDone(CombatDuelSubCircle caster, IEnumerable<SpellEffect> usedCharms, bool defeatedSomeone) {
        if (caster is null || usedCharms is null) {
            return;
        }

        var blades = usedCharms.Where(c => c is not null && c.m_effectType == kSpellEffects.kModifyOutgoingDamage && c.m_effectParam > 0
                                           && !CombatRequirements.IsAura(c))
            .Sum(c => (float) c.m_effectParam);
        if (blades <= 0) {
            return;
        }

        var kept = blades * (defeatedSomeone ? SpentBladesKeptOnKill : SpentBladesKept);
        Guard(caster, ledger => ledger.SpentBlades[caster.SlotIndex] = ledger.SpentBlades.GetValueOrDefault(caster.SlotIndex) + kept);
    }

    private static void Guard(CombatDuelSubCircle circle, Action<Ledger> write) {
        try {
            if (For(circle?._duelActor) is { } ledger) {
                lock (ledger.Lock) {
                    write(ledger);
                }
            }
        }
        catch (Exception e) {
            Logger.Debug("Rewritten AI | ledger write failed: {0}", Logger.Args(e.Message));
        }
    }

}
