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
    private readonly StackPanel _areaRows = new() { Spacing = 10 };

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
        _customHeaderCard.Child = Card(new StackPanel {
            Spacing = 8,
            Children = {
                _customHeaderTitle,
                _customHeaderBase,
                StatRow("Bring it in", Row(_customJoinMe, Action(Plain("Copy"), () => CopyAsync(_customJoinMe.Text ?? ""))),
                    "Type this in a battle. Its stats, deck, cheats and drops count only for it."),
                StatRow("Name", _customName, "The name it shows in battle. Empty: the creature's own name."),
                StatRow("Drops", _customDrops, "Drop table names, with commas between. Empty: the creature's own drops."),
                WithMargin(_customGameDrops, new Thickness(190, 0, 0, 0)),
                WithMargin(Heading("When it changes form"), new Thickness(0, 8, 0, 0)),
                Sub("A boss that turns into another creature (Malistaire into Haunted Malistaire, creature 1336037) is that " +
                    "creature from then on. Name a custom of that creature here to have it fight as your custom instead."),
                _customFormRows,
                Row(Action(Plain("Add a form"), () => {
                    AddCustomFormRow("", "");

                    return Task.CompletedTask;
                })),
                Sub("Press Save changes at the bottom to keep the name, drops and forms with the rest."),
            },
        });

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
            Card(new StackPanel {
                Children = {
                    Heading("Custom areas"),
                    Sub("Places the server adds to the game (its ZoneObjects files): sigils, private rooms and their bosses. " +
                        "Pick who the boss of a room is; it counts from the next time a team goes in."),
                    Sub("Move, turn or resize an object with its boxes and Save: the running server moves it at once for " +
                        "everyone in the zone, with no restart. Use my position fills the boxes with where your wizard " +
                        "stands and faces (walk there in the game first). Your changes are kept apart from the files, so " +
                        "updates keep them; Put back returns an object to the file's place."),
                    WithMargin(_areaRows, new Thickness(0, 10, 0, 0)),
                },
            }),
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

    // ---------------------------------------------------------------------
    // Custom areas
    // ---------------------------------------------------------------------

    private void RenderAreas() {
        _areaRows.Children.Clear();
        var areas = CustomArea.All(Path.Combine(_manager.ExtraFolder, "ZoneObjects"));
        if (areas.Count == 0) {
            _areaRows.Children.Add(Sub("The server has no added areas (spiraldb-extra/ZoneObjects is empty)."));

            return;
        }

        var rooms = SafeRooms();
        var customs = CustomMob.All(_manager.CustomsFolder);
        foreach (var area in areas) {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock {
                Text = area.Variant.Length > 0 ? $"{area.Zone}  ·  a private copy ({area.Variant})" : area.Zone,
                FontWeight = FontWeight.SemiBold, FontSize = 15,
            });
            if (area.Notes.Length > 0) {
                panel.Children.Add(Sub(area.Notes.Length > 320 ? area.Notes[..320].TrimEnd() + "…" : area.Notes));
            }

            var placements = SafePlacements();
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in area.List("Objects")) {
                var name = CustomArea.Text(obj, "Name");
                fileNames.Add(name);
                var placement = CustomPlacements.Of(placements, area.Room, name);
                var isRemoved = placement is not null && BossChanges.FindIn(placement, "Removed") is JsonValue r && r.ToJsonString() == "true";
                panel.Children.Add(Bullet($"Object \"{name}\" (template {CustomArea.Number(Placed(obj, placement), "TemplateID")}) at " +
                                          $"{Place(Placed(obj, placement))}{(isRemoved ? ", removed in this tab" : placement is not null ? ", moved in this tab" : "")}."));
                if (BossChanges.FindIn(obj, "Sigil") is JsonObject sigil) {
                    var to = CustomArea.Text(sigil, "DestinationZone");
                    var variant = CustomArea.Text(sigil, "Variant");
                    panel.Children.Add(Bullet($"It is a sigil: it sends the team to {to} at {CustomArea.Text(sigil, "DestinationLoc")}" +
                                              (variant.Length > 0 ? $", into a fresh private copy ({variant})." : "."), 2));
                }

                panel.Children.Add(WithMargin(PlacementEditor(area, name, obj, placement, isRemoved, fromFile: true), new Thickness(16, 2, 0, 6)));
            }

            // Objects added in this tab, which the file does not have.
            if (BossChanges.FindIn(placements, area.Room) is JsonObject roomPlacements) {
                foreach (var (name, node) in roomPlacements) {
                    if (fileNames.Contains(name) || node is not JsonObject placement || CustomArea.Number(placement, "TemplateID") <= 0) {
                        continue;
                    }

                    panel.Children.Add(Bullet($"Object \"{name}\" (template {CustomArea.Number(placement, "TemplateID")}) at {Place(placement)}, added in this tab."));
                    panel.Children.Add(WithMargin(PlacementEditor(area, name, placement, placement, false, fromFile: false), new Thickness(16, 2, 0, 6)));
                }
            }

            panel.Children.Add(WithMargin(AddObjectEditor(area), new Thickness(16, 4, 0, 6)));

            foreach (var creature in area.List("Creatures")) {
                var id = ((ulong) CustomArea.Number(creature, "TemplateID")).ToString(CultureInfo.InvariantCulture);
                panel.Children.Add(Bullet($"Creature {CreatureName(id)} at {Place(creature)}."));
            }

            foreach (var encounter in area.List("Encounters")) {
                var boss = ((ulong) CustomArea.Number(encounter, "Boss")).ToString(CultureInfo.InvariantCulture);
                var minions = (BossChanges.FindIn(encounter, "Minions") as JsonArray)?.Count ?? 0;
                var count = (int) CustomArea.Number(encounter, "MinionCount");
                panel.Children.Add(Bullet($"Battle against {CreatureName(boss)}" +
                                          (count > 0 ? $": {count} of its {minions} helpers join it." : ".")));
                panel.Children.Add(WithMargin(BossPicker(area, boss, rooms, customs), new Thickness(16, 2, 0, 4)));
            }

            if (BossChanges.FindIn(area.Json, "Exit") is JsonObject exit && CustomArea.Text(exit, "Zone").Length > 0) {
                panel.Children.Add(Bullet($"Leaving goes to {CustomArea.Text(exit, "Zone")} at {CustomArea.Text(exit, "Loc")}."));
            }

            var removed = (BossChanges.FindIn(area.Json, "Remove") as JsonArray)?.Count ?? 0;
            if (removed > 0) {
                panel.Children.Add(Bullet($"Leaves out {removed} of the zone's own objects."));
            }

            panel.Children.Add(Sub($"File: {Path.GetFileName(area.File)}"));
            _areaRows.Children.Add(new Border {
                Padding = new Thickness(4, 8), BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Child = panel,
            });
        }
    }

    // Who a room's boss is: the game's creature, or one of the customs made from it.
    private Control BossPicker(CustomArea area, string boss, Dictionary<string, Dictionary<string, int>> rooms,
                               List<CustomMob> customs) {
        var choices = customs.Where(c => c.BaseId == boss).ToList();
        var items = new List<string> { $"{CreatureName(boss)} (the game's)" };
        items.AddRange(choices.Select(c => $"Custom {c.Id}: {c.Shown}"));
        var fromFile = BossChanges.FindIn(area.Json, "ReplaceSpawns") is JsonObject replace
                       && BossChanges.FindIn(replace, boss) is JsonValue v && int.TryParse(v.ToJsonString(), out var f) ? f : 0;
        var set = rooms.TryGetValue(area.Room, out var swaps) && swaps.TryGetValue(boss, out var s) ? s : 0;
        var now = set != 0 ? Math.Max(0, set) : fromFile;
        var combo = new ComboBox { Width = 360, ItemsSource = items, SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == now) + 1) };
        if (now > 0 && choices.All(c => c.Id != now)) {
            items.Add($"Custom {now} (missing)");
            combo.ItemsSource = items.ToList();
            combo.SelectedIndex = items.Count - 1;
        }

        combo.SelectionChanged += async (_, _) => await Guard(() => {
            var index = combo.SelectedIndex;
            if (index < 0 || index > choices.Count) {
                return Task.CompletedTask;
            }

            var custom = index == 0 ? (fromFile > 0 ? -1 : 0) : choices[index - 1].Id;
            CustomRooms.Set(_manager.CustomsFolder, area.Room, boss, custom);
            RenderCustoms();
            Say(index == 0
                ? $"{RoomName(area.Room)} has the game's {CreatureName(boss)} again, from the next time a team goes in."
                : $"{RoomName(area.Room)} has custom {choices[index - 1].Id} as its boss from the next time a team goes in.");

            return Task.CompletedTask;
        });

        return new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { WithCenter(new TextBlock { Text = "Boss of this room", FontWeight = FontWeight.Medium }), combo },
        };
    }

    // An object's place with this tab's placement over the file's.
    private static JsonObject Placed(JsonObject obj, JsonObject? placement) {
        var placed = new JsonObject();
        foreach (var key in new[] { "TemplateID", "X", "Y", "Z", "Yaw", "Scale" }) {
            var value = placement is not null && BossChanges.FindIn(placement, key) is JsonValue p ? p : BossChanges.FindIn(obj, key);
            if (value is JsonValue v) {
                placed[key] = JsonNode.Parse(v.ToJsonString());
            }
        }

        return placed;
    }

    private JsonObject SafePlacements() {
        try {
            return CustomPlacements.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    // X, Y, Z, rotation (degrees and yaw) and size boxes for one object, with Use my position, Save, Put back and Remove.
    private Control PlacementEditor(CustomArea area, string name, JsonObject obj, JsonObject? placement, bool removed, bool fromFile) {
        var placed = Placed(obj, placement);
        var boxes = PlaceBoxes(placed);
        var buttons = new List<Control> {
            Action(Plain("Use my position"), () => {
                FillFromMyPosition(area, boxes);

                return Task.CompletedTask;
            }),
            Action(Primary("Save"), () => {
                var saved = boxes.Read();
                if (!fromFile || CustomArea.Number(obj, "TemplateID") != CustomArea.Number(placed, "TemplateID")) {
                    saved["TemplateID"] = (ulong) CustomArea.Number(placed, "TemplateID");
                }

                CustomPlacements.Set(_manager.CustomsFolder, area.Room, name, saved);
                RenderAreas();
                Say($"\"{name}\" is saved at {Place(saved)}. The server moves it now for everyone in {area.Zone}.");

                return Task.CompletedTask;
            }),
        };
        if (fromFile && placement is not null) {
            buttons.Add(Action(Plain(removed ? "Put back" : "Put back to the file's place"), () => {
                CustomPlacements.Set(_manager.CustomsFolder, area.Room, name, null);
                RenderAreas();
                Say($"\"{name}\" is back at the file's place ({Place(obj)}). The server moves it now.");

                return Task.CompletedTask;
            }));
        }

        if (!removed) {
            buttons.Add(Danger(Action(Plain("Remove"), async () => {
                if (!await ConfirmAsync($"Take \"{name}\" out of {area.Zone}? " +
                                        (fromFile ? "Put back brings it again." : "It is gone for good."))) {
                    return;
                }

                CustomPlacements.Set(_manager.CustomsFolder, area.Room, name, fromFile ? new JsonObject { ["Removed"] = true } : null);
                RenderAreas();
                Say($"\"{name}\" is taken out. The server removes it now.");
            })));
        }

        return new StackPanel { Spacing = 6, Children = { boxes.Row, Row([.. buttons]) } };
    }

    // A new object: a name, a template and a place.
    private Control AddObjectEditor(CustomArea area) {
        var name = new TextBox { Width = 260, Watermark = "A name of its own, such as Shadow Portal 2" };
        var template = new TextBox { Width = 120, Watermark = "Template ID" };
        var boxes = PlaceBoxes(new JsonObject { ["Scale"] = 1 });

        return new StackPanel {
            Spacing = 6,
            Children = {
                new TextBlock { Text = "Add an object here", FontWeight = FontWeight.Medium },
                Row(name, template),
                boxes.Row,
                Row(
                    Action(Plain("Use my position"), () => {
                        FillFromMyPosition(area, boxes);

                        return Task.CompletedTask;
                    }),
                    Action(Primary("Add"), () => {
                        var objectName = (name.Text ?? "").Trim();
                        if (objectName.Length == 0) {
                            throw new PanelException("Give the object a name.");
                        }

                        if (!ulong.TryParse((template.Text ?? "").Trim(), out var templateId) || templateId == 0) {
                            throw new PanelException("The template ID is a number, such as 1719467 (the Shadow Portal).");
                        }

                        if (area.List("Objects").Concat(area.List("Creatures")).Any(o => string.Equals(CustomArea.Text(o, "Name"), objectName, StringComparison.OrdinalIgnoreCase))
                            || CustomPlacements.Of(SafePlacements(), area.Room, objectName) is not null) {
                            throw new PanelException($"There is already an object called \"{objectName}\" here.");
                        }

                        var saved = boxes.Read();
                        saved["TemplateID"] = templateId;
                        CustomPlacements.Set(_manager.CustomsFolder, area.Room, objectName, saved);
                        RenderAreas();
                        Say($"\"{objectName}\" is added at {Place(saved)}. The server puts it in {area.Zone} now.");

                        return Task.CompletedTask;
                    })),
            },
        };
    }

    private sealed record PlaceBoxesSet(Control Row, TextBox X, TextBox Y, TextBox Z, TextBox Degrees, TextBox Yaw, TextBox Scale) {

        internal JsonObject Read() {
            static double Number(TextBox box, string what) {
                if (!double.TryParse((box.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    || double.IsNaN(n) || double.IsInfinity(n)) {
                    throw new PanelException($"{what} is a number, such as -2340 or 0.5.");
                }

                return Math.Round(n, 3);
            }

            var scale = Number(Scale, "Size");
            if (scale <= 0) {
                throw new PanelException("Size is more than 0 (1 is the object's own size).");
            }

            return new JsonObject {
                ["X"] = Number(X, "X"), ["Y"] = Number(Y, "Y"), ["Z"] = Number(Z, "Z"), ["Yaw"] = Number(Yaw, "Yaw"), ["Scale"] = scale,
            };
        }

    }

    private static PlaceBoxesSet PlaceBoxes(JsonObject placed) {
        static TextBox Box(double value, double width) => new() {
            Width = width, Text = Math.Round(value, 3).ToString(CultureInfo.InvariantCulture),
        };
        static Control Labelled(string label, TextBox box) => new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { WithCenter(new TextBlock { Text = label, Foreground = MUTED }), box },
        };

        var yawValue = CustomArea.Number(placed, "Yaw");
        var x = Box(CustomArea.Number(placed, "X"), 90);
        var y = Box(CustomArea.Number(placed, "Y"), 90);
        var z = Box(CustomArea.Number(placed, "Z"), 80);
        var degrees = Box((yawValue * 180.0 / Math.PI % 360 + 360) % 360, 70);
        var yaw = Box(yawValue, 80);
        var scale = Box(placed.ContainsKey("Scale") ? CustomArea.Number(placed, "Scale") : 1, 60);

        // Degrees and yaw are one rotation: typing in either fills the other.
        var syncing = false;
        degrees.TextChanged += (_, _) => {
            if (!syncing && double.TryParse(degrees.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) {
                syncing = true;
                yaw.Text = Math.Round(d * Math.PI / 180.0, 3).ToString(CultureInfo.InvariantCulture);
                syncing = false;
            }
        };
        yaw.TextChanged += (_, _) => {
            if (!syncing && double.TryParse(yaw.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) {
                syncing = true;
                degrees.Text = Math.Round((r * 180.0 / Math.PI % 360 + 360) % 360, 1).ToString(CultureInfo.InvariantCulture);
                syncing = false;
            }
        };

        var row = new WrapPanel {
            Orientation = Orientation.Horizontal,
            Children = {
                WithMargin(Labelled("X", x), new Thickness(0, 0, 12, 4)),
                WithMargin(Labelled("Y", y), new Thickness(0, 0, 12, 4)),
                WithMargin(Labelled("Z", z), new Thickness(0, 0, 12, 4)),
                WithMargin(Labelled("Rotation °", degrees), new Thickness(0, 0, 12, 4)),
                WithMargin(Labelled("Yaw", yaw), new Thickness(0, 0, 12, 4)),
                WithMargin(Labelled("Size", scale), new Thickness(0, 0, 12, 4)),
            },
        };

        return new PlaceBoxesSet(row, x, y, z, degrees, yaw, scale);
    }

    // Where the wizard who moved last stands and faces, from the server's logs/positions.txt (the Play tab's Position).
    private void FillFromMyPosition(CustomArea area, PlaceBoxesSet boxes) {
        string[] lines;
        try {
            lines = File.Exists(PositionFile) ? File.ReadAllLines(PositionFile) : [];
        }
        catch (IOException) {
            throw new PanelException("The position file is being written; press again.");
        }

        var parts = lines.Select(l => l.Split('\t')).FirstOrDefault(p => p.Length >= 7)
                    ?? throw new PanelException("There is no position yet. Start the server, go into the game and walk a step.");
        if (!string.Equals(parts[1], area.Zone, StringComparison.OrdinalIgnoreCase)) {
            throw new PanelException($"{parts[0]} is in {parts[1]}, not {area.Zone}. Go there in the game and walk a step.");
        }

        syncFill(boxes.X, parts[2]);
        syncFill(boxes.Y, parts[3]);
        syncFill(boxes.Z, parts[4]);
        syncFill(boxes.Yaw, parts[5]);
        Say($"Filled with where {parts[0]} stands and faces (at {parts[6]}). Press Save to put the object there.");

        static void syncFill(TextBox box, string value) => box.Text = value;
    }

    private static TextBlock Bullet(string text, int level = 1) => new() {
        Text = "•  " + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8 * level, 0, 0, 0),
    };

    // "X -2340, Y 4812, Z -28, facing 311°" from an object's X, Y, Z and Yaw (radians).
    private static string Place(JsonObject obj) {
        string N(string key) => Math.Round(CustomArea.Number(obj, key), 1).ToString(CultureInfo.InvariantCulture);
        var degrees = (CustomArea.Number(obj, "Yaw") * 180.0 / Math.PI % 360 + 360) % 360;

        return $"X {N("X")}, Y {N("Y")}, Z {N("Z")}, facing {Math.Round(degrees)}° (yaw {N("Yaw")})";
    }

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
