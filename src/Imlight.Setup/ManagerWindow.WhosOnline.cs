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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Home's "Who's online" card (Niduus, 2026-10-27): each player's account, the wizard they are on and where they are.
/// The server writes online-players.json when the list changes (OnlineList); the card reads it with the 2-second
/// status check, only when the file changed.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ONLINE_FILE = "online-players.json";

    private readonly StackPanel _onlineRows = new() { Spacing = 8 };
    private readonly TextBlock _onlineLine = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private bool? _onlineServerRunning;
    private DateTime _onlineFileTime;
    private bool _onlineReading;
    private bool _onlineShown;
    private Dictionary<string, string[]>? _onlineZoneNames;

    private sealed record OnlineRow(string Account, string Wizard, string Zone);

    private Control HomeWhosOnline() {
        _onlineLine.Text = "Checking the server…";

        return Fold("users", "Accounts", "Who's online",
            "Everyone playing on your server right now: their account, the wizard they are on and where they are. It updates by itself.",
            _onlineLine, _onlineRows);
    }

    /// <summary>With every status check: the server's state, then the list when the server wrote a new one.</summary>
    private async void RefreshWhosOnline(bool running) {
        if (_onlineReading) {
            return;
        }

        if (!running) {
            if (_onlineServerRunning != false) {
                _onlineServerRunning = false;
                _onlineShown = false;
                _onlineRows.Children.Clear();
                _onlineLine.Text = "● The server is off, so nobody is online. Start it on this page and players show here.";
                _onlineLine.Foreground = MUTED;
            }

            return;
        }

        _onlineServerRunning = true;
        _onlineReading = true;
        try {
            var file = Path.Combine(_manager.ServerFolder, ONLINE_FILE);
            var time = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (_onlineShown && time == _onlineFileTime) {
                return;
            }

            var zoneDetails = Path.Combine(_manager.ExtraFolder, "Zones", "zone-details.json");
            var known = _onlineZoneNames;
            var (rows, names, serverHasIt) = await Task.Run(() => (ReadOnline(file), known ?? Lookup.ReadZoneDetails(zoneDetails), _manager.HasWhosOnline));
            _onlineZoneNames = names;
            _onlineFileTime = time;
            _onlineShown = true;
            ShowOnline(rows, serverHasIt);
        }
        catch (Exception) {
            // The server is writing it (or the disk is busy): the next check reads it. Never take the window down.
        }
        finally {
            _onlineReading = false;
        }
    }

    // Null when the server hasn't written a list (an older server, or one still starting).
    private static List<OnlineRow>? ReadOnline(string file) {
        if (!File.Exists(file)) {
            return null;
        }

        try {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return [
                .. document.RootElement.GetProperty("Players").EnumerateArray().Select(p => new OnlineRow(
                    Text(p, "Account"), Text(p, "Wizard"), Text(p, "Zone"))),
            ];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) {
            return null;
        }

        static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private void ShowOnline(List<OnlineRow>? rows, bool serverHasIt) {
        _onlineRows.Children.Clear();
        _onlineLine.Foreground = MUTED;
        if (rows is null) {
            _onlineLine.Text = serverHasIt
                ? "● The server is on. Nobody is online yet."
                : "● The server is on, but this server copy can't tell who is online. Click \"Update Ahziael\" at the top to get it.";

            return;
        }

        if (rows.Count == 0) {
            _onlineLine.Text = "● The server is on. Nobody is online right now.";

            return;
        }

        _onlineLine.Text = rows.Count == 1 ? "● 1 player is online." : $"● {rows.Count} players are online.";
        _onlineLine.Foreground = GOOD;
        foreach (var row in rows) {
            _onlineRows.Children.Add(OnlinePlayerRow(row));
        }
    }

    private Control OnlinePlayerRow(OnlineRow row) {
        var picking = row.Wizard.Length == 0;
        var name = new TextBlock {
            Text = picking ? "Picking a wizard" : row.Wizard, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        var account = new TextBlock {
            Text = "Account: " + (row.Account.Length > 0 ? row.Account : "(name unknown)"),
            FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextWrapping = TextWrapping.Wrap,
        };
        var place = new TextBlock {
            Text = picking ? "At the wizard select screen" : PlaceInWords(row.Zone),
            TextWrapping = TextWrapping.Wrap, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT),
        };
        if (!picking && row.Zone.Length > 0) {
            ToolTip.SetTip(place, $"Zone {row.Zone}");
        }

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(12, 10),
            Child = new DockPanel {
                Children = {
                    WithDock(new Border {
                        Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                        Child = ManagerTheme.Tile(picking ? "users" : "sparkle", ManagerTheme.Accent("Accounts"), 36),
                    }, Dock.Left),
                    new StackPanel { Spacing = 2, Children = { name, account, place } },
                },
            },
        };
    }

    /// <summary>
    /// A zone path as a place: its name from the zone list and its world (WizardCity/WC_Hub is "The Commons, Wizard
    /// City"); a zone the list lacks gets the words of its path.
    /// </summary>
    private string PlaceInWords(string zone) {
        if (zone.Length == 0) {
            return "Somewhere the server hasn't said";
        }

        var world = Lookup.ZoneWorld(zone);
        if (world == "Houses") {
            return "At a house";
        }

        var area = _onlineZoneNames is not null && _onlineZoneNames.TryGetValue(zone, out var details) && details.Length > 0 && details[0].Length > 0
            ? details[0]
            : ZonePathWords(zone);
        if (world == "Other" || area.Equals(world, StringComparison.OrdinalIgnoreCase)) {
            return area;
        }

        return area.Length > 0 ? $"{area}, {world}" : world;
    }

    // WizardCity/Interiors/WC_Ravenwood_Teleporter: "Ravenwood Teleporter".
    private static string ZonePathWords(string zone) {
        var words = zone.Split('/')[^1].Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !ZonePathCode().IsMatch(part))
            .Select(part => CamelWords().Replace(part, " "));

        return string.Join(" ", words).Trim();
    }

    [GeneratedRegex(@"^([A-Z]{1,3}\d*|Z\d+\w*|I\d+|i\d+|\d+)$")]
    private static partial Regex ZonePathCode();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelWords();

}
