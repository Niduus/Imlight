using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Imlight.Setup;

/// <summary>
/// The Plant growth card on the Server tab (Niduus, 2026-10-25: "Make them obey to configs in manager for their age. and
/// dynamically update"): how many times faster than the game plants grow, in all and for each stage, and a pause. The
/// app writes garden-growth.json next to the server; the server reads it within a second and every garden takes the new
/// speeds at its next growth check, plants halfway through a stage included (Game/Gardening/GardenTiming). The
/// `.garden speed`, `.garden pause` and `.garden resume` commands write the same file.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string GARDEN_GROWTH_FILE = "garden-growth.json";

    // The file's keys, the names shown, and what each one changes.
    private static readonly (string Key, string Name, string Help)[] s_gardenSpeeds = [
        ("Speed", "Every plant", "All plant times. 1 = the game's own times, 60 = an hour becomes a minute, 0.5 = twice as slow."),
        ("Seedling", "Seedling stage", "How fast a seedling grows into a young plant, on top of Every plant."),
        ("Young", "Young stage", "How fast a young plant grows into a mature one."),
        ("Mature", "Mature stage", "How fast a mature plant grows into an elder one."),
        ("Harvest", "Harvests", "How soon a mature plant is ready to harvest again."),
        ("Needs", "Needs and pests", "How soon plants need water, sun, bees, magic or music, how often pests come, how long until a plant wilts, and how long protections last."),
    ];

    private readonly Dictionary<string, NumericUpDown> _gardenSpeedBoxes = [];
    private readonly ToggleSwitch _gardenPaused = new() { OnContent = "Paused", OffContent = "Growing" };
    private readonly TextBlock _gardenGrowthNote = Sub("");
    private bool _showingGardenGrowth;

    private string GardenGrowthPath => Path.Combine(_manager.ServerFolder, GARDEN_GROWTH_FILE);

    private Control PlantGrowthCard() {
        var rows = new StackPanel { Spacing = 4 };
        foreach (var (key, name, help) in s_gardenSpeeds) {
            var box = new NumericUpDown { Minimum = 0.01m, Maximum = 100000, Increment = 1, FormatString = "0.##", Width = 140, Value = 1 };
            _gardenSpeedBoxes[key] = box;
            box.ValueChanged += async (_, _) => {
                if (!_showingGardenGrowth) {
                    await Guard(SaveGardenGrowthAsync);
                }
            };
            rows.Children.Add(Row(new TextBlock { Text = name, Width = 150, VerticalAlignment = VerticalAlignment.Center }, box,
                new TextBlock { Text = "times faster", VerticalAlignment = VerticalAlignment.Center }));
            rows.Children.Add(WithMargin(Sub(help), new Thickness(14, 0, 0, 6)));
        }

        _gardenPaused.IsCheckedChanged += async (_, _) => {
            if (!_showingGardenGrowth) {
                await Guard(SaveGardenGrowthAsync);
            }
        };

        LoadGardenGrowth();

        return Shut(Fold("leaf", "Server", "Plant growth",
            "How fast garden plants grow. Changes count right away, also for plants already growing: the time a plant still " +
            "has left is stretched or shrunk to the new speed. A stage's time is divided by Every plant times its own " +
            "number. Pause stops every garden until you switch it back. In game, .garden times lists each plant's times.",
            SwitchRow("Pause every garden", _gardenPaused),
            rows,
            Row(Action(Plain("Game's own times"), ResetGardenGrowthAsync), Action(Plain("Reload"), () => {
                LoadGardenGrowth();

                return Task.CompletedTask;
            })),
            _gardenGrowthNote));
    }

    private async Task ResetGardenGrowthAsync() {
        _showingGardenGrowth = true;
        foreach (var box in _gardenSpeedBoxes.Values) {
            box.Value = 1;
        }

        _gardenPaused.IsChecked = false;
        _showingGardenGrowth = false;
        await SaveGardenGrowthAsync();
    }

    private void LoadGardenGrowth() {
        _showingGardenGrowth = true;
        try {
            var saved = File.Exists(GardenGrowthPath) ? JsonNode.Parse(File.ReadAllText(GardenGrowthPath)) as JsonObject : null;
            foreach (var (key, box) in _gardenSpeedBoxes) {
                var value = saved?[key] is JsonValue node && node.TryGetValue<double>(out var number) ? number : 1;
                if (key == "Speed" && saved is null
                    && double.TryParse(IniFile.Get(_manager.ConfigFile, "Gardening", "Speed"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ini)) {
                    value = ini;
                }

                box.Value = (decimal) Math.Clamp(value, 0.01, 100000);
            }

            _gardenPaused.IsChecked = saved?["Paused"] is JsonValue paused && paused.TryGetValue<bool>(out var on) && on;
            _gardenGrowthNote.Text = saved is null ? "The game's own times (nothing changed yet)." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read the plant growth settings: {ex.Message}", isError: true);
        }
        finally {
            _showingGardenGrowth = false;
        }
    }

    private async Task SaveGardenGrowthAsync() {
        var data = new JsonObject();
        foreach (var (key, box) in _gardenSpeedBoxes) {
            data[key] = (double) (box.Value ?? 1);
        }

        data["Paused"] = _gardenPaused.IsChecked == true;
        data["ChangedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{GardenGrowthPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, GardenGrowthPath, overwrite: true);
        _gardenGrowthNote.Text = _gardenPaused.IsChecked == true
            ? "Saved. Every garden is paused."
            : "Saved. Gardens use these speeds within a few seconds.";
    }

}
