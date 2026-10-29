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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>One badge of the game, as the server's world data (SpiralDB's BadgeDefinitions) describes it.</summary>
internal sealed class BadgeRow {

    public uint Id { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string? ProgressInfo { get; set; }

    public string? RegistryName { get; set; }

    public int Max { get; set; }

    public uint Index { get; set; }

    public string? Image { get; set; }

    public string? Filter { get; set; }

    public bool ExtraCredit { get; set; }

    public bool Retired { get; set; }

    public bool HasReward { get; set; }

    public string? RewardOverrideDescription { get; set; }

    public string? RewardLootTable { get; set; }

    public bool AutoAdd { get; set; }

    internal string Name => DisplayName is { Length: > 0 } name ? name : $"Badge {Id}";

    /// <summary>Where the game's badge list files it, in plain words, such as Wizard City or Boss Rematch.</summary>
    internal string Group { get; set; } = "";

    /// <summary>The counter that badge progress adds to, as the server names it.</summary>
    internal string Counter => RegistryName is { Length: > 0 } registry ? registry
        : ProgressInfo is { Length: > 0 } progress ? progress
        : Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A badge earned by counting up, such as defeating 500 Elementals.</summary>
    internal bool Counts => Max > 1;

    internal string Haystack { get; set; } = "";

}

/// <summary>
/// The Badges tab (Niduus, 2026-10-28: "Implement more to the Database section"): every badge of the game from the
/// server's world data, searchable by name, what it is for and where the game's badge list files it, with buttons that
/// give it, add to its counter, or show it as the wizard's title.
/// </summary>
internal sealed partial class ManagerWindow {

    private DetailsPopup? _badgesPopup;

    private static readonly string[] s_badgeSorts = ["Best match", "Name", "Group", "The game's order", "Most to count first"];

    // Group names of the game's badge list that read better spelled out.
    private static readonly Dictionary<string, string> s_badgeGroups = new(StringComparer.OrdinalIgnoreCase) {
        ["MooShu"] = "Mooshu", ["PVPAge4"] = "PvP 4th Age", ["PVPAge5"] = "PvP 5th Age", ["4Dungeons"] = "Dungeons",
        ["FiveBOXES"] = "Five boxes", ["BossRematch"] = "Boss Rematch", ["MonsterMagic"] = "Monstrology",
        ["MythWeaving"] = "Myth spellweaving", ["StormWeaving"] = "Storm spellweaving", ["FireWeaving"] = "Fire spellweaving",
        ["IceWeaving"] = "Ice spellweaving", ["LifeWeaving"] = "Life spellweaving", ["DeathWeaving"] = "Death spellweaving",
        ["BalanceWeaving"] = "Balance spellweaving", ["LostPages"] = "Lost pages", ["SkeletonKey"] = "Skeleton keys",
        ["PetHatching"] = "Pet hatching", ["HousingGauntlet"] = "Housing gauntlets", ["SpecialEvents"] = "Special events",
    };

    private readonly TextBox _badgesSearch = new() { Watermark = "Search a badge by name, what it is for or ID, such as Elementalist or Defeat 500" };
    private readonly ComboBox _badgesGroup = FilterCombo([ANY_FILTER], 180);
    private readonly ComboBox _badgesSort = FilterCombo(s_badgeSorts, 170);
    private readonly CheckBox _badgesCounts = new() { Content = "Earned by counting up (defeat 500…)" };
    private readonly CheckBox _badgesReward = new() { Content = "Gives a reward" };
    private readonly CheckBox _badgesExtra = new() { Content = "Extra credit" };
    private readonly CheckBox _badgesRetired = new() { Content = "Retired badges" };
    private readonly CheckBox _badgesFavoritesOnly = FavoritesOnlyTick();
    private readonly NumericUpDown _badgesAmount = new() { Minimum = 1, Maximum = 100_000, Value = 1, Increment = 1, FormatString = "0", Width = 130 };
    private readonly TextBlock _badgesCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _badgesList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly Border _badgesPicture = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _badgesDetails = new() { Spacing = 4 };
    private readonly DispatcherTimer _badgesDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Dictionary<string, Bitmap?> _badgeImages = new(StringComparer.OrdinalIgnoreCase);

    private List<BadgeRow>? _badges;
    private string? _badgesProblem;
    private BadgeRow? _badgesPicked;

    private Control BadgesTab() {
        _badgesDelay.Tick += (_, _) => {
            _badgesDelay.Stop();
            RenderBadgesTab();
        };
        _badgesSearch.TextChanged += (_, _) => {
            _badgesDelay.Stop();
            _badgesDelay.Start();
        };
        WhenFiltersChange(RenderBadgesTab, _badgesGroup, _badgesSort, _badgesCounts, _badgesReward, _badgesExtra, _badgesRetired, _badgesFavoritesOnly);
        WhenFavoritesChange(FAV_BADGES, RenderBadgesTab);
        _badgesList.ItemTemplate = new FuncDataTemplate<BadgeRow>((badge, _) => badge is null ? new Panel() : BadgeListRow(badge));
        _badgesList.SelectionChanged += (_, e) => {
            if (e.Source == _badgesList && _badgesList.SelectedItem is BadgeRow badge && badge != _badgesPicked) {
                ShowBadge(badge);
            }
        };

        var filters = SearchFilters(
            SearchFilter("Group", _badgesGroup),
            SearchFilter("Sort by", _badgesSort),
            SearchTick(_badgesCounts),
            SearchTick(_badgesReward),
            SearchTick(_badgesExtra),
            SearchTick(_badgesRetired),
            SearchTick(_badgesFavoritesOnly));
        var details = new ScrollViewer {
            Width = 560,
            Content = new StackPanel { Spacing = 12, Children = { _badgesPicture, _badgesDetails } },
        };
        _badgesPopup = new DetailsPopup("Badge details", details, () => { _badgesList.SelectedItem = null; ShowBadge(null); },
            by => StepList(_badgesList, by));
        var root = new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(18, 10, 18, 14),
                    Children = {
                        WithDock(Card(new StackPanel {
                            Spacing = 10,
                            Children = {
                                WithHint(_badgesSearch, "Search by the badge's name, what it is for (such as Defeat 500 Elementals) or its ID, " +
                                    "or narrow the list with the filters: the group the game's badge list files it under (a world, Fishing, " +
                                    "Gardening, Raid and more), badges earned by counting up, badges with a reward, extra credit and retired " +
                                    "badges. With nothing typed, every badge shows. Click one to see it, and Give to my wizard gives it to the " +
                                    "wizard playing here (.badge grant). A counting badge can also get progress added. Click the star on a row " +
                                    "to make it a favorite: favorites show first."),
                                WithCount(filters, _badgesCount),
                            },
                        }), Dock.Top),
                        WithMargin(_badgesList, new Thickness(0, 10, 0, 0)),
                    },
                },
                _badgesPopup,
            },
        };
        ShowBadge(null);

        // The tab reads the badges the first time it is opened, and again when the server has downloaded them since.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadBadgesAsync);

        return root;
    }

    /// <summary>The folder the server keeps its world data in ([Database] SpiralDBLocalPath, next to the server).</summary>
    private string BadgeFolder() {
        var local = IniFile.Get(_manager.ConfigFile, "Database", "SpiralDBLocalPath") is { Length: > 0 } path ? path : "./spiraldb-cache";

        return Path.GetFullPath(Path.Combine(_manager.ServerFolder, local, "BadgeDefinitions"));
    }

    private async Task LoadBadgesAsync() {
        if (_badges is { Count: > 0 }) {
            RenderBadgesTab();

            return;
        }

        _badgesCount.Text = "Reading the badges…";
        var folder = BadgeFolder();
        try {
            _badges = await Task.Run(() => ReadBadges(folder));
            _badgesProblem = _badges.Count == 0
                ? "The badge list isn't here yet. Start the server once (it downloads the game's world data, badges included), then open this tab again."
                : null;
        }
        catch (Exception ex) {
            _badgesProblem = $"The badges could not be read from {folder}: {ex.Message}";
        }

        if (_badges is { Count: > 0 } badges) {
            FillFilterCombo(_badgesGroup, badges.Select(b => b.Group).OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
        }

        RenderBadgesTab();
    }

    private static List<BadgeRow> ReadBadges(string folder) {
        if (!Directory.Exists(folder)) {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var badges = new List<BadgeRow>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json")) {
            try {
                if (JsonSerializer.Deserialize<BadgeRow>(File.ReadAllText(file), options) is { Id: > 0 } badge) {
                    // The game's text marks, such as <image;Rank_Captain> before a PvP rank's name, are left out.
                    badge.DisplayName = Regex.Replace(badge.DisplayName ?? "", "<[^>]*>", "").Trim();
                    badge.Description = Regex.Replace(badge.Description ?? "", "<[^>]*>", "").Trim();
                    badge.Group = BadgeGroup(badge.Filter ?? "");
                    badge.Haystack = SearchText.Normalize($"{badge.Name} {badge.Description} {badge.Id} {badge.Group} {badge.Filter}");
                    badges.Add(badge);
                }
            }
            catch (Exception) {
                // A file the reader can't make sense of is left out, as the server leaves it out.
            }
        }

        return [.. badges.GroupBy(b => b.Id).Select(g => g.First()).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A group of the game's badge list in plain words: WizardCity is Wizard City, IceSpellements is Ice Spellements.</summary>
    private static string BadgeGroup(string filter) {
        if (filter.Length == 0) {
            return "Other";
        }

        return s_badgeGroups.TryGetValue(filter, out var shown) ? shown : Regex.Replace(filter, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private void RenderBadgesTab() {
        if (_badges is not { Count: > 0 } badges) {
            _badgesCount.Text = _badgesProblem ?? "Reading the badges…";
            _badgesList.ItemsSource = null;

            return;
        }

        var query = (_badgesSearch.Text ?? "").Trim();
        var words = SearchText.Words(query);
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;
        var group = FilterPick(_badgesGroup);
        var counts = _badgesCounts.IsChecked == true;
        var reward = _badgesReward.IsChecked == true;
        var extra = _badgesExtra.IsChecked == true;
        var retired = _badgesRetired.IsChecked == true;
        var favorites = Favorites(FAV_BADGES);
        var favoritesOnly = _badgesFavoritesOnly.IsChecked == true;

        var found = badges.Where(b =>
            (id is not null ? b.Id == id || words.All(b.Haystack.Contains) : words.All(b.Haystack.Contains))
            && (retired || !b.Retired || b.Id == id)
            && (group is null || b.Group.Equals(group, StringComparison.OrdinalIgnoreCase))
            && (!counts || b.Counts)
            && (!reward || b.HasReward)
            && (!extra || b.ExtraCredit)
            && (!favoritesOnly || favorites.Contains(BadgeKey(b))));

        List<BadgeRow> shown = _badgesSort.SelectedIndex switch {
            1 => [.. found.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],
            2 => [.. found.OrderBy(b => b.Group, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Index)],
            3 => [.. found.OrderBy(b => b.Index).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],
            4 => [.. found.OrderByDescending(b => b.Max).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],
            // The exact name (or ID) first, then names that start with what was typed.
            _ => [.. found.OrderBy(b => !(b.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || b.Id == id))
                .ThenBy(b => query.Length > 0 && !b.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)],
        };

        shown = [.. FavoritesFirst(shown, FAV_BADGES, BadgeKey)];
        _badgesList.ItemsSource = shown;
        _badgesCount.Text = shown.Count == 0
            ? favoritesOnly ? "None of your favorites match. Star a badge to make it a favorite, or clear \"Only my favorites\"."
            : "Nothing matches. Clear the search or pick \"Any\" in the filters."
            : $"{shown.Count:N0} of {badges.Count:N0} badges. Click one to see it.";
        if (_badgesPicked is not null && shown.Contains(_badgesPicked)) {
            _badgesList.SelectedItem = _badgesPicked;
        }
    }

    private static string BadgeKey(BadgeRow badge) => badge.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>One badge in the list: its picture, its name, and what it is for, its group and ID underneath.</summary>
    private Control BadgeListRow(BadgeRow badge) {
        var parts = new List<string>();
        if (badge.Description is { Length: > 0 } description) {
            parts.Add(description);
        }

        parts.Add(badge.Group);
        parts.Add($"ID {badge.Id}");
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (badge.HasReward) {
            marks.Children.Add(BadgeChip("Reward"));
        }

        if (badge.Retired) {
            marks.Children.Add(BadgeChip("Retired"));
        }

        return new DockPanel {
            Margin = new Thickness(2, 1),
            Children = {
                WithDock(FavoriteStar(FAV_BADGES, BadgeKey(badge)), Dock.Left),
                WithDock(WithMargin(BadgePicture(badge, 32), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithDock(marks, Dock.Right),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = {
                        new TextBlock { Text = badge.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = string.Join("  ·  ", parts), FontSize = 12, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    private static Control BadgeChip(string text) => new Border {
        Background = ManagerTheme.Brush(ManagerTheme.AccentColor("Badges"), 40), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL),
        Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = ManagerTheme.Accent("Badges") },
    };

    /// <summary>The game's picture of a badge (from its GUI art), or a crown while it is read (or when it has none).</summary>
    private Control BadgePicture(BadgeRow badge, double size) {
        var host = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        host.Child = ManagerTheme.Tile("crown", ManagerTheme.Accent("Badges"), size);
        if (badge.Image is not { Length: > 0 } file || _spellBook is not { } book) {
            return host;
        }

        if (_badgeImages.TryGetValue(file, out var cached)) {
            if (cached is not null) {
                host.Child = new Image { Source = cached, Stretch = Stretch.Uniform };
            }

            return host;
        }

        _ = Task.Run(() => book.Image(file)).ContinueWith(t => Dispatcher.UIThread.Post(() => {
            var bitmap = t.Status == TaskStatus.RanToCompletion && t.Result is { } image ? ToBitmap(image) : null;
            _badgeImages[file] = bitmap;
            if (bitmap is not null) {
                host.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
        }), TaskScheduler.Default);

        return host;
    }

    private void ShowBadge(BadgeRow? badge) {
        _badgesPopup?.ShowFor(_badgesList, badge);

        _badgesPicked = badge;
        _badgesDetails.Children.Clear();
        if (badge is null) {
            _badgesPicture.Child = null;
            _badgesDetails.Children.Add(Sub(_badgesProblem ?? "Click a badge to see it here."));

            return;
        }

        _badgesPicture.Child = BadgePicture(badge, 72);
        _badgesDetails.Children.Add(new TextBlock {
            Text = badge.Name, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
        });
        if (badge.Description is { Length: > 0 } description) {
            _badgesDetails.Children.Add(new TextBlock {
                Text = description, Foreground = MUTED, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            });
        }

        var id = BadgeKey(badge);
        _badgesDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Badge ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });

        var give = $".badge grant {badge.Id}";
        var title = $".mod badge {badge.Name}";
        _badgesDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Give to my wizard"), () => {
                    _manager.SendCommand(give);
                    Say($"Sent {give} ({badge.Name}). Open your badges in the game to see it.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Show as my title"), () => {
                    _manager.SendCommand(title);
                    Say($"Sent {title}. Your title under your name is now {badge.Name}.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(give))),
            },
        });
        if (badge.Counts) {
            _badgesAmount.Value = Math.Min(_badgesAmount.Value ?? 1, badge.Max);
            _badgesDetails.Children.Add(new WrapPanel {
                Children = {
                    Spaced(WithCenter(new TextBlock { Text = "Add progress", Foreground = MUTED })),
                    Spaced(_badgesAmount),
                    Spaced(Action(Plain("Add to my wizard's count"), () => {
                        var amount = (long) (_badgesAmount.Value ?? 1);
                        var command = $".badge progress {badge.Counter} {amount}";
                        _manager.SendCommand(command);
                        Say($"Sent {command}. The badge is given once the count reaches {badge.Max:N0}.");

                        return Task.CompletedTask;
                    })),
                },
            });
        }

        _badgesDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _badgesDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 120, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("Group", badge.Group);
        Fact("Earned by", badge.Counts ? $"Counting up to {badge.Max:N0}" : "Doing it once");
        Fact("Counter", badge.Counts ? badge.Counter : "");
        Fact("Reward", !badge.HasReward ? "None"
            : badge.RewardOverrideDescription is { Length: > 0 } words ? words
            : badge.RewardLootTable is { Length: > 0 } table ? $"Yes (drop table {table})"
            : "Yes");
        Fact("Extra credit", badge.ExtraCredit ? "Yes: it doesn't count toward finishing its group" : "");
        Fact("Retired", badge.Retired ? "Yes: it can no longer be earned in the game" : "");
        Fact("Given by itself", badge.AutoAdd ? "Yes: the game gives it once its needs are met" : "");
        Fact("Picture", badge.Image ?? "");
    }

}
