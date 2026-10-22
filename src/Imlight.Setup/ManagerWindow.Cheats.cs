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
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The Cheats section of the boss and custom editors (Niduus, 2026-10-21: "modify cheats like the spells that get
/// cast? Default if unchanged"; 2026-10-22: "Custom Bosses so cheats can have their conditions and such able to be
/// modified and created" and "'block'-like programming"). Each cheat is a script of snapped blocks: a When block, If
/// blocks and Do blocks, built from the palette at the side. The game's cheats can be changed, turned off or put back
/// and new ones added; they are saved with the boss's other changes and its cheat file is never changed.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _cheatRows = new() { Spacing = 10 };
    private readonly TextBlock _cheatNote = Sub("");
    private readonly Dictionary<CheatRule, (TextBlock Summary, TextBlock Marks)> _cheatHeads = [];
    private readonly HashSet<CheatRule> _cheatExpanded = [];

    private BossCheatList? _cheats;
    private CheatLines? _cheatLines;
    private CheatRule? _cheatFocus;
    private Section? _cheatsSection;

    private static readonly Regex s_sentence = new(@"\{(\w+)\}", RegexOptions.Compiled);

    private Control CheatsCard() {
        var palette = new BlockPalette(
            CheatSpecs.All.Where(s => s.Id is not ("W.Other" or "D.Other"))
                .Select(s => new PaletteEntry(s.Kind, s.Icon, s.Palette, s.Help, s)),
            entry => Guarded(() => AddFromPalette((CheatSpec) entry.Payload, null, -1)),
            "Cheat blocks") {
            Width = 236, MaxHeight = 760, VerticalAlignment = VerticalAlignment.Top,
        };

        var legend = new WrapPanel {
            Children = {
                Legend(BlockKind.When, "When… starts a cheat: what sets it off"),
                Legend(BlockKind.If, "If… only when something is true"),
                Legend(BlockKind.Do, "Do… what the boss does, top to bottom"),
            },
        };

        var right = new StackPanel {
            Spacing = 8,
            Children = {
                CheatsGuide(),
                legend,
                _cheatNote,
                _cheatRows,
                Row(Action(Primary("New cheat"), () => {
                    NewCheat(CheatSpecs.Get("W.RoundStart"));

                    return Task.CompletedTask;
                }), Action(Plain("Open all"), () => {
                    foreach (var rule in _cheats?.Cheats ?? []) {
                        _cheatExpanded.Add(rule);
                    }

                    RenderCheats();

                    return Task.CompletedTask;
                }), Action(Plain("Close all"), () => {
                    _cheatExpanded.Clear();
                    RenderCheats();

                    return Task.CompletedTask;
                }), Danger(Action(Plain("Put back the game's cheats"), ResetCheatsAsync))),
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(palette);
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(14, 0, 0, 0);
        grid.Children.Add(right);

        _cheatsSection = new Section("bolt", BlockColors.Accent("cheats"), "Cheats",
            "Moves the boss makes outside its turn. Each cheat reads as a sentence: When something happens, If it is true, Do these things. " +
            "Grey words in the list are the game's own cheats; yours show in orange. Press Save changes to keep them.", grid);

        return _cheatsSection;
    }

    private static Control Legend(BlockKind kind, string text) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 16, 4),
        Children = {
            new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = BlockColors.Fill(kind), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = 12, Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    private void Guarded(Action action) => _ = Guard(() => {
        action();

        return Task.CompletedTask;
    });

    private string CheatExtraFolder => Path.GetDirectoryName(_manager.BossChangesFolder) ?? "";

    /// <summary>Shows the boss's cheats: its cheat file with the saved changes laid over it.</summary>
    private void LoadBossCheats(BossInfo boss, BossChanges? changes) {
        _cheatLines ??= CheatLines.Load(_manager.IdLookupFolder);
        CheatSpecs.CreatureNames = id => _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == id.ToString(CultureInfo.InvariantCulture)) is { } c ? $"{c.Name} ({id})" : null;
        CheatSpecs.CustomNames = id => CustomMob.Load(_manager.CustomsFolder, id) is { } custom ? $"custom {id}, {custom.Shown}" : null;
        try {
            _cheats = BossCheatList.Load(CheatExtraFolder, boss.Id, changes?.Cheats);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _cheats = new BossCheatList();
            Say($"The cheat file of {boss.Name} could not be read: {ex.Message}");
        }

        _cheatExpanded.Clear();
        _cheatFocus = null;
        RenderCheats();
        EnsureGameIcons(() => {
            if (_cheats is not null) {
                RenderCheats();
            }
        });
    }

    private void RenderCheats() {
        _cheatRows.Children.Clear();
        _cheatHeads.Clear();
        if (_cheats is null) {
            return;
        }

        var notes = new List<string>();
        if (!_cheats.HasFile) {
            notes.Add("This boss has no cheats in the game. Make one with New cheat, or click a When block in the palette.");
        }

        if (_cheats.Lost > 0) {
            notes.Add($"{_cheats.Lost} saved change(s) are for cheats the game's cheat file no longer has, so they are left out. " +
                      "Save changes to clear them.");
        }

        _cheatNote.Text = string.Join(" ", notes);
        _cheatNote.IsVisible = notes.Count > 0;
        for (var i = 0; i < _cheats.Cheats.Count; i++) {
            _cheatRows.Children.Add(CheatCard(_cheats.Cheats[i], i + 1));
        }

        if (_cheats.Cheats.Count == 0) {
            _cheatRows.Children.Add(Sub(_cheats.HasFile ? "Its cheat file has no cheats." : "No cheats yet."));
        }

        var changed = _cheats.Cheats.Count(c => c.IsChanged);
        _cheatsSection?.SetBadge(_cheats.Cheats.Count == 0 ? "" : $"{_cheats.Cheats.Count} cheats" + (changed > 0 ? $", {changed} yours" : ""),
            changed > 0 ? WARN : null);
    }

    // One cheat: its number, its sentence and buttons; opened, its blocks.
    private Control CheatCard(CheatRule rule, int number) {
        var open = _cheatExpanded.Contains(rule);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var marks = new TextBlock { Foreground = WARN, FontSize = 12 };
        _cheatHeads[rule] = (summary, marks);
        UpdateCheatHead(rule);

        Button Tool(string icon, string tip, Action click, bool enabled = true) {
            var button = new Button { Content = BlockIcons.Icon(icon, 15, Brushes.White), Padding = new Thickness(6), IsEnabled = enabled };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Guarded(click);

            return button;
        }

        var list = _cheats!.Cheats;
        var at = list.IndexOf(rule);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Action(open ? Plain("Close") : Primary("Edit blocks"), () => {
            if (!_cheatExpanded.Remove(rule)) {
                _cheatExpanded.Add(rule);
                _cheatFocus = rule;
            }

            RenderCheats();

            return Task.CompletedTask;
        }));
        tools.Children.Add(Tool(rule.Off ? "eyeoff" : "eye", rule.Off ? "Turn this cheat back on" : "Turn this cheat off (it stays here)", () => {
            rule.Off = !rule.Off;
            RenderCheats();
        }));
        tools.Children.Add(Tool("copy", "Make a copy of this cheat, as a new one of yours", () => {
            var copy = rule.Copy();
            list.Add(copy);
            _cheatExpanded.Add(copy);
            _cheatFocus = copy;
            RenderCheats();
            Say("Copied. The copy is at the bottom of the list.");
        }));
        // The game's cheats keep their places (the server finds a change by its place); yours move among yours.
        var movable = rule.Added;
        tools.Children.Add(Tool("up", movable ? "Move up" : "The game's cheats keep their place", () => Swap(at, at - 1),
            movable && at > 0 && list[at - 1].Added));
        tools.Children.Add(Tool("down", movable ? "Move down" : "The game's cheats keep their place", () => Swap(at, at + 1),
            movable && at + 1 < list.Count && list[at + 1].Added));
        if (rule.Added) {
            tools.Children.Add(Tool("trash", "Delete this cheat", () => {
                list.Remove(rule);
                _cheatExpanded.Remove(rule);
                RenderCheats();
            }));
        }
        else if (rule.IsChanged) {
            tools.Children.Add(Tool("undo", "Put back the game's cheat", () => {
                var fresh = CheatRule.FromCheat(rule.Game, rule.Index, rule.Game, rule.GameCanonical);
                list[at] = fresh;
                if (_cheatExpanded.Remove(rule)) {
                    _cheatExpanded.Add(fresh);
                }

                RenderCheats();
            }));
        }

        void Swap(int a, int b) {
            (list[a], list[b]) = (list[b], list[a]);
            RenderCheats();
        }

        var head = new DockPanel {
            Children = {
                WithDock(new Border {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 10, 0),
                    Background = rule.Off ? LINE : BlockColors.WHEN, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                }, Dock.Left),
                WithDock(WithMargin(tools, new Thickness(10, 0, 0, 0)), Dock.Right),
                new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { marks, summary } },
            },
        };
        var card = new StackPanel { Spacing = 10, Children = { head } };
        if (open) {
            card.Children.Add(CheatScript(rule));
            var notes = new TextBox { Text = rule.Notes, Watermark = "Your notes on this cheat (the server does not use them)", AcceptsReturn = false };
            notes.TextChanged += (_, _) => rule.Notes = notes.Text ?? "";
            card.Children.Add(notes);
        }

        var border = new Border {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = open ? new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)) : new SolidColorBrush(Color.FromArgb(14, 128, 128, 128)),
            BorderBrush = rule == _cheatFocus && open ? BlockColors.WHEN : LINE, BorderThickness = new Thickness(rule == _cheatFocus && open ? 2 : 1),
            Child = card,
        };
        border.PointerPressed += (_, _) => {
            if (open && _cheatFocus != rule) {
                _cheatFocus = rule;
                foreach (var other in _cheatRows.Children.OfType<Border>()) {
                    other.BorderThickness = new Thickness(1);
                    other.BorderBrush = LINE;
                }

                border.BorderBrush = BlockColors.WHEN;
                border.BorderThickness = new Thickness(2);
            }
        };

        return border;
    }

    private void UpdateCheatHead(CheatRule rule) {
        if (!_cheatHeads.TryGetValue(rule, out var head)) {
            return;
        }

        head.Summary.Text = CheatSpecs.Summary(rule);
        head.Summary.Foreground = rule.Off ? MUTED : rule.IsChanged ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, 200, 200, 200));
        head.Summary.TextDecorations = rule.Off ? TextDecorations.Strikethrough : null;
        var marks = new List<string>();
        if (rule.Added) {
            marks.Add("Yours");
        }
        else if (rule.IsChanged && !rule.Off) {
            marks.Add("Changed from the game's");
        }
        else if (!rule.Off) {
            marks.Add("The game's");
        }

        if (rule.Off) {
            marks.Add("Turned off");
        }

        head.Marks.Text = string.Join("  ·  ", marks);
        head.Marks.Foreground = rule.Added || rule.IsChanged ? WARN : MUTED;
    }

    // ---------------------------------------------------------------------
    // A cheat's script of blocks
    // ---------------------------------------------------------------------

    private Control CheatScript(CheatRule rule) {
        var stack = new BlockStack("Drop If or Do blocks here, or click one in the palette");
        var blocks = new List<BlockView> { BlockFor(rule, rule.When) };
        blocks.AddRange(rule.Ifs.Select(b => BlockFor(rule, b)));
        blocks.AddRange(rule.Dos.Select(b => BlockFor(rule, b)));
        stack.SetBlocks(blocks);
        stack.Moved += (view, to) => Guarded(() => {
            var block = (CheatBlock) view.Model!;
            var list = view.Kind == BlockKind.If ? rule.Ifs : rule.Dos;
            list.Remove(block);
            list.Insert(Math.Clamp(to, 0, list.Count), block);
            RenderCheats();
        });
        stack.Dropped += (payload, _, index) => Guarded(() => AddFromPalette((CheatSpec) payload, rule, index));

        return new Border { Padding = new Thickness(4, 6, 4, 2), Child = stack };
    }

    // A palette block: into the given cheat (or the one being edited), at index among its kind (-1: at the end). A
    // When block changes what sets the cheat off, or starts a new cheat when none is open.
    private void AddFromPalette(CheatSpec spec, CheatRule? into, int index) {
        if (_cheats is null) {
            throw new PanelException("Pick a boss first.");
        }

        var rule = into ?? (_cheatFocus is { } focus && _cheatExpanded.Contains(focus) && _cheats.Cheats.Contains(focus) ? focus : null);
        if (rule is null) {
            if (spec.Kind != BlockKind.When) {
                NewCheat(CheatSpecs.Get("W.RoundStart"));
                rule = _cheatFocus!;
            }
            else {
                NewCheat(spec);

                return;
            }
        }

        var block = CheatBlock.New(spec);
        switch (spec.Kind) {
            case BlockKind.When:
                // What the old When had in common (its round numbers, school...) stays.
                foreach (var (key, value) in rule.When.Values) {
                    if (block.Values.Any(p => p.Key == key)) {
                        block.Set(key, value?.DeepClone());
                    }
                }

                rule.When = block;
                Say($"This cheat now goes off {spec.Summary(block).ToLowerInvariant().Replace("when ", "when ")}.");
                break;
            case BlockKind.If:
                rule.Ifs.Insert(index < 0 ? rule.Ifs.Count : Math.Min(index, rule.Ifs.Count), block);
                break;
            default:
                rule.Dos.Insert(index < 0 ? rule.Dos.Count : Math.Min(index, rule.Dos.Count), block);
                break;
        }

        _cheatExpanded.Add(rule);
        _cheatFocus = rule;
        RenderCheats();
    }

    private void NewCheat(CheatSpec when) {
        if (_cheats is null) {
            throw new PanelException("Pick a boss first.");
        }

        var rule = new CheatRule { When = CheatBlock.New(when) };
        rule.Dos.Add(CheatBlock.New(CheatSpecs.Get("D.Cast")));
        _cheats.Cheats.Add(rule);
        _cheatExpanded.Add(rule);
        _cheatFocus = rule;
        RenderCheats();
        Say("A new cheat is at the bottom of the list. Pick its spell, and add blocks from the palette.");
    }

    // A block of a cheat, its boxes wired to the block's values.
    private BlockView BlockFor(CheatRule rule, CheatBlock block) {
        var spec = block.Kind;
        var hat = spec.Kind == BlockKind.When;
        var view = new BlockView(spec.Kind, spec.Icon, spec.Help + (hat ? " Drag or click another When block in the palette to change it." : ""),
            canDelete: !hat, canDuplicate: !hat) { Model = block };
        view.SetOn(hat ? !rule.Off : block.On);
        view.Toggled += on => Guarded(() => {
            if (hat) {
                rule.Off = !on;
                RenderCheats();

                return;
            }

            block.On = on;
            UpdateCheatHead(rule);
        });
        view.Deleted += () => Guarded(() => {
            rule.Ifs.Remove(block);
            rule.Dos.Remove(block);
            RenderCheats();
        });
        view.Duplicated += () => Guarded(() => {
            var list = spec.Kind == BlockKind.If ? rule.Ifs : rule.Dos;
            list.Insert(list.IndexOf(block) + 1, block.Copy());
            RenderCheats();
        });
        ToolTip.SetTip(view.Grip, hat ? "The block that starts this cheat" : "Drag to move it up or down");

        void Changed() => UpdateCheatHead(rule);

        var parts = s_sentence.Split(spec.Sentence);
        // Split keeps the captured names at odd places: words, a box, words, a box...
        for (var i = 0; i < parts.Length; i++) {
            if (i % 2 == 0) {
                foreach (var word in parts[i].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                    view.Text(word, bold: i == 0 && word == parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
                }

                continue;
            }

            var field = spec.Fields.FirstOrDefault(f => f.Key == parts[i]);
            if (field is not null) {
                view.Field(FieldFor(block, field, Changed));
            }
        }

        return view;
    }

    private Control FieldFor(CheatBlock block, CheatField field, Action changed) {
        void Set(string key, JsonNode? value) {
            block.Set(key, value);
            changed();
        }

        switch (field.Type) {
            case CheatFieldType.Number:
                return BlockFields.Number(block.Number(field.Key), n => Set(field.Key, n), 0, field.Max);
            case CheatFieldType.Percent:
                return Inline(BlockFields.Number(block.Number(field.Key), n => Set(field.Key, n), 0, field.Max, 54), "%");
            case CheatFieldType.Words:
                return BlockFields.Words(block.Text(field.Key), t => Set(field.Key, t.Trim()), "type here");
            case CheatFieldType.Cards: {
                var box = BlockFields.Words(string.Join(", ", block.List(field.Key)),
                    t => Set(field.Key, new JsonArray([.. t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(w => (JsonNode) JsonValue.Create(w)!)])), "Tower Shield, Fire Blade", 260);

                return box;
            }
            case CheatFieldType.Choice:
                return BlockFields.Choice(field.Options ?? [], block.Text(field.Key), v => Set(field.Key, v));
            case CheatFieldType.Toggle:
                return BlockFields.Toggle(field.Label, block.Flag(field.Key), on => Set(field.Key, on));
            case CheatFieldType.Target:
                return BlockFields.Choice(CheatSpecs.TARGETS, block.Text(field.Key), v => Set(field.Key, v));
            case CheatFieldType.Category:
                return BlockFields.Choice(CheatSpecs.CATEGORIES, block.Text(field.Key), v => Set(field.Key, v));
            case CheatFieldType.School: {
                var holder = new Border { Child = SchoolBadge(block.Text(field.Key), 20) };
                var combo = BlockFields.Choice(CheatSpecs.SCHOOLS, block.Text(field.Key), v => {
                    Set(field.Key, v);
                    holder.Child = SchoolBadge(v, 20);
                });

                return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { WithCenter(holder), combo } };
            }
            case CheatFieldType.Rounds:
                return RoundsField(block, changed);
            case CheatFieldType.Health: {
                var hp = block.Number("Health") > 0;
                var number = BlockFields.Number(hp ? block.Number("Health") : block.Number("Percent"), n => {
                    if (block.Number("Health") > 0) {
                        Set("Health", n);
                    }
                    else {
                        Set("Percent", Math.Min(n, 100));
                    }
                }, 0, 100_000_000, 80);
                var unit = BlockFields.Choice([("pct", "% of its health"), ("hp", "hit points")], hp ? "hp" : "pct", v => {
                    var value = Math.Max(block.Number("Health"), block.Number("Percent"));
                    block.Set(v == "hp" ? "Health" : "Percent", v == "hp" ? Math.Max(1, value) : Math.Min(100, value));
                    block.Set(v == "hp" ? "Percent" : "Health", 0);
                    changed();
                });

                return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { number, unit } };
            }
            case CheatFieldType.HealAmount: {
                var number = BlockFields.Number(block.Number("Amount"), n => Set("Amount", n), 0, 100_000_000, 80);
                var unit = BlockFields.Choice([("hp", "hit points"), ("pct", "% of the most health")], block.Flag("Percent") ? "pct" : "hp",
                    v => Set("Percent", v == "pct"));

                return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { number, unit } };
            }
            case CheatFieldType.Sign: {
                var amount = block.Number(field.Key);
                var sign = amount < 0 ? -1 : 1;
                var number = BlockFields.Number(Math.Abs(amount), n => Set(field.Key, n * (block.Number(field.Key) < 0 ? -1 : 1)), 1, field.Max, 48);
                var choice = BlockFields.Choice([("give", "Give"), ("take", "Take")], sign < 0 ? "take" : "give",
                    v => Set(field.Key, Math.Abs(block.Number(field.Key)) * (v == "take" ? -1 : 1)));

                return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { choice, number } };
            }
            case CheatFieldType.Spell:
                return SpellField(block, changed);
            case CheatFieldType.Creature:
                return CreatureField(block, changed);
            case CheatFieldType.Line:
                return LineField(block, changed);
            default:
                return new TextBlock { Text = "?" };
        }
    }

    private static Control Inline(Control box, string after) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 3,
        Children = { box, new TextBlock { Text = after, Foreground = BlockColors.TEXT, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
    };

    // Rounds: every round, round N, every N rounds, or round N then every M rounds.
    private static Control RoundsField(CheatBlock block, Action changed) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var round = BlockFields.Number(Math.Max(1, block.Number("Round")), n => { block.Set("Round", n); changed(); }, 1, 999, 48);
        var every = BlockFields.Number(Math.Max(2, block.Number("Every")), n => { block.Set("Every", n); changed(); }, 1, 999, 48);
        var then = new TextBlock { Text = "then every", Foreground = BlockColors.TEXT, VerticalAlignment = VerticalAlignment.Center };
        var rounds = new TextBlock { Text = "rounds", Foreground = BlockColors.TEXT, VerticalAlignment = VerticalAlignment.Center };
        var mode = block.Number("Round") > 0 && block.Number("Every") > 0 ? "both" : block.Number("Round") > 0 ? "round"
            : block.Number("Every") > 1 ? "every" : "each";
        void Show(string m) {
            round.IsVisible = m is "round" or "both";
            every.IsVisible = m is "every" or "both";
            then.IsVisible = m == "both";
            rounds.IsVisible = m is "every" or "both";
        }

        var choice = BlockFields.Choice([("each", "every round"), ("round", "round"), ("every", "every"), ("both", "round")], mode, m => {
            block.Set("Round", m is "round" or "both" ? Math.Max(1, int.TryParse(round.Text, out var r) ? r : 1) : 0);
            block.Set("Every", m is "every" or "both" ? Math.Max(1, int.TryParse(every.Text, out var e) ? e : 2) : 0);
            Show(m);
            changed();
        });
        // Two choices read "round": the second one is "round N, then every M".
        choice.ItemsSource = new[] { "every round", "round …", "every … rounds", "round … then every …" };
        Show(mode);
        panel.Children.Add(choice);
        panel.Children.Add(round);
        panel.Children.Add(then);
        panel.Children.Add(every);
        panel.Children.Add(rounds);

        return panel;
    }

    // A spell: its school icon and name; the pop-up searches the spells, with their cards from the game.
    private Control SpellField(CheatBlock block, Action changed) {
        Button? button = null;
        string SchoolOf() => _bossCatalog?.Spells.GetValueOrDefault(block.Id("SpellID"))?.School
                             ?? _bossCatalog?.Spells.Values.FirstOrDefault(s => s.Name.Equals(block.Text("Spell"), StringComparison.OrdinalIgnoreCase))?.School ?? "";
        uint IdOf() => block.Id("SpellID") is var id and > 0 ? id
            : _bossCatalog?.Spells.Values.FirstOrDefault(s => s.Name.Equals(block.Text("Spell"), StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
        string Name() => block.Text("Spell") is { Length: > 0 } s ? s : block.Id("SpellID") > 0 ? $"spell {block.Id("SpellID")}" : "pick a spell";

        void ShowCard() {
            if (button is not null && IdOf() is var id and > 0 && CardOf(id) is not null) {
                ToolTip.SetTip(button, SpellThumb(id, SchoolOf(), 180));
            }
        }

        button = BlockFields.Picker(SchoolBadge(SchoolOf(), 18), Name(), flyout => {
            var search = new TextBox { Watermark = "Search a spell, such as Supernova or Steal Charm", Width = 380, Text = block.Text("Spell") };
            var results = new StackPanel { Spacing = 4 };
            void Render() {
                results.Children.Clear();
                if (_bossCatalog is null || _bossCatalog.Spells.Count == 0) {
                    results.Children.Add(Sub("The spell list shows up once the server has made its ID list."));

                    return;
                }

                var found = _bossCatalog.SearchSpells(search.Text ?? "", 8);
                if (found.Count == 0) {
                    results.Children.Add(Sub((search.Text ?? "").Trim().Length == 0 ? "Type a few letters of the spell's name." : "No spell matches."));
                }

                foreach (var spell in found) {
                    var row = new Button {
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6),
                        Content = new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 10,
                            Children = {
                                SpellThumb(spell.Id, spell.School, 40),
                                new StackPanel {
                                    VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                                    Children = {
                                        new TextBlock { Text = spell.Name, FontWeight = FontWeight.SemiBold },
                                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { SchoolBadge(spell.School, 14), new TextBlock { Text = $"{spell.School}  ·  ID {spell.Id}", Foreground = MUTED, FontSize = 12 } } },
                                    },
                                },
                            },
                        },
                    };
                    row.Click += (_, _) => {
                        block.Set("Spell", spell.Name);
                        block.Set("SpellID", spell.Id);
                        BlockFields.SetPicker(button!, SchoolBadge(spell.School, 18), spell.Name);
                        ShowCard();
                        changed();
                        flyout.Hide();
                    };
                    results.Children.Add(row);
                }
            }

            search.TextChanged += (_, _) => Render();
            Render();

            return new StackPanel { Spacing = 8, Width = 400, Children = { search, new ScrollViewer { MaxHeight = 420, Content = results } } };
        }, "Click to pick the spell");
        ShowCard();

        return button;
    }

    // A creature of the game, or one of the customs.
    private Control CreatureField(CheatBlock block, Action changed) {
        Button? button = null;
        string Shown() => block.Number("Custom") > 0
            ? CustomMob.Load(_manager.CustomsFolder, block.Number("Custom")) is { } c ? $"custom {c.Id}: {c.Shown}" : $"custom {block.Number("Custom")}"
            : block.Id("Creature") > 0 ? CreatureName(block.Id("Creature").ToString(CultureInfo.InvariantCulture)) : "pick a creature";
        string School() {
            var id = block.Number("Custom") > 0 ? CustomMob.Load(_manager.CustomsFolder, block.Number("Custom"))?.BaseId : block.Id("Creature").ToString(CultureInfo.InvariantCulture);

            return _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == id)?.School ?? "";
        }

        button = BlockFields.Picker(SchoolBadge(School(), 18), Shown(), flyout => {
            var search = new TextBox { Watermark = "Search a creature, such as Haunted Malistaire or 1336037", Width = 380 };
            var results = new StackPanel { Spacing = 4 };
            void Pick(uint creature, int custom) {
                block.Set("Creature", creature);
                block.Set("Custom", custom);
                BlockFields.SetPicker(button!, SchoolBadge(School(), 18), Shown());
                changed();
                flyout.Hide();
            }

            Button Choice(string name, string details, string school, Action click) {
                var row = new Button {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6),
                    Content = new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        Children = {
                            MobPortrait(name, school, 34),
                            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = details, Foreground = MUTED, FontSize = 12 } } },
                        },
                    },
                };
                row.Click += (_, _) => click();

                return row;
            }

            void Render() {
                results.Children.Clear();
                var customs = CustomMob.All(_manager.CustomsFolder);
                if (customs.Count > 0) {
                    results.Children.Add(new TextBlock { Text = "Your customs", FontWeight = FontWeight.SemiBold });
                    foreach (var custom in customs.Take(8)) {
                        var baseCreature = _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == custom.BaseId);
                        results.Children.Add(Choice($"Custom {custom.Id}: {custom.Shown}", $"based on {CreatureName(custom.BaseId)}", baseCreature?.School ?? "",
                            () => Pick(uint.TryParse(custom.BaseId, out var b) ? b : 0, custom.Id)));
                    }
                }

                results.Children.Add(new TextBlock { Text = "The game's creatures", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                if (_bossCatalog is null || _bossCatalog.Missing is not null) {
                    results.Children.Add(Sub(_bossCatalog?.Missing ?? "The creature list is not here yet."));

                    return;
                }

                var found = _bossCatalog.Search(search.Text ?? "", bossesOnly: false, new HashSet<string>(), 8, out _);
                if (found.Count == 0) {
                    results.Children.Add(Sub((search.Text ?? "").Trim().Length == 0 ? "Type a name or ID to search." : "No creature matches."));
                }

                foreach (var creature in found) {
                    results.Children.Add(Choice(creature.Name, $"ID {creature.Id}  ·  level {creature.Level}  ·  {creature.Health:N0} health", creature.School,
                        () => Pick(uint.TryParse(creature.Id, out var id) ? id : 0, 0)));
                }
            }

            search.TextChanged += (_, _) => Render();
            Render();

            return new StackPanel { Spacing = 8, Width = 400, Children = { search, new ScrollViewer { MaxHeight = 420, Content = results } } };
        }, "Click to pick the creature");

        return button;
    }

    // A game line: words or its number, what it shows, and a pop-up to find one.
    private Control LineField(CheatBlock block, Action changed) {
        var preview = new TextBlock { Foreground = BlockColors.TEXT, FontStyle = FontStyle.Italic, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBox? box = null;
        void Preview() {
            var line = block.Text("Line");
            preview.Text = line.Length == 0 ? "" : _cheatLines?.Resolve(line) is { } shown ? $"“{shown}”"
                : _cheatLines?.Any == true ? "(no game line holds these words)" : "";
        }

        box = BlockFields.Words(block.Text("Line"), t => {
            block.Set("Line", t.Trim());
            Preview();
            changed();
        }, "a few words of the line", 200);
        var find = BlockFields.Picker(BlockIcons.Icon("search", 14, BlockColors.FIELD_TEXT), "find", flyout => {
            var search = new TextBox { Watermark = "Words of the line, such as cannot be stopped", Width = 380, Text = block.Text("Line") };
            var results = new StackPanel { Spacing = 2 };
            void Render() {
                results.Children.Clear();
                if (_cheatLines is not { Any: true }) {
                    results.Children.Add(Sub("The game's lines show up once the server has made its ID list."));

                    return;
                }

                foreach (var (number, text) in _cheatLines.Search(search.Text ?? "", 12)) {
                    var row = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
                    row.Click += (_, _) => {
                        // The line's number finds exactly this line.
                        box!.Text = number.ToString(CultureInfo.InvariantCulture);
                        flyout.Hide();
                    };
                    results.Children.Add(row);
                }
            }

            search.TextChanged += (_, _) => Render();
            Render();

            return new StackPanel { Spacing = 8, Width = 400, Children = { search, new ScrollViewer { MaxHeight = 380, Content = results } } };
        }, "Find a line of the game");
        Preview();

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { box, find, preview } };
    }

    /// <summary>Kept for the editor's Save: the blocks change their cheat as they are edited, so nothing waits.</summary>
    private static void ApplyOpenCheat() {
    }

    // Takes the Cheats section out of the boss's file; its other changes stay.
    private async Task ResetCheatsAsync() {
        if (_boss is not { } boss || _bossCatalog is null) {
            throw new PanelException("Pick a boss first.");
        }

        if (!await ConfirmAsync($"Put back the game's cheats for {boss.Name}? Your cheat changes and added cheats are deleted; " +
                                "its other changes stay.")) {
            return;
        }

        var folder = _editFolder;
        if (BossChanges.Load(folder, _editFileId, _bossCatalog.Spells) is { } saved) {
            saved.Cheats = null;
            if (saved.IsEmpty && !saved.IsCustom) {
                File.Delete(BossChanges.FileOf(folder, _editFileId));
            }
            else {
                saved.Save(folder, _editFileId);
            }
        }

        LoadBossCheats(boss, BossChanges.Load(folder, _editFileId, _bossCatalog.Spells));
        if (_editCustom is null) {
            RenderBossResults();
        }
        Say($"{boss.Name} uses the game's cheats again from its next battle.");
    }

}
