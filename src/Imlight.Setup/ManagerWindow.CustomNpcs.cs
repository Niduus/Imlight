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
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's vendors and trainers (Niduus, 2026-10-23: "Think we can implement custom Vendors/Trainer in the
/// Custom section?"): an NPC of your own where your wizard stands, with any NPC or creature's look, that sells your
/// items for gold or teaches your spells for training points. Saved in spiraldb-extra/Customs/VendorsTrainers.json;
/// the server puts them in the game at once, as it does the duel circles.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _npcRows = new() { Spacing = 6 };
    private readonly TextBlock _npcCount = Sub("");
    private readonly Border _npcEditorHost = new();

    // The vendor or trainer being edited: a copy, saved by Save.
    private CustomNpc? _npcEdit;
    private bool _npcListsLoading;

    // The ready-made ones the server wrote (presets.json), read again when the tab is drawn.
    private readonly ComboBox _npcPresets = new() { Width = 420 };
    private readonly TextBlock _npcPresetHelp = new() { Foreground = MUTED, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private List<CustomNpcPreset> _npcPresetList = [];

    private Control CustomNpcsCard() => Fold("bag", "Customs", "Vendors and trainers",
        "Shopkeepers and trainers of your own. Stand where one should go in the game, then press Add.",
        WithHint(Sub("A vendor sells the items you pick for gold. A trainer teaches the spells you pick for training points."),
            "Go into the game and stand where it should be, facing the way it should face, and walk a step. Then press " +
            "Add a vendor here or Add a trainer here: it appears in the game at once, where you stand. Press Edit to " +
            "give it a name, a look and its items or spells, then Save. Players press X near it to open its shop or " +
            "spell list. Everything here counts at once; the server needs no restart."),
        Row(Action(Primary("Add a vendor here"), () => AddNpcAsync(trainer: false)),
            Action(Plain("Add a trainer here"), () => AddNpcAsync(trainer: true))),
        PresetRow(),
        _npcCount,
        _npcRows,
        _npcEditorHost);

    private List<CustomNpc> SafeNpcs() {
        try {
            return CustomNpcs.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    // Niduus (2026-10-23): "make Presets for Trainers and Vendors". A preset fills a new one; it is then edited and
    // moved like any other.
    private Control PresetRow() {
        _npcPresets.SelectionChanged += (_, _) => _npcPresetHelp.Text =
            _npcPresets.SelectedIndex is var i and >= 0 && i < _npcPresetList.Count ? _npcPresetList[i].Help : "";

        return new StackPanel {
            Spacing = 2,
            Children = {
                Row(WithCenter(new TextBlock { Text = "Or a ready-made one:" }), _npcPresets,
                    Action(Plain("Add this preset here"), AddPresetAsync)),
                _npcPresetHelp,
            },
        };
    }

    private void LoadNpcPresets() {
        var picked = _npcPresets.SelectedIndex >= 0 && _npcPresets.SelectedIndex < _npcPresetList.Count ? _npcPresetList[_npcPresets.SelectedIndex].Name : null;
        try {
            _npcPresetList = CustomNpcPreset.Load(_manager.IdLookupFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _npcPresetList = [];
        }

        _npcPresets.ItemsSource = _npcPresetList.Count > 0
            ? _npcPresetList.Select(p => p.Label).ToList()
            : new List<string> { "None yet: start the server once and it makes them" };
        _npcPresets.SelectedIndex = Math.Max(0, _npcPresetList.FindIndex(p => p.Name == picked));
    }

    private async Task AddPresetAsync() {
        if (_npcPresets.SelectedIndex < 0 || _npcPresets.SelectedIndex >= _npcPresetList.Count) {
            throw new PanelException("There are no presets yet. Start the server once (it makes them from the game files), then come back to this tab.");
        }

        var preset = _npcPresetList[_npcPresets.SelectedIndex];
        var (who, zone, x, y, z, yaw) = MySpot();
        var npc = preset.ToNpc(CustomNpcs.NextId(SafeNpcs()));
        (npc.Zone, npc.X, npc.Y, npc.Z, npc.Yaw) = (zone, x, y, z, yaw);
        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, npc);
        RenderNpcs();
        OpenNpc(npc);
        Say($"Added {npc.Shown} where {who} stands in {ZoneDisplay(zone)}, with {(npc.IsTrainer ? Plural(npc.Spells.Count, "spell") : Plural(npc.Items.Count, "item"))} " +
            "at 1 each. It is in the game now; change anything below and press Save.");
        await Task.CompletedTask;
    }

    private void RenderNpcs() {
        LoadNpcPresets();
        // The names come from the Lookup tab's lists and the area pickers' templates; they are read once.
        if (_lookup is null && !_npcListsLoading) {
            _npcListsLoading = true;
            _ = Guard(async () => {
                await LoadLookupAsync();
                _npcListsLoading = false;
                RenderNpcs();
            });
        }

        _npcRows.Children.Clear();
        var npcs = SafeNpcs();
        _npcCount.Text = npcs.Count == 0 ? "None yet." : npcs.Count == 1 ? "1 vendor or trainer." : $"{npcs.Count} vendors and trainers.";
        foreach (var npc in npcs) {
            var details = new[] {
                npc.IsTrainer ? $"Trainer, {Plural(npc.Spells.Count, "spell")}"
                    : $"Vendor, {Plural(npc.Items.Count, "item")}{(npc.TreasureCards.Count > 0 ? $" and {Plural(npc.TreasureCards.Count, "treasure card")}" : "")}",
                ZoneDisplay(npc.Zone),
                $"looks like {LookName(npc.TemplateID)}",
            };
            _npcRows.Children.Add(ManagerTheme.Item(highlighted: _npcEdit?.Id == npc.Id, content: new DockPanel {
                Children = {
                    WithDock(Row(
                        Action(Primary("Edit"), () => {
                            OpenNpc(npc);

                            return Task.CompletedTask;
                        }),
                        Action(Plain("Move to my spot"), () => MoveNpcAsync(npc)),
                        Danger(Action(Plain("Delete"), () => DeleteNpcAsync(npc)))), Dock.Right),
                    WithDock(WithMargin(WithCenter(ManagerTheme.Tile(npc.IsTrainer ? "school" : "bag", ManagerTheme.Accent("Customs"), 32)),
                        new Thickness(0, 0, 12, 0)), Dock.Left),
                    new StackPanel {
                        Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = $"{npc.Id}.  {npc.Shown}", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                            new TextBlock { Text = string.Join("  ·  ", details), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            }));
        }
    }

    private static string Plural(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";

    private async Task AddNpcAsync(bool trainer) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var npcs = SafeNpcs();
        var npc = new CustomNpc {
            Id = CustomNpcs.NextId(npcs), Kind = trainer ? CustomNpc.TRAINER : CustomNpc.VENDOR,
            Name = trainer ? "Spell Trainer" : "Shopkeeper", Zone = zone, X = x, Y = y, Z = z, Yaw = yaw,
        };
        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, npc);
        RenderNpcs();
        OpenNpc(npc);
        Say($"Added {(trainer ? "trainer" : "vendor")} {npc.Id} where {who} stands in {ZoneDisplay(zone)}. It is in the game now. " +
            $"Give it its {(trainer ? "spells" : "items")} below and press Save.");
        await Task.CompletedTask;
    }

    private Task MoveNpcAsync(CustomNpc npc) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var moved = npc.Copy();
        (moved.Zone, moved.X, moved.Y, moved.Z, moved.Yaw) = (zone, x, y, z, yaw);
        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, moved);
        if (_npcEdit?.Id == npc.Id) {
            (_npcEdit.Zone, _npcEdit.X, _npcEdit.Y, _npcEdit.Z, _npcEdit.Yaw) = (zone, x, y, z, yaw);
        }

        RenderNpcs();
        Say($"{npc.Shown} now stands where {who} stands in {ZoneDisplay(zone)}.");

        return Task.CompletedTask;
    }

    private async Task DeleteNpcAsync(CustomNpc npc) {
        if (!await ConfirmAsync($"Delete {npc.Shown}? It leaves the game at once.")) {
            return;
        }

        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, null);
        if (_npcEdit?.Id == npc.Id) {
            CloseNpc();
        }

        RenderNpcs();
        Say($"{npc.Shown} is deleted.");
    }

    // Where the wizard stands, in any zone (the position tool's file).
    private (string Who, string Zone, double X, double Y, double Z, double Yaw) MySpot() {
        var parts = MyPositionLine("");
        if (parts[1].Trim().Length == 0) {
            throw new PanelException("Your zone is not known yet. Walk a step in the game and press again.");
        }

        static double D(string text) => double.Parse(text, CultureInfo.InvariantCulture);

        return (parts[0], parts[1].Trim(), D(parts[2]), D(parts[3]), D(parts[4]), D(parts[5]));
    }

    // ---------------------------------------------------------------------
    // The editor
    // ---------------------------------------------------------------------

    private void OpenNpc(CustomNpc npc) {
        _npcEdit = npc.Copy();
        RenderNpcEditor();
        RenderNpcs();
        Dispatcher.UIThread.Post(() => _npcEditorHost.BringIntoView(), DispatcherPriority.Background);
    }

    private void CloseNpc() {
        _npcEdit = null;
        _npcEditorHost.Child = null;
    }

    private void RenderNpcEditor() {
        if (_npcEdit is not { } npc) {
            _npcEditorHost.Child = null;

            return;
        }

        var name = new TextBox { Width = 360, Text = npc.Name, Watermark = "Its name, such as Gamma's Gear", HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => npc.Name = name.Text ?? "";

        var kind = new ComboBox { Width = 360, ItemsSource = new[] { "Vendor: sells items for gold", "Trainer: teaches spells for training points" } };
        kind.SelectedIndex = npc.IsTrainer ? 1 : 0;
        kind.SelectionChanged += (_, _) => {
            var trainer = kind.SelectedIndex == 1;
            if (trainer != npc.IsTrainer) {
                npc.Kind = trainer ? CustomNpc.TRAINER : CustomNpc.VENDOR;
                RenderNpcEditor();
            }
        };

        var lookName = new TextBlock { Text = $"{LookName(npc.TemplateID)} (ID {npc.TemplateID})", VerticalAlignment = VerticalAlignment.Center };
        var look = Row(lookName,
            new Button {
                Content = "Change the look",
                Flyout = SearchFlyout("Search NPCs and creatures by name or ID", LookResults, picked => {
                    npc.TemplateID = picked;
                    lookName.Text = $"{LookName(picked)} (ID {picked})";
                }),
            },
            Action(Plain("Shopkeeper look"), () => {
                npc.TemplateID = CustomNpc.DEFAULT_LOOK;
                lookName.Text = $"{LookName(npc.TemplateID)} (ID {npc.TemplateID})";

                return Task.CompletedTask;
            }));

        var body = new StackPanel {
            Spacing = 8,
            Children = {
                new TextBlock { Text = $"Editing {(npc.IsTrainer ? "trainer" : "vendor")} {npc.Id}", FontSize = 18, FontWeight = FontWeight.SemiBold },
                Sub($"It stands in {ZoneDisplay(npc.Zone)}. Move to my spot puts it where your wizard stands."),
                Field("Name", "The name over its head and in its window.", name),
                Field("Kind", "A vendor opens a shop. A trainer opens a spell list.", kind),
                Field("Look", "Any NPC or creature of the game. A creature only lends its look: it never starts a battle.", look),
            },
        };

        if (npc.IsTrainer) {
            body.Children.Add(Field("Spells", "Each spell's price is in training points (1 if you leave it). Players see only the spells they do not have yet, 50 at a time; walking away and back shows the next 50.",
                EntryList(npc.Spells, "Spell", "training points", "Add a spell")));
        }
        else {
            var window = new ComboBox { Width = 360, ItemsSource = CustomNpc.WINDOWS.Select(w => w.Label).ToList() };
            window.SelectedIndex = Math.Max(0, Array.FindIndex(CustomNpc.WINDOWS, w => string.Equals(w.Value, npc.Window, StringComparison.OrdinalIgnoreCase)));
            window.SelectionChanged += (_, _) => npc.Window = CustomNpc.WINDOWS[Math.Max(0, window.SelectedIndex)].Value;
            body.Children.Add(Field("Shop window", "Which of the game's shop windows opens, the one its own shop keepers of that kind of goods open. " +
                                                   "Automatic picks the kind most of the items are. Snacks, reagents and jewels each show only in their own window.", window));
            body.Children.Add(Field("Items", "Each item's price is in gold (1 if you leave it). The game's window may still show the item's usual price; " +
                                             "buying takes the price set here.",
                EntryList(npc.Items, "Item", "gold", "Add an item")));
            body.Children.Add(Field("Treasure cards", "Sold in the game's treasure card shop, a second choice when players talk to it. Each price is in gold " +
                                                      "(1 if you leave it). Pick a spell and it sells that spell's treasure card.",
                EntryList(npc.TreasureCards, "Spell", "gold", "Add a treasure card")));
        }

        body.Children.Add(WithMargin(Row(
            Action(Primary("Save"), SaveNpcAsync),
            Action(Plain("Close without saving"), () => {
                CloseNpc();
                RenderNpcs();

                return Task.CompletedTask;
            })), new Thickness(0, 6, 0, 0)));

        _npcEditorHost.Child = ManagerTheme.Item(highlighted: true, content: body);
    }

    // The items or spells of the NPC: each with its name, ID, price and Remove; then a search and an ID box to add one.
    private Control EntryList(List<CustomNpcEntry> list, string kind, string unit, string addText) {
        var panel = new StackPanel { Spacing = 4 };
        if (list.Count == 0) {
            panel.Children.Add(Sub($"None yet. {addText} below."));
        }

        foreach (var entry in list) {
            var price = new TextBox { Width = 70, Text = entry.Price.ToString(CultureInfo.InvariantCulture), Watermark = "1" };
            var normal = price.BorderBrush;
            price.TextChanged += (_, _) => {
                var text = (price.Text ?? "").Trim();
                var ok = text.Length == 0 || (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0);
                entry.Price = text.Length == 0 ? 1 : ok ? int.Parse(text, CultureInfo.InvariantCulture) : entry.Price;
                price.BorderBrush = ok ? normal : BAD;
            };
            panel.Children.Add(new DockPanel {
                Children = {
                    WithDock(Row(price, WithCenter(new TextBlock { Text = unit, Foreground = MUTED }),
                        Danger(Action(Plain("Remove"), () => {
                            list.Remove(entry);
                            RenderNpcEditor();

                            return Task.CompletedTask;
                        }))), Dock.Right),
                    WithCenter(new TextBlock { Text = $"{EntryName(kind, entry.ID)}  ·  ID {entry.ID}", TextTrimming = TextTrimming.CharacterEllipsis }),
                },
            });
        }

        void Add(ulong id) {
            if (id == 0 || id > uint.MaxValue) {
                throw new PanelException($"{id} is not an ID.");
            }

            if (list.Any(e => e.ID == id)) {
                Say("That one is in the list already.");

                return;
            }

            list.Add(new CustomNpcEntry { ID = (uint) id, Price = 1 });
            RenderNpcEditor();
        }

        var byId = new TextBox { Width = 140, Watermark = $"or type an {kind.ToLowerInvariant()} ID" };
        panel.Children.Add(Row(
            new Button {
                Content = addText,
                Flyout = SearchFlyout($"Search {kind.ToLowerInvariant()}s by name or ID", q => LookupResults(kind, q), id => _ = Guard(() => {
                    Add(id);

                    return Task.CompletedTask;
                })),
            },
            byId,
            Action(Plain("Add this ID"), () => {
                if (!ulong.TryParse((byId.Text ?? "").Trim(), out var id)) {
                    throw new PanelException($"Type the {kind.ToLowerInvariant()}'s ID, a number (the Lookup tab finds it).");
                }

                Add(id);

                return Task.CompletedTask;
            })));

        return panel;
    }

    private async Task SaveNpcAsync() {
        if (_npcEdit is not { } npc) {
            return;
        }

        if (npc.Name.Trim().Length == 0) {
            throw new PanelException("Give it a name.");
        }

        // The list in the file may have changed since it was opened (moved from the list): its place is the file's.
        if (SafeNpcs().FirstOrDefault(n => n.Id == npc.Id) is { } saved) {
            (npc.Zone, npc.X, npc.Y, npc.Z, npc.Yaw) = (saved.Zone, saved.X, saved.Y, saved.Z, saved.Yaw);
        }

        npc.Name = npc.Name.Trim();
        CustomNpcs.Set(_manager.CustomsFolder, npc.Id, npc.Copy());
        RenderNpcs();
        Say($"Saved {npc.Shown}. It is in the game with its new {(npc.IsTrainer ? "spells" : "items")} now; " +
            "a player standing at it walks away and back to see them.");
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // Names and searches
    // ---------------------------------------------------------------------

    private string LookName(ulong id) {
        var key = id.ToString(CultureInfo.InvariantCulture);
        if (_areaCatalog?.NameOf(id) is { Length: > 0 } name) {
            return name;
        }

        return (_lookup?.ById("NPC", key) ?? _lookup?.ById("Creature", key))?.Name ?? (id == CustomNpc.DEFAULT_LOOK ? "Elmer Meadowgrass" : $"template {id}");
    }

    private string EntryName(string kind, uint id) => _lookup?.ById(kind, id.ToString(CultureInfo.InvariantCulture))?.Name ?? $"{kind} {id}";

    private List<(ulong Id, string Text)> LookResults(string query) {
        var rows = new List<(ulong, string)>();
        if (_areaCatalog is not null) {
            rows.AddRange(_areaCatalog.Search(query, 20, t => t.Kind is "NPC" or "Creature").Select(t => (t.Id, $"{t.Label}  ·  {t.Kind}  ·  ID {t.Id}")));
        }

        if (rows.Count == 0 && _lookup is not null) {
            foreach (var kind in new[] { "NPC", "Creature" }) {
                rows.AddRange(_lookup.Search(kind, query, 10, out _).Where(e => ulong.TryParse(e.Id, out _))
                    .Select(e => (ulong.Parse(e.Id, CultureInfo.InvariantCulture), $"{e.Name}  ·  {kind}  ·  ID {e.Id}")));
            }
        }

        return rows;
    }

    private List<(ulong Id, string Text)> LookupResults(string kind, string query)
        => _lookup is null ? [] : [.. _lookup.Search(kind, query, 20, out _).Where(e => ulong.TryParse(e.Id, out _))
            .Select(e => (ulong.Parse(e.Id, CultureInfo.InvariantCulture), string.Join("  ·  ", new[] { e.Name, e.Detail, $"ID {e.Id}" }.Where(t => t.Length > 0))))];

    // A search box with its results under it; pressing a result picks it.
    private Flyout SearchFlyout(string watermark, Func<string, List<(ulong Id, string Text)>> search, Action<ulong> picked) {
        var box = new TextBox { Width = 420, Watermark = watermark };
        var results = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        void Fill() {
            results.Children.Clear();
            var query = (box.Text ?? "").Trim();
            if (query.Length == 0) {
                results.Children.Add(Sub("Type a name or an ID."));

                return;
            }

            var rows = search(query);
            if (rows.Count == 0) {
                results.Children.Add(Sub(_lookup?.Missing ?? "Nothing matches."));
            }

            foreach (var (id, text) in rows) {
                var b = new Button {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                    Content = new TextBlock { Text = text },
                };
                b.Click += (_, _) => {
                    flyout.Hide();
                    picked(id);
                };
                results.Children.Add(b);
            }
        }

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        delay.Tick += (_, _) => {
            delay.Stop();
            Fill();
        };
        box.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        flyout.Opening += (_, _) => Fill();
        flyout.Content = new StackPanel { Spacing = 6, Width = 460, Children = { box, new ScrollViewer { MaxHeight = 360, Content = results } } };

        return flyout;
    }

}
