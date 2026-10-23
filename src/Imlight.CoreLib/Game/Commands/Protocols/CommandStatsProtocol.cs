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
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.stats` (Niduus, 2026-10-23: "make sure the server is getting it as well and gear is always as said on the
/// client"): the damage, resist, pierce and accuracy the server fights with, school by school, to hold next to the
/// game's own Stats page. No diminishing returns: these are the plain sums of gear, jewels and pet. `.stats damage`
/// turns the per-hit damage lines on or off.
/// </summary>
internal class CommandStatsProtocol : CommandProtocol {

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];

    internal override string Group { get; set; } = "";

    [Command("stats")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void StatsCommand(string what = "") {
        // `.stats damage` turns the "[enemy]: dealt [damage]" lines after each hit on or off; they start off.
        if (what.Equals("damage", StringComparison.OrdinalIgnoreCase)) {
            var charId = Context.Character.CharId;
            var on = CombatEffectApplicator.DamageLinesOn.TryAdd(charId, 0) || !CombatEffectApplicator.DamageLinesOn.TryRemove(charId, out _);
            InformSenderClient(on ? "Damage lines on: after each hit you see what it dealt to each enemy."
                : "Damage lines off.");

            return;
        }

        var stats = Context.Character.GameStats;
        if (stats is null) {
            InformSenderClient("No stats found.");

            return;
        }

        static float School(List<float> list, string school)
            => list is not null && MagicSchools.GetMagicSchool(school) is { } s && s.m_schoolIndex < list.Count ? list[(int) s.m_schoolIndex] : 0f;
        static string Pct(float raw) => $"{Math.Round(CombatEffectApplicator.AsFraction(raw) * 100f)}%";

        InformSenderClient("Server stats (damage / resist / pierce / accuracy, flat damage):");
        foreach (var school in s_schools) {
            var damage = School(stats.m_dmgBonusPercent, school) + stats.m_dmgBonusPercentAll;
            var resist = School(stats.m_dmgReducePercent, school) + stats.m_dmgReducePercentAll;
            var pierce = School(stats.m_apBonusPercent, school) + stats.m_apBonusPercentAll;
            var accuracy = School(stats.m_accBonusPercent, school) + stats.m_accBonusPercentAll;
            var flat = School(stats.m_dmgBonusFlat, school) + stats.m_dmgBonusFlatAll;
            InformSenderClient($"{school}: {Pct(damage)} / {Pct(resist)} / {Pct(pierce)} / {Pct(accuracy)}, +{flat}");
            Logger.Debug(".stats {0}: raw damage {1}, resist {2}, pierce {3}, accuracy {4}, flat {5}.",
                Logger.Args(school, damage, resist, pierce, accuracy, flat));
        }

        var conversion = string.Join(", ", s_schools.Where(x => x != "Shadow")
            .Select(x => $"{x} {Math.Round(School(stats.m_pipConversionRatingPerSchool, x) + stats.m_pipConversionRatingAll)}"));
        InformSenderClient($"Pip Conversion: {conversion}");
        InformSenderClient($"Stun Resistance: {Math.Round(stats.m_stunResistancePercent * 100f)}%");
    }

}
