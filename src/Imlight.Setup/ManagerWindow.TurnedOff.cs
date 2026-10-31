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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Game Rules > On and Off (Niduus, 2026-10-31: "allow free customizations of enabling or disabling NPC/creatures/zones/areas
/// across the board, including normal and custom"): every NPC, creature, zone and world of the game, and every custom
/// creature, vendor, trainer and area of the Customs tab, with a switch. The game's own ones and the customs' creatures and
/// vendors are kept in spiraldb-extra/Customs/TurnedOff.json (the server's TurnedOff.cs watches it, so a switch counts at
/// once); a custom area's switch is its file's own Enabled, as on the Customs tab.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TURNED_OFF_FILE = "TurnedOff.json";
    // The Commons: the server never turns it off, so a player in a turned-off zone always has somewhere to go.
    private const string ALWAYS_ON_ZONE = "WizardCity/WC_Hub";
    private const int TURNED_OFF_MAX_ROWS = 300;

    private const string OO_NPC = "NPC";
    private const string OO_CREATURE = "Creature";
    private const string OO_ZONE = "Zone";
    private const string OO_WORLD = "World";
    private const string OO_CUSTOM = "My custom creature";
    private const string OO_CUSTOM_NPC = "My vendor or trainer";
    private const string OO_AREA = "My custom area";

    private static readonly string[] s_ooKinds = [ANY_FILTER, OO_NPC, OO_CREATURE, OO_ZONE, OO_WORLD, OO_CUSTOM, OO_CUSTOM_NPC, OO_AREA];
    private static readonly string[] s_ooShow = ["Everything", "Only what is off", "Only what is on"];

    private sealed class TurnedOffSwitches {
        public List<ulong> Templates { get; set; } = [];
        public List<int> Customs { get; set; } = [];
        public List<int> CustomNpcs { get; set; } = [];
        public List<string> Zones { get; set; } = [];
        public Dictionary<string, string> Names { get; set; } = [];
    }

    /// <summary>One thing with a switch: Key is the template id, zone, world, custom number or area file.</summary>
    private sealed record OnOffRow(string Kind, string Name, string Detail, string Key, string World, string Haystack);

    private static readonly JsonSerializerOptions s_ooJson = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly TextBox _ooSearch = new() { Watermark = "Search a name, an ID or a zone, such as Malistaire, Ravenwood or 1336035" };
    private readonly ComboBox _ooKind = FilterCombo(s_ooKinds, 190);
    private readonly ComboBox _ooWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _ooShowCombo = FilterCombo(s_ooShow, 170);
    private readonly TextBlock _ooCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _ooSummary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ListBox _ooList = new() { Background = Brushes.Transparent, Classes = { "compact" } };

    private TurnedOffSwitches _ooSwitches = new();
    private List<OnOffRow> _ooRows = [];
    private HashSet<string> _ooAreasOff = new(StringComparer.OrdinalIgnoreCase);

    private string TurnedOffPath => Path.Combine(_manager.CustomsFolder, TURNED_OFF_FILE);

    private Control TurnedOffTab() {
        _ooList.ItemTemplate = new FuncDataTemplate<OnOffRow>((row, _) => row is null ? new Panel() : OnOffRowView(row));
        WhenFiltersChange(RenderTurnedOff, _ooSearch, _ooKind, _ooWorld, _ooShowCombo);

        var allOn = Plain("Turn everything back on");
        ToolTip.SetTip(allOn, "Turns every NPC, creature, zone, world, custom and custom area on this page back on.");

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(Fold("power", "On and Off", "Turn anything on or off",
                    "Find any NPC, creature, zone or world of the game, or any of your own customs, and flip its switch. " +
                    "It counts at once, with no server restart.",
                    WithHint(_ooSearch,
                        "Type a name, an ID or a zone. With nothing typed, everything shows (the first " + TURNED_OFF_MAX_ROWS + "). " +
                        "Off NPCs and creatures are gone from every zone. Off zones and worlds cannot be entered: players who try are told " +
                        "so and stay where they are, and anyone who logs in inside one goes to the Commons. The Commons is always on, so nobody " +
                        "gets stuck. A creature already in a battle finishes it first. The game's files are never changed."),
                    WithCount(SearchFilters(
                        SearchFilter("Kind", _ooKind),
                        SearchFilter("World", _ooWorld),
                        SearchFilter("Show", _ooShowCombo)),
                        _ooCount),
                    new WrapPanel { Children = { Spaced(WithCenter(_ooSummary)), Spaced(Action(allOn, TurnEverythingOnAsync)) } }), Dock.Top),
                WithMargin(_ooList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the switches, the game's lists (the ID lookup) and the customs again. Runs when the tab shows.</summary>
    private async Task LoadTurnedOffAsync() {
        await LoadLookupAsync();
        _ooSwitches = ReadTurnedOff();
        var lookup = _lookup;
        var customsFolder = _manager.CustomsFolder;
        var areaFolder = AreaFolder;
        _ooCount.Text = "Loading…";
        try {
            (_ooRows, _ooAreasOff) = await Task.Run(() => BuildOnOffRows(lookup, customsFolder, areaFolder, _ooSwitches));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _ooCount.Text = $"Could not read the lists: {ex.Message}";

            return;
        }

        FillFilterCombo(_ooWorld, _ooRows.Select(r => r.World).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
        RenderTurnedOff();
    }

    private TurnedOffSwitches ReadTurnedOff() {
        try {
            if (File.Exists(TurnedOffPath)) {
                var read = JsonSerializer.Deserialize<TurnedOffSwitches>(File.ReadAllText(TurnedOffPath), s_ooJson) ?? new TurnedOffSwitches();
                read.Templates ??= [];
                read.Customs ??= [];
                read.CustomNpcs ??= [];
                read.Zones ??= [];
                read.Names ??= [];

                return read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read {TURNED_OFF_FILE}: {ex.Message}", isError: true);
        }

        return new TurnedOffSwitches();
    }

    private static (List<OnOffRow> Rows, HashSet<string> AreasOff) BuildOnOffRows(Lookup? lookup, string customsFolder, string areaFolder,
                                                                                    TurnedOffSwitches switches) {
        var rows = new List<OnOffRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string kind, string name, string detail, string key, string world) {
            if (seen.Add($"{kind}|{key}")) {
                rows.Add(new OnOffRow(kind, name, detail, key, world, SearchText.Normalize($"{kind} {name} {detail} {key} {world}")));
            }
        }

        // The customs first: they are few, and the ones most often switched.
        foreach (var custom in CustomMob.All(customsFolder)) {
            Add(OO_CUSTOM, custom.Shown, $"Custom {custom.Id} · made from creature {custom.BaseId}", custom.Id.ToString(CultureInfo.InvariantCulture), "");
        }

        try {
            foreach (var npc in CustomNpcs.Load(customsFolder)) {
                Add(OO_CUSTOM_NPC, npc.Name.Length > 0 ? npc.Name : $"{npc.Kind} {npc.Id}", $"{npc.Kind} {npc.Id} · {npc.Zone}",
                    npc.Id.ToString(CultureInfo.InvariantCulture), Lookup.ZoneWorld(npc.Zone));
            }
        }
        catch (JsonException) {
            // A file broken by hand: its vendors are left out here.
        }

        var areasOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in CustomArea.All(areaFolder)) {
            if (BossChanges.FindIn(area.Json, "Enabled") is JsonValue on && on.ToJsonString() == "false") {
                areasOff.Add(area.File);
            }

            var notes = area.Notes.Length > 0 ? " · " + area.Notes : "";
            Add(OO_AREA, area.Room, $"{Path.GetFileName(area.File)}{notes}", area.File, Lookup.ZoneWorld(area.Zone));
        }

        // The game's worlds and zones (the Lookup tab's teleport areas).
        var entries = lookup?.Entries ?? [];
        foreach (var zone in entries.Where(e => e.Kind == Lookup.TELEPORT_AREA && !string.IsNullOrEmpty(e.Zone))
                     .GroupBy(e => e.Zone.Split('/')[0], StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) {
            var world = zone.First().World;
            Add(OO_WORLD, world.Length > 0 ? world : zone.Key, $"Every zone in {zone.Key}/ ({zone.Count()} zones)", zone.Key, world);
        }

        foreach (var zone in entries.Where(e => e.Kind == Lookup.TELEPORT_AREA && !string.IsNullOrEmpty(e.Zone))) {
            Add(OO_ZONE, zone.Name, $"{zone.Zone}{(zone.Detail.Length > 0 ? " · " + zone.Detail : "")}", zone.Zone, zone.World);
        }

        foreach (var entry in entries.Where(e => e.Kind is "NPC" or "Creature" && e.Id.Length > 0)) {
            Add(entry.Kind == "NPC" ? OO_NPC : OO_CREATURE, entry.Name, $"{entry.Kind} {entry.Id}{(entry.Detail.Length > 0 ? " · " + entry.Detail : "")}",
                entry.Id, "");
        }

        // What is off but not in the lists (the ID lookup is not made yet, or a custom was deleted) still shows, so it can go back on.
        foreach (var id in switches.Templates) {
            var key = id.ToString(CultureInfo.InvariantCulture);
            if (!seen.Contains($"{OO_NPC}|{key}") && !seen.Contains($"{OO_CREATURE}|{key}")) {
                Add(OO_CREATURE, switches.Names.GetValueOrDefault("T:" + key) ?? $"Template {key}", $"Template {key}", key, "");
            }
        }

        foreach (var zone in switches.Zones) {
            if (!seen.Contains($"{OO_ZONE}|{zone}") && !seen.Contains($"{OO_WORLD}|{zone}")) {
                Add(zone.Contains('/') ? OO_ZONE : OO_WORLD, switches.Names.GetValueOrDefault("Z:" + zone) ?? zone, zone, zone, Lookup.ZoneWorld(zone));
            }
        }

        foreach (var id in switches.Customs) {
            var key = id.ToString(CultureInfo.InvariantCulture);
            Add(OO_CUSTOM, switches.Names.GetValueOrDefault("C:" + key) ?? $"Custom {key}", $"Custom {key} (no longer on the Customs tab)", key, "");
        }

        foreach (var id in switches.CustomNpcs) {
            var key = id.ToString(CultureInfo.InvariantCulture);
            Add(OO_CUSTOM_NPC, switches.Names.GetValueOrDefault("N:" + key) ?? $"Vendor {key}", $"Vendor or trainer {key} (no longer on the Customs tab)", key, "");
        }

        return (rows, areasOff);
    }

    private bool IsRowOff(OnOffRow row) => row.Kind switch {
        OO_NPC or OO_CREATURE => ulong.TryParse(row.Key, out var id) && _ooSwitches.Templates.Contains(id),
        OO_ZONE or OO_WORLD => _ooSwitches.Zones.Contains(row.Key, StringComparer.OrdinalIgnoreCase),
        OO_CUSTOM => int.TryParse(row.Key, out var c) && _ooSwitches.Customs.Contains(c),
        OO_CUSTOM_NPC => int.TryParse(row.Key, out var n) && _ooSwitches.CustomNpcs.Contains(n),
        OO_AREA => _ooAreasOff.Contains(row.Key),
        _ => false,
    };

    private int TurnedOffCount
        => _ooSwitches.Templates.Count + _ooSwitches.Zones.Count + _ooSwitches.Customs.Count + _ooSwitches.CustomNpcs.Count + _ooAreasOff.Count;

    private void RenderTurnedOff() {
        var off = TurnedOffCount;
        _ooSummary.Text = off == 0 ? "Everything is on." : $"{off} {(off == 1 ? "thing is" : "things are")} turned off.";
        _ooSummary.Foreground = off == 0 ? GOOD : WARN;

        if (_ooRows.Count == 0) {
            _ooList.ItemsSource = null;
            _ooCount.Text = "Nothing to list yet. Start the server once and give it a few minutes: it makes the list of NPCs, creatures and zones.";

            return;
        }

        var words = SearchText.Words(_ooSearch.Text);
        var kind = FilterPick(_ooKind);
        var world = FilterPick(_ooWorld);
        var show = _ooShowCombo.SelectedItem as string ?? s_ooShow[0];
        var exact = (_ooSearch.Text ?? "").Trim();
        var matches = _ooRows.Where(r => words.All(w => r.Haystack.Contains(w, StringComparison.Ordinal))
                                         && (kind is null || r.Kind == kind)
                                         && (world is null || string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase))
                                         && (show == s_ooShow[0] || IsRowOff(r) == (show == s_ooShow[1])))
            // What is off first, then an exact name or ID, then the order above (customs, worlds, zones, then the rest).
            .OrderBy(r => !IsRowOff(r))
            .ThenBy(r => !(r.Key.Equals(exact, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _ooList.ItemsSource = matches.Take(TURNED_OFF_MAX_ROWS).ToList();
        _ooCount.Text = matches.Count > TURNED_OFF_MAX_ROWS
            ? $"Showing {TURNED_OFF_MAX_ROWS} of {matches.Count:N0}. Type more of a name to find the one you want."
            : $"{matches.Count:N0} found.";
    }

    private Control OnOffRowView(OnOffRow row) {
        var isCommons = row.Kind == OO_ZONE && string.Equals(row.Key, ALWAYS_ON_ZONE, StringComparison.OrdinalIgnoreCase);
        var toggle = new ToggleSwitch {
            OnContent = "On", OffContent = "Off", IsChecked = !IsRowOff(row), IsEnabled = !isCommons, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, isCommons
            ? "The Commons is always on, so a player in a turned-off zone always has somewhere to go."
            : row.Kind == OO_WORLD ? "Turns every zone of this world on or off at once (the Commons stays on)." : "On: it is in the game. Off: it is gone.");
        toggle.IsCheckedChanged += (_, _) => SetRowOn(row, toggle.IsChecked == true);

        return new DockPanel {
            Margin = new Thickness(4, 3),
            Children = {
                WithDock(toggle, Dock.Right),
                new StackPanel {
                    Spacing = 1,
                    Children = {
                        new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock {
                            Text = $"{row.Kind} · {row.Detail}{(isCommons ? " · always on" : "")}", Foreground = MUTED,
                            FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            },
        };
    }

    private void SetRowOn(OnOffRow row, bool on) {
        try {
            if (row.Kind == OO_AREA) {
                SetAreaOn(row.Key, on);
            }
            else {
                // Read again first, so a change made in the file (or on another PC's Remote) is kept.
                _ooSwitches = ReadTurnedOff();
                var s = _ooSwitches;
                switch (row.Kind) {
                    case OO_NPC or OO_CREATURE when ulong.TryParse(row.Key, out var id):
                        Toggle(s.Templates, id, on);
                        Name(s, "T:" + row.Key, row.Name, on);
                        break;
                    case OO_ZONE or OO_WORLD:
                        s.Zones.RemoveAll(z => string.Equals(z, row.Key, StringComparison.OrdinalIgnoreCase));
                        if (!on) {
                            s.Zones.Add(row.Key);
                        }

                        Name(s, "Z:" + row.Key, row.Name, on);
                        break;
                    case OO_CUSTOM when int.TryParse(row.Key, out var c):
                        Toggle(s.Customs, c, on);
                        Name(s, "C:" + row.Key, row.Name, on);
                        break;
                    case OO_CUSTOM_NPC when int.TryParse(row.Key, out var n):
                        Toggle(s.CustomNpcs, n, on);
                        Name(s, "N:" + row.Key, row.Name, on);
                        break;
                }

                WriteTurnedOff(s);
            }

            Say($"{row.Name} is {(on ? "on" : "off")} now. It counts at once if the server is running.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or PanelException) {
            Say($"Could not change {row.Name}: {ex.Message}", isError: true);
        }

        RenderTurnedOff();

        static void Toggle<T>(List<T> list, T value, bool on) {
            list.Remove(value);
            if (!on) {
                list.Add(value);
            }
        }

        static void Name(TurnedOffSwitches s, string key, string name, bool on) {
            if (on) {
                s.Names.Remove(key);
            }
            else {
                s.Names[key] = name;
            }
        }
    }

    private void WriteTurnedOff(TurnedOffSwitches switches) {
        Directory.CreateDirectory(_manager.CustomsFolder);
        var path = TurnedOffPath;
        File.WriteAllText(path + ".saving", JsonSerializer.Serialize(switches, s_ooJson));
        File.Move(path + ".saving", path, overwrite: true);
    }

    // A custom area's switch is its file's own Enabled, the same one its page on the Customs tab has.
    private void SetAreaOn(string file, bool on) {
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file), documentOptions: AreaFiles.READ) is not JsonObject json) {
            throw new PanelException("That area's file is gone. Open the Customs tab to see your areas.");
        }

        if (on) {
            json.Remove("Enabled");
            _ooAreasOff.Remove(file);
        }
        else {
            json["Enabled"] = false;
            _ooAreasOff.Add(file);
        }

        AreaFiles.Save(AreaFolder, _manager.CustomsFolder, file, json);
    }

    private async Task TurnEverythingOnAsync() {
        if (TurnedOffCount == 0) {
            Say("Everything is on already.");

            return;
        }

        if (!await ConfirmAsync($"Turn all {TurnedOffCount} things that are off back on?")) {
            return;
        }

        WriteTurnedOff(new TurnedOffSwitches());
        _ooSwitches = new TurnedOffSwitches();
        foreach (var file in _ooAreasOff.ToList()) {
            SetAreaOn(file, true);
        }

        RenderTurnedOff();
        Say("Everything is on again.");
    }

}
