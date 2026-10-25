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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Vendors &amp; Trainers tab (Niduus, 2026-10-25: "add to manager quests, vendor and trainer searches"): every shop
/// keeper and trainer of the game, with where it stands, what it sells (items, recipes, treasure cards) and what it
/// teaches, searchable by every one of those, listed in full when nothing is typed. The server writes the list
/// (vendors.json in the ID lookup folder); item and spell names come from the ID lookup.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string VENDORS_FILE = "vendors.json";

    private sealed record VendorShop(string Window, string Title, string From, List<uint> Items, List<string> Recipes);
    private sealed record VendorSpell(uint Id, int Level);
    private sealed record VendorNpc(uint Id, string Name, string Object, string Kind, string Test, List<string[]> Zones,
        List<VendorShop> Shops, List<string> TreasureCards, List<VendorSpell> Spells) {
        internal int Things => Shops.Sum(s => s.Items.Count + s.Recipes.Count) + TreasureCards.Count + Spells.Count;
        internal string World => Zones.Count > 0 ? WorldOfZone(Zones[0][0]) : "";
        internal string Place => Zones.Count > 0 ? ZoneShown(Zones[0]) : "";
    }

    private sealed record VendorRow(VendorNpc Npc, string Haystack, string Sold, string Taught, string[] Schools);

    private static readonly string[] s_vendorKinds = [ANY_FILTER, "Vendor", "Trainer", "Vendor and trainer", "Test trainer"];
    private static readonly string[] s_vendorSorts = ["Name", "World", "Most things first", "Fewest things first", "Template ID"];

    private readonly TextBox _vendorSearch = new() { Watermark = "Search an NPC, a place, or anything it sells or teaches, such as Fire Elf or 12345" };
    private readonly ComboBox _vendorKind = FilterCombo(s_vendorKinds, 150);
    private readonly ComboBox _vendorWindow = FilterCombo([ANY_FILTER], 160);
    private readonly ComboBox _vendorWorld = FilterCombo([ANY_FILTER], 160);
    private readonly ComboBox _vendorZone = FilterCombo([ANY_FILTER], 220);
    private readonly ComboBox _vendorFrom = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _vendorSchool = FilterCombo([ANY_FILTER], 130);
    private readonly TextBox _vendorSells = new() { Watermark = ANY_FILTER, Width = 180 };
    private readonly TextBox _vendorTeaches = new() { Watermark = ANY_FILTER, Width = 180 };
    private readonly NumericUpDown _vendorLevelMin = FilterNumber(5);
    private readonly NumericUpDown _vendorLevelMax = FilterNumber(5);
    private readonly NumericUpDown _vendorCountMin = FilterNumber(5);
    private readonly NumericUpDown _vendorCountMax = FilterNumber(5);
    private readonly ComboBox _vendorSort = FilterCombo(s_vendorSorts, 160);
    private readonly CheckBox _vendorCards = new() { Content = "Sells treasure cards" };
    private readonly CheckBox _vendorRecipes = new() { Content = "Sells recipes" };
    private readonly CheckBox _vendorPlaced = new() { Content = "Only ones standing in a zone" };
    private readonly CheckBox _vendorShops = new() { Content = "Has more than one shop" };
    private readonly CheckBox _vendorChanged = new() { Content = "Only ones I changed" };
    private readonly TextBlock _vendorCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _vendorList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _vendorDetails = new() { Spacing = 6 };

    private List<VendorRow> _vendors = [];
    private DateTime _vendorsWritten;
    private Lookup? _vendorNamesFrom;
    private VendorNpc? _vendorPicked;

    private string VendorsPath => Path.Combine(_manager.IdLookupFolder, VENDORS_FILE);

    private Control VendorsTab() {
        _vendorList.ItemTemplate = new FuncDataTemplate<VendorRow>((row, _) => row is null ? new Panel() : VendorRowView(row));
        _vendorList.SelectionChanged += (_, e) => {
            if (e.Source == _vendorList && _vendorList.SelectedItem is VendorRow row && row.Npc != _vendorPicked) {
                ShowVendor(row.Npc);
            }
        };
        WhenFiltersChange(RenderVendors, _vendorSearch, _vendorKind, _vendorWindow, _vendorWorld, _vendorZone, _vendorFrom,
            _vendorSchool, _vendorSells, _vendorTeaches, _vendorLevelMin, _vendorLevelMax, _vendorCountMin, _vendorCountMax,
            _vendorSort, _vendorCards, _vendorRecipes, _vendorPlaced, _vendorShops, _vendorChanged);

        var details = new ScrollViewer { Width = 430, Margin = new Thickness(14, 0, 0, 0), Content = Card(_vendorDetails) };
        ShowVendor(null);

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("bag", "Vendors", "Find vendors and trainers",
                    "Search or filter, then click one to see everything it sells or teaches and where it stands.",
                    WithHint(_vendorSearch, "Search by the NPC's name, its zone or world, its shop's name, or the name or ID of anything it " +
                        "sells or teaches. With nothing typed, every vendor and trainer shows. Click one to see its whole list."),
                    SearchFilters(
                        SearchFilter("Kind", _vendorKind),
                        SearchFilter("Shop window", _vendorWindow),
                        SearchFilter("World", _vendorWorld),
                        SearchFilter("Zone", _vendorZone),
                        SearchFilter("Sells something called", _vendorSells),
                        SearchFilter("Teaches a spell called", _vendorTeaches),
                        SearchFilter("Teaches a school", _vendorSchool),
                        SearchFilter("Spell level from", _vendorLevelMin),
                        SearchFilter("Spell level to", _vendorLevelMax),
                        SearchFilter("How many things, from", _vendorCountMin),
                        SearchFilter("How many things, to", _vendorCountMax),
                        SearchFilter("List comes from", _vendorFrom),
                        SearchFilter("Sort by", _vendorSort),
                        SearchTick(_vendorCards),
                        SearchTick(_vendorRecipes),
                        SearchTick(_vendorShops),
                        SearchTick(_vendorPlaced),
                        SearchTick(_vendorChanged)),
                    _vendorCount), Dock.Top),
                WithMargin(_vendorList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the list again when the server has written it since, with the ID lookup for names. Runs when the tab shows.</summary>
    private async Task LoadVendorsAsync() {
        await LoadLookupAsync();
        var path = VendorsPath;
        try {
            if (!File.Exists(path)) {
                _vendors = [];
                _vendorsWritten = default;
                RenderVendors();

                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (written == _vendorsWritten && _vendorNamesFrom == _lookup) {
                return;
            }

            _vendorCount.Text = "Loading…";
            var lookup = _lookup;
            var rows = await Task.Run(() => ReadVendors(path, lookup));
            (_vendors, _vendorsWritten, _vendorNamesFrom) = (rows, written, lookup);
            FillFilterCombo(_vendorWindow, rows.SelectMany(r => r.Npc.Shops).Select(s => s.Window).OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_vendorWorld, rows.Select(r => r.Npc.World).OrderBy(WorldOrder).ThenBy(w => w, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_vendorZone, rows.SelectMany(r => r.Npc.Zones).Select(ZoneShown).OrderBy(z => z, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_vendorFrom, rows.SelectMany(r => r.Npc.Shops).Select(s => s.From).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_vendorSchool, rows.SelectMany(r => r.Schools).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _vendorCount.Text = $"Could not read {VENDORS_FILE}: {ex.Message}";

            return;
        }

        RenderVendors();
    }

    private static List<VendorRow> ReadVendors(string path, Lookup? lookup) {
        var npcs = JsonSerializer.Deserialize<List<VendorNpc>>(File.ReadAllText(path)) ?? [];
        var names = new Dictionary<string, LookupEntry>(StringComparer.Ordinal);
        foreach (var entry in lookup?.Entries ?? []) {
            if (entry.Kind is "Item" or "Spell") {
                names.TryAdd($"{entry.Kind}:{entry.Id}", entry);
            }
        }

        var rows = new List<VendorRow>();
        foreach (var npc in npcs.Where(n => n is not null)) {
            var npcFixed = npc with {
                Zones = npc.Zones ?? [], Shops = npc.Shops ?? [], TreasureCards = npc.TreasureCards ?? [], Spells = npc.Spells ?? [],
                Name = npc.Name ?? "", Object = npc.Object ?? "", Kind = npc.Kind ?? "", Test = npc.Test ?? "",
            };
            var sold = string.Join(' ', npcFixed.Shops.SelectMany(s => s.Items ?? []).Select(id => names.TryGetValue($"Item:{id}", out var e) ? $"{e.Name} {id}" : $"{id}"))
                + " " + string.Join(' ', npcFixed.Shops.SelectMany(s => s.Recipes ?? [])) + " " + string.Join(' ', npcFixed.TreasureCards);
            var spells = npcFixed.Spells.Select(s => names.TryGetValue($"Spell:{s.Id}", out var e) ? e : null).ToList();
            var taught = string.Join(' ', npcFixed.Spells.Zip(spells, (s, e) => e is null ? $"{s.Id}" : $"{e.Name} {s.Id}"));
            var schools = spells.Where(e => e is not null).Select(e => e!.School).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var haystack = SearchText.Normalize($"{npcFixed.Name} {npcFixed.Object} {npcFixed.Id} {npcFixed.Kind} {npcFixed.Test} " +
                $"{string.Join(' ', npcFixed.Zones.Select(z => string.Join(' ', z)))} {npcFixed.World} " +
                $"{string.Join(' ', npcFixed.Shops.Select(s => $"{s.Window} {s.Title} {s.From}"))} {sold} {taught} {string.Join(' ', schools)}");
            rows.Add(new VendorRow(npcFixed, haystack, SearchText.Normalize(sold), SearchText.Normalize(taught), schools));
        }

        return [.. rows.OrderBy(r => r.Npc.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string WorldOfZone(string zone) {
        var top = zone.Split('/')[0];
        foreach (var (_, world) in s_questWorlds) {
            if (world.Replace(" ", "").Equals(top, StringComparison.OrdinalIgnoreCase)) {
                return world;
            }
        }

        return top;
    }

    private static string ZoneShown(string[] zone)
        => zone.Length > 1 && zone[1].Length > 0 ? $"{zone[1]} ({zone[0]})" : zone.Length > 0 ? zone[0] : "";

    private void RenderVendors() {
        if (_vendors.Count == 0) {
            _vendorList.ItemsSource = null;
            _vendorCount.Text = File.Exists(VendorsPath)
                ? "The vendor list is empty."
                : "No vendor list yet. Start the server once: it writes the list about a minute after it starts (with the ID lookup on), " +
                  "and it shows here then.";

            return;
        }

        var words = SearchText.Words(_vendorSearch.Text);
        var kind = FilterPick(_vendorKind);
        var window = FilterPick(_vendorWindow);
        var world = FilterPick(_vendorWorld);
        var zone = FilterPick(_vendorZone);
        var from = FilterPick(_vendorFrom);
        var school = FilterPick(_vendorSchool);
        var sells = SearchText.Words(_vendorSells.Text);
        var teaches = SearchText.Words(_vendorTeaches.Text);
        var levels = _vendorLevelMin.Value is not null || _vendorLevelMax.Value is not null;
        var changed = _vendorChanged.IsChecked == true ? SafeNpcChanges().Select(c => c.TemplateID).ToHashSet() : null;

        var rows = _vendors.Where(row => {
            var npc = row.Npc;

            return words.All(w => row.Haystack.Contains(w, StringComparison.Ordinal))
                && kind switch {
                    null => true,
                    "Test trainer" => npc.Test.Length > 0,
                    _ => npc.Kind == kind,
                }
                && (window is null || npc.Shops.Any(s => s.Window == window))
                && (world is null || npc.Zones.Any(z => WorldOfZone(z[0]) == world))
                && (zone is null || npc.Zones.Any(z => ZoneShown(z) == zone))
                && (from is null || npc.Shops.Any(s => s.From == from))
                && (school is null || row.Schools.Contains(school, StringComparer.OrdinalIgnoreCase))
                && sells.All(w => row.Sold.Contains(w, StringComparison.Ordinal))
                && teaches.All(w => row.Taught.Contains(w, StringComparison.Ordinal))
                && (!levels || npc.Spells.Any(s => InFilterRange(s.Level, _vendorLevelMin, _vendorLevelMax)))
                && InFilterRange(npc.Things, _vendorCountMin, _vendorCountMax)
                && (_vendorCards.IsChecked != true || npc.TreasureCards.Count > 0)
                && (_vendorRecipes.IsChecked != true || npc.Shops.Any(s => s.Recipes.Count > 0))
                && (_vendorShops.IsChecked != true || npc.Shops.Count > 1)
                && (_vendorPlaced.IsChecked != true || npc.Zones.Count > 0)
                && (changed is null || changed.Contains(npc.Id));
        }).ToList();

        rows = _vendorSort.SelectedIndex switch {
            1 => [.. rows.OrderBy(r => r.Npc.World.Length == 0).ThenBy(r => WorldOrder(r.Npc.World)).ThenBy(r => r.Npc.World, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Npc.Place, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Npc.Name, StringComparer.OrdinalIgnoreCase)],
            2 => [.. rows.OrderByDescending(r => r.Npc.Things).ThenBy(r => r.Npc.Name, StringComparer.OrdinalIgnoreCase)],
            3 => [.. rows.OrderBy(r => r.Npc.Things).ThenBy(r => r.Npc.Name, StringComparer.OrdinalIgnoreCase)],
            4 => [.. rows.OrderBy(r => r.Npc.Id)],
            _ => rows,
        };

        _vendorList.ItemsSource = rows;
        var vendors = rows.Count(r => r.Npc.Kind != "Trainer");
        _vendorCount.Text = $"{FilterCount(rows.Count, _vendors.Count, "vendors and trainers")} " +
                            $"{vendors:N0} sell, {rows.Count(r => r.Npc.Spells.Count > 0):N0} teach.";
    }

    private Control VendorRowView(VendorRow row) {
        var npc = row.Npc;
        var what = new List<string>();
        foreach (var shop in npc.Shops) {
            var count = shop.Items.Count + shop.Recipes.Count;
            what.Add($"{shop.Window} shop, {count:N0} {(shop.Recipes.Count > 0 ? "recipe" : "item")}{(count == 1 ? "" : "s")}");
        }

        if (npc.TreasureCards.Count > 0) {
            what.Add($"{npc.TreasureCards.Count:N0} treasure cards");
        }

        if (npc.Spells.Count > 0) {
            what.Add($"{(what.Count == 0 ? "Teaches" : "teaches")} {npc.Spells.Count:N0} spell{(npc.Spells.Count == 1 ? "" : "s")}{(row.Schools.Length is > 0 and <= 3 ? $" ({string.Join(", ", row.Schools)})" : "")}");
        }

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel {
                Spacing = 2,
                Children = {
                    new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        Children = {
                            new TextBlock { Text = npc.Name.Length > 0 ? npc.Name : npc.Object, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                            WithCenter(new TextBlock {
                                Text = string.Join("  ·  ", new[] { npc.Test.Length > 0 ? $"Test trainer ({npc.Test})" : npc.Kind, npc.World, npc.Id.ToString(CultureInfo.InvariantCulture) }
                                    .Where(t => t.Length > 0)),
                                Foreground = MUTED,
                            }),
                        },
                    },
                    Sub(string.Join("; ", what) + (npc.Place.Length > 0 ? $". In {npc.Place}{(npc.Zones.Count > 1 ? $" and {npc.Zones.Count - 1} more" : "")}." : ". Not standing in any zone the app knows.")),
                },
            },
        };
    }

    private void ShowVendor(VendorNpc? npc) {
        _vendorPicked = npc;
        _vendorDetails.Children.Clear();
        if (npc is null) {
            _vendorDetails.Children.Add(Sub("Click a vendor or trainer on the left to see everything it sells or teaches."));

            return;
        }

        var names = new Dictionary<string, LookupEntry>(StringComparer.Ordinal);
        foreach (var entry in _lookup?.Entries ?? []) {
            if (entry.Kind is "Item" or "Spell") {
                names.TryAdd($"{entry.Kind}:{entry.Id}", entry);
            }
        }

        string NameOf(string kind, uint id) => names.TryGetValue($"{kind}:{id}", out var e) ? e.Name : $"Unknown {kind.ToLowerInvariant()}";

        _vendorDetails.Children.Add(new SelectableTextBlock { Text = npc.Name.Length > 0 ? npc.Name : npc.Object, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _vendorDetails.Children.Add(Sub($"{(npc.Test.Length > 0 ? $"Test trainer ({npc.Test})" : npc.Kind)}. Object {npc.Object}."));
        var id = npc.Id.ToString(CultureInfo.InvariantCulture);
        _vendorDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });

        var buttons = new WrapPanel();
        if (npc.Zones.Count > 0) {
            var zone = npc.Zones[0][0];
            buttons.Children.Add(Spaced(Action(Plain("Teleport there"), () => {
                _manager.SendCommand($".tp {zone}");
                Say($"Sent .tp {zone}. {(npc.Name.Length > 0 ? npc.Name : "The NPC")} stands somewhere in that zone.");

                return Task.CompletedTask;
            })));
        }

        // The Customs tab's editor for the game's vendors and trainers (what they sell or teach, and at what price).
        var changed = SafeNpcChanges().Any(c => c.TemplateID == npc.Id);
        var change = Spaced(Action(Primary(changed ? "Edit my changes" : "Change this NPC"), () => {
            OpenNpcChanges(npc.Id);

            return Task.CompletedTask;
        }));
        ToolTip.SetTip(change, "Opens Customs, Change the game's vendors and trainers, for this NPC: hide its shop, add or remove items, " +
            "change prices or the spells it teaches. The list here is the game's own, without your changes.");
        buttons.Children.Add(change);

        if (buttons.Children.Count > 0) {
            _vendorDetails.Children.Add(buttons);
        }

        void Heading(string text) => _vendorDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 10, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => _vendorDetails.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        Heading("Where it stands");
        Lines(npc.Zones.Count > 0 ? npc.Zones.Select(ZoneShown) : ["Not in any zone the app knows (it may be an old or test NPC, or one a quest brings)."]);

        foreach (var shop in npc.Shops) {
            Heading($"{shop.Window} shop{(shop.Title.Length > 0 ? $": {shop.Title}" : "")}");
            _vendorDetails.Children.Add(Sub($"List from {shop.From}. {shop.Items.Count + shop.Recipes.Count:N0} things."));
            Lines(shop.Items.Select(item => $"{NameOf("Item", item)}  ({item})").Concat(shop.Recipes));
        }

        if (npc.TreasureCards.Count > 0) {
            Heading($"Treasure cards ({npc.TreasureCards.Count:N0})");
            Lines(npc.TreasureCards);
        }

        if (npc.Spells.Count > 0) {
            Heading($"Teaches ({npc.Spells.Count:N0})");
            Lines(npc.Spells.OrderBy(s => s.Level).ThenBy(s => NameOf("Spell", s.Id), StringComparer.OrdinalIgnoreCase).Select(s => {
                var school = names.TryGetValue($"Spell:{s.Id}", out var e) && e.School.Length > 0 ? $", {e.School}" : "";

                return $"{NameOf("Spell", s.Id)}  ({s.Id}{school}{(s.Level > 0 ? $", level {s.Level}" : "")})";
            }));
        }
    }

}
