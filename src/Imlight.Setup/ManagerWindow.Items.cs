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
/// The Items tab (Niduus, 2026-10-28: "Implement more to the Database section"): every item the Gear and Furniture tabs
/// don't show, so reagents, jewels, pins, pet snacks, elixirs, henchmen, emotes, transformations and packs, searchable and
/// filtered by kind, school, level, rarity, marks and stats. Clicking one shows its details with a button that gives it
/// to the wizard playing here.
/// </summary>
internal sealed partial class ManagerWindow {

    private DetailsPopup? _itemsPopup;

    private static readonly string[] s_itemSorts = [
        "Best match", "Name", "Kind", "Level (low first)", "Level (high first)", "Gold cost (high first)", "Template ID",
    ];

    private readonly TextBox _itemsSearch = new() { Watermark = "Search an item by name or ID, such as Mist Wood, Health Opal or Dragon's Hoard Pack" };
    private readonly ComboBox _itemsCategory = FilterCombo([ANY_FILTER, .. ItemBook.CATEGORIES], 170);
    private readonly ComboBox _itemsType = FilterCombo([ANY_FILTER], 150);
    private readonly ComboBox _itemsSchool = FilterCombo([ANY_FILTER], 120);
    private readonly ComboBox _itemsRarity = FilterCombo([ANY_FILTER], 120);
    private readonly ComboBox _itemsMark = FilterCombo([ANY_FILTER], 160);
    private readonly NumericUpDown _itemsLevelMin = FilterNumber(10);
    private readonly NumericUpDown _itemsLevelMax = FilterNumber(10);
    private readonly ComboBox _itemsSort = FilterCombo(s_itemSorts, 170);
    private readonly CheckBox _itemsStats = new() { Content = "Gives stats or a card" };
    private readonly CheckBox _itemsCrowns = new() { Content = "Crowns only" };
    private readonly CheckBox _itemsSold = new() { Content = "Sold by a vendor" };
    private readonly CheckBox _itemsTest = new() { Content = "Unnamed and test items" };
    private readonly CheckBox _itemsFavoritesOnly = FavoritesOnlyTick();
    private readonly NumericUpDown _itemsAmount = new() { Minimum = 1, Maximum = 99, Value = 1, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _itemsCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _itemsList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly Border _itemsPicture = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _itemsDetails = new() { Spacing = 4 };
    private readonly DispatcherTimer _itemsDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private ItemBook? _itemBook;
    private string? _itemsProblem;
    private Task? _itemsLoading;
    private OtherItem? _itemsPicked;
    private bool _itemsFiltersFilled;

    private Control ItemsTab() {
        _itemsDelay.Tick += (_, _) => {
            _itemsDelay.Stop();
            RenderItemsTab();
        };
        _itemsSearch.TextChanged += (_, _) => {
            _itemsDelay.Stop();
            _itemsDelay.Start();
        };
        // The types of a kind follow the kind picked (a jewel's shape, a reagent's kind, a snack's kind).
        _itemsCategory.SelectionChanged += (_, e) => {
            if (e.Source == _itemsCategory && _itemBook is { } book) {
                FillItemTypes(book);
            }
        };
        WhenFiltersChange(RenderItemsTab, _itemsCategory, _itemsType, _itemsSchool, _itemsRarity, _itemsMark, _itemsLevelMin,
            _itemsLevelMax, _itemsSort, _itemsStats, _itemsCrowns, _itemsSold, _itemsTest, _itemsFavoritesOnly);
        WhenFavoritesChange(FAV_ITEMS, RenderItemsTab);
        _itemsList.ItemTemplate = new FuncDataTemplate<OtherItem>((item, _) => item is null ? new Panel() : ItemRow(item));
        _itemsList.SelectionChanged += (_, e) => {
            if (e.Source == _itemsList && _itemsList.SelectedItem is OtherItem item && item != _itemsPicked) {
                ShowItem(item);
            }
        };

        var filters = SearchFilters(
            SearchFilter("Kind", _itemsCategory),
            SearchFilter("Type", _itemsType),
            SearchFilter("School", _itemsSchool),
            SearchFilter("Rarity", _itemsRarity),
            SearchFilter("Mark", _itemsMark),
            SearchFilter("Level from", _itemsLevelMin),
            SearchFilter("Level to", _itemsLevelMax),
            SearchFilter("Sort by", _itemsSort),
            SearchTick(_itemsStats),
            SearchTick(_itemsCrowns),
            SearchTick(_itemsSold),
            SearchTick(_itemsTest),
            SearchTick(_itemsFavoritesOnly));
        var details = new ScrollViewer {
            Width = 560,
            Content = new StackPanel { Spacing = 12, Children = { _itemsPicture, _itemsDetails } },
        };
        _itemsPopup = new DetailsPopup("Item details", details, () => { _itemsList.SelectedItem = null; ShowItem(null); },
            by => StepList(_itemsList, by));
        var root = new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(18, 10, 18, 14),
                    Children = {
                        WithDock(Card(new StackPanel {
                            Spacing = 10,
                            Children = {
                                WithHint(_itemsSearch, "Search by name or template ID, or narrow the list with the filters: kind (reagents, jewels, " +
                                    "pins, pet snacks, elixirs and potions, transformations, henchmen, emotes, packs and reagent bundles), its type " +
                                    "(a jewel's shape, a reagent's kind, a snack's kind), school, rarity, marks and level. Gear is on the Gear tab " +
                                    "and house items on the Furniture tab. With nothing typed, every item shows. Click one to see what it does, " +
                                    "and Give to my wizard puts it in your backpack (reagents in your reagent bag, snacks in your snack bag). " +
                                    "Click the star on a row to make it a favorite: favorites show first."),
                                WithCount(filters, _itemsCount),
                            },
                        }), Dock.Top),
                        WithMargin(_itemsList, new Thickness(0, 10, 0, 0)),
                    },
                },
                _itemsPopup,
            },
        };
        ShowItem(null);

        // The tab reads the game's items the first time it is opened.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadItemsTabAsync);

        return root;
    }

    private Task LoadItemsTabAsync() {
        if (_itemBook is not null || _itemsProblem is not null) {
            _ = Guard(LoadFurnitureSellersAsync);
            RenderItemsTab();

            return Task.CompletedTask;
        }

        return _itemsLoading ??= LoadAsync();

        async Task LoadAsync() {
            _itemsCount.Text = "Reading the items from your game files…";
            try {
                if (_spellBook is null) {
                    await LoadSpellsTabAsync();
                }

                if (_spellBook is not { } book) {
                    throw new PanelException(_spellBookProblem ?? "Wizard101's game files could not be found, so the items can't be shown.");
                }

                var items = await Task.Run(() => ItemBook.Load(book));
                _itemBook ??= items;
            }
            catch (Exception ex) {
                _itemsProblem ??= ex is PanelException ? ex.Message : $"The items could not be read from the game files: {ex.Message}";
            }
            finally {
                _itemsLoading = null;
            }

            RenderItemsTab();
            // "Sold by" comes from the same list of vendors as the Furniture tab's.
            await LoadFurnitureSellersAsync();
            RenderItemsTab();
        }
    }

    private void FillItemsFilters(ItemBook book) {
        if (_itemsFiltersFilled) {
            return;
        }

        _itemsFiltersFilled = true;
        FillItemTypes(book);
        FillFilterCombo(_itemsSchool, book.Items.Select(i => i.School).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        FillFilterCombo(_itemsRarity, book.Items.Select(i => i.Rarity).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
        FillFilterCombo(_itemsMark, book.Items.SelectMany(i => i.Flags).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void FillItemTypes(ItemBook book) {
        var category = FilterPick(_itemsCategory);
        FillFilterCombo(_itemsType, book.Items.Where(i => category is null || i.Category == category).Select(i => i.Type)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
    }

    private void RenderItemsTab() {
        if (_itemBook is not { } book) {
            _itemsCount.Text = _itemsProblem ?? "Reading the items from your game files…";
            _itemsList.ItemsSource = null;

            return;
        }

        FillItemsFilters(book);
        var query = (_itemsSearch.Text ?? "").Trim();
        var words = SearchText.Words(query);
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;
        var category = FilterPick(_itemsCategory);
        var type = FilterPick(_itemsType);
        var school = FilterPick(_itemsSchool);
        var rarity = FilterPick(_itemsRarity);
        var mark = FilterPick(_itemsMark);
        var stats = _itemsStats.IsChecked == true;
        var crowns = _itemsCrowns.IsChecked == true;
        var sold = _itemsSold.IsChecked == true;
        var test = _itemsTest.IsChecked == true;
        var favorites = Favorites(FAV_ITEMS);
        var favoritesOnly = _itemsFavoritesOnly.IsChecked == true;
        var sellers = _furnitureSellers;

        var found = book.Items.Where(i =>
            (id is not null ? i.Id == id || words.All(i.Haystack.Contains) : words.All(i.Haystack.Contains))
            // Unnamed and test copies show when asked for, or by their ID.
            && (test || !i.Test || i.Id == id)
            && (category is null || i.Category == category)
            && (type is null || i.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            && (school is null || i.School.Equals(school, StringComparison.OrdinalIgnoreCase))
            && (rarity is null || i.Rarity.Equals(rarity, StringComparison.OrdinalIgnoreCase))
            && (mark is null || i.Flags.Contains(mark, StringComparer.OrdinalIgnoreCase))
            && InFilterRange(i.Level, _itemsLevelMin, _itemsLevelMax)
            && (!stats || i.Stats.Count > 0 || i.Spells.Count > 0)
            && (!crowns || i.CrownsOnly)
            && (!sold || sellers.ContainsKey(i.Id))
            && (!favoritesOnly || favorites.Contains(ItemKey(i))));

        List<OtherItem> shown = _itemsSort.SelectedIndex switch {
            1 => [.. found.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
            2 => [.. found.OrderBy(i => Array.IndexOf(ItemBook.CATEGORIES, i.Category)).ThenBy(i => i.Type).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            3 => [.. found.OrderBy(i => i.Level).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            4 => [.. found.OrderByDescending(i => i.Level).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            5 => [.. found.OrderByDescending(i => i.GoldCost).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            6 => [.. found.OrderBy(i => i.Id)],
            // The exact name (or ID) first, then names that start with what was typed.
            _ => [.. found.OrderBy(i => !(i.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || i.Id == id))
                .ThenBy(i => query.Length > 0 && !i.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
        };

        shown = [.. FavoritesFirst(shown, FAV_ITEMS, ItemKey)];
        _itemsList.ItemsSource = shown;
        _itemsCount.Text = shown.Count == 0
            ? favoritesOnly ? "None of your favorites match. Star an item to make it a favorite, or clear \"Only my favorites\"."
            : "Nothing matches. Clear the search or pick \"Any\" in the filters."
            : $"{shown.Count:N0} of {book.Items.Count:N0} items. Click one to see it.";
        if (_itemsPicked is not null && shown.Contains(_itemsPicked)) {
            _itemsList.SelectedItem = _itemsPicked;
        }
    }

    private static string ItemKey(OtherItem item) => item.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>One item in the list: its picture, its name, and its kind, type, level and ID underneath.</summary>
    private Control ItemRow(OtherItem item) {
        var parts = new List<string> { item.Type.Length > 0 && item.Type != "Other" ? $"{item.Category} ({item.Type})" : item.Category };
        if (item.School.Length > 0 && item.School != "All") {
            parts.Add(item.School);
        }

        if (item.Level > 0) {
            parts.Add($"Level {item.Level}+");
        }

        parts.Add($"ID {item.Id}");
        if (item.Stats.Count > 0) {
            parts.Add(string.Join(", ", item.Stats.Take(2).Select(ItemStatWords)));
        }

        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (item.CrownsOnly) {
            marks.Children.Add(GearMark("CrownsOnly", 18));
        }

        return new DockPanel {
            Margin = new Thickness(2, 1),
            Children = {
                WithDock(FavoriteStar(FAV_ITEMS, ItemKey(item)), Dock.Left),
                WithDock(WithMargin(ItemPicture(item, 32), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithDock(marks, Dock.Right),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = string.Join("  ·  ", parts), FontSize = 12, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    /// <summary>A stat line in plain words, such as "Max Health +6" or "Fire Damage +3%".</summary>
    private static string ItemStatWords(GearStat stat) {
        var what = stat.Words.Split(':')[0].Trim();
        var value = stat.Value.Replace(" MAX", "", StringComparison.Ordinal);

        return (stat.School.Length > 0 ? $"{stat.School} {what}" : what) + " " + value + (stat.After.Length > 0 ? $" {stat.After.ToLowerInvariant()}" : "");
    }

    /// <summary>The kind's icon, until the game's own picture of the item is found (most items have one).</summary>
    private static string ItemKindIcon(string category) => category switch {
        ItemBook.REAGENTS => "flask",
        ItemBook.JEWELS => "sparkle",
        ItemBook.PINS => "shield",
        ItemBook.SNACKS => "heart",
        ItemBook.ELIXIRS => "drop",
        ItemBook.TRANSFORMATIONS => "users",
        ItemBook.HENCHMEN => "user",
        ItemBook.EMOTES => "chat",
        ItemBook.PACKS or ItemBook.REAGENT_BUNDLES => "gift",
        _ => "bag",
    };

    /// <summary>The game's picture of an item, or its kind's icon while it is read (or when it has none).</summary>
    private Control ItemPicture(OtherItem item, double size) {
        var host = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        host.Child = ManagerTheme.Tile(ItemKindIcon(item.Category), ManagerTheme.Accent("Items"), size);
        if (_spellBook is not { } book) {
            return host;
        }

        var key = ItemKey(item);
        if (_itemIcons.TryGetValue(key, out var cached)) {
            if (cached is not null) {
                host.Child = new Image { Source = cached, Stretch = Stretch.Uniform };
            }

            return host;
        }

        _ = Task.Run(() => book.ItemIcon(item.File)).ContinueWith(t => Dispatcher.UIThread.Post(() => {
            var bitmap = t.Status == TaskStatus.RanToCompletion && t.Result is { } image ? ToBitmap(image) : null;
            _itemIcons[key] = bitmap;
            if (bitmap is not null) {
                host.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
        }), TaskScheduler.Default);

        return host;
    }

    private void ShowItem(OtherItem? item) {
        _itemsPopup?.ShowFor(_itemsList, item);

        _itemsPicked = item;
        _itemsDetails.Children.Clear();
        if (item is null || _itemBook is null) {
            _itemsPicture.Child = null;
            _itemsDetails.Children.Add(Sub(_itemsProblem ?? "Click an item to see it here, with its template ID."));

            return;
        }

        _itemsPicture.Child = ItemPicture(item, 72);
        _itemsDetails.Children.Add(new TextBlock {
            Text = item.Name, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
        });
        if (item.Description.Length > 0) {
            _itemsDetails.Children.Add(new TextBlock {
                Text = item.Description, Foreground = MUTED, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            });
        }

        var id = item.Id.ToString(CultureInfo.InvariantCulture);
        _itemsDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });
        var where = item.Category switch {
            ItemBook.REAGENTS => "your reagent bag",
            ItemBook.SNACKS => "your pet snack bag",
            _ => "your backpack",
        };
        _itemsDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(WithCenter(new TextBlock { Text = "How many", Foreground = MUTED })),
                Spaced(_itemsAmount),
                Spaced(Action(Primary("Give to my wizard"), () => {
                    var times = (int) (_itemsAmount.Value ?? 1);
                    _manager.SendCommand(item.Command, times);
                    Say(times == 1
                        ? $"Sent {item.Command} ({item.Name}). Look in {where} in the game."
                        : $"Sent {item.Command} {times} times ({times} {item.Name}). Look in {where} in the game.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(item.Command))),
            },
        });
        _itemsDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _itemsDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 120, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("Kind", item.Category);
        Fact("Type", item.Type == "Other" ? "" : item.Type);
        Fact("School", item.School);
        Fact("Level needed", item.Level > 0 ? $"{item.Level}" : "");
        Fact("Lasts", item.Lasts.Length > 0 ? GearBook.RentalWords(item.Lasts) : "");
        Fact("Gives", string.Join("\n", item.Stats.Select(ItemStatWords)));
        Fact("Cards", string.Join("\n", item.Spells.Select(s => {
            var card = _spellBook?.Spells.FirstOrDefault(c => c.Code.Equals(s.Code, StringComparison.OrdinalIgnoreCase));
            var name = card?.Name is { Length: > 0 } shown ? shown : s.Code;

            return s.Copies > 1 ? $"{s.Copies} × {name}" : name;
        })));
        Fact("Rarity", item.Rarity);
        Fact("Shop price", string.Join("  ·  ", new[] {
            item.GoldCost > 0 ? $"{item.GoldCost:N0} gold" : "", item.CrownsCost > 0 ? $"{item.CrownsCost:N0} Crowns" : "",
        }.Where(p => p.Length > 0)));
        if (_furnitureSellers.TryGetValue(item.Id, out var sellers)) {
            Fact("Sold by", string.Join("\n", sellers.Take(12)) + (sellers.Count > 12 ? $"\n…and {sellers.Count - 12} more" : ""));
        }

        var marks = GearMarks.MARKS.Where(m => item.Flags.Contains(m.Flag)).Select(m => m.Name).ToList();
        Fact("Marks", marks.Count > 0 ? string.Join(", ", marks) : "None: it can be sold, traded and auctioned");
        Fact("Command", item.Command);
        Fact("Game's name", item.Code);
        Fact("File", item.File);
    }

}
