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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.CoreLib.Game.World;

namespace Imlight.Setup;

/// <summary>One place of the game in the Locations tab: its name, its code name and what kind of place it is.</summary>
internal sealed class LocationRow {

    internal required ZoneName Zone { get; init; }

    internal string Name => Zone.Name.Length > 0 ? Zone.Name : Zone.Tail;

    internal string Code => Zone.Code;

    internal string World { get; init; } = "";

    internal string Kind { get; init; } = "";

    internal string Access { get; init; } = "";

    internal string Haystack { get; init; } = "";

    /// <summary>Where `.tp` with this place's name goes (this place, or another one with the same name).</summary>
    internal ZoneName? ByName { get; set; }

    /// <summary>The other places with the same name.</summary>
    internal int SameName { get; set; }

}

/// <summary>
/// The Locations tab (Niduus, 2026-11-04: "Implement locations in the UI with display name and code name, with .tp able
/// to read either or"): every zone of the game with the name players know it by (Unicorn Way) and its code name
/// (WizardCity/WC_Streets/WC_Unicorn), searchable by either, with a button that teleports the wizard there. It also
/// says where typing `.tp` with the name goes, by the same matching the server uses (ZoneNameMatch).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string LOCATIONS = "Locations";

    private static readonly string[] s_locationSorts = ["Best match", "Name", "Code name", "World"];

    private DetailsPopup? _locationsPopup;

    private readonly TextBox _locationsSearch = new() { Watermark = "Search a place by its name or code name, such as Unicorn Way or WC_Unicorn" };
    private readonly ComboBox _locationsWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _locationsKind = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _locationsSort = FilterCombo(s_locationSorts, 150);
    private readonly CheckBox _locationsOld = new() { Content = "Old and test zones" };
    private readonly CheckBox _locationsFavoritesOnly = FavoritesOnlyTick();
    private readonly TextBlock _locationsCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _locationsList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly StackPanel _locationsDetails = new() { Spacing = 4 };
    private readonly DispatcherTimer _locationsDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private List<LocationRow>? _locations;
    private DateTime _locationsReadFrom = DateTime.MinValue;
    private string? _locationsProblem;
    private LocationRow? _locationsPicked;

    private Control LocationsTab() {
        _locationsDelay.Tick += (_, _) => {
            _locationsDelay.Stop();
            RenderLocationsTab();
        };
        _locationsSearch.TextChanged += (_, _) => {
            _locationsDelay.Stop();
            _locationsDelay.Start();
        };
        WhenFiltersChange(RenderLocationsTab, _locationsWorld, _locationsKind, _locationsSort, _locationsOld, _locationsFavoritesOnly);
        WhenFavoritesChange(FAV_LOCATIONS, RenderLocationsTab);
        _locationsList.ItemTemplate = new FuncDataTemplate<LocationRow>((row, _) => row is null ? new Panel() : LocationListRow(row));
        _locationsList.SelectionChanged += (_, e) => {
            if (e.Source == _locationsList && _locationsList.SelectedItem is LocationRow row && row != _locationsPicked) {
                ShowLocation(row);
            }
        };

        var filters = SearchFilters(
            SearchFilter("World", _locationsWorld),
            SearchFilter("Kind", _locationsKind),
            SearchFilter("Sort by", _locationsSort),
            SearchTick(_locationsOld),
            SearchTick(_locationsFavoritesOnly));
        var details = new ScrollViewer { Width = 560, Content = _locationsDetails };
        _locationsPopup = new DetailsPopup("Location", details, () => { _locationsList.SelectedItem = null; ShowLocation(null); },
            by => StepList(_locationsList, by));
        var root = new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(18, 10, 18, 14),
                    Children = {
                        WithDock(Card(new StackPanel {
                            Spacing = 10,
                            Children = {
                                WithHint(_locationsSearch, "Every place of the game, by the name players know it by (such as Unicorn Way) " +
                                    "and its code name (such as WizardCity/WC_Streets/WC_Unicorn). Search by either. Click a place to see " +
                                    "it, and Teleport my wizard here sends your wizard there. In the game's chat, .tp takes either name " +
                                    "too: .tp Unicorn Way, .tp WC_Unicorn or the whole code name all go to the same street, and small " +
                                    "typos are forgiven. Where many places share a name (houses and rooms are often named after their " +
                                    "street), .tp with the name goes to the main one; use the code name for the others."),
                                WithCount(filters, _locationsCount),
                            },
                        }), Dock.Top),
                        WithMargin(_locationsList, new Thickness(0, 10, 0, 0)),
                    },
                },
                _locationsPopup,
            },
        };
        ShowLocation(null);

        // Read the first time the tab opens, and again when the server has written a new zone list since.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadLocationsAsync);

        return root;
    }

    private (string Zones, string Details) LocationFiles()
        => (Path.Combine(_manager.IdLookupFolder, "zones.txt"), Path.Combine(_manager.ExtraFolder, "Zones", "zone-details.json"));

    private async Task LoadLocationsAsync() {
        var (zones, details) = LocationFiles();
        var changed = new[] { zones, details }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if (_locations is { Count: > 0 } && changed <= _locationsReadFrom) {
            RenderLocationsTab();

            return;
        }

        _locationsCount.Text = "Reading the places…";
        try {
            _locations = await Task.Run(() => ReadLocations(zones, details));
            _locationsReadFrom = changed;
            _locationsProblem = _locations.Count == 0
                ? "The place list isn't here yet. Start the server once (it writes its zone list), then open this tab again."
                : File.Exists(zones) ? null
                : "The server hasn't written its zone list yet, so this shows every place Wizard404 knows. Start the server once for the list it can teleport to.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _locationsProblem = $"The places could not be read: {ex.Message}";
        }

        if (_locations is { Count: > 0 } rows) {
            FillFilterCombo(_locationsWorld, rows.Select(r => r.World).Where(w => w.Length > 0).OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_locationsKind, rows.Select(r => r.Kind).Where(k => k.Length > 0).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        }

        RenderLocationsTab();
    }

    private static List<LocationRow> ReadLocations(string zonesFile, string detailsFile) {
        var details = Lookup.ReadZoneDetails(detailsFile);
        var codes = File.Exists(zonesFile)
            ? File.ReadLines(zonesFile).Select(z => z.Trim()).Where(z => z.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [.. details.Keys];
        var zones = codes.Select(code => details.TryGetValue(code, out var d)
            ? new ZoneName(code, d.Length > 0 ? d[0] : "", d.Length > 2 ? d[2] : "", d.Length > 3 ? d[3] : "")
            : new ZoneName(code, "")).ToList();

        var rows = zones.Select(zone => {
            var d = details.TryGetValue(zone.Code, out var found) ? found : null;
            var kind = zone.Code.StartsWith("Housing", StringComparison.OrdinalIgnoreCase) ? "House"
                : zone.Type switch { "S" => "World area", "D" => "Instance (dungeon)", "T" => "Starting area", _ => "" };
            var access = d is { Length: > 1 } ? d[1] switch { "F" => "Free", "P" => "Paid (members)", _ => "" } : "";
            var world = Lookup.ZoneWorld(zone.Code);

            return new LocationRow {
                Zone = zone, World = world, Kind = kind, Access = access,
                Haystack = SearchText.Normalize($"{zone.Name} {zone.Code} {SearchText.SplitCamel(zone.Tail)} {world}")
                    + $" {ZoneNameMatch.Key(zone.Name)} {ZoneNameMatch.Key(zone.Tail)}",
            };
        }).ToList();

        // Where .tp with each name goes, worked out once per name, as the server does it.
        foreach (var group in rows.Where(r => r.Zone.Name.Length > 0).GroupBy(r => ZoneNameMatch.Key(r.Zone.Name))) {
            var match = ZoneNameMatch.Find(zones, group.First().Zone.Name);
            foreach (var row in group) {
                row.ByName = match?.Zone;
                row.SameName = group.Count() - 1;
            }
        }

        return [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)];
    }

    private void RenderLocationsTab() {
        if (_locations is not { Count: > 0 } rows) {
            _locationsCount.Text = _locationsProblem ?? "Reading the places…";
            _locationsList.ItemsSource = null;

            return;
        }

        var query = (_locationsSearch.Text ?? "").Trim();
        var words = SearchText.Words(query);
        var key = ZoneNameMatch.Key(query);
        var world = FilterPick(_locationsWorld);
        var kind = FilterPick(_locationsKind);
        var old = _locationsOld.IsChecked == true;
        var favorites = Favorites(FAV_LOCATIONS);
        var favoritesOnly = _locationsFavoritesOnly.IsChecked == true;

        // What `.tp <search>` would pick, so it can lead the list and the count line can say so.
        var tp = query.Length > 0 ? ZoneNameMatch.Find([.. rows.Select(r => r.Zone)], query) : null;

        var found = rows.Where(r =>
            (words.All(r.Haystack.Contains) || (key.Length > 0 && r.Haystack.Contains(key, StringComparison.Ordinal)) || r.Zone == tp?.Zone)
            && (old || !r.Zone.OldOrTest || r.Zone == tp?.Zone)
            && (world is null || r.World.Equals(world, StringComparison.OrdinalIgnoreCase))
            && (kind is null || r.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            && (!favoritesOnly || favorites.Contains(r.Code)));

        List<LocationRow> shown = _locationsSort.SelectedIndex switch {
            1 => [.. found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)],
            2 => [.. found.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase)],
            3 => [.. found.OrderBy(r => r.World, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
            // Where .tp goes first, then exact names, then names that start with what was typed.
            _ => [.. found.OrderBy(r => r.Zone != tp?.Zone)
                .ThenBy(r => !(key.Length > 0 && (ZoneNameMatch.Key(r.Zone.Name) == key || ZoneNameMatch.Key(r.Zone.Tail) == key)))
                .ThenBy(r => key.Length > 0 && !ZoneNameMatch.Key(r.Name).StartsWith(key, StringComparison.Ordinal))
                .ThenBy(r => r.Zone.OldOrTest)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)],
        };

        shown = [.. FavoritesFirst(shown, FAV_LOCATIONS, r => r.Code)];
        _locationsList.ItemsSource = shown;
        var goes = tp is { } m ? $" Typing .tp {query} in the game goes to {(m.Zone.Name.Length > 0 ? m.Zone.Name + " (" + m.Zone.Code + ")" : m.Zone.Code)}." : "";
        _locationsCount.Text = shown.Count == 0
            ? favoritesOnly ? "None of your favorites match. Star a place to make it a favorite, or clear \"Only my favorites\"."
            : "Nothing matches. Clear the search or pick \"Any\" in the filters."
            : $"{shown.Count:N0} of {rows.Count:N0} places. Click one to see it.{goes}"
                + (_locationsProblem is { } note ? " " + note : "");
        if (_locationsPicked is not null && shown.Contains(_locationsPicked)) {
            _locationsList.SelectedItem = _locationsPicked;
        }
    }

    /// <summary>One place in the list: its name, and its code name, world and kind underneath.</summary>
    private Control LocationListRow(LocationRow row) {
        var parts = new[] { row.World, row.Kind, row.Access }.Where(p => p.Length > 0);
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (row.Zone.OldOrTest) {
            marks.Children.Add(LocationChip("Old or test"));
        }

        if (row.ByName == row.Zone && row.SameName > 0) {
            marks.Children.Add(LocationChip(".tp by name goes here"));
        }

        return new DockPanel {
            Margin = new Thickness(2, 1),
            Children = {
                WithDock(FavoriteStar(FAV_LOCATIONS, row.Code), Dock.Left),
                WithDock(WithMargin(ManagerTheme.Tile("map", ManagerTheme.Accent(LOCATIONS), 30), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithDock(marks, Dock.Right),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new TextBlock {
                            Inlines = [
                                new Avalonia.Controls.Documents.Run(row.Name) { FontWeight = FontWeight.SemiBold },
                                new Avalonia.Controls.Documents.Run("   " + row.Code) { FontFamily = MONO, FontSize = 12, Foreground = GOOD },
                            ],
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock { Text = string.Join("  ·  ", parts), FontSize = 12, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    private static Control LocationChip(string text) => new Border {
        Background = ManagerTheme.Brush(ManagerTheme.AccentColor(LOCATIONS), 40), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL),
        Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = ManagerTheme.Accent(LOCATIONS) },
    };

    private void ShowLocation(LocationRow? row) {
        _locationsPopup?.ShowFor(_locationsList, row);

        _locationsPicked = row;
        _locationsDetails.Children.Clear();
        if (row is null) {
            _locationsDetails.Children.Add(Sub(_locationsProblem ?? "Click a place to see it here."));

            return;
        }

        _locationsDetails.Children.Add(new TextBlock {
            Text = row.Name, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
        });

        void NameLine(string label, string value) => _locationsDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(value))), Dock.Right),
                WithDock(WithCenter(new TextBlock { Text = label, Width = 120, Foreground = MUTED }), Dock.Left),
                WithCenter(new SelectableTextBlock { Text = value, FontFamily = MONO, Foreground = GOOD, TextWrapping = TextWrapping.Wrap }),
            },
        });

        NameLine("Name", row.Zone.Name.Length > 0 ? row.Zone.Name : "(none known)");
        NameLine("Code name", row.Code);

        var command = $".tp {row.Code}";
        var byName = row.Zone.Name.Length > 0 && row.ByName == row.Zone ? $".tp {row.Zone.Name}" : null;
        _locationsDetails.Children.Add(new WrapPanel {
            Margin = new Thickness(0, 6, 0, 0),
            Children = {
                Spaced(Action(Primary("Teleport my wizard here"), () => {
                    _manager.SendCommand(command);
                    Say($"Sent {command} ({row.Name}). Your wizard goes there in a few seconds.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy .tp command"), () => CopyAsync(byName ?? command))),
            },
        });

        _locationsDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _locationsDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 120, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("World", row.World);
        Fact("Kind", row.Kind);
        Fact("Access", row.Access);
        Fact("Old or test", row.Zone.OldOrTest ? "Yes: an old (retired) zone or a test zone of the game's makers. It may be empty or broken." : "");
        Fact("In chat", byName is not null
            ? $"Type {byName} or {command}, or just .tp {row.Zone.Tail}."
            : row.ByName is { } other
                ? $"Type {command} or .tp {row.Zone.Tail}. (.tp {row.Zone.Name} goes to {other.Code}, the main place with that name.)"
                : $"Type {command} or .tp {row.Zone.Tail}.");
        if (row.SameName > 0 && _locations is { } all) {
            var key = ZoneNameMatch.Key(row.Zone.Name);
            var same = all.Where(r => r != row && ZoneNameMatch.Key(r.Zone.Name) == key).Take(8).Select(r => r.Code).ToList();
            Fact("Same name", $"{row.SameName} other place{(row.SameName == 1 ? "" : "s")} {(row.SameName == 1 ? "is" : "are")} also called {row.Zone.Name}: "
                + string.Join(", ", same) + (row.SameName > same.Count ? ", …" : ""));
        }
    }

}
