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
/// The Crown Shop card on the Server tab (Niduus, 2026-10-25): the items on the shop's Featured tab, sales of one's
/// own (a sale price with the normal price crossed out, for some hours or days or for good) and a Crowns Rewards event
/// (spend crowns in the shop, reach a tier, get its item). The app writes crown-shop.json next to the server; the
/// server reads it within a second, and the shop shows the change the next time it is opened.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string CROWN_SHOP_FILE = "crown-shop.json";
    private const int CROWN_SEARCH_RESULTS = 8;

    private sealed class ShopSale {
        internal string Id = "";
        internal int Crowns;
        internal int Gold;
        internal int Days;
        internal int Hours;
        internal DateTime? Until;
    }

    private sealed class ShopTier {
        internal int Crowns = 1000;
        internal string Id = "";
        internal int Count = 1;
    }

    private readonly List<string> _featured = [];
    private readonly List<ShopSale> _sales = [];
    private readonly List<ShopTier> _rewardTiers = [];
    private readonly StackPanel _featuredRows = new() { Spacing = 4 };
    private readonly StackPanel _saleRows = new() { Spacing = 6 };
    private readonly StackPanel _tierRows = new() { Spacing = 6 };
    private readonly ToggleSwitch _rewardsOn = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBox _rewardsName = new() { Width = 300, Text = "Crowns Rewards" };
    private readonly NumericUpDown _rewardsDays = new() { Minimum = 0, Maximum = 365, Increment = 1, FormatString = "0", Width = 110 };
    private readonly NumericUpDown _rewardsHours = new() { Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0", Width = 110 };
    private readonly TextBlock _rewardsLeft = Sub("");
    private DateTime? _rewardsStarted;
    private DateTime? _rewardsUntil;

    private string CrownShopPath => Path.Combine(_manager.ServerFolder, CROWN_SHOP_FILE);

    private Control CrownShopCard() {
        LoadCrownShop();

        var featuredSearch = CrownItemSearch("Search an item to feature, by name or ID", id => {
            if (!_featured.Contains(id)) {
                _featured.Add(id);
            }
            RenderFeatured();

            return SaveCrownShopAsync("Featured");
        });
        var saleSearch = CrownItemSearch("Search an item to put on sale, by name or ID", id => {
            _sales.Add(new ShopSale { Id = id });
            RenderSales();
            Say("Set its sale price and how long it lasts, then press Save sales.");

            return Task.CompletedTask;
        });

        _rewardsOn.IsCheckedChanged += (_, _) => RenderRewardsLeft();

        RenderFeatured();
        RenderSales();
        RenderTiers();
        RenderRewardsLeft();
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => RenderRewardsLeft();
        ticker.Start();

        return Shut(Fold("bag", "Server", "Crown Shop",
            "The Featured tab, sales of your own and Crowns Rewards. Changes count at once, with no restart: the shop " +
            "shows them the next time it is opened.",
            Heading("Featured tab"),
            Sub("These items show on the shop's Featured tab, in this order, and still in their own section too. " +
                "Gear with level versions shows the one for each wizard's level."),
            _featuredRows,
            featuredSearch,
            WithMargin(Heading("Sales"), new Thickness(0, 12, 0, 0)),
            Sub("A sale sells an item for your price, with its normal price crossed out and a SALE sticker. Set 0 to keep " +
                "the normal crowns or gold price. Days and hours: how long the sale lasts from when you press Save sales; " +
                "0 and 0 keeps it on for good."),
            _saleRows,
            saleSearch,
            Row(Action(Primary("Save sales"), () => SaveCrownShopAsync("Sales"))),
            WithMargin(Heading("Crowns Rewards"), new Thickness(0, 12, 0, 0)),
            Sub("While it is on, the crowns each account spends in the shop add up, and each tier it reaches gives that " +
                "item once. Turning it on again with Start new starts everyone at 0. Wizards are told where they stand " +
                "when they open the shop and after each buy."),
            SwitchRow("Crowns Rewards", _rewardsOn),
            Row(new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center }, _rewardsName),
            Row(new TextBlock { Text = "Lasts", VerticalAlignment = VerticalAlignment.Center }, _rewardsDays,
                new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center }, _rewardsHours,
                new TextBlock { Text = "hours (0 and 0: until you turn it off)", VerticalAlignment = VerticalAlignment.Center }),
            _rewardsLeft,
            _tierRows,
            CrownItemSearch("Search a reward item for a new tier, by name or ID", id => {
                var next = (_rewardTiers.Count == 0 ? 0 : _rewardTiers.Max(t => t.Crowns)) + 1000;
                _rewardTiers.Add(new ShopTier { Id = id, Crowns = next });
                RenderTiers();

                return Task.CompletedTask;
            }),
            Row(Action(Primary("Save Crowns Rewards"), () => SaveCrownShopAsync("Rewards", newEvent: false)),
                Action(Plain("Start new (everyone at 0)"), () => SaveCrownShopAsync("Rewards", newEvent: true)))));
    }

    /// <summary>A search box over the server's item list, each match with a button that picks it.</summary>
    private Control CrownItemSearch(string watermark, Func<string, Task> picked) {
        var search = new TextBox { Watermark = watermark, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new StackPanel { Spacing = 2 };
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) => {
            delay.Stop();
            results.Children.Clear();
            var query = (search.Text ?? "").Trim();
            if (query.Length < 2) {
                return;
            }

            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = DropCatalog().Where(i => i.Kind == "Item"
                    && (i.Id == query || words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(i => !i.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.Name.Length)
                .Take(CROWN_SEARCH_RESULTS).ToList();
            if (matches.Count == 0) {
                results.Children.Add(Sub(DropCatalog().Count == 0
                    ? "The item list isn't ready yet: start the server once, then search again."
                    : "No item has that name. Try fewer words or the item's ID."));
            }

            foreach (var item in matches) {
                results.Children.Add(Row(DropIcon(item.Id, 26),
                    new TextBlock { Text = $"{item.Name} ({item.Id})", VerticalAlignment = VerticalAlignment.Center },
                    Action(Plain("Add"), async () => {
                        search.Text = "";
                        results.Children.Clear();
                        await picked(item.Id);
                    })));
            }
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };

        return new StackPanel { Spacing = 4, Children = { search, results } };
    }

    private string CrownItemName(string id) => CatalogItem(id)?.Name is { Length: > 0 } name ? $"{name} ({id})" : $"Item {id}";

    private void RenderFeatured() {
        _featuredRows.Children.Clear();
        if (_featured.Count == 0) {
            _featuredRows.Children.Add(Sub("Nothing featured yet."));
        }

        for (var i = 0; i < _featured.Count; i++) {
            var index = i;
            var id = _featured[i];
            _featuredRows.Children.Add(Row(DropIcon(id, 26),
                new TextBlock { Text = CrownItemName(id), VerticalAlignment = VerticalAlignment.Center, Width = 360 },
                Action(Plain("Up"), () => MoveFeatured(index, -1)),
                Action(Plain("Down"), () => MoveFeatured(index, 1)),
                Danger(Action(Plain("Remove"), () => {
                    _featured.RemoveAt(index);
                    RenderFeatured();

                    return SaveCrownShopAsync("Featured");
                }))));
        }
    }

    private Task MoveFeatured(int index, int by) {
        var to = index + by;
        if (to < 0 || to >= _featured.Count) {
            return Task.CompletedTask;
        }

        (_featured[index], _featured[to]) = (_featured[to], _featured[index]);
        RenderFeatured();

        return SaveCrownShopAsync("Featured");
    }

    private void RenderSales() {
        _saleRows.Children.Clear();
        if (_sales.Count == 0) {
            _saleRows.Children.Add(Sub("No sales yet."));
        }

        foreach (var sale in _sales) {
            var crowns = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Increment = 50, FormatString = "0", Width = 130, Value = sale.Crowns };
            var gold = new NumericUpDown { Minimum = 0, Maximum = 100_000_000, Increment = 100, FormatString = "0", Width = 150, Value = sale.Gold };
            var days = new NumericUpDown { Minimum = 0, Maximum = 365, Increment = 1, FormatString = "0", Width = 110, Value = sale.Days };
            var hours = new NumericUpDown { Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0", Width = 110, Value = sale.Hours };
            crowns.ValueChanged += (_, _) => sale.Crowns = (int) (crowns.Value ?? 0);
            gold.ValueChanged += (_, _) => sale.Gold = (int) (gold.Value ?? 0);
            void LengthChanged() {
                sale.Days = (int) (days.Value ?? 0);
                sale.Hours = (int) (hours.Value ?? 0);
                // A new length counts from the next Save sales.
                sale.Until = null;
            }
            days.ValueChanged += (_, _) => LengthChanged();
            hours.ValueChanged += (_, _) => LengthChanged();

            var state = sale.Until is { } until
                ? until > DateTime.UtcNow ? $"Ends {until.ToLocalTime():g}." : "Ended."
                : sale.Days + sale.Hours > 0 ? "Starts when you press Save sales." : "On for good.";
            _saleRows.Children.Add(new StackPanel {
                Spacing = 2,
                Children = {
                    Row(DropIcon(sale.Id, 26), new TextBlock { Text = CrownItemName(sale.Id), VerticalAlignment = VerticalAlignment.Center },
                        Danger(Action(Plain("Remove"), () => {
                            _sales.Remove(sale);
                            RenderSales();

                            return SaveCrownShopAsync("Sales");
                        }))),
                    Row(new TextBlock { Text = "Crowns", VerticalAlignment = VerticalAlignment.Center }, crowns,
                        new TextBlock { Text = "Gold", VerticalAlignment = VerticalAlignment.Center }, gold,
                        new TextBlock { Text = "Lasts", VerticalAlignment = VerticalAlignment.Center }, days,
                        new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center }, hours,
                        new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center }),
                    Sub(state),
                },
            });
        }
    }

    private void RenderTiers() {
        _tierRows.Children.Clear();
        if (_rewardTiers.Count == 0) {
            _tierRows.Children.Add(Sub("No tiers yet. Search a reward item below to add one."));
        }

        foreach (var tier in _rewardTiers.OrderBy(t => t.Crowns).ToList()) {
            var crowns = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Increment = 500, FormatString = "0", Width = 140, Value = tier.Crowns };
            var count = new NumericUpDown { Minimum = 1, Maximum = 100, Increment = 1, FormatString = "0", Width = 100, Value = tier.Count };
            crowns.ValueChanged += (_, _) => tier.Crowns = (int) (crowns.Value ?? 1);
            count.ValueChanged += (_, _) => tier.Count = (int) (count.Value ?? 1);
            _tierRows.Children.Add(Row(new TextBlock { Text = "Spend", VerticalAlignment = VerticalAlignment.Center }, crowns,
                new TextBlock { Text = "crowns, get", VerticalAlignment = VerticalAlignment.Center }, count,
                DropIcon(tier.Id, 26), new TextBlock { Text = CrownItemName(tier.Id), VerticalAlignment = VerticalAlignment.Center },
                Danger(Action(Plain("Remove"), () => {
                    _rewardTiers.Remove(tier);
                    RenderTiers();

                    return Task.CompletedTask;
                }))));
        }
    }

    private void RenderRewardsLeft() {
        if (_rewardsOn.IsChecked != true || _rewardsStarted is null) {
            _rewardsLeft.Text = _rewardsOn.IsChecked == true ? "Press Save Crowns Rewards to start it." : "";
            return;
        }

        if (_rewardsUntil is not { } until) {
            _rewardsLeft.Text = $"On since {_rewardsStarted.Value.ToLocalTime():g}, until you turn it off.";
            return;
        }

        var left = until - DateTime.UtcNow;
        _rewardsLeft.Text = left <= TimeSpan.Zero ? "It has ended."
            : $"On since {_rewardsStarted.Value.ToLocalTime():g}: {(int) left.TotalDays}d {left.Hours}h {left.Minutes}m {left.Seconds}s left.";
    }

    private void LoadCrownShop() {
        _featured.Clear();
        _sales.Clear();
        _rewardTiers.Clear();
        try {
            if (!File.Exists(CrownShopPath) || JsonNode.Parse(File.ReadAllText(CrownShopPath)) is not JsonObject root) {
                return;
            }

            foreach (var id in (root["Featured"] as JsonArray ?? []).OfType<JsonValue>()) {
                _featured.Add(id.ToString());
            }

            foreach (var sale in (root["Sales"] as JsonArray ?? []).OfType<JsonObject>()) {
                _sales.Add(new ShopSale {
                    Id = sale["TemplateId"]?.ToString() ?? "",
                    Crowns = sale["Crowns"]?.GetValue<int>() ?? 0,
                    Gold = sale["Gold"]?.GetValue<int>() ?? 0,
                    Days = sale["Days"]?.GetValue<int>() ?? 0,
                    Hours = sale["Hours"]?.GetValue<int>() ?? 0,
                    Until = Time(sale["Until"]),
                });
            }

            if (root["Rewards"] is JsonObject rewards) {
                _rewardsOn.IsChecked = rewards["On"]?.GetValue<bool>() == true;
                _rewardsName.Text = rewards["Name"]?.GetValue<string>() ?? "Crowns Rewards";
                _rewardsDays.Value = rewards["Days"]?.GetValue<int>() ?? 0;
                _rewardsHours.Value = rewards["Hours"]?.GetValue<int>() ?? 0;
                _rewardsStarted = Time(rewards["StartedAt"]);
                _rewardsUntil = Time(rewards["Until"]);
                foreach (var tier in (rewards["Tiers"] as JsonArray ?? []).OfType<JsonObject>()) {
                    _rewardTiers.Add(new ShopTier {
                        Crowns = tier["Crowns"]?.GetValue<int>() ?? 0,
                        Id = tier["TemplateId"]?.ToString() ?? "",
                        Count = tier["Count"]?.GetValue<int>() ?? 1,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read the Crown Shop settings: {ex.Message}", isError: true);
        }
    }

    private static DateTime? Time(JsonNode? node)
        => node?.GetValue<string>() is { } text && DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUniversalTime() : null;

    private async Task SaveCrownShopAsync(string what, bool newEvent = false) {
        var now = DateTime.UtcNow;
        if (what == "Sales") {
            foreach (var sale in _sales.Where(s => s.Until is null && s.Days + s.Hours > 0)) {
                sale.Until = now + TimeSpan.FromDays(sale.Days) + TimeSpan.FromHours(sale.Hours);
            }
        }

        if (what == "Rewards") {
            var on = _rewardsOn.IsChecked == true;
            if (on && _rewardTiers.Count == 0) {
                throw new PanelException("Add at least one tier: search a reward item under the tiers.");
            }

            // Turning it on (or Start new) starts a new event; saving while on keeps everyone's progress.
            if (on && (newEvent || _rewardsStarted is null)) {
                _rewardsStarted = now;
                _rewardsUntil = null;
            }
            if (on) {
                var length = TimeSpan.FromDays((double) (_rewardsDays.Value ?? 0)) + TimeSpan.FromHours((double) (_rewardsHours.Value ?? 0));
                _rewardsUntil = length > TimeSpan.Zero ? _rewardsStarted + length : null;
            }
            else {
                _rewardsStarted = null;
                _rewardsUntil = null;
            }
        }

        var root = new JsonObject {
            ["Featured"] = new JsonArray([.. _featured.Where(id => ulong.TryParse(id, out _)).Select(id => (JsonNode) ulong.Parse(id))]),
            ["Sales"] = new JsonArray([.. _sales.Where(s => ulong.TryParse(s.Id, out _)).Select(s => (JsonNode) new JsonObject {
                ["TemplateId"] = ulong.Parse(s.Id),
                ["Crowns"] = s.Crowns,
                ["Gold"] = s.Gold,
                ["Days"] = s.Days,
                ["Hours"] = s.Hours,
                ["Until"] = s.Until?.ToString("o"),
            })]),
            ["Rewards"] = new JsonObject {
                ["On"] = _rewardsOn.IsChecked == true,
                ["Name"] = string.IsNullOrWhiteSpace(_rewardsName.Text) ? "Crowns Rewards" : _rewardsName.Text.Trim(),
                ["Days"] = (int) (_rewardsDays.Value ?? 0),
                ["Hours"] = (int) (_rewardsHours.Value ?? 0),
                ["StartedAt"] = _rewardsStarted?.ToString("o"),
                ["Until"] = _rewardsUntil?.ToString("o"),
                ["Tiers"] = new JsonArray([.. _rewardTiers.Where(t => ulong.TryParse(t.Id, out _)).OrderBy(t => t.Crowns)
                    .Select(t => (JsonNode) new JsonObject { ["Crowns"] = t.Crowns, ["TemplateId"] = ulong.Parse(t.Id), ["Count"] = t.Count })]),
            },
        };

        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{CrownShopPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, CrownShopPath, overwrite: true);
        if (what == "Sales") {
            RenderSales();
        }
        RenderRewardsLeft();
        Say(what switch {
            "Featured" => "Saved the Featured tab. The shop shows it the next time it is opened.",
            "Sales" => "Saved the sales. The shop shows them the next time it is opened.",
            _ => _rewardsOn.IsChecked == true ? "Saved Crowns Rewards. It is on." : "Saved Crowns Rewards. It is off.",
        });
    }

}
