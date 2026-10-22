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

/// <summary>
/// The Gear tab (Niduus, 2026-10-22: "Make a Gear category in the manager and have it portray gear as it would in game.
/// Speed and icon, marks such as no sell, no auction, no trade, crowns etc."): every hat, robe, boots, wand, athame,
/// amulet, ring, deck, mount and pet, searchable and filtered by slot, school, level and Crowns. Clicking one shows the
/// game's item tooltip as the game draws it: the name in cyan, each stat in yellow with the game's own stat icon, the
/// item cards, sockets, the level needed in red and the marks at the bottom, each explained when pointed at. Its
/// cards are painted below, as on the Spells tab, with its ID and a button that gives it to the wizard playing here.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ALL_GEAR = "All gear";
    private const string NO_SCHOOL = "No school needed";
    private const double GEAR_TIP_WIDTH = 290;
    private const int GEAR_TEXT_CACHE = 1500;

    // The tooltip's colours, as the game's item tooltip has them.
    private const uint TIP_CYAN = 0xFF44E4F2;
    private const uint TIP_YELLOW = 0xFFFFE834;
    private const uint TIP_RED = 0xFFFF3A2E;
    private const uint TIP_ORANGE = 0xFFFFA126;
    private const uint TIP_WHITE = 0xFFFFFFFF;

    private static readonly string[] s_gearSorts = ["Name", "Level (high first)", "Level (low first)", "Template ID"];

    // The game's names for a pet's ages, 1 to 7, when its text files don't say.
    private static readonly string[] s_petAges = ["Baby", "Teen", "Adult", "Ancient", "Epic", "Mega", "Ultra"];

    private readonly TextBox _gearSearch = new() { Watermark = "Search by name or ID, such as Whalebird or 1770010" };
    private readonly ComboBox _gearSlot = new() { ItemsSource = new[] { ALL_GEAR }.Concat(GearBook.SLOTS).ToList(), SelectedIndex = 0, MinWidth = 130 };
    private readonly ComboBox _gearSchool = new() { MinWidth = 150 };
    private readonly NumericUpDown _gearLevelMin = new() { Minimum = 0, Maximum = 999, Increment = 10, FormatString = "0", Width = 120, Watermark = "Any" };
    private readonly NumericUpDown _gearLevelMax = new() { Minimum = 0, Maximum = 999, Increment = 10, FormatString = "0", Width = 120, Watermark = "Any" };
    private readonly ComboBox _gearSort = new() { ItemsSource = s_gearSorts, SelectedIndex = 0, MinWidth = 160 };
    private readonly CheckBox _gearCrowns = new() { Content = "Crowns only" };
    private readonly CheckBox _gearUnnamed = new() { Content = "Unnamed and test items" };
    private readonly TextBlock _gearCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _gearList = new() { Background = Brushes.Transparent };
    private readonly Border _gearTooltipHost = new() { HorizontalAlignment = HorizontalAlignment.Center };
    // Bundle contents and spell cards, each row centered under the tooltip (Niduus, 2026-10-22: "always have spells centered").
    private readonly StackPanel _gearCards = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _gearDetails = new() { Spacing = 4 };
    private readonly DispatcherTimer _gearDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    // Painted words and the game's small pictures, kept so a tooltip shows at once the next time.
    private readonly Dictionary<(string Text, uint Color, int Size), Bitmap?> _gearText = [];
    private readonly Dictionary<string, Bitmap?> _gearPictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _gearMarkPictures = [];

    private GearBook? _gearBook;
    private string? _gearProblem;
    private Task? _gearLoading;
    private GearItem? _gearPicked;
    private bool _gearFiltersFilled;

    private Control GearTab() {
        _gearDelay.Tick += (_, _) => {
            _gearDelay.Stop();
            RenderGearTab();
        };
        _gearSearch.TextChanged += (_, _) => {
            _gearDelay.Stop();
            _gearDelay.Start();
        };
        foreach (var box in new[] { _gearSlot, _gearSchool, _gearSort }) {
            box.SelectionChanged += (_, e) => {
                if (e.Source == box) {
                    RenderGearTab();
                }
            };
        }

        foreach (var number in new[] { _gearLevelMin, _gearLevelMax }) {
            number.ValueChanged += (_, _) => {
                _gearDelay.Stop();
                _gearDelay.Start();
            };
        }

        _gearCrowns.IsCheckedChanged += (_, _) => RenderGearTab();
        _gearUnnamed.IsCheckedChanged += (_, _) => RenderGearTab();
        _gearList.ItemTemplate = new FuncDataTemplate<GearItem>((item, _) => item is null ? new Panel() : GearRow(item));
        _gearList.SelectionChanged += (_, e) => {
            if (e.Source == _gearList && _gearList.SelectedItem is GearItem item && item != _gearPicked) {
                ShowGear(item);
            }
        };

        Control Labelled(string label, Control box) => new StackPanel {
            Spacing = 2, Margin = new Thickness(0, 0, 10, 6),
            Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12 }, box },
        };

        var filters = new WrapPanel {
            Children = {
                Labelled("Slot", _gearSlot),
                Labelled("School", _gearSchool),
                Labelled("Level from", _gearLevelMin),
                Labelled("Level to", _gearLevelMax),
                Labelled("Sort by", _gearSort),
                WithMargin(_gearCrowns, new Thickness(4, 18, 0, 6)),
                WithMargin(_gearUnnamed, new Thickness(14, 18, 0, 6)),
            },
        };
        var details = new ScrollViewer {
            Width = 430,
            Margin = new Thickness(14, 0, 0, 0),
            Content = Card(new StackPanel {
                Spacing = 12,
                Children = { _gearTooltipHost, _gearCards, _gearDetails },
            }),
        };
        // The item's side runs the full height of the tab, beside the search (Niduus, 2026-10-22: "extend the gear view up more").
        var root = new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("shield", "Gear", "Find gear",
                    "Search or filter, then click an item to see it as the game shows it.",
                    MissingNote("the give-gear commands (.hat, .robe, .pet and the rest) are " + ServerSource.DefaultName +
                        "'s. [Give to my wizard] sends .mod additem instead.", () => !_manager.HasGearCommands),
                    WithHint(_gearSearch, "Search by name or template ID, or narrow the list with the filters. Click an item to see its " +
                        "tooltip as the game draws it, with its stats, item cards, sockets, the level it needs and its marks. Point at a " +
                        "mark or a stat icon to read what it means. Unnamed and test items are the game's own copies with no name, " +
                        "which players never see."),
                    filters, _gearCount, GearGuide()), Dock.Top),
                WithMargin(_gearList, new Thickness(0, 14, 0, 0)),
            },
        };
        ShowGear(null);

        // The tab reads the game's gear the first time it is opened.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadGearTabAsync);

        return root;
    }

    private Task LoadGearTabAsync() {
        if (_gearBook is not null || _gearProblem is not null) {
            RenderGearTab();

            return Task.CompletedTask;
        }

        return _gearLoading ??= LoadAsync();

        async Task LoadAsync() {
            _gearCount.Text = "Reading the gear and its pictures from your game files…";
            try {
                // The spells come first: item cards and pet cards are painted from them.
                if (_spellBook is null) {
                    await LoadSpellsTabAsync();
                }

                if (_spellBook is not { } book) {
                    throw new PanelException(_spellBookProblem ?? "Wizard101's game files could not be found, so the gear can't be shown.");
                }

                var gear = await Task.Run(() => GearBook.Load(book));
                _gearBook ??= gear;
                WriteGamePictureNames(book);
            }
            catch (Exception ex) {
                _gearProblem ??= ex is PanelException ? ex.Message : $"The gear could not be read from the game files: {ex.Message}";
            }
            finally {
                _gearLoading = null;
            }

            RenderGearTab();
        }
    }

    /// <summary>
    /// Writes the names of the game's pictures to game-picture-names.txt in the app's folder, so the right stat and
    /// pet icons can be matched to the game's own files later.
    /// </summary>
    private void WriteGamePictureNames(SpellBook book) {
        try {
            File.WriteAllLines(Path.Combine(_manager.InstallFolder, "game-picture-names.txt"),
                book.ImageNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception) {
            // Only a help for matching icons; the tab works without it.
        }
    }

    private void FillGearFilters(GearBook gear) {
        if (_gearFiltersFilled) {
            return;
        }

        _gearFiltersFilled = true;
        string[] mainSchools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow"];
        var schools = gear.Items.Select(i => i.SchoolNeeded).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => Array.IndexOf(mainSchools, s) is var i and >= 0 ? i : 100).ThenBy(s => s, StringComparer.OrdinalIgnoreCase);
        _gearSchool.ItemsSource = new[] { ALL_SCHOOLS }.Concat(schools).Append(NO_SCHOOL).ToList();
        _gearSchool.SelectedIndex = 0;
    }

    private void RenderGearTab() {
        if (_gearBook is not { } gear) {
            _gearCount.Text = _gearProblem ?? "Reading the gear and its pictures from your game files…";
            _gearList.ItemsSource = null;

            return;
        }

        FillGearFilters(gear);
        var query = (_gearSearch.Text ?? "").Trim();
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;
        var slot = _gearSlot.SelectedItem as string ?? ALL_GEAR;
        var school = _gearSchool.SelectedItem as string ?? ALL_SCHOOLS;
        var min = (int?) _gearLevelMin.Value;
        var max = (int?) _gearLevelMax.Value;
        var crowns = _gearCrowns.IsChecked == true;
        var unnamed = _gearUnnamed.IsChecked == true;

        var found = gear.Items.Where(i =>
            (id is not null ? i.Id == id || words.All(i.Haystack.Contains) : words.All(i.Haystack.Contains))
            // An item with no name of its own is a test or unused copy; it shows when asked for by its ID.
            && (unnamed || i.Name != i.Code || i.Id == id)
            && (slot == ALL_GEAR || i.Slot == slot)
            && (school == ALL_SCHOOLS || (school == NO_SCHOOL ? i.SchoolNeeded.Length == 0 : i.SchoolNeeded.Equals(school, StringComparison.OrdinalIgnoreCase)))
            && (min is null || i.Level >= min)
            && (max is null || i.Level <= max)
            && (!crowns || i.CrownsOnly));

        List<GearItem> shown = _gearSort.SelectedIndex switch {
            1 => [.. found.OrderByDescending(i => i.Level).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            2 => [.. found.OrderBy(i => i.Level).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            3 => [.. found.OrderBy(i => i.Id)],
            // The exact name (or ID) first, then names that start with what was typed.
            _ => [.. found.OrderBy(i => !(i.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || i.Id == id))
                .ThenBy(i => query.Length > 0 && !i.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Level).ThenBy(i => i.Id)],
        };

        _gearList.ItemsSource = shown;
        _gearCount.Text = shown.Count == 0
            ? "Nothing matches. Clear the search or pick \"All\" in the filters."
            : $"{shown.Count:N0} of {gear.Items.Count:N0} items. Click one to see it on the right.";
        if (_gearPicked is not null && shown.Contains(_gearPicked)) {
            _gearList.SelectedItem = _gearPicked;
        }
        else if (shown.Count > 0 && query.Length > 0) {
            _gearList.SelectedItem = shown[0];
        }
    }

    /// <summary>One item in the list: its school, its name, and its slot, level and ID underneath.</summary>
    private Control GearRow(GearItem item) {
        // A bundle's tiers differ by the level of what is inside.
        var level = item.Level > 0 || item.BundleItems.Count == 0 || _gearBook is not { } gear ? item.Level
            : item.BundleItems.Select(gear.Item).Max(i => i?.Level ?? 0);
        var line = $"{item.Slot}" + (level > 0 ? $"  ·  Level {level}+" : "") + $"  ·  ID {item.Id}";
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (item.CrownsOnly) {
            marks.Children.Add(GearMark("CrownsOnly", 18));
        }

        if (item.Retired) {
            marks.Children.Add(GearMark("Retired", 18));
        }

        // The school's icon, or for gear any school may wear, a picture of its slot.
        var school = item.SchoolNeeded.Length > 0 ? item.SchoolNeeded : item.School;
        var badge = school.Length > 0 && !school.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? SchoolBadge(school, 22)
            : ManagerTheme.Icon(item.Slot switch {
                "Mount" => "wing",
                "Pet" => "paw",
                "Deck" => "cards",
                "Wand" or "Athame" => "sword",
                "Ring" => "ring",
                "Amulet" => "sigil",
                "Bundle" => "gift",
                _ => "shield",
            }, 22, ManagerTheme.Accent("Gear"));

        return new DockPanel {
            Margin = new Thickness(2, 3),
            Children = {
                WithDock(WithMargin(badge, new Thickness(0, 0, 10, 0)), Dock.Left),
                WithDock(marks, Dock.Right),
                new StackPanel {
                    Children = {
                        new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = line, FontSize = 12, Foreground = MUTED },
                    },
                },
            },
        };
    }

    // ---------------------------------------------------------------------
    // The item, as the game shows it
    // ---------------------------------------------------------------------

    private void ShowGear(GearItem? item) {
        _gearPicked = item;
        _gearCards.Children.Clear();
        _gearDetails.Children.Clear();
        if (item is null || _gearBook is not { } gear || _spellBook is not { } book) {
            _gearTooltipHost.Child = null;
            _gearDetails.Children.Add(Sub(_gearProblem ?? "Click an item to see it here as the game shows it, with its template ID."));

            return;
        }

        _gearTooltipHost.Child = GearTooltip(gear, book, item);
        if (item.BundleItems.Count > 0) {
            ShowBundleContents(gear, book, item);
        }

        // The item's cards, painted big below the tooltip as the game shows them beside it.
        var cards = item.Spells.Select(s => s.Code).Concat(item.Pet?.Spells ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(gear.Spell).OfType<SpellCard>().ToList();
        _gearCards.Children.Add(CardRow(cards, 118));

        var id = item.Id.ToString(CultureInfo.InvariantCulture);
        _gearDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });
        _gearDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Give to my wizard"), () => {
                    var command = GiveGearCommand(item);
                    _manager.SendCommand(command);
                    Say($"Sent {command} ({item.Name}). Look in your backpack in the game.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(GiveGearCommand(item)))),
            },
        });
        _gearDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _gearDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 110, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        Fact("Slot", item.Slot);
        Fact("School needed", item.SchoolNeeded.Length > 0 ? item.SchoolNeeded : "Any school");
        Fact("Level needed", item.Level > 0 ? $"{item.Level} or higher" : "Any level");
        Fact("Rarity", item.Rarity);
        var rental = GearBook.RentalWords(item.Rental).ToLowerInvariant();
        Fact("Kept", item.Rental.Length == 0 ? "For good" : $"For {rental}{(rental.StartsWith("1 ", StringComparison.Ordinal) ? "" : "s")}, then it is gone");
        Fact("Shop price", string.Join("  ·  ", new[] {
            item.GoldCost > 0 ? $"{item.GoldCost:N0} gold" : "", item.CrownsCost > 0 ? $"{item.CrownsCost:N0} Crowns" : "",
        }.Where(p => p.Length > 0)));
        if (item.Pet is { } pet) {
            Fact("Talents", $"{pet.Talents} pet talents and {pet.DerbyTalents} derby talents it can learn");
            Fact("Hatches in", pet.HatchTime);
        }

        var marks = GearMarks.MARKS.Where(m => item.Flags.Contains(m.Flag)).Select(m => MarkName(book, m.TextKey, m.Name)).ToList();
        Fact("Marks", marks.Count > 0 ? string.Join(", ", marks) : "None: it can be sold, traded and auctioned");
        Fact("Game's name", item.Code);
        Fact("File", item.File);
    }

    // A row of spell cards, centered, wrapping when they don't fit.
    private Control CardRow(IReadOnlyList<SpellCard> cards, double width) {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var card in cards) {
            row.Children.Add(WithMargin(SpellThumb(card.Id, card.School, width), new Thickness(4)));
        }

        return row;
    }

    /// <summary>
    /// A bundle's items under its own tooltip, each as the game's tooltip with its cards, as the game's bundle pictures
    /// show them side by side. A bundle inside a bundle (a set's tiers) shows its items too.
    /// </summary>
    private void ShowBundleContents(GearBook gear, SpellBook book, GearItem bundle) {
        var seen = new HashSet<uint> { bundle.Id };
        var others = new List<string>();
        void Walk(GearItem holder, int depth) {
            foreach (var id in holder.BundleItems) {
                if (!seen.Add(id)) {
                    continue;
                }

                if (gear.Item(id) is not { } inside) {
                    others.Add($"Item {id}");
                    continue;
                }

                if (inside.BundleItems.Count > 0 && depth < 3) {
                    var heading = WithMargin(GearText(inside.Name, TIP_CYAN, 18), new Thickness(0, 6, 0, 0));
                    heading.HorizontalAlignment = HorizontalAlignment.Center;
                    _gearCards.Children.Add(heading);
                    Walk(inside, depth + 1);
                    continue;
                }

                var tooltip = GearTooltip(gear, book, inside);
                tooltip.Margin = new Thickness(4);
                tooltip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                ToolTip.SetTip(tooltip, $"{inside.Slot}  ·  ID {inside.Id}. Click to see it on its own.");
                tooltip.PointerPressed += (_, _) => ShowGear(inside);
                tooltip.HorizontalAlignment = HorizontalAlignment.Center;
                _gearCards.Children.Add(tooltip);
                _gearCards.Children.Add(CardRow(inside.Spells.Select(sp => sp.Code).Concat(inside.Pet?.Spells ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(gear.Spell).OfType<SpellCard>().ToList(), 96));
            }
        }

        Walk(bundle, 0);
        if (others.Count > 0) {
            _gearCards.Children.Add(WithMargin(Sub($"Also in it (not gear): {string.Join(", ", others)}"), new Thickness(4)));
        }
    }

    /// <summary>The command that gives the item: the server's own give-gear command, or .mod additem on another copy.</summary>
    private string GiveGearCommand(GearItem item) => _manager.HasGearCommands ? item.Command : $".mod additem {item.Id}";

    /// <summary>
    /// The item's tooltip as the game draws it, top to bottom: name, pet lines, stats, deck limits, sockets, spells,
    /// what it needs and its marks. The words use the game's own font (WizardFancy from Root.wad's Fonts folder).
    /// </summary>
    private Control GearTooltip(GearBook gear, SpellBook book, GearItem item) {
        var lines = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        void Add(Control line) {
            line.HorizontalAlignment = HorizontalAlignment.Center;
            lines.Children.Add(line);
        }

        // A rental's time is part of its name in the game ("Whalebird Explorer (7 Day)"); a name without it gets it.
        var name = item.Rental.Length > 0 && !item.Name.Contains('(') ? $"{item.Name} ({GearBook.RentalWords(item.Rental)})" : item.Name;
        Add(GearText(name, TIP_CYAN, 21));

        if (item.Pet is { } pet) {
            if (item.Sockets.Any(s => s.Shape == "Star")) {
                Add(GearText(GameWords(book, "SocketsGUI_SocketAtAncient", "Socket at Ancient"), TIP_YELLOW, 19));
            }

            foreach (var (stat, max) in pet.MaxStats) {
                Add(TipLine(GearText($"{max} MAX", TIP_YELLOW, 19), GearIcon(stat.ToLowerInvariant(), $"{stat}: the most this pet's {stat.ToLowerInvariant()} can reach")));
            }
        }

        foreach (var stat in item.Stats) {
            var parts = new List<Control>();
            if (stat.Value.Length > 0) {
                parts.Add(GearText(stat.Value, TIP_YELLOW, 19));
            }

            if (stat.School.Length > 0) {
                parts.Add(WithTip(SchoolBadge(stat.School, 18), stat.Words));
            }

            parts.AddRange(stat.Icons.Select(icon => GearIcon(icon, stat.Words)));
            if (stat.After.Length > 0) {
                parts.Add(GearText(stat.After, TIP_YELLOW, 19));
            }

            var line = TipLine([.. parts]);
            ToolTip.SetTip(line, stat.Words);
            Add(line);
        }

        if (item.BundleItems.Count > 0) {
            Add(GearText(item.BundleItems.Count == 1 ? "1 item inside" : $"{item.BundleItems.Count} items inside", TIP_YELLOW, 18));
        }

        if (item.Deck is { } deck) {
            Add(GearText($"{deck.MaxSpells} MAX SPELLS", TIP_YELLOW, 18));
            Add(GearText($"{deck.MaxCopies} MAX COPIES", TIP_YELLOW, 18));
            Add(GearText($"{deck.MaxSchoolCopies} MAX SCHOOL COPIES", TIP_YELLOW, 18));
            Add(GearText($"{deck.MaxTreasureCards} MAX TREASURE CARDS", TIP_YELLOW, 18));
        }

        var sockets = item.Sockets.Where(s => item.Pet is null || s.Shape != "Star").ToList();
        if (sockets.Count > 0) {
            Add(GearText(GameWords(book, "SocketsGUI_SocketDivider", "SOCKETS"), TIP_ORANGE, 19));
            foreach (var socket in sockets) {
                var parts = new List<Control>();
                if (socket.Locked) {
                    parts.Add(GearIcon("lock", "Locked: unlock it with a socket wrench or Crowns before a jewel goes in"));
                }

                parts.Add(SocketShape(socket.Shape));
                parts.Add(GearText(GameWords(book, "SocketsGUI_SocketType" + SocketTextKey(socket.Shape), $"({socket.Shape})"), TIP_WHITE, 18));
                var line = TipLine([.. parts]);
                ToolTip.SetTip(line, $"A {socket.Shape.ToLowerInvariant()} socket" + (socket.Locked ? " that starts locked" : ""));
                Add(line);
            }
        }

        if (item.Spells.Count > 0) {
            Add(GearText(GameWords(book, "ItemStatsInfo_SpellsText", "Spells"), TIP_YELLOW, 19));
            Add(SpellIcons(gear, book, item.Spells.SelectMany(s => Enumerable.Repeat(s.Code, Math.Min(s.Copies, 4)))));
        }

        if (item.Pet is { Spells.Count: > 0 } petWithSpells) {
            var age = PetAge(book, petWithSpells.SpellsAtLevel);
            Add(GearText(GameWords(book, "ItemStatsInfo_SpellGivenAtPetLevel", "Spells at #1:$PetLevel$").Replace("#1:$PetLevel$", age), TIP_YELLOW, 19));
            Add(SpellIcons(gear, book, petWithSpells.Spells));
        }

        if (item.SchoolNeeded.Length > 0) {
            Add(WithTip(TipLine(SchoolBadge(item.SchoolNeeded, 18), GearText($"{item.SchoolNeeded} only", TIP_RED, 19)),
                $"Only a {item.SchoolNeeded} wizard can wear it."));
        }

        if (item.Level > 0) {
            Add(WithTip(GearText($"Level {item.Level}+", TIP_RED, 20), $"Your wizard must be level {item.Level} or higher to wear it."));
        }

        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var mark in GearMarks.MARKS.Where(m => item.Flags.Contains(m.Flag))) {
            marks.Children.Add(GearMark(mark.Flag, 24));
        }

        if (marks.Children.Count > 0) {
            Add(marks);
        }

        // The game's tooltip frame: a dark blue panel inside a gold edge and a deep red border. The game's own frame
        // pictures are drawn by its GUI files, which the app can't read, so this is drawn in the same colours.
        return new Border {
            Width = GEAR_TIP_WIDTH,
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(3), Padding = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(118, 22, 16)),
            Background = new SolidColorBrush(Color.FromRgb(214, 168, 66)),
            Child = new Border {
                CornerRadius = new CornerRadius(2), Padding = new Thickness(10, 8, 10, 12),
                Background = new LinearGradientBrush {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = {
                        new GradientStop(Color.FromRgb(30, 50, 82), 0),
                        new GradientStop(Color.FromRgb(18, 30, 54), 1),
                    },
                },
                Child = lines,
            },
        };
    }

    /// <summary>Pieces of a tooltip line side by side, centred on the line as the game puts them.</summary>
    private static StackPanel TipLine(params Control[] parts) {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var part in parts) {
            part.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(part);
        }

        return line;
    }

    private static T WithTip<T>(T control, string tip) where T : Control {
        ToolTip.SetTip(control, tip);

        return control;
    }

    /// <summary>The small picture of each card under SPELLS, in a cyan square; pointing at one shows the card big.</summary>
    private Control SpellIcons(GearBook gear, SpellBook book, IEnumerable<string> codes) {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var code in codes) {
            var card = gear.Spell(code);
            Control picture = card is not null && GearPicture(card.ImageName) is { } art
                ? new Image { Source = art, Stretch = Stretch.UniformToFill }
                : SchoolBadge(card?.School ?? "", 22);
            var box = new Border {
                Width = 34, Height = 34, Margin = new Thickness(2), ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.FromUInt32(TIP_CYAN)), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(20, 26, 40)),
                Child = picture,
            };
            if (card is not null) {
                box.PointerEntered += (_, _) => {
                    if (ToolTip.GetTip(box) is null) {
                        ToolTip.SetTip(box, SpellThumb(card.Id, card.School, 200));
                    }
                };
            }
            else {
                ToolTip.SetTip(box, $"{code} (not in the game's spell list)");
            }

            row.Children.Add(box);
        }

        return row;
    }

    private static string PetAge(SpellBook book, int level) {
        var index = Math.Clamp(level, 1, s_petAges.Length);

        return GameWords(book, $"GUI_ShopPetLevel{index}", s_petAges[index - 1]);
    }

    private static string SocketTextKey(string shape) => shape switch {
        "Star" => "Pet",
        "Power" => "PinSquarePip",
        "Shield" => "PinSquareShield",
        "Sword" => "PinSquareSword",
        _ => shape,
    };

    [GeneratedRegex(@"<[^>]*>|#\d+:(?!\$)")]
    private static partial Regex GameMarkup();

    /// <summary>A line of the game's own text without its colour and layout tags, or the fallback when the game has none.</summary>
    private static string GameWords(SpellBook book, string key, string fallback)
        => book.Text(key) is { Length: > 0 } text && GameMarkup().Replace(text, "").Trim() is { Length: > 0 } plain ? plain : fallback;

    private static string MarkName(SpellBook book, string key, string fallback) => key.Length > 0 ? GameWords(book, key, fallback) : fallback;

    // ---------------------------------------------------------------------
    // Words in the game's font
    // ---------------------------------------------------------------------

    /// <summary>
    /// Words painted in the game's tooltip font with its dark edge, wrapped to the tooltip's width; plain bold text in
    /// the same colour when the game's font isn't found.
    /// </summary>
    private Control GearText(string text, uint color, double size) {
        var font = _spellBook?.Font("WizardFancy.xml") ?? _spellBook?.Font("WizardNormal.xml");
        if (font is null) {
            return new TextBlock {
                Text = text, Foreground = new SolidColorBrush(Color.FromUInt32(color)), FontSize = size * 0.8, FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = GEAR_TIP_WIDTH - 30,
            };
        }

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var line in WrapGameText(font, text, (float) size, (float) (GEAR_TIP_WIDTH - 30))) {
            if (PaintedText(font, line, color, size) is { } bitmap) {
                // Painted twice as big, so it stays sharp on a sharp screen.
                var image = new Image { Source = bitmap, Width = bitmap.PixelSize.Width / 2.0, Height = bitmap.PixelSize.Height / 2.0 };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                stack.Children.Add(image);
            }
        }

        return stack;
    }

    private static List<string> WrapGameText(GlyphFont font, string text, float size, float room) {
        var scale = size / font.CellHeight;
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            var tried = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.Measure(tried, scale) > room) {
                lines.Add(current);
                current = word;
            }
            else {
                current = tried;
            }
        }

        if (current.Length > 0) {
            lines.Add(current);
        }

        return lines;
    }

    private Bitmap? PaintedText(GlyphFont font, string text, uint color, double size) {
        var key = (text, color, (int) Math.Round(size * 10));
        if (_gearText.TryGetValue(key, out var cached)) {
            return cached;
        }

        Bitmap? bitmap = null;
        try {
            var height = (float) size * 2;
            var scale = height / font.CellHeight;
            var outline = Math.Max(2f, height * 0.08f);
            // Room for the dark edge on the sides, and a little above and below so lines sit as close as the game's.
            var padX = (int) Math.Ceiling(outline) + 2;
            var padY = (int) Math.Ceiling(height * 0.1f) + 1;
            var width = (int) Math.Ceiling(font.Measure(text, scale)) + padX * 2;
            var tall = (int) Math.Ceiling(height) + padY * 2;
            var layer = new CardCanvas(width, tall);
            font.Draw(layer, text, padX, padY, scale, color);
            var canvas = new CardCanvas(width, tall);
            canvas.DrawLayer(layer, outline, 0xFF080A12);
            bitmap = ToBitmap(canvas.ToImage());
        }
        catch (Exception) {
            // Left out; the line shows without it.
        }

        if (_gearText.Count >= GEAR_TEXT_CACHE) {
            _gearText.Clear();
        }

        _gearText[key] = bitmap;

        return bitmap;
    }

    // ---------------------------------------------------------------------
    // The game's stat icons and marks
    // ---------------------------------------------------------------------

    // Each stat icon's picture in the game: one of its small text icons (MadlibIcons.xml) by token, or a picture by
    // file name. A full game folder has them in its GUI .wad files; a stat whose picture isn't found is drawn instead
    // in the game's colour for it (see GearIconStandIn).
    private static readonly Dictionary<string, string[]> s_gearIconPictures = new(StringComparer.OrdinalIgnoreCase) {
        ["health"] = ["Icon_Health.dds", "Icon_MaxHealth.dds", "Icon_Health_Small.dds"],
        ["mana"] = ["Icon_Mana.dds", "Icon_MaxMana.dds"],
        ["energy"] = ["Icon_Energy.dds", "Icon_MaxEnergy.dds"],
        ["accuracy"] = ["token:Accuracy_image", "Icon_Accuracy.dds"],
        ["damage"] = ["token:Damage_image", "Type_Damage.dds"],
        ["damageflat"] = ["token:Damage_Flat_image", "Type_Damage_Flat.dds"],
        ["resist"] = ["token:Resist_image", "Type_Resist.dds"],
        ["resistflat"] = ["token:Resist_Flat_image", "Type_Resist_Flat.dds"],
        ["pierce"] = ["token:Armor_Penetration_image", "Icon_Armor_Penetration.dds"],
        ["critical"] = ["token:CriticalRating_image", "Icon_CriticalRating.dds"],
        ["block"] = ["token:Block_image", "Icon_BlockRating.dds"],
        ["powerpip"] = ["token:PowerPips_image", "Type_PowerPips.dds"],
        ["shadowpip"] = ["token:ShadowPips_image", "Icon_Shadow_Pip.dds"],
        ["pip"] = ["token:Pips_image", "Type_Pip.dds"],
        ["outgoing"] = ["token:Outgoing_image", "Icon_Outgoing.dds"],
        ["incoming"] = ["token:Incoming_image", "Icon_Incoming.dds"],
        ["heal"] = ["token:Heal_image", "Type_Heal.dds"],
        ["stunresist"] = ["token:StunResist_image", "Type_Stun_Resist.dds"],
        ["archmastery"] = ["token:Archmastery_image", "Icon_Archmastery.dds"],
        ["pipconversion"] = ["Icon_PipConversion.dds", "Icon_Pip_Conversion.dds"],
        ["fishing"] = ["token:FishingSchoolIcon", "Icon_Fishing.dds"],
        ["speed"] = ["Icon_Speed.dds", "Icon_MountSpeed.dds", "Icon_Mount_Speed.dds"],
        ["strength"] = ["Icon_Pet_Strength.dds", "Icon_Strength.dds"],
        ["intellect"] = ["Icon_Pet_Intellect.dds", "Icon_Intellect.dds"],
        ["agility"] = ["Icon_Pet_Agility.dds", "Icon_Agility.dds"],
        ["will"] = ["Icon_Pet_Will.dds", "Icon_Will.dds"],
        ["power"] = ["Icon_Pet_Power.dds", "Icon_Power.dds"],
        ["happiness"] = ["Icon_Pet_Happiness.dds", "Icon_Happiness.dds"],
        ["lock"] = ["Icon_Lock.dds", "Icon_Locked.dds", "Icon_Socket_Locked.dds"],
    };

    // When none of a stat's pictures above is in the game files, the first picture whose name holds all of one of
    // these word sets is used (Niduus, 2026-10-22: the pet icons weren't the game's), before the drawn stand-in.
    private static readonly Dictionary<string, string[][]> s_gearIconSearch = new(StringComparer.OrdinalIgnoreCase) {
        ["strength"] = [["Pet", "Strength"], ["Icon", "Strength"]],
        ["intellect"] = [["Pet", "Intellect"], ["Icon", "Intellect"]],
        ["agility"] = [["Pet", "Agility"], ["Icon", "Agility"]],
        ["will"] = [["Pet", "Will"], ["Icon", "Will_"]],
        ["power"] = [["Pet", "Power"], ["Icon", "Power_"]],
        ["happiness"] = [["Pet", "Happiness"], ["Icon", "Happiness"]],
        ["shadowpip"] = [["Shadow", "Pip"]],
        ["speed"] = [["Icon", "Speed"]],
    };

    // The drawn stand-in for each stat icon: one of the app's icons in the colour the game gives that stat.
    private static readonly Dictionary<string, (string Icon, uint Color)> s_gearIconStandIns = new(StringComparer.OrdinalIgnoreCase) {
        ["health"] = ("heart", 0xFFE8302A),
        ["mana"] = ("drop", 0xFF3D86F2),
        ["energy"] = ("bolt", 0xFF5CCB3A),
        ["accuracy"] = ("target", 0xFFF2C53A),
        ["damage"] = ("hit", 0xFFF26A2A),
        ["damageflat"] = ("hit", 0xFFD0582A),
        ["resist"] = ("shield", 0xFFB9C4CF),
        ["resistflat"] = ("shield", 0xFF8C99A6),
        ["pierce"] = ("sword", 0xFFD9E0E6),
        ["critical"] = ("star", 0xFFF2C230),
        ["block"] = ("shield", 0xFF4F8FE0),
        ["powerpip"] = ("sparkle", 0xFFF2C230),
        ["shadowpip"] = ("sparkle", 0xFF9A62E0),
        ["pip"] = ("sparkle", 0xFFF4F0E0),
        ["outgoing"] = ("right", 0xFFE8E8E8),
        ["incoming"] = ("undo", 0xFFE8E8E8),
        ["heal"] = ("heart", 0xFFE8302A),
        ["stunresist"] = ("bolt", 0xFFF2D640),
        ["archmastery"] = ("ring", 0xFFB58AF0),
        ["pipconversion"] = ("repeat", 0xFFF2C230),
        ["fishing"] = ("drop", 0xFF52C8E8),
        ["speed"] = ("wing", 0xFFF4F4F4),
        ["strength"] = ("weight", 0xFFC9CED4),
        ["intellect"] = ("note", 0xFF5FA6F0),
        ["agility"] = ("wing", 0xFFF0E2C8),
        ["will"] = ("user", 0xFFF2C25A),
        ["power"] = ("target", 0xFFF2F2F2),
        ["happiness"] = ("sparkle", 0xFFF28AB0),
        ["lock"] = ("lock", 0xFFE0B040),
    };

    /// <summary>A stat icon, the game's own picture when it is in the game files, pointing at it says what it is.</summary>
    private Control GearIcon(string key, string tip, double size = 20) {
        Control icon;
        var picture = s_gearIconPictures.TryGetValue(key, out var names) ? names.Select(GearPicture).FirstOrDefault(p => p is not null) : null;
        if (picture is null && _spellBook is { } book && s_gearIconSearch.TryGetValue(key, out var searches)) {
            picture = searches.SelectMany(words => book.FindImages(words).Where(n => n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Take(1))
                .Select(GearPicture).FirstOrDefault(p => p is not null);
        }
        if (picture is not null) {
            icon = new Image { Source = picture, Width = size, Height = size, Stretch = Stretch.Uniform };
        }
        else {
            var (name, color) = s_gearIconStandIns.TryGetValue(key, out var standIn) ? standIn : ("info", TIP_WHITE);
            icon = ManagerTheme.Icon(name, size, new SolidColorBrush(Color.FromUInt32(color)));
        }

        ToolTip.SetTip(icon, tip);

        return icon;
    }

    /// <summary>A picture from the game files by file name (or "token:" and a text icon's name), shown small; null when not there.</summary>
    private Bitmap? GearPicture(string? name) {
        if (string.IsNullOrEmpty(name) || _spellBook is not { } book) {
            return null;
        }

        if (_gearPictures.TryGetValue(name, out var cached)) {
            return cached;
        }

        Bitmap? bitmap = null;
        try {
            var file = name.StartsWith("token:", StringComparison.Ordinal) ? book.TokenIcon(name[6..]) : name;
            if (book.Image(file) is { } image) {
                bitmap = ToBitmap(image);
            }
        }
        catch (Exception) {
            // The drawn stand-in shows.
        }

        _gearPictures[name] = bitmap;

        return bitmap;
    }

    /// <summary>A socket's shape, drawn as the game outlines it.</summary>
    private static Control SocketShape(string shape) {
        var data = shape switch {
            "Square" => "M3 3h14v14H3z",
            "Circle" => "M10 2a8 8 0 1 0 0.01 0z",
            "Triangle" => "M10 2l8.5 15h-17z",
            "Tear" => "M10 2C10 2 3.5 9.5 3.5 13a6.5 6.5 0 0 0 13 0C16.5 9.5 10 2 10 2z",
            "Star" => "M10 1.5l2.5 5.4 5.9.7-4.4 4 1.2 5.9L10 14.6l-5.2 2.9 1.2-5.9-4.4-4 5.9-.7z",
            _ => "",
        };
        if (data.Length == 0) {
            var icon = shape switch { "Power" => "sparkle", "Shield" => "shield", "Sword" => "sword", _ => "square" };

            return ManagerTheme.Icon(icon, 18, new SolidColorBrush(Color.FromRgb(236, 236, 236)));
        }

        return new Avalonia.Controls.Shapes.Path {
            Data = Geometry.Parse(data), Width = 18, Height = 18, Stretch = Stretch.Uniform,
            Stroke = Brushes.White, StrokeThickness = 1.6, Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
        };
    }

    /// <summary>
    /// A mark from the bottom of the tooltip (No Auction, Crowns Only, No Sell and the rest), with its meaning in plain
    /// words when pointed at. The game's picture is used when the game files have one named for the mark; otherwise it is
    /// drawn the way the game draws it: the thing it is about, struck through in red, and the green G for Crowns.
    /// </summary>
    private Control GearMark(string flag, double size) {
        var (_, key, fallback, words) = GearMarks.MARKS.FirstOrDefault(m => m.Flag == flag);
        var name = _spellBook is { } book ? MarkName(book, key ?? "", fallback ?? flag) : fallback ?? flag;
        var tip = $"{name}: {words}";

        if (!_gearMarkPictures.TryGetValue(flag, out var found)) {
            found = _spellBook?.FindImages(flag).FirstOrDefault(n => n.StartsWith("Icon", StringComparison.OrdinalIgnoreCase));
            _gearMarkPictures[flag] = found;
        }

        var picture = GearPicture(found);
        Control mark;
        if (picture is not null) {
            mark = new Image { Source = picture, Width = size, Height = size, Stretch = Stretch.Uniform };
        }
        else if (flag == "CrownsOnly") {
            mark = new Panel {
                Width = size, Height = size,
                Children = {
                    new TextBlock {
                        Text = "G", FontSize = size * 0.95, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(Color.FromRgb(10, 40, 12)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1.5, 1.5, 0, 0),
                    },
                    new TextBlock {
                        Text = "G", FontSize = size * 0.95, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(Color.FromRgb(62, 214, 72)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
        }
        else {
            var (icon, color, struck) = flag switch {
                "NoAuction" => ("gavel", 0xFFE0A050u, true),
                "NoSell" => ("coin", 0xFFF2C53Au, true),
                "NoTrade" => ("swap", 0xFFE8E8E8u, true),
                "NoGift" => ("gift", 0xFFE070B0u, true),
                "NoBank" => ("home", 0xFFC8B090u, true),
                "NoDrop" => ("trash", 0xFFC0C8D0u, true),
                "NoDye" => ("drop", 0xFF60B0F0u, true),
                "NoShatter" => ("sparkle", 0xFF80D8F0u, true),
                "NoHatchmaking" => ("paw", 0xFFE0C090u, true),
                "NoPVP" => ("swords", 0xFFE0E0E0u, true),
                "PVPOnly" => ("swords", 0xFFF2C53Au, false),
                "NoCombat" => ("sword", 0xFFE0E0E0u, true),
                "ArenaPointsOnly" => ("card", 0xFFF2C53Au, false),
                "Retired" => ("hourglass", 0xFFB0B8C0u, false),
                _ => ("info", 0xFFE0E0E0u, false),
            };
            var panel = new Panel { Width = size, Height = size };
            panel.Children.Add(ManagerTheme.Icon(icon, size * 0.82, new SolidColorBrush(Color.FromUInt32(color))));
            if (struck) {
                panel.Children.Add(ManagerTheme.Icon("ban", size, new SolidColorBrush(Color.FromRgb(232, 40, 30))));
            }

            foreach (var child in panel.Children) {
                child.HorizontalAlignment = HorizontalAlignment.Center;
                child.VerticalAlignment = VerticalAlignment.Center;
            }

            mark = panel;
        }

        ToolTip.SetTip(mark, tip);

        return mark;
    }

}
