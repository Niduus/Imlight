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
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Recipes tab (Niduus, 2026-10-24: "make a recipe vendor and recipe browser in manager"): every recipe of the
/// game, searchable and filtered by crafting station and school. Clicking one shows what it needs, what it makes, where
/// it is crafted and its cooldown, with buttons that teach it to the wizard playing here, and that put it (or every
/// recipe shown) in a recipe vendor of the Customs tab.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ALL_STATIONS = "All stations";
    private const string ANY_RECIPE_SCHOOL = "Any school";

    /// <summary>The most recipes one vendor gets from Make a recipe vendor, as the server's presets split them.</summary>
    private const int RECIPE_VENDOR_SIZE = 100;

    private readonly TextBox _recipeSearch = new() { Watermark = "Search a recipe, what it makes or needs, such as Elixir, Amber or 12345" };
    private readonly ComboBox _recipeStation = new() { MinWidth = 220 };
    private readonly ComboBox _recipeSchool = new() { MinWidth = 140 };
    private readonly CheckBox _recipeNoStation = new() { Content = "Recipes of no crafting station" };
    private readonly TextBlock _recipeCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _recipeList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _recipeDetails = new() { Spacing = 6 };
    private readonly DispatcherTimer _recipeDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private RecipeBook? _recipeBook;
    private string? _recipeProblem;
    private Task? _recipeLoading;
    private Recipe? _recipePicked;
    private List<Recipe> _recipesShown = [];
    private bool _recipeFiltersFilled;

    private Control RecipesTab() {
        _recipeDelay.Tick += (_, _) => {
            _recipeDelay.Stop();
            RenderRecipesTab();
        };
        _recipeSearch.TextChanged += (_, _) => {
            _recipeDelay.Stop();
            _recipeDelay.Start();
        };
        foreach (var box in new[] { _recipeStation, _recipeSchool }) {
            box.SelectionChanged += (_, e) => {
                if (e.Source == box) {
                    RenderRecipesTab();
                }
            };
        }

        _recipeNoStation.IsCheckedChanged += (_, _) => RenderRecipesTab();
        _recipeList.ItemTemplate = new FuncDataTemplate<Recipe>((recipe, _) => recipe is null ? new Panel() : RecipeRow(recipe));
        _recipeList.SelectionChanged += (_, e) => {
            if (e.Source == _recipeList && _recipeList.SelectedItem is Recipe recipe && recipe != _recipePicked) {
                ShowRecipe(recipe);
            }
        };

        Control Labelled(string label, Control box) => new StackPanel {
            Spacing = 2, Margin = new Thickness(0, 0, 10, 6),
            Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12 }, box },
        };

        var filters = new WrapPanel {
            Children = {
                Labelled("Crafting station", _recipeStation),
                Labelled("School", _recipeSchool),
                WithMargin(_recipeNoStation, new Thickness(4, 18, 0, 6)),
            },
        };
        var vendor = WithHint(Row(
                Action(Primary("Make a recipe vendor here"), MakeRecipeVendorAsync),
                Action(Plain("Open my crafting window"), () => {
                    _manager.SendCommand(".craft");
                    Say("Sent .craft. The crafting window opens in the game with every recipe your wizard knows.");

                    return Task.CompletedTask;
                })),
            $"Stand where the vendor should go in the game, then press Make a recipe vendor here: it sells the recipes the list " +
            $"shows (the first {RECIPE_VENDOR_SIZE}), each for its usual gold. It is a vendor of the Customs tab, so you can rename it, " +
            "move it, change prices or delete it there. Players press X near it and buy a recipe to learn it.");
        var details = new ScrollViewer {
            Width = 430,
            Margin = new Thickness(14, 0, 0, 0),
            Content = Card(_recipeDetails),
        };
        var root = new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("flask", "Recipes", "Find recipes",
                    "Search or filter, then click a recipe to see what it needs and makes.",
                    MissingNote("the .recipe and .craft commands are " + ServerSource.DefaultName + "'s.", () => !_manager.HasRecipeCommands),
                    WithHint(_recipeSearch, "Search by the recipe's name, what it makes, an ingredient, or its template ID. Click a recipe to see " +
                        "its ingredients, the station it is crafted at and its cooldown. Learn on my wizard teaches it to the wizard you play here."),
                    filters, vendor, _recipeCount), Dock.Top),
                WithMargin(_recipeList, new Thickness(0, 14, 0, 0)),
            },
        };
        ShowRecipe(null);

        // The tab reads the game's recipes the first time it is opened.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadRecipesTabAsync);

        return root;
    }

    private Task LoadRecipesTabAsync() {
        if (_recipeBook is not null || _recipeProblem is not null) {
            RenderRecipesTab();

            return Task.CompletedTask;
        }

        return _recipeLoading ??= LoadAsync();

        async Task LoadAsync() {
            _recipeCount.Text = "Reading the recipes from your game files…";
            try {
                // The spells come first: treasure card recipes are named from them.
                if (_spellBook is null) {
                    await LoadSpellsTabAsync();
                }

                if (_spellBook is not { } book) {
                    throw new PanelException(_spellBookProblem ?? "Wizard101's game files could not be found, so the recipes can't be shown.");
                }

                var recipes = await Task.Run(() => RecipeBook.Load(book));
                _recipeBook ??= recipes;
            }
            catch (Exception ex) {
                _recipeProblem ??= ex is PanelException ? ex.Message : $"The recipes could not be read from the game files: {ex.Message}";
            }
            finally {
                _recipeLoading = null;
            }

            RenderRecipesTab();
        }
    }

    private void FillRecipeFilters(RecipeBook book) {
        if (_recipeFiltersFilled) {
            return;
        }

        _recipeFiltersFilled = true;
        var stations = RecipeBook.STATIONS.Values.Where(s => book.Recipes.Any(r => r.Stations.Contains(s))).ToList();
        _recipeStation.ItemsSource = new[] { ALL_STATIONS }.Concat(stations).ToList();
        _recipeStation.SelectedIndex = 0;
        string[] mainSchools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];
        var schools = book.Recipes.Select(r => r.School).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => Array.IndexOf(mainSchools, s) is var i and >= 0 ? i : 100).ThenBy(s => s, StringComparer.OrdinalIgnoreCase);
        _recipeSchool.ItemsSource = new[] { ANY_RECIPE_SCHOOL }.Concat(schools).ToList();
        _recipeSchool.SelectedIndex = 0;
    }

    private void RenderRecipesTab() {
        if (_recipeBook is not { } book) {
            _recipeCount.Text = _recipeProblem ?? "Reading the recipes from your game files…";
            _recipeList.ItemsSource = null;

            return;
        }

        FillRecipeFilters(book);
        var query = (_recipeSearch.Text ?? "").Trim();
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;
        var station = _recipeStation.SelectedItem as string ?? ALL_STATIONS;
        var school = _recipeSchool.SelectedItem as string ?? ANY_RECIPE_SCHOOL;
        var noStation = _recipeNoStation.IsChecked == true;

        _recipesShown = [.. book.Recipes.Where(r =>
                (r.Id == id || words.All(r.Haystack.Contains))
                // The game's other recipes (spellements, idols, jewel trades) are no station's; they show when asked for.
                && (noStation || r.Stations.Count > 0 || r.Id == id)
                && (station == ALL_STATIONS || r.Stations.Contains(station))
                && (school == ANY_RECIPE_SCHOOL || r.School.Equals(school, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => !(r.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || r.Id == id))
            .ThenBy(r => query.Length > 0 && !r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id)];

        _recipeList.ItemsSource = _recipesShown;
        _recipeCount.Text = _recipesShown.Count == 0
            ? "Nothing matches. Clear the search or pick \"All\" in the filters."
            : $"{_recipesShown.Count:N0} of {book.Recipes.Count:N0} recipes. Click one to see it on the right.";
        if (_recipePicked is not null && _recipesShown.Contains(_recipePicked)) {
            _recipeList.SelectedItem = _recipePicked;
        }
        else if (_recipesShown.Count > 0 && query.Length > 0) {
            _recipeList.SelectedItem = _recipesShown[0];
        }
    }

    /// <summary>One recipe in the list: its picture, its name, and its station, cooldown and ID underneath.</summary>
    private Control RecipeRow(Recipe recipe) {
        var line = $"{recipe.Station}  ·  {RecipeBook.Time(recipe.Cooldown)}  ·  ID {recipe.Id}";

        return new DockPanel {
            Margin = new Thickness(2, 3),
            Children = {
                WithDock(WithMargin(RecipeIcon(recipe.MakesId, recipe.MakesFile, recipe.MakesSpell, 30), new Thickness(0, 0, 10, 0)), Dock.Left),
                new StackPanel {
                    Children = {
                        new TextBlock { Text = recipe.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = line, FontSize = 12, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    /// <summary>The game's picture of what a recipe makes or needs: an item's icon or a card, or a flask until it is found.</summary>
    private Control RecipeIcon(uint id, string file, string spell, double size) {
        if (spell.Length > 0 && _recipeBook?.Spell(spell) is { } card) {
            return new Border { Width = size, Height = size, Child = SpellThumb(card.Id, card.School, size * 0.72) };
        }

        var host = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        host.Child = ManagerTheme.Tile("flask", ManagerTheme.Accent("Recipes"), size);
        if (id == 0 || file.Length == 0 || _spellBook is not { } book) {
            return host;
        }

        var key = id.ToString(CultureInfo.InvariantCulture);
        if (_itemIcons.TryGetValue(key, out var cached)) {
            if (cached is not null) {
                host.Child = new Image { Source = cached, Stretch = Stretch.Uniform };
            }

            return host;
        }

        _ = Task.Run(() => book.ItemIcon(file)).ContinueWith(t => Dispatcher.UIThread.Post(() => {
            var bitmap = t.Status == TaskStatus.RanToCompletion && t.Result is { } image ? ToBitmap(image) : null;
            _itemIcons[key] = bitmap;
            if (bitmap is not null) {
                host.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
        }), TaskScheduler.Default);

        return host;
    }

    // ---------------------------------------------------------------------
    // One recipe
    // ---------------------------------------------------------------------

    private void ShowRecipe(Recipe? recipe) {
        _recipePicked = recipe;
        _recipeDetails.Children.Clear();
        if (recipe is null || _recipeBook is null) {
            _recipeDetails.Children.Add(Sub(_recipeProblem ?? "Click a recipe to see what it needs, what it makes and where it is crafted."));

            return;
        }

        _recipeDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithMargin(RecipeIcon(recipe.MakesId, recipe.MakesFile, recipe.MakesSpell, 48), new Thickness(0, 0, 12, 0)), Dock.Left),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new SelectableTextBlock { Text = recipe.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        Sub(recipe.Makes.Length > 0 ? $"Makes {recipe.Makes}" : "Makes nothing the app can name"),
                    },
                },
            },
        });

        var id = recipe.Id.ToString(CultureInfo.InvariantCulture);
        _recipeDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });
        _recipeDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Learn on my wizard"), () => {
                    _manager.SendCommand(recipe.LearnCommand);
                    Say($"Sent {recipe.LearnCommand} ({recipe.Name}). It is in your spellbook's Recipes page in the game now; type .craft to craft it anywhere.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(recipe.LearnCommand))),
            },
        });
        _recipeDetails.Children.Add(AddToVendorRow(recipe));
        _recipeDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        _recipeDetails.Children.Add(new TextBlock { Text = "Needs", FontWeight = FontWeight.SemiBold });
        if (recipe.Ingredients.Count == 0) {
            _recipeDetails.Children.Add(Sub("Nothing."));
        }

        foreach (var ingredient in recipe.Ingredients) {
            _recipeDetails.Children.Add(new DockPanel {
                Children = {
                    WithDock(WithMargin(RecipeIcon(ingredient.Id, ingredient.File, ingredient.Spell, 28), new Thickness(0, 0, 10, 0)), Dock.Left),
                    WithDock(WithCenter(new TextBlock { Text = $"x{ingredient.Quantity}", FontWeight = FontWeight.SemiBold }), Dock.Right),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = {
                            new SelectableTextBlock { Text = ingredient.Name, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = ingredient.Id > 0 ? $"{ingredient.Kind}  ·  ID {ingredient.Id}" : ingredient.Kind, FontSize = 12, Foreground = MUTED },
                        },
                    },
                },
            });
        }

        _recipeDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 6, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _recipeDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 110, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("Crafted at", recipe.Station);
        Fact("Cooldown", RecipeBook.Time(recipe.Cooldown) + (recipe.Cooldown > 0 ? " (the server's [Crafting] CooldownMultiplier changes it)" : ""));
        Fact("Makes", recipe.MakesId > 0 ? $"{recipe.Makes} (ID {recipe.MakesId})" : recipe.Makes);
        Fact("School", recipe.School);
        Fact("Category", recipe.Category);
        Fact("Shop price", string.Join("  ·  ", new[] {
            recipe.GoldCost > 0 ? $"{recipe.GoldCost:N0} gold" : "", recipe.CrownsCost > 0 ? $"{recipe.CrownsCost:N0} Crowns" : "",
        }.Where(p => p.Length > 0)) is { Length: > 0 } price ? price : "Free");
        Fact("Game's name", recipe.Code);
        Fact("File", recipe.File);
    }

    // The custom vendors (Customs tab) a recipe can be added to.
    private Control AddToVendorRow(Recipe recipe) {
        var vendors = SafeNpcs().Where(n => !n.IsTrainer).ToList();
        if (vendors.Count == 0) {
            return Sub("No vendors of your own yet. Make a recipe vendor here (on the left) makes one.");
        }

        var box = new ComboBox { Width = 250, ItemsSource = vendors.Select(v => $"{v.Id}. {v.Shown}").ToList() };
        box.SelectedIndex = Math.Max(0, vendors.FindIndex(v => string.Equals(v.Window, "Recipe", StringComparison.OrdinalIgnoreCase)));

        return Row(box, Action(Plain("Add to this vendor"), () => {
            if (box.SelectedIndex < 0 || box.SelectedIndex >= vendors.Count) {
                throw new PanelException("Pick a vendor first.");
            }

            // Read again: the Customs tab may have changed it since this recipe was opened.
            var vendor = SafeNpcs().FirstOrDefault(n => n.Id == vendors[box.SelectedIndex].Id)
                         ?? throw new PanelException("That vendor is gone. Click the recipe again.");
            if (vendor.Items.Any(e => e.ID == recipe.Id)) {
                Say($"{vendor.Shown} sells {recipe.Name} already.");

                return Task.CompletedTask;
            }

            vendor.Items.Add(new CustomNpcEntry { ID = recipe.Id, Price = recipe.GoldCost });
            var window = vendor.Window;
            vendor.Window = "Recipe";
            CustomNpcs.Set(_manager.CustomsFolder, vendor.Id, vendor);
            if (_npcEdit?.Id == vendor.Id) {
                OpenNpc(vendor);
            }

            Say($"{vendor.Shown} now sells {recipe.Name} for {recipe.GoldCost:N0} gold" +
                (string.Equals(window, "Recipe", StringComparison.OrdinalIgnoreCase) ? "." : ", and opens the recipe shop window now.") +
                " A player standing at it walks away and back to see it.");

            return Task.CompletedTask;
        }));
    }

    private async Task MakeRecipeVendorAsync() {
        if (_recipeBook is null) {
            throw new PanelException("The recipes are still being read. Wait a moment and press again.");
        }

        if (_recipesShown.Count == 0) {
            throw new PanelException("The list is empty. Search or pick a station first; the vendor sells the recipes the list shows.");
        }

        var recipes = _recipesShown.Take(RECIPE_VENDOR_SIZE).ToList();
        var (who, zone, x, y, z, yaw) = MySpot();
        var station = _recipeStation.SelectedItem as string ?? ALL_STATIONS;
        var query = (_recipeSearch.Text ?? "").Trim();
        var name = station != ALL_STATIONS ? station.Replace(" Crafting", "") + " Recipes"
            : query.Length > 0 ? $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(query)} Recipes" : "Recipe Vendor";
        var npc = new CustomNpc {
            Id = CustomNpcs.NextId(SafeNpcs()), Kind = CustomNpc.VENDOR, Name = name, Window = "Recipe",
            Zone = zone, X = x, Y = y, Z = z, Yaw = yaw,
            Items = [.. recipes.Select(r => new CustomNpcEntry { ID = r.Id, Price = r.GoldCost })],
        };
        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, npc);
        if (_recipePicked is { } picked) {
            ShowRecipe(picked);
        }

        Say($"Made {npc.Shown} (vendor {npc.Id}) where {who} stands in {ZoneDisplay(zone)}, selling {Plural(recipes.Count, "recipe")}" +
            (_recipesShown.Count > recipes.Count ? $" (the first {RECIPE_VENDOR_SIZE} of the {_recipesShown.Count:N0} shown; search or filter to make more)" : "") +
            ". It is in the game now; rename it or change prices on the Customs tab.");
        await Task.CompletedTask;
    }

}
