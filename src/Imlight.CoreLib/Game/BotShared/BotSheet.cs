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
using System.Text.Json.Nodes;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// A server-played wizard's sheet for Ahziael (Niduus, 2026-11-13: "pages ... to view their gear, stats and their current
/// deck and quest"), shared by the Quest Team and Ultimus. It adds these keys to a status member: "school", "mana",
/// "stats" (lines like "Damage: 12%"), "deckCards" (lines like "Frost Beetle x4"), "deckSize". Ahziael's WizardSheet shows
/// them, with "gear", "questNow", "stepNow", "questList" and "spells" when the status has them.
/// </summary>
internal static class BotSheet {

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];

    internal static void Fill(JsonObject member, Wizard wizard, string school) {
        var stats = wizard?.GameStats;
        member["school"] = school ?? "";
        member["mana"] = stats is null ? "" : $"{stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}";
        member["stats"] = new JsonArray([.. Lines(wizard, school).Select(l => (JsonNode) l)]);
        var deck = wizard?.SpellbookBehavior?.SpellList?.Where(d => d is not null && d.m_quantity > 0).ToList() ?? [];
        member["deckCards"] = new JsonArray([.. BotCards.Describe(deck.Select(d => ((uint) d.m_templateID, (int) d.m_quantity)))
            .Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(c => (JsonNode) c)]);
        member["deckSize"] = deck.Sum(d => (int) d.m_quantity);
    }

    private static List<string> Lines(Wizard wizard, string school) {
        var stats = wizard?.GameStats;
        if (stats is null) {
            return [];
        }

        static float At(List<float> list, string name)
            => list is not null && MagicSchools.GetMagicSchool(name) is { } s && s.m_schoolIndex < list.Count ? list[(int) s.m_schoolIndex] : 0f;
        static string Pct(float fraction) => $"{Math.Round(fraction * 100f)}%";

        var own = string.IsNullOrEmpty(school) ? "Fire" : school;
        var lines = new List<string> {
            $"Health: {stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}",
            $"Mana: {stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}",
            $"Damage ({own}): {Pct(At(stats.m_dmgBonusPercent, own) + stats.m_dmgBonusPercentAll)}" +
            (At(stats.m_dmgBonusFlat, own) + stats.m_dmgBonusFlatAll is var flat and > 0 ? $", +{flat:0} flat" : ""),
            $"Accuracy ({own}): {Pct(At(stats.m_accBonusPercent, own) + stats.m_accBonusPercentAll)}",
            $"Pierce ({own}): {Pct(At(stats.m_apBonusPercent, own) + stats.m_apBonusPercentAll)}",
            $"Critical rating ({own}): {At(stats.m_criticalHitRatingBySchool, own) + stats.m_criticalHitRatingAll:0}",
            $"Block rating: {stats.m_blockRatingAll:0}",
            $"Power pip chance: {Pct(stats.m_powerPipBase + stats.m_powerPipBonusPercentAll)}",
            $"Outgoing healing: {Pct(stats.m_healBonusPercentAll)}, incoming healing: {Pct(stats.m_healIncBonusPercentAll)}",
            $"Pip conversion ({own}): {At(stats.m_pipConversionRatingPerSchool, own) + stats.m_pipConversionRatingAll:0}",
            $"Shadow pip rating: {stats.m_shadowPipRating + stats.m_bonusShadowPipRating:0}",
            $"Archmastery: {stats.m_archmasteryBase:0}",
            $"Stun resistance: {Pct(stats.m_stunResistancePercent)}",
        };
        var resists = s_schools.Where(s => s != "Shadow")
            .Select(s => $"{s} {Pct(At(stats.m_dmgReducePercent, s) + stats.m_dmgReducePercentAll)}");
        lines.Add("Resist: " + string.Join(", ", resists));

        return lines;
    }

}
