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
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Furniture tab (Niduus, 2026-10-28: "Have Ahziael also give a furniture browser with a .furniture command for it as
/// well"): every house item of the game, searchable and filtered by kind, world, rarity, marks, price
/// and what it does. Clicking one shows its details with a button that gives it to the wizard playing here (.furniture).
/// </summary>
internal sealed partial class ManagerWindow {

    private DetailsPopup? _furniturePopup;

    private static readonly string[] s_furnitureSorts = [
        "Best match", "Name", "Kind", "World or set", "Gold cost (low first)", "Gold cost (high first)", "Template ID",
    ];

    private readonly TextBox _furnitureSearch = new() { Watermark = "Search a house item by name or ID, such as Scroll Stand or 82947" };
    private readonly ComboBox _furnitureCategory = FilterCombo([ANY_FILTER, .. FurnitureBook.CATEGORIES], 150);
    private readonly ComboBox _furnitureFrom = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _furnitureRarity = FilterCombo([ANY_FILTER], 120);
    private readonly ComboBox _furnitureMark = FilterCombo([ANY_FILTER], 160);
    private readonly NumericUpDown _furnitureGoldMin = FilterNumber(100, 130);
    private readonly NumericUpDown _furnitureGoldMax = FilterNumber(100, 130);
    private readonly ComboBox _furnitureSort = FilterCombo(s_furnitureSorts, 170);
    private readonly CheckBox _furnitureCrowns = new() { Content = "Crowns only" };
    private readonly CheckBox _furnitureCrafted = new() { Content = "Crafted" };
    private readonly CheckBox _furnitureSold = new() { Content = "Sold by a vendor" };
    private readonly CheckBox _furnitureInteractive = new() { Content = "Does something when you press X" };
    private readonly CheckBox _furnitureSound = new() { Content = "Makes sound or music" };
    private readonly CheckBox _furnitureAnimated = new() { Content = "Moves (animated)" };
    private readonly CheckBox _furnitureLimit = new() { Content = "Has a limit per house" };
    private readonly CheckBox _furnitureTest = new() { Content = "Unnamed and test items" };
    private readonly CheckBox _furnitureFavoritesOnly = FavoritesOnlyTick();
    private readonly NumericUpDown _furnitureAmount = new() { Minimum = 1, Maximum = 100, Value = 1, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _furnitureCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _furnitureList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly Border _furniturePicture = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _furnitureDetails = new() { Spacing = 4 };
    private readonly DispatcherTimer _furnitureDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private FurnitureBook? _furnitureBook;
    private string? _furnitureProblem;
    private Task? _furnitureLoading;
    private FurnitureItem? _furniturePicked;
    private bool _furnitureFiltersFilled;
    // Which vendors sell each item, from the Vendors tab's list (vendors.json), by template ID.
    private Dictionary<uint, List<string>> _furnitureSellers = [];
    private List<VendorRow>? _furnitureSellersFrom;

    private Control FurnitureTab() {
        _furnitureDelay.Tick += (_, _) => {
            _furnitureDelay.Stop();
            RenderFurnitureTab();
        };
        _furnitureSearch.TextChanged += (_, _) => {
            _furnitureDelay.Stop();
            _furnitureDelay.Start();
        };
        WhenFiltersChange(RenderFurnitureTab, _furnitureCategory, _furnitureFrom, _furnitureRarity, _furnitureMark, _furnitureGoldMin,
            _furnitureGoldMax, _furnitureSort, _furnitureCrowns, _furnitureCrafted, _furnitureSold, _furnitureInteractive, _furnitureSound,
            _furnitureAnimated, _furnitureLimit, _furnitureTest, _furnitureFavoritesOnly);
        WhenFavoritesChange(FAV_FURNITURE, RenderFurnitureTab);
        _furnitureList.ItemTemplate = new FuncDataTemplate<FurnitureItem>((item, _) => item is null ? new Panel() : FurnitureRow(item));
        _furnitureList.SelectionChanged += (_, e) => {
            if (e.Source == _furnitureList && _furnitureList.SelectedItem is FurnitureItem item && item != _furniturePicked) {
                ShowFurniture(item);
            }
        };

        var filters = SearchFilters(
            SearchFilter("Kind", _furnitureCategory),
            SearchFilter("World or set", _furnitureFrom),
            SearchFilter("Rarity", _furnitureRarity),
            SearchFilter("Mark", _furnitureMark),
            SearchFilter("Gold cost from", _furnitureGoldMin),
            SearchFilter("Gold cost to", _furnitureGoldMax),
            SearchFilter("Sort by", _furnitureSort),
            SearchTick(_furnitureCrowns),
            SearchTick(_furnitureCrafted),
            SearchTick(_furnitureSold),
            SearchTick(_furnitureInteractive),
            SearchTick(_furnitureSound),
            SearchTick(_furnitureAnimated),
            SearchTick(_furnitureLimit),
            SearchTick(_furnitureTest),
            SearchTick(_furnitureFavoritesOnly));
        var details = new ScrollViewer {
            Width = 560,
            Content = new StackPanel { Spacing = 12, Children = { _furniturePicture, _furnitureDetails } },
        };
        _furniturePopup = new DetailsPopup("House item details", details, () => { _furnitureList.SelectedItem = null; ShowFurniture(null); },
            by => StepList(_furnitureList, by));
        var root = new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(18, 10, 18, 14),
                    Children = {
                        WithDock(Card(new StackPanel {
                            Spacing = 10,
                            Children = {
                                MissingNote("the .furniture command is " + ServerSource.DefaultName + "'s. [Give to my wizard] sends .mod additem instead.",
                                    () => !_manager.HasFurnitureCommand),
                                WithHint(_furnitureSearch, "Search by name or template ID, or narrow the list with the filters: kind (furniture, " +
                                    "decorations, wall hangings, outdoor items, plants, music scrolls, castle blocks and more), the world it comes " +
                                    "from, rarity, marks and price. With nothing typed, every house item shows. Click one to see its " +
                                    "details, and Give to my wizard puts it in your backpack. In the game, .furniture 1 <name or ID> does the same, " +
                                    "and .furniture find <words> lists matches. Click the star on a row to make it a favorite: favorites show first."),
                                WithCount(filters, _furnitureCount),
                            },
                        }), Dock.Top),
                        WithMargin(_furnitureList, new Thickness(0, 12, 0, 0)),
                    },
                },
                _furniturePopup,
            },
        };
        ShowFurniture(null);

        // The tab reads the game's house items the first time it is opened.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadFurnitureTabAsync);

        return root;
    }

    private Task LoadFurnitureTabAsync() {
        if (_furnitureBook is not null || _furnitureProblem is not null) {
            _ = Guard(LoadFurnitureSellersAsync);
            RenderFurnitureTab();

            return Task.CompletedTask;
        }

        return _furnitureLoading ??= LoadAsync();

        async Task LoadAsync() {
            _furnitureCount.Text = "Reading the house items from your game files…";
            try {
                if (_spellBook is null) {
                    await LoadSpellsTabAsync();
                }

                if (_spellBook is not { } book) {
                    throw new PanelException(_spellBookProblem ?? "Wizard101's game files could not be found, so the house items can't be shown.");
                }

                var furniture = await Task.Run(() => FurnitureBook.Load(book));
                _furnitureBook ??= furniture;
            }
            catch (Exception ex) {
                _furnitureProblem ??= ex is PanelException ? ex.Message : $"The house items could not be read from the game files: {ex.Message}";
            }
            finally {
                _furnitureLoading = null;
            }

            RenderFurnitureTab();
            await LoadFurnitureSellersAsync();
        }
    }

    /// <summary>Reads which vendors sell what (the Vendors tab's list, which the server writes), for "Sold by".</summary>
    private async Task LoadFurnitureSellersAsync() {
        await LoadVendorsAsync();
        if (_furnitureSellersFrom == _vendors) {
            return;
        }

        var sellers = new Dictionary<uint, List<string>>();
        foreach (var row in _vendors) {
            var place = row.Npc.Place.Length > 0 ? $"{row.Npc.Name} ({row.Npc.Place})" : row.Npc.Name;
            foreach (var id in row.Npc.Shops.SelectMany(s => s.Items ?? []).Distinct()) {
                if (!sellers.TryGetValue(id, out var names)) {
                    names = [];
                    sellers[id] = names;
                }

                if (!names.Contains(place)) {
                    names.Add(place);
                }
            }
        }

        (_furnitureSellers, _furnitureSellersFrom) = (sellers, _vendors);
        RenderFurnitureTab();
    }

    private void FillFurnitureFilters(FurnitureBook book) {
        if (_furnitureFiltersFilled) {
            return;
        }

        _furnitureFiltersFilled = true;
        FillFilterCombo(_furnitureFrom, book.Items.Select(i => i.From).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        FillFilterCombo(_furnitureRarity, book.Items.Select(i => i.Rarity).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        FillFilterCombo(_furnitureMark, book.Items.SelectMany(i => i.Flags).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void RenderFurnitureTab() {
        if (_furnitureBook is not { } book) {
            _furnitureCount.Text = _furnitureProblem ?? "Reading the house items from your game files…";
            _furnitureList.ItemsSource = null;

            return;
        }

        FillFurnitureFilters(book);
        var query = (_furnitureSearch.Text ?? "").Trim();
        var words = SearchText.Words(query);
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;
        var category = FilterPick(_furnitureCategory);
        var from = FilterPick(_furnitureFrom);
        var rarity = FilterPick(_furnitureRarity);
        var mark = FilterPick(_furnitureMark);
        var crowns = _furnitureCrowns.IsChecked == true;
        var crafted = _furnitureCrafted.IsChecked == true;
        var sold = _furnitureSold.IsChecked == true;
        var interactive = _furnitureInteractive.IsChecked == true;
        var sound = _furnitureSound.IsChecked == true;
        var animated = _furnitureAnimated.IsChecked == true;
        var limit = _furnitureLimit.IsChecked == true;
        var test = _furnitureTest.IsChecked == true;
        var favorites = Favorites(FAV_FURNITURE);
        var favoritesOnly = _furnitureFavoritesOnly.IsChecked == true;
        var sellers = _furnitureSellers;

        var found = book.Items.Where(i =>
            (id is not null ? i.Id == id || words.All(i.Haystack.Contains) : words.All(i.Haystack.Contains))
            // Unnamed and test copies show when asked for, or by their ID.
            && (test || !i.Test || i.Id == id)
            && (category is null || i.Category == category)
            && (from is null || i.From.Equals(from, StringComparison.OrdinalIgnoreCase))
            && (rarity is null || i.Rarity.Equals(rarity, StringComparison.OrdinalIgnoreCase))
            && (mark is null || i.Flags.Contains(mark, StringComparer.OrdinalIgnoreCase))
            && InFilterRange(i.GoldCost, _furnitureGoldMin, _furnitureGoldMax)
            && (!crowns || i.CrownsOnly)
            && (!crafted || i.Crafted)
            && (!sold || sellers.ContainsKey(i.Id))
            && (!interactive || i.Interactive)
            && (!sound || i.Sound)
            && (!animated || i.Animated)
            && (!limit || i.HouseLimit > 0)
            && (!favoritesOnly || favorites.Contains(FurnitureKey(i))));

        List<FurnitureItem> shown = _furnitureSort.SelectedIndex switch {
            1 => [.. found.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
            2 => [.. found.OrderBy(i => Array.IndexOf(FurnitureBook.CATEGORIES, i.Category)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            3 => [.. found.OrderBy(i => i.From, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            4 => [.. found.OrderBy(i => i.GoldCost).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            5 => [.. found.OrderByDescending(i => i.GoldCost).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            6 => [.. found.OrderBy(i => i.Id)],
            // The exact name (or ID) first, then names that start with what was typed.
            _ => [.. found.OrderBy(i => !(i.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || i.Id == id))
                .ThenBy(i => query.Length > 0 && !i.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
        };

        shown = [.. FavoritesFirst(shown, FAV_FURNITURE, FurnitureKey)];
        _furnitureList.ItemsSource = shown;
        _furnitureCount.Text = shown.Count == 0
            ? favoritesOnly ? "None of your favorites match. Star an item to make it a favorite, or clear \"Only my favorites\"."
            : "Nothing matches. Clear the search or pick \"Any\" in the filters."
            : $"{shown.Count:N0} of {book.Items.Count:N0} house items. Click one to see it.";
        if (_furniturePicked is not null && shown.Contains(_furniturePicked)) {
            _furnitureList.SelectedItem = _furniturePicked;
        }
    }

    private static string FurnitureKey(FurnitureItem item) => item.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>One house item in the list: its picture, its name, and its kind, world and ID underneath.</summary>
    private Control FurnitureRow(FurnitureItem item) {
        var line = $"{item.Category}  ·  {item.From}  ·  ID {item.Id}";
        if (item.GoldCost > 0) {
            line += $"  ·  {item.GoldCost:N0} gold";
        }

        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (item.CrownsOnly) {
            marks.Children.Add(GearMark("CrownsOnly", 18));
        }

        return new DockPanel {
            Margin = new Thickness(2, 3),
            Children = {
                WithDock(FavoriteStar(FAV_FURNITURE, FurnitureKey(item)), Dock.Left),
                WithDock(WithMargin(FurnitureIcon(item, 34), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithDock(marks, Dock.Right),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = line, FontSize = 12, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    /// <summary>
    /// A house item's kind as a picture. House items have no 2D picture in the game files (the game shows the 3D model
    /// itself), so each kind gets an icon of its own.
    /// </summary>
    private static Control FurnitureIcon(FurnitureItem item, double size) => ManagerTheme.Tile(item.Category switch {
        "Wall hangings" => "square",
        "Wallpapers and floors" => "layers",
        "Outdoor" => "globe",
        "Plants" or "Gardening" => "sparkle",
        "House guests" => "users",
        "Music scrolls" => "note",
        "Castle blocks" => "cube",
        "Castle Magic" => "bolt",
        "Trophies" => "crown",
        "Fishing" => "drop",
        "Raid keys" => "key",
        "House deeds" => "file",
        "Decorations" => "star",
        _ => "home",
    }, ManagerTheme.Accent("Furniture"), size);

    private string GiveFurnitureCommand(FurnitureItem item, int amount)
        => _manager.HasFurnitureCommand ? $".furniture {amount} {item.Id}" : $".mod additem {item.Id}";

    private void ShowFurniture(FurnitureItem? item) {
        _furniturePopup?.ShowFor(_furnitureList, item);

        _furniturePicked = item;
        _furnitureDetails.Children.Clear();
        if (item is null || _furnitureBook is null) {
            _furniturePicture.Child = null;
            _furnitureDetails.Children.Add(Sub(_furnitureProblem ?? "Click a house item to see it here, with its template ID."));

            return;
        }

        _furniturePicture.Child = FurnitureIcon(item, 72);
        _furnitureDetails.Children.Add(new TextBlock {
            Text = item.Name, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
        });
        var id = item.Id.ToString(CultureInfo.InvariantCulture);
        _furnitureDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });
        _furnitureDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(WithCenter(new TextBlock { Text = "How many", Foreground = MUTED })),
                Spaced(_furnitureAmount),
                Spaced(Action(Primary("Give to my wizard"), () => {
                    var command = GiveFurnitureCommand(item, (int) (_furnitureAmount.Value ?? 1));
                    _manager.SendCommand(command);
                    Say($"Sent {command} ({item.Name}). Look in your backpack's house items in the game.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(GiveFurnitureCommand(item, (int) (_furnitureAmount.Value ?? 1))))),
            },
        });
        _furnitureDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _furnitureDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 120, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("Kind", item.Category);
        Fact("World or set", item.From);
        Fact("Rarity", item.Rarity);
        Fact("Shop price", string.Join("  ·  ", new[] {
            item.GoldCost > 0 ? $"{item.GoldCost:N0} gold" : "", item.CrownsCost > 0 ? $"{item.CrownsCost:N0} Crowns" : "",
        }.Where(p => p.Length > 0)));
        Fact("Per house", item.HouseLimit > 0 ? $"At most {item.HouseLimit} in one house" : "");
        Fact("Does", string.Join(", ", new[] {
            item.Interactive ? "something when you press X" : "", item.Sound ? "makes sound or music" : "", item.Animated ? "moves" : "",
            item.Crafted ? "is crafted" : "",
        }.Where(p => p.Length > 0)));
        if (_furnitureSellers.TryGetValue(item.Id, out var sellers)) {
            Fact("Sold by", string.Join("\n", sellers.Take(12)) + (sellers.Count > 12 ? $"\n…and {sellers.Count - 12} more" : ""));
        }

        var marks = GearMarks.MARKS.Where(m => item.Flags.Contains(m.Flag)).Select(m => m.Name).ToList();
        Fact("Marks", marks.Count > 0 ? string.Join(", ", marks) : "None: it can be sold, traded and auctioned");
        Fact("Game's name", item.Code);
        Fact("File", item.File);
    }

}
