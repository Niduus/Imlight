/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The training plan on each Ultimus and Quest Team wizard card (Niduus, 2026-11-15): what its own fights say to save
/// training points for and why, a bar of the points saved against the points the plan needs, and a Pin button to choose
/// the spell by hand (written to logs\bots\training-pins.json, read by the server within seconds; Unpin goes back to the
/// plan from its fights).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TRAINING_PINS = "training-pins.json";

    // The spells a plan can be pinned to: the ones a wizard trains from another school's trainer (the server says so in the
    // plan when one can't be trained, for example its own school's).
    private static readonly string[] s_pinnableSpells = [
        "Sprite", "Satyr", "Fairy", "Unicorn", "Tower Shield", "Feint", "Fire Shield", "Snow Shield", "Storm Shield", "Life Shield",
        "Death Shield", "Myth Shield", "Elemental Blade", "Spirit Blade", "Weakness", "Reshuffle",
    ];

    private sealed class TrainingRow {
        public TextBlock Plan = null!;
        public TextBlock Saved = null!;
        public ProgressBar Bar = null!;
        public ComboBox Pick = null!;
        public string CharId = "";
        public Control Root = null!;
    }

    private TrainingRow MakeTrainingRow(string who) {
        var row = new TrainingRow {
            Plan = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = MUTED },
            Saved = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
            Bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, MinWidth = 160, VerticalAlignment = VerticalAlignment.Center },
            Pick = new ComboBox { Width = 180, ItemsSource = s_pinnableSpells, PlaceholderText = "Pick a spell to pin" },
        };
        var pin = Action(Plain("Pin"), () => {
            if (row.Pick.SelectedItem is not string spell) {
                throw new PanelException("Pick a spell in the list first.");
            }

            SetTrainingPin(row.CharId, spell);
            Say($"Pinned: {who} saves training points for {spell} from now on (its plan updates within a few seconds).");

            return Task.CompletedTask;
        });
        ToolTip.SetTip(pin, "Choose this wizard's training goal by hand. It saves its points for this spell and the spells the trainer asks for first.");
        var unpin = Action(Plain("Unpin"), () => {
            SetTrainingPin(row.CharId, null);
            Say($"Unpinned: {who} plans its training from its own fights again.");

            return Task.CompletedTask;
        });
        ToolTip.SetTip(unpin, "Let this wizard pick its training goal from its own fights again.");
        row.Root = new StackPanel {
            Spacing = 4,
            Children = {
                row.Plan,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { row.Saved, row.Bar } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { row.Pick, pin, unpin } },
            },
        };

        return row;
    }

    private static void ShowTrainingRow(TrainingRow row, JsonObject member) {
        row.CharId = (string?) member["charId"] ?? "";
        row.Plan.Text = (string?) member["trainingPlan"] ?? "";
        var have = (int?) member["training"] ?? 0;
        var needs = (int?) member["trainingNeeds"] ?? 0;
        var goal = (string?) member["trainingGoal"] ?? "";
        row.Bar.Maximum = Math.Max(1, needs);
        row.Bar.Value = Math.Min(have, Math.Max(1, needs));
        row.Saved.Text = needs > 0 ? $"Points saved for {goal}: {Math.Min(have, needs)} of {needs}" : "No points needed right now";
        row.Root.IsVisible = row.Plan.Text.Length > 0;
    }

    private void SetTrainingPin(string charId, string? spell) {
        if (string.IsNullOrEmpty(charId)) {
            throw new PanelException("The wizard is not known yet. Start the server and let the bots log in first.");
        }

        var folder = Path.Combine(_manager.LogFolder, "bots");
        var path = Path.Combine(folder, TRAINING_PINS);
        Dictionary<string, string> pins;
        try {
            pins = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            pins = [];
        }

        if (spell is null) {
            pins.Remove(charId);
        }
        else {
            pins[charId] = spell;
        }

        try {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new PanelException("Could not save the pin: " + ex.Message);
        }
    }

}
