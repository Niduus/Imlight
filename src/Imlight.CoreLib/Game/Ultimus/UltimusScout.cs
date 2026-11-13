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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What Ultimus knows of the creatures it fights: their decks, read card by card, as a player knows them from the wikis
/// and from fighting them before (Niduus, 2026-11-13: "I saw Ultimus just use Elemental Shields against a solo Myth Mob
/// ... Any way we can make Ultimus able to view mob decks to better prepare/predict circumstances?"). From the decks it
/// knows which schools the enemies can hit with (natural attacks too), and whether they trap, shield, blade or heal.
/// Ultimus shields only against schools that can actually hit it, makes its deck ready with those shields, and leans its
/// gear's resist toward the schools the area's creatures hit with.
/// </summary>
internal static class UltimusScout {

    /// <summary>What a set of enemies can do, read from their decks.</summary>
    internal sealed class Threat {

        internal static readonly Threat None = new();

        // Each damage school's share of the enemies' hits (the biggest 1), by base damage and copies.
        internal Dictionary<string, double> Hits { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int Cards { get; set; }
        internal int Traps { get; set; }
        internal int Shields { get; set; }
        internal int Blades { get; set; }
        internal int Heals { get; set; }
        internal bool HitsAll { get; set; }

        /// <summary>Whether any deck was read (a creature with an unknown deck tells it nothing).</summary>
        internal bool Known => Cards > 0;

        /// <summary>The schools it can be hit with, most first.</summary>
        internal IReadOnlyList<string> Schools => [.. Hits.OrderByDescending(h => h.Value).Select(h => h.Key)];

        /// <summary>How much of the enemies' damage a shield of these schools (none or "All": every school) stops, 0 to 1.</summary>
        internal double Covered(IReadOnlyCollection<string> schools) {
            if (!Known) {
                return 1;
            }

            var total = Hits.Values.Sum();
            if (total <= 0) {
                return 0;
            }

            if (schools.Count == 0 || schools.Any(s => string.IsNullOrEmpty(s) || s.Equals("All", StringComparison.OrdinalIgnoreCase))) {
                return 1;
            }

            return Hits.Where(h => schools.Contains(h.Key, StringComparer.OrdinalIgnoreCase)).Sum(h => h.Value) / total;
        }

        internal string Describe() {
            if (!Known) {
                return "an unknown deck";
            }

            var parts = new List<string> {
                Hits.Count == 0 ? "no hits" : "hits with " + string.Join(", ", Schools.Take(4)) + (HitsAll ? " (some on everyone)" : ""),
            };
            if (Traps > 0) {
                parts.Add("traps");
            }

            if (Blades > 0) {
                parts.Add("blades");
            }

            if (Shields > 0) {
                parts.Add("shields");
            }

            if (Heals > 0) {
                parts.Add("heals");
            }

            return string.Join(", ", parts);
        }

    }

    private static readonly ConcurrentDictionary<ulong, Threat> s_byTemplate = new();
    private static readonly ConditionalWeakTable<CombatDuelSubCircle, Threat> s_byCircle = new();

    /// <summary>What the enemies in a battle can do, from the decks they fight with there.</summary>
    internal static Threat InBattle(IEnumerable<CombatDuelSubCircle> enemies) {
        var all = new Threat();
        foreach (var enemy in enemies.Where(e => e is { IsAlive: true })) {
            Threat one;
            try {
                one = s_byCircle.GetValue(enemy, c => Read(c.DeckCards));
            }
            catch (Exception) {
                continue;
            }

            Add(all, one);
        }

        Normalize(all);

        return all;
    }

    /// <summary>What a creature (by template) can do, from the deck the game files give it.</summary>
    internal static Threat OfCreature(ulong template) {
        if (template == 0) {
            return Threat.None;
        }

        return s_byTemplate.GetOrAdd(template, id => {
            try {
                var spells = CombatCreatureDeckComponent.GameSpellIds(CoreObjectFactory.GetCoreTemplate(id));
                var threat = Read(spells.Select(s => (s, 1)));
                Normalize(threat);

                return threat;
            }
            catch (Exception) {
                return Threat.None;
            }
        });
    }

    /// <summary>The fight against this creature as the deck maker sees it, with the schools its deck hits with.</summary>
    internal static BotShared.BotCards.Situation SituationOf(ulong creatureTemplate) {
        var situation = BotShared.BotCards.SituationOf(creatureTemplate);
        var threat = OfCreature(creatureTemplate);

        return threat.Known && threat.Hits.Count > 0 ? situation with { HitSchools = threat.Schools } : situation;
    }

    // The last creatures fought (or about to be), for the gear's resist: an area's creatures hit with the same few schools.
    private static readonly Queue<Threat> s_recent = new();
    private const int RECENT = 6;

    /// <summary>A creature it is about to fight: its deck counts toward the resist its gear leans to (BotGear.SoloResistMix).</summary>
    internal static void Expect(ulong creatureTemplate) {
        var threat = OfCreature(creatureTemplate);
        if (!threat.Known || threat.Hits.Count == 0) {
            return;
        }

        lock (s_recent) {
            s_recent.Enqueue(threat);
            while (s_recent.Count > RECENT) {
                s_recent.Dequeue();
            }

            var mix = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var one in s_recent) {
                foreach (var (school, share) in one.Hits) {
                    mix[school] = mix.GetValueOrDefault(school) + share;
                }
            }

            var most = mix.Values.DefaultIfEmpty(0).Max();
            BotShared.BotGear.SoloResistMix = most <= 0 ? new Dictionary<string, double>()
                : mix.ToDictionary(m => m.Key, m => m.Value / most, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Threat Read(IEnumerable<(uint Id, int Copies)> deck) {
        var threat = new Threat();
        foreach (var (id, copies) in deck) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate spell || RewrittenAI.ReadTemplate(id) is not { } card) {
                continue;
            }

            threat.Cards += copies;
            threat.Traps += card.Trap ? copies : 0;
            threat.Shields += card.Shield && !card.Hit ? copies : 0;
            threat.Blades += card.Blade ? copies : 0;
            threat.Heals += card.Heal && !card.Hit ? copies : 0;
            if (!card.Hit) {
                continue;
            }

            threat.HitsAll |= card.HitsAll;
            // Each way a hit may land shares the card (a random head is one of several); a natural attack is a hit too.
            var hits = CombatEnchantments.Leaves(spell.m_effects ?? [])
                .Where(e => e is not null && CombatCharms.DamageEffects.Contains(e.m_effectType)).ToList();
            if (hits.Count == 0) {
                var school = string.IsNullOrEmpty(card.DamageSchool) ? card.School : card.DamageSchool;
                if (!string.IsNullOrEmpty(school)) {
                    threat.Hits[school] = threat.Hits.GetValueOrDefault(school) + (Math.Max(1, card.BaseDamage) * copies);
                }

                continue;
            }

            foreach (var hit in hits) {
                var school = (string) hit.m_sDamageType;
                if (string.IsNullOrEmpty(school)) {
                    school = string.IsNullOrEmpty(card.DamageSchool) ? card.School : card.DamageSchool;
                }

                if (!string.IsNullOrEmpty(school)) {
                    threat.Hits[school] = threat.Hits.GetValueOrDefault(school) + (Math.Max(1, hit.m_effectParam) * copies / (double) hits.Count);
                }
            }
        }

        return threat;
    }

    private static void Add(Threat into, Threat one) {
        if (one is null || !one.Known) {
            return;
        }

        into.Cards += one.Cards;
        into.Traps += one.Traps;
        into.Shields += one.Shields;
        into.Blades += one.Blades;
        into.Heals += one.Heals;
        into.HitsAll |= one.HitsAll;
        foreach (var (school, share) in one.Hits) {
            into.Hits[school] = into.Hits.GetValueOrDefault(school) + share;
        }
    }

    private static void Normalize(Threat threat) {
        var most = threat.Hits.Count == 0 ? 0 : threat.Hits.Values.Max();
        if (most <= 0) {
            return;
        }

        foreach (var school in threat.Hits.Keys.ToList()) {
            threat.Hits[school] /= most;
        }
    }

}
