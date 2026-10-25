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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Divide cards (kDivideDamage: one pool of damage split between the enemies picked) are the game's legacy versions of
/// the pick-your-enemies cards (TargetCountSpellEffect: a set amount for 1, 2, 3 or 4 enemies picked). Niduus
/// (2026-10-23): "I want all Divides (because they are legacy) to be the target enemy (Pick-your-enemy) and to behave
/// as such. If not, then reinstate the Divide system for those cards." A wizard's divide card is cast as the
/// pick-your-enemies card of the same name (Qismahs Curse - Shadow as Qismahs Curse, a T02 - TEMP card as its T02 - A),
/// so the game is told that card and shows its damage. A divide card with no such twin (Llamasu - Poison Oak, boss
/// and test cards) keeps dividing, between the enemies picked. [Battle] DivideAsPick=false keeps every divide card.
/// </summary>
internal static class DivideCards {

    private static bool s_divideAsPick => ConfigurationManager.GetValue("Battle.DivideAsPick", true);
    private static readonly Lazy<Dictionary<uint, (uint Id, SpellTemplate Template)>> s_pickVersions = new(Build);

    /// <summary>The pick-your-enemies card a divide card is cast as, or null.</summary>
    internal static (uint Id, SpellTemplate Template)? PickVersion(uint templateId)
        => s_divideAsPick && s_pickVersions.Value.TryGetValue(templateId, out var pick) ? pick : null;

    /// <summary>A card whose damage goes on enemies the wizard picks (pick-your-enemies or divide).</summary>
    internal static bool IsPickCard(SpellTemplate template)
        => template?.m_effects is { } effects
           && (effects.Any(e => e?.m_effectTarget == kEffectTarget.kMultiTargetEnemy)
               || CombatEnchantments.Leaves(effects).Any(e => e?.m_effectTarget == kEffectTarget.kMultiTargetEnemy));

    // The pick-your-enemies card's names for a divide card, best first.
    private static IEnumerable<string> PickNames(string divide) {
        var name = divide.Replace(" MOB Only", "", StringComparison.OrdinalIgnoreCase).Trim();
        var names = new List<string>();
        if (name.EndsWith(" - TEMP", StringComparison.OrdinalIgnoreCase)) {
            names.Add(name[..^" - TEMP".Length] + " - A");
        }

        foreach (var school in new[] { " - Shadow", " - Sun", " - Moon", " - Star" }) {
            if (name.EndsWith(school, StringComparison.OrdinalIgnoreCase)) {
                name = name[..^school.Length];
            }
        }

        names.Add(name);

        // The game spells the worm both ways (Sand Worm - Sun, Sand Wurm - Shadow).
        return names.Concat(names.Select(n => n.Replace("Wurm", "Worm", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDivide(SpellTemplate template)
        => CombatEnchantments.Leaves(template.m_effects ?? []).Any(e => e?.m_effectType == kSpellEffects.kDivideDamage);

    private static bool IsTargetCount(SpellTemplate template)
        => (template.m_effects ?? []).Any(e => e is TargetCountSpellEffect);

    private static Dictionary<uint, (uint, SpellTemplate)> Build() {
        var map = new Dictionary<uint, (uint, SpellTemplate)>();
        try {
            var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                string file = location?.m_filename;
                if (!string.IsNullOrEmpty(file) && location.m_id <= uint.MaxValue) {
                    ids.TryAdd(file, (uint) location.m_id);
                }
            }

            var spells = SpellFactory.AllSpellTemplates().Where(s => ids.ContainsKey(s.Path))
                .Select(s => (Id: ids[s.Path], s.Template)).ToList();
            var picks = new Dictionary<string, (uint, SpellTemplate)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, template) in spells.Where(s => IsTargetCount(s.Template))) {
                picks.TryAdd((string) template.m_name, (id, template));
            }

            foreach (var (id, template) in spells.Where(s => IsDivide(s.Template))) {
                var pick = PickNames((string) template.m_name).Select(n => picks.TryGetValue(n, out var p) ? p : default)
                    .FirstOrDefault(p => p.Item2 is not null);
                if (pick.Item2 is null) {
                    Logger.Information("Divide cards: {0} has no pick-your-enemies card, it keeps dividing.", Logger.Args((string) template.m_name));
                    continue;
                }

                map[id] = pick;
                Logger.Information("Divide cards: {0} casts as {1}.", Logger.Args((string) template.m_name, (string) pick.Item2.m_name));
            }
        }
        catch (Exception e) {
            Logger.Error("Divide cards: could not match divide cards to pick-your-enemies cards: {0}", Logger.Args(e));
        }

        return map;
    }

}
