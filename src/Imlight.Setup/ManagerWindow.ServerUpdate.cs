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
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Imlight.Setup;

/// <summary>
/// An update that changed the server's program waits while the server runs (Niduus, 2026-10-28: "without disconnecting
/// the server unless we absolutely have to"). Ahziael restarts the server for it when it is safe: after a countdown in
/// the game once nobody is in a battle, when nobody is playing, right away, or the next time the server starts. Updates
/// that only change Ahziael or the server's game data go in without a restart (Program.ApplyUpdateAsync).
/// </summary>
internal sealed partial class ManagerWindow {

    private enum ServerRestartPlan { None, AfterCountdown, WhenNobodyPlays }

    private static readonly TimeSpan s_restartCountdown = TimeSpan.FromMinutes(2);
    // When the countdown tells everyone playing how long is left.
    private static readonly int[] s_countdownWarnings = [60, 30, 10];

    private ServerRestartPlan _restartPlan;
    private DateTime _restartAt;
    private int _nextWarning;
    private bool _toldWaitingForBattles;
    private int _emptyChecks;
    private bool _restartingForUpdate;
    private bool _restartOffered;

    /// <summary>Whether an update's server waits for the running server to restart.</summary>
    private bool ServerUpdateWaits(bool running) => running && SetupSteps.ServerWaits(_manager.InstallFolder);

    /// <summary>When the app opens with a server update waiting: plans the restart, or asks how to do it.</summary>
    private async Task OfferServerRestartAsync() {
        if (_restartOffered || !ServerUpdateWaits(await Task.Run(() => InstalledCommands.IsServerRunning(_manager.InstallFolder)))) {
            return;
        }

        _restartOffered = true;
        if (_manager.AutoInstallUpdates) {
            PlanServerRestart(ServerRestartPlan.AfterCountdown);

            return;
        }

        await ChooseServerRestartAsync();
    }

    /// <summary>Asks when to restart the server for the waiting update.</summary>
    private async Task ChooseServerRestartAsync() {
        var choice = await ChooseAsync(
            "This update changed the server's program, so the server has to restart once to use it. Everything else is " +
            "already in. When should it restart? Players' games try to reconnect by themselves after it.",
            "After a 2-minute warning, once nobody is in a battle (best)",
            "When nobody is playing",
            "Right now",
            "Not now: the next time I start the server");
        switch (choice) {
            case 0:
                PlanServerRestart(ServerRestartPlan.AfterCountdown);
                break;
            case 1:
                PlanServerRestart(ServerRestartPlan.WhenNobodyPlays);
                break;
            case 2:
                await RestartForUpdateAsync();
                break;
            default:
                _restartPlan = ServerRestartPlan.None;
                Say("The server update waits. It goes in the next time the server starts or restarts, or click " +
                    "Restart server to update at the top of the window.");
                ShowServerUpdateButton();
                break;
        }
    }

    private void PlanServerRestart(ServerRestartPlan plan) {
        _restartPlan = plan;
        _emptyChecks = 0;
        _toldWaitingForBattles = false;
        if (plan == ServerRestartPlan.AfterCountdown) {
            _restartAt = DateTime.Now + s_restartCountdown;
            _nextWarning = 0;
            TryAnnounce($"The server restarts for an update in {(int) s_restartCountdown.TotalMinutes} minutes. " +
                "It waits for battles to end. Your game should reconnect by itself; if it doesn't, log in again.");
            Say($"The server restarts for the update in {(int) s_restartCountdown.TotalMinutes} minutes, once nobody is in a battle. " +
                "Everyone playing was told in the game. Click the button at the top to change this.");
        }
        else {
            Say("The server restarts for the update as soon as nobody is playing. Click the button at the top to change this.");
        }

        ShowServerUpdateButton();
    }

    /// <summary>With every status check (2 s): moves the planned restart along.</summary>
    private void TickServerRestart(bool running) {
        if (_restartingForUpdate) {
            return;
        }

        if (!ServerUpdateWaits(running)) {
            if (_restartPlan != ServerRestartPlan.None || _updateButton.Content as string == SERVER_UPDATE_BUTTON) {
                // The server was stopped or restarted some other way, which put the update in.
                _restartPlan = ServerRestartPlan.None;
                ShowServerUpdateButton();
            }

            return;
        }

        var (players, inBattle) = ReadPlayersAndBattles();
        switch (_restartPlan) {
            case ServerRestartPlan.WhenNobodyPlays:
                // Two checks in a row, so someone changing zones (gone from the list for a moment) isn't missed.
                _emptyChecks = players == 0 ? _emptyChecks + 1 : 0;
                if (_emptyChecks >= 2) {
                    _ = Guard(RestartForUpdateAsync);
                }

                break;

            case ServerRestartPlan.AfterCountdown:
                var left = (int) Math.Ceiling((_restartAt - DateTime.Now).TotalSeconds);
                if (_nextWarning < s_countdownWarnings.Length && left <= s_countdownWarnings[_nextWarning] && left > 0) {
                    var seconds = s_countdownWarnings[_nextWarning++];
                    TryAnnounce(seconds >= 60
                        ? "The server restarts for an update in 1 minute."
                        : $"The server restarts for an update in {seconds} seconds.");
                }

                if (left > 0) {
                    _updateButton.Content = $"Server restarts in {left / 60}:{left % 60:00}";
                }
                else if (inBattle > 0) {
                    _updateButton.Content = "Waiting for battles to end";
                    if (!_toldWaitingForBattles) {
                        _toldWaitingForBattles = true;
                        TryAnnounce("The server restarts for an update as soon as the battles going on now are over.");
                    }
                }
                else {
                    _ = Guard(RestartForUpdateAsync);
                }

                break;
        }
    }

    /// <summary>Restarts the server; starting it puts the waiting update in first (InstalledCommands.StartAsync).</summary>
    private async Task RestartForUpdateAsync() {
        if (_restartingForUpdate) {
            return;
        }

        _restartingForUpdate = true;
        _restartPlan = ServerRestartPlan.None;
        try {
            _updateButton.Content = "Restarting the server...";
            Say("The server is restarting for the update. It is back in about a minute.");
            await Task.Run(() => _manager.StopServer("The server is restarting for an update now. Your game should reconnect " +
                "by itself in a minute or two; if it doesn't, log in again."));
            await Task.Delay(1500);
            Say("The server update is in. " + _manager.StartServer());
        }
        finally {
            _restartingForUpdate = false;
            ShowServerUpdateButton();
        }
    }

    private const string SERVER_UPDATE_BUTTON = "Restart server to update";

    /// <summary>The update button says a server update waits, unless an Ahziael update is on its way.</summary>
    private void ShowServerUpdateButton() {
        if (_readyUpdate is not null || _preparingUpdate) {
            return;
        }

        var waits = SetupSteps.ServerWaits(_manager.InstallFolder);
        _updateButton.Content = waits ? SERVER_UPDATE_BUTTON : "Update Ahziael";
    }

    // Who is online and how many are in a battle, from online-players.json (OnlineList). A server too old to say counts
    // nobody in a battle, and an unreadable list counts as somebody playing.
    private (int Players, int InBattle) ReadPlayersAndBattles() {
        try {
            var file = Path.Combine(_manager.ServerFolder, ONLINE_FILE);
            if (!File.Exists(file)) {
                return (1, 0);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var players = root.TryGetProperty("Players", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 1;
            var inBattle = root.TryGetProperty("InBattle", out var battles) && battles.TryGetInt32(out var count) ? count : 0;

            return (players, inBattle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return (1, 0);
        }
    }

    private void TryAnnounce(string text) {
        try {
            _manager.Announce(text, box: false, chat: true);
        }
        catch (Exception ex) when (ex is PanelException or IOException or UnauthorizedAccessException) {
            // The server stopped meanwhile; nobody is there to tell.
        }
    }

    /// <summary>A question with a button for each answer, one under the other. Null when the window is closed.</summary>
    private async Task<int?> ChooseAsync(string question, params string[] answers) {
        var dialog = Dialog(question, null, out _);
        if (dialog.Content is not StackPanel body) {
            return null;
        }

        // The answers take the place of OK and Cancel.
        body.Children.RemoveAt(body.Children.Count - 1);
        var buttons = new StackPanel { Spacing = 8 };
        for (var i = 0; i < answers.Length; i++) {
            var index = i;
            var button = i == 0 ? Primary(answers[i]) : Plain(answers[i]);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => dialog.Close(index);
            buttons.Children.Add(button);
        }

        body.Children.Add(buttons);
        dialog.Width = 460;
        var picked = await dialog.ShowDialog<object?>(this);

        return picked is int answer ? answer : null;
    }

}
