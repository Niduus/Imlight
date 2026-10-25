using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Loyalty Store card on the Server tab (Niduus, 2026-10-25: "Loyalty shop is not present and have all his
/// rotations viewable via manager and able to have configs for when his wares rotate and a option in configs to
/// announce his rotation"). Majid in the Shopping District sells one rotation of wares at a time; the card lists every
/// rotation with its wares (spiraldb-extra/LoyaltyStore/Rotations.json), which one is on and when the next comes, and
/// sets when the wares change. The app writes loyalty-store.json next to the server; the server reads it within a few
/// seconds (Game/Loyalty/LoyaltyStore.cs, which works out the rotation the same way as RotationAt here).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string LOYALTY_FILE = "loyalty-store.json";

    // The schedule names in the file, and what the card calls them.
    private static readonly (string Key, string Name)[] s_loyaltySchedules = [
        ("Monthly", "On the 1st of every month (like the game)"),
        ("Every", "Every set number of days and hours"),
        ("Never", "Never: keep the current wares"),
    ];

    private sealed class LoyaltyWare {
        internal uint TemplateId;
        internal string Name = "";
        internal int Price;
        internal int Limit;
    }

    private sealed class LoyaltyRotation {
        internal string Id = "";
        internal string Name = "";
        internal List<LoyaltyWare> Items = [];
    }

    private List<LoyaltyRotation> _loyaltyRotations = [];
    private string _loyaltyStart = "";
    private DateTime? _loyaltyStartedAt;
    private readonly HashSet<string> _loyaltySkip = [];
    private bool _showingLoyalty;
    private string _loyaltyListShown = "";

    private readonly ComboBox _loyaltySchedule = new() { Width = 330, ItemsSource = s_loyaltySchedules.Select(s => s.Name).ToList(), SelectedIndex = 0 };
    private readonly NumericUpDown _loyaltyDays = new() { Minimum = 0, Maximum = 365, Increment = 1, FormatString = "0", Width = 110, Value = 30 };
    private readonly NumericUpDown _loyaltyHours = new() { Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0", Width = 110, Value = 0 };
    private readonly NumericUpDown _loyaltyTokens = new() { Minimum = 0, Maximum = 1000, Increment = 1, FormatString = "0", Width = 110, Value = 5 };
    private readonly ToggleSwitch _loyaltyAnnounce = new() { OnContent = "On", OffContent = "Off", IsChecked = true };
    private readonly ToggleSwitch _loyaltyShuffle = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock _loyaltyNow = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _loyaltyList = new() { Spacing = 6 };
    private Control? _loyaltyEveryRow;

    private string LoyaltyPath => Path.Combine(_manager.ServerFolder, LOYALTY_FILE);

    private string LoyaltyRotationsPath => Path.Combine(_manager.ExtraFolder, "LoyaltyStore", "Rotations.json");

    private string LoyaltySchedule => s_loyaltySchedules[Math.Clamp(_loyaltySchedule.SelectedIndex, 0, s_loyaltySchedules.Length - 1)].Key;

    private Control LoyaltyStoreCard() {
        LoadLoyalty();
        _loyaltyEveryRow = Row(new TextBlock { Text = "Change every", VerticalAlignment = VerticalAlignment.Center }, _loyaltyDays,
            new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center }, _loyaltyHours,
            new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center });
        _loyaltyEveryRow.IsVisible = LoyaltySchedule == "Every";

        _loyaltySchedule.SelectionChanged += async (_, _) => {
            if (_loyaltyEveryRow is not null) {
                _loyaltyEveryRow.IsVisible = LoyaltySchedule == "Every";
            }

            if (!_showingLoyalty) {
                // A new schedule starts from the wares on now, so they do not jump.
                await Guard(() => KeepCurrentAndSaveAsync());
            }
        };
        foreach (var box in new[] { _loyaltyDays, _loyaltyHours }) {
            box.ValueChanged += async (_, _) => {
                if (!_showingLoyalty) {
                    await Guard(() => KeepCurrentAndSaveAsync());
                }
            };
        }

        _loyaltyTokens.ValueChanged += async (_, _) => {
            if (!_showingLoyalty) {
                await Guard(SaveLoyaltyAsync);
            }
        };
        foreach (var toggle in new[] { _loyaltyAnnounce, _loyaltyShuffle }) {
            toggle.IsCheckedChanged += async (_, _) => {
                if (!_showingLoyalty) {
                    await Guard(toggle == _loyaltyShuffle ? () => KeepCurrentAndSaveAsync() : SaveLoyaltyAsync);
                }
            };
        }

        // The time left counts down while the window is open, and the list follows when the wares change.
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => ShowLoyalty();
        ticker.Start();
        ShowLoyalty();

        return Shut(Fold("bag", "Server", "Loyalty Store",
            "Majid in the Wizard City Shopping District sells one set of wares at a time for Loyalty Tokens. Here are all " +
            "his wares, which set is on, and when they change. Each account can claim its tokens once per set in his " +
            "window (or type .loyalty claim). Testers can type .loyalty tokens 50 to get tokens.",
            _loyaltyNow,
            SwitchRow("When the wares change", _loyaltySchedule),
            _loyaltyEveryRow,
            SwitchRow("Announce in game", _loyaltyAnnounce, "Tell everyone online when Majid gets new wares."),
            SwitchRow("Mix the order", _loyaltyShuffle, "After going through every set once, go through them again in a mixed order."),
            SwitchRow("Tokens each set", _loyaltyTokens, "How many Loyalty Tokens each account can claim while a set is on."),
            Heading("Every set of wares"),
            Sub("Make current puts a set on now; the ones after it follow. Skip leaves a set out."),
            _loyaltyList));
    }

    /// <summary>
    /// The set on at a time, with when it started and ends (null: never), the same way the server works it out.
    /// </summary>
    private (LoyaltyRotation Rotation, DateTime Start, DateTime? End)? RotationAt(DateTime utcNow) {
        var used = _loyaltyRotations.Where(r => r.Items.Count > 0 && !_loyaltySkip.Contains(r.Id)).ToList();
        if (used.Count == 0) {
            used = [.. _loyaltyRotations.Where(r => r.Items.Count > 0)];
        }

        if (used.Count == 0) {
            return null;
        }

        var startIndex = used.FindIndex(r => r.Id == _loyaltyStart);
        if (startIndex < 0) {
            startIndex = used.FindIndex(r => r.Id == DateTime.Now.ToString("yyyy-MM"));
            startIndex = startIndex >= 0 ? startIndex : used.Count - 1;
        }

        static DateTime MonthStart(DateTime local) => new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local);

        // Not started from the app yet: the set went on in its own month ("2026-10"), as in the game.
        var anchor = _loyaltyStartedAt
            ?? (DateTime.TryParseExact(used[startIndex].Id, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                ? MonthStart(month).ToUniversalTime() : MonthStart(DateTime.Now).ToUniversalTime());
        var length = TimeSpan.FromDays((double) (_loyaltyDays.Value ?? 0)) + TimeSpan.FromHours((double) (_loyaltyHours.Value ?? 0));
        long steps;
        DateTime start;
        DateTime? end;
        switch (LoyaltySchedule) {
            case "Never":
                return (used[startIndex], anchor, null);
            case "Every" when length > TimeSpan.Zero:
                var startedAt = anchor;
                steps = utcNow < startedAt ? 0 : (utcNow - startedAt).Ticks / length.Ticks;
                start = startedAt + TimeSpan.FromTicks(length.Ticks * steps);
                end = start + length;
                break;
            default:
                var first = MonthStart(anchor.ToLocalTime());
                var thisMonth = MonthStart(utcNow.ToLocalTime());
                steps = Math.Max(0, (thisMonth.Year - first.Year) * 12 + thisMonth.Month - first.Month);
                start = thisMonth.ToUniversalTime();
                end = thisMonth.AddMonths(1).ToUniversalTime();
                break;
        }

        // In order; with Mix the order, each round after the first in a mixed order (the server's IndexAfter).
        var position = startIndex + steps;
        var round = position / used.Count;
        var place = (int) (position % used.Count);
        var index = place;
        if (_loyaltyShuffle.IsChecked == true && round > 0) {
            var order = Enumerable.Range(0, used.Count).ToArray();
            new Random((int) (round * 7919 % int.MaxValue)).Shuffle(order);
            index = order[place];
        }

        return (used[index], start, end);
    }

    private void ShowLoyalty() {
        var now = DateTime.UtcNow;
        var on = RotationAt(now);
        var next = on?.End is { } end ? RotationAt(end.AddSeconds(1)) : null;
        _loyaltyNow.Text = _loyaltyRotations.Count == 0
            ? "The list of Majid's wares was not found. Update the server (Play tab) to get it."
            : on is not { } current ? "No set of wares is on: every set is skipped."
            : $"On now: {current.Rotation.Name}" + (current.End is { } ends ? $", {Left(ends - now)} left." : ", it stays on.")
                + (next is { } coming && current.End is { } at ? $" Next: {coming.Rotation.Name}, {Local(at)}." : "");

        // The rows are only built again when something they show changed, so their buttons stay pressable.
        var shown = $"{on?.Rotation.Id}|{next?.Rotation.Id}|{string.Join(",", _loyaltySkip)}|{_loyaltyRotations.Count}";
        if (shown == _loyaltyListShown) {
            return;
        }

        _loyaltyListShown = shown;
        _loyaltyList.Children.Clear();
        foreach (var rotation in _loyaltyRotations) {
            var skipped = _loyaltySkip.Contains(rotation.Id);
            var state = on?.Rotation == rotation ? "on now" : next?.Rotation == rotation ? "next" : skipped ? "skipped" : "";
            var title = new TextBlock {
                Text = $"{rotation.Name}{(state.Length > 0 ? $" ({state})" : "")}: {rotation.Items.Count} wares",
                FontWeight = state == "on now" ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = skipped ? MUTED : null,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            var wares = new StackPanel { Spacing = 4, Margin = new Thickness(6, 4, 0, 4) };
            var expander = new Expander {
                Header = title, HorizontalAlignment = HorizontalAlignment.Stretch, Content = wares, IsExpanded = false,
            };

            // The pictures are only loaded when a set is opened.
            expander.Expanding += (_, _) => {
                if (wares.Children.Count > 0) {
                    return;
                }

                foreach (var ware in rotation.Items) {
                    wares.Children.Add(Row(DropIcon(ware.TemplateId.ToString(), 30), new TextBlock {
                        Text = $"{ware.Name}: {ware.Price} token{(ware.Price == 1 ? "" : "s")}, up to {ware.Limit} per account",
                        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    }));
                }
            };
            if (state == "on now") {
                expander.IsExpanded = true;
            }

            var id = rotation.Id;
            _loyaltyList.Children.Add(new DockPanel {
                Children = {
                    WithDock(Row(Action(Plain("Make current"), () => MakeLoyaltyCurrentAsync(id)),
                        Action(Plain(skipped ? "Use" : "Skip"), () => SkipLoyaltyAsync(id))), Dock.Right),
                    expander,
                },
            });
        }
    }

    private async Task MakeLoyaltyCurrentAsync(string id) {
        if (_loyaltySkip.Remove(id)) {
            Say("That set was skipped; it is used again.");
        }

        _loyaltyStart = id;
        _loyaltyStartedAt = DateTime.UtcNow;
        await SaveLoyaltyAsync();
        Say($"Majid sells the {_loyaltyRotations.First(r => r.Id == id).Name} wares now." +
            (_loyaltyAnnounce.IsChecked == true ? " Everyone online is told." : ""));
    }

    private async Task SkipLoyaltyAsync(string id) {
        // The wares on now stay on; only the order after them changes.
        var on = RotationAt(DateTime.UtcNow);
        if (!_loyaltySkip.Remove(id)) {
            if (_loyaltyRotations.Count(r => !_loyaltySkip.Contains(r.Id)) <= 1) {
                throw new PanelException("At least one set has to stay in use.");
            }

            _loyaltySkip.Add(id);
        }

        if (on is { } current && current.Rotation.Id != id) {
            _loyaltyStart = current.Rotation.Id;
            _loyaltyStartedAt = current.Start;
        }

        await SaveLoyaltyAsync();
    }

    // Changing the schedule or the order keeps the set on now: the new schedule counts from now.
    private async Task KeepCurrentAndSaveAsync() {
        if (RotationAt(DateTime.UtcNow) is { } current) {
            _loyaltyStart = current.Rotation.Id;
            _loyaltyStartedAt = DateTime.UtcNow;
        }

        await SaveLoyaltyAsync();
    }

    private void LoadLoyalty() {
        _showingLoyalty = true;
        try {
            _loyaltyRotations = [];
            if (File.Exists(LoyaltyRotationsPath) && JsonNode.Parse(File.ReadAllText(LoyaltyRotationsPath)) is JsonObject file) {
                foreach (var item in (file["Rotations"] as JsonArray ?? []).OfType<JsonObject>()) {
                    var rotation = new LoyaltyRotation {
                        Id = item["Id"]?.GetValue<string>() ?? "",
                        Name = item["Name"]?.GetValue<string>() ?? "",
                        Items = [.. (item["Items"] as JsonArray ?? []).OfType<JsonObject>().Select(w => new LoyaltyWare {
                            TemplateId = w["TemplateId"]?.GetValue<uint>() ?? 0,
                            Name = w["Name"]?.GetValue<string>() ?? "",
                            Price = w["Price"]?.GetValue<int>() ?? 1,
                            Limit = w["Limit"]?.GetValue<int>() ?? 1,
                        }).Where(w => w.TemplateId != 0)],
                    };
                    if (rotation.Id.Length > 0) {
                        _loyaltyRotations.Add(rotation);
                    }
                }
            }

            _loyaltySkip.Clear();
            if (!File.Exists(LoyaltyPath) || JsonNode.Parse(File.ReadAllText(LoyaltyPath)) is not JsonObject json) {
                return;
            }

            var schedule = json["Schedule"]?.GetValue<string>() ?? "Monthly";
            _loyaltySchedule.SelectedIndex = Math.Max(0, Array.FindIndex(s_loyaltySchedules, s => string.Equals(s.Key, schedule, StringComparison.OrdinalIgnoreCase)));
            _loyaltyDays.Value = json["Days"]?.GetValue<int>() ?? 30;
            _loyaltyHours.Value = json["Hours"]?.GetValue<int>() ?? 0;
            _loyaltyStart = json["StartRotation"]?.GetValue<string>() ?? "";
            _loyaltyStartedAt = json["StartedAt"]?.GetValue<string>() is { } started
                && DateTime.TryParse(started, null, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
            foreach (var skip in (json["Skip"] as JsonArray ?? []).Select(s => s?.GetValue<string>()).OfType<string>()) {
                _loyaltySkip.Add(skip);
            }

            _loyaltyShuffle.IsChecked = json["Shuffle"]?.GetValue<bool>() == true;
            _loyaltyAnnounce.IsChecked = json["Announce"]?.GetValue<bool>() != false;
            _loyaltyTokens.Value = json["TokensPerRotation"]?.GetValue<int>() ?? 5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read the Loyalty Store settings: {ex.Message}", isError: true);
        }
        finally {
            _showingLoyalty = false;
        }
    }

    private async Task SaveLoyaltyAsync() {
        var json = new JsonObject {
            ["Schedule"] = LoyaltySchedule,
            ["Days"] = (int) (_loyaltyDays.Value ?? 30),
            ["Hours"] = (int) (_loyaltyHours.Value ?? 0),
            ["StartRotation"] = _loyaltyStart,
            ["StartedAt"] = _loyaltyStartedAt?.ToString("o"),
            ["Skip"] = new JsonArray([.. _loyaltySkip.Select(s => (JsonNode) JsonValue.Create(s)!)]),
            ["Shuffle"] = _loyaltyShuffle.IsChecked == true,
            ["Announce"] = _loyaltyAnnounce.IsChecked == true,
            ["TokensPerRotation"] = (int) (_loyaltyTokens.Value ?? 5),
        };
        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{LoyaltyPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, LoyaltyPath, overwrite: true);
        _loyaltyListShown = "";
        ShowLoyalty();
    }

}
