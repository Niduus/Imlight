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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Bosses tab (Niduus, 2026-10-21: "edit the bosses deck and stats"): pick a boss, change its health, stats and
/// spells, and save. The server lays the saved file over the game's values from the boss's next battle.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string GAME_SCHOOL = "The game's school";
    private const string GAME_VALUE = "The game's value";
    private static readonly string[] TRI_STATE = [GAME_VALUE, "Yes", "No"];

    private readonly TextBox _bossSearch = new() { Watermark = "Search a boss by name or ID, such as Malistaire or Rattlebones" };
    private readonly CheckBox _bossesOnly = new() { Content = "Bosses only", IsChecked = true, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _bossCount = Sub("");
    private readonly ListBox _bossResults = new() { Background = Brushes.Transparent, MaxHeight = 260 };
    private readonly DispatcherTimer _bossDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly StackPanel _bossEditor = new() { Spacing = 14, IsVisible = false };
    private readonly TextBlock _bossTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _bossGame = Sub("");
    private readonly TextBlock _bossGameStats = Sub("");
    private readonly TextBlock _bossChangedNote = new() { Foreground = WARN, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _bossHealth = new() { Width = 140 };
    private readonly TextBox _bossLevel = new() { Width = 140 };
    private readonly ComboBox _bossSchool = new() { Width = 180 };
    private readonly Dictionary<string, TextBox> _bossStatBoxes = [];
    private readonly Dictionary<(string Row, string School), TextBox> _bossGridBoxes = [];
    private readonly ComboBox _bossStunable = new() { Width = 180, ItemsSource = TRI_STATE };
    private readonly ComboBox _bossBeguilable = new() { Width = 180, ItemsSource = TRI_STATE };
    private readonly StackPanel _bossDeckRows = new() { Spacing = 2 };
    private readonly TextBlock _bossDeckNote = Sub("");
    private readonly TextBox _bossSpellSearch = new() { Watermark = "Search a spell to add, such as Fire Cat, Meteor or 2062265892" };
    private readonly StackPanel _bossSpellResults = new() { Spacing = 2 };
    private readonly DispatcherTimer _bossSpellDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // The editor below is shared: the Bosses tab shows it for a creature (its BossOverrides file), the Customs tab for
    // a custom (its Customs file), each in a host of its own. These say which file it is editing now.
    private readonly Border _bossesEditorHost = new();
    private string _editFolder = "";
    private string _editFileId = "";
    private CustomMob? _editCustom;

    private BossCatalog? _bossCatalog;
    private DateTime _bossCatalogFrom;
    private BossInfo? _boss;
    private List<BossCard> _bossDeck = [];
    private bool _bossDeckChanged;

    private Control BossesTab() {
        _bossDelay.Tick += (_, _) => {
            _bossDelay.Stop();
            RenderBossResults();
        };
        _bossSearch.TextChanged += (_, _) => {
            _bossDelay.Stop();
            _bossDelay.Start();
        };
        _bossesOnly.IsCheckedChanged += (_, _) => RenderBossResults();
        _bossResults.ItemTemplate = new FuncDataTemplate<BossInfo>((boss, _) => boss is null ? new Panel() : BossRow(boss));
        _bossResults.SelectionChanged += async (_, _) => {
            if (_bossResults.SelectedItem is BossInfo boss && boss != _boss) {
                await Guard(() => {
                    OpenBoss(boss);

                    return Task.CompletedTask;
                });
            }
        };
        _bossSpellDelay.Tick += (_, _) => {
            _bossSpellDelay.Stop();
            RenderSpellResults();
        };
        _bossSpellSearch.TextChanged += (_, _) => {
            _bossSpellDelay.Stop();
            _bossSpellDelay.Start();
        };
        _bossSchool.ItemsSource = new[] { GAME_SCHOOL }.Concat(BossCatalog.SCHOOLS).ToList();

        var stats = new StackPanel { Spacing = 6 };
        stats.Children.Add(StatRow("Health", _bossHealth, "Its most health. The battle starts with it full."));
        stats.Children.Add(StatRow("Level", _bossLevel, "Changes its critical hits and the level shown in battle."));
        stats.Children.Add(StatRow("School", _bossSchool, "The school shown on its circle in battle."));
        foreach (var (key, label, help) in BossChanges.STATS) {
            var box = new TextBox { Width = 140, Watermark = "Game's value" };
            _bossStatBoxes[key] = box;
            stats.Children.Add(StatRow(label, box, help));
        }

        stats.Children.Add(StatRow("Can be stunned", _bossStunable, "No: stun spells do nothing to it."));
        stats.Children.Add(StatRow("Can be beguiled", _bossBeguilable,
            "No: a beguile spell cannot take it over. The server has no beguile spells yet, so this counts once it does."));

        BuildBossSections(stats);

        return Page(
            MissingNote("changing bosses needs " + ServerSource.DefaultName + ", which lists the creatures and reads the changes.",
                () => !_manager.HasExtraFolder || !_manager.HasIdLookup),
            BossesGuide(),
            Fold("search", "Bosses", "Find a boss",
                "Search for it, click it, change it and press Save changes.",
                WithHint(new DockPanel {
                    Children = { WithDock(WithCenter(_bossesOnly), Dock.Right), _bossSearch },
                }, "Search for it, click it, change what you want and press Save changes. " +
                   "Turn off Bosses only to find any creature that fights."),
                _bossCount,
                _bossResults),
            _bossesEditorHost);
    }

    /// <summary>Puts the shared editor into a tab's host, taking it out of the other one.</summary>
    private void PlaceBossEditor(Border host) {
        if (_bossEditor.Parent is Border old && old != host) {
            old.Child = null;
        }

        host.Child = _bossEditor;
    }

    /// <summary>
    /// The school grid: a row per stat (BossChanges.GRID_ROWS), a column for All and each school, and the help of each
    /// row under it.
    /// </summary>
    private Control SchoolGrid() {
        var columns = new[] { BossChanges.ALL }.Concat(BossChanges.SCHOOLS).ToList();
        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("150," + string.Join(',', columns.Select(_ => "*"))),
            RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", BossChanges.GRID_ROWS.Length + 1))),
        };
        void Put(Control control, int row, int column) {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        for (var c = 0; c < columns.Count; c++) {
            var header = new TextBlock {
                Text = columns[c], FontWeight = FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            };
            if (c > 0) {
                header.Foreground = MUTED;
            }

            Put(header, 0, c + 1);
        }

        var help = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
        for (var r = 0; r < BossChanges.GRID_ROWS.Length; r++) {
            var (key, label, text) = BossChanges.GRID_ROWS[r];
            Put(new TextBlock { Text = label, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center }, r + 1, 0);
            for (var c = 0; c < columns.Count; c++) {
                var box = new TextBox {
                    MinWidth = 0, Margin = new Thickness(2), Padding = new Thickness(4, 4), HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                ToolTip.SetTip(box, $"{label}, {(c == 0 ? "every school left empty" : columns[c])}");

                // The game's value (the grey number) shows dimmer than a number typed in.
                foreach (var state in new[] { "", "PointerOver", "Focused" }) {
                    box.Resources[$"TextControlPlaceholderForeground{state}"] = MUTED;
                }

                _bossGridBoxes[(key, columns[c])] = box;
                Put(box, r + 1, c + 1);
            }

            // A number under All is what the empty boxes of the row get, so they show it instead of the game's value.
            var rowKey = key;
            _bossGridBoxes[(key, BossChanges.ALL)].TextChanged += (_, _) => ShowGridRowValues(rowKey);

            help.Children.Add(Sub($"{label}: {text}"));
        }

        return new StackPanel { Children = { grid, help } };
    }

    private static Control StatRow(string label, Control box, string help) => new DockPanel {
        Children = {
            WithDock(new TextBlock { Text = label, Width = 190, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center }, Dock.Left),
            WithDock(box, Dock.Left),
            WithMargin(WithCenter(Sub(help)), new Thickness(12, 0, 0, 0)),
        },
    };

    private async Task LoadBossesAsync() {
        if (_bossCatalog is null || BossCatalogChanged()) {
            _bossCount.Text = "Loading…";
            await LoadBossCatalogAsync();
        }

        RenderBossResults();
    }

    // When ids.csv or creatures.csv was last written.
    private DateTime BossCatalogWritten() {
        var folder = _manager.IdLookupFolder;

        return new[] { BossCatalog.CREATURES_FILE, "ids.csv" }
            .Select(name => Path.Combine(folder, name))
            .Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
    }

    private bool BossCatalogChanged() => BossCatalogWritten() > _bossCatalogFrom;

    /// <summary>
    /// Reads the creature and spell lists. The cheat editors' spell and creature pickers use them too, so the Player cheats
    /// tab loads them itself: before, its spell picker said the ID list wasn't made yet unless the Bosses tab was opened first.
    /// </summary>
    private async Task LoadBossCatalogAsync() {
        var folder = _manager.IdLookupFolder;
        var changed = BossCatalogWritten();
        try {
            _bossCatalog = await Task.Run(() => BossCatalog.Load(folder));
            _bossCatalogFrom = changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _bossCatalog = BossCatalog.Load("");
        }
    }

    // The IDs of the creatures that have saved changes.
    private HashSet<string> ChangedBosses() {
        var folder = _manager.BossChangesFolder;

        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()]
            : [];
    }

    private void RenderBossResults() {
        if (_bossCatalog is null) {
            return;
        }

        if (_bossCatalog.Missing is not null) {
            _bossCount.Text = _bossCatalog.Missing;
            _bossResults.ItemsSource = null;

            return;
        }

        const int MAX = 200;
        var query = _bossSearch.Text ?? "";
        var changed = ChangedBosses();
        var results = _bossCatalog.Search(query, _bossesOnly.IsChecked == true, changed, MAX, out var total);
        _bossResults.ItemsSource = results;
        _bossCount.Text = query.Trim().Length == 0
            ? changed.Count == 0 ? "Type a name to find a boss." : $"Bosses you changed ({total}). Type a name to find another."
            : total == 0 ? _bossesOnly.IsChecked == true ? "No boss matches. Turn off Bosses only to search every creature." : "Nothing matches."
            : total > MAX ? $"{total:N0} matches. Showing the first {MAX}; add words to narrow it down."
            : total == 1 ? "1 match." : $"{total:N0} matches.";
    }

    private Control BossRow(BossInfo boss) {
        var changed = File.Exists(BossChanges.FileOf(_manager.BossChangesFolder, boss.Id));
        var details = new[] {
            boss.Level > 0 ? $"Level {boss.Level} {boss.School}".Trim() : boss.School,
            $"{boss.Health:N0} health",
            boss.IsBoss ? "Boss" : "",
            $"ID {boss.Id}",
            changed ? "Changed" : "",
        }.Where(t => t.Length > 0);

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = {
                    new TextBlock { Text = boss.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    WithCenter(new TextBlock { Text = string.Join("  ·  ", details), Foreground = changed ? WARN : MUTED }),
                },
            },
        };
    }

    private void OpenBoss(BossInfo boss) {
        _customHeaderCard.IsVisible = false;
        OpenBossEditor(boss, _manager.BossChangesFolder, boss.Id, null, _bossesEditorHost);
    }

    /// <summary>
    /// Shows the editor for a creature's file: its Bosses tab changes, or a custom's file (custom set) based on it.
    /// </summary>
    private void OpenBossEditor(BossInfo boss, string folder, string fileId, CustomMob? custom, Border host) {
        if (_bossCatalog is null) {
            return;
        }

        _editFolder = folder;
        _editFileId = fileId;
        _editCustom = custom;
        PlaceBossEditor(host);
        var changes = BossChanges.Load(folder, fileId, _bossCatalog.Spells);
        _boss = boss;
        _bossTitle.Text = custom is null ? boss.Name : $"Custom {custom.Id}: {custom.Shown} (based on {boss.Name})";
        _bossGame.Text = $"ID {boss.Id}. In the game: level {boss.Level}, {(boss.School.Length > 0 ? boss.School : "no school")}, " +
                         $"{boss.Health:N0} health{(boss.IsBoss ? ", a boss" : "")}.";
        _bossGameStats.Text = boss.Stats.Length > 0
            ? $"From its gear and the game: {boss.Stats}."
            : "It gets no extra damage, resist or other stats from the game.";
        var changed = changes is not null && !(changes.IsCustom && changes.WithoutCustom().IsEmpty);
        _bossChangedNote.Text = !changed ? "" : custom is null ? "This boss has saved changes. The boxes show them."
            : "This custom has changes. The boxes show them; empty boxes are the game's values.";
        _bossChangedNote.IsVisible = changed;

        _bossHealth.Watermark = $"Game: {boss.Health}";
        _bossHealth.Text = changes?.Health?.ToString(CultureInfo.InvariantCulture) ?? "";
        _bossLevel.Watermark = $"Game: {boss.Level}";
        _bossLevel.Text = changes?.Level?.ToString(CultureInfo.InvariantCulture) ?? "";
        var school = BossCatalog.SCHOOLS.FirstOrDefault(s => s.Equals(changes?.School, StringComparison.OrdinalIgnoreCase));
        _bossSchool.SelectedItem = school ?? GAME_SCHOOL;
        foreach (var (key, box) in _bossStatBoxes) {
            box.Text = changes is not null && changes.Stats.TryGetValue(key, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "";
        }

        var cheatShadowPips = CheatShadowPips(boss.Id);
        _bossStatBoxes["ShadowPips"].Watermark = cheatShadowPips is { } most ? $"Game: {most}" : "Game's value";
        foreach (var ((row, column), box) in _bossGridBoxes) {
            box.Text = changes?.Grid.GetValueOrDefault(row)?.TryGetValue(column, out var value) == true
                ? value.ToString(CultureInfo.InvariantCulture) : "";
        }

        foreach (var (row, _, _) in BossChanges.GRID_ROWS) {
            ShowGridRowValues(row);
        }

        _bossStunable.SelectedItem = Tri(changes?.Stunable);
        _bossBeguilable.SelectedItem = Tri(changes?.Beguilable);

        _bossDeck = changes?.Deck ?? _bossCatalog.GameDeck(boss);
        _bossDeckChanged = changes?.Deck is not null;
        _bossSpellSearch.Text = "";
        _bossSpellResults.Children.Clear();
        RenderBossDeck();
        LoadBossCheats(boss, changes);
        UpdateBossSections();
        _bossEditor.IsVisible = true;
    }

    private void RenderBossDeck() {
        _bossDeckRows.Children.Clear();
        _bossDeckNote.Text = _bossDeckChanged
            ? "This is your deck for the boss. Save changes to keep it."
            : "This is the game's deck for the boss.";
        if (_bossDeck.Count == 0) {
            _bossDeckRows.Children.Add(Sub("No spells yet. Add one below."));
        }

        foreach (var card in _bossDeck) {
            var copies = new NumericUpDown { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "0", Width = 130, Value = card.Copies };
            copies.ValueChanged += (_, _) => {
                card.Copies = (int) (copies.Value ?? 0);
                _bossDeckChanged = true;
            };
            var remove = Danger(Action(Plain("Remove"), () => {
                _bossDeck.Remove(card);
                _bossDeckChanged = true;
                RenderBossDeck();

                return Task.CompletedTask;
            }));
            _bossDeckRows.Children.Add(new Border {
                Padding = new Thickness(4, 4),
                BorderBrush = LINE,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new DockPanel {
                    Children = {
                        WithDock(Row(new TextBlock { Text = "Copies", Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center }, copies, remove), Dock.Right),
                        new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                SpellThumb(card.SpellId, card.School, 30, card.Copies),
                                WithCenter(new TextBlock { Text = card.Name, FontWeight = FontWeight.SemiBold }),
                                WithCenter(new TextBlock { Text = string.Join("  ·  ", new[] { card.School, $"ID {card.SpellId}" }.Where(t => t.Length > 0)), Foreground = MUTED }),
                            },
                        },
                    },
                },
            });
        }
    }

    private void RenderSpellResults() {
        _bossSpellResults.Children.Clear();
        if (_bossCatalog is null || (_bossSpellSearch.Text ?? "").Trim().Length == 0) {
            return;
        }

        var spells = _bossCatalog.SearchSpells(_bossSpellSearch.Text ?? "", 8);
        if (spells.Count == 0) {
            _bossSpellResults.Children.Add(Sub(_bossCatalog.Spells.Count == 0
                ? "The spell list shows up once the server has made its ID list."
                : "No spell matches."));
        }

        foreach (var spell in spells) {
            _bossSpellResults.Children.Add(new DockPanel {
                Children = {
                    WithDock(Action(Plain("Add"), () => {
                        if (_bossDeck.Any(c => c.SpellId == spell.Id)) {
                            throw new PanelException($"{spell.Name} is already in the deck. Change its copies instead.");
                        }

                        _bossDeck.Add(new BossCard { SpellId = spell.Id, Name = spell.Name, School = spell.School });
                        _bossDeckChanged = true;
                        RenderBossDeck();
                        Say($"{spell.Name} was added to the deck. Press Save changes to keep it.");

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithCenter(new TextBlock {
                        Text = string.Join("  ·  ", new[] { spell.Name, spell.School, $"ID {spell.Id}" }.Where(t => t.Length > 0)),
                    }),
                },
            });
        }
    }

    private Task SaveBossAsync() {
        if (_boss is not { } boss || _bossCatalog is null) {
            throw new PanelException("Pick a boss first.");
        }

        var changes = new BossChanges {
            Name = boss.Name,
            Health = WholeNumber(_bossHealth, "Health", 1, 100_000_000),
            Level = WholeNumber(_bossLevel, "Level", 1, 1000),
            School = _bossSchool.SelectedItem as string is { } school && school != GAME_SCHOOL ? school : null,
        };
        foreach (var (key, label, _) in BossChanges.STATS) {
            var box = _bossStatBoxes[key];
            float? value = key switch {
                "StartingPips" or "StartingPowerPips" => WholeNumber(box, label, 0, 7),
                "ShadowPips" => WholeNumber(box, label, 0, 50),
                "ShadowPipChance" => WholeNumber(box, label, 0, 100),
                _ => Number(box, label),
            };
            if (value is { } v) {
                changes.Stats[key] = v;
            }
        }

        foreach (var ((row, column), box) in _bossGridBoxes) {
            var label = BossChanges.GRID_ROWS.First(r => r.Key == row).Label;
            if (Number(box, $"{label} for {column}") is { } value) {
                (changes.Grid.TryGetValue(row, out var values) ? values : changes.Grid[row] = [])[column] = value;
            }
        }

        changes.Stunable = FromTri(_bossStunable);
        changes.Beguilable = FromTri(_bossBeguilable);

        // The game's deck as it is needs no file, and keeps randomizer mode's spell swaps.
        var gameDeck = _bossCatalog.GameDeck(boss);
        var sameAsGame = _bossDeck.Count == gameDeck.Count && _bossDeck.All(c => c.Copies == 0)
                         && _bossDeck.Select(c => c.SpellId).SequenceEqual(gameDeck.Select(c => c.SpellId));
        if (_bossDeck.Count == 0) {
            throw new PanelException("The deck is empty. Add a spell, or press Put back the game's deck.");
        }

        changes.Deck = _bossDeckChanged && !sameAsGame ? [.. _bossDeck] : null;

        ApplyOpenCheat();
        changes.Cheats = _cheats?.ToJson();
        RewardsInto(changes);

        var folder = _editFolder;
        if (_editCustom is { } custom) {
            // A custom keeps its file (its number, name and creature) even with nothing else changed.
            CustomHeaderInto(changes, custom);
            changes.Save(folder, _editFileId);
            Say($"Saved custom {custom.Id}. It uses the changes from its next battle or spawn. No restart needed.");
        }
        else if (changes.IsEmpty) {
            File.Delete(BossChanges.FileOf(folder, _editFileId));
            Say($"Nothing is changed, so {boss.Name} fights as in the game.");
        }
        else {
            changes.Save(folder, _editFileId);
            Say($"Saved. {boss.Name} uses the changes from its next battle. No restart needed.");
        }

        ReopenBossEditor();

        return Task.CompletedTask;
    }

    // Shows the file just saved or put back, in the tab it is in.
    private void ReopenBossEditor() {
        if (_boss is not { } boss) {
            return;
        }

        if (_editCustom is { } custom) {
            var fresh = CustomMob.Load(_manager.CustomsFolder, custom.Id) ?? custom;
            OpenBossEditor(boss, _editFolder, _editFileId, fresh, _customsEditorHost);
            RenderCustoms();
        }
        else {
            OpenBossEditor(boss, _editFolder, _editFileId, null, _bossesEditorHost);
            RenderBossResults();
        }
    }

    private async Task ResetBossAsync() {
        if (_boss is not { } boss) {
            throw new PanelException("Pick a boss first.");
        }

        var what = _editCustom is { } c ? $"custom {c.Id} ({c.Shown})" : boss.Name;
        if (!await ConfirmAsync($"Put back the game's health, stats, deck and cheats for {what}? Your changes for it are deleted." +
                                (_editCustom is null ? "" : " The custom itself, its name, drops and forms stay."))) {
            return;
        }

        var file = BossChanges.FileOf(_editFolder, _editFileId);
        if (_editCustom is not null && _bossCatalog is not null
            && BossChanges.Load(_editFolder, _editFileId, _bossCatalog.Spells) is { } saved) {
            saved.WithoutCustom(clear: true).Save(_editFolder, _editFileId);
        }
        else {
            File.Delete(file);
        }

        ReopenBossEditor();
        Say($"{what} fights with the game's values again from its next battle.");
    }

    // The grey numbers of a grid row: the row's All when one is typed, else the game's value of each school.
    private void ShowGridRowValues(string row) {
        var all = (_bossGridBoxes[(row, BossChanges.ALL)].Text ?? "").Trim().TrimEnd('%');
        var hasAll = float.TryParse(all.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        foreach (var column in new[] { BossChanges.ALL }.Concat(BossChanges.SCHOOLS)) {
            _bossGridBoxes[(row, column)].Watermark = _boss is null ? ""
                : hasAll && column != BossChanges.ALL ? all : GameGridValue(_boss, row, column);
        }
    }

    // The game's value of a grid cell, shown grey in the empty box: the school's number, or under All the number every
    // school shares (empty when they differ).
    private static string GameGridValue(BossInfo boss, string row, string school) {
        if (!boss.Grid.TryGetValue(row, out var values) || values.Length < BossChanges.SCHOOLS.Length) {
            return "";
        }

        if (school == BossChanges.ALL) {
            return values.Distinct().Count() == 1 ? Shown(values[0]) : "";
        }

        return Shown(values[Array.IndexOf(BossChanges.SCHOOLS, school)]);
    }

    private static string Shown(float value) => MathF.Round(value, 1).ToString(CultureInfo.InvariantCulture);

    // The ShadowPips of the boss's cheat file (spiraldb-extra/BossCheats), which the Bosses tab's value wins over.
    private int? CheatShadowPips(string id) {
        try {
            var file = Path.Combine(Path.GetDirectoryName(_manager.BossChangesFolder) ?? "", "BossCheats", $"{id}.json");
            if (File.Exists(file) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file),
                    documentOptions: new System.Text.Json.JsonDocumentOptions {
                        AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    }) is System.Text.Json.Nodes.JsonObject json) {
                var node = json.FirstOrDefault(p => p.Key.Equals("ShadowPips", StringComparison.OrdinalIgnoreCase)).Value;
                if (node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out var most) && most > 0) {
                    return most;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            // A cheat file that cannot be read shows no game value.
        }

        return null;
    }

    private static string Tri(bool? value) => value switch { true => "Yes", false => "No", null => GAME_VALUE };

    private static bool? FromTri(ComboBox box) => (box.SelectedItem as string) switch { "Yes" => true, "No" => false, _ => null };

    private static int? WholeNumber(TextBox box, string label, int min, int max) {
        var text = (box.Text ?? "").Trim().Replace(",", "");
        if (text.Length == 0) {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max) {
            throw new PanelException($"{label} must be a whole number from {min} to {max:N0}, or empty to keep the game's value.");
        }

        return value;
    }

    private static float? Number(TextBox box, string label) {
        var text = (box.Text ?? "").Trim().TrimEnd('%').Trim();
        if (text.Length == 0) {
            return null;
        }

        if (!float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || float.IsNaN(value) || float.IsInfinity(value)) {
            throw new PanelException($"{label} must be a number, such as 25, or empty to keep the game's value.");
        }

        return value;
    }

}
