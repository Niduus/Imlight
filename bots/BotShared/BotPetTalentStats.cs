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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Every pet talent a real pet can have, with what it really gives a wizard at 260 pet stats (the game's talent effects:
/// damage, resist, pierce, accuracy, healing, health...), so talents are picked by what they do, not by their shown name.
/// Shown names are not to be trusted for it (Niduus, 2026-11-19: his Life pet showed Myth damage talents after a pick by
/// name; "still bugged not picking right talents").
/// </summary>
internal static class BotPetTalentStats {

    internal sealed record Info(string Key, string Shown, IReadOnlyList<BotGear.Stat> Stats, string MayCast, bool MayCastHeals) {
        internal string Words => Stats.Count == 0
            ? (MayCast is null ? Shown : $"{Shown} (may cast {MayCast})")
            : $"{Shown} ({string.Join(", ", Stats.Select(s => $"+{s.Value:0.#} {(s.School.Length == 0 ? "all" : s.School)} {s.Kind}"))})";
    }

    private static volatile List<Info> s_real;

    private static readonly Dictionary<string, string> s_kinds = new(StringComparer.Ordinal) {
        ["m_damageBonusPercent"] = "Damage", ["m_damageBonusFlat"] = "FlatDamage", ["m_accuracyBonusPercent"] = "Accuracy",
        ["m_armorPiercingBonusPercent"] = "ArmorPiercing", ["m_damageReducePercent"] = "ReduceDamage",
        ["m_damageReduceFlat"] = "FlatReduceDamage", ["m_healBonusPercent"] = "Healing", ["m_healIncBonusPercent"] = "IncHealing",
        ["m_hitPointBonus"] = "MaxHealth", ["m_powerPipBonusPercent"] = "PowerPips", ["m_criticalHitRating"] = "CriticalHit",
        ["m_blockRating"] = "Block",
    };

    /// <summary>Every talent real pets can have (no derby talents), read once.</summary>
    internal static IReadOnlyList<Info> Real {
        get {
            if (s_real is { } ready) {
                return ready;
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, template) in PetFactory.AllPetTemplates) {
                var pet = template?.m_behaviors?.OfType<PetItemBehaviorTemplate>().FirstOrDefault();
                if (pet is null) {
                    continue;
                }

                foreach (var name in Names(pet.m_talents).Concat(Names(pet.m_guaranteedTalents))) {
                    keys.Add(name);
                }
            }

            var list = new List<Info>();
            foreach (var talent in PetData.AllTalents.OfType<PetTalentTemplate>()) {
                string key = talent.m_talentName;
                if (string.IsNullOrEmpty(key) || !keys.Contains(key) || key.Contains("Test", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("Dev", StringComparison.Ordinal)) {
                    continue;
                }

                try {
                    list.Add(Read(talent));
                }
                catch (Exception e) {
                    Logger.Debug("Bot pet talents: {0} not read: {1}", Logger.Args(key, e.Message));
                }
            }

            return s_real = list;
        }
    }

    /// <summary>A talent by the game's own name ("Talent-Damage-Myth02"), or null.</summary>
    internal static Info Of(string key) => Real.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
                                           ?? (PetData.GetTalent(key) is PetTalentTemplate t ? Read(t) : null);

    /// <summary>How much a talent is worth for a school (or "" for every school only) and style.</summary>
    /// <remarks>
    /// Accuracy already at its top from his gear: a school's accuracy is worth nothing more, an every-school one still
    /// counts (Niduus, 2026-11-19: "try to have the bot go for universal accuracy"); power pips already at 100%: pip
    /// talents count for nothing.
    /// </remarks>
    internal static double Score(Info talent, string school, BotStyle style, bool accuracyFull = false, bool pipsFull = false) {
        if (talent is null) {
            return 0;
        }

        // A pet's resist to one school only (Myth-Ward, Myth-Proof) is not taken: it only helps against that school's hits,
        // and players never use their own school's (it chose Myth-Ward over Pain-Giver for a Myth hitter).
        var stats = talent.Stats.Where(s => !(accuracyFull && s.Kind == "Accuracy" && s.School.Length > 0) && !(pipsFull && s.Kind == "PowerPips")
                                            && !(s.Kind.Contains("ReduceDamage", StringComparison.Ordinal) && s.School.Length > 0));
        var score = BotGear.ScoreStats(stats, school, style);
        if (talent.MayCast is not null) {
            // Healing may-casts (Unicorn, Fairy...) for a healer; a little for a tank; nothing for damage-only setups.
            score += talent.MayCastHeals
                ? style switch { BotStyle.FullHealer => 60, BotStyle.Healer => 45, BotStyle.Tank or BotStyle.FullTank => 15, BotStyle.Backup => 8, _ => 0 }
                : 0;
        }

        return score;
    }

    /// <summary>The best five talents for a school and style, best first (one per family of the same stat and school).</summary>
    internal static List<Info> Best(string school, BotStyle style, int count = 5, bool accuracyFull = false, bool pipsFull = false) {
        var picked = new List<Info>();
        var left = Real.Select(t => (Talent: t, Score: Score(t, school, style, accuracyFull, pipsFull))).Where(x => x.Score > 0).ToList();
        while (picked.Count < count && left.Count > 0) {
            // One healing may-cast counts in full, a second half, more hardly at all: five may-casts would leave no room
            // for healing or resist.
            var casts = picked.Count(p => p.MayCastHeals);
            var next = left.Select(x => (x.Talent, Score: x.Talent.MayCastHeals ? x.Score * (casts == 0 ? 1 : casts == 1 ? 0.5 : 0.15) : x.Score))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Talent.Key, StringComparer.OrdinalIgnoreCase).First();
            left.RemoveAll(x => ReferenceEquals(x.Talent, next.Talent));
            // The same talent read twice (two keys, same stats and name) is one.
            if (picked.Any(p => p.Shown == next.Talent.Shown && p.Stats.SequenceEqual(next.Talent.Stats))) {
                continue;
            }

            picked.Add(next.Talent);
        }

        return picked;
    }

    private static Info Read(PetTalentTemplate talent) {
        var stats = new List<BotGear.Stat>();
        string mayCast = null;
        var heals = false;
        foreach (var info in talent.m_effectList ?? []) {
            switch (info) {
                case StatisticEffectInfo statInfo when PetData.GetTalentEffect(statInfo.m_effectName) is { } effect:
                    stats.AddRange(StatsOf(effect));
                    break;
                case ProvideCombatTriggerInfo:
                    mayCast = PetMayCasts.SpellOf(talent);
                    try {
                        heals = mayCast is not null && SpellFactory.GetSpell(mayCast) is { } spell && RewrittenAI.Read(spell) is { Heal: true };
                    }
                    catch (Exception) {
                        heals = false;
                    }

                    break;
            }
        }

        string shown = Locale.GetEnglishName((string) talent.m_displayName ?? "") ?? "";
        return new Info(talent.m_talentName, shown.Length > 0 ? shown : (string) talent.m_talentName, stats, mayCast, heals);
    }

    // What a talent effect gives at 260 in every pet stat (BotPetTalents.PET_STAT).
    private static IEnumerable<BotGear.Stat> StatsOf(PetBoostPlayerStatEffectTemplate template) {
        string first = template.m_primaryStat1;
        string second = template.m_primaryStat2;
        string extra = template.m_secondaryStat;
        float Top(string stat) => string.IsNullOrEmpty(stat) ? 0 : BotPetTalents.PET_STAT;
        var basis = Top(first) + Top(second) + (Top(extra) * template.m_secondaryModifier);
        string school = template.m_school;
        var forSchool = template.m_buffAll || string.IsNullOrEmpty(school) ? "" : school;
        foreach (var property in typeof(PetBoostPlayerStatEffectTemplate).GetProperties()) {
            if (!s_kinds.TryGetValue(property.Name, out var kind)) {
                continue;
            }

            float per;
            try {
                per = Convert.ToSingle(property.GetValue(template) ?? 0f);
            }
            catch (Exception) {
                continue;
            }

            if (per == 0) {
                continue;
            }

            var percent = property.Name.Contains("Percent", StringComparison.Ordinal);
            var value = per * basis * (percent ? 100 : 1);
            // Rounded as the game shows it (half up: Pain-Giver's 6.5 shows as +7).
            yield return new BotGear.Stat(kind, kind is "MaxHealth" ? "" : forSchool, MathF.Round(value, MidpointRounding.AwayFromZero));
        }
    }

    private static IEnumerable<string> Names(IEnumerable list)
        => list?.Cast<object>().Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)) ?? [];

}
