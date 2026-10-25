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
/// The Tamed Drops card on the Server tab (Niduus, 2026-10-23: "make this a toggle and customizeable via manager"): the
/// on/off switch ([TamedDrops] Enabled) and every number and choice of it, saved to tamed-drops.json
/// next to the server. The server reads that file again whenever it changes, so those apply from the next boss. The
/// defaults are what the server's TamedDropsSettings has with no file. It also shows and resets the accounts' pity meters.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TAMED_DROPS_FILE = "tamed-drops.json";

    // The prizes, as the server's TamedPrize names them, with the words the card shows.
    private static readonly (string Key, string Name)[] s_tamedPrizes = [
        ("None", "Nothing"),
        ("NotJunk", "A drop that is not junk"),
        ("DoubleGearOdds", "Better gear odds for that battle"),
        ("OtherSchoolGear", "Gear for another school"),
        ("AnyGear", "Gear for any school"),
        ("OwnSchoolGear", "Gear for the wizard's own school"),
    ];

    // Each tier's defaults, 5 first: most rounds, pity runs, junk odds, other odds, pity prize, every-run extra.
    private static readonly (int Tier, int MaxRounds, double PityRuns, double Junk, double Other, string Prize, string EveryRun)[] s_tamedTierDefaults = [
        (5, 1, 1, 1, 1, "NotJunk", "None"),
        (4, 3, 3, 0.8, 1.25, "DoubleGearOdds", "None"),
        (3, 5, 6, 0.6, 1.5, "OtherSchoolGear", "None"),
        (2, 8, 9, 0.4, 1.75, "AnyGear", "None"),
        (1, 0, 12, 0.25, 2, "OwnSchoolGear", "OwnSchoolGear"),
    ];

    private sealed class TamedTierRow {
        internal required int Tier;
        internal readonly NumericUpDown MaxRounds = TamedNumber(1, 999, 1, "0");
        internal readonly NumericUpDown PityRuns = TamedNumber(0, 999, 1, "0.##");
        internal readonly NumericUpDown Junk = TamedNumber(0, 100, 0.05m, "0.##");
        internal readonly NumericUpDown Other = TamedNumber(0, 100, 0.05m, "0.##");
        internal readonly ComboBox Prize = TamedPrizeBox();
        internal readonly ComboBox EveryRun = TamedPrizeBox();
    }

    private readonly ToggleSwitch _tamedOn = new() { OnContent = "On", OffContent = "Off" };
    private readonly List<TamedTierRow> _tamedTiers = [];
    private readonly NumericUpDown _tamedOwnFill = TamedNumber(0, 100, 0.05m, "0.##");
    private readonly NumericUpDown _tamedOtherFill = TamedNumber(0, 100, 0.05m, "0.##");
    private readonly NumericUpDown _tamedPrizeGear = TamedNumber(0, 100, 0.25m, "0.##");
    private readonly CheckBox _tamedJunkReagents = new() { Content = "Reagents" };
    private readonly CheckBox _tamedJunkCards = new() { Content = "Treasure cards" };
    private readonly CheckBox _tamedJunkFurniture = new() { Content = "Furniture and house items" };
    private readonly CheckBox _tamedJunkSnacks = new() { Content = "Pet snacks" };
    private readonly ToggleSwitch _tamedPassesStop = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _tamedPassesCount = TamedNumber(0, 99, 1, "0");
    private readonly ToggleSwitch _tamedForcedCount = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tamedAllMonsters = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tamedShowNote = new() { OnContent = "On", OffContent = "Off" };
    private readonly StackPanel _tamedPityRows = new() { Spacing = 6 };
    private readonly TextBlock _tamedPityNote = Sub("Click \"Show pity meters\" while the server is running.");
    private bool _showingTamed;

    private string TamedDropsPath => Path.Combine(_manager.ServerFolder, TAMED_DROPS_FILE);

    private static NumericUpDown TamedNumber(decimal min, decimal max, decimal step, string format)
        => new() { Minimum = min, Maximum = max, Increment = step, FormatString = format, Width = 130 };

    private static ComboBox TamedPrizeBox() => new() { ItemsSource = s_tamedPrizes.Select(p => p.Name).ToList(), Width = 260 };

    private Control TamedDropsCard() {
        _tamedOn.IsCheckedChanged += async (_, _) => {
            if (!_updatingRandomizer) {
                await Guard(() => SetSwitchAsync("TamedDrops", "Enabled", "Tamed Drops", _tamedOn.IsChecked == true));
            }
        };

        var tiers = new StackPanel { Spacing = 8 };
        foreach (var (tier, _, _, _, _, _, _) in s_tamedTierDefaults) {
            var row = new TamedTierRow { Tier = tier };
            _tamedTiers.Add(row);
            var speed = tier switch { 5 => "the fastest wins", 1 => "the slowest wins", _ => "" };
            var fields = new StackPanel { Spacing = 4 };
            fields.Children.Add(new TextBlock {
                Text = $"Tier {tier}" + (speed.Length > 0 ? $" ({speed})" : ""), FontWeight = FontWeight.SemiBold,
            });
            fields.Children.Add(tier == 1
                ? Sub("Every battle that takes more rounds than tier 2 allows.")
                : SwitchRow("Most rounds for this tier", row.MaxRounds,
                    "A win in this many rounds or fewer earns this tier (if a faster tier does not take it first)."));
            fields.Children.Add(SwitchRow("Runs to fill the pity meter", row.PityRuns,
                "How many runs of this tier fill its meter for a boss. 0: this tier has no pity meter."));
            fields.Children.Add(SwitchRow("Pity prize", row.Prize,
                "What a full meter gives. It then starts over. \"Nothing\" also turns this tier's meter off."));
            fields.Children.Add(SwitchRow("Junk odds ×", row.Junk,
                "Multiplies the chance of every junk drop (see below what is junk). 1: normal, 0.5: half as likely, 0: never."));
            fields.Children.Add(SwitchRow("Other drops odds ×", row.Other,
                "Multiplies the chance of every drop that is not junk. 1: normal, 2: twice as likely."));
            fields.Children.Add(SwitchRow("Extra drop every run", row.EveryRun,
                "Something this tier always adds from the boss's own drops, pity or not."));
            tiers.Children.Add(ManagerTheme.Item(fields));
        }

        var junk = new WrapPanel { Children = { Spaced(_tamedJunkReagents), Spaced(_tamedJunkCards), Spaced(_tamedJunkFurniture), Spaced(_tamedJunkSnacks) } };

        // Every change is saved at once; the server picks it up from the next round or boss.
        foreach (var box in _tamedTiers.SelectMany(r => new[] { r.MaxRounds, r.PityRuns, r.Junk, r.Other })
                     .Concat([_tamedOwnFill, _tamedOtherFill, _tamedPrizeGear, _tamedPassesCount])) {
            box.ValueChanged += async (_, _) => await SaveTamedIfChangedAsync();
        }

        foreach (var combo in _tamedTiers.SelectMany(r => new[] { r.Prize, r.EveryRun })) {
            combo.SelectionChanged += async (_, _) => await SaveTamedIfChangedAsync();
        }

        foreach (var check in new[] { _tamedJunkReagents, _tamedJunkCards, _tamedJunkFurniture, _tamedJunkSnacks }) {
            check.IsCheckedChanged += async (_, _) => await SaveTamedIfChangedAsync();
        }

        foreach (var toggle in new[] { _tamedPassesStop, _tamedForcedCount, _tamedAllMonsters, _tamedShowNote }) {
            toggle.IsCheckedChanged += async (_, _) => await SaveTamedIfChangedAsync();
        }

        LoadTamedDrops();

        return Shut(Fold("gift", "Bosses", "Tamed Drops",
            "Boss loot depends on how many rounds the battle took: the faster the win, the higher the tier (5 is best). " +
            "Longer battles make junk rarer and other drops likelier. Each tier has a pity meter per boss, shared by all " +
            "the wizards of an account. Changes below are saved at once and apply from the next boss, no restart needed.",
            SwitchRow("Tamed Drops", _tamedOn, "Turns the whole feature on or off, from the next boss."),
            Row(Action(Plain("Reset to defaults"), ResetTamedDropsAsync)),
            Heading("Tiers"),
            tiers,
            Heading("Pity meters"),
            SwitchRow("A run fills its own tier's meter by", _tamedOwnFill, "How much one run adds to the meter of the tier it earned. Normal: 1."),
            SwitchRow("…and every other tier's meter by", _tamedOtherFill, "How much one run adds to the meters of the other four tiers. Normal: 0.25 (a quarter)."),
            SwitchRow("\"Better gear odds\" multiplies gear odds by", _tamedPrizeGear, "Used when a prize or extra is \"Better gear odds for that battle\". Normal: 2 (double)."),
            Heading("What counts as junk"),
            WithHint(junk, "Checked kinds of drops are junk: their odds use each tier's \"Junk odds\", and \"A drop that is not junk\" never picks them."),
            Heading("Passing"),
            SwitchRow("Passed rounds stop counting", _tamedPassesStop,
                "On: when every wizard passes several rounds in a row, the extra ones do not count toward the rounds (so stalling does not lower the tier on purpose). Off: every round counts."),
            SwitchRow("Passed rounds in a row that still count", _tamedPassesCount,
                "Normal: 2, so the third pass in a row and every one after it do not count. A round where someone casts counts again."),
            SwitchRow("A stunned or confused wizard's pass always counts", _tamedForcedCount,
                "On: a wizard who could not cast did not choose to pass, so that round counts. Off: it is treated like a chosen pass."),
            Heading("Other"),
            SwitchRow("Every monster, not just bosses", _tamedAllMonsters, "Off: only bosses (and creatures with drops set in this app). On: every monster's loot follows Tamed Drops."),
            SwitchRow("Tell the wizard the tier and pity", _tamedShowNote, "After each boss, a line in chat says the tier, the rounds and the pity meters."),
            Heading("Account pity meters"),
            Row(Action(Plain("Show pity meters"), LoadTamedPityAsync)),
            _tamedPityNote,
            _tamedPityRows));
    }

    /// <summary>The Tamed Drops on/off switch, from Imlight.ini (ShowStatus calls this as it does the other switches).</summary>
    private void ShowTamedDropsSwitch(bool has, bool on) {
        _tamedOn.IsChecked = on;
        _tamedOn.IsEnabled = has;
    }

    private void LoadTamedDrops() {
        JsonObject saved = null!;
        try {
            saved = File.Exists(TamedDropsPath) ? JsonNode.Parse(File.ReadAllText(TamedDropsPath)) as JsonObject ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read the Tamed Drops settings: {ex.Message}", isError: true);
            saved = [];
        }

        ShowTamed(saved);
    }

    private void ShowTamed(JsonObject saved) {
        _showingTamed = true;
        try {
            var tiers = (saved["Tiers"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            foreach (var row in _tamedTiers) {
                var d = s_tamedTierDefaults.First(t => t.Tier == row.Tier);
                var item = tiers.FirstOrDefault(t => Number(t, "Tier") == row.Tier);
                row.MaxRounds.Value = (decimal) Math.Max(1, Number(item, "MaxRounds") ?? (d.MaxRounds > 0 ? d.MaxRounds : 9));
                row.PityRuns.Value = (decimal) (Number(item, "PityRuns") ?? d.PityRuns);
                row.Junk.Value = (decimal) (Number(item, "JunkOdds") ?? d.Junk);
                row.Other.Value = (decimal) (Number(item, "OtherOdds") ?? d.Other);
                row.Prize.SelectedIndex = PrizeIndex(Text(item, "PityPrize") ?? d.Prize);
                row.EveryRun.SelectedIndex = PrizeIndex(Text(item, "EveryRun") ?? d.EveryRun);
            }

            _tamedOwnFill.Value = (decimal) (Number(saved, "OwnTierFill") ?? 1);
            _tamedOtherFill.Value = (decimal) (Number(saved, "OtherTierFill") ?? 0.25);
            _tamedPrizeGear.Value = (decimal) (Number(saved, "PrizeGearOdds") ?? 2);
            _tamedJunkReagents.IsChecked = Flag(saved, "JunkReagents") ?? true;
            _tamedJunkCards.IsChecked = Flag(saved, "JunkTreasureCards") ?? true;
            _tamedJunkFurniture.IsChecked = Flag(saved, "JunkFurniture") ?? true;
            _tamedJunkSnacks.IsChecked = Flag(saved, "JunkPetSnacks") ?? true;
            _tamedPassesStop.IsChecked = Flag(saved, "PassesStopCounting") ?? true;
            _tamedPassesCount.Value = (decimal) (Number(saved, "PassesThatCount") ?? 2);
            _tamedForcedCount.IsChecked = Flag(saved, "ForcedPassesCount") ?? true;
            _tamedAllMonsters.IsChecked = Flag(saved, "AllMonsters") ?? false;
            _tamedShowNote.IsChecked = Flag(saved, "ShowNote") ?? true;
        }
        finally {
            _showingTamed = false;
        }

        static double? Number(JsonObject? json, string key)
            => json?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
        static string? Text(JsonObject? json, string key)
            => json?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        static bool? Flag(JsonObject? json, string key)
            => json?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
        static int PrizeIndex(string key)
            => Math.Max(0, Array.FindIndex(s_tamedPrizes, p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task SaveTamedIfChangedAsync() {
        if (!_showingTamed) {
            await Guard(SaveTamedDropsAsync);
        }
    }

    private async Task SaveTamedDropsAsync() {
        // A faster tier may not allow more rounds than a slower one; the server fixes it too, the card shows it.
        _showingTamed = true;
        var least = 1m;
        foreach (var row in _tamedTiers.Where(r => r.Tier > 1)) {
            if ((row.MaxRounds.Value ?? 1) < least) {
                row.MaxRounds.Value = least;
            }

            least = (row.MaxRounds.Value ?? least) + 1;
        }
        _showingTamed = false;

        var tiers = new JsonArray();
        foreach (var row in _tamedTiers) {
            tiers.Add(new JsonObject {
                ["Tier"] = row.Tier,
                ["MaxRounds"] = row.Tier == 1 ? 0 : (int) (row.MaxRounds.Value ?? 1),
                ["PityRuns"] = (double) (row.PityRuns.Value ?? 0),
                ["JunkOdds"] = (double) (row.Junk.Value ?? 1),
                ["OtherOdds"] = (double) (row.Other.Value ?? 1),
                ["PityPrize"] = s_tamedPrizes[Math.Max(0, row.Prize.SelectedIndex)].Key,
                ["EveryRun"] = s_tamedPrizes[Math.Max(0, row.EveryRun.SelectedIndex)].Key,
            });
        }

        var json = new JsonObject {
            ["Tiers"] = tiers,
            ["OwnTierFill"] = (double) (_tamedOwnFill.Value ?? 1),
            ["OtherTierFill"] = (double) (_tamedOtherFill.Value ?? 0.25m),
            ["PrizeGearOdds"] = (double) (_tamedPrizeGear.Value ?? 2),
            ["JunkReagents"] = _tamedJunkReagents.IsChecked == true,
            ["JunkTreasureCards"] = _tamedJunkCards.IsChecked == true,
            ["JunkFurniture"] = _tamedJunkFurniture.IsChecked == true,
            ["JunkPetSnacks"] = _tamedJunkSnacks.IsChecked == true,
            ["PassesStopCounting"] = _tamedPassesStop.IsChecked == true,
            ["PassesThatCount"] = (int) (_tamedPassesCount.Value ?? 2),
            ["ForcedPassesCount"] = _tamedForcedCount.IsChecked == true,
            ["AllMonsters"] = _tamedAllMonsters.IsChecked == true,
            ["ShowNote"] = _tamedShowNote.IsChecked == true,
        };

        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{TamedDropsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, TamedDropsPath, overwrite: true);
    }

    private async Task ResetTamedDropsAsync() {
        if (!await ConfirmAsync("Put every Tamed Drops number and choice back to how it came? (The on/off switch and the pity meters stay as they are.)")) {
            return;
        }

        if (File.Exists(TamedDropsPath)) {
            File.Delete(TamedDropsPath);
        }

        ShowTamed([]);
        Say("Tamed Drops is back to its defaults. The server uses them from the next boss.");
    }

    private async Task LoadTamedPityAsync() {
        _tamedPityRows.Children.Clear();
        List<(string Username, Dictionary<string, double> Meters)> list;
        try {
            using var accounts = _manager.Accounts();
            list = await accounts.TamedPityAsync();
        }
        catch (Exception ex) when (ex is AccountException or PanelException) {
            _tamedPityNote.Text = ex.Message;

            return;
        }

        var withMeters = list.Where(a => a.Meters.Values.Any(v => v > 0)).ToList();
        _tamedPityNote.Text = withMeters.Count == 0
            ? "No account has filled a pity meter yet."
            : "Each line is one boss (its number, as on the Lookup tab) and how full each tier's meter is.";
        if (withMeters.Count > 1) {
            _tamedPityRows.Children.Add(Row(Action(Danger(Plain("Reset every account")), async () => {
                if (await ConfirmAsync("Empty the Tamed Drops pity meters of every account?")) {
                    using var accounts = _manager.Accounts();
                    foreach (var (username, _) in withMeters) {
                        await accounts.ResetTamedPityAsync(username);
                    }

                    Say("Every account's pity meters are empty.");
                    await LoadTamedPityAsync();
                }
            })));
        }

        foreach (var (username, meters) in withMeters) {
            var runs = _tamedTiers.ToDictionary(r => r.Tier, r => (double) (r.PityRuns.Value ?? 0));
            var lines = meters.Where(m => m.Value > 0)
                .Select(m => (Parts: m.Key.Split(':'), m.Value))
                .Where(m => m.Parts.Length == 2 && int.TryParse(m.Parts[1], out _))
                .GroupBy(m => m.Parts[0])
                .Select(boss => $"Boss {boss.Key}: " + string.Join(", ", boss
                    .OrderByDescending(m => int.Parse(m.Parts[1]))
                    .Select(m => $"tier {m.Parts[1]} {Math.Floor(m.Value * 100) / 100:0.##}/{runs.GetValueOrDefault(int.Parse(m.Parts[1])):0.##}")))
                .ToList();
            var reset = Action(Plain("Reset"), async () => {
                if (await ConfirmAsync($"Empty the Tamed Drops pity meters of {username}?")) {
                    using var accounts = _manager.Accounts();
                    await accounts.ResetTamedPityAsync(username);
                    Say($"{username}'s pity meters are empty.");
                    await LoadTamedPityAsync();
                }
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock { Text = username, FontWeight = FontWeight.SemiBold });
            foreach (var line in lines) {
                text.Children.Add(new SelectableTextBlock { Text = line, Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
            }

            _tamedPityRows.Children.Add(ManagerTheme.Item(new DockPanel {
                Children = { WithDock(WithMargin(WithCenter(reset), new Thickness(12, 0, 0, 0)), Dock.Right), text },
            }));
        }
    }

}
