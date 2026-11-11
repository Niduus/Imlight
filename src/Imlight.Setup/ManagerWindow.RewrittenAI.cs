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
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Rewritten AI's tuning sliders (Niduus, 2026-11-11), folded under the switch on Game Rules > Battles and World. Each
/// slider is an Imlight.ini [RewrittenAI] setting the server reads live, so a change counts from the next round.
/// </summary>
internal sealed partial class ManagerWindow {

    internal const string REWRITTEN_AI = "RewrittenAI";

    private static readonly (string Key, string Label, string Help, int Min, int Max, int Step, int Default, string Unit)[] s_rewrittenSliders = [
        ("DangerWeight", "How much blades and pips count", "A wizard with a big blade stack or lots of pips draws the attacks. 100% is normal.", 0, 300, 10, 100, "%"),
        ("DamageWeight", "How much damage done counts", "The damage a wizard did to a creature (by amount, not number of hits).", 0, 300, 10, 100, "%"),
        ("HealingWeight", "How much healing counts", "Healers draw attacks by how much they healed.", 0, 300, 10, 100, "%"),
        ("LowHealthWeight", "How much low health counts", "How strongly creatures go after a wizard who is nearly out.", 0, 300, 10, 100, "%"),
        ("MatchupWeight", "How much school matchup counts", "A fire creature wants the ice wizard and avoids the fire wizard; gear resist counts too.", 0, 300, 10, 100, "%"),
        ("ThreatFadePercent", "How fast threat fades", "How much old damage and healing threat is forgotten each round. 0 = never.", 0, 100, 5, 30, "% a round"),
        ("StubbornPercent", "How stubborn creatures are", "How far ahead another wizard must be before a creature switches target.", 0, 200, 5, 20, "%"),
        ("SpentBladesKeptPercent", "Threat kept from used-up blades", "When a wizard's blades get used in a hit, creatures keep this share of their threat.", 0, 200, 10, 50, "%"),
        ("SpentBladesKeptOnKillPercent", "Kept when that hit defeated a creature", "The same, when the hit defeated one of them: they want payback.", 0, 200, 10, 100, "%"),
        ("GangUpChance", "Gang-up chance", "Starting chance a side gangs up on one wizard (a standout wizard or a feed raises it).", 0, 100, 5, 50, "%"),
        ("MaxSetupRounds", "Most setup rounds in a row", "How many rounds a creature may spend on blades and traps before it must hit.", 0, 10, 1, 5, " rounds"),
        ("BigHitPercent", "A big enough hit", "Setup stops once the planned hit would take this share of the target's health.", 10, 100, 5, 50, "%"),
    ];

    private readonly Dictionary<string, Slider> _rewrittenSliderControls = [];
    private readonly DispatcherTimer _rewrittenSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>The folded "Rewritten AI: fine-tuning" sliders under the switch.</summary>
    private Control RewrittenAISliders() {
        var rows = new StackPanel { Spacing = 4 };
        foreach (var (key, label, help, min, max, step, value, unit) in s_rewrittenSliders) {
            var slider = new Slider {
                Minimum = min, Maximum = max, Value = value, Width = 220, TickFrequency = step, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var shown = new TextBlock { Width = 90, VerticalAlignment = VerticalAlignment.Center, Text = $"{value}{unit}" };
            slider.ValueChanged += (_, _) => {
                shown.Text = $"{(int) Math.Round(slider.Value)}{unit}";
                if (!_updatingRandomizer) {
                    _rewrittenSave.Stop();
                    _rewrittenSave.Start();
                }
            };
            _rewrittenSliderControls[key] = slider;
            rows.Children.Add(SwitchRow(label, new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, shown },
            }, help));
        }

        _rewrittenSave.Tick += async (_, _) => {
            _rewrittenSave.Stop();
            await Guard(() => {
                SaveRewrittenSliders();

                return System.Threading.Tasks.Task.CompletedTask;
            });
        };

        return Shut(Fold("sliders", "Bosses", "Rewritten AI: fine-tuning",
            "How the creatures weigh threat and plan. Each creature can have its own in Database > Bosses. Changes count from the next round.",
            rows));
    }

    private void SaveRewrittenSliders() {
        var changed = new List<Setting>();
        foreach (var (key, slider) in _rewrittenSliderControls) {
            var value = ((int) Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture);
            if (_manager.HasSetting(REWRITTEN_AI, key) && _manager.GetSetting(REWRITTEN_AI, key) != value) {
                changed.Add(new Setting(REWRITTEN_AI, key, value, ""));
            }
        }

        if (changed.Count == 0) {
            return;
        }

        _manager.SaveSettings(changed);
        _settings = [];
        Say($"Rewritten AI: {string.Join(", ", changed.Select(c => s_rewrittenSliders.First(s => s.Key == c.Key).Label.ToLowerInvariant()))} saved. "
            + "It counts from the next round, no restart needed.");
    }

    /// <summary>Shows the server's values on the sliders (called while the page refreshes).</summary>
    private void ShowRewrittenSliders(Dictionary<(string Section, string Key), string> ini) {
        foreach (var (key, _, _, min, max, _, value, _) in s_rewrittenSliders) {
            if (!_rewrittenSliderControls.TryGetValue(key, out var slider)) {
                continue;
            }

            slider.IsEnabled = ini.ContainsKey((REWRITTEN_AI, key));
            slider.Value = ini.TryGetValue((REWRITTEN_AI, key), out var text)
                           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? Math.Clamp(number, min, max) : value;
        }
    }

}
