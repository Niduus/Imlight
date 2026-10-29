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
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Imlight.Setup;

/// <summary>
/// Favorites in the Database lists (Niduus, 2026-10-28: "allow for people to favorite stuff in the database in that
/// respected category so that way, people can quickly get the desired results they want FIRST before the rest of the
/// list"): a star on each row, kept per list, and the starred ones that match the search and filters come first. They
/// are kept in a file next to the app on this PC, which updates never touch.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string FAVORITES_FILE = "favorites.json";

    // The lists that keep favorites of their own. Lookup's Spell list shares the Spells tab's, as both are spell cards.
    private const string FAV_LOOKUP = "Lookup";
    private const string FAV_SPELLS = "Spells";
    private const string FAV_GEAR = "Gear";
    private const string FAV_BOSSES = "Bosses";
    private const string FAV_RECIPES = "Recipes";
    private const string FAV_FURNITURE = "Furniture";
    private const string FAV_VENDORS = "Vendors";
    private const string FAV_PET_TALENTS = "Pet Talents";
    private const string FAV_PET_DERBY = "Pet Derby";
    private const string FAV_PHRASES = "Phrases";
    private const string FAV_ITEMS = "Items";
    private const string FAV_BADGES = "Badges";

    private static readonly IBrush FAVORITE = new SolidColorBrush(Color.FromRgb(242, 194, 48));
    private static readonly JsonSerializerOptions s_favoritesWrite = new() { WriteIndented = true };

    private Dictionary<string, HashSet<string>>? _favorites;
    private readonly Dictionary<string, List<Action>> _favoritesChanged = [];

    private string FavoritesPath => Path.Combine(_manager.InstallFolder, FAVORITES_FILE);

    /// <summary>The favorites of one list, read from the file the first time.</summary>
    private HashSet<string> Favorites(string list) {
        if (_favorites is null) {
            _favorites = [];
            try {
                if (File.Exists(FavoritesPath)
                    && JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FavoritesPath)) is { } read) {
                    foreach (var (name, keys) in read) {
                        _favorites[name] = new HashSet<string>(keys, StringComparer.Ordinal);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                // A file that can't be read leaves the lists without favorites; starring one writes a new file.
            }
        }

        if (!_favorites.TryGetValue(list, out var set)) {
            set = new HashSet<string>(StringComparer.Ordinal);
            _favorites[list] = set;
        }

        return set;
    }

    private bool IsFavorite(string list, string key) => Favorites(list).Contains(key);

    /// <summary>What a spell card's favorite is saved as: its template ID.</summary>
    private static string SpellKey(SpellCard spell) => spell.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Shows a list again whenever one of its favorites is starred or unstarred.</summary>
    private void WhenFavoritesChange(string list, Action render) {
        if (!_favoritesChanged.TryGetValue(list, out var renders)) {
            renders = [];
            _favoritesChanged[list] = renders;
        }

        renders.Add(render);
    }

    /// <summary>The rows with the favorites first, each part in the order it had.</summary>
    private IEnumerable<T> FavoritesFirst<T>(IEnumerable<T> rows, string list, Func<T, string> key) {
        var favorites = Favorites(list);

        return favorites.Count == 0 ? rows : rows.OrderBy(r => !favorites.Contains(key(r)));
    }

    /// <summary>The "Only my favorites" tick for a list's filters.</summary>
    private static CheckBox FavoritesOnlyTick() => new() { Content = "Only my favorites" };

    /// <summary>
    /// The star on a row: filled when it is a favorite. Clicking it stars or unstars the row, saves, and shows the list
    /// again with the row in its new place.
    /// </summary>
    private Button FavoriteStar(string list, string key, double size = 18) {
        var on = IsFavorite(list, key);
        var star = new Button {
            Classes = { "clear" }, Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = ManagerTheme.Icon(on ? "star" : "starline", size, on ? FAVORITE : MUTED),
        };
        ToolTip.SetTip(star, on ? "A favorite: it shows first. Click to take it off your favorites." : "Click to make it a favorite, so it shows first.");
        star.Click += (_, e) => {
            e.Handled = true;
            // The list it sits in, and its row, so the row can be scrolled to after the list is shown again.
            var listBox = star.FindAncestorOfType<ListBox>();
            var row = star.FindAncestorOfType<ListBoxItem>()?.DataContext;
            ToggleFavorite(list, key);
            if (listBox is not null && row is not null) {
                Dispatcher.UIThread.Post(() => {
                    if (listBox.ItemsSource is System.Collections.IList items && items.Contains(row)) {
                        listBox.ScrollIntoView(row);
                    }
                }, DispatcherPriority.Background);
            }
        };

        return star;
    }

    private void ToggleFavorite(string list, string key) {
        var favorites = Favorites(list);
        var starred = favorites.Add(key);
        if (!starred) {
            favorites.Remove(key);
        }

        SaveFavorites();
        Say(starred ? $"Added to your {list} favorites. It shows first now." : $"Taken off your {list} favorites.");
        foreach (var render in _favoritesChanged.GetValueOrDefault(list) ?? []) {
            render();
        }
    }

    private void SaveFavorites() {
        if (_favorites is null) {
            return;
        }

        var saved = _favorites.Where(f => f.Value.Count > 0).OrderBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(f => f.Key, f => f.Value.Order(StringComparer.Ordinal).ToList());
        try {
            // Written beside the file first, so a crash halfway never leaves half a file.
            var temp = FavoritesPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, s_favoritesWrite));
            File.Move(temp, FavoritesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"Your favorites could not be saved: {ex.Message}");
        }
    }
}
