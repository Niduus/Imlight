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
/// A server-played wizard's quests laid out like the game's "All Current Quests" book (Niduus, 2026-11-13: "a quest page
/// ... that lays them out as they are in game"): two columns of parchment cards, four to a page with red arrows to turn
/// it. Each card has the quest's red title banner (with gold stars for a main quest), the step's verb over a round
/// portrait, the "x of y" count, the name, the world and area on a scroll, and the rewards along a red footer. The quest
/// played now is lit yellow, as the game lights the tracked one. Fed by the "questBook" key of BotSheet.Fill.
/// </summary>
internal sealed class QuestBookView : StackPanel {

    private const int PER_PAGE = 4;

    private static readonly IBrush s_cover = new SolidColorBrush(Color.FromRgb(0x5A, 0x3A, 0x1E));
    private static readonly IBrush s_parchment = new SolidColorBrush(Color.FromRgb(0xEE, 0xE0, 0xBC));
    private static readonly IBrush s_lit = new SolidColorBrush(Color.FromRgb(0xF2, 0xE6, 0x8A));
    private static readonly IBrush s_edge = new SolidColorBrush(Color.FromRgb(0x9A, 0x7A, 0x48));
    private static readonly IBrush s_banner = new SolidColorBrush(Color.FromRgb(0x8E, 0x10, 0x14));
    private static readonly IBrush s_bannerEdge = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x3C));
    private static readonly IBrush s_titleGold = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x30));
    private static readonly IBrush s_scroll = new SolidColorBrush(Color.FromRgb(0xE2, 0xCC, 0x98));
    private static readonly IBrush s_ink = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x0A));
    private static readonly IBrush s_star = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x1E));
    private static readonly IBrush s_coin = new SolidColorBrush(Color.FromRgb(0xD8, 0xA8, 0x18));
    private static readonly IBrush s_cap = new SolidColorBrush(Color.FromRgb(0x2C, 0x8C, 0x8C));
    private static readonly IBrush s_gem = new SolidColorBrush(Color.FromRgb(0x8C, 0x7C, 0xD8));

    private readonly Grid _pageGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,12,*"), RowDefinitions = new RowDefinitions("Auto,12,Auto") };
    private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
    private readonly Button _back;
    private readonly Button _next;
    private readonly TextBlock _none = new() { Text = "No quests held.", Foreground = Brushes.White, Margin = new Thickness(8) };
    private List<JsonObject> _cards = [];
    private int _page;
    private string _shownKey = "";

    /// <summary>A portrait for a name at a size (the window gives a drawn one); a disc with initials without it.</summary>
    internal Func<string, double, Control>? Portrait { get; set; }

    internal QuestBookView() {
        Spacing = 6;
        _back = Arrow("◀", -1);
        _next = Arrow("▶", 1);
        var header = new DockPanel {
            Children = {
                WithDock(_back, Dock.Left),
                WithDock(_next, Dock.Right),
                new TextBlock {
                    Text = "ALL CURRENT QUESTS", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeight.Bold, FontSize = 16, Foreground = s_titleGold,
                },
            },
        };
        var book = new Border {
            Background = s_cover, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), BorderBrush = s_edge, BorderThickness = new Thickness(2),
            Child = new StackPanel {
                Spacing = 8,
                Children = { header, _none, _pageGrid, new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = _pageText } },
            },
        };
        Children.Add(book);
    }

    private Button Arrow(string glyph, int step) {
        var button = new Button {
            Content = glyph, Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(0),
            Background = s_banner, Foreground = s_titleGold, BorderBrush = s_bannerEdge, BorderThickness = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => {
            _page = Math.Clamp(_page + step, 0, Pages - 1);
            Draw();
        };

        return button;
    }

    private int Pages => Math.Max(1, (int) Math.Ceiling(_cards.Count / (double) PER_PAGE));

    /// <summary>The cards of "questBook" (null: none). Drawn again only when they change; the page stays where it was.</summary>
    internal void Show(JsonNode? book) {
        var key = book?.ToJsonString() ?? "";
        if (key == _shownKey) {
            return;
        }

        _shownKey = key;
        _cards = book is JsonArray array ? [.. array.OfType<JsonObject>()] : [];
        _page = Math.Clamp(_page, 0, Pages - 1);
        Draw();
    }

    private void Draw() {
        _pageGrid.Children.Clear();
        _none.IsVisible = _cards.Count == 0;
        _pageGrid.IsVisible = _cards.Count > 0;
        _back.IsEnabled = _page > 0;
        _next.IsEnabled = _page < Pages - 1;
        _pageText.Text = $"Page {_page + 1} of {Pages}";
        var shown = _cards.Skip(_page * PER_PAGE).Take(PER_PAGE).ToList();
        for (var i = 0; i < shown.Count; i++) {
            var card = Card(shown[i]);
            Grid.SetColumn(card, (i % 2) * 2);
            Grid.SetRow(card, (i / 2) * 2);
            _pageGrid.Children.Add(card);
        }
    }

    private Control Card(JsonObject q) {
        var main = (bool?) q["main"] == true;
        var active = (bool?) q["active"] == true;
        var body = new StackPanel { Spacing = 4, Margin = new Thickness(8, 6, 8, 0) };
        body.Children.Add(Banner(S(q, "title"), main));
        if (S(q, "verb") is { Length: > 0 } verb) {
            body.Children.Add(Ink(verb, 14, FontWeight.SemiBold));
        }

        var target = S(q, "target");
        var portrait = new Panel { HorizontalAlignment = HorizontalAlignment.Center };
        portrait.Children.Add(Portrait?.Invoke(target, 64) ?? Disc(target, 64));
        body.Children.Add(portrait);
        if ((int?) q["of"] is > 0 and var of) {
            body.Children.Add(Scroll($"{(int?) q["count"] ?? 0} of {of}", 12, narrow: true));
        }

        if (target.Length > 0) {
            body.Children.Add(Ink(target, 13));
        }

        var world = S(q, "world");
        var place = S(q, "place");
        if (world.Length > 0 || place.Length > 0) {
            body.Children.Add(Scroll(world.Length > 0 && place.Length > 0 ? $"{world}\n{place}" : world + place, 13));
        }

        var rewards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        if ((int?) q["gold"] is > 0 and var gold) {
            rewards.Children.Add(Reward(s_coin, "G", gold.ToString("N0"), "Gold"));
        }

        if ((int?) q["xp"] is > 0 and var xp) {
            rewards.Children.Add(Reward(s_cap, "XP", xp.ToString("N0"), "Experience"));
        }

        if ((int?) q["items"] is > 0) {
            rewards.Children.Add(Reward(s_gem, "◆", "", "An item reward"));
        }

        var footer = new Border {
            Background = s_banner, BorderBrush = s_bannerEdge, BorderThickness = new Thickness(0, 2, 0, 0), Padding = new Thickness(6, 6, 6, 8),
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 56, Child = rewards,
        };

        var card = new Border {
            Background = active ? s_lit : s_parchment, BorderBrush = active ? s_titleGold : s_edge, BorderThickness = new Thickness(active ? 3 : 2),
            CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Child = new DockPanel { Children = { WithDock(footer, Dock.Bottom), body } },
        };
        if (active) {
            ToolTip.SetTip(card, "The quest it is playing now");
        }

        return card;
    }

    private static Control Banner(string title, bool main) {
        var banner = new Border {
            Background = s_banner, BorderBrush = s_bannerEdge, BorderThickness = new Thickness(2), Padding = new Thickness(18, 3),
            Child = new TextBlock {
                Text = title.ToUpperInvariant(), Foreground = s_titleGold, FontWeight = FontWeight.Bold, FontSize = 13,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            },
        };
        if (!main) {
            return banner;
        }

        // A main quest's gold stars on both ends of its banner.
        return new Grid {
            Children = {
                banner,
                Star(HorizontalAlignment.Left),
                Star(HorizontalAlignment.Right),
            },
        };
    }

    private static TextBlock Star(HorizontalAlignment side) => new() {
        Text = "★", FontSize = 20, Foreground = s_star, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(-4, -10, -4, 0),
        Effect = new DropShadowEffect { BlurRadius = 2, OffsetX = 0, OffsetY = 1, Color = Colors.Black, Opacity = 0.8 },
    };

    private static TextBlock Ink(string text, double size, FontWeight weight = FontWeight.Normal) => new() {
        Text = text, FontSize = size, FontWeight = weight, Foreground = s_ink, HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
    };

    private static Border Scroll(string text, double size, bool narrow = false) => new() {
        Background = s_scroll, BorderBrush = s_edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(narrow ? 8 : 10),
        Padding = new Thickness(narrow ? 10 : 6, 2), HorizontalAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        Child = Ink(text, size),
    };

    private static Control Reward(IBrush color, string mark, string amount, string tip) {
        var icon = new Border {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = color, BorderBrush = s_bannerEdge, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock {
                Text = mark, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = mark.Length > 1 ? 11 : 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var stack = new StackPanel { Spacing = -4, Children = { icon } };
        if (amount.Length > 0) {
            stack.Children.Add(new Border {
                Background = s_scroll, BorderBrush = s_edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Center, Child = Ink(amount, 12, FontWeight.SemiBold),
            });
        }

        ToolTip.SetTip(stack, amount.Length > 0 ? $"{tip}: {amount}" : tip);

        return stack;
    }

    private static Control Disc(string name, double size) {
        var initials = string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));

        return new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x6A)),
            BorderBrush = s_bannerEdge, BorderThickness = new Thickness(3),
            Child = new TextBlock {
                Text = initials.Length > 0 ? initials : "?", Foreground = Brushes.White, FontSize = size * 0.34, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static T WithDock<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    private static string S(JsonObject m, string key) => (string?) (m[key] as JsonValue) ?? "";

}
