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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

// The Kitharas Family section (Niduus, 2026-11-20: twenty more bots that "function the same with the same purpose as TCO7",
// then "I want them to have a Sub Menu in Ahziael for them to do what I already do with Ahziael, True Form and TCO7").
// The family's page turns them on and off, sets how many play at once, their talk, team-up and squad, each member's switch,
// and shows what each is doing, their council and their pace. Each member's page is Ultimus's own page (with its Issues and
// Logs) showing that member, exactly as a TCO7 member's page (ManagerWindow.Tco7.cs).
internal sealed partial class ManagerWindow {

    private const string KITHARAS_TAB = "Kitharas Family";

    // Written out (not made from s_kitharasSeats): the side bar's list is made before that one is.
    private static string[] KitharasTabNames => [
        KITHARAS_TAB, "Kitharas Moon", "Kitharas Hart", "Kitharas Rock", "Kitharas Andromeda", "Kitharas Hydra", "Kitharas Jackle",
        "Kitharas Lamia", "Kitharas Libra", "Kitharas Shade", "Kitharas Wraith", "Lumeria of Kitharas", "Kitharas Consumer",
        "Kitharas Mordiael", "Kitharas Devourer", "Kitharas Hexation", "Kitharas Sentence", "Kitharas Was Silenced",
        "Crimes Against Kitharas", "Kitharas Umbella", "AlchemiZist of Kitharas",
    ];

    private readonly ToggleSwitch _kinEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _kinTalk = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _kinTeamUp = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _kinCount = new() { Minimum = 0, Maximum = 20, Increment = 1, FormatString = "0", Width = 140 };
    private readonly TextBox _kinSquad = new() { Width = 320, Watermark = "Moon, Hart, Rock, Hydra" };
    private readonly Dictionary<string, ToggleSwitch> _kinMembers = [];
    private readonly StackPanel _kinRows = new() { Spacing = 6 };
    private readonly TextBlock _kinLoad = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly TextBox _kinCouncil = new() {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 420, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly TextBox _kinEfficiency = new() {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 420, FontFamily = new FontFamily("Consolas, Menlo, monospace"),
    };
    private readonly Avalonia.Threading.DispatcherTimer _kinTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _showingKin;

    /// <summary>The family's page, then a page for each member (the same as a TCO7 member's).</summary>
    private IEnumerable<TabItem> KitharasTabs() {
        yield return Tab(KITHARAS_TAB, KitharasTab);
        foreach (var seat in s_kitharasSeats) {
            yield return Tab(seat.Name, () => Tco7MemberTab(seat));
        }
    }

    private Control KitharasTab() {
        foreach (var seat in s_kitharasSeats) {
            _kinMembers[seat.SwitchKey] = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        }

        _kinEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingKin) {
                await Guard(() => SaveKitharasAsync("Enabled", _kinEnabled.IsChecked == true ? "true" : "false",
                    _kinEnabled.IsChecked == true
                        ? $"The Kitharas Family is on. With the server running, {KitharasCount()} of them log in within a few seconds and start questing."
                        : "The Kitharas Family is off. They log out within a few seconds."));
            }
        };
        _kinTalk.IsCheckedChanged += async (_, _) => {
            if (!_showingKin) {
                await Guard(() => SaveKitharasAsync("Talk", _kinTalk.IsChecked == true ? "true" : "false",
                    _kinTalk.IsChecked == true ? "The Kitharas Family talk to each other when they meet." : "The Kitharas Family keep quiet with each other."));
            }
        };
        _kinTeamUp.IsCheckedChanged += async (_, _) => {
            if (!_showingKin) {
                await Guard(() => SaveKitharasAsync("TeamUp", _kinTeamUp.IsChecked == true ? "true" : "false",
                    _kinTeamUp.IsChecked == true ? "Two of the family on the same quest may now group up and quest together." : "Each of the family quests alone."));
            }
        };
        _kinCount.ValueChanged += async (_, _) => {
            if (!_showingKin && _kinCount.Value is { } value) {
                var n = (int) Math.Clamp(value, 0, s_kitharasSeats.Length);
                await Guard(() => SaveKitharasAsync("Count", n.ToString(CultureInfo.InvariantCulture),
                    $"{n} of the Kitharas Family may play at once (the first {n} switched on below). Extra ones log out, new ones log in, within a few seconds."));
            }
        };
        foreach (var seat in s_kitharasSeats) {
            var toggle = _kinMembers[seat.SwitchKey];
            toggle.IsCheckedChanged += async (_, _) => {
                if (!_showingKin) {
                    await Guard(() => SaveKitharasAsync(seat.SwitchKey, toggle.IsChecked == true ? "true" : "false",
                        toggle.IsChecked == true ? $"{seat.Name} is on." : $"{seat.Name} is off: it stays logged out, and the next one takes its seat if Count allows."));
                }
            };
        }

        _kinSquad.LostFocus += async (_, _) => {
            if (!_showingKin) {
                await Guard(SaveKitharasSquadAsync);
            }
        };

        _kinTimer.Tick += (_, _) => {
            if (_shownPage == KITHARAS_TAB) {
                ShowKitharasRows();
            }
        };
        _kinTimer.Start();

        var members = new StackPanel { Spacing = 4 };
        foreach (var seat in s_kitharasSeats) {
            members.Children.Add(SwitchRow($"{seat.Name} ({seat.School})", _kinMembers[seat.SwitchKey]));
        }

        return Page(
            Fold("users", ULTIMUS_TAB, "The Kitharas Family",
                "Twenty wizards, each its own Ultimus exactly like a TCO7 member: the same brain, the same rules (quests like a player, " +
                "no skipping, no free XP or gold, Main Story first) and its own little program next to the server. Their schools " +
                "were picked at random once and stay with them. They read everything Ultimus has learned and add to it, try to beat " +
                "his time on every quest, talk with each other, team up on the same quest or quest alone, and hold their own council. " +
                "Players call one by its own word (\"Moon, heal me\"), since \"Kitharas\" would call them all. Off until you turn them on.",
                SwitchRow("Kitharas Family", _kinEnabled, "On: the members switched on below log in a few seconds later and start questing, up to Count of them. Off: they log out."),
                SwitchRow("How many play", _kinCount, "How many of them play at once (0 to 20): the first ones switched on below. Start with a few and raise it while the PC copes."),
                _kinLoad,
                SwitchRow("Talk to each other", _kinTalk, "On: when two meet, they say hello and ask each other's quest, in game chat (each starts a talk at most once every 90 seconds)."),
                SwitchRow("Team up", _kinTeamUp, "On: two that meet on the same quest may group up and quest together, and part when their quests split."),
                SwitchRow("Squad", _kinSquad, "Up to four of them with commas, by name or their own word; empty = no squad. The squad groups up for good: the one " +
                    "furthest behind leads and the others help it catch up, then they quest on together. The rest quest at their own pace."),
                members),
            Fold("list", ULTIMUS_TAB, "What each one is doing",
                "Each member's state, quest and level, every few seconds. Open a member's page for everything else.",
                _kinRows),
            Fold("flag", ULTIMUS_TAB, "Kitharas Family council",
                "What got a member completely stuck, and every problem 11 or more of the 20 share, with who got past it and what worked for them. " +
                "Ultimus and every bot under him read this on that quest. Written every 10 minutes while the family is on.",
                _kinCouncil),
            Fold("clock", ULTIMUS_TAB, "Kitharas Family efficiency",
                "Each member's pace, the quests where they are clearly faster than Ultimus (a quicker way exists), and quests where they are all slow.",
                _kinEfficiency));
    }

    private void ShowKitharas() {
        _showingKin = true;
        try {
            _kinEnabled.IsChecked = _manager.GetSwitch(KITHARAS, "Enabled");
            _kinTalk.IsChecked = !string.Equals(_manager.GetSetting(KITHARAS, "Talk")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _kinTeamUp.IsChecked = !string.Equals(_manager.GetSetting(KITHARAS, "TeamUp")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _kinCount.Value = KitharasCount();
            _kinSquad.Text = _manager.GetSetting(KITHARAS, "Squad") ?? "";
            foreach (var (key, toggle) in _kinMembers) {
                toggle.IsChecked = !string.Equals(_manager.GetSetting(KITHARAS, key)?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally {
            _showingKin = false;
        }

        ShowKitharasRows();
    }

    private void ShowKitharasRows() {
        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var count = KitharasCount();
        _kinLoad.Text = $"Each one is a full questing bot with its own little program and about 15 MB of logs a day. {count} at once is set now; " +
                        "watch the server's Health lines (Server > Logs) and your PC's memory before raising it.";
        _kinRows.Children.Clear();
        foreach (var seat in s_kitharasSeats) {
            var folder = Path.Combine(_manager.LogFolder, seat.Folder);
            var status = ReadTco7Status(folder);
            var at = DateTime.TryParse((string?) status?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
                ? written.ToUniversalTime() : DateTime.MinValue;
            var live = status is not null && DateTime.UtcNow - at < TimeSpan.FromSeconds(30);
            var why = TeamOffWhy(seat);
            var ownOff = string.Equals(_manager.GetSetting(seat.Section, "Enabled")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            var on = why.Length == 0 && !ownOff;
            var member = (status?["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var line = !running ? "server not running"
                : ownOff ? "off (its own page)"
                : !on ? (why.StartsWith("only", StringComparison.Ordinal) ? "waiting (past Count)" : "off")
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
            _kinRows.Children.Add(row);
        }

        var council = ReadAllOf(Path.Combine(UltimusSharedFolder, "kitharas-council.txt"), 64 * 1024).TrimEnd();
        council = council.Length > 0 ? council : "Nothing yet: the council writes here once the Kitharas Family have played a while.";
        if (_kinCouncil.Text != council) {
            _kinCouncil.Text = council;
        }

        var efficiency = ReadAllOf(Path.Combine(UltimusSharedFolder, "kitharas-efficiency.txt"), 64 * 1024).TrimEnd();
        efficiency = efficiency.Length > 0 ? efficiency : "Nothing yet: it shows once the Kitharas Family have finished some quests.";
        if (_kinEfficiency.Text != efficiency) {
            _kinEfficiency.Text = efficiency;
        }
    }

    private Task SaveKitharasSquadAsync() {
        if (!_manager.HasSetting(KITHARAS, "Squad")) {
            ShowKitharas();
            throw new PanelException("This server's Imlight.ini has no [Kitharas] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        var parts = (_kinSquad.Text ?? "").Contains(',') ? (_kinSquad.Text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            : (_kinSquad.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var picked = parts.Select(p => KitharasNamed(p.Trim())).Where(s => s is not null).Select(s => s!).Distinct().Take(4).ToList();
        // Saved by full name, with commas (the server reads names, keys and words alike).
        var saved = string.Join(", ", picked.Select(s => s.Name));
        _manager.SaveSettings([new Setting(KITHARAS, "Squad", saved, "")]);
        _settings = [];
        _kinSquad.Text = saved;
        Say(picked.Count >= 2 ? $"Squad saved: {saved}. They group up within a minute." : picked.Count == 1
            ? "A squad needs at least two of them." : "No squad: each of the family quests at its own pace.");

        return Task.CompletedTask;
    }

    // A member by its name, key or own word ("Moon"), any case.
    private static UltSeat? KitharasNamed(string who)
        => s_kitharasSeats.FirstOrDefault(s => s.Name.Equals(who, StringComparison.OrdinalIgnoreCase)
                                               || s.SwitchKey.Equals(who, StringComparison.OrdinalIgnoreCase)
                                               || Path.GetFileName(s.Folder).Equals(who, StringComparison.OrdinalIgnoreCase));

    private Task SaveKitharasAsync(string key, string value, string said) {
        if (!_manager.HasSetting(KITHARAS, key)) {
            ShowKitharas();
            throw new PanelException("This server's Imlight.ini has no [Kitharas] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting(KITHARAS, key, value, "")]);
        _settings = [];
        Say(said);
        ShowKitharasRows();

        return Task.CompletedTask;
    }

}
