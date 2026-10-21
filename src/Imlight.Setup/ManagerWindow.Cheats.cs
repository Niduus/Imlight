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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Cheats part of the Bosses tab (Justin, 2026-10-21: "modify cheats like the spells that get cast? Default if
/// unchanged"): the moves a boss makes outside its turn, from its cheat file. Each can be changed, turned off or put
/// back, and new ones added; they are saved with the boss's other changes and the cheat file is never changed.
/// </summary>
internal sealed partial class ManagerWindow {

    private enum CheatFieldKind { Choice, Whole, Text, Words, Flag }

    /// <summary>
    /// A box of the cheat editor: the cheat file's name for it, its label and help, and for a Choice or Flag the values
    /// with their words. Whens: the kinds of cheat it counts for (null = every kind).
    /// </summary>
    private sealed record CheatField(string Key, string Label, CheatFieldKind Kind, string Help,
                                     (string Value, string Label)[]? Options = null, string[]? Whens = null);

    private static readonly (string Value, string Label)[] CHEAT_WHENS = [
        ("BattleStart", "When the battle starts"),
        ("RoundStart", "At the start of a round"),
        ("RoundEnd", "At the end of a round"),
        ("HealthBelow", "When its health falls below"),
        ("PlayerCasts", "When a player casts a spell"),
        ("PlayerPasses", "When a player passes"),
    ];

    private static readonly (string Value, string Label)[] CHEAT_TARGETS = [
        ("", "As the spell aims"),
        ("Self", "The boss itself"),
        ("Caster", "The player who set it off"),
        ("Receiver", "Whoever that player's spell was aimed at"),
        ("Enemy", "A random player"),
        ("FirstEnemy", "The first player"),
        ("AllEnemies", "Every player"),
    ];

    private static readonly (string Value, string Label)[] YES_NO = [("true", "Yes"), ("false", "No")];

    private static readonly string[] ROUNDS = ["RoundStart", "RoundEnd"];

    private static readonly CheatField[] CHEAT_FIELDS = [
        new("When", "When", CheatFieldKind.Choice, "What sets it off.", CHEAT_WHENS),
        new("Phase", "Phase", CheatFieldKind.Whole, "Only in this phase of the fight. 0 means every phase; a boss starts in phase 1."),
        new("Round", "Round", CheatFieldKind.Whole, "The boss's round it happens in (rounds count from the start of the phase). 0 means not set.", Whens: ROUNDS),
        new("Every", "Every … rounds", CheatFieldKind.Whole, "Again every this many rounds (after Round, if set). 0 means not set; with neither, every round.", Whens: ROUNDS),
        new("NoAllies", "Only when alone", CheatFieldKind.Flag, "Yes: only when no one else on the boss's side is standing.", YES_NO, ROUNDS),
        new("Health", "Health below", CheatFieldKind.Whole, "Hit points. 0 means Percent counts instead.", Whens: ["HealthBelow"]),
        new("Percent", "Health below %", CheatFieldKind.Whole, "Percent of its most health, when Health below is 0.", Whens: ["HealthBelow"]),
        new("School", "Player's school", CheatFieldKind.Choice, "Only a spell of this school.",
            [("", "Any school"), .. BossCatalog.SCHOOLS.Select(s => (s, s))], ["PlayerCasts"]),
        new("Kind", "Card type", CheatFieldKind.Text, "Only a card of this type: Damage, Heal, Ward, Charm, Aura, Global, Steal, " +
            "Manipulation, Shadow… Type none for any.", Whens: ["PlayerCasts"]),
        new("Spells", "Player's cards", CheatFieldKind.Words, "Only these cards, by name, with commas between. Type none for any card.", Whens: ["PlayerCasts"]),
        new("OnBoss", "Aimed at the boss", CheatFieldKind.Flag, "Yes: only a spell aimed at the boss.", YES_NO, ["PlayerCasts"]),
        new("SingleTarget", "One target only", CheatFieldKind.Flag, "Yes: only a spell that hits one target.", YES_NO, ["PlayerCasts"]),
        new("Chance", "Chance %", CheatFieldKind.Whole, "The chance it happens each time it could. 100 means always."),
        new("Once", "Only once", CheatFieldKind.Flag, "Yes: only the first time in a battle.", YES_NO),
        new("Target", "Spells go on", CheatFieldKind.Choice, "Who the spells below are cast on.", CHEAT_TARGETS),
        new("Random", "How the spells are cast", CheatFieldKind.Flag, "All of them one after another, or one of them picked at random each time.",
            [("false", "Cast all, in order"), ("true", "Cast one picked at random")]),
        new("Line", "Line", CheatFieldKind.Text, "What the boss says: words from the game's line, or its number (Lookup tab, Phrase). Type none for no line."),
        new("SetHealth", "Health becomes", CheatFieldKind.Whole, "Its most and current health become this, even after it was defeated. 0 means no change."),
        new("SetPhase", "Moves on to phase", CheatFieldKind.Whole, "The fight moves on to this phase from the next round. 0 means no change."),
        new("Defeat", "Boss is defeated", CheatFieldKind.Flag, "Yes: the boss is defeated after the cheat.", YES_NO),
        new("Spawn", "Brings in creature", CheatFieldKind.Whole, "The ID of a creature that joins on the boss's side (a second form). 0 means none."),
        new("Notes", "Notes", CheatFieldKind.Text, "Your notes. The server does not use them."),
    ];

    private readonly StackPanel _cheatRows = new() { Spacing = 2 };
    private readonly TextBlock _cheatNote = Sub("");
    private readonly StackPanel _cheatEditor = new() { Spacing = 6, IsVisible = false };
    private readonly TextBox _cheatSpellSearch = new() { Watermark = "Search a spell to add, such as Steal Charm or Meteor" };
    private readonly StackPanel _cheatSpellResults = new() { Spacing = 2 };
    private readonly StackPanel _cheatSpellRows = new() { Spacing = 2 };
    // Made again for each open editor: it sits in the editor's own header, which a closed editor still holds.
    private TextBlock _cheatSpellsChanged = new();
    private readonly DispatcherTimer _cheatSpellDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private BossCheatList? _cheats;
    private CheatLines? _cheatLines;
    private CheatEdit? _cheatOpen;
    private List<CheatSpell> _cheatDraftSpells = [];
    private readonly List<(CheatField Field, Func<JsonNode?> Read)> _cheatReaders = [];

    private Control CheatsCard() {
        _cheatSpellDelay.Tick += (_, _) => {
            _cheatSpellDelay.Stop();
            RenderCheatSpellResults();
        };
        _cheatSpellSearch.TextChanged += (_, _) => {
            _cheatSpellDelay.Stop();
            _cheatSpellDelay.Start();
        };

        return Card(new StackPanel {
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(Danger(Action(Plain("Put back the game's cheats"), ResetCheatsAsync)), Dock.Right),
                        Heading("Cheats"),
                    },
                },
                Sub("Moves the boss makes outside its turn: free spells and lines, when something happens in the battle. " +
                    "Change one, turn it off, or add your own; grey shows the game's setting. Press Save changes to keep them."),
                WithMargin(_cheatNote, new Thickness(0, 6, 0, 0)),
                WithMargin(_cheatRows, new Thickness(0, 8, 0, 0)),
                WithMargin(Row(Action(Plain("Add a cheat"), () => {
                    ApplyOpenCheat();
                    if (_cheats is null) {
                        throw new PanelException("Pick a boss first.");
                    }

                    var added = new CheatEdit();
                    added.Changes["When"] = "RoundStart";
                    added.Spells = [];
                    _cheats.Cheats.Add(added);
                    OpenCheat(added);

                    return Task.CompletedTask;
                })), new Thickness(0, 10, 0, 0)),
                WithMargin(_cheatEditor, new Thickness(0, 12, 0, 0)),
            },
        });
    }

    private string CheatExtraFolder => Path.GetDirectoryName(_manager.BossChangesFolder) ?? "";

    /// <summary>Shows the boss's cheats: its cheat file with the saved changes laid over it.</summary>
    private void LoadBossCheats(BossInfo boss, BossChanges? changes) {
        _cheatLines ??= CheatLines.Load(_manager.IdLookupFolder);
        try {
            _cheats = BossCheatList.Load(CheatExtraFolder, boss.Id, changes?.Cheats);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _cheats = new BossCheatList();
            Say($"The cheat file of {boss.Name} could not be read: {ex.Message}");
        }

        CloseCheat();
        RenderCheats();
    }

    private void RenderCheats() {
        _cheatRows.Children.Clear();
        if (_cheats is null) {
            return;
        }

        var notes = new List<string>();
        if (!_cheats.HasFile) {
            notes.Add("This boss has no cheats in the game. Add one below.");
        }

        if (_cheats.Lost > 0) {
            notes.Add($"{_cheats.Lost} saved change(s) are for cheats the game's cheat file no longer has, so they are left out. " +
                      "Save changes to clear them.");
        }

        _cheatNote.Text = string.Join(" ", notes);
        _cheatNote.IsVisible = notes.Count > 0;

        var number = 0;
        foreach (var cheat in _cheats.Cheats) {
            number++;
            var marks = new List<string>();
            if (cheat.Added) {
                marks.Add("Added by you");
            }
            else if (cheat.Off) {
                marks.Add("Turned off");
            }
            else if (cheat.IsChanged) {
                marks.Add("Changed");
            }

            var buttons = new List<Control> {
                Action(Plain("Change"), () => {
                    ApplyOpenCheat();
                    OpenCheat(cheat);

                    return Task.CompletedTask;
                }),
            };
            if (!cheat.Added) {
                buttons.Add(Action(Plain(cheat.Off ? "Turn on" : "Turn off"), () => {
                    ApplyOpenCheat();
                    cheat.Off = !cheat.Off;
                    RenderCheats();

                    return Task.CompletedTask;
                }));
            }

            if (cheat.Added || cheat.IsChanged) {
                buttons.Add(Danger(Action(Plain(cheat.Added ? "Remove" : "Put back"), () => {
                    if (_cheatOpen == cheat) {
                        CloseCheat();
                    }
                    else {
                        ApplyOpenCheat();
                    }

                    if (cheat.Added) {
                        _cheats.Cheats.Remove(cheat);
                    }
                    else {
                        cheat.Changes.Clear();
                        cheat.Spells = null;
                        cheat.Off = false;
                    }

                    RenderCheats();

                    return Task.CompletedTask;
                })));
            }

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = {
                    new TextBlock { Text = $"{number}.", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = string.Join("  ·  ", marks), Foreground = WARN, IsVisible = marks.Count > 0 },
                },
            });
            var summary = new TextBlock { Text = DescribeCheat(cheat), TextWrapping = TextWrapping.Wrap };
            if (cheat.Off) {
                summary.Foreground = MUTED;
                summary.TextDecorations = TextDecorations.Strikethrough;
            }

            text.Children.Add(summary);
            if (cheat.Text("Notes") is { Length: > 0 } note) {
                text.Children.Add(Sub(note));
            }

            _cheatRows.Children.Add(new Border {
                Padding = new Thickness(4, 6),
                BorderBrush = LINE,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = cheat == _cheatOpen ? LINE : null,
                Child = new DockPanel {
                    Children = {
                        WithDock(WithMargin(Row([.. buttons]), new Thickness(10, 0, 0, 0)), Dock.Right),
                        text,
                    },
                },
            });
        }

        if (_cheats.Cheats.Count == 0 && _cheats.HasFile) {
            _cheatRows.Children.Add(Sub("Its cheat file has no cheats."));
        }
    }

    /// <summary>A cheat in plain words, such as "At the end of round 3, then every 5 rounds: casts Supernova Sun on a random player."</summary>
    private string DescribeCheat(CheatEdit cheat) {
        var parts = new List<string>();
        var when = cheat.Text("When");
        var round = cheat.Number("Round");
        var every = cheat.Number("Every");
        var rounds = round > 0 && every > 0 ? $"round {round}, then every {every} rounds"
            : round > 0 ? $"round {round}" : every > 0 ? $"every {every} rounds" : "every round";
        var trigger = when.ToLowerInvariant() switch {
            "battlestart" => "When the battle starts",
            "roundstart" => $"At the start of {rounds}",
            "roundend" => $"At the end of {rounds}",
            "healthbelow" => cheat.Number("Health") > 0 ? $"When its health falls under {cheat.Number("Health"):N0}"
                : $"When its health falls under {cheat.Number("Percent")}%",
            "playercasts" => "When a player casts " + PlayerSpell(cheat),
            "playerpasses" => "When a player passes",
            "" => "Never (no When is set)",
            _ => $"When {when}",
        };
        if (cheat.Flag("NoAllies") && ROUNDS.Contains(when, StringComparer.OrdinalIgnoreCase)) {
            trigger += ", when no one else on its side is standing";
        }

        if (cheat.Number("Phase") > 0) {
            trigger = $"In phase {cheat.Number("Phase")}: " + char.ToLowerInvariant(trigger[0]) + trigger[1..];
        }

        var spells = cheat.CurrentSpells;
        if (spells.Count > 0) {
            var names = spells.Select(s => s.Name + (s.Silent ? " (silent)" : "") + (s.DispelImmune ? "" : " (can be dispelled)")).ToList();
            var list = names.Count == 1 ? names[0]
                : cheat.Flag("Random") ? "one of " + string.Join(", ", names[..^1]) + " or " + names[^1]
                : string.Join(", then ", names);
            var target = CHEAT_TARGETS.FirstOrDefault(t => t.Value.Equals(cheat.Text("Target"), StringComparison.OrdinalIgnoreCase)).Label
                         ?? cheat.Text("Target");
            parts.Add($"casts {list}" + (cheat.Text("Target").Length > 0 ? $" on {char.ToLowerInvariant(target[0])}{target[1..]}" : ""));
        }

        if (cheat.Text("Line") is { Length: > 0 } line) {
            var shown = _cheatLines?.Resolve(line);
            parts.Add(shown is null ? $"says the line \"{line}\" (not found in the game's lines)" : $"says \"{shown}\"");
        }

        if (cheat.Number("SetHealth") > 0) {
            parts.Add($"its health becomes {cheat.Number("SetHealth"):N0}");
        }

        if (cheat.Number("SetPhase") > 0) {
            parts.Add($"the fight moves on to phase {cheat.Number("SetPhase")}");
        }

        if (cheat.Number("Spawn") > 0) {
            var spawn = cheat.Number("Spawn").ToString(CultureInfo.InvariantCulture);
            var name = _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == spawn)?.Name;
            parts.Add($"brings in {(name is null ? "creature " + spawn : $"{name} ({spawn})")}");
        }

        if (cheat.Flag("Defeat")) {
            parts.Add("the boss is defeated");
        }

        var extra = new List<string>();
        if (cheat.Number("Chance") < 100) {
            extra.Add($"{cheat.Number("Chance")}% chance");
        }

        if (cheat.Flag("Once")) {
            extra.Add("only once");
        }

        var what = parts.Count == 0 ? "does nothing" : string.Join("; ", parts);

        // A line that ends its own sentence ("…Tartarus.") needs no second full stop.
        var stop = what.Length > 1 && what[^1] == '"' && ".!?".Contains(what[^2]) ? "" : ".";

        return $"{trigger}: {what}{stop}" +(extra.Count > 0 ? $" ({string.Join(", ", extra)})" : "");
    }

    private static string PlayerSpell(CheatEdit cheat) {
        var kind = cheat.Text("Kind");
        var school = cheat.Text("School");
        var cards = cheat.List("Spells");
        var text = cards.Count > 0 ? (cards.Count == 1 ? cards[0] : "one of " + string.Join(", ", cards))
            : $"a {string.Join(' ', new[] { school, kind }.Where(t => t.Length > 0)).Trim()} spell".Replace("a  spell", "a spell");
        if (cards.Count > 0 && (school.Length > 0 || kind.Length > 0)) {
            text += $" ({string.Join(' ', new[] { school, kind }.Where(t => t.Length > 0))})";
        }

        if (cheat.Flag("OnBoss")) {
            text += " at the boss";
        }

        if (cheat.Flag("SingleTarget")) {
            text += " that hits one target";
        }

        return text;
    }

    // ---------------------------------------------------------------------
    // The editor of one cheat
    // ---------------------------------------------------------------------

    private void OpenCheat(CheatEdit cheat) {
        _cheatOpen = cheat;
        _cheatReaders.Clear();
        _cheatEditor.Children.Clear();
        _cheatDraftSpells = [.. cheat.CurrentSpells];
        _cheatSpellsChanged = new TextBlock { Foreground = WARN };

        var number = (_cheats?.Cheats.IndexOf(cheat) ?? 0) + 1;
        _cheatEditor.Children.Add(Heading($"Cheat {number}"));
        _cheatEditor.Children.Add(Sub(cheat.Added
            ? "A cheat you added. Empty boxes keep the server's default, shown grey."
            : "Leave a box empty, or pick the first choice, to keep the game's setting (shown grey). Changed settings show in orange."));

        var rows = new List<(CheatField Field, Control Row)>();
        ComboBox? whenBox = null;
        foreach (var field in CHEAT_FIELDS) {
            var (control, read) = CheatFieldControl(cheat, field);
            var label = new TextBlock { Text = field.Label, Width = 190, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
            void Mark() {
                var changed = !SameCheatValue(field, read(), cheat.GameValue(field.Key));
                if (changed) {
                    label.Foreground = WARN;
                }
                else {
                    label.ClearValue(TextBlock.ForegroundProperty);
                }

                ToolTip.SetTip(label, changed ? "Changed from the game's setting" : null);
            }

            switch (control) {
                case TextBox box:
                    box.TextChanged += (_, _) => Mark();
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => Mark();
                    break;
            }

            Mark();
            var row = new DockPanel {
                Children = {
                    WithDock(label, Dock.Left),
                    WithDock(control, Dock.Left),
                    WithMargin(WithCenter(Sub(field.Help)), new Thickness(12, 0, 0, 0)),
                },
            };
            if (field.Key == "Line" && control is TextBox lineBox) {
                var preview = Sub("");
                void Preview() {
                    var line = (lineBox.Text ?? "").Trim();
                    line = line.Length == 0 ? cheat.Text("Line") : line.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : line;
                    preview.Text = line.Length == 0 ? "No line." : _cheatLines?.Resolve(line) is { } shown ? $"Shows: \"{shown}\""
                        : _cheatLines?.Any == true ? "No game line holds these words." : "";
                }

                lineBox.TextChanged += (_, _) => Preview();
                Preview();
                rows.Add((field, new StackPanel { Spacing = 2, Children = { row, WithMargin(preview, new Thickness(190, 0, 0, 0)) } }));
            }
            else {
                rows.Add((field, row));
            }

            if (field.Key == "When") {
                whenBox = control as ComboBox;
            }

            _cheatReaders.Add((field, read));
        }

        // Boxes that do not count for the kind of cheat picked are hidden.
        void ShowRows() {
            var when = (_cheatReaders.First(r => r.Field.Key == "When").Read() as JsonValue)?.ToString()
                       ?? (cheat.GameValue("When") as JsonValue)?.ToString() ?? "";
            foreach (var (field, row) in rows) {
                row.IsVisible = field.Whens is null || field.Whens.Contains(when, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (whenBox is not null) {
            whenBox.SelectionChanged += (_, _) => ShowRows();
        }

        ShowRows();

        var fieldPanel = new StackPanel { Spacing = 6 };
        foreach (var (_, row) in rows.Where(r => r.Field.Key != "Notes")) {
            fieldPanel.Children.Add(row);
        }

        _cheatEditor.Children.Add(fieldPanel);

        _cheatEditor.Children.Add(WithMargin(new DockPanel {
            Children = {
                WithDock(Action(Plain("Put back the game's spells"), () => {
                    _cheatDraftSpells = [.. cheat.GameSpells];
                    RenderCheatSpells();

                    return Task.CompletedTask;
                }), Dock.Right),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Heading("Spells it casts"), WithCenter(_cheatSpellsChanged) } },
            },
        }, new Thickness(0, 10, 0, 0)));
        _cheatEditor.Children.Add(Sub("Free spells, no pips. Silent: no casting animation. Dispel immune: a Dispel on the boss " +
                                      "does not stop it (on by default; cheat spells never fizzle and work through stun)."));
        _cheatEditor.Children.Add(_cheatSpellRows);
        _cheatEditor.Children.Add(_cheatSpellSearch);
        _cheatEditor.Children.Add(_cheatSpellResults);
        _cheatEditor.Children.Add(rows.First(r => r.Field.Key == "Notes").Row);
        _cheatEditor.Children.Add(WithMargin(Row(
            Action(Primary("Keep these"), () => {
                ApplyOpenCheat();
                CloseCheat();
                RenderCheats();
                Say("Kept. Press Save changes to save it for the boss's next battle.");

                return Task.CompletedTask;
            }),
            Action(Plain("Cancel"), () => {
                if (cheat.Added && !cheat.IsChanged) {
                    _cheats?.Cheats.Remove(cheat);
                }

                CloseCheat();
                RenderCheats();

                return Task.CompletedTask;
            })), new Thickness(0, 8, 0, 0)));

        _cheatSpellSearch.Text = "";
        _cheatSpellResults.Children.Clear();
        RenderCheatSpells();
        _cheatEditor.IsVisible = true;
        RenderCheats();
    }

    private void CloseCheat() {
        _cheatOpen = null;
        _cheatReaders.Clear();
        _cheatEditor.Children.Clear();
        _cheatEditor.IsVisible = false;
    }

    /// <summary>Keeps what the open editor holds in its cheat; a box that cannot be read stops with a message.</summary>
    private void ApplyOpenCheat() {
        if (_cheatOpen is not { } cheat) {
            return;
        }

        var values = _cheatReaders.Select(r => (r.Field, Value: r.Read())).ToList();
        foreach (var (field, value) in values) {
            if (value is null || SameCheatValue(field, value, cheat.GameValue(field.Key))) {
                cheat.Changes.Remove(field.Key);
            }
            else {
                cheat.Changes[field.Key] = value;
            }
        }

        cheat.Spells = SameSpells(_cheatDraftSpells, cheat.GameSpells) && !cheat.Added ? null : [.. _cheatDraftSpells];
    }

    private static bool SameSpells(List<CheatSpell> a, List<CheatSpell> b) => a.SequenceEqual(b);

    /// <summary>The box of a field and how to read it: null keeps the game's value; it throws on a value it cannot read.</summary>
    private (Control Control, Func<JsonNode?> Read) CheatFieldControl(CheatEdit cheat, CheatField field) {
        var game = cheat.GameValue(field.Key);
        var changed = cheat.Changes.TryGetValue(field.Key, out var value);
        switch (field.Kind) {
            case CheatFieldKind.Choice or CheatFieldKind.Flag: {
                var options = field.Options ?? [];
                string Of(JsonNode? node) => field.Kind == CheatFieldKind.Flag
                    ? (node is JsonValue v && v.TryGetValue<bool>(out var b) && b ? "true" : "false")
                    : (node as JsonValue)?.ToString() ?? "";
                string LabelOf(string v) => options.FirstOrDefault(o => o.Value.Equals(v, StringComparison.OrdinalIgnoreCase)).Label ?? v;
                var items = new List<string>();
                // An added cheat has no game setting to keep, so its box shows the value itself.
                var keep = !cheat.Added;
                if (keep) {
                    items.Add($"Game's: {LabelOf(Of(game))}");
                }

                var current = changed || !keep ? Of(cheat.Get(field.Key)) : null;
                var at = current is null ? -1 : Array.FindIndex(options, o => o.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (current is not null && at < 0) {
                    // A value the file has that is not in the list (a When the server added since) stays pickable.
                    options = [.. options, (current, current)];
                    at = options.Length - 1;
                }

                items.AddRange(options.Select(o => o.Label));
                var combo = new ComboBox { Width = 280, ItemsSource = items, SelectedIndex = at < 0 ? 0 : at + (keep ? 1 : 0) };
                var picked = options;

                return (combo, () => {
                    var index = combo.SelectedIndex - (keep ? 1 : 0);
                    if (index < 0 || index >= picked.Length) {
                        return null;
                    }

                    return field.Kind == CheatFieldKind.Flag ? JsonValue.Create(picked[index].Value == "true") : JsonValue.Create(picked[index].Value);
                });
            }

            case CheatFieldKind.Whole: {
                var box = GreyBox(cheat.Added ? $"{(field.Key == "Chance" ? 100 : 0)}" : $"Game's: {CheatNumber(game, field.Key)}");
                box.Width = 160;
                box.Text = changed ? CheatNumber(value, field.Key).ToString(CultureInfo.InvariantCulture) : "";

                return (box, () => {
                    var text = (box.Text ?? "").Trim().TrimEnd('%').Replace(",", "");
                    if (text.Length == 0) {
                        return null;
                    }

                    var max = field.Key is "Chance" or "Percent" ? 100 : field.Key == "Spawn" ? uint.MaxValue : 100_000_000;
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0 || n > max) {
                        throw new PanelException($"{field.Label} must be a whole number from 0 to {max:N0}, or empty to keep the game's setting.");
                    }

                    return JsonValue.Create(n);
                });
            }

            default: {
                string Shown(JsonNode? node) => field.Kind == CheatFieldKind.Words
                    ? string.Join(", ", (node as JsonArray ?? []).Select(n => n?.ToString()).Where(s => s is { Length: > 0 }))
                    : (node as JsonValue)?.ToString() ?? "";
                var gameText = Shown(game);
                var box = GreyBox(cheat.Added ? "Not set" : gameText.Length == 0 ? "Game's: none" : $"Game's: {gameText}");
                box.Width = 280;
                box.Text = changed ? Shown(value) is { Length: > 0 } s ? s : "none" : "";

                return (box, () => {
                    var text = (box.Text ?? "").Trim();
                    if (text.Length == 0) {
                        return null;
                    }

                    var none = text.Equals("none", StringComparison.OrdinalIgnoreCase);
                    if (field.Kind == CheatFieldKind.Words) {
                        return new JsonArray([.. (none ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            .Select(w => (JsonNode) JsonValue.Create(w)!)]);
                    }

                    return JsonValue.Create(none ? "" : text);
                });
            }
        }
    }

    private static TextBox GreyBox(string watermark) {
        var box = new TextBox { Watermark = watermark };
        foreach (var state in new[] { "", "PointerOver", "Focused" }) {
            box.Resources[$"TextControlPlaceholderForeground{state}"] = MUTED;
        }

        return box;
    }

    private static long CheatNumber(JsonNode? node, string key)
        => CheatEdit.NumberOf(node) is { } n ? (long) n : key == "Chance" ? 100 : 0;

    // Whether a value is the same as the game's, a field the cheat file leaves out counting as the server's default.
    private static bool SameCheatValue(CheatField field, JsonNode? value, JsonNode? game) {
        if (value is null) {
            return true;
        }

        return field.Kind switch {
            CheatFieldKind.Whole => CheatNumber(value, field.Key) == CheatNumber(game, field.Key),
            CheatFieldKind.Flag => (value is JsonValue a && a.TryGetValue<bool>(out var x) && x) == (game is JsonValue b && b.TryGetValue<bool>(out var y) && y),
            CheatFieldKind.Words => JsonNode.DeepEquals(value, game ?? new JsonArray()),
            _ => string.Equals((value as JsonValue)?.ToString() ?? "", (game as JsonValue)?.ToString() ?? "", StringComparison.Ordinal),
        };
    }

    // ---------------------------------------------------------------------
    // The spells of the open cheat
    // ---------------------------------------------------------------------

    private void RenderCheatSpells() {
        _cheatSpellRows.Children.Clear();
        if (_cheatOpen is not { } cheat) {
            return;
        }

        _cheatSpellsChanged.Text = SameSpells(_cheatDraftSpells, cheat.GameSpells) ? "" : cheat.Added ? "" : "Changed";
        if (!cheat.Added && cheat.GameSpells.Count > 0) {
            _cheatSpellsChanged.Text += (_cheatSpellsChanged.Text.Length > 0 ? " · " : "") + "Game's: " + string.Join(", ", cheat.GameSpells.Select(s => s.Name));
            _cheatSpellsChanged.Foreground = SameSpells(_cheatDraftSpells, cheat.GameSpells) ? MUTED : WARN;
        }

        if (_cheatDraftSpells.Count == 0) {
            _cheatSpellRows.Children.Add(Sub("No spells: it only says its line (or does what is set above). Add one below."));
        }

        for (var i = 0; i < _cheatDraftSpells.Count; i++) {
            var at = i;
            var spell = _cheatDraftSpells[i];
            var silent = new CheckBox { Content = "Silent", IsChecked = spell.Silent };
            silent.IsCheckedChanged += (_, _) => {
                _cheatDraftSpells[at] = _cheatDraftSpells[at] with { Silent = silent.IsChecked == true };
                RenderCheatSpells();
            };
            var immune = new CheckBox { Content = "Dispel immune", IsChecked = spell.DispelImmune };
            immune.IsCheckedChanged += (_, _) => {
                _cheatDraftSpells[at] = _cheatDraftSpells[at] with { DispelImmune = immune.IsChecked == true };
                RenderCheatSpells();
            };
            Button Move(string text, int by) {
                var button = Action(Plain(text), () => {
                    (_cheatDraftSpells[at], _cheatDraftSpells[at + by]) = (_cheatDraftSpells[at + by], _cheatDraftSpells[at]);
                    RenderCheatSpells();

                    return Task.CompletedTask;
                });
                button.IsEnabled = at + by >= 0 && at + by < _cheatDraftSpells.Count;
                ToolTip.SetTip(button, by < 0 ? "Cast it earlier" : "Cast it later");

                return button;
            }

            var remove = Danger(Action(Plain("Remove"), () => {
                _cheatDraftSpells.RemoveAt(at);
                RenderCheatSpells();

                return Task.CompletedTask;
            }));
            var known = spell.SpellId != 0 ? _bossCatalog?.Spells.GetValueOrDefault(spell.SpellId) : null;
            _cheatSpellRows.Children.Add(new Border {
                Padding = new Thickness(4, 4),
                BorderBrush = LINE,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new DockPanel {
                    Children = {
                        WithDock(Row(silent, immune, Move("Up", -1), Move("Down", 1), remove), Dock.Right),
                        new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                new TextBlock { Text = $"{at + 1}. {spell.Name}", FontWeight = FontWeight.SemiBold },
                                new TextBlock {
                                    Text = string.Join("  ·  ", new[] { known?.School ?? "", spell.SpellId != 0 ? $"ID {spell.SpellId}" : "" }.Where(t => t.Length > 0)),
                                    Foreground = MUTED,
                                },
                            },
                        },
                    },
                },
            });
        }
    }

    private void RenderCheatSpellResults() {
        _cheatSpellResults.Children.Clear();
        if (_bossCatalog is null || (_cheatSpellSearch.Text ?? "").Trim().Length == 0) {
            return;
        }

        var spells = _bossCatalog.SearchSpells(_cheatSpellSearch.Text ?? "", 8);
        if (spells.Count == 0) {
            _cheatSpellResults.Children.Add(Sub(_bossCatalog.Spells.Count == 0
                ? "The spell list shows up once the server has made its ID list."
                : "No spell matches."));
        }

        foreach (var spell in spells) {
            _cheatSpellResults.Children.Add(new DockPanel {
                Children = {
                    WithDock(Action(Plain("Add"), () => {
                        _cheatDraftSpells.Add(new CheatSpell(spell.Id, spell.Name, false));
                        RenderCheatSpells();

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithCenter(new TextBlock {
                        Text = string.Join("  ·  ", new[] { spell.Name, spell.School, $"ID {spell.Id}" }.Where(t => t.Length > 0)),
                    }),
                },
            });
        }
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

        var folder = _manager.BossChangesFolder;
        if (BossChanges.Load(folder, boss.Id, _bossCatalog.Spells) is { } saved) {
            saved.Cheats = null;
            if (saved.IsEmpty) {
                File.Delete(BossChanges.FileOf(folder, boss.Id));
            }
            else {
                saved.Save(folder, boss.Id);
            }
        }

        LoadBossCheats(boss, BossChanges.Load(folder, boss.Id, _bossCatalog.Spells));
        RenderBossResults();
        Say($"{boss.Name} uses the game's cheats again from its next battle.");
    }

}
