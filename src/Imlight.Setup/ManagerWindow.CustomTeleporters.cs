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
            "nothing to press. Give the stones the names of their streets and press Save names. Everything here counts at " +
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

    private Control HubRow(TeleporterHub saved) {
        var hub = saved.Copy();
        var name = new TextBox { Width = 300, Text = hub.Name, Watermark = "The hub's name, such as Colossus Hub", HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => hub.Name = name.Text ?? "";

        var stones = new StackPanel { Spacing = 4, Margin = new Thickness(44, 6, 0, 0) };
        for (var i = 0; i < hub.Pads.Count; i++) {
            var index = i;
            var pad = hub.Pads[i];
            var padName = new TextBox { Width = 220, Text = pad.Name, Watermark = "The street it goes to" };
            padName.TextChanged += (_, _) => pad.Name = padName.Text ?? "";
            var where = pad.Street is { } street ? $"goes to {ZoneDisplay(street.Zone)}" : "no street teleporter yet";
            var buttons = new List<Control> {
                WithCenter(new TextBlock { Text = $"Stone {index + 1}", FontWeight = FontWeight.SemiBold, Width = 60 }),
                padName,
                WithCenter(new TextBlock { Text = where, Foreground = MUTED, Width = 220, TextWrapping = TextWrapping.Wrap }),
                Action(Primary(pad.Street is null ? "Put street teleporter here" : "Move street teleporter here"), () => SetStreetAsync(saved, index)),
                Action(Plain("Move stone here"), () => MoveStoneAsync(saved, index)),
            };
            if (pad.Street is not null) {
                buttons.Add(Danger(Action(Plain("Remove street"), () => RemoveStreetAsync(saved, index))));
            }

            stones.Children.Add(Row([.. buttons]));
        }

        return ManagerTheme.Item(highlighted: false, content: new StackPanel {
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(Row(
                            Action(Primary("Save names"), () => SaveHubNamesAsync(hub)),
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

    private Task SaveHubNamesAsync(TeleporterHub edited) {
        // Only the names: where the stones stand is kept as the file has it now.
        var hub = SafeHubs().FirstOrDefault(h => h.Id == edited.Id) ?? edited;
        hub.Name = edited.Name.Trim();
        for (var i = 0; i < hub.Pads.Count && i < edited.Pads.Count; i++) {
            hub.Pads[i].Name = edited.Pads[i].Name.Trim();
        }

        TeleporterHubs.Set(_manager.CustomsFolder, hub.Id, hub);
        RenderHubs();
        Say($"Saved the names of {hub.Shown}. The stones show them in the game now.");

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
