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
 * ASTRAL SCHOOLS
 * ========================================================================
 *
 * PURPOSE:
 * A second school a wizard can pick on top of their own: Sun, Moon, Star or Shadow (Niduus, 2026-10-27: "the
 * possibility of making some schools Moon, Sun, Star and/or Shadow"). The game only knows the 7 schools as a
 * wizard's school (icons, ranks, pips, trainers and gear exist for those alone), so the wizard keeps their real
 * school and the astral school adds its perks to their battle stats. `.astral` picks it.
 *
 * USAGE EXAMPLE:
 * AstralSchools.Apply(wizard.GameStats);   // after gear is added up, and again at the start of each battle
 *
 * NOTE:
 * - The perks of each school are in the [AstralSchools] section of Imlight.ini, such as "Damage 10, Pierce 5".
 *   They are read again at the start of every battle, so a change applies without a restart.
 * - Apply takes back what it added last time before adding the perks again, so it can run as often as it likes.
 * - Battle stats only: the game's Stats page adds up gear itself, so the perks don't show there.
 *
 * Last Updated: 10/27/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Combat;

internal static class AstralSchools {

    internal static readonly string[] NAMES = ["Sun", "Moon", "Star", "Shadow"];

    // The perks each school has when Imlight.ini says nothing.
    private static readonly Dictionary<string, string> s_defaultPerks = new(StringComparer.OrdinalIgnoreCase) {
        ["Sun"] = "Damage 10, Pierce 5",
        ["Moon"] = "Resist 10, Healing 10",
        ["Star"] = "Accuracy 10, Critical 75",
        ["Shadow"] = "Damage 5, ShadowPipRating 25",
    };

    // The words a perk can be written with, and what each means. Percents are written as whole numbers (10 for 10%).
    private static readonly Dictionary<string, string> s_statWords = new(StringComparer.OrdinalIgnoreCase) {
        ["Damage"] = "Damage", ["Resist"] = "Resist", ["Accuracy"] = "Accuracy", ["Pierce"] = "Pierce",
        ["Critical"] = "Critical", ["Crit"] = "Critical", ["Block"] = "Block",
        ["Healing"] = "Healing", ["OutgoingHealing"] = "Healing", ["IncomingHealing"] = "IncomingHealing", ["Incoming"] = "IncomingHealing",
        ["PowerPip"] = "PowerPip", ["PowerPipChance"] = "PowerPip",
        ["ShadowPipRating"] = "ShadowPipRating", ["ShadowRating"] = "ShadowPipRating",
        ["Archmastery"] = "Archmastery",
    };

    internal static bool Enabled => ConfigurationManager.GetValue("AstralSchools.Enabled", true);
    // If false, only QA and up can pick one with .astral.
    internal static bool PlayersCanPick => ConfigurationManager.GetValue("AstralSchools.PlayersCanPick", true);
    // If true, the game is told the astral school as the wizard's school outside battle, so the icon by their name, the
    // character page and character select show Sun, Moon, Star or Shadow. A test: the game was made for the 7 schools
    // only, so school gear, the spellbook or trainers may act up. Battles always use the real school. Counts from the
    // next login.
    internal static bool ShowAsSchoolIcon => ConfigurationManager.GetValue("AstralSchools.ShowAsSchoolIcon", false);

    /// <summary>
    /// The school the game is shown outside battle: the astral school with [AstralSchools] ShowAsSchoolIcon on, else
    /// the wizard's own.
    /// </summary>
    internal static uint ShownSchoolId(MagicSchool school, string astral)
        => Enabled && ShowAsSchoolIcon && Find(astral) is { } name && Enum.TryParse<MagicSchool>(name, out var shown)
            ? (uint) shown
            : (uint) school;

    /// <summary>The astral school's name as it is written ("Sun"), or null for none or an unknown name.</summary>
    internal static string Find(string name)
        => NAMES.FirstOrDefault(n => n.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The perks of a school, from [AstralSchools] in Imlight.ini.</summary>
    internal static AstralPerks PerksOf(string school) {
        if (Find(school) is not { } name) {
            return default;
        }

        var text = ConfigurationManager.GetValue($"AstralSchools.{name}", s_defaultPerks[name]);

        return Parse(text);
    }

    internal static AstralPerks Parse(string text) {
        var perks = new AstralPerks();
        foreach (var part in (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (part.Equals("none", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2
                || !s_statWords.TryGetValue(string.Concat(words[..^1]), out var stat)
                || !float.TryParse(words[^1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) {
                Logger.Warning("[AstralSchools] \"{0}\" is not a perk (write it like Damage 10); it is left out.", Logger.Args(part));
                continue;
            }

            switch (stat) {
                case "Damage": perks.Damage += amount; break;
                case "Resist": perks.Resist += amount; break;
                case "Accuracy": perks.Accuracy += amount; break;
                case "Pierce": perks.Pierce += amount; break;
                case "Critical": perks.Critical += amount; break;
                case "Block": perks.Block += amount; break;
                case "Healing": perks.Healing += amount; break;
                case "IncomingHealing": perks.IncomingHealing += amount; break;
                case "PowerPip": perks.PowerPip += amount; break;
                case "ShadowPipRating": perks.ShadowPipRating += amount; break;
                case "Archmastery": perks.Archmastery += amount; break;
            }
        }

        return perks;
    }

    /// <summary>
    /// Puts the perks of the wizard's astral school into their stats, in place of the ones added last time.
    /// </summary>
    internal static void Apply(ServerWizGameStats stats) {
        if (stats is null) {
            return;
        }

        var perks = Enabled ? PerksOf(stats.m_astralSchool) : default;
        Add(stats, stats.AstralPerksAdded, -1);
        Add(stats, perks, 1);
        stats.AstralPerksAdded = perks;
    }

    private static void Add(ServerWizGameStats stats, AstralPerks perks, int sign) {
        // Battle reads percents as fractions (0.1 for 10%); ratings are whole numbers.
        stats.m_dmgBonusPercentAll += sign * perks.Damage / 100f;
        stats.m_dmgReducePercentAll += sign * perks.Resist / 100f;
        stats.m_accBonusPercentAll += sign * perks.Accuracy / 100f;
        stats.m_apBonusPercentAll += sign * perks.Pierce / 100f;
        stats.m_healBonusPercentAll += sign * perks.Healing / 100f;
        stats.m_healIncBonusPercentAll += sign * perks.IncomingHealing / 100f;
        stats.m_powerPipBonusPercentAll += sign * perks.PowerPip / 100f;
        stats.m_criticalHitRatingAll += sign * perks.Critical;
        stats.m_blockRatingAll += sign * perks.Block;
        stats.m_bonusShadowPipRating += sign * perks.ShadowPipRating;
        stats.m_archmasteryBonus += sign * perks.Archmastery;
    }

    /// <summary>The perks in words, such as "+10% damage, +5% pierce".</summary>
    internal static string Describe(AstralPerks perks) {
        var parts = new List<string>();
        void Percent(float value, string what) { if (value != 0) parts.Add($"{(value > 0 ? "+" : "")}{value:0.##}% {what}"); }
        void Rating(float value, string what) { if (value != 0) parts.Add($"{(value > 0 ? "+" : "")}{value:0.##} {what}"); }

        Percent(perks.Damage, "damage");
        Percent(perks.Resist, "resist");
        Percent(perks.Accuracy, "accuracy");
        Percent(perks.Pierce, "pierce");
        Rating(perks.Critical, "critical rating");
        Rating(perks.Block, "block rating");
        Percent(perks.Healing, "outgoing healing");
        Percent(perks.IncomingHealing, "incoming healing");
        Percent(perks.PowerPip, "power pip chance");
        Rating(perks.ShadowPipRating, "shadow pip rating");
        Rating(perks.Archmastery, "Archmastery");

        return parts.Count == 0 ? "no perks" : string.Join(", ", parts);
    }

}

/// <summary>What an astral school adds to a wizard's battle stats. Percents are whole numbers (10 for 10%).</summary>
internal record struct AstralPerks(
    float Damage, float Resist, float Accuracy, float Pierce, float Critical, float Block,
    float Healing, float IncomingHealing, float PowerPip, float ShadowPipRating, float Archmastery);
