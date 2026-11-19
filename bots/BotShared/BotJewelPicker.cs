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
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Which jewel a bot puts in a socket, as players pick them (Niduus, 2026-11-19), counting what its gear and the jewels
/// already put in give:
/// <list type="bullet">
/// <item>Flat damage is not sought after; flat resist only to every school, and under a % damage jewel.</item>
/// <item>Circle: % damage or pierce, whichever raises its hits more now ("circle is a toss out").</item>
/// <item>Triangle: accuracy until its school's spells hit 100%, power pip chance until 100%; past both, what helps most.</item>
/// <item>Square: % damage or resist; outgoing healing for Death and Life ("Death and Life can get away with it").</item>
/// <item>Tear and the rest: the best of its kind (the +155 health tear over the +144 one).</item>
/// </list>
/// </summary>
internal sealed class BotJewelPicker {

    // A top level enemy's resist, for how much a point of pierce is worth.
    private const double ENEMY_RESIST = 60;

    // Each school's usual spell accuracy (the game's spells), so the accuracy that brings it to 100%.
    private static readonly Dictionary<string, double> s_baseAccuracy = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = 75, ["Ice"] = 80, ["Storm"] = 70, ["Myth"] = 80, ["Life"] = 90, ["Death"] = 85, ["Balance"] = 85,
    };

    private readonly string _school;
    private readonly BotStyle _style;
    private double _damage;
    private double _pierce;
    private double _accuracy;
    private double _powerPips;

    private BotJewelPicker(string school, BotStyle style) {
        _school = school ?? "";
        _style = style;
    }

    /// <summary>A picker for a wizard, counting the stats of the gear it wears (not its jewels: add them as they go in).</summary>
    internal static BotJewelPicker For(Wizard wizard, string school, BotStyle style) {
        var picker = new BotJewelPicker(school, style);
        foreach (var item in wizard?.EquipmentBehavior?.EquippedItems ?? []) {
            picker.Add(BotGear.StatsOf(ItemHelper.GetItemTemplate(item)));
        }

        return picker;
    }

    /// <summary>Counts a jewel just put in.</summary>
    internal void Add(BotJewels.Jewel jewel) => Add(jewel?.Stats);

    private void Add(IEnumerable<BotGear.Stat> stats) {
        foreach (var stat in stats ?? []) {
            if (!Counts(stat)) {
                continue;
            }

            switch (stat.Kind) {
                case "Damage": _damage += stat.Value; break;
                case "ArmorPiercing": _pierce += stat.Value; break;
                case "Accuracy": _accuracy += stat.Value; break;
                case "PowerPips": _powerPips += stat.Value; break;
            }
        }
    }

    private bool Counts(BotGear.Stat stat) => stat.School.Length == 0 || stat.School.Equals(_school, StringComparison.OrdinalIgnoreCase);

    private double AccuracyGoal => 100 - (s_baseAccuracy.TryGetValue(_school, out var at) ? at : 80);

    /// <summary>His school's spells already hit 100% from his gear.</summary>
    internal bool AccuracyFull => _accuracy >= AccuracyGoal;

    /// <summary>His power pip chance is already 100%.</summary>
    internal bool PipsFull => _powerPips >= 100;

    /// <summary>How good a (gear) jewel is in a socket of this shape; 0 or less for one not worth putting in.</summary>
    internal double Score(BotJewels.Jewel jewel, JewelSocketType shape) {
        if (jewel is null) {
            return 0;
        }

        var stats = Wanted(jewel.Stats).ToList();
        // A full healing or full tank setup puts every socket on its one stat (outgoing healing, resist).
        // Full glass and universal damage: every socket on damage, pierce or accuracy, none on resist or health.
        if (_style is BotStyle.FullHealer or BotStyle.FullTank or BotStyle.GlassCannon or BotStyle.UniversalHitter) {
            return BotGear.ScoreStats(stats, _school, _style);
        }

        switch (shape) {
            case JewelSocketType.SOCKETTYPE_CIRCLE: {
                // How much more its hits do, in hundredths of a percent: a damage point adds to all its damage
                // (100 + damage), a pierce point takes a point off the enemy's resist until its pierce is past it.
                var gain = 0.0;
                foreach (var stat in stats.Where(Counts)) {
                    if (stat.Kind == "Damage") {
                        gain += stat.Value / (100 + _damage);
                    }
                    else if (stat.Kind == "ArmorPiercing") {
                        var left = Math.Max(0, ENEMY_RESIST - _pierce);
                        gain += Math.Min(stat.Value, left) / (100 - left);
                    }
                }

                return (gain * 10000) + (Rest(stats, "Damage", "ArmorPiercing") * 0.01);
            }
            case JewelSocketType.SOCKETTYPE_TRIANGLE: {
                // Up to 100% first: every accuracy point still missing, then every power pip point; past both, the rest.
                var filled = 0.0;
                foreach (var stat in stats.Where(Counts)) {
                    if (stat.Kind == "Accuracy") {
                        filled += Math.Min(stat.Value, Math.Max(0, AccuracyGoal - _accuracy)) * 150;
                    }
                    else if (stat.Kind == "PowerPips") {
                        filled += Math.Min(stat.Value, Math.Max(0, 100 - _powerPips)) * 100;
                    }
                }

                // Accuracy or power pips already too high: universal accuracy next, so his other schools' spells (Sun,
                // Shadow, treasure cards, a switched school) hit too (Niduus, 2026-11-19: "try to have the bot go for
                // universal accuracy"). Ahead of school accuracy even before, a little, for the same points.
                foreach (var stat in stats.Where(s => s.Kind == "Accuracy" && s.School.Length == 0)) {
                    filled += stat.Value * (AccuracyFull || PipsFull ? 40 : 1);
                }

                return filled + (Rest(stats, "Accuracy", "PowerPips") * 0.01) + Rest(stats.Where(s => s.Kind is "Accuracy" or "PowerPips"), null) * 0.0001;
            }
            case JewelSocketType.SOCKETTYPE_SQUARE: {
                var score = BotGear.ScoreStats(stats, _school, _style);
                // Death and Life heal well enough off outgoing healing to put it here, as much as % damage.
                if (_school.Equals("Death", StringComparison.OrdinalIgnoreCase) || _school.Equals("Life", StringComparison.OrdinalIgnoreCase)) {
                    score += stats.Where(s => s.Kind == "Healing" && Counts(s)).Sum(s => s.Value * 3.5);
                }

                return score;
            }
            default:
                return BotGear.ScoreStats(stats, _school, _style);
        }
    }

    private double Rest(IEnumerable<BotGear.Stat> stats, params string[] leaveOut)
        => BotGear.ScoreStats(stats.Where(s => leaveOut is null || !leaveOut.Contains(s.Kind)), _school, _style);

    // The stats a jewel is picked by: flat damage does not count, flat resist only to every school and then well under
    // a % damage jewel ("flat damage and flat resist is not usually sought after ... maybe universal flat resist for squares").
    private static IEnumerable<BotGear.Stat> Wanted(IEnumerable<BotGear.Stat> stats) {
        foreach (var stat in stats ?? []) {
            switch (stat.Kind) {
                case "FlatDamage":
                    continue;
                case "FlatReduceDamage":
                    if (stat.School.Length == 0) {
                        yield return stat with { Value = stat.Value * 0.25f };
                    }

                    continue;
                default:
                    yield return stat;
                    break;
            }
        }
    }

}
