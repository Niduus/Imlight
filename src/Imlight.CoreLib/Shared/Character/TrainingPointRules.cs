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
using System.Text.RegularExpressions;

namespace Imlight.CoreLib.Shared.Character;

/// <summary>
/// The [Trainers] TrainingPointRules list: extra training points on level up, on top of the game's own
/// (NaturalTrainingPoints). One rule reads "2 after 10" (2 points for every level past 10), "1 after 50 every 5"
/// (levels 55, 60, ...) or "3 after 20 until 40" (levels 21 to 40); rules are split by ";". The Manager uses this
/// same file to show and save the list.
/// </summary>
internal static partial class TrainingPointRules {

    internal const int MAX_POINTS = 1000;
    internal const int MAX_LEVEL = 1000;

    /// <param name="Points">Training points each matching level gives.</param>
    /// <param name="After">Levels past this one count (0 = every level).</param>
    /// <param name="Every">Only every Nth level past <paramref name="After"/> (1 = each level).</param>
    /// <param name="Until">The last level that counts, 0 = no end.</param>
    internal sealed record Rule(int Points, int After, int Every = 1, int Until = 0) {

        internal bool Gives(int level) => level > After
            && (Until <= 0 || level <= Until)
            && (level - After) % Math.Max(1, Every) == 0;

        public override string ToString() {
            var text = $"{Points} after {After}";
            if (Every > 1) {
                text += $" every {Every}";
            }

            if (Until > 0) {
                text += $" until {Until}";
            }

            return text;
        }

    }

    [GeneratedRegex(@"^\s*(\d+)\s*(?:training\s*)?(?:points?|tp)?\s*(?:after|past)\s*(?:level\s*)?(\d+)" +
        @"(?:\s*every\s*(\d+)(?:\s*levels?)?)?(?:\s*(?:until|to|through)\s*(?:level\s*)?(\d+))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RuleText();

    /// <summary>Reads the rules out of the setting's text; parts that aren't a rule are left out.</summary>
    internal static List<Rule> Parse(string? text) {
        List<Rule> rules = [];
        foreach (var part in (text ?? "").Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)) {
            var match = RuleText().Match(part);
            if (!match.Success) {
                continue;
            }

            static int Number(Group group, int none, int max)
                => group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    ? Math.Clamp(n, 0, max) : none;

            var points = Number(match.Groups[1], 0, MAX_POINTS);
            if (points <= 0) {
                continue;
            }

            rules.Add(new Rule(points, Number(match.Groups[2], 0, MAX_LEVEL),
                Math.Max(1, Number(match.Groups[3], 1, MAX_LEVEL)), Number(match.Groups[4], 0, MAX_LEVEL)));
        }

        return rules;
    }

    /// <summary>The setting's text for these rules.</summary>
    internal static string Format(IEnumerable<Rule> rules) => string.Join("; ", rules.Where(r => r.Points > 0));

    /// <summary>Training points the rules give for reaching <paramref name="level"/>.</summary>
    internal static int For(IEnumerable<Rule> rules, int level) => rules.Where(r => r.Gives(level)).Sum(r => r.Points);

    /// <summary>A rule in plain words, for the Manager.</summary>
    internal static string Describe(Rule rule) {
        var points = rule.Points == 1 ? "1 training point" : $"{rule.Points} training points";
        var start = rule.After + rule.Every;
        var which = rule.Every > 1
            ? $"every {rule.Every} levels from level {start} ({start}, {start + rule.Every}, {start + (2 * rule.Every)}...)"
            : rule.After <= 0 ? "every level" : $"every level from level {start}";
        return $"Gain {points} {which}{(rule.Until > 0 ? $" up to level {rule.Until}" : "")}";
    }

}
