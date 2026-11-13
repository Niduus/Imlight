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
/// health, mana, questNow, stepNow, questList[], stats[], gear[], deckCards[], deckSize, deckFor and spells[] (all optional;
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

    private void Fill(JsonObject m) {
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
        var cards = List(m["deckCards"]);
        if (cards.Count == 0 && S(m, "deck") is { Length: > 0 } deck) {
            cards = [.. deck.Split(", ", StringSplitOptions.RemoveEmptyEntries)];
        }

        Lines(_deck, cards, "Empty.");
        _spells.Text = List(m["spells"]) is { Count: > 0 } spells ? string.Join(", ", spells) : "None yet.";
    }

    private static string Key(JsonObject m) => Or(S(m, "role"), S(m, "name"));

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
