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
/// The Customs tab (Justin, 2026-10-22: "a new Custom's tab in the manager for this custom area and the custom
/// malistaire I plan to make"). Custom mobs: a game creature with its own number, name, stats, deck, cheats and drops,
/// which count only when it is brought in as that custom (.joinme, or a room set to it); the base creature never
/// changes. Custom areas: the server's added zones and objects (spiraldb-extra/ZoneObjects), and which boss each room
/// spawns.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Border _customsEditorHost = new();
    private readonly StackPanel _customRows = new() { Spacing = 2 };
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

        return Page(
            Card(new StackPanel {
                Children = {
                    Heading("Custom mobs"),
                    Sub("A custom is its own creature, made from a game creature: it has a number, a name, and stats, a deck, " +
                        "cheats and drops of its own. Changing it never changes the game's creature or the Bosses tab. " +
                        "Bring it into a battle with .joinme, the creature and its number, such as .joinme 1336035 1."),
                    WithMargin(_customCount, new Thickness(0, 8, 0, 0)),
                    WithMargin(_customRows, new Thickness(0, 6, 0, 0)),
                    WithMargin(Heading("New custom"), new Thickness(0, 16, 0, 0)),
                    Sub("1. Search the creature it is based on and press Use.  2. Give it a name.  3. Press Make the custom."),
                    WithMargin(_customBaseSearch, new Thickness(0, 6, 0, 0)),
                    WithMargin(_customBaseResults, new Thickness(0, 4, 0, 0)),
                    WithMargin(StatRow("Based on", _customNewBaseText, ""), new Thickness(0, 8, 0, 0)),
                    WithMargin(StatRow("Name", _customNewName, ""), new Thickness(0, 6, 0, 0)),
                    WithMargin(Row(Action(Primary("Make the custom"), MakeCustomAsync)), new Thickness(0, 8, 0, 0)),
                },
            }),
            CustomAreasCard(),
            _customHeaderCard,
            _customsEditorHost);
    }

    private async Task LoadCustomsAsync() {
        await LoadBossesAsync();
        // The editor may have moved to the Bosses tab since.
        _customHeaderCard.IsVisible = _bossEditor.Parent == _customsEditorHost && _editCustom is not null;
        RenderCustoms();
        RenderAreas();
    }

    private void RenderCustoms() {
        _customRows.Children.Clear();
        var customs = CustomMob.All(_manager.CustomsFolder);
        var rooms = SafeRooms();
        _customCount.Text = customs.Count == 0 ? "No customs yet. Make one below." : customs.Count == 1 ? "1 custom." : $"{customs.Count} customs.";
        foreach (var custom in customs) {
            var usedIn = rooms.Where(r => r.Value.Values.Contains(custom.Id)).Select(r => r.Key).ToList();
            var details = new[] {
                $"based on {CreatureName(custom.BaseId)}",
                custom.JoinMe,
                usedIn.Count > 0 ? "boss of " + string.Join(", ", usedIn.Select(RoomName)) : "",
            }.Where(t => t.Length > 0);
            var editing = _editCustom?.Id == custom.Id && _bossEditor.Parent == _customsEditorHost;
            _customRows.Children.Add(new Border {
                Padding = new Thickness(4, 6),
                BorderBrush = LINE,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = editing ? LINE : null,
                Child = new DockPanel {
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
                        new StackPanel {
                            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                new TextBlock { Text = $"{custom.Id}.  {custom.Shown}", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                                new TextBlock { Text = string.Join("  ·  ", details), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                            },
                        },
                    },
                },
            });
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
            _customBaseResults.Children.Add(new DockPanel {
                Children = {
                    WithDock(Action(Plain("Use"), () => {
                        _customNewBase = creature;
                        _customNewBaseText.Text = $"{creature.Name} (ID {creature.Id}, level {creature.Level}, {creature.Health:N0} health)";
                        _customNewBaseText.ClearValue(TextBlock.ForegroundProperty);
                        if ((_customNewName.Text ?? "").Trim().Length == 0) {
                            _customNewName.Text = creature.Name;
                        }

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithCenter(new TextBlock {
                        Text = string.Join("  ·  ", new[] {
                            creature.Name, $"ID {creature.Id}", creature.Level > 0 ? $"level {creature.Level}" : "",
                            $"{creature.Health:N0} health", creature.IsBoss ? "boss" : "",
                        }.Where(t => t.Length > 0)),
                    }),
                },
            });
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
        Say($"Made custom {custom.Id}. It fights as the game's {creature.Name} until you change it below. Bring it in with {custom.JoinMe}.");

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
