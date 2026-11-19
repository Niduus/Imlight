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
 *
 */

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Bot Updates (Niduus, 2026-11-19: "Make the bots more update-able ... soft reset only themselves ... lowers the bloat in
/// patch notes"): which bots the server runs, the bot updates published on GitHub with their notes, and the switch for
/// live bot updates. The server writes what it knows to its bots\state.json (Game/BotDoor/BotUpdates.cs); this reads it.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string BOT_UPDATES_TAB = "Bot Updates";

    private readonly ToggleSwitch _botUpdLive = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _botUpdWarn = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _botUpdWarnMinutes = new() { Minimum = 0.5m, Maximum = 30, Increment = 0.5m, FormatString = "0.#", Width = 110, Value = 2 };
    private readonly TextBlock _botUpdState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _botUpdList = new() { Spacing = 8 };
    private readonly StackPanel _botUpdLog = new() { Spacing = 2 };
    private DispatcherTimer? _botUpdTimer;
    private bool _showingBotUpd;

    private string BotsFolder => Path.Combine(_manager.ServerFolder, "bots");

    private Control BotUpdatesTab() {
        _botUpdLive.IsCheckedChanged += async (_, _) => {
            if (!_showingBotUpd) {
                await Guard(() => SetLiveBotUpdatesAsync(_botUpdLive.IsChecked == true));
            }
        };

        _botUpdWarn.IsCheckedChanged += (_, _) => {
            if (!_showingBotUpd) {
                SaveBotWarning();
            }
        };
        _botUpdWarnMinutes.ValueChanged += (_, _) => {
            if (!_showingBotUpd && _botUpdWarnMinutes.Value is not null) {
                SaveBotWarning();
            }
        };

        _botUpdTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => RefreshBotUpdates());
        _botUpdTimer.Start();
        RefreshBotUpdates();

        return Page(
            Fold("download", ULTIMUS_TAB, "Bot updates",
                "Ultimus, the TCO7, the Quest Team and the Bot Players are their own part of the server now, so they can be " +
                "updated without a new Ahziael. Every bot fix goes out as a bot update by itself as soon as it is merged. With live bot updates on, " +
                "the server looks for a new one every 10 minutes, builds it on this PC and swaps the bots: they log out and come " +
                "back a few seconds later (after any battle they are in ends). You and other players stay in the game and the " +
                "server keeps running. A bot update that does not fit this server, or that needs a server change this server does not have yet, waits and the bots stay as they are until the next full update.",
                _botUpdState,
                SwitchRow("Live bot updates", _botUpdLive,
                    "On: new bot updates are taken by themselves within 10 minutes. Off: the bots stay as they are until you turn this on or update Ahziael."),
                SwitchRow("Warn players first", _botUpdWarn,
                    "On: everyone online is told in chat before the bots go down for an update (and again at 1 minute and 30 seconds), then when they are back. Nothing holds the bots back, and with nobody online they update at once. Off: no warning and no wait."),
                SwitchRow("Warning time (minutes)", _botUpdWarnMinutes,
                    "How long the warning runs before the bots go down, from half a minute to 30 minutes. Changes count at once."),
                Row(Action(Plain("Check now"), CheckBotUpdatesNowAsync), Action(Plain("Open the bots folder"), () => {
                    Directory.CreateDirectory(BotsFolder);

                    return Open(BotsFolder);
                }))),
            Fold("list", ULTIMUS_TAB, "Published bot updates",
                "Each bot update with what it changed, newest first. These notes stay here instead of Ahziael's own What's New.",
                _botUpdList),
            Shut(Fold("log", ULTIMUS_TAB, "What the server did",
                "The last things the server did with bot updates (downloads, swaps, and why one was skipped).",
                _botUpdLog)));
    }

    private void RefreshBotUpdates() {
        _showingBotUpd = true;
        try {
            _botUpdLive.IsChecked = !(_manager.GetSetting("Bots", "LiveUpdates") is { } live && live.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
            _botUpdWarn.IsChecked = !(_manager.GetSetting("Bots", "UpdateWarning") is { } warn && warn.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
            if (!_botUpdWarnMinutes.IsKeyboardFocusWithin
                && decimal.TryParse(_manager.GetSetting("Bots", "UpdateWarningMinutes") ?? "2", NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) {
                _botUpdWarnMinutes.Value = Math.Clamp(minutes, 0.5m, 30);
            }

            _botUpdWarnMinutes.IsEnabled = _botUpdWarn.IsChecked == true;
        }
        finally {
            _showingBotUpd = false;
        }

        JsonNode? state = null;
        try {
            var path = Path.Combine(BotsFolder, "state.json");
            state = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            // Being written right now: read again in 10 seconds.
            return;
        }

        var serverOn = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var running = (string?) state?["running"] ?? "";
        var problem = (string?) state?["problem"] ?? "";
        var checkedAt = (string?) state?["lastCheck"] ?? "";
        var result = (string?) state?["lastCheckResult"] ?? "";
        _botUpdState.Text = state is null
            ? "The server has not said which bots it runs yet. Start the server (with this version of Ahziael) and this fills in."
            : (!serverOn ? "The server is not running. Last time: " : "") +
              (running.Length == 0 ? "no bots are running." : running == "built in" ? "the bots that came with this Ahziael are running." : $"bot update {BotNumber(running)} is running.") +
              (checkedAt.Length > 0 ? $" Last looked for a bot update at {checkedAt}{(result.Length > 0 ? $" ({result})" : "")}." : "") +
              (problem.Length > 0 ? $" Problem: {problem}." : "");

        _botUpdList.Children.Clear();
        var updates = state?["updates"]?.AsArray() ?? [];
        if (updates.Count == 0) {
            _botUpdList.Children.Add(new TextBlock { Text = "No bot update has been published yet.", Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var update in updates) {
            var tag = (string?) update?["tag"] ?? "";
            var when = DateTime.TryParse((string?) update?["published"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture) : "";
            var notes = ((string?) update?["notes"] ?? "").ReplaceLineEndings("\n").Split('\n')
                .Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => "• " + l[2..]).ToList();
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(new TextBlock {
                Text = $"Bot update {BotNumber(tag)}{(when.Length > 0 ? $" ({when})" : "")}{(tag == running ? ", running now" : "")}",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            foreach (var note in notes.DefaultIfEmpty("• Bot fixes and improvements.")) {
                box.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
            }

            _botUpdList.Children.Add(box);
        }

        _botUpdLog.Children.Clear();
        foreach (var line in (state?["log"]?.AsArray() ?? []).Reverse()) {
            _botUpdLog.Children.Add(new TextBlock { Text = (string?) line ?? "", Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
        }

        static string BotNumber(string tag) => tag.StartsWith("bots-v", StringComparison.OrdinalIgnoreCase) ? tag[6..] : tag;
    }

    private Task SetLiveBotUpdatesAsync(bool on) {
        _manager.SaveSettings([new Setting("Bots", "LiveUpdates", on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? "Live bot updates are on. The server takes a new bot update by itself within 10 minutes."
            : "Live bot updates are off. The bots stay as they are until you turn this back on or update Ahziael.");

        return Task.CompletedTask;
    }

    private void SaveBotWarning() {
        var on = _botUpdWarn.IsChecked == true;
        var minutes = Math.Clamp(_botUpdWarnMinutes.Value ?? 2, 0.5m, 30);
        _manager.SaveSettings([
            new Setting("Bots", "UpdateWarning", on ? "true" : "false", ""),
            new Setting("Bots", "UpdateWarningMinutes", minutes.ToString("0.#", CultureInfo.InvariantCulture), ""),
        ]);
        _settings = [];
        _botUpdWarnMinutes.IsEnabled = on;
        Say(on ? $"Players are warned {minutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes before the bots go down for an update."
            : "No warning before bot updates: the bots go down for an update at once.");
    }

    private Task CheckBotUpdatesNowAsync() {
        if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            Say("Start the server first: it is the server that looks for bot updates.");

            return Task.CompletedTask;
        }

        Directory.CreateDirectory(BotsFolder);
        File.WriteAllText(Path.Combine(BotsFolder, "check-now.txt"), DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        Say("The server looks for a bot update within a few seconds. If there is a newer one, the bots log out and come back with it.");

        return Task.CompletedTask;
    }

}
