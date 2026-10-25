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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's teleporter hubs (Niduus, 2026-10-25: "a TP hud of 3 teleporters and to make those sub teleporters
/// link to the hub"): a ring of three teleporter stones where your wizard stands, like the game's own hub on Colossus
/// Boulevard, and a street teleporter for each stone out on a street that goes back to the hub. Saved in
/// spiraldb-extra/Customs/Teleporters.json; the server puts them in the game at once.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _hubRows = new() { Spacing = 6 };
    private readonly TextBlock _hubCount = Sub("");

    private Control CustomTeleportersCard() => Fold("sigil", "Customs", "Teleporter hubs",
        "A hub of three teleporter stones, each linked to a street teleporter that brings players back. Makes questing faster.",
        WithHint(Sub("Stand where the hub should be and press Make a hub here. Then go out on a street, stand where a teleporter " +
                "should be and press Put street teleporter here on one of the hub's stones."),
            "Make a hub here puts three teleporter stones in a ring round where you stand (the game's Colossus Boulevard " +
            "stones). Walk to a street, stand where its teleporter should go, facing the way players should face when they " +
            "arrive, walk a step, and press Put street teleporter here on stone 1, 2 or 3. Players press X at a hub stone " +
            "to go to its street, and X at the street teleporter to come back to the hub. A stone with no street yet has " +
            "nothing to press. Give the stones the names of their streets, change their X, Y, Z, turn (in degrees) or look in the boxes, and press Save changes. The arrows change the stones' order. Everything here counts at " +
            "once; the server needs no restart."),
        Row(Action(Primary("Make a hub here"), MakeHubAsync)),
        _hubCount,
        _hubRows);

    private List<TeleporterHub> SafeHubs() {
        try {
            return TeleporterHubs.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    private void RenderHubs() {
        _hubRows.Children.Clear();
        var hubs = SafeHubs();
        _hubCount.Text = hubs.Count == 0 ? "No hubs yet." : hubs.Count == 1 ? "1 hub." : $"{hubs.Count} hubs.";
        foreach (var hub in hubs) {
            _hubRows.Children.Add(HubRow(hub));
        }
    }

    // The stone looks to pick from: the game's Colossus Boulevard teleporters (0: by the stone's place in the hub).
    private static readonly (ulong Id, string Label)[] HUB_LOOKS = [
        (0, "Automatic"), (1488125, "Baron Tower stone"), (1488127, "Briskbreeze Tower stone"), (1488130, "Hildreth Perch stone"),
    ];

    private static readonly (ulong Id, string Label)[] STREET_LOOKS = [
        (0, "Automatic"), (1488126, "To Teleporter Hub (Baron)"), (1488129, "To Teleporter Hub (Briskbreeze)"),
        (1488131, "To Teleporter Hub (Hildreth)"),
    ];

    private Control HubRow(TeleporterHub saved) {
        var hub = saved.Copy();
        var name = new TextBox { Width = 300, Text = hub.Name, Watermark = "The hub's name, such as Colossus Hub", HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => hub.Name = name.Text ?? "";

        var stones = new StackPanel { Spacing = 8, Margin = new Thickness(44, 6, 0, 0) };
        for (var i = 0; i < hub.Pads.Count; i++) {
            var index = i;
            var pad = hub.Pads[i];
            var padName = new TextBox { Width = 220, Text = pad.Name, Watermark = "The street it goes to" };
            padName.TextChanged += (_, _) => pad.Name = padName.Text ?? "";
            var where = pad.Street is { } street ? $"goes to {ZoneDisplay(street.Zone)}" : "no street teleporter yet";
            var buttons = new List<Control> {
                WithCenter(new TextBlock { Text = $"Stone {index + 1}", FontWeight = FontWeight.SemiBold, Width = 60 }),
                Action(Plain("▲"), () => MoveStoneInOrderAsync(saved, index, -1)),
                Action(Plain("▼"), () => MoveStoneInOrderAsync(saved, index, +1)),
                padName,
                WithCenter(new TextBlock { Text = where, Foreground = MUTED, Width = 200, TextWrapping = TextWrapping.Wrap }),
                Action(Primary(pad.Street is null ? "Put street teleporter here" : "Move street teleporter here"), () => SetStreetAsync(saved, index)),
                Action(Plain("Move stone here"), () => MoveStoneAsync(saved, index)),
            };
            if (pad.Street is not null) {
                buttons.Add(Danger(Action(Plain("Remove street"), () => RemoveStreetAsync(saved, index))));
            }

            var stone = new StackPanel { Spacing = 4, Children = { WrapRow([.. buttons]) } };
            stone.Children.Add(SpotEditor("Stone", pad.X, pad.Y, pad.Z, pad.Yaw, pad.Look, HUB_LOOKS,
                (x, y, z, yaw) => (pad.X, pad.Y, pad.Z, pad.Yaw) = (x, y, z, yaw), look => pad.Look = look));
            if (pad.Street is { } linked) {
                stone.Children.Add(SpotEditor("Street", linked.X, linked.Y, linked.Z, linked.Yaw, linked.Look, STREET_LOOKS,
                    (x, y, z, yaw) => (linked.X, linked.Y, linked.Z, linked.Yaw) = (x, y, z, yaw), look => linked.Look = look));
            }

            stones.Children.Add(stone);
        }

        return ManagerTheme.Item(highlighted: false, content: new StackPanel {
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(Row(
                            Action(Primary("Save changes"), () => SaveHubAsync(hub)),
                            Action(Plain("Move hub here"), () => MoveHubAsync(saved)),
                            Danger(Action(Plain("Delete"), () => DeleteHubAsync(saved)))), Dock.Right),
                        WithDock(WithMargin(WithCenter(ManagerTheme.Tile("sigil", ManagerTheme.Accent("Customs"), 32)),
                            new Thickness(0, 0, 12, 0)), Dock.Left),
                        new StackPanel {
                            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                            Children = {
                                Row(WithCenter(new TextBlock { Text = $"{hub.Id}.", FontWeight = FontWeight.SemiBold, FontSize = 15 }), name),
                                new TextBlock {
                                    Text = $"{ZoneDisplay(hub.Zone)}  ·  {hub.Pads.Count(p => p.Street is not null)} of {hub.Pads.Count} stones linked",
                                    Foreground = MUTED, TextWrapping = TextWrapping.Wrap,
                                },
                            },
                        },
                    },
                },
                stones,
            },
        });
    }

    // A stone's place, turn and look, edited in boxes (Niduus, 2026-10-25: "add settings in manager to change their
    // position, rotations"); Save changes writes them. The turn is shown in degrees and saved as the game's radians.
    private static Control SpotEditor(string label, double x, double y, double z, double yaw, ulong look, (ulong Id, string Label)[] looks,
                                      Action<double, double, double, double> setSpot, Action<ulong> setLook) {
        NumericUpDown Box(double value, double step) => new() {
            Minimum = -1_000_000, Maximum = 1_000_000, Increment = (decimal) step, FormatString = "0.##", Width = 120, Value = (decimal) Math.Round(value, 2),
        };

        var bx = Box(x, 10);
        var by = Box(y, 10);
        var bz = Box(z, 5);
        var degrees = Math.Round(yaw * 180 / Math.PI, 1);
        var turn = new NumericUpDown { Minimum = -360, Maximum = 360, Increment = 15, FormatString = "0.#", Width = 110, Value = (decimal) degrees };
        void Changed(object? sender, NumericUpDownValueChangedEventArgs e)
            => setSpot((double) (bx.Value ?? 0), (double) (by.Value ?? 0), (double) (bz.Value ?? 0), (double) (turn.Value ?? 0) * Math.PI / 180);
        foreach (var box in new[] { bx, by, bz, turn }) {
            box.ValueChanged += Changed;
        }

        var lookBox = new ComboBox { Width = 230, ItemsSource = looks.Select(l => l.Label).ToList() };
        var at = Array.FindIndex(looks, l => l.Id == look);
        lookBox.SelectedIndex = at >= 0 ? at : 0;
        lookBox.SelectionChanged += (_, _) => setLook(looks[Math.Max(0, lookBox.SelectedIndex)].Id);

        TextBlock Label(string text) => (TextBlock) WithCenter(new TextBlock { Text = text, Foreground = MUTED });

        return WrapRow(WithCenter(new TextBlock { Text = label, Width = 60 }), Label("X"), bx, Label("Y"), by, Label("Z"), bz,
            Label("Turn°"), turn, Label("Look"), lookBox);
    }

    private Task MakeHubAsync() {
        var (who, zone, x, y, z, yaw) = MySpot();
        var hubs = SafeHubs();
        var hub = new TeleporterHub { Id = TeleporterHubs.NextId(hubs) };
        hub.Name = $"Teleporter Hub {hub.Id}";
        hub.PlaceAt(zone, x, y, z, yaw);
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, hub);
        RenderHubs();
        Say($"Made {hub.Shown} with {TeleporterHub.PAD_COUNT} stones round where {who} stands in {ZoneDisplay(zone)}. It is in the game now. " +
            "Go to a street and press Put street teleporter here on a stone to link it.");

        return Task.CompletedTask;
    }

    private Task MoveHubAsync(TeleporterHub hub) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var moved = hub.Copy();
        moved.PlaceAt(zone, x, y, z, yaw);
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, moved);
        RenderHubs();
        Say($"{hub.Shown} and its stones now stand round where {who} stands in {ZoneDisplay(zone)}. Its street teleporters stay where they are.");

        return Task.CompletedTask;
    }

    private Task MoveStoneAsync(TeleporterHub hub, int index) {
        var (who, zone, x, y, z, yaw) = MySpot();
        if (!string.Equals(zone, hub.Zone, StringComparison.OrdinalIgnoreCase)) {
            throw new PanelException($"A stone stays in its hub's area ({ZoneDisplay(hub.Zone)}), and {who} is in {ZoneDisplay(zone)}. " +
                "Use Move hub here to move the whole hub.");
        }

        var moved = hub.Copy();
        (moved.Pads[index].X, moved.Pads[index].Y, moved.Pads[index].Z, moved.Pads[index].Yaw) = (x, y, z, yaw);
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, moved);
        RenderHubs();
        Say($"Stone {index + 1} of {hub.Shown} now stands where {who} stands.");

        return Task.CompletedTask;
    }

    private Task SetStreetAsync(TeleporterHub hub, int index) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var linked = hub.Copy();
        var look = linked.Pads[index].Street?.Look ?? 0;
        linked.Pads[index].Street = new TeleporterStreet { Zone = zone, X = x, Y = y, Z = z, Yaw = yaw, Look = look };
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, linked);
        RenderHubs();
        Say($"Stone {index + 1} of {hub.Shown} now goes to a street teleporter where {who} stands in {ZoneDisplay(zone)}, " +
            "and that one goes back to the hub. Both work in the game now.");

        return Task.CompletedTask;
    }

    private async Task RemoveStreetAsync(TeleporterHub hub, int index) {
        if (!await ConfirmAsync($"Remove the street teleporter of stone {index + 1} of {hub.Shown}? It leaves the game at once.")) {
            return;
        }

        var unlinked = hub.Copy();
        unlinked.Pads[index].Street = null;
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, unlinked);
        RenderHubs();
        Say($"Stone {index + 1} of {hub.Shown} has no street teleporter now.");
    }

    // A row that wraps onto the next line when the window is narrow.
    private static WrapPanel WrapRow(params Control[] controls) {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls) {
            control.Margin = new Thickness(0, 2, 10, 2);
            row.Children.Add(control);
        }

        return row;
    }

    private Task SaveHubAsync(TeleporterHub edited) {
        // The names, places, turns and looks in the boxes; the hub's area and street links are kept as the file has them.
        var hub = SafeHubs().FirstOrDefault(h => h.Id == edited.Id) ?? edited;
        hub.Name = edited.Name.Trim();
        for (var i = 0; i < hub.Pads.Count && i < edited.Pads.Count; i++) {
            var (pad, box) = (hub.Pads[i], edited.Pads[i]);
            (pad.Name, pad.X, pad.Y, pad.Z, pad.Yaw, pad.Look) = (box.Name.Trim(), box.X, box.Y, box.Z, box.Yaw, box.Look);
            if (pad.Street is { } street && box.Street is { } streetBox) {
                (street.X, street.Y, street.Z, street.Yaw, street.Look) = (streetBox.X, streetBox.Y, streetBox.Z, streetBox.Yaw, streetBox.Look);
            }
        }

        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, hub);
        RenderHubs();
        Say($"Saved {hub.Shown}. The stones show the changes in the game now.");

        return Task.CompletedTask;
    }

    // Niduus (2026-10-25): "the sort": a stone's street moves up or down the hub's list.
    private Task MoveStoneInOrderAsync(TeleporterHub hub, int index, int by) {
        var to = index + by;
        if (to < 0 || to >= hub.Pads.Count) {
            return Task.CompletedTask;
        }

        // The stones stay where they stand (and keep their looks); their names and streets change places, so the stone
        // that went to a street now stands one place up or down the ring.
        var sorted = hub.Copy();
        var (a, b) = (sorted.Pads[index], sorted.Pads[to]);
        (a.Name, b.Name) = (b.Name, a.Name);
        (a.Street, b.Street) = (b.Street, a.Street);
        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, sorted);
        RenderHubs();
        Say($"{sorted.Pads[to].Name} is now stone {to + 1} of {hub.Shown}.");

        return Task.CompletedTask;
    }

    private async Task DeleteHubAsync(TeleporterHub hub) {
        if (!await ConfirmAsync($"Delete {hub.Shown} and its street teleporters? They leave the game at once.")) {
            return;
        }

        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, null);
        RenderHubs();
        Say($"{hub.Shown} is deleted.");
    }

}
