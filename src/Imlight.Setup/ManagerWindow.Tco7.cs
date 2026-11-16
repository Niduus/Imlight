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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

// The TCO7 section (Niduus, 2026-11-16): "This will be called the TCO7 (The Calling of 7) for now, I want one of each
// school ... Each Member gets their own sub-page in the TCO7 section. Each page is a complete mirror of Ultimus' page."
// The TCO7 page turns the team on and off, with its talk and team-up switches and each member's state. Each member's page
// is Ultimus's own page (with its Issues and Logs) showing that member: its folder (logs\tco7\<name>) and its own settings
// ([TCO7-<Name>], empty = the same as Ultimus's). There is one Ultimus page; it moves to the member page that is open.
internal sealed partial class ManagerWindow {

    private const string TCO7_TAB = "TCO7";

    // Ultimus's three pages (main, Issues, Logs), and where each lives on the Ultimus section.
    private readonly Control?[] _ultBodies = new Control?[3];
    private readonly ContentControl?[] _ultHomes = new ContentControl?[3];
    private static readonly string[] s_ultParts = ["Overview", "Issues", "Logs"];

    // Each member page's holder, and which of the three parts it shows.
    private readonly Dictionary<string, ContentControl> _tco7Hosts = [];
    private readonly Dictionary<string, int> _tco7Part = [];
    private readonly Dictionary<string, TextBlock> _tco7Notes = [];
    private readonly Dictionary<string, List<Button>> _tco7PartButtons = [];

    // The TCO7 page.
    private readonly ToggleSwitch _tco7Enabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tco7Talk = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _tco7TeamUp = new() { OnContent = "On", OffContent = "Off" };
    private readonly Dictionary<string, ToggleSwitch> _tco7Members = [];
    private readonly StackPanel _tco7Rows = new() { Spacing = 6 };
    private readonly TextBox _tco7Link = new() {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 360, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly TextBox _tco7Council = new() {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 420, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly TextBox _tco7Efficiency = new() {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 420, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly Avalonia.Threading.DispatcherTimer _tco7Timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _showingTco7;

    /// <summary>One of Ultimus's three pages, built once and kept where it can be moved to a TCO7 member's page.</summary>
    private Control UltimusPart(int part, Func<Control> build) {
        var body = build();
        _ultBodies[part] = body;
        var home = new ContentControl { Content = body };
        _ultHomes[part] = home;

        return home;
    }

    /// <summary>The tab of each TCO7 member, after the TCO7 page.</summary>
    private IEnumerable<TabItem> Tco7Tabs() {
        yield return Tab(TCO7_TAB, Tco7Tab);
        foreach (var seat in s_tco7Seats) {
            yield return Tab(seat.Name, () => Tco7MemberTab(seat));
        }
    }

    // Written out (not made from s_tco7Seats): the side bar's list is made before that one is.
    private static string[] Tco7TabNames => [TCO7_TAB, "Tulkar", "Cyndr", "Kurrent", "Mythis", "Idunn", "Garm", "Ra"];

    // ---------------------------------------------------------------------
    // A member's page: Ultimus's page, Issues and Logs, showing that member
    // ---------------------------------------------------------------------

    private Control Tco7MemberTab(UltSeat seat) {
        var host = new ContentControl();
        _tco7Hosts[seat.Name] = host;
        _tco7Part[seat.Name] = 0;
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
        _tco7Notes[seat.Name] = note;
        var buttons = new List<Button>();
        for (var i = 0; i < s_ultParts.Length; i++) {
            var part = i;
            var button = Plain(s_ultParts[i]);
            button.Click += (_, _) => ShowTco7Part(seat, part);
            buttons.Add(button);
        }

        _tco7PartButtons[seat.Name] = buttons;
        var top = new StackPanel {
            Margin = new Thickness(24, 10, 24, 0), Spacing = 6, MaxWidth = ManagerTheme.PAGE_MAX_WIDTH, HorizontalAlignment = HorizontalAlignment.Left,
            Children = {
                new TextBlock { Text = $"{seat.Name}  ·  {seat.School}  ·  Essential", FontWeight = FontWeight.SemiBold, FontSize = ManagerTheme.FONT_SMALL + 3 },
                note,
                Row([.. buttons]),
            },
        };
        PaintTco7Parts(seat.Name);

        return new DockPanel { Children = { WithDock(top, Dock.Top), host } };
    }

    private void ShowTco7Part(UltSeat seat, int part) {
        _tco7Part[seat.Name] = part;
        if (_ultSeat == seat) {
            PutUltimusPart(seat);
        }

        PaintTco7Parts(seat.Name);
    }

    private void PaintTco7Parts(string name) {
        if (!_tco7PartButtons.TryGetValue(name, out var buttons)) {
            return;
        }

        var shown = _tco7Part.GetValueOrDefault(name);
        for (var i = 0; i < buttons.Count; i++) {
            buttons[i].FontWeight = i == shown ? FontWeight.Bold : FontWeight.Normal;
            buttons[i].Opacity = i == shown ? 1 : 0.75;
        }
    }

    // The part shown on this member's page moves there; the other parts go home to the Ultimus section.
    private void PutUltimusPart(UltSeat seat) {
        var shown = _tco7Part.GetValueOrDefault(seat.Name);
        if (!_tco7Hosts.TryGetValue(seat.Name, out var host)) {
            return;
        }

        foreach (var other in _tco7Hosts.Values.Where(h => h != host)) {
            other.Content = null;
        }

        for (var i = 0; i < _ultBodies.Length; i++) {
            if (i != shown && _ultHomes[i] is { } home && home.Content != _ultBodies[i]) {
                host.Content = host.Content == _ultBodies[i] ? null : host.Content;
                home.Content = _ultBodies[i];
            }
        }

        if (_ultBodies[shown] is { } body && host.Content != body) {
            if (_ultHomes[shown] is { } from && from.Content == body) {
                from.Content = null;
            }

            host.Content = null;
            host.Content = body;
        }
    }

    // Every part back on the Ultimus section.
    private void BringUltimusPartsHome() {
        foreach (var host in _tco7Hosts.Values) {
            host.Content = null;
        }

        for (var i = 0; i < _ultBodies.Length; i++) {
            if (_ultHomes[i] is { } home && home.Content != _ultBodies[i]) {
                home.Content = _ultBodies[i];
            }
        }
    }

    /// <summary>A page came into view: the Ultimus pages show Ultimus, or the TCO7 member whose page it is.</summary>
    private void UltimusSeatFor(string? page) {
        if (page is ULTIMUS_TAB or ULTIMUS_ISSUES_TAB or ULTIMUS_LOGS_TAB) {
            BringUltimusPartsHome();
            UseUltSeat(s_ultimusSeat);

            return;
        }

        if (page == TCO7_TAB) {
            ShowTco7();

            return;
        }

        if (s_tco7Seats.FirstOrDefault(s => s.Name == page) is { } seat) {
            UseUltSeat(seat);
            PutUltimusPart(seat);
            ShowTco7MemberNote(seat);
        }
    }

    private void UseUltSeat(UltSeat seat) {
        if (_ultSeat == seat) {
            return;
        }

        _ultSeat = seat;
        // What was read for the one shown before is read again for this one.
        _ultIssuesWritten = DateTime.MinValue;
        _ultIssueList = [];
        _ultIssuesShown = -1;
        _ultLogText = _ultQuestLogText = _ultProgressText = _ultThoughtsText = _ultMistakesText = "";
        _ultReportText = _ultDiaryText = _ultReadinessText = "";
        _ultFriendsRead = _ultReportRead = _ultDiaryRead = _ultStuckWeekRead = _ultGatesRead = _ultPeopleRead = DateTime.MinValue;
        _ultReadinessChanged = _ultSolutionsRead = _ultLearnedRead = _ultSafetyRead = DateTime.MinValue;
        _ultSheet.Seat = seat.IsTco7 ? seat.Name : "";
        LoadUltimusSettings();
        RefreshUltimus();
    }

    private void ShowTco7MemberNote(UltSeat seat) {
        if (!_tco7Notes.TryGetValue(seat.Name, out var note)) {
            return;
        }

        var team = _manager.GetSwitch("TCO7", "Enabled");
        var own = !string.Equals(_manager.GetSetting("TCO7", seat.Name)?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        note.Text = !team ? "The TCO7 are off. Turn them on with the switch on the TCO7 page; then this page shows " + seat.Name + " playing."
            : !own ? $"{seat.Name} is switched off on the TCO7 page, so it stays logged out while the others play."
            : $"Everything below is {seat.Name}'s own: its folder is logs\\tco7\\{seat.Name.ToLowerInvariant()}, and a setting left as Ultimus's follows his. " +
              "It reads all Ultimus has learned and tries to beat his quest times.";
        note.Foreground = !team || !own ? WARN : MUTED;
    }

    // ---------------------------------------------------------------------
    // The TCO7 page
    // ---------------------------------------------------------------------

    private Control Tco7Tab() {
        foreach (var seat in s_tco7Seats) {
            _tco7Members[seat.Name] = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        }

        _tco7Enabled.IsCheckedChanged += async (_, _) => {
            if (!_showingTco7) {
                await Guard(() => SaveTco7Async("Enabled", _tco7Enabled.IsChecked == true,
                    "The TCO7 are on. With the server running, each logs in within a few seconds and starts questing.",
                    "The TCO7 are off. They log out within a few seconds."));
            }
        };
        _tco7Talk.IsCheckedChanged += async (_, _) => {
            if (!_showingTco7) {
                await Guard(() => SaveTco7Async("Talk", _tco7Talk.IsChecked == true,
                    "The TCO7 talk to each other when they meet.", "The TCO7 keep quiet with each other."));
            }
        };
        _tco7TeamUp.IsCheckedChanged += async (_, _) => {
            if (!_showingTco7) {
                await Guard(() => SaveTco7Async("TeamUp", _tco7TeamUp.IsChecked == true,
                    "Two TCO7 members on the same quest may now group up and quest together.", "Each TCO7 member quests alone."));
            }
        };
        foreach (var (name, toggle) in _tco7Members) {
            toggle.IsCheckedChanged += async (_, _) => {
                if (!_showingTco7) {
                    await Guard(() => SaveTco7Async(name, toggle.IsChecked == true, $"{name} is on.", $"{name} is off: it stays logged out."));
                }
            };
        }

        _tco7Timer.Tick += (_, _) => {
            if (_shownPage == TCO7_TAB) {
                ShowTco7Rows();
            }
        };
        _tco7Timer.Start();

        var members = new StackPanel { Spacing = 4 };
        foreach (var seat in s_tco7Seats) {
            members.Children.Add(SwitchRow($"{seat.Name} ({seat.School})", _tco7Members[seat.Name]));
        }

        return Page(
            Fold("users", ULTIMUS_TAB, "The Calling of 7",
                "Seven wizards, one of each school: Tulkar (Ice), Cyndr (Fire), Kurrent (Storm), Mythis (Myth), Idunn (Life), " +
                "Garm (Death) and Ra (Balance), all with the Essential title. Each one is its own Ultimus: the same brain, the same " +
                "rules (quests like a player, no skipping, no free XP or gold, Main Story first) and its own little program next to " +
                "the server. They read everything Ultimus has done (his maps, teleporters, stuck answers, quest tree and creatures), " +
                "add what they learn to it, and try to beat his time on every quest. They talk to each other when they meet and may " +
                "team up on the same quest, or quest alone. Ultimus himself always quests alone. Each member has a page of its own " +
                "here, the same as Ultimus's page.",
                SwitchRow("TCO7", _tco7Enabled, "On: the members switched on below log in a few seconds later and start questing. Off: they log out."),
                SwitchRow("Talk to each other", _tco7Talk, "On: when two meet, they say hello and ask each other's quest, in game chat."),
                SwitchRow("Team up", _tco7TeamUp, "On: two that meet on the same quest may group up and quest together, and part when their quests split."),
                members),
            Fold("list", ULTIMUS_TAB, "What each one is doing",
                "Each member's state, quest and level, every few seconds. Open a member's page for everything else.",
                _tco7Rows),
            Fold("book", ULTIMUS_TAB, "What they read from Ultimus",
                "What the TCO7 read from Ultimus every half hour: how far he is, his slowest quests to beat, his newest lessons and his stuck answers.",
                _tco7Link),
            Fold("flag", ULTIMUS_TAB, "TCO7 council",
                "What got a member completely stuck, and every problem 4 or more of the 7 share, with who got past it and what worked for them. " +
                "Ultimus and every member read this on that quest, so the whole model gets better from it. Written every 10 minutes while the TCO7 are on.",
                _tco7Council),
            Fold("clock", ULTIMUS_TAB, "TCO7 efficiency",
                "Each member's pace, the quests where they are clearly faster than Ultimus (a quicker way exists), and quests where they are all slow.",
                _tco7Efficiency));
    }

    private void ShowTco7() {
        _showingTco7 = true;
        try {
            _tco7Enabled.IsChecked = _manager.GetSwitch("TCO7", "Enabled");
            _tco7Talk.IsChecked = !string.Equals(_manager.GetSetting("TCO7", "Talk")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _tco7TeamUp.IsChecked = !string.Equals(_manager.GetSetting("TCO7", "TeamUp")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            foreach (var (name, toggle) in _tco7Members) {
                toggle.IsChecked = !string.Equals(_manager.GetSetting("TCO7", name)?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally {
            _showingTco7 = false;
        }

        ShowTco7Rows();
    }

    private void ShowTco7Rows() {
        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var team = _manager.GetSwitch("TCO7", "Enabled");
        _tco7Rows.Children.Clear();
        var link = "";
        foreach (var seat in s_tco7Seats) {
            var folder = Path.Combine(_manager.LogFolder, seat.Folder);
            var status = ReadTco7Status(folder);
            var at = DateTime.TryParse((string?) status?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
                ? written.ToUniversalTime() : DateTime.MinValue;
            var live = status is not null && DateTime.UtcNow - at < TimeSpan.FromSeconds(30);
            var on = team && _tco7Members.TryGetValue(seat.Name, out var toggle) && toggle.IsChecked == true;
            var member = (status?["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var line = !running ? "server not running"
                : !on ? "off"
                : !live ? "starting"
                : $"level {(int?) member?["level"] ?? 0}  ·  {(string?) member?["zone"] ?? "?"}  ·  {((string?) status!["quest"] is { Length: > 0 } q ? q : "no quest")}  ·  {(string?) status["doing"] ?? ""}";
            var row = new DockPanel();
            var open = Plain("Open");
            open.Click += (_, _) => SelectTab(seat.Name);
            row.Children.Add(WithDock(open, Dock.Right));
            row.Children.Add(new TextBlock {
                Text = $"{seat.Name} ({seat.School}):  {line}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Foreground = running && on && live ? ManagerTheme.TEXT_BRUSH : MUTED,
            });
            _tco7Rows.Children.Add(row);
            if (link.Length == 0) {
                link = ReadAllOf(Path.Combine(folder, "ultimus-link.txt"), 32 * 1024);
            }
        }

        var council = ReadAllOf(Path.Combine(UltimusSharedFolder, "tco7-council.txt"), 64 * 1024).TrimEnd();
        council = council.Length > 0 ? council : "Nothing yet: the council writes here once the TCO7 have played a while.";
        if (_tco7Council.Text != council) {
            _tco7Council.Text = council;
        }

        var efficiency = ReadAllOf(Path.Combine(UltimusSharedFolder, "tco7-efficiency.txt"), 64 * 1024).TrimEnd();
        efficiency = efficiency.Length > 0 ? efficiency : "Nothing yet: it shows once the TCO7 have finished some quests.";
        if (_tco7Efficiency.Text != efficiency) {
            _tco7Efficiency.Text = efficiency;
        }

        var shown = link.Length > 0 ? link.TrimEnd() : "Nothing read yet: the TCO7 read Ultimus's records once they are playing.";
        if (_tco7Link.Text != shown) {
            _tco7Link.Text = shown;
        }
    }

    private static JsonObject? ReadTco7Status(string folder) {
        try {
            var path = Path.Combine(folder, "status.json");
            if (!File.Exists(path)) {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    private Task SaveTco7Async(string key, bool on, string saidOn, string saidOff) {
        if (!_manager.HasSetting("TCO7", key)) {
            ShowTco7();
            throw new PanelException("This server's Imlight.ini has no [TCO7] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("TCO7", key, on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? saidOn : saidOff);
        ShowTco7Rows();

        return Task.CompletedTask;
    }

}
