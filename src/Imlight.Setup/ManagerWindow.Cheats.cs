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
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.CoreLib.Game.Combat;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The Cheats section of the boss and custom editors (Niduus, 2026-10-21: "modify cheats like the spells that get
/// cast? Default if unchanged"; 2026-10-22: "Custom Bosses so cheats can have their conditions and such able to be
/// modified and created" and "'block'-like programming"). Each cheat is a script of snapped blocks: a When block, If
/// blocks and Do blocks, built from the palette at the side. The game's cheats can be changed, turned off or put back
/// and new ones added; they are saved with the boss's other changes and its cheat file is never changed.
/// The same editor shows the player cheats (Player Cheats tab), each one a cheat of the player's own.
/// </summary>
internal sealed partial class ManagerWindow {

    /// <summary>One list of cheats being edited: a boss's (or custom's) or the player cheats.</summary>
    private sealed class CheatBoard(bool player) {
        internal bool Player { get; } = player;
        internal StackPanel Rows { get; } = new() { Spacing = 10 };
        internal TextBlock Note { get; } = Sub("");
        internal Dictionary<CheatRule, (TextBlock Summary, TextBlock Marks)> Heads { get; } = [];
        internal Dictionary<CheatRule, List<Action>> Counters { get; } = [];
        internal BossCheatList? List { get; set; }
        internal Section? Section { get; set; }

        // The cheat open on the edit page (null: the list of cheats shows). The edit page puts the palette right beside
        // the cheat's blocks, each scrolling on its own (Niduus, 2026-10-22: blocks couldn't be dragged that far down).
        internal CheatRule? Editing { get; set; }
        internal CheatRule? Shown { get; set; }
        internal Control ListView { get; set; } = new Panel();
        internal Control EditView { get; set; } = new Panel();
        internal Grid EditGrid { get; } = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        internal ContentControl EditHead { get; } = new();
        internal ScrollViewer EditScroll { get; } = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private readonly CheatBoard _bossBoard = new(player: false);
    private readonly CheatBoard _playerBoard = new(player: true);
    private CheatLines? _cheatLines;
    private bool _playerCheatsOn = true;

    // The boss editor's cheats (saved by SaveBossAsync).
    private BossCheatList? _cheats => _bossBoard.List;

    private static readonly Regex s_sentence = new(@"\{(\w+)\}", RegexOptions.Compiled);

    private Control CheatsCard() {
        var grid = CheatEditor(_bossBoard, [
            Danger(Action(Plain("Put back the game's cheats"), ResetCheatsAsync)),
        ], [], CheatsGuide());

        _bossBoard.Section = new Section("bolt", BlockColors.Accent("cheats"), "Cheats",
            "Moves the boss makes outside its turn. Each cheat reads as a sentence: When something happens, If it is true, Do these things. " +
            "Grey words in the list are the game's own cheats; yours show in orange. Press Save changes to keep them.", grid);

        return _bossBoard.Section;
    }

    // The Player Cheats tab (Niduus, 2026-10-25: "Make a new tab for player cheats in manager"); it was a sub-tab of
    // Commands before. Its editor is made when the tab is first shown (PageChanged), as it reads the cheat files then.
    private readonly ContentControl _playerCheatsPage = new();

    private Control PlayerCheatsTab() => _playerCheatsPage;

    private void ShowPlayerCheats() {
        if (_playerCheatsPage.Content is null) {
            Guarded(() => _playerCheatsPage.Content = PlayerCheatsPanel());
        }
    }

    private Control PlayerCheatsPanel() {
        _cheatLines ??= CheatLines.Load(_manager.IdLookupFolder);
        LoadPlayerCheats();
        var master = new CheckBox { Content = "Player cheats on", IsChecked = _playerCheatsOn, FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(master, "Turns every player cheat on or off at once. Saved at once; counts from the next round.");
        master.IsCheckedChanged += (_, _) => Guarded(() => {
            _playerCheatsOn = master.IsChecked == true;
            SavePlayerCheats();
            Say(_playerCheatsOn ? "Player cheats are on." : "Player cheats are off. Your cheats stay here.");
        });

        Control SaveButton() => Action(Primary("Save player cheats"), () => {
            SavePlayerCheats();
            Say("Player cheats saved. They count from the next round, with no restart.");

            return Task.CompletedTask;
        });

        var grid = CheatEditor(_playerBoard, [SaveButton()], [SaveButton()], master);
        EnsureGameIcons(() => RenderCheats(_playerBoard));
        if (_bossCatalog is null || _bossCatalog.Spells.Count == 0) {
            Dispatcher.UIThread.Post(async () => {
                await LoadBossCatalogAsync();
                RenderCheats(_playerBoard);
            });
        }

        return Page(Fold("bolt", "Player Cheats", "Your cheats",
            "Cheats for you, like a boss's: When something happens, If it is true, Do these things. Every block of the boss cheats " +
            "works here too, with you as \"it\" and the monsters as \"the players\". They only work for the wizard you play on this PC, " +
            "never for a friend's wizard. Saved changes count from the next round, with no restart.",
            grid));
    }

    private void LoadPlayerCheats() {
        try {
            (_playerBoard.List, _playerCheatsOn) = PlayerCheatFile.Load(CheatExtraFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _playerBoard.List = new BossCheatList();
            Say($"The player cheats could not be read: {ex.Message}");
        }

        _playerBoard.Editing = null;
        RenderCheats(_playerBoard);
    }

    private void SavePlayerCheats() {
        if (_playerBoard.List is null) {
            return;
        }

        try {
            PlayerCheatFile.Save(CheatExtraFolder, _playerBoard.List, _playerCheatsOn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new PanelException($"The player cheats could not be saved: {ex.Message}");
        }
    }

    // Two pages: the list of cheats with its buttons, and the edit page of one cheat with the palette beside its blocks.
    private Control CheatEditor(CheatBoard board, Control[] listButtons, Control[] editButtons, Control top) {
        var palette = new BlockPalette(
            CheatSpecs.All.Where(s => s.Id is not ("W.Other" or "D.Other") && s.For(board.Player))
                .Select(s => new PaletteEntry(s.Kind, s.Icon, s.Palette, s.Help, s)),
            entry => Guarded(() => AddFromPalette(board, (CheatSpec) entry.Payload, null, -1)),
            "Cheat blocks") {
            Width = 236, VerticalAlignment = VerticalAlignment.Stretch,
        };

        Control Legends() => new WrapPanel {
            Children = {
                Legend(BlockKind.When, "When… starts a cheat: what sets it off"),
                Legend(BlockKind.Passive, "Passive… always true, with no When (such as its circle)"),
                Legend(BlockKind.If, "If… only when something is true"),
                Legend(BlockKind.Do, board.Player ? "Do… what you get, top to bottom" : "Do… what the boss does, top to bottom"),
            },
        };

        var buttons = Row([
            Action(Primary("New cheat"), () => {
                NewCheat(board, CheatSpecs.Get(board.Player ? "W.ICast" : "W.RoundStart"));

                return Task.CompletedTask;
            }), .. listButtons, ExamplesButton(board),
        ]);
        board.ListView = new StackPanel { Spacing = 8, Children = { top, Legends(), board.Note, board.Rows, buttons } };

        var back = Action(Plain("← Back to all cheats"), () => {
            board.Editing = null;
            RenderCheats(board);

            return Task.CompletedTask;
        });
        board.EditGrid.Children.Add(palette);
        board.EditScroll.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(board.EditScroll, 1);
        board.EditGrid.Children.Add(board.EditScroll);
        board.EditView = new StackPanel {
            Spacing = 10, IsVisible = false,
            Children = {
                Row([back, .. editButtons]),
                board.EditHead,
                Sub("Click a block in the palette to add it at the end, or drag it to the place you want. Drag a block's grip to move it."),
                Legends(),
                board.EditGrid,
            },
        };

        return new StackPanel { Children = { board.ListView, board.EditView } };
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
            _bossBoard.List = BossCheatList.Load(CheatExtraFolder, boss.Id, changes?.Cheats);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _bossBoard.List = new BossCheatList();
            Say($"The cheat file of {boss.Name} could not be read: {ex.Message}");
        }

        _bossBoard.Editing = null;
        RenderCheats(_bossBoard);
        EnsureGameIcons(() => {
            if (_bossBoard.List is not null) {
                RenderCheats(_bossBoard);
            }
        });
    }

    private void RenderCheats(CheatBoard board) {
        board.Rows.Children.Clear();
        board.Heads.Clear();
        board.Counters.Clear();
        if (board.List is not { } list) {
            return;
        }

        var notes = new List<string>();
        if (!board.Player && !list.HasFile) {
            notes.Add("This boss has no cheats in the game. Make one with New cheat.");
        }

        if (list.Lost > 0) {
            notes.Add($"{list.Lost} saved change(s) are for cheats the game's cheat file no longer has, so they are left out. " +
                      "Save changes to clear them.");
        }

        board.Note.Text = string.Join(" ", notes);
        board.Note.IsVisible = notes.Count > 0;
        if (board.Editing is { } editing && !list.Cheats.Contains(editing)) {
            board.Editing = null;
        }

        var changed = list.Cheats.Count(c => c.IsChanged);
        board.Section?.SetBadge(list.Cheats.Count == 0 ? "" : $"{list.Cheats.Count} cheats" + (changed > 0 ? $", {changed} yours" : ""),
            changed > 0 ? WARN : null);
        board.ListView.IsVisible = board.Editing is null;
        board.EditView.IsVisible = board.Editing is not null;
        var opened = board.Shown != board.Editing;
        board.Shown = board.Editing;
        if (board.Editing is { } rule) {
            RenderEditPage(board, rule, opened);

            return;
        }

        board.EditHead.Content = null;
        board.EditScroll.Content = null;
        for (var i = 0; i < list.Cheats.Count; i++) {
            board.Rows.Children.Add(CheatCard(board, list.Cheats[i], i + 1));
        }

        if (list.Cheats.Count == 0) {
            board.Rows.Children.Add(Sub(board.Player ? "No player cheats yet. Press New cheat to make one."
                : list.HasFile ? "Its cheat file has no cheats." : "No cheats yet."));
        }

        if (opened) {
            Dispatcher.UIThread.Post(() => board.ListView.BringIntoView(), DispatcherPriority.Background);
        }
    }

    // The edit page of one cheat: its head, and its blocks beside the palette, both as tall as the window allows.
    private void RenderEditPage(CheatBoard board, CheatRule rule, bool opened) {
        board.EditHead.Content = CheatCard(board, rule, board.List!.Cheats.IndexOf(rule) + 1, editing: true);
        var notes = new TextBox { Text = rule.Notes, Watermark = "Your notes on this cheat (the server does not use them)", AcceptsReturn = false };
        notes.TextChanged += (_, _) => rule.Notes = notes.Text ?? "";
        board.EditScroll.Content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 14, 0), Children = { CheatScript(board, rule), notes } };
        board.EditGrid.Height = Math.Clamp(ClientSize.Height - 400, 400, 1100);
        if (opened) {
            board.EditScroll.Offset = default;
            Dispatcher.UIThread.Post(() => board.EditView.BringIntoView(), DispatcherPriority.Background);
        }
    }

    // One cheat: its number, its sentence and buttons (on the edit page, without Edit and moving).
    private Control CheatCard(CheatBoard board, CheatRule rule, int number, bool editing = false) {
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var marks = new TextBlock { Foreground = WARN, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        board.Heads[rule] = (summary, marks);
        UpdateCheatHead(board, rule);

        Button Tool(string icon, string tip, Action click, bool enabled = true) {
            var button = new Button { Content = BlockIcons.Icon(icon, 15, Brushes.White), Padding = new Thickness(6), IsEnabled = enabled };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Guarded(click);

            return button;
        }

        var list = board.List!.Cheats;
        var at = list.IndexOf(rule);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (!editing) {
            tools.Children.Add(Action(Primary("Edit blocks"), () => {
                board.Editing = rule;
                RenderCheats(board);

                return Task.CompletedTask;
            }));
        }

        tools.Children.Add(Tool(rule.Off ? "eyeoff" : "eye", rule.Off ? "Turn this cheat back on" : "Turn this cheat off (it stays here)", () => {
            rule.Off = !rule.Off;
            RenderCheats(board);
        }));
        tools.Children.Add(Tool("copy", "Make a copy of this cheat, as a new one of yours", () => {
            var copy = rule.Copy();
            list.Add(copy);
            board.Editing = copy;
            RenderCheats(board);
            Say("Copied. You are now editing the copy; it is at the bottom of the list.");
        }));
        // The game's cheats keep their places (the server finds a change by its place); yours move among yours.
        var movable = rule.Added;
        if (!editing) {
            tools.Children.Add(Tool("up", movable ? "Move up" : "The game's cheats keep their place", () => Swap(at, at - 1),
                movable && at > 0 && list[at - 1].Added));
            tools.Children.Add(Tool("down", movable ? "Move down" : "The game's cheats keep their place", () => Swap(at, at + 1),
                movable && at + 1 < list.Count && list[at + 1].Added));
        }

        if (rule.Added) {
            tools.Children.Add(Tool("trash", "Delete this cheat", () => {
                list.Remove(rule);
                if (board.Editing == rule) {
                    board.Editing = null;
                }

                RenderCheats(board);
            }));
        }
        else if (rule.IsChanged) {
            tools.Children.Add(Tool("undo", "Put back the game's cheat", () => {
                var fresh = CheatRule.FromCheat(rule.Game, rule.Index, rule.Game, rule.GameCanonical);
                list[at] = fresh;
                if (board.Editing == rule) {
                    board.Editing = fresh;
                }

                RenderCheats(board);
            }));
        }

        void Swap(int a, int b) {
            (list[a], list[b]) = (list[b], list[a]);
            RenderCheats(board);
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
        return new Border {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(editing ? (byte) 30 : (byte) 14, 128, 128, 128)),
            BorderBrush = editing ? BlockColors.WHEN : LINE, BorderThickness = new Thickness(editing ? 2 : 1),
            Child = head,
        };
    }

    private void UpdateCheatHead(CheatBoard board, CheatRule rule) {
        foreach (var refresh in board.Counters.GetValueOrDefault(rule) ?? []) {
            refresh();
        }

        if (!board.Heads.TryGetValue(rule, out var head)) {
            return;
        }

        head.Summary.Text = CheatSpecs.Summary(rule);
        head.Summary.Foreground = rule.Off ? MUTED : rule.IsChanged ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, 200, 200, 200));
        head.Summary.TextDecorations = rule.Off ? TextDecorations.Strikethrough : null;
        var marks = new List<string>();
        if (board.Player) {
            marks.Add(rule.Off ? "Turned off" : "On");
        }
        else {
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
        }

        var missing = Unfinished(board, rule);
        head.Marks.Text = string.Join("  ·  ", marks) + (missing.Count > 0 ? "\nNot finished, so it does nothing yet: " + string.Join(" ", missing) : "");
        head.Marks.Foreground = missing.Count > 0 || rule.Added || rule.IsChanged ? WARN : MUTED;
    }

    /// <summary>
    /// What a cheat's blocks still need before the server can do them, and how to fill each in (Niduus, 2026-10-22: adding
    /// "cast for free" or "show a banner" didn't say what was wrong or how to finish it).
    /// </summary>
    private List<string> Unfinished(CheatBoard board, CheatRule rule) {
        var missing = new List<string>();
        foreach (var block in new[] { rule.When }.Concat(rule.Ifs).Concat(rule.Dos).Where(b => b.On)) {
            var spec = CheatSpecs.Get(block.Spec);
            var name = $"\"{spec.Palette}\"";
            foreach (var field in spec.Fields) {
                switch (field.Type) {
                    case CheatFieldType.Spell when spec.Kind == BlockKind.Do && block.Text("Spell").Length == 0 && block.Id("SpellID") == 0:
                        missing.Add($"In {name}, click \"pick a spell\" and choose the spell to cast.");
                        break;
                    case CheatFieldType.Line when block.Text("Line").Length == 0:
                        missing.Add($"In {name}, type a few words of a game line (such as \"cannot be stopped\") or click \"find\" to pick one.");
                        break;
                    case CheatFieldType.Line when _cheatLines?.Any == true && _cheatLines.Resolve(block.Text("Line")) is null:
                        missing.Add($"In {name}, no game line holds \"{block.Text("Line")}\": click \"find\" and pick one from the list.");
                        break;
                    case CheatFieldType.Creature when block.Id("Creature") == 0 && block.Number("Custom") == 0:
                        missing.Add($"In {name}, click \"pick a creature\" and choose one.");
                        break;
                    case CheatFieldType.Words when field.Key == "Text" && block.Text("Text").Length == 0:
                        missing.Add($"In {name}, type the words to show.");
                        break;
                }
            }
        }

        return [.. missing.Distinct()];
    }

    // ---------------------------------------------------------------------
    // A cheat's script of blocks
    // ---------------------------------------------------------------------

    private Control CheatScript(CheatBoard board, CheatRule rule) {
        var stack = new BlockStack("Drop If or Do blocks here, or click one in the palette");
        var blocks = new List<BlockView> { BlockFor(board, rule, rule.When) };
        blocks.AddRange(rule.Ifs.Select(b => BlockFor(board, rule, b)));
        blocks.AddRange(rule.Dos.Select(b => BlockFor(board, rule, b)));
        stack.SetBlocks(blocks);
        stack.Moved += (view, to) => Guarded(() => {
            var block = (CheatBlock) view.Model!;
            var list = view.Kind == BlockKind.If ? rule.Ifs : rule.Dos;
            list.Remove(block);
            list.Insert(Math.Clamp(to, 0, list.Count), block);
            RenderCheats(board);
        });
        stack.Dropped += (payload, _, index) => Guarded(() => AddFromPalette(board, (CheatSpec) payload, rule, index));

        return new Border { Padding = new Thickness(4, 6, 4, 2), Child = stack };
    }

    // A palette block: into the given cheat (or the one being edited), at index among its kind (-1: at the end). A
    // When block changes what sets the cheat off, or starts a new cheat when none is open.
    private void AddFromPalette(CheatBoard board, CheatSpec spec, CheatRule? into, int index) {
        if (board.List is null) {
            throw new PanelException("Pick a boss first.");
        }

        var rule = into ?? (board.Editing is { } editing && board.List.Cheats.Contains(editing) ? editing : null);
        if (rule is null) {
            if (!BlockColors.IsHat(spec.Kind)) {
                NewCheat(board, CheatSpecs.Get(board.Player ? "W.ICast" : "W.RoundStart"));
                rule = board.Editing!;
            }
            else {
                NewCheat(board, spec);

                return;
            }
        }

        var block = CheatBlock.New(spec);
        switch (spec.Kind) {
            case BlockKind.When or BlockKind.Passive:
                // What the old When had in common (its round numbers, school...) stays.
                foreach (var (key, value) in rule.When.Values) {
                    if (block.Values.Any(p => p.Key == key)) {
                        block.Set(key, value?.DeepClone());
                    }
                }

                rule.When = block;
                Say(spec.Kind == BlockKind.Passive ? $"This cheat is now a passive: {spec.Summary(block).ToLowerInvariant()}."
                    : $"This cheat now goes off {spec.Summary(block).ToLowerInvariant()}.");
                break;
            case BlockKind.If:
                rule.Ifs.Insert(index < 0 ? rule.Ifs.Count : Math.Min(index, rule.Ifs.Count), block);
                break;
            default:
                rule.Dos.Insert(index < 0 ? rule.Dos.Count : Math.Min(index, rule.Dos.Count), block);
                break;
        }

        board.Editing = rule;
        RenderCheats(board);
    }

    private void NewCheat(CheatBoard board, CheatSpec when) {
        if (board.List is null) {
            throw new PanelException("Pick a boss first.");
        }

        var rule = new CheatRule { When = CheatBlock.New(when) };
        // A passive does its own thing (its circle...), with no Do blocks under it.
        if (when.Kind != BlockKind.Passive) {
            rule.Dos.Add(CheatBlock.New(CheatSpecs.Get(board.Player ? "D.FreeCast" : "D.Cast")));
        }

        board.List.Cheats.Add(rule);
        board.Editing = rule;
        RenderCheats(board);
        Say("A new cheat is open. Pick its spell, and add blocks from the palette beside it.");
    }

    // A block of a cheat, its boxes wired to the block's values.
    private BlockView BlockFor(CheatBoard board, CheatRule rule, CheatBlock block) {
        var spec = block.Kind;
        var hat = BlockColors.IsHat(spec.Kind);
        var view = new BlockView(spec.Kind, spec.Icon, spec.Help + (hat ? " Drag or click another When block in the palette to change it." : ""),
            canDelete: !hat, canDuplicate: !hat) { Model = block };
        view.SetOn(hat ? !rule.Off : block.On);
        view.Toggled += on => Guarded(() => {
            if (hat) {
                rule.Off = !on;
                RenderCheats(board);

                return;
            }

            block.On = on;
            UpdateCheatHead(board, rule);
        });
        view.Deleted += () => Guarded(() => {
            rule.Ifs.Remove(block);
            rule.Dos.Remove(block);
            RenderCheats(board);
        });
        view.Duplicated += () => Guarded(() => {
            var list = spec.Kind == BlockKind.If ? rule.Ifs : rule.Dos;
            list.Insert(list.IndexOf(block) + 1, block.Copy());
            RenderCheats(board);
        });
        ToolTip.SetTip(view.Grip, hat ? "The block that starts this cheat" : "Drag to move it up or down");

        Action? refreshNote = null;
        void Changed() {
            UpdateCheatHead(board, rule);
            refreshNote?.Invoke();
        }

        if (block.Spec == "D.AddCharm") {
            refreshNote = () => view.Note(AddCharmWarning(block));
            refreshNote();
        }

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
                view.Field(FieldFor(board, rule, block, field, Changed));
            }
        }

        // A field its sentence forgets still gets a box at the end (remove effects' "even protected ones" had none,
        // Niduus 2026-10-26).
        var shown = new HashSet<string>(parts.Where((_, i) => i % 2 == 1));
        foreach (var field in spec.Fields.Where(f => !shown.Contains(f.Key))) {
            view.Field(FieldFor(board, rule, block, field, Changed));
        }

        return view;
    }

    /// <summary>
    /// "Add an effect of your own size": a warning when the spell cast for it (its cast animation, "shown as", or the
    /// one it falls back to) puts on an effect of another school than yours. The game then shows that one and uses it
    /// on that school's hits, while the server uses yours, so they no longer go together (Niduus's log, 2026-10-25:
    /// a Sun cast of a 666% Shadow blade kept its Sun Blade icon after the Shadow hit used the server's blade up).
    /// </summary>
    private string? AddCharmWarning(CheatBlock block) {
        var kind = block.Text("Kind");
        if (kind is "DoT" or "HoT") {
            return null;
        }

        var school = block.Text("School");
        var look = block.Text("Look");
        var spell = block.Text("Spell");
        var global = CheatLookalikes.IsGlobal(kind);
        var word = CheatLookalikes.KindWord(kind);
        var target = block.Text("Target");
        var onCaster = global || target.Equals("Self", StringComparison.OrdinalIgnoreCase)
                       || target.Length == 0 && kind is not ("Weakness" or "Trap");
        static string SchoolWord(string s) => string.IsNullOrWhiteSpace(s) || s.Equals("All", StringComparison.OrdinalIgnoreCase) ? "all-school" : s;
        static string A(string w) => "aeiou".Contains(char.ToLowerInvariant(w[0])) ? $"an {w}" : $"a {w}";
        string shown;
        string shownSchool;
        var fellBack = "";
        if (spell.Length > 0 || block.Id("SpellID") > 0) {
            var known = _bossCatalog?.Spells.GetValueOrDefault(block.Id("SpellID"))
                        ?? _bossCatalog?.Spells.Values.FirstOrDefault(s => s.Name.Equals(spell, StringComparison.OrdinalIgnoreCase));
            shown = known?.Name ?? spell;
            shownSchool = CheatLookalikes.EffectSchoolOf(shown) ?? known?.School ?? "";
            if (shownSchool.Length == 0) {
                return null;
            }
        }
        else {
            var cast = CheatLookalikes.WithCast(kind, school, look, onCaster);
            if (cast is null && look.Length > 0) {
                fellBack = $"The game has no {look} {word}, so the normal one is cast. ";
            }

            var picked = cast ?? CheatLookalikes.Plain(kind, school);
            if (picked is null) {
                return null;
            }

            shown = picked.Value.Spell;
            shownSchool = picked.Value.EffectSchool;
        }

        // A healing global changes healing of every school, so only a damage global's school counts.
        if (kind is "HealGlobal" or "GloomGlobal" || CheatLookalikes.SameSchool(shownSchool, school)) {
            return fellBack.Length > 0 ? fellBack.Trim() : null;
        }

        var fix = school.Length > 0 && CheatLookalikes.WithCast(kind, school, school, onCaster) is { } same && CheatLookalikes.SameSchool(same.EffectSchool, school)
            ? $" Pick {(spell.Length > 0 ? $"no \"shown as\" and the {school} animation" : school)} to keep them in step."
            : CheatLookalikes.Plain(kind, school) is { } plain && CheatLookalikes.SameSchool(plain.EffectSchool, school)
                ? $" Pick {(spell.Length > 0 ? "no \"shown as\" and the normal animation" : "the normal animation")} to keep them in step."
                : "";
        var used = global ? "won't count for the same hits" : "won't be used up by the same hits";

        return $"{fellBack}The game shows {A($"{SchoolWord(shownSchool)} {word}")} ({shown}) but the server uses {A($"{SchoolWord(school)} {word}")}, "
               + $"so the shown {word} {used}.{fix}";
    }

    private Control FieldFor(CheatBoard board, CheatRule rule, CheatBlock block, CheatField field, Action changed) {
        void Set(string key, JsonNode? value) {
            block.Set(key, value);
            changed();
        }

        switch (field.Type) {
            case CheatFieldType.Number:
                return BlockFields.Number(block.Number(field.Key), n => Set(field.Key, n), field.Min, field.Max);
            case CheatFieldType.Percent:
                return Inline(BlockFields.Number(block.Number(field.Key), n => Set(field.Key, n), field.Min, field.Max, 54), "%");
            case CheatFieldType.Words:
                return BlockFields.Words(block.Text(field.Key), t => Set(field.Key, t.Trim()), field.Key switch { "Spell" or "HasSpell" or "RemovedSpell" or "GlobalSpell" => "any spell (names or spell IDs)", "Weights" => "such as 2, 1, 1", _ => "type here" });
            case CheatFieldType.Cards: {
                var box = BlockFields.Words(string.Join(", ", block.List(field.Key)),
                    t => Set(field.Key, new JsonArray([.. t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(w => (JsonNode) JsonValue.Create(w)!)])), field.Key is "Brings" or "BringsRandom" or "AlliesUp" or "AlliesDown" ? "1668859, 1668858" : "Scald, Tower Shield or 857718733", 220);
                var creatures = field.Key is "Brings" or "BringsRandom" or "AlliesUp" or "AlliesDown";
                ToolTip.SetTip(box, creatures ? "Creature ids, with commas between, such as 1668859, 1668858." : "Spell names or spell IDs, with commas between, such as Scald, Tower Shield, 857718733. An ID is that very spell.");

                return box;
            }
            case CheatFieldType.VersionChoice:
                return BlockFields.Choice([("any", "any version of"), ("exact", "exactly")], block.Flag(field.Key) ? "any" : "exact",
                    v => Set(field.Key, v == "any"));
            case CheatFieldType.MatchCount:
                return MatchCountField(board, rule, inWhen: block == rule.When);
            case CheatFieldType.Choice:
                return BlockFields.Choice(field.Options ?? [], block.Text(field.Key), v => Set(field.Key, v));
            case CheatFieldType.Toggle:
                return BlockFields.Toggle(field.Label, block.Flag(field.Key), on => {
                    Set(field.Key, on);
                    if (field.Key == "Enchant" && _enchantPickers.TryGetValue(block, out var picker)) {
                        picker.IsVisible = on;
                    }
                });
            case CheatFieldType.Enchants:
                return EnchantsField(block, changed);
            // One list of targets for boss and player cheats alike (the .c1 circles are in it).
            case CheatFieldType.Target or CheatFieldType.PlayerTarget:
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
                var unit = BlockFields.Choice([("pct", "% of the most health"), ("hp", "hit points")], hp ? "hp" : "pct", v => {
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

    // ---------------------------------------------------------------------
    // "Matches 7 spells": what a cheat's spell boxes cover
    // ---------------------------------------------------------------------

    // The live count of the spells the cheat's When and If blocks cover, with the first of them in its tip. The When
    // block's count hides while a "these spells" block shows it.
    private Control MatchCountField(CheatBoard board, CheatRule rule, bool inWhen) {
        var text = new TextBlock { Foreground = BlockColors.TEXT, FontStyle = FontStyle.Italic, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var holder = new Border { Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), Child = text };
        void Refresh() {
            holder.IsVisible = !inWhen || !rule.Ifs.Any(b => b.On && b.Spec == "I.Spells");
            var (count, names, note) = SpellMatches(rule);
            text.Text = count < 0 ? "any spell" : count == 1 ? "matches 1 spell" : $"matches {count:N0} spells";
            var tip = names.Count == 0 ? (count < 0 ? "Any spell sets it off." : "No spell in the game matches.")
                : string.Join("\n", names) + (count > names.Count ? $"\n…and {count - names.Count:N0} more" : "");
            ToolTip.SetTip(holder, note.Length > 0 ? $"{tip}\n\n{note}" : tip);
        }

        if (!board.Counters.TryGetValue(rule, out var list)) {
            board.Counters[rule] = list = [];
        }

        list.Add(Refresh);
        Refresh();

        return holder;
    }

    /// <summary>
    /// The spells a cheat's When (school, kind of card) and If blocks (these spells, pip cost, card type) cover, as the
    /// server works it out. -1 when nothing narrows it. From the game's own spell files when they are read, else from the
    /// ID list (names and schools only).
    /// </summary>
    private (int Count, List<string> Names, string Note) SpellMatches(CheatRule rule) {
        var school = rule.When.Text("School");
        var category = string.Join(',', new[] { rule.When.Text("Category"), CheatSpecs.CastWhenKind(rule.When.Spec) }.Where(c => c.Length > 0));
        var spells = rule.Ifs.FirstOrDefault(b => b.On && b.Spec == "I.Spells");
        var names = spells?.List("Spells") ?? [];
        var anyVersion = spells?.Flag("AnyVersion") ?? false;
        var cost = rule.Ifs.FirstOrDefault(b => b.On && b.Spec == "I.Cost");
        var type = rule.Ifs.FirstOrDefault(b => b.On && b.Spec == "I.CardType")?.Text("Kind") ?? "";
        if (school.Length == 0 && category.Length == 0 && names.Count == 0 && cost is null && type.Length == 0) {
            return (-1, [], "");
        }

        bool NameOk(params string?[] spellNames) => names.Count == 0 || SpellNameFamily.IsOneOf(names, anyVersion, spellNames);
        bool SchoolOk(string s) => school.Length == 0 || school.Equals(s, StringComparison.OrdinalIgnoreCase);

        if (_spellBook is { } book) {
            var found = book.Spells.Where(c => NameOk(c.Code, c.Name, c.Id.ToString(CultureInfo.InvariantCulture)) && SchoolOk(c.School)
                                               && (type.Length == 0 || type.Equals(c.Type, StringComparison.OrdinalIgnoreCase))
                                               && (cost is null || SpellNameFamily.CostMatches(c.XPips ? 0 : c.Pips, cost.Number("CostMin"), cost.Number("CostMax")))
                                               && SpellNameFamily.CategoryMatches(category, KindsOf(c)))
                .OrderBy(c => c.Hidden).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (found.Count, [.. found.Take(25).Select(c => $"{c.Name}{(c.Code != c.Name ? $"  ({c.Code})" : "")}  ·  {c.School}  ·  {c.Cost}")], "");
        }

        if (_bossCatalog is { Spells.Count: > 0 } catalog) {
            var found = catalog.Spells.Values.Where(s => NameOk(s.Name, s.Id.ToString(CultureInfo.InvariantCulture)) && SchoolOk(s.School)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            return (found.Count, [.. found.Take(25).Select(s => $"{s.Name}  ·  {s.School}")],
                category.Length > 0 || cost is not null || type.Length > 0
                    ? "The kind of card and pip cost are not counted until the game's spell files are read." : "");
        }

        return (0, [], "The spell list shows up once the server has made its ID list.");
    }

    // What a card does, as the server's CombatResolver.CategoriesOf works it out from its effects.
    private static HashSet<string> KindsOf(SpellCard card) {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Aura", "Global", "Polymorph", "Steal", "Shadow", "Enchantment", "Manipulation", "Charm", "Ward" }) {
            if (card.Type.Contains(name, StringComparison.OrdinalIgnoreCase)) {
                found.Add(name);
            }
        }

        foreach (var e in card.Effects) {
            var kind = e.Kind;
            if (e.Target == "Global") {
                found.Add("Global");
            }

            if (!kind.StartsWith("Modify", StringComparison.Ordinal) && kind.Contains("Damage", StringComparison.Ordinal) && kind != "AbsorbDamage") {
                found.Add("Damage");
            }

            if (!kind.StartsWith("Modify", StringComparison.Ordinal) && kind.Contains("Heal", StringComparison.Ordinal)) {
                found.Add("Heal");
            }

            switch (kind) {
                case "Stun": found.Add("Stun"); break;
                case "Dispel": found.Add("Dispel"); found.Add("Charm"); break;
                case "Polymorph": found.Add("Polymorph"); break;
                case "StealCharm" or "StealWard" or "StealHealth": found.Add("Steal"); break;
                case "AbsorbDamage": found.Add("Shield"); break;
                case "ModifyIncomingDamage" or "ModifyIncomingDamageFlat": found.Add(e.Amount < 0 ? "Shield" : "Trap"); break;
                case "ModifyOutgoingDamage" or "ModifyOutgoingDamageFlat": found.Add(e.Amount > 0 ? "Blade" : "Weakness"); break;
                case "ModifyAccuracy" or "CritBoost" or "ModifyOutgoingHeal" or "ModifyOutgoingArmorPiercing": found.Add("Charm"); break;
                case "DamageOverTime": found.Add("DoT"); break;
                case "HealOverTime": found.Add("HoT"); break;
            }
        }

        if (card.Minion) {
            found.Add("Minion");
        }

        if (found.Contains("Aura")) {
            found.Remove("Blade");
        }
        else if (found.Contains("Blade") || found.Contains("Weakness")) {
            found.Add("Charm");
        }

        if (found.Contains("Shield") || found.Contains("Trap")) {
            found.Add("Ward");
        }

        if (card.Treasure) {
            found.Add("Treasure");
        }

        return found;
    }

    private static Control Inline(Control box, string after) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 3,
        Children = { box, new TextBlock { Text = after, Foreground = BlockColors.TEXT, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
    };

    // Rounds: every round, round N, every N rounds, or round N then every M rounds.
    private static Control RoundsField(CheatBlock block, Action changed) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var mode = block.Number("Round") > 0 && block.Number("Every") > 0 ? "both" : block.Number("Round") > 0 ? "round"
            : block.Number("Every") > 1 ? "every" : "each";
        // A box writes only while its pick shows it (Niduus, 2026-10-26: "At the start of" kept changing): a hidden one
        // must not bring its number back into the rule. Every shows the rule's own number (1 stays 1).
        var round = BlockFields.Number(Math.Max(1, block.Number("Round")), n => {
            if (mode is "round" or "both") {
                block.Set("Round", n);
                changed();
            }
        }, 1, 999, 48);
        var every = BlockFields.Number(block.Number("Every") > 0 ? block.Number("Every") : 2, n => {
            if (mode is "every" or "both") {
                block.Set("Every", n);
                changed();
            }
        }, 1, 999, 48);
        var then = new TextBlock { Text = "then every", Foreground = BlockColors.TEXT, VerticalAlignment = VerticalAlignment.Center };
        var rounds = new TextBlock { Text = "rounds", Foreground = BlockColors.TEXT, VerticalAlignment = VerticalAlignment.Center };
        void Show(string m) {
            round.IsVisible = m is "round" or "both";
            every.IsVisible = m is "every" or "both";
            then.IsVisible = m == "both";
            rounds.IsVisible = m is "every" or "both";
        }

        var choice = BlockFields.Choice([("each", "every round"), ("round", "round …"), ("every", "every … rounds"), ("both", "round … then every …")], mode, m => {
            mode = m;
            block.Set("Round", m is "round" or "both" ? Math.Max(1, int.TryParse(round.Text, out var r) ? r : 1) : 0);
            block.Set("Every", m is "every" or "both" ? Math.Max(m == "every" ? 2 : 1, int.TryParse(every.Text, out var e) ? e : 2) : 0);
            Show(m);
            changed();
        });
        Show(mode);
        panel.Children.Add(choice);
        panel.Children.Add(round);
        panel.Children.Add(then);
        panel.Children.Add(every);
        panel.Children.Add(rounds);

        return panel;
    }

    // The spell picker's pip costs.
    private static readonly (string Value, string Label)[] s_pipChoices = [
        ("", "any pips"), ("0", "0 pips"), ("1", "1 pip"), ("2", "2 pips"), ("3", "3 pips"), ("4", "4 pips"), ("5", "5 pips"), ("6", "6 pips"),
        ("7", "7 pips"), ("8+", "8+ pips"), ("x", "X pips"),
    ];

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
            var search = new TextBox { Watermark = "Search a spell: Supernova, supnova, Steal Charm, or school:Fire kind:jinx pips:4", Width = 380, Text = block.Text("Spell") };
            var results = new StackPanel { Spacing = 4 };
            // Niduus, 2026-10-25 ("search methods"): narrow by school, kind of card and pip cost; an empty name lists them.
            var (pickSchool, pickKind, pickPips) = ("", "", "");
            var filters = new WrapPanel {
                Children = {
                    BlockFields.Choice(CheatSpecs.SCHOOLS, "", v => { pickSchool = v; Render(); }, 120),
                    BlockFields.Choice(CheatSpecs.CATEGORIES.Select(c => c.Value.Length == 0 ? (c.Value, "any kind") : c).ToArray(), "", v => { pickKind = v; Render(); }, 150),
                    BlockFields.Choice(s_pipChoices, "", v => { pickPips = v; Render(); }, 110),
                },
            };
            void Render() {
                results.Children.Clear();
                if (_bossCatalog is null) {
                    results.Children.Add(Sub("Loading the spell list…"));
                    Dispatcher.UIThread.Post(async () => {
                        await LoadBossCatalogAsync();
                        Render();
                    });

                    return;
                }

                if (_bossCatalog.Spells.Count == 0) {
                    results.Children.Add(Sub("The spell list shows up once the server has made its ID list."));

                    return;
                }

                var found = FindSpells(search.Text ?? "", pickSchool, pickKind, pickPips, 12, out var note);
                if (note.Length > 0) {
                    results.Children.Add(Sub(note));
                }

                if (found.Count == 0) {
                    results.Children.Add(Sub((search.Text ?? "").Trim().Length == 0 && pickSchool.Length + pickKind.Length + pickPips.Length == 0
                        ? "Type a few letters of the spell's name, or pick a school, kind or pip cost." : "No spell matches."));
                }

                foreach (var (spell, detail) in found) {
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
                                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { SchoolBadge(spell.School, 14), new TextBlock { Text = detail, Foreground = MUTED, FontSize = 12 } } },
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

            return new StackPanel { Spacing = 8, Width = 400, Children = { search, filters, new ScrollViewer { MaxHeight = 420, Content = results } } };
        }, "Click to pick the spell");
        ShowCard();

        return button;
    }

    // The enchantment picker of each FreeCast block, shown while its "enchanted" box is ticked.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CheatBlock, Control> _enchantPickers = [];

    /// <summary>
    /// The enchantments to put on an enchanted free cast (Niduus, 2026-10-22: "if enchant, allow select(s)"): tick one
    /// or more; the server puts each on in the order picked, and with none picked the best one that fits.
    /// </summary>
    private Control EnchantsField(CheatBlock block, Action changed) {
        Button? button = null;
        string Shown() => block.List("Enchants") is { Count: > 0 } picked ? string.Join(" + ", picked) : "best that fits";

        button = BlockFields.Picker(new TextBlock { Text = "✨", VerticalAlignment = VerticalAlignment.Center }, Shown(), flyout => {
            var search = new TextBox { Watermark = "Search an enchantment, such as Epic or Aegis", Width = 380 };
            var results = new StackPanel { Spacing = 2 };
            void Render() {
                results.Children.Clear();
                if (_spellBook is null) {
                    _ = Guard(LoadSpellBookAsync);
                    results.Children.Add(Sub(_spellBookProblem ?? "Reading the enchantments from your game files. Click here again in a moment."));

                    return;
                }

                var picked = block.List("Enchants");
                var words = (search.Text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var names = _spellBook.Spells
                    .Where(c => c.Type.Equals("Enchantment", StringComparison.OrdinalIgnoreCase) && (!c.Hidden || picked.Contains(c.Name)))
                    .Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => words.All(n.ToLowerInvariant().Contains))
                    .OrderBy(n => !picked.Contains(n, StringComparer.OrdinalIgnoreCase)).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(60).ToList();
                if (names.Count == 0) {
                    results.Children.Add(Sub("No enchantment matches."));
                }

                foreach (var name in names) {
                    var at = picked.FindIndex(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var box = new CheckBox { Content = at >= 0 ? $"{at + 1}. {name}" : name, IsChecked = at >= 0 };
                    box.IsCheckedChanged += (_, _) => {
                        var list = block.List("Enchants").Where(p => !p.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (box.IsChecked == true) {
                            list.Add(name);
                        }

                        block.Set("Enchants", new JsonArray([.. list.Select(n => (JsonNode) JsonValue.Create(n)!)]));
                        BlockFields.SetPicker(button!, new TextBlock { Text = "✨", VerticalAlignment = VerticalAlignment.Center }, Shown());
                        changed();
                        Render();
                    };
                    results.Children.Add(box);
                }
            }

            search.TextChanged += (_, _) => Render();
            Render();

            return new StackPanel {
                Spacing = 8, Width = 400,
                Children = { Sub("Tick one or more. They go on in the order ticked (the numbers), so several stack."), search, new ScrollViewer { MaxHeight = 420, Content = results } },
            };
        }, "Click to pick the enchantments");
        button.IsVisible = block.Flag("Enchant");
        _enchantPickers.AddOrUpdate(block, button);

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
            var search = new TextBox { Watermark = "Search a creature: Winged Sorrow, 1336037, world:Darkmoor, has:cheats", Width = 380 };
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

                // By name or ID (partly, or with a slip), world:, zone:, has:cheats and uses: (ManagerWindow.CheatSearch.cs).
                var found = FindCreatures(search.Text ?? "", bossesOnly: false, new HashSet<string>(), 8, out _);
                if (found.Count == 0) {
                    results.Children.Add(Sub((search.Text ?? "").Trim().Length == 0
                        ? "Type a name or ID to search, or world:Darkmoor, zone:DM_ or has:cheats." : "No creature matches."));
                }

                foreach (var creature in found) {
                    var note = CreatureNote(creature, search.Text ?? "", inCheats: false);
                    results.Children.Add(Choice(creature.Name, $"ID {creature.Id}  ·  level {creature.Level}  ·  {creature.Health:N0} health{(note.Length > 0 ? "  ·  " + note : "")}", creature.School,
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
