/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Ultimus Spectator add-on's data (Niduus, 2026-11-13: "Spectate Ultimus, able to view his location and quests via
/// Minimap (Required) and when in battle, able to see his spells in a new UI module. Ultimus Spectator requires Minimap, but
/// doesn't Override PLAYER Minimap. It allows it's own Ultimus Minimap and will always show the Quest Tracking of Ultimus'
/// Quests and NOT the players"). Ultimus is the same on every game window, so his map is kept once, under its own key
/// (<see cref="KEY"/>) beside the player's maps; the player's minimap and Quest Tracker are never touched.
/// </summary>
internal static class OverlayUltimusData {

    /// <summary>Where Ultimus's map, and his quest's way, are kept in the minimap's and Quest Tracker's data (never a game window).</summary>
    internal const long KEY = long.MinValue + 77;

    internal sealed record Step(string Text, string Where, bool Here);

    internal sealed record Card(string Name, string School, int Rank, bool Castable, bool Picked);

    internal sealed record Fighter(string Name, int Health, int Most, bool Alive);

    internal sealed record Battle(int Round, int Health, int Most, int Pips, int Power, int Shadow, IReadOnlyList<Card> Hand, string Pick, string Why,
                                  IReadOnlyList<string> Discards, IReadOnlyList<string> Casts, IReadOnlyList<Fighter> Enemies, IReadOnlyList<Fighter> Allies,
                                  string Deck);

    internal sealed record Info(string Name, int Level, int Health, int Most, string Place, string Doing, string Quest, string QuestTitle, string Kind,
                                string NextStep, IReadOnlyList<Step> Steps, IReadOnlyList<string> Way, IReadOnlyList<string> Others, Battle? Fight,
                                string Note, DateTime At) {

        internal bool Stale => DateTime.UtcNow - At > TimeSpan.FromSeconds(8);

    }

    private static readonly object s_lock = new();
    private static Info? s_info;
    private static string s_said = "";
    private static int s_version;
    private static volatile bool s_wanted;

    /// <summary>Whether this overlay shows any Ultimus Spectator piece (with the Minimap it needs): the link then asks for Ultimus.</summary>
    internal static bool Wanted {
        get => s_wanted;
        set => s_wanted = value;
    }

    /// <summary>Changes when what the quest box or the spells panel shows changes (the overlay is laid out again then).</summary>
    internal static int Version {
        get {
            lock (s_lock) {
                return s_version;
            }
        }
    }

    /// <summary>Ultimus as last heard, or (for the designers, window 0) a made-up Ultimus.</summary>
    internal static Info? For(long window) {
        if (window == 0) {
            return Sample;
        }

        lock (s_lock) {
            return s_info;
        }
    }

    /// <summary>The server's answer to op "ultimus".</summary>
    internal static void Heard(JsonObject answer) {
        // His map (with his quest's way) goes where the minimap keeps maps, under his own key.
        OverlayMinimapData.Heard(KEY, answer);
        Info info;
        string said;
        if (answer["ok"]?.GetValue<bool>() == true && answer["ultimus"] is JsonObject sent) {
            info = Read(sent);
            said = sent.ToJsonString();
            // His quest is always the one drawn on his map (never a quest the player picked): the way the server sent with his map.
            var drawn = answer["map"] is JsonObject map && map["route"] is JsonObject route ? (string?) route["quest"] : null;
            OverlayQuestData.Follow(KEY, string.IsNullOrEmpty(drawn) ? info.Quest : drawn);
        }
        else {
            var error = ((string?) answer["error"] ?? "").Trim();
            if (error == "busy") {
                return;
            }

            error = error.Length > 200 ? error[..200] : error;
            info = new Info("Ultimus", 0, 0, 0, "", "", "", "", "", "", [], [], [], null, error, DateTime.UtcNow);
            said = "error:" + error;
            OverlayQuestData.Follow(KEY, "");
        }

        lock (s_lock) {
            var wasStale = s_info?.Stale ?? true;
            s_info = info;
            if (said != s_said || wasStale) {
                s_said = said;
                s_version++;
            }
        }
    }

    /// <summary>Once a second: a feed that stopped coming is laid out again once, with a note.</summary>
    internal static void Tick() {
        lock (s_lock) {
            if (!s_wanted) {
                s_info = null;
                s_said = "";

                return;
            }

            if (s_info is { Stale: true } && s_said != "stale") {
                s_said = "stale";
                s_version++;
            }
        }
    }

    private static Info Read(JsonObject sent) {
        var steps = (sent["steps"] as JsonArray ?? []).OfType<JsonObject>().Take(6)
            .Select(s => new Step(Short((string?) s["s"], 120), Short((string?) s["where"], 80), s["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)).ToList();
        var way = Texts(sent["way"], 12, 80);
        var others = (sent["quests"] as JsonArray ?? []).OfType<JsonObject>().Take(40)
            .Where(q => !string.Equals((string?) q["id"], (string?) sent["quest"], StringComparison.OrdinalIgnoreCase))
            .Select(q => Short((string?) q["t"], 120)).Where(t => t.Length > 0).ToList();
        Battle? battle = null;
        if (sent["battle"] is JsonObject fight) {
            var hand = (fight["hand"] as JsonArray ?? []).OfType<JsonObject>().Take(12)
                .Select(c => new Card(Short((string?) c["n"], 60), Short((string?) c["s"], 12), (int) Number(c["r"]),
                    c["ok"]?.GetValueKind() == System.Text.Json.JsonValueKind.True, c["pick"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)).ToList();
            battle = new Battle((int) Number(fight["round"]), (int) Number(fight["hp"]), (int) Number(fight["max"]), (int) Number(fight["pips"]),
                (int) Number(fight["power"]), (int) Number(fight["shadow"]), hand, Short((string?) fight["pick"], 120), Short((string?) fight["why"], 160),
                Texts(fight["discards"], 8, 60), Texts(fight["casts"], 8, 140), Fighters(fight["enemies"]), Fighters(fight["allies"]),
                Short((string?) fight["deck"], 400));
        }

        return new Info(Short((string?) sent["name"], 40) is { Length: > 0 } name ? name : "Ultimus", (int) Number(sent["level"]), (int) Number(sent["hp"]),
            (int) Number(sent["max"]), Short((string?) sent["place"], 80), Short((string?) sent["doing"], 120), Short((string?) sent["quest"], 120),
            Short((string?) sent["questTitle"], 120), Short((string?) sent["kind"], 10), Short((string?) sent["step"], 160), steps, way, others, battle, "",
            DateTime.UtcNow);
    }

    private static List<Fighter> Fighters(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Take(8)
        .Select(f => new Fighter(Short((string?) f["n"], 60), (int) Number(f["hp"]), (int) Number(f["max"]), f["alive"]?.GetValueKind() != System.Text.Json.JsonValueKind.False))
        .ToList();

    private static List<string> Texts(JsonNode? node, int count, int max) => (node as JsonArray ?? []).Take(count)
        .Select(t => Short(t?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?) t : "", max)).Where(t => t.Length > 0).ToList();

    private static double Number(JsonNode? node) {
        try {
            var value = node?.GetValue<double>() ?? 0;

            return double.IsFinite(value) ? value : 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
            return 0;
        }
    }

    private static string Short(string? text, int max) {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

        return text.Length > max ? text[..max] : text;
    }

    // A made-up Ultimus for the designers, so the pieces can be placed and styled before he is watched.
    private static readonly Info Sample = new("Ultimus", 9, 612, 780, "Unicorn Way", "walking to Lost Souls", "sample-main", "Unicorn Way Ghosts", "main",
        "defeat 3 Lost Souls", [new Step("Defeat 3 Lost Souls", "Wizard City / Triton Avenue", false)], ["Ravenwood", "Triton Avenue"],
        ["Lost Souls", "Trouble in the Commons"],
        new Battle(3, 612, 780, 3, 1, 0, [
            new Card("Scarab", "Balance", 1, true, false), new Card("Scorpion", "Balance", 2, true, true), new Card("Sandstorm", "Balance", 3, false, false),
            new Card("Spirit Blade", "Balance", 0, true, false), new Card("Weakness", "Balance", 0, true, false), new Card("Fairy", "Life", 3, false, false),
        ], "Scorpion on Lost Soul", "the weakest enemy: Lost Soul with Scorpion (about 145)", ["Elemental Shield"],
            ["Round 3: casts Scorpion on Lost Soul", "Round 2: casts Spirit Blade on himself", "Round 1: passes"],
            [new Fighter("Lost Soul", 120, 300, true), new Fighter("Lost Soul", 300, 300, true)], [], "Scarab x4, Scorpion x3, Sandstorm x2"),
        "", DateTime.MaxValue);

}

/// <summary>
/// The Ultimus Spectator's pieces: Ultimus's own minimap (the player's Minimap's drawing, centred on him, with his quest's
/// way), his quest box, and his spells in battle. Only drawing: what they show comes from the server.
/// </summary>
internal static class UltimusViews {

    /// <summary>
    /// The Ultimus Spectator needs the Minimap add-on (Niduus: "Ultimus Spectator requires Minimap"). Without a Minimap on
    /// the overlay, each of its pieces says so instead of showing Ultimus.
    /// </summary>
    internal const string NEEDS_MINIMAP = "Ultimus Spectator needs the Minimap add-on on your overlay too. Put it on in Ahziael: Play > Overlay > Add-ons.";

    /// <summary>Ultimus's quest box: who he is, where, and his quest now with its step and way (never the player's quests).</summary>
    internal static Control Quest(OverlayElement e, long window, bool hasMap) {
        var panel = new StackPanel { Spacing = 2 };
        var look = Look(e);
        if (!hasMap) {
            panel.Children.Add(Line(NEEDS_MINIMAP, look.Size, look.Faint, wrap: true));

            return panel;
        }

        var info = OverlayUltimusData.For(window);
        if (Waiting(info) is { } waiting) {
            panel.Children.Add(Line("Ultimus", look.Size + 2, look.Gold, bold: true));
            panel.Children.Add(Line(waiting, look.Size - 1, look.Faint, wrap: true));

            return panel;
        }

        panel.Children.Add(Line($"{info!.Name}" + (info.Level > 0 ? $"  ·  level {info.Level}" : ""), look.Size + 2, look.Gold, bold: true));
        if (info.Most > 0) {
            panel.Children.Add(Bar(info.Health, info.Most, look));
        }

        if (info.Place.Length > 0) {
            panel.Children.Add(Line("In " + info.Place + (info.Fight is not null ? "  (in battle)" : ""), look.Size - 1, look.Text));
        }

        if (info.Doing.Length > 0) {
            panel.Children.Add(Line(Capital(info.Doing), look.Size - 2, look.Faint, wrap: true));
        }

        panel.Children.Add(new Border { Height = 4 });
        if (info.Quest.Length == 0) {
            panel.Children.Add(Line("No quest picked yet.", look.Size - 1, look.Faint));
        }
        else {
            var color = OverlayRenderer.Parse(new OverlayQuestList().ColorOf(info.Kind)) is { } kind ? new SolidColorBrush(kind) : look.Gold;
            panel.Children.Add(Line(info.QuestTitle.Length > 0 ? info.QuestTitle : info.Quest, look.Size + 1, color, bold: true, wrap: true));
            foreach (var step in info.Steps.Take(3)) {
                panel.Children.Add(Line("• " + step.Text + (step.Where.Length > 0 && !step.Here ? "  (" + step.Where + ")" : ""), look.Size - 1, look.Text, wrap: true));
            }

            if (info.Steps.Count == 0 && info.NextStep.Length > 0) {
                panel.Children.Add(Line("• " + Capital(info.NextStep), look.Size - 1, look.Text, wrap: true));
            }

            if (info.Way.Count > 0) {
                panel.Children.Add(Line("Way: " + string.Join(" > ", info.Way), look.Size - 2, look.Faint, wrap: true));
            }
        }

        if (info.Others.Count > 0) {
            panel.Children.Add(Line($"Also holds {info.Others.Count} other quest{(info.Others.Count == 1 ? "" : "s")}.", look.Size - 3, look.Faint));
        }

        return panel;
    }

    /// <summary>
    /// Ultimus's spells in battle: his hand (the card he picks lit up, the ones he can't cast yet faded), his pips and health,
    /// what he casts and why, what he threw away, his last rounds and the enemies. Over a game it shows only while he fights.
    /// </summary>
    internal static Control? Spells(OverlayElement e, long window, bool hasMap) {
        var panel = new StackPanel { Spacing = 2 };
        var look = Look(e);
        if (!hasMap) {
            panel.Children.Add(Line(NEEDS_MINIMAP, look.Size, look.Faint, wrap: true));

            return panel;
        }

        var info = OverlayUltimusData.For(window);
        if (info is null || info.Stale || info.Note.Length > 0 || info.Fight is not { } fight) {
            return null;
        }

        panel.Children.Add(Line($"{info.Name} in battle" + (fight.Round > 0 ? $"  ·  round {fight.Round}" : ""), look.Size + 2, look.Gold, bold: true));
        if (fight.Most > 0) {
            panel.Children.Add(Bar(fight.Health, fight.Most, look));
        }

        panel.Children.Add(Line(Pips(fight), look.Size - 1, look.Text));
        if (fight.Hand.Count > 0) {
            var hand = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var card in fight.Hand) {
                hand.Children.Add(CardChip(card, look));
            }

            panel.Children.Add(hand);
        }
        else {
            panel.Children.Add(Line("Waiting for his hand...", look.Size - 1, look.Faint));
        }

        if (fight.Pick.Length > 0) {
            panel.Children.Add(Line((fight.Pick == "passes" ? "Passes" : "Casts " + fight.Pick), look.Size, look.Gold, bold: true, wrap: true));
        }

        if (fight.Why.Length > 0) {
            panel.Children.Add(Line("Why: " + fight.Why, look.Size - 2, look.Faint, wrap: true));
        }

        if (fight.Discards.Count > 0) {
            panel.Children.Add(Line("Threw away: " + string.Join(", ", fight.Discards.Select(d => d.StartsWith("discards ", StringComparison.Ordinal) ? d[9..] : d)),
                look.Size - 2, look.Faint, wrap: true));
        }

        if (fight.Enemies.Count > 0) {
            panel.Children.Add(new Border { Height = 3 });
            foreach (var enemy in fight.Enemies) {
                panel.Children.Add(Line($"{enemy.Name}: " + (enemy.Alive ? $"{enemy.Health:N0}/{enemy.Most:N0}" : "defeated"), look.Size - 2,
                    enemy.Alive ? look.Text : look.Faint));
            }
        }

        if (fight.Casts.Count > 1) {
            panel.Children.Add(new Border { Height = 3 });
            foreach (var cast in fight.Casts.Skip(1).Take(4)) {
                panel.Children.Add(Line(cast, look.Size - 3, look.Faint, wrap: true));
            }
        }

        return panel;
    }

    private sealed record Looks(double Size, IBrush Text, IBrush Faint, IBrush Gold);

    // The piece's own words colour and size; the gold and faint lines follow from them.
    private static Looks Look(OverlayElement e) {
        var size = Math.Clamp(e.Size > 0 ? e.Size : 13, 8, 40);
        var text = OverlayRenderer.Brush(e.Fill) ?? Brushes.White;
        var faint = OverlayRenderer.Parse(e.Fill.Color1) is { } c ? new SolidColorBrush(c, 0.7) : new SolidColorBrush(Colors.White, 0.7);

        return new Looks(size, text, faint, new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)));
    }

    private static string? Waiting(OverlayUltimusData.Info? info) => info is null
        ? OverlayHost.Link.Connected
            ? OverlayHost.Link.SendsUltimus ? "Looking for Ultimus..." : "This server's Imlight is too old to watch Ultimus. The host needs to update it."
            : "Not linked to a server yet. Start the game with Play in Ahziael."
        : info.Note.Length > 0 ? info.Note
        : info.Stale ? "Ultimus stopped answering for now..." : null;

    private static string Pips(OverlayUltimusData.Battle fight) {
        var words = $"Pips: {fight.Pips}";
        if (fight.Power > 0) {
            words += $" + {fight.Power} power";
        }

        if (fight.Shadow > 0) {
            words += $" + {fight.Shadow} shadow";
        }

        return words;
    }

    private static Control CardChip(OverlayUltimusData.Card card, Looks look) {
        var school = SchoolColor(card.School);
        return new Border {
            Background = new SolidColorBrush(card.Picked ? Color.FromArgb(0xE0, 0x5A, 0x46, 0x10) : Color.FromArgb(0xB0, 0x14, 0x18, 0x2A)),
            BorderBrush = new SolidColorBrush(card.Picked ? Color.FromRgb(0xFF, 0xD2, 0x4A) : school),
            BorderThickness = new Thickness(card.Picked ? 2 : 1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Opacity = card.Castable || card.Picked ? 1 : 0.45,
            Child = new TextBlock {
                Text = (card.Picked ? "▶ " : "") + card.Name + (card.Rank > 0 ? $" ({card.Rank})" : ""),
                FontSize = Math.Max(8, look.Size - 2), FontWeight = card.Picked ? FontWeight.Bold : FontWeight.Normal, Foreground = look.Text,
            },
        };
    }

    private static Color SchoolColor(string school) => school.ToLowerInvariant() switch {
        "fire" => Color.FromRgb(0xFF, 0x6A, 0x3A),
        "ice" => Color.FromRgb(0x7A, 0xD0, 0xFF),
        "storm" => Color.FromRgb(0xB0, 0x7A, 0xFF),
        "myth" => Color.FromRgb(0xFF, 0xD8, 0x4A),
        "life" => Color.FromRgb(0x6A, 0xE0, 0x6A),
        "death" => Color.FromRgb(0xB8, 0xB8, 0xB8),
        "balance" => Color.FromRgb(0xE0, 0x9A, 0x50),
        _ => Color.FromRgb(0x90, 0x90, 0xA0),
    };

    private static Control Bar(int health, int most, Looks look) {
        var share = Math.Clamp((double) health / Math.Max(1, most), 0, 1);
        var color = share > 0.6 ? Color.FromRgb(0x5A, 0xD0, 0x6A) : share > 0.3 ? Color.FromRgb(0xFF, 0xC8, 0x3C) : Color.FromRgb(0xFF, 0x50, 0x50);
        var grid = new Grid { Height = Math.Max(10, look.Size), Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.001, share), GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.001, 1 - share), GridUnitType.Star)));
        var back = new Border { Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(back, 2);
        grid.Children.Add(back);
        grid.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(4) });
        var words = new TextBlock {
            Text = $"{health:N0} / {most:N0}", FontSize = Math.Max(8, look.Size - 3), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumnSpan(words, 2);
        grid.Children.Add(words);

        return grid;
    }

    private static TextBlock Line(string text, double size, IBrush brush, bool bold = false, bool wrap = false) => new() {
        Text = text, FontSize = Math.Max(8, size), Foreground = brush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

}
