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
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab (Niduus, 2026-10-22: "a new Custom's tab in the manager for this custom area and the custom
/// malistaire I plan to make"). Custom mobs: a game creature with its own number, name, stats, deck, cheats and drops,
/// which count only when it is brought in as that custom (.joinme, or a room set to it); the base creature never
/// changes. Custom areas: the server's added zones and objects (spiraldb-extra/ZoneObjects), and which boss each room
/// spawns. Vendors and trainers: shopkeepers and trainers of your own (ManagerWindow.CustomNpcs.cs).
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Border _customsEditorHost = new();
    private readonly StackPanel _customRows = new() { Spacing = 6 };
    private readonly TextBlock _customCount = Sub("");
    private readonly TextBox _customBaseSearch = new() { Watermark = "Search the creature it is based on, such as Malistaire or 1336035" };
    private readonly StackPanel _customBaseResults = new() { Spacing = 2 };
    private readonly DispatcherTimer _customBaseDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _customNewBaseText = new() { Text = "No creature picked yet.", Foreground = MUTED };
    private readonly TextBox _customNewName = new() { Watermark = "Its name in battle, such as Malistaire the Unbroken", Width = 360 };
    private readonly Border _customHeaderCard = new();
    private readonly TextBlock _customHeaderTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _customHeaderBase = Sub("");
    private readonly TextBox _customJoinMe = new() { IsReadOnly = true, Width = 360 };
    private readonly TextBox _customName = new() { Width = 360 };
    private readonly TextBox _customDrops = new() { Width = 360 };
    private readonly TextBlock _customGameDrops = Sub("");
    private readonly StackPanel _customFormRows = new() { Spacing = 4 };

    private BossInfo? _customNewBase;
    private readonly List<(TextBox Creature, TextBox Custom)> _customForms = [];

    private Control CustomsTab() {
        _customBaseDelay.Tick += (_, _) => {
            _customBaseDelay.Stop();
            RenderCustomBaseResults();
        };
        _customBaseSearch.TextChanged += (_, _) => {
            _customBaseDelay.Stop();
            _customBaseDelay.Start();
        };
        _customName.TextChanged += (_, _) => {
            if (_editCustom is { } custom) {
                var name = (_customName.Text ?? "").Trim();
                _customHeaderTitle.Text = $"Custom {custom.Id}: {(name.Length > 0 ? name : $"Custom {custom.Id}")}";
            }
        };
        _customHeaderCard.IsVisible = false;
        // The custom's own fields now sit in the editor's Identity, Drops and Forms sections (ManagerWindow.CustomBosses.cs).

        // The step-by-step maker lives on the Guides tab (Niduus, 2026-10-22: "move to guide"); New custom… opens it there.
        return Page(
            MissingNote("customs need " + ServerSource.DefaultName + ", which lists the creatures and reads the customs and areas.",
                () => !_manager.HasExtraFolder || !_manager.HasIdLookup),
            CustomsGuide(),
            Fold("paw", "Customs", "Custom mobs",
                "Your own creatures, copied from the game's. Bring one into a battle with .joinme.",
                new DockPanel {
                    Children = {
                        WithDock(Action(Primary("New custom…"), () => {
                            ShowNewCustom();

                            return Task.CompletedTask;
                        }), Dock.Right),
                        WithCenter(_customCount),
                    },
                },
                _customRows),
            CustomNpcsCard(),
            CustomBattlesCard(),
            GauntletSigilsCard(),
            CustomAreasCard(),
            _customHeaderCard,
            _customsEditorHost);
    }

    private async Task LoadCustomsAsync() {
        await LoadBossesAsync();
        // The editor may have moved to the Bosses tab since.
        _customHeaderCard.IsVisible = _bossEditor.Parent == _customsEditorHost && _editCustom is not null;
        RenderCustoms();
        RenderNpcs();
        LoadBattleCustoms();
        RenderGauntletSigils();
        RenderAreas();
    }

    private void RenderCustoms() {
        _customRows.Children.Clear();
        UpdateCustomWizard();
        var customs = CustomMob.All(_manager.CustomsFolder);
        var rooms = SafeRooms();
        _customCount.Text = customs.Count == 0 ? "No customs yet. Press New custom… to make one." : customs.Count == 1 ? "1 custom." : $"{customs.Count} customs.";
        foreach (var custom in customs) {
            var usedIn = rooms.Where(r => r.Value.Values.Contains(custom.Id)).Select(r => r.Key).ToList();
            var details = new[] {
                $"based on {CreatureName(custom.BaseId)}",
                custom.JoinMe,
                usedIn.Count > 0 ? "boss of " + string.Join(", ", usedIn.Select(RoomName)) : "",
            }.Where(t => t.Length > 0);
            var editing = _editCustom?.Id == custom.Id && _bossEditor.Parent == _customsEditorHost;
            _customRows.Children.Add(ManagerTheme.Item(highlighted: editing, content: new DockPanel {
                    Children = {
                        WithDock(Row(
                            Action(Primary("Edit"), () => {
                                OpenCustom(custom);

                                return Task.CompletedTask;
                            }),
                            Action(Plain("Copy .joinme"), () => CopyAsync(custom.JoinMe)),
                            Action(Plain("Duplicate"), () => {
                                var copy = CustomMob.Duplicate(_manager.CustomsFolder, custom);
                                RenderCustoms();
                                Say($"Made custom {copy.Id}, a copy of custom {custom.Id}.");

                                return Task.CompletedTask;
                            }),
                            Danger(Action(Plain("Delete"), () => DeleteCustomAsync(custom)))), Dock.Right),
                        WithDock(WithMargin(WithCenter(ManagerTheme.Tile("paw", ManagerTheme.Accent("Customs"), 32)), new Thickness(0, 0, 12, 0)), Dock.Left),
                        new StackPanel {
                            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                new TextBlock { Text = $"{custom.Id}.  {custom.Shown}", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                                new TextBlock { Text = string.Join("  ·  ", details), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                            },
                        },
                    },
                }));
        }
    }

    // ---------------------------------------------------------------------
    // New custom: a guided wizard (Niduus, 2026-10-22: "make making a custom mob/boss easier to understand")
    // ---------------------------------------------------------------------

    private readonly Border[] _customStepMarks = new Border[4];
    private readonly Border _customPickedPortrait = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _customNumberText = new() { FontSize = ManagerTheme.FONT_HEADING, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock _customJoinPreview = new() { FontFamily = MONO, FontSize = ManagerTheme.FONT_HEADING, Foreground = GOOD };

    /// <summary>The wizard, as a section of the Guides tab (it is built there, under the guides' intro).</summary>
    private Section NewCustomWizard() {
        _customNewName.TextChanged += (_, _) => UpdateCustomWizard();
        _customNewName.HorizontalAlignment = HorizontalAlignment.Left;
        _customNewBaseText.TextWrapping = TextWrapping.Wrap;
        var picked = ManagerTheme.Item(new DockPanel {
            Children = {
                WithDock(WithMargin(_customPickedPortrait, new Thickness(0, 0, 12, 0)), Dock.Left),
                WithCenter(_customNewBaseText),
            },
        });
        var number = new StackPanel {
            Spacing = 4,
            Children = {
                _customNumberText,
                new DockPanel {
                    Children = {
                        WithDock(WithCenter(new TextBlock { Text = "Bring it into a battle with  ", Foreground = MUTED }), Dock.Left),
                        WithCenter(_customJoinPreview),
                    },
                },
            },
        };
        var steps = new StackPanel {
            Spacing = 0,
            Children = {
                WizardStep(0, "Pick the creature it starts from",
                    "Search a game creature by name or ID and press Use. Your custom starts as a copy of it: same look, stats and spells.",
                    _customBaseSearch, _customBaseResults, picked),
                WizardStep(1, "Give it a name",
                    "The name players see in battle. Keep the creature's own name or type a new one, such as Malistaire the Unbroken.",
                    _customNewName),
                WizardStep(2, "It gets a number of its own",
                    "Each custom has a number, so several customs can come from the same creature. Imlight picks the next free one.",
                    number),
                WizardStep(3, "Make it, then change what you like",
                    "Its editor opens on the Customs tab: stats, deck, cheat blocks, drops and forms. Press Save changes there when you are done.",
                    Row(Action(Primary("Make the custom"), MakeCustomAsync))),
            },
        };
        UpdateCustomWizard();

        var section = Fold("plus", "Customs", "Make a custom, step by step",
            "A custom is your own copy of a game creature, with its own name, stats, deck, cheats and drops. The game's creature never changes.",
            steps);
        section.SetBadge("Try it");

        return section;
    }

    /// <summary>One step of the wizard: a numbered circle (a tick once it is done), what to do, and its boxes.</summary>
    private Control WizardStep(int index, string title, string help, params Control[] body) {
        var mark = new Border {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top,
        };
        _customStepMarks[index] = mark;
        var content = new StackPanel {
            Spacing = 8,
            Children = {
                WizardTitle(index, $"Step {index + 1}.  {title}"),
                Sub(help),
            },
        };
        content.Children.AddRange(body);

        // A line runs down from each circle to the next, as in a list of steps.
        var rail = new DockPanel {
            Children = {
                WithDock(mark, Dock.Top),
                new Border { Width = 2, Background = index < 3 ? LINE : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) },
            },
        };

        return new DockPanel {
            Margin = new Thickness(0, 0, 0, index < 3 ? 6 : 0),
            Children = { WithDock(rail, Dock.Left), WithMargin(content, new Thickness(14, 4, 0, index < 3 ? 14 : 0)) },
        };
    }

    /// <summary>Ticks the steps that are done and shows the number and .joinme line the new custom will have.</summary>
    private void UpdateCustomWizard() {
        var next = 1;
        try {
            next = CustomMob.NextId(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The folder cannot be read; 1 is shown.
        }

        var picked = _customNewBase;
        _customNumberText.Text = $"It will be custom {next}.";
        _customJoinPreview.Text = picked is null ? $".joinme <creature ID> {next}" : $".joinme {picked.Id} {next}";
        _customPickedPortrait.Child = picked is null
            ? ManagerTheme.Tile("paw", ManagerTheme.Brush(ManagerTheme.MUTED, 90), 40)
            : MobPortrait(picked.Name, picked.School, 44);
        var done = new[] { picked is not null, picked is not null && (_customNewName.Text ?? "").Trim().Length > 0, picked is not null, false };
        var current = Array.IndexOf(done, false);
        for (var i = 0; i < _customStepMarks.Length; i++) {
            if (_customStepMarks[i] is not { } mark) {
                continue;
            }

            var color = done[i] ? ManagerTheme.GOOD : i == current ? ManagerTheme.AccentColor("Customs") : ManagerTheme.LINE_STRONG;
            mark.BorderBrush = ManagerTheme.Brush(color);
            mark.Background = done[i] || i == current ? ManagerTheme.Brush(color, (byte) (done[i] ? 255 : 60)) : null;
            mark.Child = done[i]
                ? new Border { Padding = new Thickness(5), Child = ManagerTheme.Icon("check", 16, Brushes.White) }
                : new TextBlock {
                    Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontWeight = FontWeight.Bold,
                    Foreground = i == current ? Brushes.White : MUTED, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
        }
    }

    private void RenderCustomBaseResults() {
        _customBaseResults.Children.Clear();
        var query = (_customBaseSearch.Text ?? "").Trim();
        if (_bossCatalog is null || query.Length == 0) {
            return;
        }

        if (_bossCatalog.Missing is not null) {
            _customBaseResults.Children.Add(Sub(_bossCatalog.Missing));

            return;
        }

        var found = _bossCatalog.Search(query, bossesOnly: false, new HashSet<string>(), 8, out var total);
        if (found.Count == 0) {
            _customBaseResults.Children.Add(Sub("No creature matches."));
        }

        foreach (var creature in found) {
            _customBaseResults.Children.Add(ManagerTheme.Item(highlighted: _customNewBase?.Id == creature.Id, content: new DockPanel {
                Children = {
                    WithDock(WithCenter(Action(Plain("Use"), () => {
                        _customNewBase = creature;
                        _customNewBaseText.Text = $"{creature.Name} (ID {creature.Id}, level {creature.Level}, {creature.Health:N0} health)";
                        _customNewBaseText.ClearValue(TextBlock.ForegroundProperty);
                        if ((_customNewName.Text ?? "").Trim().Length == 0) {
                            _customNewName.Text = creature.Name;
                        }

                        UpdateCustomWizard();
                        RenderCustomBaseResults();

                        return Task.CompletedTask;
                    })), Dock.Right),
                    WithDock(WithMargin(MobPortrait(creature.Name, creature.School, 36), new Thickness(0, 0, 12, 0)), Dock.Left),
                    WithCenter(new StackPanel {
                        Children = {
                            new TextBlock { Text = creature.Name, FontWeight = FontWeight.SemiBold },
                            new TextBlock {
                                Foreground = MUTED,
                                Text = string.Join("  ·  ", new[] {
                                    $"ID {creature.Id}", creature.Level > 0 ? $"level {creature.Level}" : "", creature.School,
                                    $"{creature.Health:N0} health", creature.IsBoss ? "boss" : "",
                                }.Where(t => t.Length > 0)),
                            },
                        },
                    }),
                },
            }));
        }

        if (total > found.Count) {
            _customBaseResults.Children.Add(Sub($"{total:N0} match; add words to narrow it down."));
        }
    }

    private Task MakeCustomAsync() {
        if (_customNewBase is not { } creature) {
            throw new PanelException("Search the creature it is based on and press Use first.");
        }

        var name = (_customNewName.Text ?? "").Trim();
        var custom = CustomMob.Create(_manager.CustomsFolder, creature.Id, name);
        _customNewBase = null;
        _customNewBaseText.Text = "No creature picked yet.";
        _customNewBaseText.Foreground = MUTED;
        _customNewName.Text = "";
        _customBaseSearch.Text = "";
        _customBaseResults.Children.Clear();
        OpenCustom(custom);
        UpdateCustomWizard();
        // The wizard is on the Guides tab and the custom's editor on the Customs tab, so it goes there.
        SelectTab("Customs");
        Dispatcher.UIThread.Post(() => _customsEditorHost.BringIntoView(), DispatcherPriority.Background);
        Say($"Made custom {custom.Id}. It fights as the game's {creature.Name} until you change it in its editor. Bring it in with {custom.JoinMe}.");

        return Task.CompletedTask;
    }

    private void OpenCustom(CustomMob custom) {
        if (_bossCatalog?.Creatures.FirstOrDefault(c => c.Id == custom.BaseId) is not { } creature) {
            throw new PanelException($"Creature {custom.BaseId} is not in the server's creature list. Start the server once so it makes the list.");
        }

        _bossResults.SelectedItem = null;
        OpenBossEditor(creature, _manager.CustomsFolder, custom.Id.ToString(CultureInfo.InvariantCulture), custom, _customsEditorHost);
        _customHeaderTitle.Text = $"Custom {custom.Id}: {custom.Shown}";
        _customHeaderBase.Text = $"Based on {creature.Name} (ID {creature.Id}). Below are its stats, deck and cheats; " +
                                 "empty boxes are the game's values for that creature.";
        _customJoinMe.Text = custom.JoinMe;
        _customName.Watermark = creature.Name;
        _customName.Text = custom.Name;
        _customDrops.Text = string.Join(", ", custom.Drops);
        var gameDrops = GameDrops(custom.BaseId);
        _customDrops.Watermark = gameDrops.Count > 0 ? "The creature's own drops" : "Only gold, as the creature";
        _customGameDrops.Text = gameDrops.Count > 0 ? "The creature's own: " + string.Join(", ", gameDrops) : "The creature drops only gold.";
        _customFormRows.Children.Clear();
        _customForms.Clear();
        foreach (var (form, id) in custom.Forms) {
            AddCustomFormRow(form, id.ToString(CultureInfo.InvariantCulture));
        }

        _customHeaderCard.IsVisible = true;
        RenderCustoms();
    }

    private void AddCustomFormRow(string creature, string custom) {
        var creatureBox = new TextBox { Width = 140, Text = creature, Watermark = "Creature ID" };
        var customBox = new TextBox { Width = 100, Text = custom, Watermark = "Custom" };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pair = (creatureBox, customBox);
        row.Children.Add(WithCenter(new TextBlock { Text = "When it turns into creature" }));
        row.Children.Add(creatureBox);
        row.Children.Add(WithCenter(new TextBlock { Text = "it is custom" }));
        row.Children.Add(customBox);
        row.Children.Add(Danger(Action(Plain("Remove"), () => {
            _customForms.Remove(pair);
            _customFormRows.Children.Remove(row);

            return Task.CompletedTask;
        })));
        _customForms.Add(pair);
        _customFormRows.Children.Add(row);
    }

    /// <summary>Puts the custom's own fields (number, creature, name, drops, forms) into the file Save writes.</summary>
    private void CustomHeaderInto(BossChanges changes, CustomMob custom) {
        changes.Name = (_customName.Text ?? "").Trim();
        changes.Extra["CustomID"] = custom.Id;
        changes.Extra["BaseTemplateID"] = ulong.Parse(custom.BaseId, CultureInfo.InvariantCulture);
        var drops = (_customDrops.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        changes.Extra.Remove("Drops");
        if (drops.Length > 0) {
            changes.Extra["Drops"] = new JsonArray([.. drops.Select(d => (JsonNode) JsonValue.Create(d)!)]);
        }

        var forms = new JsonObject();
        foreach (var (creatureBox, customBox) in _customForms) {
            var creature = (creatureBox.Text ?? "").Trim();
            var number = (customBox.Text ?? "").Trim();
            if (creature.Length == 0 && number.Length == 0) {
                continue;
            }

            if (!ulong.TryParse(creature, out _) || !int.TryParse(number, out var id) || id <= 0) {
                throw new PanelException("A form needs a creature ID and a custom number, such as 1336037 and 2.");
            }

            if (CustomMob.Load(_manager.CustomsFolder, id) is not { } form) {
                throw new PanelException($"There is no custom {id} for the form {creature}.");
            }

            if (form.BaseId != creature) {
                throw new PanelException($"Custom {id} is based on {CreatureName(form.BaseId)}, not on creature {creature}.");
            }

            forms[creature] = id;
        }

        changes.Extra.Remove("Forms");
        if (forms.Count > 0) {
            changes.Extra["Forms"] = forms;
        }
    }

    private async Task DeleteCustomAsync(CustomMob custom) {
        var rooms = SafeRooms().Where(r => r.Value.Values.Contains(custom.Id)).Select(r => r.Key).ToList();
        if (!await ConfirmAsync($"Delete custom {custom.Id} ({custom.Shown})? Its changes are gone for good" +
                                (rooms.Count > 0 ? $", and {string.Join(", ", rooms.Select(RoomName))} goes back to the game's boss." : "."))) {
            return;
        }

        File.Delete(CustomMob.FileOf(_manager.CustomsFolder, custom.Id));
        foreach (var room in rooms) {
            foreach (var (template, _) in SafeRooms()[room].Where(s => s.Value == custom.Id).ToList()) {
                CustomRooms.Set(_manager.CustomsFolder, room, template, 0);
            }
        }

        if (_editCustom?.Id == custom.Id) {
            _customsEditorHost.Child = null;
            _customHeaderCard.IsVisible = false;
            _editCustom = null;
            _boss = null;
        }

        RenderCustoms();
        RenderAreas();
        Say($"Custom {custom.Id} is deleted.");
    }

    // The Custom areas half of the tab is in ManagerWindow.CustomAreas.cs.

    private string CreatureName(string id)
        => _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == id) is { } c ? $"{c.Name} ({id})" : $"creature {id}";

    private static string RoomName(string room) => room.Contains('@') ? $"the {room[(room.IndexOf('@') + 1)..]} room" : room;

    private Dictionary<string, Dictionary<string, int>> SafeRooms() {
        try {
            return CustomRooms.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // The drop tables the game's creature drops (spiraldb-extra/NpcDropTable), for the custom's grey default.
    private List<string> GameDrops(string baseId) {
        try {
            var file = Path.Combine(_manager.ExtraFolder, "NpcDropTable", $"{baseId}.json");
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject json
                && BossChanges.FindIn(json, "DropTableNames") is JsonArray names) {
                return [.. names.Select(n => (n as JsonValue)?.ToString() ?? "").Where(n => n.Length > 0)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            // Shown as no drops.
        }

        return [];
    }

}
