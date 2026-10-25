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
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Member benefits card on the Server tab (Niduus, 2026-10-23): a switch for each member benefit, each with an
/// optional time after which it turns itself off. The app writes member-benefits.json next to the server; the server
/// picks it up within a second, sends the game the new benefits and tells everyone online what turned on or off.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string MEMBER_BENEFITS_FILE = "member-benefits.json";

    // The game's member benefits, as the server's MemberBenefits.All has them.
    private static readonly (string Key, string Name, string Does)[] s_memberBenefits = [
        ("GardeningBonus", "Gardening Bonus", "Harvests give twice the gold, items and gardening XP."),
        ("DoublePetXP", "Double Pet XP", "Pet games and snacks give double pet XP."),
        ("FreeRespec", "Free Respec", "Training point resets are always free on this server, on or off."),
        ("DailyQuestBonus", "Daily Quest Bonus", "Daily Assignments give twice the gold and crowns, and better chests."),
        ("MonsterMagic", "Monster Magic", "Monstrology extracts give double Animus."),
        ("DailyPvPBonus", "Daily PvP Bonus", "Not on this server yet: it has no daily PvP assignments."),
        ("FishingBonus", "Fishing Bonus", "Fishing costs no energy."),
        ("CraftingBonus", "Crafting Bonus", "No crafting cooldowns."),
        ("SecondChanceChestBonus", "Second Chance Chest", "Not on this server yet: it has no second chance chests."),
        ("DoubleScrollOfFortunePoints", "Double Scroll of Fortune Points", "Scroll of Fortune points count double."),
        ("FasterEnergyRegeneration", "Faster Energy Regeneration", "Energy comes back twice as fast."),
    ];

    private sealed class BenefitRow {
        internal required string Key;
        internal readonly ToggleSwitch Switch = new() { OnContent = "On", OffContent = "Off" };
        internal readonly NumericUpDown Days = new() { Minimum = 0, Maximum = 365, Increment = 1, FormatString = "0", Width = 110 };
        internal readonly NumericUpDown Hours = new() { Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0", Width = 110 };
        internal readonly TextBlock Left = Sub("");
        internal DateTime? Until;
    }

    private readonly List<BenefitRow> _benefitRows = [];
    private bool _showingBenefits;

    private string MemberBenefitsPath => Path.Combine(_manager.ServerFolder, MEMBER_BENEFITS_FILE);

    private Control MemberBenefitsCard() {
        var rows = new StackPanel { Spacing = 4 };
        foreach (var (key, name, does) in s_memberBenefits) {
            var row = new BenefitRow { Key = key };
            _benefitRows.Add(row);
            row.Switch.IsCheckedChanged += async (_, _) => {
                if (!_showingBenefits) {
                    // Switching on starts the timer, if it has one.
                    row.Until = row.Switch.IsChecked == true ? EndFromNow(row) : null;
                    await Guard(SaveBenefitsAsync);
                }
            };
            foreach (var box in new[] { row.Days, row.Hours }) {
                box.ValueChanged += async (_, _) => {
                    if (!_showingBenefits && row.Switch.IsChecked == true) {
                        // A new time while it is on counts from now.
                        row.Until = EndFromNow(row);
                        await Guard(SaveBenefitsAsync);
                    }
                };
            }

            var timer = Row(new TextBlock { Text = "Turn off after", VerticalAlignment = VerticalAlignment.Center }, row.Days,
                new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center }, row.Hours,
                new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center });
            rows.Children.Add(SwitchRow(name, row.Switch));
            rows.Children.Add(WithMargin(new StackPanel { Spacing = 2, Children = { Sub(does), timer, row.Left } }, new Thickness(14, 0, 0, 8)));
        }

        LoadBenefits();

        // The time left, counting down while the window is open; a benefit whose time ran out shows off.
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => ShowBenefitTimes();
        ticker.Start();
        ShowBenefitTimes();

        return Shut(Fold("star", "Server", "Member benefits",
            "Turn each member benefit on or off. Everyone online is told when one changes, and wizards logging in are told " +
            "which are on. Set days or hours to have a benefit turn itself off after that long (0 and 0: it stays on). " +
            "Under each one is what it does on this server.",
            Row(Action(Plain("All on"), () => SetAllBenefitsAsync(true)), Action(Plain("All off"), () => SetAllBenefitsAsync(false))),
            rows));
    }

    private static DateTime? EndFromNow(BenefitRow row) {
        var span = TimeSpan.FromDays((double) (row.Days.Value ?? 0)) + TimeSpan.FromHours((double) (row.Hours.Value ?? 0));

        return span > TimeSpan.Zero ? DateTime.UtcNow + span : null;
    }

    private async Task SetAllBenefitsAsync(bool on) {
        _showingBenefits = true;
        foreach (var row in _benefitRows) {
            row.Switch.IsChecked = on;
            row.Until = on ? EndFromNow(row) : null;
        }
        _showingBenefits = false;
        await SaveBenefitsAsync();
    }

    private void LoadBenefits() {
        _showingBenefits = true;
        try {
            var saved = new Dictionary<string, JsonNode>();
            if (File.Exists(MemberBenefitsPath)
                && JsonNode.Parse(File.ReadAllText(MemberBenefitsPath))?["Benefits"] is JsonArray list) {
                foreach (var item in list.OfType<JsonObject>()) {
                    if (item["Key"]?.GetValue<string>() is { } key) {
                        saved[key] = item;
                    }
                }
            }

            // No file yet: the old switch decides, every benefit on or none.
            var allOn = !File.Exists(MemberBenefitsPath)
                && !string.Equals(IniFile.Get(_manager.ConfigFile, "Character", "MemberBenefits"), "false", StringComparison.OrdinalIgnoreCase);
            foreach (var row in _benefitRows) {
                var item = saved.GetValueOrDefault(row.Key);
                row.Days.Value = item?["Days"]?.GetValue<int>() ?? 0;
                row.Hours.Value = item?["Hours"]?.GetValue<int>() ?? 0;
                row.Until = item?["Until"]?.GetValue<string>() is { } until && DateTime.TryParse(until, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var end) ? end.ToUniversalTime() : null;
                var on = item is null ? allOn : item["On"]?.GetValue<bool>() == true;
                row.Switch.IsChecked = on && (row.Until is null || row.Until > DateTime.UtcNow);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read the member benefits: {ex.Message}", isError: true);
        }
        finally {
            _showingBenefits = false;
        }
    }

    private async Task SaveBenefitsAsync() {
        var list = new JsonArray();
        foreach (var row in _benefitRows) {
            list.Add(new JsonObject {
                ["Key"] = row.Key,
                ["On"] = row.Switch.IsChecked == true,
                ["Days"] = (int) (row.Days.Value ?? 0),
                ["Hours"] = (int) (row.Hours.Value ?? 0),
                ["Until"] = row.Switch.IsChecked == true && row.Until is { } until ? until.ToString("o") : null,
            });
        }

        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{MemberBenefitsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, new JsonObject { ["Benefits"] = list }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, MemberBenefitsPath, overwrite: true);
        ShowBenefitTimes();
    }

    private void ShowBenefitTimes() {
        foreach (var row in _benefitRows) {
            if (row.Switch.IsChecked != true) {
                row.Left.Text = "";
                continue;
            }

            if (row.Until is not { } until) {
                row.Left.Text = "On, no timer.";
                continue;
            }

            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) {
                // The server turned it off; show the same here.
                _showingBenefits = true;
                row.Switch.IsChecked = false;
                _showingBenefits = false;
                row.Left.Text = "";
                continue;
            }

            row.Left.Text = $"{(int) left.TotalDays}d {left.Hours}h {left.Minutes}m {left.Seconds}s left";
        }
    }

}
