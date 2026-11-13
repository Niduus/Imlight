using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// One server-played wizard at a time (Niduus, 2026-11-13: "pages ... that allow me to select members of QT (or default in
/// the case of Ultimus) to view their gear, stats and their current deck and quest"). Shared by the Quest Team and Ultimus
/// pages: give it the "members" of a status.json; each member is a JSON object with role, name, level, school, zone,
/// health, mana, questNow, stepNow, questList[], stats[], gear[], deckCards[], deckSize, deckFor, spells[], and deckGrid[],
/// itemGrid[] (with "off"), treasureGrid[] and deckMax for the card pictures (all optional;
/// the server's BotSheet.Fill writes stats, deckCards, deckSize, school and mana). With one member the picker hides.
/// </summary>
internal sealed class WizardSheet {

    private readonly StackPanel _picker = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, ToggleButton> _buttons = new(StringComparer.Ordinal);
    private readonly TextBlock _title = Text(strong: true, size: 16);
    private readonly TextBlock _where = Text();
    private readonly TextBlock _quest = Text();
    private readonly StackPanel _stats = new() { Spacing = 2 };
    private readonly StackPanel _gear = new() { Spacing = 2 };
    private readonly TextBlock _deckTitle = Text(strong: true);
    private readonly StackPanel _deck = new() { Spacing = 2 };
    private readonly TextBlock _spells = Text();
    private readonly TextBlock _empty = Text();
    private readonly Control _body;
    private string _picked = "";
    private JsonObject? _shown;
    private string _gridKey = "";

    /// <summary>
    /// Paints a spell's card (spell id, school, width, count; 0 = no count) as the game draws it. Set by the window; without
    /// it the deck shows as lines of text.
    /// </summary>
    internal Func<uint, string, double, int, Control>? CardThumb { get; set; }

    internal Control View { get; }

    internal WizardSheet(string emptyText) {
        _empty.Text = emptyText;
        _empty.Foreground = ManagerTheme.MUTED_BRUSH;
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        columns.Children.Add(Section("Stats", _stats, 0, 0));
        columns.Children.Add(Section("Gear", _gear, 0, 2));
        var deck = Section(null, _deck, 1, 0, _deckTitle);
        columns.Children.Add(deck);
        columns.Children.Add(Section("Trained spells", _spells, 1, 2));
        _body = new StackPanel { Spacing = 10, Children = { _title, _where, _quest, columns } };
        View = new StackPanel { Spacing = 12, Children = { _picker, _empty, _body } };
        _body.IsVisible = false;
    }

    /// <summary>Shows the members of a status.json (null or empty: the empty text).</summary>
    internal void Show(JsonArray? members) {
        var list = (members ?? []).OfType<JsonObject>().ToList();
        _empty.IsVisible = list.Count == 0;
        _body.IsVisible = list.Count > 0;
        _picker.IsVisible = list.Count > 1;
        if (list.Count == 0) {
            return;
        }

        foreach (var member in list) {
            var key = Key(member);
            if (_buttons.ContainsKey(key)) {
                continue;
            }

            var button = new ToggleButton { Content = key, MinWidth = 90 };
            button.Click += (_, _) => {
                _picked = key;
                Show(members);
            };
            _buttons[key] = button;
            _picker.Children.Add(button);
        }

        var shown = list.FirstOrDefault(m => Key(m) == _picked) ?? list[0];
        _picked = Key(shown);
        foreach (var (key, button) in _buttons) {
            button.IsChecked = key == _picked;
        }

        Fill(shown);
    }

    /// <summary>Draws the shown wizard again, as when the game's card pictures have just been read.</summary>
    internal void Redraw() {
        _gridKey = "";
        if (_shown is not null) {
            Fill(_shown);
        }
    }

    private void Fill(JsonObject m) {
        _shown = m;
        var school = S(m, "school");
        _title.Text = $"{S(m, "name")}  ·  level {(int?) m["level"] ?? 0}" + (school.Length > 0 ? $" {school}" : "") +
                      (S(m, "role") is { Length: > 0 } role && role != school ? $"  ·  {role}" : "");
        _where.Text = $"In {Or(S(m, "zone"), "?")}  ·  health {Or(S(m, "health"), "?")}" +
                      (S(m, "mana") is { Length: > 0 } mana ? $"  ·  mana {mana}" : "") +
                      ((bool?) m["battle"] == true ? "  ·  in a battle" : "");
        var quest = S(m, "questNow");
        var step = S(m, "stepNow");
        var others = List(m["questList"]).Where(q => q != quest).ToList();
        _quest.Text = (quest.Length > 0 ? $"Quest now: {quest}" + (step.Length > 0 ? $"\nStep: {step}" : "") : "Quest now: none") +
                      (others.Count > 0 ? $"\nAlso holds: {string.Join(", ", others)}" : "");
        Lines(_stats, List(m["stats"]), "No stats yet (update the server to see them).");
        Lines(_gear, List(m["gear"]), "Nothing worn yet.");
        var size = (int?) m["deckSize"] ?? 0;
        var deckFor = S(m, "deckFor");
        _deckTitle.Text = "Deck" + (size > 0 ? $" ({size} cards)" : "") + (deckFor.Length > 0 ? $", made ready for {deckFor}" : "");
        if (CardThumb is not null && m["deckGrid"] is JsonArray) {
            // As pictures, like the game's deck window (Niduus, 2026-11-13); built again only when the deck changes, so
            // the 3-second refresh does not repaint the cards.
            var gridKey = Key(m) + "|" + m["deckGrid"]?.ToJsonString() + m["itemGrid"]?.ToJsonString() + m["treasureGrid"]?.ToJsonString() + m["deckMax"];
            if (gridKey != _gridKey) {
                _gridKey = gridKey;
                _deck.Children.Clear();
                _deck.Children.Add(DeckWindow(m));
            }
        }
        else {
            _gridKey = "";
            var cards = List(m["deckCards"]);
            if (cards.Count == 0 && S(m, "deck") is { Length: > 0 } deck) {
                cards = [.. deck.Split(", ", StringSplitOptions.RemoveEmptyEntries)];
            }

            Lines(_deck, cards, "Empty.");

            // Item cards and treasure cards under their own headings, as the game's deck window shows them.
            Group(_deck, "Item Cards", (int?) m["itemCount"] ?? 0, List(m["itemCards"]), "None from gear.");
            Group(_deck, "Treasure Cards", (int?) m["treasureCount"] ?? 0, List(m["treasureCards"]), "None.");
        }

        _spells.Text = List(m["spells"]) is { Count: > 0 } spells ? string.Join(", ", spells) : "None yet.";
    }

    // The game's deck window: tan slots with a gold edge in a blue frame, 8 to a row.
    private const int COLUMNS = 8;
    private const double SLOT = 44;
    private static readonly IBrush s_frame = new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x8C));
    private static readonly IBrush s_slot = new SolidColorBrush(Color.FromRgb(0xC8, 0xA8, 0x6E));
    private static readonly IBrush s_slotEdge = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x2A));
    private static readonly IBrush s_itemSlot = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0x98));
    private static readonly IBrush s_cross = new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x18));

    private Control DeckWindow(JsonObject m) {
        static List<(uint Id, string School, int N, bool Off)> Cards(JsonNode? node)
            => node is JsonArray array
                ? [.. array.OfType<JsonObject>().Select(c => ((uint?) c["id"] ?? 0, (string?) c["school"] ?? "", (int?) c["n"] ?? 1, (bool?) c["off"] == true))]
                : [];

        var deck = Cards(m["deckGrid"]);
        var items = Cards(m["itemGrid"]);
        var treasure = Cards(m["treasureGrid"]);
        var max = (int?) m["deckMax"] ?? 0;

        // The deck: one slot per copy, then the empty slots up to what the deck holds.
        var copies = deck.SelectMany(c => Enumerable.Repeat(c, Math.Max(1, c.N))).ToList();
        var deckGrid = Slots(copies.Select(c => Card(c.Id, c.School, 0, false)), Math.Max(copies.Count, max), s_slot);

        // The item cards the gear gives, one slot per copy; a red X on the ones turned off. At least two rows, as the game shows.
        var itemSlots = Math.Max(COLUMNS * 2, (items.Count + COLUMNS - 1) / COLUMNS * COLUMNS);
        var itemGrid = Slots(items.Select(c => Card(c.Id, c.School, 0, c.Off)), itemSlots, s_itemSlot);
        var off = items.Count(c => c.Off);

        var window = new StackPanel {
            Spacing = 6,
            Children = {
                Frame(deckGrid),
                Text($"Item Cards ({items.Count}" + (off > 0 ? $", {off} turned off" : "") + ")", strong: true),
                Frame(itemGrid),
            },
        };

        // Treasure cards: one slot per card, with how many it has.
        window.Children.Add(Text($"Treasure Cards ({treasure.Sum(c => c.N)})", strong: true));
        if (treasure.Count == 0) {
            window.Children.Add(Text("None."));
        }
        else {
            window.Children.Add(Frame(Slots(treasure.Select(c => Card(c.Id, c.School, c.N, false)), treasure.Count, s_slot)));
        }

        window.Children.Add(new TextBlock {
            Text = "Point at a card to see it big. A red X is an item card the wizard turned off, as you do by clicking it in the game.",
            Foreground = ManagerTheme.MUTED_BRUSH, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });

        return window;
    }

    private static Border Frame(Control content) => new() {
        Background = s_frame, CornerRadius = new CornerRadius(4), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left,
        Child = content,
    };

    private static UniformGrid Slots(IEnumerable<Control> cards, int slots, IBrush empty) {
        var grid = new UniformGrid { Columns = COLUMNS };
        var count = 0;
        foreach (var card in cards) {
            grid.Children.Add(Slot(card, empty));
            count++;
        }

        for (; count < slots || count % COLUMNS != 0; count++) {
            grid.Children.Add(Slot(null, empty));
        }

        return grid;
    }

    private static Border Slot(Control? card, IBrush background) => new() {
        Width = SLOT, Height = SLOT, Margin = new Thickness(1), Background = background, BorderBrush = s_slotEdge,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), ClipToBounds = true,
        Child = card,
    };

    private Control Card(uint id, string school, int count, bool off) {
        // The top of the card (its picture) fills the slot, as the game's deck shows small cards.
        var thumb = CardThumb!(id, school, SLOT - 4, count);
        thumb.HorizontalAlignment = HorizontalAlignment.Center;
        thumb.VerticalAlignment = VerticalAlignment.Top;
        if (!off) {
            return thumb;
        }

        var cross = new Grid { Children = { thumb } };
        foreach (var angle in new[] { 45.0, -45.0 }) {
            cross.Children.Add(new Border {
                Width = SLOT * 1.1, Height = 6, Background = s_cross, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(angle), IsHitTestVisible = false,
            });
        }

        return cross;
    }

    private static string Key(JsonObject m) => Or(S(m, "role"), S(m, "name"));

    private static void Group(StackPanel panel, string title, int count, List<string> lines, string none) {
        var header = Text(title + (count > 0 ? $" ({count} cards)" : ""), strong: true);
        header.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(header);
        foreach (var line in lines.Count == 0 ? [none] : lines) {
            panel.Children.Add(Text(line));
        }
    }

    private static void Lines(StackPanel panel, List<string> lines, string none) {
        panel.Children.Clear();
        foreach (var line in lines.Count == 0 ? [none] : lines) {
            panel.Children.Add(Text(line));
        }
    }

    private static Control Section(string? title, Control content, int row, int column, TextBlock? header = null) {
        var box = new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel { Spacing = 6, Children = { header ?? Text(title ?? "", strong: true), content } },
        };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column);

        return box;
    }

    private static TextBlock Text(string text = "", bool strong = false, double size = 0) {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal };
        if (size > 0) {
            block.FontSize = size;
        }

        return block;
    }

    private static string S(JsonObject m, string key) => (string?) (m[key] as JsonValue) ?? "";

    private static string Or(string text, string fallback) => text.Length > 0 ? text : fallback;

    private static List<string> List(JsonNode? node)
        => node is JsonArray array ? [.. array.Select(n => (string?) n).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)] : [];

}
