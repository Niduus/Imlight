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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Gear tab's stat filters (Niduus, 2026-10-25: "add to gear search in manager to filter and/or search for stats",
/// "also to search for set bonuses"): up to three stats, each with a school and a lowest amount, matched all together
/// (and) or any one of them (or), counted on the item itself, with its full set bonus, or on its set bonus alone. The
/// set picker shows only gear that is part of a set, or the pieces of one set.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ANY_STAT = "Any stat";
    private const string ANY_STAT_SCHOOL = "Any school";
    private const string UNIVERSAL = "Universal (all schools)";
    private const string ANY_SET = "Any, in a set or not";
    private const string IN_A_SET = "Only gear in a set";

    private static readonly string[] s_statMatches = ["Has all of them (and)", "Has any of them (or)"];
    private static readonly string[] s_statFrom = ["The item itself", "The item and its full set bonus", "Its set bonus only"];
    private static readonly string[] s_statSchoolOrder = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow"];

    // The order the stats are listed in: the ones people look for most come first.
    private static readonly string[] s_statOrder = [
        "Damage", "FlatDamage", "ReduceDamage", "FlatReduceDamage", "Accuracy", "ArmorPiercing", "CriticalHit", "Block",
        "MaxHealth", "PowerPips", "Healing", "IncHealing", "ShadowPipRating", "Archmastery", "PipConversion", "StunResistance",
        "Mastery", "StartingPips", "StartingPowerPips", "MaxMana", "MaxEnergy", "Speed",
    ];

    private readonly (ComboBox Stat, ComboBox School, NumericUpDown Min)[] _gearStats = [.. Enumerable.Range(0, 3).Select(_ => (
        new ComboBox { MinWidth = 190 },
        new ComboBox { MinWidth = 170 },
        new NumericUpDown { Minimum = -9999, Maximum = 99999, Increment = 5, FormatString = "0", Width = 120, Watermark = "Any" }))];

    private readonly ComboBox _gearStatMatch = new() { ItemsSource = s_statMatches, SelectedIndex = 0, MinWidth = 190 };
    private readonly ComboBox _gearStatFrom = new() { ItemsSource = s_statFrom, SelectedIndex = 0, MinWidth = 220 };
    private readonly ComboBox _gearSetPick = new() { ItemsSource = new[] { ANY_SET }, SelectedIndex = 0, MinWidth = 220 };

    /// <summary>A stat in the stat pickers, shown by its plain name.</summary>
    private sealed record GearStatChoice(string Kind, string Name) {

        public override string ToString() => Name;

    }

    /// <summary>The stat filters under the other filters.</summary>
    private Control GearStatFilters() {
        foreach (var box in _gearStats.SelectMany(r => new[] { r.Stat, r.School }).Append(_gearStatMatch).Append(_gearStatFrom).Append(_gearSetPick)) {
            box.SelectionChanged += (_, e) => {
                if (e.Source == box) {
                    RenderGearTab();
                }
            };
        }

        foreach (var (_, _, min) in _gearStats) {
            min.ValueChanged += (_, _) => {
                _gearDelay.Stop();
                _gearDelay.Start();
            };
        }

        Control Labelled(string label, Control box) => new StackPanel {
            Spacing = 2, Margin = new Thickness(0, 0, 10, 6),
            Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12 }, box },
        };

        var rows = new StackPanel();
        for (var i = 0; i < _gearStats.Length; i++) {
            var (stat, school, min) = _gearStats[i];
            rows.Children.Add(new WrapPanel {
                Children = { Labelled($"Stat {i + 1}", stat), Labelled("For school", school), Labelled("At least", min) },
            });
        }

        rows.Children.Add(new WrapPanel {
            Children = {
                Labelled("Match", _gearStatMatch),
                Labelled("Count stats from", _gearStatFrom),
                Labelled("Set bonus", _gearSetPick),
                WithMargin(Action(Plain("Clear stats"), () => {
                    ClearGearStats();

                    return Task.CompletedTask;
                }), new Thickness(0, 18, 0, 6)),
            },
        });

        return WithHint(rows, "Pick up to three stats to find gear that has them, such as Damage for Fire at least 40 and Resist for " +
            "Any school. \"Any school\" counts a school's own stat plus the one for all schools, so +10% Damage and +5% Fire Damage is " +
            "15 for Fire. Match \"all\" needs every stat, \"any\" needs one of them. \"Count stats from\" can add the item's full set " +
            "bonus, or look at the set bonus only. Sort by \"Picked stat\" to see the highest of Stat 1 first. Typing a set's name in " +
            "the search finds its pieces too.");
    }

    private void ClearGearStats() {
        foreach (var (stat, school, min) in _gearStats) {
            stat.SelectedIndex = 0;
            school.SelectedIndex = 0;
            min.Value = null;
        }

        _gearStatMatch.SelectedIndex = 0;
        _gearStatFrom.SelectedIndex = 0;
        _gearSetPick.SelectedIndex = 0;
        RenderGearTab();
    }

    /// <summary>Fills the stat and set pickers from the stats the game's gear and set bonuses really have.</summary>
    private void FillGearStatFilters(GearBook gear) {
        var allStats = gear.Items.SelectMany(i => i.Stats).Concat(gear.Sets.SelectMany(s => s.AllStats)).Where(s => s.Kind.Length > 0).ToList();
        var stats = allStats.Select(s => s.Kind).Distinct()
            .OrderBy(k => Array.IndexOf(s_statOrder, k) is var i and >= 0 ? i : 100).ThenBy(GearBook.StatName, StringComparer.OrdinalIgnoreCase)
            .Select(k => new GearStatChoice(k, GearBook.StatName(k)));
        var schools = allStats.Select(s => s.School).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => Array.IndexOf(s_statSchoolOrder, s) is var i and >= 0 ? i : 100).ThenBy(s => s, StringComparer.OrdinalIgnoreCase);
        var statList = new[] { new GearStatChoice("", ANY_STAT) }.Concat(stats).ToList();
        var schoolList = new[] { ANY_STAT_SCHOOL, UNIVERSAL }.Concat(schools).ToList();
        foreach (var (stat, school, _) in _gearStats) {
            stat.ItemsSource = statList;
            stat.SelectedIndex = 0;
            school.ItemsSource = schoolList;
            school.SelectedIndex = 0;
        }

        _gearSetPick.ItemsSource = new[] { ANY_SET, IN_A_SET }.Concat(gear.Sets.Where(s => gear.Items.Any(i => i.Set == s))
            .Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
        _gearSetPick.SelectedIndex = 0;
    }

    /// <summary>The stat rows that have a stat picked.</summary>
    private List<(string Kind, string Name, string School, int? Min)> PickedGearStats() =>
        [.. _gearStats.Where(r => r.Stat.SelectedItem is GearStatChoice { Kind.Length: > 0 })
            .Select(r => {
                var choice = (GearStatChoice) r.Stat.SelectedItem!;

                return (choice.Kind, choice.Name, r.School.SelectedItem as string ?? ANY_STAT_SCHOOL, (int?) r.Min.Value);
            })];

    /// <summary>The stats that count for the filters: the item's own, with its set bonus, or the set bonus alone.</summary>
    private IEnumerable<GearStat> CountedGearStats(GearItem item) => _gearStatFrom.SelectedIndex switch {
        1 => item.Stats.Concat(item.Set?.AllStats ?? []),
        2 => item.Set?.AllStats ?? [],
        _ => item.Stats,
    };

    /// <summary>
    /// The item's total of one stat for a school, or null when it has none of it. A school's own stat and the stat for
    /// all schools add up, as they do in battle; "Any school" takes the school where the item is best.
    /// </summary>
    private static int? GearStatTotal(IEnumerable<GearStat> stats, string kind, string school) {
        var lines = stats.Where(s => s.Kind == kind).ToList();
        if (lines.Count == 0) {
            return null;
        }

        var universal = lines.Where(s => s.School.Length == 0).ToList();
        if (school == UNIVERSAL) {
            return universal.Count > 0 ? universal.Sum(s => s.Amount) : null;
        }

        var bySchool = lines.Where(s => s.School.Length > 0).GroupBy(s => s.School, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Amount), StringComparer.OrdinalIgnoreCase);
        if (school == ANY_STAT_SCHOOL) {
            return universal.Sum(s => s.Amount) + (bySchool.Count > 0 ? bySchool.Values.Max() : 0);
        }

        return universal.Count > 0 || bySchool.ContainsKey(school) ? universal.Sum(s => s.Amount) + bySchool.GetValueOrDefault(school) : null;
    }

    /// <summary>A filter for the picked stats and set, or null when none is picked.</summary>
    private Func<GearItem, bool>? GearStatFilter() {
        var picked = PickedGearStats();
        var set = _gearSetPick.SelectedItem as string ?? ANY_SET;
        if (picked.Count == 0 && set == ANY_SET) {
            return null;
        }

        var any = _gearStatMatch.SelectedIndex == 1;

        return item => {
            if (set == IN_A_SET ? item.Set is null : set != ANY_SET && !string.Equals(item.Set?.Name, set, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            if (picked.Count == 0) {
                return true;
            }

            var stats = CountedGearStats(item).ToList();
            bool Has((string Kind, string Name, string School, int? Min) p) =>
                GearStatTotal(stats, p.Kind, p.School) is { } total && (p.Min is null || total >= p.Min);

            return any ? picked.Any(Has) : picked.All(Has);
        };
    }

    /// <summary>The amount of Stat 1 (the first picked stat), for sorting by it.</summary>
    private int GearStatSortKey(GearItem item) => PickedGearStats() is [var first, ..]
        ? GearStatTotal(CountedGearStats(item), first.Kind, first.School) ?? int.MinValue
        : 0;

    /// <summary>The picked stats' totals for an item's line in the list, such as "Fire Damage +40%".</summary>
    private string GearStatSummary(GearItem item) {
        var parts = new List<string>();
        var stats = CountedGearStats(item).ToList();
        foreach (var (kind, name, school, _) in PickedGearStats()) {
            if (GearStatTotal(stats, kind, school) is { } total) {
                var percent = stats.Any(s => s.Kind == kind && s.Percent);
                var schoolName = school is ANY_STAT_SCHOOL or UNIVERSAL ? "" : school + " ";
                parts.Add(kind == "Mastery" ? $"{schoolName}Mastery" : $"{schoolName}{name} {Signed(total)}{(percent ? "%" : "")}");
            }
        }

        if (item.Set is { } set && (_gearSetPick.SelectedIndex > 0 || _gearStatFrom.SelectedIndex > 0)) {
            parts.Add(set.Name);
        }

        return string.Join("  ·  ", parts);
    }

    private static string Signed(int value) => (value >= 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A stat in words, such as "+40% Fire Damage" or "+1150 Max Health".</summary>
    private static string GearStatWords(GearStat stat) {
        var school = stat.School.Length > 0 ? stat.School + " " : "";

        return stat.Kind switch {
            "Mastery" => $"{school}Mastery",
            "" => $"{stat.Value} {stat.After}".Trim(),
            _ => $"{Signed(stat.Amount)}{(stat.Percent ? "%" : "")} {school}{GearBook.StatName(stat.Kind)}",
        };
    }

    /// <summary>The item's set bonus under its facts: the set's name and what each number of pieces adds.</summary>
    private void ShowGearSet(GearBook gear, GearItem item) {
        if (item.Set is not { } set) {
            return;
        }

        _gearDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));
        _gearDetails.Children.Add(new TextBlock { Text = $"Set bonus: {set.Name}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var tier in set.Tiers) {
            var words = tier.Stats.Select(GearStatWords)
                .Concat(tier.Spells.Select(s => (gear.Spell(s.Code)?.Name ?? s.Code) + (s.Copies > 1 ? $" x{s.Copies}" : "") + " card"))
                .ToList();
            var text = words.Count > 0 ? string.Join(", ", words) : tier.Description;
            _gearDetails.Children.Add(new DockPanel {
                Children = {
                    WithDock(new TextBlock { Text = $"{tier.Pieces} pieces", Width = 110, Foreground = MUTED }, Dock.Left),
                    new SelectableTextBlock { Text = text.Length > 0 ? text : "(the game files don't say)", TextWrapping = TextWrapping.Wrap },
                },
            });
        }

        var pieces = gear.Items.Where(i => i.Set == set).ToList();
        _gearDetails.Children.Add(Sub($"{pieces.Count} item{(pieces.Count == 1 ? "" : "s")} in the game files belong to this set."));
        _gearDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Plain("Show the whole set"), () => {
                    _gearSearch.Text = "";
                    _gearSetPick.SelectedItem = set.Name;

                    return Task.CompletedTask;
                })),
            },
        });
    }

}
