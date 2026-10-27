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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's changes to the game's own vendors and trainers (Niduus, 2026-10-25: "add custom options for
/// existing vendors and trainers (In detail)"): pick one, then add to or replace what it sells and teaches, take things
/// out, set prices and its shop window, or hide its shop, treasure cards or spell list. Saved in
/// spiraldb-extra/Customs/NpcChanges.json; the server uses them the next time a player talks to the NPC.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _npcChangeRows = new() { Spacing = 6 };
    private readonly TextBlock _npcChangeCount = Sub("");
    private readonly Border _npcChangeEditorHost = new();

    // The NPC whose changes are open: a copy, saved by Save.
    private NpcChange? _npcChangeEdit;

    private Control NpcChangesCard() => Fold("bag", "Customs", "Change the game's vendors and trainers",
        "Change what an existing shop keeper sells or a trainer teaches, and for how much.",
        WithHint(Sub("Pick a vendor or trainer of the game. Add items, treasure cards or spells, take some out, set prices, or hide a shop."),
            "Pick any NPC of the game with the button below. For its shop, treasure cards and spells you can: add some to what it " +
            "already has, or make it sell only your list; take some of its own out; set a price (an item's 0 keeps its usual " +
            "price); pick its shop window; or hide that choice. Press Save: players see it the next time they talk to it, with " +
            "no restart. Every copy of that NPC in the game changes. An NPC that had no shop or spell list at all gets one the " +
            "next time its area loads. Undo all takes every change of that NPC away."),
        Row(new Button {
            Content = "Pick a vendor or trainer",
            Flyout = SearchFlyout("Search NPCs by name or ID", NpcOnlyResults, picked => OpenNpcChanges((uint) picked)),
        }),
        _npcChangeCount,
        _npcChangeRows);

    private List<NpcChange> SafeNpcChanges() {
        try {
            return NpcChanges.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    private List<(ulong Id, string Text)> NpcOnlyResults(string query)
        => [.. LookResults(query).Where(row => !row.Text.Contains("·  Creature  ·", StringComparison.Ordinal))];

    private void RenderNpcChanges() {
        _npcChangeRows.Children.Clear();
        var changes = SafeNpcChanges();
        _npcChangeCount.Text = changes.Count == 0 ? "No NPC of the game is changed yet."
            : changes.Count == 1 ? "1 NPC of the game is changed." : $"{changes.Count} NPCs of the game are changed.";
        foreach (var change in changes) {
            _npcChangeRows.Children.Add(ManagerTheme.Item(highlighted: _npcChangeEdit?.TemplateID == change.TemplateID, content: new DockPanel {
                Children = {
                    WithDock(Row(
                        Action(Primary("Edit"), () => {
                            OpenNpcChanges(change.TemplateID);

                            return Task.CompletedTask;
                        }),
                        Danger(Action(Plain("Undo all"), () => UndoNpcChangesAsync(change)))), Dock.Right),
                    WithDock(WithMargin(WithCenter(ManagerTheme.Tile("bag", ManagerTheme.Accent("Customs"), 32)),
                        new Thickness(0, 0, 12, 0)), Dock.Left),
                    new StackPanel {
                        Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = $"{ChangeName(change)}  ·  ID {change.TemplateID}", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                            new TextBlock { Text = change.Summary, Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            }));
        }
    }

    private string ChangeName(NpcChange change) => change.Name.Trim().Length > 0 ? change.Name.Trim() : LookName(change.TemplateID);

    /// <summary>Opens the changes of a game NPC (a new, empty set when it has none yet) and shows them.</summary>
    internal void OpenNpcChanges(uint templateId) {
        var change = SafeNpcChanges().FirstOrDefault(c => c.TemplateID == templateId)?.Copy()
            ?? new NpcChange { TemplateID = templateId, Name = LookName(templateId) };
        _npcChangeEdit = change;
        RenderNpcChangeEditor();
        RenderNpcChanges();
        ShowCustomPage("change", templateId.ToString(CultureInfo.InvariantCulture), "bag", $"Change {ChangeName(change)}",
            $"A vendor or trainer of the game, ID {change.TemplateID}. Every copy of it in the game changes.", CloseNpcChanges, _npcChangeEditorHost);
    }

    private void CloseNpcChanges() {
        _npcChangeEdit = null;
        _npcChangeEditorHost.Child = null;
    }

    private async Task UndoNpcChangesAsync(NpcChange change) {
        if (!await ConfirmAsync($"Take every change of {ChangeName(change)} away? It goes back to what the game has.")) {
            return;
        }

        NpcChanges.Set(_manager.CustomsFolder, change.TemplateID, null);
        if (_npcChangeEdit?.TemplateID == change.TemplateID) {
            CloseNpcChanges();
        }

        if (IsCustomPage("change", change.TemplateID.ToString(CultureInfo.InvariantCulture))) {
            BackToCustoms();
        }

        RenderNpcChanges();
        Say($"{ChangeName(change)} is back to what the game has.");
    }

    private void RenderNpcChangeEditor() {
        if (_npcChangeEdit is not { } change) {
            _npcChangeEditorHost.Child = null;

            return;
        }

        var body = new StackPanel {
            Spacing = 8,
            Children = {
                Sub("Each part below changes only what you set; leave a part as it is to keep the game's own."),
            },
        };

        // The shop.
        var windows = new List<(string Value, string Label)> { ("", "Its own window") };
        windows.AddRange(CustomNpc.WINDOWS.Where(w => w.Value is not ("Recipe" or CustomNpc.DECKATHALON)));
        var window = new ComboBox { Width = 360, ItemsSource = windows.Select(w => w.Label).ToList() };
        window.SelectedIndex = Math.Max(0, windows.FindIndex(w => string.Equals(w.Value, change.Window, StringComparison.OrdinalIgnoreCase)));
        window.SelectionChanged += (_, _) => change.Window = windows[Math.Max(0, window.SelectedIndex)].Value;
        body.Children.Add(ChangeSection("Shop",
            ChangeHide("Hide its shop", change.HideShop, v => change.HideShop = v),
            ChangeMode(change.ShopMode, v => change.ShopMode = v, "items"),
            Field("Shop window", "Which of the game's shop windows its shop opens.", window),
            Field("Items to add", "Each price is in gold. 0 keeps the item's usual price; an item it already sells gets this price.",
                EntryList(change.Items, "Item", "gold", "Add an item", RenderNpcChangeEditor, emptyPrice: 0)),
            Field("Items to take out", "Items of its own it no longer sells.", IdList(change.RemoveItems, "Item", "Take out an item"))));

        // The treasure card shop.
        body.Children.Add(ChangeSection("Treasure cards",
            ChangeHide("Hide its treasure card shop", change.HideTreasureCards, v => change.HideTreasureCards = v),
            ChangeMode(change.TreasureCardMode, v => change.TreasureCardMode = v, "treasure cards"),
            Field("Treasure cards to add", "Pick a spell and it sells that spell's treasure card. Each price is in gold; 0 keeps the card's usual price.",
                EntryList(change.TreasureCards, "Spell", "gold", "Add a treasure card", RenderNpcChangeEditor, emptyPrice: 0)),
            Field("Treasure cards to take out", "Cards of its own it no longer sells.", IdList(change.RemoveTreasureCards, "Spell", "Take out a card"))));

        // The spell list.
        body.Children.Add(ChangeSection("Training",
            ChangeHide("Hide its spell list", change.HideTraining, v => change.HideTraining = v),
            ChangeMode(change.SpellMode, v => change.SpellMode = v, "spells"),
            Field("Spells to add", "Each price is in training points (0 is free). Added spells need no level and no spell before them.",
                EntryList(change.Spells, "Spell", "training points", "Add a spell", RenderNpcChangeEditor)),
            Field("Spells to take out", "Spells of its own it no longer teaches.", IdList(change.RemoveSpells, "Spell", "Take out a spell"))));

        body.Children.Add(WithMargin(Row(
            Action(Primary("Save"), SaveNpcChangesAsync),
            Action(Plain("Close without saving"), () => {
                BackToCustoms();

                return Task.CompletedTask;
            })), new Thickness(0, 6, 0, 0)));

        _npcChangeEditorHost.Child = ManagerTheme.Item(highlighted: true, content: body);
    }

    private static Control ChangeSection(string title, params Control[] children) {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
        foreach (var child in children) {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static Control ChangeHide(string text, bool value, Action<bool> set) {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);

        return box;
    }

    private static Control ChangeMode(string value, Action<string> set, string what) {
        var box = new ComboBox { Width = 360, ItemsSource = new[] { $"Add these {what} to its own", $"Only these {what} (none of its own)" } };
        box.SelectedIndex = string.Equals(value, NpcChange.REPLACE, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        box.SelectionChanged += (_, _) => set(box.SelectedIndex == 1 ? NpcChange.REPLACE : NpcChange.ADD);

        return box;
    }

    // A list of IDs (items or spells to take out), each with Remove; then a search and an ID box to add one.
    private Control IdList(List<uint> list, string kind, string addText) {
        var panel = new StackPanel { Spacing = 4 };
        if (list.Count == 0) {
            panel.Children.Add(Sub("None."));
        }

        foreach (var id in list.ToList()) {
            panel.Children.Add(new DockPanel {
                Children = {
                    WithDock(Danger(Action(Plain("Remove"), () => {
                        list.Remove(id);
                        RenderNpcChangeEditor();

                        return Task.CompletedTask;
                    })), Dock.Right),
                    WithCenter(new TextBlock { Text = $"{EntryName(kind, id)}  ·  ID {id}", TextTrimming = TextTrimming.CharacterEllipsis }),
                },
            });
        }

        void Add(ulong id) {
            if (id == 0 || id > uint.MaxValue) {
                throw new PanelException($"{id} is not an ID.");
            }

            if (!list.Contains((uint) id)) {
                list.Add((uint) id);
            }

            RenderNpcChangeEditor();
        }

        var byId = new TextBox { Width = 140, Watermark = $"or type an {kind.ToLowerInvariant()} ID" };
        panel.Children.Add(Row(
            new Button {
                Content = addText,
                Flyout = SearchFlyout($"Search {kind.ToLowerInvariant()}s by name or ID", q => LookupResults(kind, q), id => _ = Guard(() => {
                    Add(id);

                    return Task.CompletedTask;
                }), spells: kind == "Spell"),
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

    private async Task SaveNpcChangesAsync() {
        if (_npcChangeEdit is not { } change) {
            return;
        }

        change.Name = change.Name.Trim();
        NpcChanges.Set(_manager.CustomsFolder, change.TemplateID, change.Copy());
        RenderNpcChanges();
        Say($"Saved the changes of {ChangeName(change)} ({change.Summary}). Players see them the next time they talk to it.");
        await Task.CompletedTask;
    }

}
