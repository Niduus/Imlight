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
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's battles and duel circles (Niduus, 2026-10-23: "Make creating a Sigil and Duel Circle as easy as
/// Vendor/Trainers"): one press puts a battle circle with a creature on it, or a duel circle, where the wizard stands.
/// Both go into the zone's own custom area (made when it has none), which then opens below to be changed like any
/// other; the server adds them at once.
/// </summary>
internal sealed partial class ManagerWindow {

    // The battle's creature: a game creature (Find) or one of the customs.
    private ulong _battleCreature;
    private readonly TextBlock _battleCreatureName = new() { Text = "None picked yet", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly ComboBox _battleCustom = new() { Width = 320 };
    private List<CustomMob> _battleCustoms = [];

    private static readonly (string Label, string Type)[] s_battleCircles = [
        ("4 places for monsters (the usual)", "CombatSigil_4EnemySlots"),
        ("3 places for monsters", "CombatSigil_3EnemySlots"),
        ("2 places for monsters", "CombatSigil_2EnemySlots"),
        ("1 place, for a boss alone", "CombatSigil_1EnemySlot"),
    ];

    private readonly ComboBox _battleCircle = new() { Width = 320, ItemsSource = s_battleCircles.Select(c => c.Label).ToList(), SelectedIndex = 0 };

    private readonly ComboBox _duelStyle = new() {
        Width = 320, SelectedIndex = 0,
        ItemsSource = new List<string> { "Take turns (the PvP way)", "Both sides at once (like a monster battle)" },
    };

    private Control CustomBattlesCard() => Fold("sword", "Customs", "Battles and duel circles",
        "A battle circle with a creature on it, or a duel circle, where you stand. Pick, then press Add.",
        WithHint(Sub("A battle circle is where players fight the creature that stands on it. On a duel circle, wizards who stand on its two sides duel each other."),
            "Go into the game, stand where it should be and walk a step. For a battle, pick the creature (Find searches every " +
            "creature and boss by name) or one of your Custom mobs, then press Add a battle here: the circle and the creature " +
            "appear at once. For a duel circle, choose how the two sides fight and press Add a duel circle here. Both go into " +
            "this zone's custom area below, which opens so you can move or change them and press Save area. A defeated " +
            "creature comes back like the zone's own, except in a private copy."),
        Row(WithCenter(new TextBlock { Text = "Creature:" }), _battleCreatureName,
            new Button {
                Content = IconLabel("Find", AreaIcon.Search),
                Flyout = PickerFlyout(PickKind.Creature, null, picked => {
                    _battleCreature = picked;
                    _battleCustom.SelectedIndex = 0;
                    _battleCreatureName.Text = TemplateName(picked, PickKind.Creature);
                }),
            }),
        Row(WithCenter(new TextBlock { Text = "Or your custom:" }), _battleCustom),
        Row(WithCenter(new TextBlock { Text = "Circle:" }), _battleCircle,
            Action(Primary("Add a battle here"), AddBattleHereAsync)),
        Row(WithCenter(new TextBlock { Text = "Duel:" }), _duelStyle,
            Action(Plain("Add a duel circle here"), AddDuelHereAsync)));

    private void LoadBattleCustoms() {
        var picked = _battleCustom.SelectedIndex > 0 && _battleCustom.SelectedIndex <= _battleCustoms.Count ? _battleCustoms[_battleCustom.SelectedIndex - 1].Id : 0;
        try {
            _battleCustoms = CustomMob.All(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            _battleCustoms = [];
        }

        _battleCustom.ItemsSource = new[] { "None: the creature picked above" }
            .Concat(_battleCustoms.Select(c => $"Custom {c.Id}: {c.Shown}")).ToList();
        _battleCustom.SelectedIndex = picked > 0 ? _battleCustoms.FindIndex(c => c.Id == picked) + 1 : 0;
    }

    private Task AddBattleHereAsync() {
        var custom = _battleCustom.SelectedIndex > 0 && _battleCustom.SelectedIndex <= _battleCustoms.Count ? _battleCustoms[_battleCustom.SelectedIndex - 1] : null;
        var template = custom is not null && ulong.TryParse(custom.BaseId, NumberStyles.None, CultureInfo.InvariantCulture, out var baseId) ? baseId : _battleCreature;
        if (custom is null && template == 0) {
            throw new PanelException("Pick the creature first: press Find beside Creature, or choose one of your customs.");
        }

        var (circleType, circleLabel) = (s_battleCircles[Math.Max(0, _battleCircle.SelectedIndex)].Type, s_battleCircles[Math.Max(0, _battleCircle.SelectedIndex)].Label);
        var shown = custom?.Shown ?? FirstPart(TemplateName(template, PickKind.Creature));

        return AddToMyZone((json, spot, unique) => {
            var circle = SpotEntry(unique("Battle circle"), spot, AreaCatalog.DUEL_CIRCLE_TEMPLATE);
            circle["Scale"] = 1;
            circle["CombatSigil"] = new JsonObject { ["SigilType"] = circleType, ["Radius"] = 0 };
            Arr(json, "Objects").Add(circle);
            var creature = SpotEntry(unique(shown.Length > 0 ? shown : "Creature"), spot, template);
            if (custom is not null) {
                creature["Custom"] = custom.Id;
            }

            Arr(json, "Creatures").Add(creature);

            return ($"Added a battle circle ({circleLabel.Split(" (")[0]}) with {(custom is null ? shown : $"your custom {custom.Id}, {custom.Shown},")} on it",
                ["/objects", "/creatures"]);
        });
    }

    private Task AddDuelHereAsync() {
        var style = _duelStyle.SelectedIndex == 1 ? "Together" : "Turns";

        return AddToMyZone((json, spot, unique) => {
            var circle = SpotEntry(unique("Duel circle"), spot, AreaCatalog.DUEL_CIRCLE_TEMPLATE);
            circle["Scale"] = 1;
            circle["CombatSigil"] = new JsonObject { ["SigilType"] = AreaCatalog.PVP_SIGIL_TYPE, ["Radius"] = 0, ["Combat"] = style };
            Arr(json, "Objects").Add(circle);

            return ($"Added a duel circle ({(style == "Together" ? "both sides at once" : "taking turns")})", ["/objects"]);
        });
    }

    private static string FirstPart(string name) => name.Split("  ·  ")[0];

    private static JsonObject SpotEntry(string name, (double X, double Y, double Z, double Yaw) spot, ulong template) => new() {
        ["Name"] = name, ["TemplateID"] = template, ["X"] = Math.Round(spot.X, 1), ["Y"] = Math.Round(spot.Y, 1), ["Z"] = Math.Round(spot.Z, 1),
        ["Yaw"] = Math.Round(spot.Yaw, 3),
    };

    // Adds to the custom area of the zone the wizard stands in (made when it has none), saves it and opens it below.
    private Task AddToMyZone(Func<JsonObject, (double X, double Y, double Z, double Yaw), Func<string, string>, (string Said, string[] Open)> add) {
        var (who, zone, x, y, z, yaw) = MySpot();
        var area = CustomArea.All(AreaFolder).FirstOrDefault(a => a.Variant.Length == 0 && a.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase));
        if (area is not null && _areaEdits.TryGetValue(area.File, out var open) && open.Dirty) {
            throw new PanelException($"The area for {ZoneDisplay(zone)} below has changes not saved yet. Press Save area or Undo on it first.");
        }

        var json = area is null ? new JsonObject { ["Zone"] = zone, ["Notes"] = "Made in the Manager's Customs tab." } : (JsonObject) area.Json.DeepClone();
        var taken = Entries(json, "Objects").Concat(Entries(json, "Creatures")).Select(o => CustomArea.Text(o, "Name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Unique(string start) {
            var name = start;
            for (var i = 2; taken.Contains(name); i++) {
                name = $"{start} {i}";
            }

            taken.Add(name);

            return name;
        }

        var (said, sections) = add(json, (x, y, z, yaw), Unique);
        var file = AreaFiles.Save(AreaFolder, _manager.CustomsFolder, area?.File, json);
        if (area is not null) {
            _areaEdits.Remove(area.File);
        }

        _areaOpen[file] = true;
        foreach (var section in sections) {
            _areaOpen[file + section] = true;
        }

        RenderAreas();
        var index = CustomArea.All(AreaFolder).FindIndex(a => string.Equals(a.File, file, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index < _areaRows.Children.Count) {
            var view = _areaRows.Children[index];
            Dispatcher.UIThread.Post(() => view.BringIntoView(), DispatcherPriority.Background);
        }

        Say($"{said} where {who} stands in {ZoneDisplay(zone)}." +
            (InstalledCommands.IsServerRunning(_manager.InstallFolder) ? " It is in the game now." : " The server is off, so it is there when it starts.") +
            $" It is in {(area is null ? "a new" : "this zone's")} custom area below; change it there and press Save area.");

        return Task.CompletedTask;
    }

}
