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

    private readonly WrapPanel _picker = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
    private readonly Dictionary<string, ToggleButton> _buttons = new(StringComparer.Ordinal);
    private readonly TextBlock _title = Text(strong: true, size: 16);
    private readonly TextBlock _where = Text();
    private readonly TextBlock _quest = Text();
    private readonly StackPanel _stats = new() { Spacing = 2 };
    private readonly StackPanel _gear = new() { Spacing = 2 };
    private readonly TextBlock _deckTitle = Text(strong: true);
    private readonly StackPanel _deck = new() { Spacing = 2 };
    private readonly StackPanel _spells = new() { Spacing = 4 };
    private readonly TextBlock _spellsTitle = Text("Trained spells", strong: true);
    private string _spellsKey = "";
    private readonly TextBlock _empty = Text();
    private readonly XpBar _xp = new();
    private readonly Control _xpRow;
    private readonly QuestBookView _book = new();
    private readonly Expander _bookBox;
    private string _xpFor = "";
    private readonly Control _body;
    private string _picked = "";
    private JsonObject? _shown;
    private string _gridKey = "";

    /// <summary>
    /// Paints a spell's card (spell id, school, width, count; 0 = no count) as the game draws it. Set by the window; without
    /// it the deck shows as lines of text.
    /// </summary>
    internal Func<uint, string, double, int, Control>? CardThumb { get; set; }

    /// <summary>A stat icon by its key ("health", "critical", ...) with its tip and size: the game's own picture when found.</summary>
    internal Func<string, string, double, Control>? StatIcon { get; set; }

    /// <summary>A school's icon at a size.</summary>
    internal Func<string, double, Control>? SchoolIcon { get; set; }

    /// <summary>A gear slot's icon ("Hat", "Robe", ... "Deck") at a size, as the game's backpack tabs show them.</summary>
    internal Func<string, double, Control>? SlotIcon { get; set; }

    /// <summary>A portrait by name at a size for the quest book's cards (the window's drawn one); initials without it.</summary>
    internal Func<string, double, Control>? Portrait {
        get => _book.Portrait;
        set => _book.Portrait = value;
    }

    /// <summary>
    /// The quest book starts closed (the Quest Team page: Niduus, 2026-11-13, "Both XP and Quest are to be shared in a more
    /// compact manor for QT as that's based on 4 characters and that would clutter the page. Maybe a drop down section").
    /// </summary>
    internal bool Compact {
        get => !_bookBox.IsExpanded;
        set => _bookBox.IsExpanded = !value;
    }

    private string _statsKey = "";
    private string _gearKey = "";

    internal Control View { get; }

    internal WizardSheet(string emptyText) {
        _empty.Text = emptyText;
        _empty.Foreground = ManagerTheme.MUTED_BRUSH;
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        columns.Children.Add(Section("Stats", _stats, 0, 0));
        columns.Children.Add(Section("Gear", _gear, 0, 2));
        var deck = Section(null, _deck, 1, 0, _deckTitle);
        columns.Children.Add(deck);
        columns.Children.Add(Section(null, _spells, 1, 2, _spellsTitle));
        // The XP bar under the name, as the game shows it at the bottom of the screen (Niduus, 2026-11-13), and the quest
        // book under the rest, a drop-down that opens as the game's "All Current Quests" page.
        var xpLabel = Text("XP", strong: true);
        xpLabel.VerticalAlignment = VerticalAlignment.Center;
        _xp.MaxWidth = 560;
        _xp.HorizontalAlignment = HorizontalAlignment.Left;
        _xpRow = new DockPanel { Children = { WithDock(xpLabel, Dock.Left, new Thickness(0, 0, 10, 0)), _xp } };
        _bookBox = new Expander {
            Header = Text("Quests", strong: true), IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _book,
        };
        _body = new StackPanel { Spacing = 10, Children = { _title, _where, _xpRow, _quest, columns, _bookBox } };
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
        _spellsKey = "";
        _statsKey = "";
        _gearKey = "";
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
        // The XP bar, live: the numbers slide along as the game's bar fills (a new wizard shown starts it where it is).
        _xpRow.IsVisible = m["xpMax"] is not null;
        if (Key(m) != _xpFor) {
            _xpFor = Key(m);
            _xp.Reset();
        }

        _xp.Set((int?) m["xp"] ?? 0, (int?) m["xpMax"] ?? 0, (int?) m["level"] ?? 0);
        _bookBox.IsVisible = m["questBook"] is JsonArray;
        _book.Show(m["questBook"]);

        var quest = S(m, "questNow");
        var step = S(m, "stepNow");
        var others = List(m["questList"]).Where(q => q != quest).ToList();
        _quest.Text = (quest.Length > 0 ? $"Quest now: {quest}" + (step.Length > 0 ? $"\nStep: {step}" : "") : "Quest now: none") +
                      (others.Count > 0 ? $"\nAlso holds: {string.Join(", ", others)}" : "");
        // With the game's icons when the window gives them and the server sends the rows (Niduus, 2026-11-13); built again
        // only when they change.
        if (StatIcon is not null && m["statRows"] is JsonArray statRows && statRows.Count > 0) {
            var key = Key(m) + statRows.ToJsonString();
            if (key != _statsKey) {
                _statsKey = key;
                _stats.Children.Clear();
                _stats.Children.Add(StatPanel(statRows));
            }
        }
        else {
            _statsKey = "";
            Lines(_stats, List(m["stats"]), "No stats yet (update the server to see them).");
        }

        if (SlotIcon is not null && m["gearRows"] is JsonArray gearRows && gearRows.Count > 0) {
            var key = Key(m) + gearRows.ToJsonString();
            if (key != _gearKey) {
                _gearKey = key;
                _gear.Children.Clear();
                foreach (var row in gearRows.OfType<JsonObject>()) {
                    _gear.Children.Add(GearRow(row));
                }
            }
        }
        else {
            _gearKey = "";
            Lines(_gear, List(m["gear"]), "Nothing worn yet.");
        }
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

        ShowSpells(m);
    }

    // The game's stat tooltip: yellow words on a dark blue panel, the icons beside the numbers.
    private static readonly IBrush s_tipPanel = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x3C));
    private static readonly IBrush s_tipYellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0x34));

    private Control StatPanel(JsonArray rows) {
        var list = new StackPanel { Spacing = 3 };
        foreach (var row in rows.OfType<JsonObject>()) {
            var words = S(row, "words");
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            line.Children.Add(new TextBlock {
                Text = S(row, "value"), Foreground = s_tipYellow, FontWeight = FontWeight.Bold, FontSize = 15, MinWidth = 64,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            });
            if (S(row, "school") is { Length: > 0 } school && SchoolIcon is not null) {
                line.Children.Add(SchoolIcon(school, 20));
            }

            foreach (var icon in List(row["icons"])) {
                line.Children.Add(StatIcon!(icon, words, 22));
            }

            if (S(row, "after") is { Length: > 0 } after) {
                line.Children.Add(new TextBlock {
                    Text = after, Foreground = s_tipYellow, FontWeight = FontWeight.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
                });
            }

            line.Children.Add(new TextBlock {
                Text = words, Foreground = Brushes.White, Opacity = 0.75, FontSize = 12, Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            ToolTip.SetTip(line, words);
            list.Children.Add(line);
        }

        return new Border {
            Background = s_tipPanel, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6), BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x9A, 0x3A)), HorizontalAlignment = HorizontalAlignment.Left,
            Child = list,
        };
    }

    private Control GearRow(JsonObject row) {
        var slot = S(row, "slot");
        var name = S(row, "name");
        var level = (int?) row["level"] ?? 0;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(SlotIcon!(slot, 32));
        line.Children.Add(new StackPanel {
            VerticalAlignment = VerticalAlignment.Center,
            Children = {
                Text(name.Length > 0 ? name : "Nothing", strong: name.Length > 0),
                new TextBlock { Text = slot + (level > 0 ? $"  ·  level {level}" : ""), Foreground = ManagerTheme.MUTED_BRUSH, FontSize = 11 },
            },
        });
        if (name.Length == 0) {
            line.Opacity = 0.55;
        }

        return line;
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

    // The trained spells beside the deck, as the game's spellbook (Niduus, 2026-11-13: "Include a Trained Spell Section next
    // to this"): the cards it can put in a deck by school, the number on a card how many copies are in the deck now, the ones
    // left out faded; then its other spells (fishing, gardening, travel) by name. Built again only when they change.
    private void ShowSpells(JsonObject m) {
        var grid = m["spellGrid"] as JsonArray;
        var others = List(m["spellsOther"]);
        var key = Key(m) + "|" + grid?.ToJsonString() + "|" + string.Join(",", others) + "|" + m["otherGrid"]?.ToJsonString() + "|" + (CardThumb is null);
        if (key == _spellsKey) {
            return;
        }

        _spellsKey = key;
        _spells.Children.Clear();
        var cards = (grid ?? []).OfType<JsonObject>()
            .Select(c => (Id: (uint?) c["id"] ?? 0, School: (string?) c["school"] ?? "", N: (int?) c["n"] ?? 0, Name: (string?) c["name"] ?? ""))
            .Where(c => c.Id != 0).ToList();
        if (grid is null) {
            // An older server: the names only.
            _spellsTitle.Text = "Trained spells";
            _spells.Children.Add(Text(List(m["spells"]) is { Count: > 0 } names ? string.Join(", ", names) : "None yet."));

            return;
        }

        var inDeck = cards.Count(c => c.N > 0);
        var copies = cards.Sum(c => c.N);
        _spellsTitle.Text = $"Trained spells ({cards.Count} deck card{(cards.Count == 1 ? "" : "s")} known, {inDeck} used, {copies} copies in the deck)";
        if (cards.Count == 0) {
            _spells.Children.Add(Text("None yet."));
        }

        foreach (var school in cards.GroupBy(c => c.School.Length > 0 ? c.School : "Other")) {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (SchoolIcon is not null && school.Key != "Other") {
                header.Children.Add(SchoolIcon(school.Key, 16));
            }

            header.Children.Add(Text($"{school.Key} ({school.Count()})", strong: true));
            _spells.Children.Add(header);
            if (CardThumb is null) {
                foreach (var card in school) {
                    _spells.Children.Add(Text(card.N > 0 ? $"{card.Name} (x{card.N} in deck)" : card.Name));
                }

                continue;
            }

            var slots = new UniformGrid { Columns = COLUMNS, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var card in school) {
                var thumb = Card(card.Id, card.School, card.N, false);
                thumb.Opacity = card.N > 0 ? 1 : 0.5;
                var slot = Slot(thumb, s_slot);
                BigCardTip(slot, card.Id, card.School, card.N,
                    card.N > 0 ? $"{card.Name}: {card.N} {(card.N == 1 ? "copy" : "copies")} in the deck" : $"{card.Name}: trained, not in the deck");
                slots.Children.Add(slot);
            }

            _spells.Children.Add(Frame(slots));
        }

        if (others.Count > 0) {
            var header = Text($"Other spells ({others.Count})", strong: true);
            header.Margin = new Thickness(0, 6, 0, 0);
            _spells.Children.Add(header);
            // Each name its own word, so pointing at it shows the spell's card (Niduus, 2026-11-13: "hover overs do not work here").
            var ids = (m["otherGrid"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => (Name: (string?) c["name"] ?? "", Id: (uint?) c["id"] ?? 0, School: (string?) c["school"] ?? ""))
                .Where(c => c.Name.Length > 0).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            var words = new WrapPanel();
            for (var i = 0; i < others.Count; i++) {
                var word = new TextBlock {
                    Text = others[i] + (i < others.Count - 1 ? ", " : ""), Foreground = ManagerTheme.MUTED_BRUSH, Background = Brushes.Transparent,
                };
                if (ids.TryGetValue(others[i], out var spell) && spell.Id != 0) {
                    BigCardTip(word, spell.Id, spell.School, 0, others[i]);
                }
                else {
                    ToolTip.SetTip(word, others[i]);
                }

                words.Children.Add(word);
            }

            _spells.Children.Add(words);
        }

        _spells.Children.Add(new TextBlock {
            Text = "Point at a card or a name to see it big, with how many copies are in the deck. Faded cards are trained but left out.",
            Foreground = ManagerTheme.MUTED_BRUSH, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
    }

    // Pointing at a card or a spell's name shows its card big with a line under it, made when first pointed at.
    private void BigCardTip(Control target, uint id, string school, int count, string line) {
        ToolTip.SetShowDelay(target, 150);
        ToolTip.SetTip(target, line);
        target.PointerEntered += (_, _) => {
            if (ToolTip.GetTip(target) is Control || CardThumb is null) {
                return;
            }

            ToolTip.SetTip(target, new StackPanel { Spacing = 4, Children = { CardThumb(id, school, 200, count), Text(line, strong: true) } });
        };
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

    private static T WithDock<T>(T control, Dock dock, Thickness margin) where T : Control {
        DockPanel.SetDock(control, dock);
        control.Margin = margin;

        return control;
    }

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
