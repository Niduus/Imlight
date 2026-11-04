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
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's portals (Niduus, 2026-11-04: "allow for interaction prompts to be made and customized with the
/// custom feature ... a prompt that changes the name layer to the direction and able to interact to teleport when in
/// it. Include a setting in this to allow for interaction or walking. I would also like a 'Teleport here' feature"):
/// one teleporter (the Shadow Portal by default) with its own prompt, used by pressing X in it or by walking in, that
/// sends players where Teleport here was pressed. Saved in spiraldb-extra/Customs/Portals.json; the server puts them in
/// the game at once.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _portalRows = new() { Spacing = 6 };
    private readonly TextBlock _portalCount = Sub("");

    private Control CustomPortalsCard() => Fold("sigil", "Customs", "Portals and prompts",
        "Your own teleporters, like a house teleporter: each with its own prompt, used by pressing X in it or by walking in.",
        WithHint(Sub("Stand where a portal should be and press Make a portal here. Then walk to where it should send players and " +
                "press Teleport here on its page. The prompt's name line says where it goes unless you give it words of your own."),
            "Make a portal here puts a Shadow Portal where you stand (pick another look on its page). Walk to the spot players " +
            "should arrive at, in any area, facing the way they should face, walk a step, and press Teleport here: that is " +
            "the portal's destination, the way a house teleporter is set. On its page, Name is the big top line of the prompt " +
            "(empty: \"To\" and the name of the place it goes, or write a direction such as \"North to Ravenwood\"). Prompt " +
            "line is the line under it: the game's own \"Press X to Teleport\", Travel, Enter, Exit or Interact (they show your " +
            "own interact key), or your own words. How it works: Press X in it, or Walk in (no prompt; walking into it sends " +
            "you, and someone who arrives inside one has to step out before it sends them again). Reach is how near a player " +
            "has to be. Make a way back puts a second portal at the destination that sends players back here. In the game, " +
            ".portal new and .portal here do the same as the buttons. Everything here counts at once; the server needs no restart."),
        Row(Action(Primary("Make a portal here"), MakePortalAsync)),
        _portalCount,
        _portalRows);

    private List<CustomPortal> SafePortals() {
        try {
            return CustomPortals.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    private void RenderPortals() {
        _portalRows.Children.Clear();
        var portals = SafePortals();
        _portalCount.Text = portals.Count == 0 ? "No portals yet." : portals.Count == 1 ? "1 portal." : $"{portals.Count} portals.";
        foreach (var portal in portals) {
            _portalRows.Children.Add(PortalListRow(portal));
        }

        // Its open page shows what the file has now; a deleted portal's page closes.
        if (IsCustomPage("portal")) {
            if (portals.FirstOrDefault(p => p.Id.ToString(CultureInfo.InvariantCulture) == _customsPageKey) is { } open) {
                OpenPortal(open);
            }
            else {
                BackToCustoms();
            }
        }
    }

    // What the prompt's name line shows.
    private string PortalShown(CustomPortal portal)
        => portal.Name.Trim().Length > 0 ? portal.Name.Trim() : portal.HasDestination ? $"To {ZoneDisplay(portal.ToZone)}" : $"Portal {portal.Id}";

    private string PortalLine(CustomPortal portal)
        => $"In {ZoneDisplay(portal.Zone)}  ·  {(portal.HasDestination ? "goes to " + ZoneDisplay(portal.ToZone) : "no destination yet")}  ·  " +
           (portal.Walk ? "walk in" : "press X");

    private Control PortalListRow(CustomPortal portal) => ManagerTheme.Item(highlighted: false, content: new DockPanel {
        Children = {
            WithDock(Row(
                Action(Primary("Edit"), () => {
                    OpenPortal(portal);

                    return Task.CompletedTask;
                }),
                Action(Plain("Teleport here"), () => PortalHereAsync(portal)),
                Danger(Action(Plain("Delete"), () => DeletePortalAsync(portal)))), Dock.Right),
            WithDock(WithMargin(WithCenter(ManagerTheme.Tile("sigil", ManagerTheme.Accent("Customs"), 32)), new Thickness(0, 0, 12, 0)), Dock.Left),
            new StackPanel {
                Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                Children = {
                    new TextBlock { Text = $"{portal.Id}.  {PortalShown(portal)}", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock { Text = PortalLine(portal), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                },
            },
        },
    });

    private void OpenPortal(CustomPortal portal) => ShowCustomPage("portal", portal.Id.ToString(CultureInfo.InvariantCulture), "sigil",
        $"Portal {portal.Id}: {PortalShown(portal)}", PortalLine(portal), null, PortalEditor(portal));

    private Control PortalEditor(CustomPortal saved) {
        var portal = saved.Copy();
        TextBlock Label(string text, double width = 110) => (TextBlock) WithCenter(new TextBlock { Text = text, Width = width });

        var name = new TextBox {
            Width = 360, Text = portal.Name, HorizontalAlignment = HorizontalAlignment.Left,
            Watermark = portal.HasDestination ? $"Empty: To {ZoneDisplay(portal.ToZone)}" : "Empty: To (the place it goes)",
        };
        name.TextChanged += (_, _) => portal.Name = name.Text ?? "";

        var text = new TextBox { Width = 360, Text = portal.Text, Watermark = "Your own words, such as Step in to go north", HorizontalAlignment = HorizontalAlignment.Left };
        text.TextChanged += (_, _) => portal.Text = text.Text ?? "";
        var prompt = new ComboBox { Width = 260, ItemsSource = CustomPortal.PROMPTS.Select(p => p.Label).ToList() };
        prompt.SelectedIndex = Math.Max(0, Array.FindIndex(CustomPortal.PROMPTS, p => string.Equals(p.Value, portal.Prompt, StringComparison.OrdinalIgnoreCase)));
        text.IsEnabled = CustomPortal.PROMPTS[prompt.SelectedIndex].Value == "Custom";
        prompt.SelectionChanged += (_, _) => {
            portal.Prompt = CustomPortal.PROMPTS[Math.Max(0, prompt.SelectedIndex)].Value;
            text.IsEnabled = portal.Prompt == "Custom";
        };

        var how = new ComboBox { Width = 260, ItemsSource = new List<string> { "Press X in it to teleport", "Walk in to teleport" } };
        how.SelectedIndex = portal.Walk ? 1 : 0;
        how.SelectionChanged += (_, _) => portal.Walk = how.SelectedIndex == 1;

        var reach = new NumericUpDown {
            Minimum = 20, Maximum = 1000, Increment = 10, FormatString = "0", Width = 120,
            Value = (decimal) (portal.Reach > 0 ? portal.Reach : CustomPortal.DEFAULT_REACH),
        };
        reach.ValueChanged += (_, _) => portal.Reach = (double) (reach.Value ?? (decimal) CustomPortal.DEFAULT_REACH);

        // The look: a house teleporter, or any template by its number.
        var looks = CustomPortal.LOOKS.Select(l => l.Label).Append("Another object (its number)").ToList();
        var look = new ComboBox { Width = 260, ItemsSource = looks };
        var lookNumber = new TextBox { Width = 120, Watermark = "Template number" };
        var current = portal.Look == 0 ? CustomPortal.SHADOW_PORTAL : portal.Look;
        var at = Array.FindIndex(CustomPortal.LOOKS, l => l.Id == current);
        look.SelectedIndex = at >= 0 ? at : looks.Count - 1;
        lookNumber.Text = at >= 0 ? "" : current.ToString(CultureInfo.InvariantCulture);
        lookNumber.IsVisible = at < 0;
        look.SelectionChanged += (_, _) => {
            var index = Math.Max(0, look.SelectedIndex);
            lookNumber.IsVisible = index >= CustomPortal.LOOKS.Length;
            if (index < CustomPortal.LOOKS.Length) {
                portal.Look = CustomPortal.LOOKS[index].Id;
            }
        };
        lookNumber.TextChanged += (_, _) => {
            if (ulong.TryParse((lookNumber.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0) {
                portal.Look = id;
            }
        };

        var settings = new StackPanel {
            Spacing = 6,
            Children = {
                WrapRow(Label("Name"), name),
                WithMargin(Sub("The prompt's top line. Write the direction or place, such as \"North: Ravenwood\". Empty says where it goes."), new Thickness(120, 0, 0, 0)),
                WrapRow(Label("Prompt line"), prompt),
                WrapRow(Label("Own words"), text),
                WrapRow(Label("How it works"), how, Label("Reach", 50), reach),
                WithMargin(Sub("Walk in: no prompt, walking into it sends players. Reach: how near they have to be (120 is about standing in it)."), new Thickness(120, 0, 0, 0)),
                WrapRow(Label("Look"), look, lookNumber),
                SpotEditor("Stands at", portal.X, portal.Y, portal.Z, portal.Yaw, 0, [(0, "(the look above)")],
                    (x, y, z, yaw) => (portal.X, portal.Y, portal.Z, portal.Yaw) = (x, y, z, yaw), _ => { }),
            },
        };

        var destination = new StackPanel {
            Spacing = 4, Margin = new Thickness(0, 10, 0, 0),
            Children = {
                new TextBlock {
                    Text = portal.HasDestination ? $"Sends players to {ZoneDisplay(portal.ToZone)}" : "No destination yet: walk to it and press Teleport here.",
                    FontWeight = FontWeight.SemiBold,
                },
                WrapRow(Action(Primary("Teleport here"), () => PortalHereAsync(saved)),
                    Action(Plain("Make a way back"), () => PortalWayBackAsync(saved))),
            },
        };
        if (portal.HasDestination) {
            destination.Children.Add(SpotEditor("Arrive at", portal.ToX, portal.ToY, portal.ToZ, portal.ToYaw, 0, [(0, "(where it sends)")],
                (x, y, z, yaw) => (portal.ToX, portal.ToY, portal.ToZ, portal.ToYaw) = (x, y, z, yaw), _ => { }));
        }

        return ManagerTheme.Item(highlighted: false, content: new StackPanel {
            Spacing = 8,
            Children = {
                Row(Action(Primary("Save changes"), () => SavePortalAsync(portal)),
                    Action(Plain("Move portal here"), () => MovePortalAsync(saved)),
                    Danger(Action(Plain("Delete"), () => DeletePortalAsync(saved)))),
                settings,
                destination,
            },
        });
    }

    private Task MakePortalAsync() {
        var (who, zone, x, y, z, yaw) = MySpot();
        var portals = SafePortals();
        var portal = new CustomPortal {
            Id = CustomPortals.NextId(portals), Zone = zone, X = x, Y = y, Z = z, Yaw = yaw, Look = CustomPortal.SHADOW_PORTAL,
        };
        CustomPortals.Set(_manager.CustomsFolder, portal.Id, portal);
        RenderPortals();
        OpenPortal(portal);
        Say($"Made portal {portal.Id} where {who} stands in {ZoneDisplay(zone)}. It is in the game now. " +
            "Walk to where it should send players and press Teleport here.");

        return Task.CompletedTask;
    }

    private Task PortalHereAsync(CustomPortal portal) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var linked = SafePortals().FirstOrDefault(p => p.Id == portal.Id) ?? portal.Copy();
        (linked.ToZone, linked.ToX, linked.ToY, linked.ToZ, linked.ToYaw) = (zone, x, y, z, yaw);
        CustomPortals.Set(_manager.CustomsFolder, linked.Id, linked);
        RenderPortals();
        Say($"Portal {linked.Id} now sends players to where {who} stands in {ZoneDisplay(zone)}, facing the same way.");

        return Task.CompletedTask;
    }

    private Task MovePortalAsync(CustomPortal portal) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var moved = SafePortals().FirstOrDefault(p => p.Id == portal.Id) ?? portal.Copy();
        (moved.Zone, moved.X, moved.Y, moved.Z, moved.Yaw) = (zone, x, y, z, yaw);
        CustomPortals.Set(_manager.CustomsFolder, moved.Id, moved);
        RenderPortals();
        Say($"Portal {moved.Id} now stands where {who} stands in {ZoneDisplay(zone)}.");

        return Task.CompletedTask;
    }

    // A second portal at this one's destination that sends players back, landing a step in front of this one.
    private Task PortalWayBackAsync(CustomPortal portal) {
        var from = SafePortals().FirstOrDefault(p => p.Id == portal.Id) ?? portal;
        if (!from.HasDestination) {
            throw new PanelException("Press Teleport here first: the way back stands where this portal sends players.");
        }

        var portals = SafePortals();
        var (backX, backY) = CustomPortals.InFront(from.X, from.Y, from.Yaw, (from.Reach > 0 ? from.Reach : CustomPortal.DEFAULT_REACH) + 60);
        // Players arriving inside it step out before it sends them back, so it can stand right where they land.
        var back = new CustomPortal {
            Id = CustomPortals.NextId(portals), Zone = from.ToZone, X = from.ToX, Y = from.ToY, Z = from.ToZ, Yaw = from.ToYaw,
            Look = from.Look, Walk = from.Walk, Reach = from.Reach, Prompt = from.Prompt,
            ToZone = from.Zone, ToX = backX, ToY = backY, ToZ = from.Z, ToYaw = from.Yaw,
        };
        CustomPortals.Set(_manager.CustomsFolder, back.Id, back);
        RenderPortals();
        Say($"Made portal {back.Id} where portal {from.Id} sends players; it sends them back to {ZoneDisplay(from.Zone)}, a few steps in " +
            $"front of portal {from.Id}. Move it or its landing on its page if it stands in the way.");

        return Task.CompletedTask;
    }

    private Task SavePortalAsync(CustomPortal edited) {
        // The boxes; the areas it stands in and goes to are kept as the file has them.
        var portal = SafePortals().FirstOrDefault(p => p.Id == edited.Id) ?? edited;
        (portal.Name, portal.Prompt, portal.Text, portal.Walk, portal.Reach, portal.Look) =
            (edited.Name.Trim(), edited.Prompt, edited.Text.Trim(), edited.Walk, edited.Reach, edited.Look);
        (portal.X, portal.Y, portal.Z, portal.Yaw) = (edited.X, edited.Y, edited.Z, edited.Yaw);
        if (portal.HasDestination) {
            (portal.ToX, portal.ToY, portal.ToZ, portal.ToYaw) = (edited.ToX, edited.ToY, edited.ToZ, edited.ToYaw);
        }

        if (portal.Prompt == "Custom" && portal.Text.Length == 0) {
            Say("Prompt line is set to your own words, but there are none, so it says Press X to Teleport.");
        }

        CustomPortals.Set(_manager.CustomsFolder, portal.Id, portal);
        RenderPortals();
        Say($"Saved portal {portal.Id}. It shows the changes in the game now (walk out of its reach and back in to see a new prompt).");

        return Task.CompletedTask;
    }

    private async Task DeletePortalAsync(CustomPortal portal) {
        if (!await ConfirmAsync($"Delete portal {portal.Id} ({PortalShown(portal)})? It leaves the game at once.")) {
            return;
        }

        CustomPortals.Set(_manager.CustomsFolder, portal.Id, null);
        RenderPortals();
        Say($"Portal {portal.Id} is deleted.");
    }

}
