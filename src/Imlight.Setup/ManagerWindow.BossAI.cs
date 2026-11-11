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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// One creature's Rewritten AI in the Bosses tab (Niduus, 2026-11-11: "free customizations and exclusions"): whether it
/// uses it, its part in the team, its own weights and what it never does. Saved in its BossOverrides file; the server
/// reads it every round.
/// </summary>
internal sealed partial class ManagerWindow {

    private static readonly string[] s_aiModes = ["Use the server's switch", "Always on", "Always off"];

    private static readonly (string Value, string Label, string Help)[] s_aiRoles = [
        ("", "Automatic", "Picked from the fight: the one boss among ordinary creatures leads, a deck of heals heals, one of shields protects."),
        ("Leader", "Leader", "The star of the fight: blades itself, and teammates feed it blades of its school."),
        ("Protector", "Protector", "Shields its team against the schools in play and answers big blade stacks."),
        ("Healer", "Healer", "Watches its team's health and heals the one most in danger, and heals ahead of a feed."),
        ("Striker", "Striker", "All damage: short setup, goes for knockouts."),
        ("Support", "Support", "Blades and traps for teammates of the matching school first."),
        ("Free", "Free", "No set part: the general rules."),
    ];

    private static readonly (string Key, string Label)[] s_aiExclusions = [
        ("NoTeamShields", "Never shields teammates"),
        ("NoTeamHeals", "Never heals teammates"),
        ("NoTeamBlades", "Never blades teammates"),
        ("NoTraps", "Never casts traps"),
        ("NoSetup", "Never sets up (no blades or traps before hitting)"),
        ("NoMatchup", "Ignores school matchup"),
        ("NoGangUp", "Never gangs up with its side"),
        ("NoHoldBack", "Never holds back to save pips"),
    ];

    private readonly ComboBox _bossAIMode = new() { Width = 220, ItemsSource = s_aiModes, SelectedIndex = 0 };
    private readonly ComboBox _bossAIRole = new() { Width = 220, ItemsSource = s_aiRoles.Select(r => r.Label).ToList(), SelectedIndex = 0 };
    private readonly Dictionary<string, CheckBox> _bossAIExclusions = [];
    private readonly TextBox _bossAINeverCast = new() { Width = 320, Watermark = "Card names, ; between them (Fire Cat; Tower Shield)" };

    /// <summary>The "Rewritten AI" rows under the creature's other stats.</summary>
    private void AddBossAIRows(StackPanel stats) {
        stats.Children.Add(WithMargin(new TextBlock { Text = "Rewritten AI", FontSize = 15, FontWeight = FontWeight.SemiBold },
            new Thickness(0, 14, 0, 0)));
        stats.Children.Add(Sub("How it thinks with Rewritten AI (Game Rules > Battles and World). Empty boxes use the server's fine-tuning. " +
                               "Counts from its next round, even in a battle already going."));
        stats.Children.Add(StatRow("Rewritten AI", _bossAIMode, "Always on gives just this creature the new AI, even with the switch off."));
        ToolTip.SetTip(_bossAIRole, string.Join("\n", s_aiRoles.Select(r => $"{r.Label}: {r.Help}")));
        stats.Children.Add(StatRow("Its part in the team", _bossAIRole,
            "Leader blades itself and gets fed; Protector shields; Healer heals; Striker hits; Support blades teammates. Hover for more."));
        foreach (var (key, label, help) in BossChanges.AI_STATS) {
            var box = new TextBox { Width = 140, Watermark = "Server's value" };
            _bossStatBoxes[key] = box;
            stats.Children.Add(StatRow(label, box, help));
        }

        var ticks = new WrapPanel();
        foreach (var (key, label) in s_aiExclusions) {
            var tick = new CheckBox { Content = label, Margin = new Thickness(0, 0, 16, 4) };
            _bossAIExclusions[key] = tick;
            ticks.Children.Add(tick);
        }

        stats.Children.Add(StatRow("Never does", ticks, "Tick what this creature should never do."));
        stats.Children.Add(StatRow("Never casts", _bossAINeverCast, "Cards from its deck it never casts with Rewritten AI."));
    }

    private void ShowBossAI(BossChanges? changes) {
        _bossAIMode.SelectedIndex = (changes?.AIMode ?? "").ToLowerInvariant() switch { "on" => 1, "off" => 2, _ => 0 };
        var role = Array.FindIndex(s_aiRoles, r => r.Value.Length > 0 && r.Value.Equals(changes?.AIRole, StringComparison.OrdinalIgnoreCase));
        _bossAIRole.SelectedIndex = Math.Max(0, role);
        foreach (var (key, tick) in _bossAIExclusions) {
            tick.IsChecked = changes?.AIExclusions.Contains(key, StringComparer.OrdinalIgnoreCase) == true;
        }

        _bossAINeverCast.Text = changes?.AINeverCast ?? "";
    }

    private void BossAIInto(BossChanges changes) {
        changes.AIMode = _bossAIMode.SelectedIndex switch { 1 => "On", 2 => "Off", _ => null };
        changes.AIRole = _bossAIRole.SelectedIndex > 0 ? s_aiRoles[_bossAIRole.SelectedIndex].Value : null;
        changes.AIExclusions = [.. _bossAIExclusions.Where(t => t.Value.IsChecked == true).Select(t => t.Key)];
        var never = string.Join("; ", (_bossAINeverCast.Text ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        changes.AINeverCast = never.Length > 0 ? never : null;
    }

    private static int? AIStatNumber(string key, TextBox box, string label)
        => key == "AISetupRounds" ? WholeNumber(box, label, 0, 10) : WholeNumber(box, label, 0, 1000);

}
