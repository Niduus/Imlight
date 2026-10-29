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
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Best Gear tab on the Progress page (Niduus, 2026-10-29: "look at the best statistical choice of gear for my level
/// ... in great detail such as best damage gear for my school, resist, health, healing, universal, weaving (and to what)
/// and so on ... If the player wants to play legit, that's fine, the recommended gear will show them were to obtain
/// it"). For a level and school (or a wizard's), the gear that gives the most of the picked stat in each slot, from the
/// game's own item files (the Gear tab's GearBook), each with where to get it: the shops that sell it (vendors.json),
/// the creatures that drop it and the quests that give it (item-sources.json, written by the server), the recipe that
/// makes it, or the Crown Shop. It only reads; it changes nothing on the server.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string BG_SOURCES_FILE = "item-sources.json";
    private const string BG_NONE = "None";
    private const string BG_MY_SCHOOL = "My school";

    // The slots worth comparing (a pet's stats come from its talents, so pets are left out).
    private static readonly string[] s_bgSlots = ["Hat", "Robe", "Boots", "Wand", "Athame", "Amulet", "Ring", "Deck", "Mount"];

    /// <summary>A goal: how much of it an item gives, for a school (and a second school at half).</summary>
    private sealed record BgGoal(string Name, string Help, Func<GearItem, string, double> Score);

    private static double BgSum(GearItem item, string kind, Func<string, bool> forSchool)
        => item.Stats.Where(s => s.Kind == kind && forSchool(s.School)).Sum(s => (double) s.Amount);

    // A school's stat counts for that school; one with no school counts for every school.
    private static Func<string, bool> BgFor(string school, bool universalOnly)
        => s => s.Length == 0 || (!universalOnly && school.Length > 0 && s.Equals(school, StringComparison.OrdinalIgnoreCase));

    private bool _bgUniversalOnly;

    private BgGoal[] BgGoals() {
        double Of(GearItem item, string kind, string school) => BgSum(item, kind, BgFor(school, _bgUniversalOnly));

        return [
            new("Damage", "Damage % for your school's spells (and every school's).", (i, s) => Of(i, "Damage", s)),
            new("Resist", "Resist % against every school.", (i, _) => BgSum(i, "ReduceDamage", BgFor("", true))),
            new("Health", "Max Health.", (i, _) => Of(i, "MaxHealth", "")),
            new("Outgoing healing", "Outgoing Healing %: your heals heal more.", (i, s) => Of(i, "Healing", s)),
            new("Incoming healing", "Incoming Healing %: heals on you heal more.", (i, s) => Of(i, "IncHealing", s)),
            new("Accuracy", "Accuracy %: your spells fizzle less.", (i, s) => Of(i, "Accuracy", s)),
            new("Pierce", "Armor Piercing %: goes through the enemy's resist.", (i, s) => Of(i, "ArmorPiercing", s)),
            new("Critical", "Critical Hit Rating.", (i, s) => Of(i, "CriticalHit", s)),
            new("Block", "Block Rating: blocks the enemy's critical hits.", (i, s) => Of(i, "Block", s)),
            new("Power pips", "Power Pip Chance %.", (i, s) => Of(i, "PowerPips", s)),
            new("Flat damage", "Flat Damage: this much more on every hit.", (i, s) => Of(i, "FlatDamage", s)),
            new("All-round", "Damage, resist, health, pierce, critical and accuracy together, each weighed about the same.", (i, s) =>
                Of(i, "Damage", s) + BgSum(i, "ReduceDamage", BgFor("", true)) + Of(i, "MaxHealth", "") / 40
                + Of(i, "ArmorPiercing", s) + Of(i, "CriticalHit", s) / 20 + Of(i, "Accuracy", s) / 2),
        ];
    }

    private readonly ComboBox _bgWizard = new() { MinWidth = 220, PlaceholderText = "Or pick a wizard" };
    private readonly NumericUpDown _bgLevel = new() { Minimum = 1, Maximum = 200, Value = 10, Increment = 1, FormatString = "0", Width = 130 };
    private readonly ComboBox _bgSchool = new() { ItemsSource = s_ffSchools, SelectedIndex = 0, MinWidth = 140 };
    private readonly ComboBox _bgGoal = new() { MinWidth = 180 };
    private readonly ComboBox _bgThen = new() { MinWidth = 180 };
    private readonly ComboBox _bgWeave = new() { MinWidth = 150 };
    private readonly NumericUpDown _bgShow = new() { Minimum = 1, Maximum = 10, Value = 3, Increment = 1, FormatString = "0", Width = 110 };
    private readonly CheckBox _bgUniversal = new() { Content = "Only stats for every school (universal)" };
    private readonly CheckBox _bgNoCrowns = new() { Content = "No Crowns-only gear", IsChecked = true };
    private readonly CheckBox _bgKnown = new() { Content = "Only gear I can get (a shop, drop, quest or recipe)", IsChecked = true };
    private readonly CheckBox _bgRetired = new() { Content = "Hide retired gear", IsChecked = true };
    private readonly StackPanel _bgResults = new() { Spacing = 10 };
    private readonly TextBlock _bgCount = new() { TextWrapping = TextWrapping.Wrap };

    private sealed record BgNpc(uint Id, string Name, List<string> Zones, bool Boss);

    private sealed record BgQuest(string Code, string Title, string Zone);

    private sealed record BgItem(List<int> Drops, List<int> Quests);

    private sealed record BgSourcesFile(List<BgNpc> Npcs, List<BgQuest> Quests, Dictionary<uint, BgItem> Items);

    private BgSourcesFile? _bgSources;
    private DateTime _bgSourcesRead;
    private Dictionary<uint, List<VendorNpc>> _bgSoldBy = [];
    private Dictionary<string, List<VendorNpc>> _bgRecipeSoldBy = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, Recipe> _bgMadeBy = [];
    private bool _bgFilling;
    private bool _bgLoading;

    private Control BestGearTab() {
        var goals = BgGoals();
        _bgGoal.ItemsSource = goals.Select(g => g.Name).ToList();
        _bgGoal.SelectedIndex = 0;
        _bgThen.ItemsSource = new[] { BG_NONE }.Concat(goals.Select(g => g.Name)).ToList();
        _bgThen.SelectedIndex = 3;
        _bgWeave.ItemsSource = new[] { BG_NONE }.Concat(s_ffSchools).ToList();
        _bgWeave.SelectedIndex = 0;
        foreach (var box in new[] { _bgSchool, _bgGoal, _bgThen, _bgWeave }) {
            box.SelectionChanged += (_, e) => {
                if (e.Source == box && !_bgFilling) {
                    RenderBestGear();
                }
            };
        }

        _bgWizard.SelectionChanged += (_, _) => {
            if (_bgFilling || _bgWizard.SelectedItem is not FfWizard wizard) {
                return;
            }

            _bgFilling = true;
            if (wizard.Level > 0) {
                _bgLevel.Value = wizard.Level;
            }

            if (s_ffSchools.FirstOrDefault(s => s.Equals(wizard.School, StringComparison.OrdinalIgnoreCase)) is { } school) {
                _bgSchool.SelectedItem = school;
            }

            _bgFilling = false;
            RenderBestGear();
        };
        _bgLevel.ValueChanged += (_, _) => {
            if (!_bgFilling) {
                RenderBestGear();
            }
        };
        _bgShow.ValueChanged += (_, _) => RenderBestGear();
        foreach (var tick in new[] { _bgUniversal, _bgNoCrowns, _bgKnown, _bgRetired }) {
            tick.IsCheckedChanged += (_, _) => RenderBestGear();
        }

        return Page(
            Fold("shield", "Best Gear", "Best Gear",
                "The gear that gives the most of what you want at your level, slot by slot, and where to get each piece.",
                WithHint(new WrapPanel {
                    Children = {
                        SearchFilter("Level", _bgLevel),
                        SearchFilter("School", _bgSchool),
                        SearchFilter("Wizard", _bgWizard),
                    },
                }, "Type a level and pick a school, or pick one of your wizards to use theirs (as of the last time they played). " +
                   "Only gear that level and school can wear is listed."),
                WithHint(new WrapPanel {
                    Children = {
                        SearchFilter("Most of", _bgGoal),
                        SearchFilter("Then", _bgThen),
                        SearchFilter("Weave with (second school)", _bgWeave),
                        SearchFilter("Show per slot", _bgShow),
                    },
                }, "Most of: the stat that decides. Then: breaks ties, and is shown beside it. Weave with: a second school you also " +
                   "cast (for example Fire with Storm spells): that school's damage, accuracy, pierce and critical count at half, so gear " +
                   "good for both rises to the top. Resist counts resist against every school; resist against one school is left out."),
                new WrapPanel { Children = { SearchTick(_bgUniversal), SearchTick(_bgNoCrowns), SearchTick(_bgKnown), SearchTick(_bgRetired) } },
                _bgCount),
            Card(_bgResults));
    }

    /// <summary>Reads the gear, the recipes and where items come from. Runs when the tab shows.</summary>
    private async Task LoadBestGearAsync() {
        FillBestGearWizards();
        if (_bgLoading) {
            return;
        }

        _bgLoading = true;
        try {
            if (_gearBook is null) {
                _bgCount.Text = "Reading the gear from your game files…";
                await LoadGearTabAsync();
            }

            await LoadVendorsAsync();
            ReadItemSources();
            if (_recipeBook is null) {
                _bgCount.Text = "Reading the recipes from your game files…";
                await LoadRecipesTabAsync();
            }

            IndexBestGearSources();
        }
        finally {
            _bgLoading = false;
        }

        RenderBestGear();
    }

    private void FillBestGearWizards() {
        if (_ffWizard.ItemsSource is null) {
            FillFastForwardWizards();
        }

        var wizards = (_ffWizard.ItemsSource as List<FfWizard>) ?? [];
        var picked = (_bgWizard.SelectedItem as FfWizard)?.Id;
        _bgFilling = true;
        _bgWizard.ItemsSource = wizards;
        _bgWizard.SelectedItem = wizards.FirstOrDefault(w => w.Id == picked);
        _bgFilling = false;
    }

    private void ReadItemSources() {
        var path = Path.Combine(_manager.IdLookupFolder, BG_SOURCES_FILE);
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (written == _bgSourcesRead) {
                return;
            }

            _bgSources = written == DateTime.MinValue ? null : JsonSerializer.Deserialize<BgSourcesFile>(File.ReadAllText(path));
            _bgSourcesRead = written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written: read on the next look.
        }
    }

    // Which vendors sell each item and each recipe, and which recipe makes each item.
    private void IndexBestGearSources() {
        var soldBy = new Dictionary<uint, List<VendorNpc>>();
        var recipeSoldBy = new Dictionary<string, List<VendorNpc>>(StringComparer.OrdinalIgnoreCase);
        foreach (var npc in _vendors.Select(v => v.Npc)) {
            foreach (var shop in npc.Shops) {
                foreach (var id in shop.Items ?? []) {
                    (soldBy.TryGetValue(id, out var list) ? list : soldBy[id] = []).Add(npc);
                }

                foreach (var code in shop.Recipes ?? []) {
                    (recipeSoldBy.TryGetValue(code, out var list) ? list : recipeSoldBy[code] = []).Add(npc);
                }
            }
        }

        var madeBy = new Dictionary<uint, Recipe>();
        foreach (var recipe in _recipeBook?.Recipes ?? []) {
            if (recipe.MakesId != 0) {
                madeBy.TryAdd(recipe.MakesId, recipe);
            }
        }

        (_bgSoldBy, _bgRecipeSoldBy, _bgMadeBy) = (soldBy, recipeSoldBy, madeBy);
    }

    private bool BgCanGet(GearItem item)
        => _bgSoldBy.ContainsKey(item.Id) || _bgMadeBy.ContainsKey(item.Id)
           || (_bgSources?.Items.TryGetValue(item.Id, out var from) == true && (from.Drops.Count > 0 || from.Quests.Count > 0));

    private void RenderBestGear() {
        _bgResults.Children.Clear();
        if (_gearBook is not { } gear) {
            _bgCount.Text = _gearProblem ?? "Reading the gear from your game files…";
            return;
        }

        var goals = BgGoals();
        _bgUniversalOnly = _bgUniversal.IsChecked == true;
        var goal = goals.FirstOrDefault(g => g.Name == _bgGoal.SelectedItem as string) ?? goals[0];
        var then = goals.FirstOrDefault(g => g.Name == _bgThen.SelectedItem as string && g != goal);
        var weave = _bgWeave.SelectedItem as string is { } w && w != BG_NONE ? w : "";
        var level = (int) (_bgLevel.Value ?? 1);
        var school = _bgSchool.SelectedItem as string ?? "";
        var noCrowns = _bgNoCrowns.IsChecked == true;
        var known = _bgKnown.IsChecked == true;
        var hideRetired = _bgRetired.IsChecked == true;
        var perSlot = (int) (_bgShow.Value ?? 3);

        double Score(GearItem item) {
            var score = goal.Score(item, school);
            if (weave.Length > 0 && !weave.Equals(school, StringComparison.OrdinalIgnoreCase)) {
                // The second school's own stats count at half (its universal ones are counted already).
                score += (goal.Score(item, weave) - goal.Score(item, "")) / 2;
            }

            return score;
        }

        var wearable = gear.Items.Where(i => s_bgSlots.Contains(i.Slot) && i.Name != i.Code && i.Level <= level
                                             && (i.SchoolNeeded.Length == 0 || i.SchoolNeeded.Equals(school, StringComparison.OrdinalIgnoreCase))
                                             && (!noCrowns || !i.CrownsOnly) && (!hideRetired || !i.Retired) && i.Rental.Length == 0)
            .ToList();
        var hidden = 0;
        if (known) {
            var before = wearable.Count;
            wearable = [.. wearable.Where(BgCanGet)];
            hidden = before - wearable.Count;
        }

        _bgCount.Text = $"Level {level} {school}: {wearable.Count:N0} pieces of gear fit." +
                        (hidden > 0 ? $" {hidden:N0} more have no known place to get them (untick the box to see them)." : "") +
                        (_bgSources is null ? " Start the server once to see which creatures drop each piece and which quests give it." : "");

        foreach (var slot in s_bgSlots) {
            var best = wearable.Where(i => i.Slot == slot)
                .Select(i => (Item: i, Score: Score(i), Then: then?.Score(i, school) ?? 0))
                .Where(p => p.Score > 0)
                .OrderByDescending(p => p.Score).ThenByDescending(p => p.Then).ThenByDescending(p => p.Item.Level)
                .Take(perSlot).ToList();

            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock { Text = slot, FontWeight = FontWeight.SemiBold, FontSize = ManagerTheme.FONT_HEADING });
            if (best.Count == 0) {
                section.Children.Add(Sub($"No {slot.ToLowerInvariant()} at level {level} or below gives {goal.Name.ToLowerInvariant()}."));
            }

            foreach (var (item, score, thenScore) in best) {
                section.Children.Add(BestGearRow(gear, item, goal, score, then, thenScore));
            }

            _bgResults.Children.Add(section);
        }
    }

    private Control BestGearRow(GearBook gear, GearItem item, BgGoal goal, double score, BgGoal? then, double thenScore) {
        var stats = item.Stats.Where(s => s.Kind.Length > 0 && s.Amount != 0).Select(GearStatWords).Take(8).ToList();
        var head = $"{item.Name}  ·  level {item.Level}" + (item.SchoolNeeded.Length > 0 ? $"  ·  {item.SchoolNeeded} only" : "") +
                   $"  ·  {goal.Name} {score:0.#}" + (then is not null && thenScore > 0 ? $", {then.Name} {thenScore:0.#}" : "");
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new SelectableTextBlock { Text = head, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (stats.Count > 0) {
            body.Children.Add(new TextBlock { Text = string.Join("  ·  ", stats), Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
        }

        if (item.Set is { } set) {
            body.Children.Add(new TextBlock { Text = $"Part of a set: {set.Name}", Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var line in WhereToGet(item)) {
            body.Children.Add(new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        }

        var give = Plain("Give to my wizard");
        ToolTip.SetTip(give, $"Sends {GiveGearCommand(item)} to the wizard playing on this PC. Start the server and log in first.");
        var show = Plain("Show in Gear");
        ToolTip.SetTip(show, "Opens the Gear tab on this item, with its tooltip as the game draws it.");
        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top,
            Children = {
                Action(show, () => {
                    _gearSearch.Text = item.Id.ToString(CultureInfo.InvariantCulture);
                    SelectTab("Gear");

                    return Task.CompletedTask;
                }),
                Action(give, () => {
                    _manager.SendCommand(GiveGearCommand(item));
                    Say($"Sent {GiveGearCommand(item)} ({item.Name}). Look in your backpack in the game.");

                    return Task.CompletedTask;
                }),
            },
        };

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
            Child = new DockPanel { Children = { WithDock(WithMargin(buttons, new Thickness(12, 0, 0, 0)), Dock.Right), body } },
        };
    }

    /// <summary>Where an item can be had, one line per kind of place, in plain words.</summary>
    private List<string> WhereToGet(GearItem item) {
        var lines = new List<string>();
        static string Place(IEnumerable<string> zones) {
            var zone = zones.FirstOrDefault() ?? "";
            if (zone.Length == 0) {
                return "";
            }

            var parts = zone.Split('|');
            var world = WorldOfZone(parts[0]);
            var name = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];

            return name.Equals(world, StringComparison.OrdinalIgnoreCase) ? $" ({world})" : $" ({name}, {world})";
        }

        if (_bgSoldBy.TryGetValue(item.Id, out var vendors)) {
            // Prefer the copy of a shopkeeper with a known place; one line per name.
            var names = vendors.DistinctBy(v => v.Id).OrderBy(v => v.Zones.Count > 0 ? 0 : 1)
                .Select(v => v.Name + (v.Zones.Count > 0 ? Place([string.Join('|', v.Zones[0])]) : ""))
                .DistinctBy(n => n.Split(" (")[0], StringComparer.OrdinalIgnoreCase).Take(4);
            lines.Add($"Shop: {string.Join("; ", names)}" + (item.GoldCost > 0 ? $"  ·  {item.GoldCost:N0} gold" : ""));
        }

        if (_bgSources?.Items.TryGetValue(item.Id, out var from) == true) {
            var drops = from.Drops.Where(i => i >= 0 && i < _bgSources.Npcs.Count).Select(i => _bgSources.Npcs[i])
                .Where(n => n.Name.Length > 0).OrderByDescending(n => n.Boss).ThenBy(n => n.Zones.Count == 0).ToList();
            if (drops.Count > 0) {
                var names = drops.DistinctBy(n => n.Name).Take(4).Select(n => (n.Boss ? "boss " : "") + n.Name + Place(n.Zones));
                lines.Add($"Drop: {string.Join("; ", names)}" + (drops.Count > 4 ? $" and {drops.Count - 4} more" : ""));
            }

            var quests = from.Quests.Where(i => i >= 0 && i < _bgSources.Quests.Count).Select(i => _bgSources.Quests[i]).ToList();
            if (quests.Count > 0) {
                lines.Add($"Quest reward: {string.Join("; ", quests.Take(3).Select(q => q.Title))}");
            }
        }

        if (_bgMadeBy.TryGetValue(item.Id, out var recipe)) {
            var sellers = _bgRecipeSoldBy.TryGetValue(recipe.Code, out var list)
                ? $", recipe sold by {string.Join("; ", list.DistinctBy(v => v.Id).Take(2).Select(v => v.Name + (v.Zones.Count > 0 ? Place([string.Join('|', v.Zones[0])]) : "")))}"
                : "";
            lines.Add($"Crafted: {recipe.Name}{sellers}");
        }

        if (item.CrownsOnly || (lines.Count == 0 && item.CrownsCost > 0)) {
            lines.Add("Crown Shop" + (item.CrownsCost > 0 ? $": {item.CrownsCost:N0} crowns" : ""));
        }

        if (lines.Count == 0) {
            lines.Add("Where to get it is not known.");
        }

        return lines;
    }

}
