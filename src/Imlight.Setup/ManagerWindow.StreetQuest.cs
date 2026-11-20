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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Imlight.Setup;

// Questing street bots (Niduus, 2026-11-20: "an Ahziael update that will allow normal street bots to quest like TCO7. I want
// to be able to swarm the server with bots to collect more accurate data so the bots have multiple success paths instead of
// one single"). Each is one more seat of Ultimus's brain, like a TCO7 member, with a plain random name. This card on
// Bots > Street Bots turns them on, sets how many, and shows what each one is doing. All of it is live ([StreetQuest]).
internal sealed partial class ManagerWindow {

    private const string STREET_QUEST_SECTION = "StreetQuest";
    private const string STREET_QUEST_FOLDER = "street-questers";
    // The same most as the bots (UltimusSeat.STREET_MOST).
    private const int STREET_QUEST_MOST = 30;

    private readonly ToggleSwitch _streetQuestEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _streetQuestCount = new() { Minimum = 0, Maximum = STREET_QUEST_MOST, Increment = 1, FormatString = "0", Width = 140 };
    private readonly ToggleSwitch _streetQuestHelp = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _streetQuestExplore = new() { Minimum = 0, Maximum = 100, Increment = 5, FormatString = "0", Width = 140 };
    private readonly StackPanel _streetQuestRows = new() { Spacing = 6 };
    private readonly DispatcherTimer _streetQuestTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _showingStreetQuest;

    private Control StreetQuestCard() {
        _streetQuestEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingStreetQuest) {
                var on = _streetQuestEnabled.IsChecked == true;
                await Guard(() => SaveStreetQuestAsync("Enabled", on ? "true" : "false",
                    on ? "Questing street bots are on. With the server running, they log in within a few seconds and start the story."
                       : "Questing street bots are off. They log out within a few seconds."));
            }
        };
        _streetQuestHelp.IsCheckedChanged += async (_, _) => {
            if (!_showingStreetQuest) {
                var on = _streetQuestHelp.IsChecked == true;
                await Guard(() => SaveStreetQuestAsync("HelpEachOther", on ? "true" : "false",
                    on ? "Questing street bots join each other's street fights." : "Each questing street bot fights alone (or with real players)."));
            }
        };
        foreach (var (box, key) in new[] { (_streetQuestCount, "Count"), (_streetQuestExplore, "ExplorePercent") }) {
            box.ValueChanged += (_, _) => {
                if (!_showingStreetQuest && box.Value is { } value) {
                    _ = Guard(() => SaveStreetQuestAsync(key, ((int) value).ToString(CultureInfo.InvariantCulture), "Saved. The bots use it right away."));
                }
            };
        }

        LoadStreetQuestSettings();
        _streetQuestTimer.Tick += (_, _) => ShowStreetQuestRows();
        _streetQuestTimer.Start();
        ShowStreetQuestRows();

        return Fold("target", STREET_BOTS_TAB, "Questing street bots",
            "Street bots that play the Main Story the way the TCO7 do, to fill the server with questers and collect far more " +
            "data. Each one runs Ultimus's own brain: the same quest rules (no skipping, Main Story only, legal ways only), and the " +
            "same shared knowledge, so every bot learns from all the others. Each gets a plain random name from the game's own " +
            "name lists, and the schools are taken in turn. They start at level 1 in Wizard City. Their logs are in " +
            "logs\\street-questers. In the game, .streetbots shows them and .streetbots on 6 or .streetbots off works too. Changes count right away.",
            SwitchRow("Questing street bots", _streetQuestEnabled,
                "On: as many as you set below log in within a few seconds and start questing. Off: they log out."),
            SwitchRow("How many quest at once", _streetQuestCount,
                $"0 to {STREET_QUEST_MOST}. Each one is a little program of its own next to the server, so a lot of them need a strong computer. Lowering it logs the extra ones out."),
            SwitchRow("Help each other in fights", _streetQuestHelp,
                "On: a questing street bot joins another one's street fight, as two players would. They never join Ultimus or the TCO7."),
            SwitchRow("Try another way (percent)", _streetQuestExplore,
                "How often a bot, stuck where a way past is already known, tries a different safe move first, so the bots learn more than one way past each spot. 0 = always the known way."),
            new TextBlock { Text = "What each one is doing", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            _streetQuestRows,
            Row(Action(Plain("Open their folder"), () => {
                var folder = Path.Combine(_manager.LogFolder, STREET_QUEST_FOLDER);
                Directory.CreateDirectory(folder);

                return Open(folder);
            })));
    }

    private void LoadStreetQuestSettings() {
        _showingStreetQuest = true;
        try {
            _streetQuestEnabled.IsChecked = _manager.GetSwitch(STREET_QUEST_SECTION, "Enabled");
            _streetQuestCount.Value = Number("Count", 4);
            _streetQuestHelp.IsChecked = !string.Equals(_manager.GetSetting(STREET_QUEST_SECTION, "HelpEachOther")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _streetQuestExplore.Value = Number("ExplorePercent", 25);
        }
        finally {
            _showingStreetQuest = false;
        }

        decimal Number(string key, int fallback)
            => int.TryParse(_manager.GetSetting(STREET_QUEST_SECTION, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private Task SaveStreetQuestAsync(string key, string value, string said) {
        if (!_manager.HasSetting(STREET_QUEST_SECTION, key)) {
            LoadStreetQuestSettings();
            throw new PanelException("This server's Imlight.ini has no [StreetQuest] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        if (_manager.GetSetting(STREET_QUEST_SECTION, key)?.Trim() == value) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting(STREET_QUEST_SECTION, key, value, "")]);
        _settings = [];
        Say(said);
        ShowStreetQuestRows();

        return Task.CompletedTask;
    }

    /// <summary>One line per questing street bot that may play now: name, school, level, area and quest.</summary>
    private void ShowStreetQuestRows() {
        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var on = _manager.GetSwitch(STREET_QUEST_SECTION, "Enabled");
        var count = on && int.TryParse(_manager.GetSetting(STREET_QUEST_SECTION, "Count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 0, STREET_QUEST_MOST) : on ? 4 : 0;
        string[] schools = ["Ice", "Fire", "Storm", "Myth", "Life", "Death", "Balance"];
        _streetQuestRows.Children.Clear();
        if (count == 0) {
            _streetQuestRows.Children.Add(new TextBlock { Text = on ? "None: set how many above." : "Off.", Foreground = MUTED });
            return;
        }

        for (var i = 1; i <= count; i++) {
            var folder = Path.Combine(_manager.LogFolder, STREET_QUEST_FOLDER, $"street{i:00}");
            var name = ReadAllOf(Path.Combine(folder, "name.txt"), 256).Trim() is { Length: > 0 } saved ? saved : $"Street bot {i}";
            var status = ReadTco7Status(folder);
            var at = DateTime.TryParse((string?) status?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
                ? written.ToUniversalTime() : DateTime.MinValue;
            var live = status is not null && DateTime.UtcNow - at < TimeSpan.FromSeconds(30);
            var member = (status?["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var line = !running ? "server not running"
                : !live ? "starting"
                : $"level {(int?) member?["level"] ?? 0}  ·  {(string?) member?["zone"] ?? "?"}  ·  {((string?) status!["quest"] is { Length: > 0 } q ? q : "no quest")}  ·  {(string?) status["doing"] ?? ""}";
            _streetQuestRows.Children.Add(new TextBlock {
                Text = $"{i}. {name} ({schools[(i - 1) % schools.Length]}):  {line}", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Foreground = running && live ? ManagerTheme.TEXT_BRUSH : MUTED,
            });
        }
    }

}
