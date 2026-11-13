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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Game Rules > Teleport Rooms (Niduus, 2026-11-04: "allow for a instance, dungeon and tower instances section of the UI
/// so I and others can choose what rooms/areas are able to be TP into"): every dungeon, tower and other instance, with
/// its rooms, and for each a choice of how it can be teleported into. Kept in spiraldb-extra/Customs/TeleportRooms.json;
/// the server's TeleportRooms.cs watches it, so a choice counts at once.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TELEPORT_ROOMS = "Teleport Rooms";
    private const string TELEPORT_ROOMS_FILE = "TeleportRooms.json";
    private const int TELEPORT_ROOMS_MAX_ROWS = 300;

    // The rules as the file and the server name them.
    private const string TR_GAME = "Game";
    private const string TR_OPEN = "Open";
    private const string TR_CLOSED = "Closed";
    private static readonly string[] s_trRules = [TR_GAME, TR_OPEN, TR_CLOSED];

    private static readonly string[] s_trShow = ["Everything", "Only what I changed", "Only closed", "Only open to friends"];

    private sealed class TeleportRoomRules {
        public string Default { get; set; } = TR_GAME;
        public Dictionary<string, string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Teleporting to a friend from the friends list asks "Do you want to go to your friend?" first (Niduus,
        // 2026-11-13: one click on a friend sometimes teleported). The server reads it from the same file.
        public bool AskBeforeFriendTeleport { get; set; } = true;
    }

    /// <summary>A dungeon, tower or other instance: rooms whose code names differ only by a number or a letter at the end.</summary>
    private sealed class TeleportGroup {
        internal required string Key { get; init; }
        internal required string Name { get; init; }
        internal required string World { get; init; }
        internal required List<LocationRow> Rooms { get; init; }
        internal required string Haystack { get; init; }
        internal bool Instance { get; init; }
    }

    /// <summary>One line of the list: a dungeon (Room is null) or one of its rooms under it.</summary>
    private sealed record TeleportLine(TeleportGroup Group, LocationRow? Room);

    private readonly TextBox _trSearch = new() { Watermark = "Search a dungeon, tower or room, such as Black Sun Pyramid, Darkmoor or KT_Arena" };
    private readonly ComboBox _trWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _trShow = FilterCombo(s_trShow, 190);
    private readonly CheckBox _trEverywhere = new() { Content = "Every place, not only instances" };
    private readonly ComboBox _trDefault = new() { MinWidth = 260 };
    private readonly CheckBox _trAskFriend = new() { Content = "Ask before teleporting to a friend (the game's \"Do you want to go to your friend?\")" };
    private readonly TextBlock _trCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _trSummary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ListBox _trList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly HashSet<string> _trOpenGroups = new(StringComparer.OrdinalIgnoreCase);

    private TeleportRoomRules _trRules = new();
    private List<TeleportGroup> _trGroups = [];
    private bool _trFilling;

    private string TeleportRoomsPath => Path.Combine(_manager.CustomsFolder, TELEPORT_ROOMS_FILE);

    private static string RuleLabel(string rule) => rule switch {
        TR_OPEN => "Open to friends",
        TR_CLOSED => "Closed",
        _ => "Like the game",
    };

    private static string RuleHelp(string rule) => rule switch {
        TR_OPEN => "Open to friends: .tp and the step buttons work, and friends and teammates can teleport to someone inside (they join that player's copy).",
        TR_CLOSED => "Closed: nobody can teleport in. Not with .tp, the Locations or step buttons, quest Go buttons, teleporting to a friend or teammate, or Mark and Recall. Its doors, sigils and Dungeon Recall still work, so players walk in.",
        _ => "Like the game: .tp and the step buttons work, and friends and teammates cannot teleport to someone inside, as on live.",
    };

    private Control TeleportRoomsTab() {
        _trList.ItemTemplate = new FuncDataTemplate<TeleportLine>((line, _) => line is null ? new Panel() : TeleportLineView(line));
        WhenFiltersChange(RenderTeleportRooms, _trSearch, _trWorld, _trShow, _trEverywhere);
        _trDefault.ItemsSource = s_trRules.Select(RuleLabel).ToList();
        _trDefault.SelectionChanged += (_, _) => {
            if (!_trFilling && _trDefault.SelectedIndex >= 0) {
                SetTeleportDefault(s_trRules[_trDefault.SelectedIndex]);
            }
        };
        ToolTip.SetTip(_trDefault, string.Join("\n\n", s_trRules.Select(RuleHelp)));
        ToolTip.SetTip(_trAskFriend, "On: picking a friend to teleport to shows the game's question first, and you only go after Yes, " +
            "so one stray click on the friends list never teleports you. Off: you go at once, as before. Counts at once.");
        _trAskFriend.IsCheckedChanged += (_, _) => {
            if (!_trFilling) {
                SetAskBeforeFriendTeleport(_trAskFriend.IsChecked == true);
            }
        };

        var reset = Plain("Put every room back to the game's way");
        ToolTip.SetTip(reset, "Every dungeon and instance room goes back to Like the game, and every room's own choice is removed.");

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(Fold("sigil", TELEPORT_ROOMS, "Choose which rooms can be teleported into",
                    "Every dungeon, tower and other instance of the game with its rooms. Pick how each one can be teleported " +
                    "into. It counts at once, with no server restart.",
                    WithHint(_trSearch,
                        "Like the game: .tp and the step buttons work, but nobody teleports to a friend or teammate inside, as on live. " +
                        "Open to friends: friends and teammates can also teleport to someone inside, and join their copy. " +
                        "Closed: nobody can teleport in at all (.tp, the Locations and step buttons, quest Go buttons, teleporting to a " +
                        "friend or teammate, Mark and Recall), so the only way in is to walk through its door or sigil. Dungeon Recall still " +
                        "works. Someone already inside can still move around in it. Pick a choice on a dungeon's line to set all its rooms at " +
                        "once, or click Rooms to set each room. The game's files are never changed."),
                    WithCount(SearchFilters(
                        SearchFilter("World", _trWorld),
                        SearchFilter("Show", _trShow),
                        SearchTick(_trEverywhere)),
                        _trCount),
                    new WrapPanel {
                        Children = {
                            Spaced(WithCenter(new TextBlock { Text = "Every dungeon and instance room I have not picked:" })),
                            Spaced(_trDefault),
                        },
                    },
                    new WrapPanel { Children = { Spaced(WithCenter(_trSummary)), Spaced(Action(reset, ResetTeleportRoomsAsync)) } },
                    new WrapPanel { Children = { Spaced(_trAskFriend) } }), Dock.Top),
                WithMargin(_trList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the rules and the place list again. Runs when the page shows.</summary>
    private async Task LoadTeleportRoomsAsync() {
        _trRules = ReadTeleportRooms();
        var (zones, details) = LocationFiles();
        _trCount.Text = "Reading the places…";
        try {
            var rows = await Task.Run(() => ReadLocations(zones, details));
            _trGroups = await Task.Run(() => GroupTeleportRooms(rows));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _trCount.Text = $"The places could not be read: {ex.Message}";

            return;
        }

        FillFilterCombo(_trWorld, _trGroups.Select(g => g.World).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
        RenderTeleportRooms();
    }

    private TeleportRoomRules ReadTeleportRooms() {
        try {
            if (File.Exists(TeleportRoomsPath)) {
                var read = JsonSerializer.Deserialize<TeleportRoomRules>(File.ReadAllText(TeleportRoomsPath), s_ooJson) ?? new TeleportRoomRules();
                read.Default = s_trRules.FirstOrDefault(r => r.Equals(read.Default, StringComparison.OrdinalIgnoreCase)) ?? TR_GAME;
                read.Rooms = new Dictionary<string, string>(
                    (read.Rooms ?? []).Where(r => s_trRules.Contains(r.Value, StringComparer.OrdinalIgnoreCase))
                        .Select(r => KeyValuePair.Create(r.Key, s_trRules.First(x => x.Equals(r.Value, StringComparison.OrdinalIgnoreCase)))),
                    StringComparer.OrdinalIgnoreCase);

                return read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read {TELEPORT_ROOMS_FILE}: {ex.Message}", isError: true);
        }

        return new TeleportRoomRules();
    }

    private void WriteTeleportRooms(TeleportRoomRules rules) {
        Directory.CreateDirectory(_manager.CustomsFolder);
        var path = TeleportRoomsPath;
        File.WriteAllText(path + ".saving", JsonSerializer.Serialize(rules, s_ooJson));
        File.Move(path + ".saving", path, overwrite: true);
    }

    // Rooms of one dungeon share a code name but for a number or letter at the end: AZ_Z01_BlackSunPyramid_Room01 to
    // _Room07, WC_TT01_Fire_L01 to _L30, GH_Aust_Cave01 to 09. A best guess from the names, so each room can still be set.
    private static readonly Regex[] s_trRoomEnds = [
        new(@"[_\-](room|floor|level|lvl|part|stage|tower|chamber|wing|r|f|t|l)?[_\-]?\d+[a-z]?$", RegexOptions.IgnoreCase),
        new(@"(room|floor|level|lvl|part|stage)\d+[a-z]?$", RegexOptions.IgnoreCase),
        new(@"(?<=[a-z])\d+$"),
        new(@"[_\-](a|b|c|d|challenge|hard|adv|advanced|boss)$", RegexOptions.IgnoreCase),
    ];

    internal static string TeleportGroupKey(string code) {
        var slash = code.LastIndexOf('/');
        var tail = slash >= 0 ? code[(slash + 1)..] : code;
        string before;
        do {
            before = tail;
            var next = s_trRoomEnds.Aggregate(tail, (t, end) => end.Replace(t, ""));
            if (next.Length < 3) {
                break;
            }

            tail = next;
        } while (tail != before);

        return code.Split('/')[0] + "/" + tail;
    }

    private static List<TeleportGroup> GroupTeleportRooms(List<LocationRow> rows) {
        // Street and world names are often given to the rooms off them too ("Three Points"); a dungeon's own name is better.
        var areaNames = new HashSet<string>(rows.Where(r => r.Zone.Type == "S").Select(r => r.Zone.Name).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return [.. rows.Where(r => !r.Zone.OldOrTest || r.Zone.Type == "D")
            .GroupBy(r => (r.Zone.Type == "D" ? "D|" : "O|") + TeleportGroupKey(r.Code), StringComparer.OrdinalIgnoreCase)
            .Select(g => {
                var rooms = g.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
                var name = rooms.Count == 1 ? rooms[0].Name
                    : rooms.Select(r => r.Zone.Name).Where(n => n.Length > 0 && !areaNames.Contains(n))
                          .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Count()).Select(n => n.Key).FirstOrDefault()
                      ?? rooms.Select(r => r.Name).GroupBy(n => n).OrderByDescending(n => n.Count()).First().Key;

                return new TeleportGroup {
                    Key = g.Key, Name = name, World = rooms[0].World, Rooms = rooms, Instance = g.Key.StartsWith("D|", StringComparison.Ordinal),
                    Haystack = SearchText.Normalize($"{name} {rooms[0].World} {string.Join(' ', rooms.Select(r => $"{r.Zone.Name} {r.Code} {SearchText.SplitCamel(r.Zone.Tail)}"))}"),
                };
            })
            .OrderBy(g => g.World, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A room's own choice, or null when it follows the default (instances) or the game (other places).</summary>
    private string? OwnRule(LocationRow room) => _trRules.Rooms.GetValueOrDefault(room.Code);

    private string RuleNow(LocationRow room) => OwnRule(room) ?? (room.Zone.Type == "D" ? _trRules.Default : TR_GAME);

    private void RenderTeleportRooms() {
        _trFilling = true;
        _trDefault.SelectedIndex = Array.IndexOf(s_trRules, _trRules.Default);
        _trAskFriend.IsChecked = _trRules.AskBeforeFriendTeleport;
        _trFilling = false;

        var closed = _trRules.Rooms.Count(r => r.Value == TR_CLOSED);
        var open = _trRules.Rooms.Count(r => r.Value == TR_OPEN);
        _trSummary.Text = _trRules.Default == TR_GAME && _trRules.Rooms.Count == 0
            ? "Every room is like the game."
            : $"Rooms I have not picked: {RuleLabel(_trRules.Default).ToLowerInvariant()}. Picked: {closed} closed, {open} open to friends, " +
              $"{_trRules.Rooms.Count - closed - open} like the game.";
        _trSummary.Foreground = _trRules.Default == TR_GAME && _trRules.Rooms.Count == 0 ? GOOD : WARN;

        if (_trGroups.Count == 0) {
            _trList.ItemsSource = null;
            _trCount.Text = "The place list isn't here yet. Start the server once (it writes its zone list), then open this page again.";

            return;
        }

        var words = SearchText.Words(_trSearch.Text);
        var world = FilterPick(_trWorld);
        var show = Math.Max(0, _trShow.SelectedIndex);
        var everywhere = _trEverywhere.IsChecked == true;
        bool Shows(LocationRow room) => show switch {
            1 => OwnRule(room) is not null,
            2 => RuleNow(room) == TR_CLOSED,
            3 => RuleNow(room) == TR_OPEN,
            _ => true,
        };

        var groups = _trGroups.Where(g => (everywhere || g.Instance || g.Rooms.Any(r => OwnRule(r) is not null))
                                          && words.All(w => g.Haystack.Contains(w, StringComparison.Ordinal))
                                          && (world is null || g.World.Equals(world, StringComparison.OrdinalIgnoreCase))
                                          && g.Rooms.Any(Shows))
            .ToList();
        var lines = new List<TeleportLine>();
        foreach (var group in groups) {
            if (lines.Count >= TELEPORT_ROOMS_MAX_ROWS) {
                break;
            }

            lines.Add(new TeleportLine(group, null));
            if (group.Rooms.Count > 1 && _trOpenGroups.Contains(group.Key)) {
                lines.AddRange(group.Rooms.Where(Shows).Select(r => new TeleportLine(group, r)));
            }
        }

        _trList.ItemsSource = lines;
        var rooms = groups.Sum(g => g.Rooms.Count);
        _trCount.Text = groups.Count == 0
            ? "Nothing matches. Clear the search or pick \"Any\" and \"Everything\" in the filters."
            : lines.Count >= TELEPORT_ROOMS_MAX_ROWS
                ? $"Showing the first {TELEPORT_ROOMS_MAX_ROWS} lines of {groups.Count:N0} places ({rooms:N0} rooms). Type more of a name to find the one you want."
                : $"{groups.Count:N0} places, {rooms:N0} rooms.";
    }

    private Control TeleportLineView(TeleportLine line) {
        var rooms = line.Room is null ? line.Group.Rooms : [line.Room];
        var now = rooms.Select(RuleNow).Distinct().ToList();
        var choice = new ComboBox { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
        var labels = s_trRules.Select(RuleLabel).ToList();
        choice.ItemsSource = labels;
        if (now.Count == 1) {
            choice.SelectedIndex = Array.IndexOf(s_trRules, now[0]);
        }
        else {
            choice.PlaceholderText = "Mixed";
        }

        ToolTip.SetTip(choice, now.Count == 1 ? RuleHelp(now[0]) : "Its rooms have different choices. Pick one to set them all, or click Rooms.");
        choice.SelectionChanged += (_, _) => {
            if (choice.SelectedIndex >= 0 && (now.Count != 1 || s_trRules[choice.SelectedIndex] != now[0])) {
                SetTeleportRule(line, s_trRules[choice.SelectedIndex]);
            }
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (line.Room is null && line.Group.Rooms.Count > 1) {
            var shown = _trOpenGroups.Contains(line.Group.Key);
            var toggle = Plain(shown ? "Hide rooms" : $"Rooms ({line.Group.Rooms.Count})");
            toggle.Click += (_, _) => {
                if (!_trOpenGroups.Remove(line.Group.Key)) {
                    _trOpenGroups.Add(line.Group.Key);
                }

                RenderTeleportRooms();
            };
            right.Children.Add(toggle);
        }

        right.Children.Add(choice);

        string title, detail;
        if (line.Room is { } room) {
            title = room.Name;
            detail = room.Code + (OwnRule(room) is null ? " · " + (room.Zone.Type == "D" ? "follows the choice above for rooms I have not picked" : "like the game") : "");
        }
        else if (line.Group.Rooms.Count == 1) {
            title = line.Group.Name;
            detail = $"{line.Group.World} · {line.Group.Rooms[0].Code}";
        }
        else {
            title = line.Group.Name;
            detail = $"{line.Group.World} · {line.Group.Rooms.Count} rooms · {line.Group.Key[2..]}…";
        }

        return new DockPanel {
            Margin = new Thickness(line.Room is null ? 4 : 34, 3, 4, 3),
            Children = {
                WithDock(right, Dock.Right),
                new StackPanel {
                    Spacing = 1,
                    Children = {
                        new TextBlock { Text = title, FontWeight = line.Room is null ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = detail, Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap },
                    },
                },
            },
        };
    }

    private void SetTeleportRule(TeleportLine line, string rule) {
        try {
            // Read again first, so a change made in the file (or on another PC's Remote) is kept.
            _trRules = ReadTeleportRooms();
            var rooms = line.Room is null ? line.Group.Rooms : [line.Room];
            foreach (var room in rooms) {
                // The same as the room would have anyway: no choice of its own, so a later default still counts for it.
                var anyway = room.Zone.Type == "D" ? _trRules.Default : TR_GAME;
                if (rule == anyway) {
                    _trRules.Rooms.Remove(room.Code);
                }
                else {
                    _trRules.Rooms[room.Code] = rule;
                }
            }

            WriteTeleportRooms(_trRules);
            var name = line.Room?.Name ?? line.Group.Name;
            Say($"{name}{(rooms.Count > 1 ? $" ({rooms.Count} rooms)" : "")}: {RuleLabel(rule).ToLowerInvariant()} now. It counts at once if the server is running.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save {TELEPORT_ROOMS_FILE}: {ex.Message}", isError: true);
        }

        RenderTeleportRooms();
    }

    private void SetTeleportDefault(string rule) {
        try {
            _trRules = ReadTeleportRooms();
            if (_trRules.Default == rule) {
                return;
            }

            _trRules.Default = rule;
            WriteTeleportRooms(_trRules);
            Say($"Every dungeon and instance room you have not picked is {RuleLabel(rule).ToLowerInvariant()} now. It counts at once if the server is running.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save {TELEPORT_ROOMS_FILE}: {ex.Message}", isError: true);
        }

        RenderTeleportRooms();
    }

    private void SetAskBeforeFriendTeleport(bool ask) {
        try {
            _trRules = ReadTeleportRooms();
            if (_trRules.AskBeforeFriendTeleport == ask) {
                return;
            }

            _trRules.AskBeforeFriendTeleport = ask;
            WriteTeleportRooms(_trRules);
            Say(ask
                ? "Teleporting to a friend asks first now. It counts at once if the server is running."
                : "Teleporting to a friend goes at once now, with no question. It counts at once if the server is running.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save {TELEPORT_ROOMS_FILE}: {ex.Message}", isError: true);
        }
    }

    private async Task ResetTeleportRoomsAsync() {
        _trRules = ReadTeleportRooms();
        if (_trRules.Default == TR_GAME && _trRules.Rooms.Count == 0) {
            Say("Every room is like the game already.");

            return;
        }

        if (!await ConfirmAsync($"Put every room back to the game's way? This removes {_trRules.Rooms.Count} room choices.")) {
            return;
        }

        _trRules = new TeleportRoomRules { AskBeforeFriendTeleport = _trRules.AskBeforeFriendTeleport };
        WriteTeleportRooms(_trRules);
        RenderTeleportRooms();
        Say("Every room is like the game again.");
    }

}
