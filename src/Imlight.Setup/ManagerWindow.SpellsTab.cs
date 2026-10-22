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
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Spells tab (Niduus, 2026-10-22: "Make a Spell TAB for all the spell cards to be at"): every spell card in the
/// game as the game draws it, in a grid that can be searched by name or ID, filtered by school, pip cost, type,
/// treasure card or not and hidden or cheat spells, and sorted. Clicking a card shows its full stats, effects and
/// template ID on the right. Only the rows on screen are made, and only the cards on screen are painted, so the
/// thousands of cards stay quick.
/// </summary>
internal sealed partial class ManagerWindow {

    private const double SPELL_TILE_WIDTH = 132;
    private const double SPELL_TILE_GAP = 10;
    // A tile's whole width on screen: the picture plus its frame's padding (3) and border (2) on both sides.
    private const double SPELL_TILE_OUTER_WIDTH = SPELL_TILE_WIDTH + 2 * (3 + 2);
    private const int SPELL_TILE_SCALE = 2;
    private const int SPELL_BIG_SCALE = 3;
    private const int CARD_BITMAP_CACHE = 900;

    private const string ANY = "Any";
    private const string ALL_SCHOOLS = "All schools";
    private const string ALL_TYPES = "All types";
    private const string MINION = "Minion";

    private static readonly IBrush SPELL_PICKED = ManagerTheme.Brush(ManagerTheme.ACCENT);

    private static readonly string[] s_spellKinds = ["All cards", "Spells (no treasure cards)", "Treasure cards", "Item cards", "Pet cards"];

    private static readonly string[] s_spellSorts = [
        "Name", "Pip cost (low first)", "Pip cost (high first)", "School", "Accuracy (high first)", "Type", "Template ID",
    ];

    private readonly TextBox _spellsSearch = new() { Watermark = "Search by name, ID or words on the card, such as Fire Cat or 103007158" };
    private readonly ComboBox _spellsSchool = new() { MinWidth = 140 };
    private readonly ComboBox _spellsPips = new() { MinWidth = 110 };
    private readonly ComboBox _spellsType = new() { MinWidth = 140 };
    private readonly ComboBox _spellsKind = new() { ItemsSource = s_spellKinds, SelectedIndex = 0, MinWidth = 190 };
    private readonly ComboBox _spellsSort = new() { ItemsSource = s_spellSorts, SelectedIndex = 0, MinWidth = 180 };
    private readonly CheckBox _spellsHidden = new() { Content = "Hidden and cheat spells" };
    private readonly TextBlock _spellsCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ItemsControl _spellsGrid = new();
    private readonly ScrollViewer _spellsScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _spellsDetails = new() { Spacing = 4 };
    private readonly Image _spellsBigCard = new() {
        Width = CardPainter.CARD_WIDTH * 2, Height = CardPainter.CARD_HEIGHT * 2, Stretch = Stretch.Uniform,
    };
    private readonly DispatcherTimer _spellsDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    // Painted cards, by spell, "xN" count and size; the oldest go first once there are too many.
    private readonly Dictionary<(uint Id, int Count, int Scale), Bitmap> _cardBitmaps = [];
    private readonly Queue<(uint Id, int Count, int Scale)> _cardBitmapOrder = new();
    private readonly Stack<CardJob> _cardJobs = new();
    private int _cardWorkers;

    private List<SpellCard> _spellsShown = [];
    private SpellCard? _spellsPicked;
    private Border? _spellsPickedTile;
    private int _spellsColumns;
    private bool _spellsFiltersFilled;
    private Task? _spellsLoading;

    /// <summary>A card waiting to be painted, and whether it is still wanted once its turn comes.</summary>
    private sealed record CardJob(SpellBook Book, SpellCard Card, int Count, int Scale, Func<bool> Wanted, Action<Bitmap> Done);

    private Control SpellsTab() {
        _spellsDelay.Tick += (_, _) => {
            _spellsDelay.Stop();
            RenderSpellsTab();
        };
        _spellsSearch.TextChanged += (_, _) => {
            _spellsDelay.Stop();
            _spellsDelay.Start();
        };
        foreach (var box in new[] { _spellsSchool, _spellsPips, _spellsType, _spellsKind, _spellsSort }) {
            box.SelectionChanged += (_, e) => {
                // Only a choice made here counts, not one from a list further in.
                if (e.Source == box) {
                    RenderSpellsTab();
                }
            };
        }

        _spellsHidden.IsCheckedChanged += (_, _) => RenderSpellsTab();
        var hideBroken = HideBrokenBox(RenderSpellsTab);
        _spellsGrid.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _spellsGrid.ItemTemplate = new FuncDataTemplate<SpellCard[]>((row, _) => row is null ? new Panel() : SpellsRow(row));
        _spellsScroll.Content = _spellsGrid;
        _spellsScroll.SizeChanged += (_, _) => {
            CenterSpells();
            if (SpellsColumns() != _spellsColumns) {
                LayOutSpells();
            }
        };
        RenderOptions.SetBitmapInterpolationMode(_spellsBigCard, BitmapInterpolationMode.HighQuality);

        Control Labelled(string label, Control box) => new StackPanel {
            Spacing = 2, Margin = new Thickness(0, 0, 10, 6),
            Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12 }, box },
        };

        var filters = new WrapPanel {
            Children = {
                Labelled("School", _spellsSchool),
                Labelled("Pip cost", _spellsPips),
                Labelled("Type", _spellsType),
                Labelled("Cards", _spellsKind),
                Labelled("Sort by", _spellsSort),
                WithMargin(_spellsHidden, new Thickness(4, 18, 0, 6)),
                WithMargin(hideBroken, new Thickness(14, 18, 0, 6)),
            },
        };
        var details = new ScrollViewer {
            Width = CardPainter.CARD_WIDTH * 2 + 72,
            Margin = new Thickness(14, 0, 0, 0),
            Content = Card(new StackPanel {
                Spacing = 10,
                Children = {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = _spellsBigCard },
                    _spellsDetails,
                },
            }),
        };
        var root = new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(Fold("cards", "Spells", "Find a card",
                    "Search or filter, then click a card to see its stats and ID.",
                    WithHint(_spellsSearch, "Search by name or template ID, or narrow the cards down with the filters. Click a card to see its stats, " +
                        "effects and ID on the right. Hidden and cheat spells are the monsters', bosses' and tests' own copies: a search always " +
                        "finds them too; the switch shows them while browsing without a search. " +
                        "BROKEN cards are ones Imlight's server can't do yet; PARTLY cards work but skip a part (point at the mark to see which). " +
                        "Each card shows the game's text, then in { } what the code does (written by Imlight from the spell's effects)."),
                    filters, _spellsCount), Dock.Top),
                WithMargin(new DockPanel {
                    Children = { WithDock(details, Dock.Right), _spellsScroll },
                }, new Thickness(0, 14, 0, 0)),
            },
        };
        ShowSpellsDetails(null);

        // The tab reads the game's spells the first time it is opened.
        root.AttachedToVisualTree += (_, _) => _ = Guard(LoadSpellsTabAsync);

        return root;
    }

    private Task LoadSpellsTabAsync() {
        if (_spellBook is not null || _spellBookProblem is not null) {
            RenderSpellsTab();

            return Task.CompletedTask;
        }

        return _spellsLoading ??= LoadAsync();

        async Task LoadAsync() {
            _spellsCount.Text = "Reading the spells and their cards from your game files…";
            try {
                var folder = new[] { _gameFiles.GameDataFolder, _gameFiles.CopyFolder }
                    .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, "Root.wad")));
                if (folder is null) {
                    throw new PanelException("Wizard101's game files could not be found, so the spell cards can't be shown. " +
                        "Click \"Update Imlight\" on the Tools tab once so the app can find Wizard101, or read them on the Game Files tab.");
                }

                var book = await Task.Run(() => SpellBook.Load(folder));
                _spellBook ??= book;
            }
            catch (Exception ex) {
                _spellBookProblem ??= ex is PanelException ? ex.Message : $"The spells could not be read from the game files: {ex.Message}";
            }
            finally {
                _spellsLoading = null;
            }

            RenderSpellsTab();
        }
    }

    /// <summary>Fills the drop-downs with the schools, costs and types the game's spells have.</summary>
    private void FillSpellFilters(SpellBook book) {
        if (_spellsFiltersFilled) {
            return;
        }

        _spellsFiltersFilled = true;
        string[] mainSchools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow"];
        var schools = book.Spells.Select(s => s.School).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => Array.IndexOf(mainSchools, s) is var i and >= 0 ? i : 100).ThenBy(s => s, StringComparer.OrdinalIgnoreCase);
        _spellsSchool.ItemsSource = new[] { ALL_SCHOOLS }.Concat(schools).ToList();
        _spellsSchool.SelectedIndex = 0;
        var pips = book.Spells.Where(s => !s.XPips).Select(s => s.Pips).Distinct().Order().Select(p => p.ToString(CultureInfo.InvariantCulture));
        _spellsPips.ItemsSource = new[] { ANY }.Concat(pips).Append("X").ToList();
        _spellsPips.SelectedIndex = 0;
        var types = book.Spells.Select(s => s.Type).Where(t => t.Length > 0).Append(MINION).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Select(t => t.Replace('_', ' '));
        _spellsType.ItemsSource = new[] { ALL_TYPES }.Concat(types).ToList();
        _spellsType.SelectedIndex = 0;
    }

    private void RenderSpellsTab() {
        if (_spellBook is not { } book) {
            _spellsCount.Text = _spellBookProblem ?? "Reading the spells and their cards from your game files…";
            _spellsShown = [];
            LayOutSpells();

            return;
        }

        FillSpellFilters(book);
        var query = (_spellsSearch.Text ?? "").Trim();
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var school = _spellsSchool.SelectedItem as string ?? ALL_SCHOOLS;
        var pips = _spellsPips.SelectedItem as string ?? ANY;
        var type = (_spellsType.SelectedItem as string ?? ALL_TYPES).Replace(' ', '_');
        var kind = _spellsKind.SelectedIndex;
        var hidden = _spellsHidden.IsChecked == true;
        var id = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (uint?) null;

        var matched = book.Spells.Where(s =>
            (id is not null ? s.Id == id || words.All(s.Haystack.Contains) : words.All(s.Haystack.Contains))
            // A spell asked for by its ID or found by a search shows even when it is hidden (Niduus, 2026-10-22: searching
            // "Emulate" found nothing, as every Emulate card is a boss copy).
            && (hidden || !s.Hidden || s.Id == id || words.Length > 0)
            && (school == ALL_SCHOOLS || s.School.Equals(school, StringComparison.OrdinalIgnoreCase))
            && (pips == ANY || (pips == "X" ? s.XPips : !s.XPips && s.Pips.ToString(CultureInfo.InvariantCulture) == pips))
            && (type == ALL_TYPES.Replace(' ', '_') || (type == MINION ? s.Minion : s.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            && kind switch {
                1 => !s.Treasure,
                2 => s.Treasure,
                3 => s.Item,
                4 => s.Source == "Pet",
                _ => true,
            }).ToList();

        // Broken cards are left out unless asked for by their ID; how many is said in the count.
        var brokenHidden = 0;
        IEnumerable<SpellCard> found = matched;
        if (HideBroken) {
            brokenHidden = matched.Count(s => s.Broken && s.Id != id);
            found = matched.Where(s => !s.Broken || s.Id == id);
        }

        IOrderedEnumerable<SpellCard> sorted = _spellsSort.SelectedIndex switch {
            1 => found.OrderBy(s => s.PipSort),
            2 => found.OrderByDescending(s => s.PipSort),
            3 => found.OrderBy(s => s.School, StringComparer.OrdinalIgnoreCase),
            4 => found.OrderByDescending(s => s.Accuracy),
            5 => found.OrderBy(s => s.Type, StringComparer.OrdinalIgnoreCase),
            6 => found.OrderBy(s => s.Id),
            // The exact name (or ID) first, then names that start with what was typed.
            _ => found.OrderBy(s => !(s.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || s.Id == id))
                .ThenBy(s => query.Length > 0 && !s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)),
        };
        // Among same-named cards, the one players know (its file is named like the card) before treasure, item, pet
        // and monster copies.
        _spellsShown = [.. sorted.ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Hidden)
            .ThenBy(s => s.Code.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ? 0 : s.Treasure ? 1 : 2)
            .ThenBy(s => s.PipSort).ThenBy(s => s.Id)];

        var total = book.Spells.Count;
        _spellsCount.Text = (_spellsShown.Count == 0
            ? brokenHidden > 0
                ? "No working spell card matches. Clear \"Hide broken cards\" to see the broken ones."
                : "No spell card matches. Clear the search or pick \"All\" in the filters."
            : $"{_spellsShown.Count:N0} of {total:N0} spell cards. Click one to see it on the right.") + BrokenHiddenNote(brokenHidden);
        LayOutSpells();
        _spellsScroll.Offset = new Vector(0, 0);
        if (_spellsPicked is null && _spellsShown.Count > 0 && query.Length > 0) {
            ShowSpellsDetails(_spellsShown[0]);
        }
    }

    private int SpellsColumns() {
        // Leave room for the scroll bar so the last card never runs under the details panel.
        var width = _spellsScroll.Bounds.Width > 0 ? _spellsScroll.Bounds.Width - 24 : 700;

        return Math.Max(1, (int) ((width + SPELL_TILE_GAP) / (SPELL_TILE_OUTER_WIDTH + SPELL_TILE_GAP)));
    }

    /// <summary>Puts the cards found into rows as wide as the grid; only the rows on screen are made.</summary>
    private void LayOutSpells() {
        _spellsColumns = SpellsColumns();
        var rows = new List<SpellCard[]>();
        for (var i = 0; i < _spellsShown.Count; i += _spellsColumns) {
            rows.Add([.. _spellsShown.Skip(i).Take(_spellsColumns)]);
        }

        _spellsGrid.ItemsSource = rows;
        CenterSpells();
    }

    /// <summary>Splits the room left over after the last whole card evenly on both sides of the grid.</summary>
    private void CenterSpells() {
        var width = _spellsScroll.Bounds.Width > 0 ? _spellsScroll.Bounds.Width - 24 : 700;
        var used = _spellsColumns * SPELL_TILE_OUTER_WIDTH + (_spellsColumns - 1) * SPELL_TILE_GAP;
        _spellsGrid.Margin = new Thickness(Math.Max(0, Math.Floor((width - used) / 2)), 0, 0, 0);
    }

    private Control SpellsRow(SpellCard[] row) {
        var panel = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = SPELL_TILE_GAP, Margin = new Thickness(0, 0, 0, SPELL_TILE_GAP),
        };
        foreach (var card in row) {
            panel.Children.Add(SpellTile(card));
        }

        return panel;
    }

    /// <summary>One card in the grid: a stand-in in its school's colour until the card is painted.</summary>
    private Border SpellTile(SpellCard card) {
        var height = SPELL_TILE_WIDTH * CardPainter.CARD_HEIGHT / CardPainter.CARD_WIDTH;
        var picture = new Panel {
            Width = SPELL_TILE_WIDTH, Height = height,
            Children = {
                new Border {
                    CornerRadius = new CornerRadius(6),
                    Background = new LinearGradientBrush {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops = {
                            new GradientStop(Color.FromUInt32(CardPainter.SchoolColor(card.School)), 0),
                            new GradientStop(Color.FromArgb(255, 40, 34, 28), 1),
                        },
                    },
                    Child = new TextBlock {
                        Text = card.Name, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8),
                    },
                },
            },
        };
        // The ID sits under the card and the BROKEN, PARTLY or hidden mark on a line of its own under that, so every
        // tile is the same size and the cards stay evenly spaced (Niduus, 2026-10-22: "I'm big on spacing").
        var marks = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6, Height = 18, HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (SpellStatusBadge(card, 10) is { } badge) {
            marks.Children.Add(badge);
        }

        if (card.Hidden) {
            marks.Children.Add(new TextBlock { Text = "hidden", FontSize = 11, Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center });
        }

        var caption = new StackPanel {
            Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
            Children = {
                new TextBlock { Text = $"ID {card.Id}", FontSize = 11, Foreground = MUTED, HorizontalAlignment = HorizontalAlignment.Center },
                marks,
            },
        };

        var tile = new Border {
            Width = SPELL_TILE_OUTER_WIDTH, Padding = new Thickness(3), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            BorderBrush = card == _spellsPicked ? SPELL_PICKED : Brushes.Transparent,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel {
                Spacing = 2,
                Children = {
                    picture,
                    caption,
                },
            },
        };
        var does = card.Shown;
        var status = card.Support.Status == SpellStatus.Works ? "" : $"\n{(card.Broken ? "BROKEN" : "PARTLY")}: {card.Support.Summary}";
        ToolTip.SetTip(tile, $"{card.Name}  ·  {card.School}  ·  {card.Cost}  ·  {card.Accuracy}%\n{does}{status}");
        if (card == _spellsPicked) {
            _spellsPickedTile = tile;
        }

        tile.PointerPressed += (_, _) => {
            if (_spellsPickedTile is not null) {
                _spellsPickedTile.BorderBrush = Brushes.Transparent;
            }

            _spellsPickedTile = tile;
            tile.BorderBrush = SPELL_PICKED;
            ShowSpellsDetails(card);
        };

        // Painted only while the row is on screen; a row scrolled past before its turn is skipped.
        var onScreen = true;
        tile.DetachedFromVisualTree += (_, _) => onScreen = false;
        if (_spellBook is { } book) {
            RequestCard(book, card, CardPainter.AUTO_COUNT, SPELL_TILE_SCALE, () => onScreen, bitmap => {
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
                picture.Children.Clear();
                picture.Children.Add(image);
            });
        }

        return tile;
    }

    /// <summary>Shows a card big on the right with everything about it.</summary>
    private void ShowSpellsDetails(SpellCard? card) {
        _spellsPicked = card;
        _spellsDetails.Children.Clear();
        if (card is null || _spellBook is not { } book) {
            _spellsBigCard.Source = null;
            _spellsDetails.Children.Add(Sub(_spellBookProblem ?? "Click a card to see its stats, effects and template ID here."));

            return;
        }

        _spellsBigCard.Source = null;
        RequestCard(book, card, CardPainter.AUTO_COUNT, SPELL_BIG_SCALE, () => _spellsPicked == card, bitmap => {
            if (_spellsPicked == card) {
                _spellsBigCard.Source = bitmap;
            }
        });

        var id = card.Id.ToString(CultureInfo.InvariantCulture);
        _spellsDetails.Children.Add(new SelectableTextBlock { Text = card.Name, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (SpellStatusLine(card) is { } statusLine) {
            _spellsDetails.Children.Add(statusLine);
        }

        _spellsDetails.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(id))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = $"Template ID {id}", FontFamily = MONO, Foreground = GOOD }),
            },
        });
        _spellsDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Learn on my wizard"), () => {
                    _manager.SendCommand(card.Command);
                    Say($"Sent {card.Command} ({card.Name}, {card.School}). Open your spellbook in the game to see it.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(card.Command))),
            },
        });
        _spellsDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _spellsDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 104, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        void FactWith(string label, Control? value) {
            if (value is not null) {
                _spellsDetails.Children.Add(new DockPanel {
                    Children = { WithDock(new TextBlock { Text = label, Width = 104, Foreground = MUTED }, Dock.Left), value },
                });
            }
        }

        string YesNo(bool value) => value ? "Yes" : "No";
        FactWith("Does", card.Does.Length > 0 ? SpellDoesText(card) : null);
        Fact("School", card.School);
        Fact("Pip cost", card.Cost);
        Fact("Accuracy", $"{card.Accuracy}%");
        Fact("Type", card.Type.Replace('_', ' ') + (card.Minion && !card.Type.Equals(MINION, StringComparison.OrdinalIgnoreCase) ? " (summons a minion)" : ""));
        Fact("Kind", card.Kind + (card.Hidden ? " (hidden: players never get it)" : ""));
        Fact("Comes from", card.Source switch {
            "Caster" => "The wizard's own deck",
            "Pet" => "A pet",
            "ShadowCreature" => "A shadow creature",
            "Weapon" => "A weapon",
            "Equipment" => "Gear (an item card)",
            var other => other,
        });
        Fact("Level", card.Level > 0 ? $"{card.Level} or higher" : "");
        Fact("PvP / PvE", card.PvP && card.PvE ? "Both" : card.PvP ? "PvP only" : card.PvE ? "PvE only" : "Neither");
        Fact("Enchantable", card.NoPvEEnchant && card.NoPvPEnchant ? "No"
            : card.NoPvEEnchant ? "Not in PvE" : card.NoPvPEnchant ? "Not in PvP" : "Yes");
        Fact("Discardable", YesNo(!card.NoDiscard));
        Fact("Cloaked", card.Cloaked ? "Yes" : "");
        Fact("Most copies", card.MaxCopies > 0 ? card.MaxCopies.ToString(CultureInfo.InvariantCulture) : "");
        Fact("Category", card.Category);
        Fact("Art", card.ImageName);
        Fact("Game's name", card.Code);
        Fact("File", card.File);

        if (card.Effects.Count > 0) {
            _spellsDetails.Children.Add(WithMargin(new TextBlock { Text = "Effects", FontWeight = FontWeight.SemiBold }, new Thickness(0, 8, 0, 0)));
            var number = 1;
            foreach (var effect in card.Effects) {
                var parts = new List<string> { effect.Kind.Length > 0 ? Words(effect.Kind) : "Effect" };
                if (effect.Amount != 0) {
                    parts.Add(effect.Amount.ToString(CultureInfo.InvariantCulture));
                }

                if (effect.School.Length > 0) {
                    parts.Add(effect.School);
                }

                if (effect.Rounds > 0) {
                    parts.Add(effect.Rounds == 1 ? "for 1 round" : $"for {effect.Rounds} rounds");
                }

                if (effect.Target.Length > 0) {
                    parts.Add("on " + Words(effect.Target).ToLowerInvariant());
                }

                _spellsDetails.Children.Add(new SelectableTextBlock {
                    Text = $"{number++}. {string.Join("  ·  ", parts)}", TextWrapping = TextWrapping.Wrap, FontSize = 13,
                });
            }
        }

        // "DamageOverTime" as "Damage Over Time".
        static string Words(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()))
            .Replace('_', ' ');
    }

    // ---------------------------------------------------------------------
    // Painting cards in the background
    // ---------------------------------------------------------------------

    /// <summary>
    /// Paints a card in the background (or takes it from the cache) and hands it to <paramref name="done"/> on the
    /// window. The newest request goes first, so the cards just scrolled to come up before the ones scrolled past.
    /// </summary>
    private void RequestCard(SpellBook book, SpellCard card, int count, int scale, Func<bool> wanted, Action<Bitmap> done) {
        if (_cardBitmaps.TryGetValue((card.Id, count, scale), out var cached)) {
            done(cached);

            return;
        }

        lock (_cardJobs) {
            _cardJobs.Push(new CardJob(book, card, count, scale, wanted, done));
            if (_cardWorkers >= Math.Clamp(Environment.ProcessorCount / 2, 1, 4)) {
                return;
            }

            _cardWorkers++;
        }

        _ = Task.Run(PaintCards);
    }

    private void PaintCards() {
        while (true) {
            CardJob job;
            lock (_cardJobs) {
                if (_cardJobs.Count == 0) {
                    _cardWorkers--;

                    return;
                }

                job = _cardJobs.Pop();
            }

            if (!job.Wanted()) {
                continue;
            }

            GameImage image;
            try {
                image = CardPainter.Paint(job.Book, job.Card, job.Count, job.Scale);
            }
            catch (Exception) {
                // The stand-in stays.
                continue;
            }

            Dispatcher.UIThread.Post(() => {
                var key = (job.Card.Id, job.Count, job.Scale);
                if (!_cardBitmaps.TryGetValue(key, out var bitmap)) {
                    bitmap = ToBitmap(image);
                    _cardBitmaps[key] = bitmap;
                    _cardBitmapOrder.Enqueue(key);
                    while (_cardBitmapOrder.Count > CARD_BITMAP_CACHE) {
                        _cardBitmaps.Remove(_cardBitmapOrder.Dequeue());
                    }
                }

                job.Done(bitmap);
            });
        }
    }

}
