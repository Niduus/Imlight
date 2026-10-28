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
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Home's "Lost and removed items" card (Niduus, 2026-10-28: "some items are disappearing in inventories"). The server
/// writes a line to item-history.jsonl for every item that leaves a wizard, with a copy of it kept in the database
/// (ItemHistory). The card lists them, newest first, and "Give back" writes the line's id to
/// item-restore-requests.txt: the server hands the item over the next time that wizard logs in or changes area.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ITEM_HISTORY_FILE = "item-history.jsonl";
    private const string ITEM_RESTORE_FILE = "item-restore-requests.txt";
    private const int LOST_ROWS_SHOWN = 60;

    private readonly StackPanel _lostRows = new() { Spacing = 8 };
    private readonly TextBlock _lostLine = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _lostShowAll = new() { Content = "Also show items that were sold, thrown away, used up or moved on purpose" };
    private DateTime _lostFileTime;
    private bool _lostReading;
    private bool _lostShown;
    private readonly HashSet<string> _lostAsked = [];

    private sealed record LostRow(string Id, DateTime Time, string CharId, string Name, string Kind, string Reason, bool Copy, string Of);

    private Control HomeLostItems() {
        _lostLine.Text = "Reading the item history…";
        _lostShowAll.IsCheckedChanged += (_, _) => {
            _lostShown = false;
            RefreshLostItems();
        };

        return Fold("shield", "Accounts", "Lost and removed items",
            "Every item that left a wizard: lost with no room, or gone with no reason the server knows, and with the tick also sold, thrown away, used up or put in a house. The server keeps a copy of each one, and Give back returns it.",
            _lostLine, _lostShowAll, _lostRows);
    }

    /// <summary>With every status check: reads the history again when the server wrote to it.</summary>
    private async void RefreshLostItems() {
        if (_lostReading) {
            return;
        }

        _lostReading = true;
        try {
            var file = Path.Combine(_manager.ServerFolder, ITEM_HISTORY_FILE);
            var time = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (_lostShown && time == _lostFileTime) {
                return;
            }

            var wizardFile = Path.Combine(_manager.ServerFolder, "wizard-list.json");
            var (rows, names) = await Task.Run(() => (ReadLostRows(file), ReadWizardNames(wizardFile)));
            _lostFileTime = time;
            _lostShown = true;
            ShowLostItems(rows, names);
        }
        catch (Exception) {
            // The server is writing it: the next check reads it. Never take the window down.
        }
        finally {
            _lostReading = false;
        }
    }

    private static List<LostRow> ReadLostRows(string file) {
        if (!File.Exists(file)) {
            return [];
        }

        var rows = new List<LostRow>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) {
            if (line.Trim().Length == 0) {
                continue;
            }

            try {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                rows.Add(new LostRow(Text(root, "Id"),
                    DateTime.TryParse(Text(root, "Time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : DateTime.MinValue,
                    Text(root, "CharId"), Text(root, "Name"), Text(root, "Kind"), Text(root, "Reason"),
                    root.TryGetProperty("Copy", out var copy) && copy.ValueKind == JsonValueKind.True, Text(root, "Of")));
            }
            catch (JsonException) {
                // A line cut short while the server wrote it.
            }
        }

        return rows;

        static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    // wizard-list.json, which the server writes: wizard id to name.
    private static Dictionary<string, string> ReadWizardNames(string file) {
        var names = new Dictionary<string, string>();
        try {
            if (!File.Exists(file)) {
                return names;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var account in document.RootElement.GetProperty("Accounts").EnumerateArray()) {
                foreach (var wizard in account.GetProperty("Wizards").EnumerateArray()) {
                    var id = wizard.GetProperty("Id").GetString() ?? "";
                    var name = wizard.GetProperty("Name").GetString() ?? "";
                    names[id] = name.Length > 0 && name != "?" ? name : $"Wizard {id}";
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException) {
        }

        return names;
    }

    private void ShowLostItems(List<LostRow> rows, Dictionary<string, string> names) {
        _lostRows.Children.Clear();
        var given = rows.Where(row => row.Kind == "Restored" && row.Of.Length > 0).Select(row => row.Of).ToHashSet();
        var waiting = rows.Where(row => row.Kind == "Waiting" && row.Of.Length > 0).Select(row => row.Of).ToHashSet();
        var records = rows.Where(row => row.Kind is "Lost" or "Missing" or "Removed" or "Moved").ToList();
        var lost = records.Where(row => row.Kind is "Lost" or "Missing" && !given.Contains(row.Id)).ToList();
        var shown = (_lostShowAll.IsChecked == true ? records : records.Where(row => row.Kind is "Lost" or "Missing").ToList())
            .OrderByDescending(row => row.Time).Take(LOST_ROWS_SHOWN).ToList();

        _lostLine.Foreground = lost.Count > 0 ? WARN : MUTED;
        _lostLine.Text = records.Count == 0
            ? "● No item has left a wizard since this server copy started keeping the item history."
            : lost.Count == 0
                ? "● No item is lost. Tick the box to see items that were sold, thrown away, used up or moved."
                : lost.Count == 1 ? "● 1 item was lost and is not given back yet." : $"● {lost.Count} items were lost and are not given back yet.";
        foreach (var row in shown) {
            _lostRows.Children.Add(LostItemRow(row, names, given.Contains(row.Id), waiting.Contains(row.Id) || _lostAsked.Contains(row.Id)));
        }
    }

    private Control LostItemRow(LostRow row, Dictionary<string, string> names, bool given, bool waiting) {
        var wizard = names.TryGetValue(row.CharId, out var name) ? name : $"Wizard {row.CharId}";
        var kind = row.Kind switch {
            "Lost" => "Lost: no room",
            "Missing" => "Missing",
            "Moved" => "Moved",
            _ => "Removed",
        };
        var title = new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var who = new TextBlock {
            Text = $"{wizard} · {(row.Time == DateTime.MinValue ? "" : row.Time.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture))} · {kind}",
            FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextWrapping = TextWrapping.Wrap,
        };
        var why = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT) };

        Control action;
        if (given) {
            action = new TextBlock { Text = "Given back", Foreground = GOOD, VerticalAlignment = VerticalAlignment.Center };
        }
        else if (waiting) {
            action = new TextBlock {
                Text = $"Comes back when {wizard} logs in or changes area", Foreground = WARN, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (row.Kind == "Moved") {
            action = new TextBlock { Text = "Not lost", Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center };
        }
        else {
            var button = Plain("Give back");
            ToolTip.SetTip(button, row.Copy
                ? $"Puts this exact {row.Name} back in {wizard}'s backpack (or bank) the next time they log in or change area."
                : $"The server only knows which item it was, so {wizard} gets a new {row.Name} the next time they log in or change area.");
            action = Action(button, () => {
                File.AppendAllText(Path.Combine(_manager.ServerFolder, ITEM_RESTORE_FILE), row.Id + Environment.NewLine);
                _lostAsked.Add(row.Id);
                _lostShown = false;
                RefreshLostItems();
                Say(InstalledCommands.IsServerRunning(_manager.InstallFolder)
                    ? $"{row.Name} goes back to {wizard} the next time they log in or change area."
                    : $"{row.Name} goes back to {wizard} after you start the server and they log in.");

                return Task.CompletedTask;
            });
        }

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(12, 10),
            Child = new DockPanel {
                Children = {
                    WithDock(WithMargin(action, new Thickness(12, 0, 0, 0)), Dock.Right),
                    new StackPanel { Spacing = 2, Children = { title, who, why } },
                },
            },
        };
    }

}
