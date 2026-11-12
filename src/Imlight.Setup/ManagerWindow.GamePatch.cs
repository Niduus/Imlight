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
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Builds the server again by itself when Wizard101 patches its messages (GamePatch), so a game update such as the
/// Ahmarra one (2026-11-12) keeps working without the player doing anything: the app notices within a few minutes of
/// the patch, builds in the background while the old server keeps running, then restarts into it the way an update does.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly DispatcherTimer _gamePatchTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private bool _gamePatchAsked;

    private void StartGamePatchCheck() {
        _gamePatchTimer.Tick += (_, _) => _ = Guard(CheckGamePatchAsync);
        _gamePatchTimer.Start();
        Closed += (_, _) => _gamePatchTimer.Stop();
    }

    /// <summary>
    /// Whether Wizard101 changed its messages since the server was built. Read off the window's thread; false when it
    /// can't be told (a server built elsewhere, one from before this check, or the game not found).
    /// </summary>
    private async Task<bool> GameIsNewerThanServerAsync() {
        if (RemoteSession.Current is not null || _manager.Source.BuiltFolder is not null) {
            return false;
        }

        var installFolder = _manager.InstallFolder;
        var serverFolder = _manager.ServerFolder;

        return await Task.Run(() => GameLocator.FindAndRemember(installFolder) is { } rootWad
            && GamePatch.ServerIsBehind(serverFolder, rootWad));
    }

    private async Task CheckGamePatchAsync() {
        if (_preparingUpdate || _readyUpdate is not null || _gamePatchAsked || !await GameIsNewerThanServerAsync()) {
            return;
        }

        // Once per time the app is open: a build that fails says why, and Update Ahziael builds it again.
        _gamePatchAsked = true;
        if (!_manager.AutoUpdate && !await ConfirmAsync("Wizard101 was updated, and the server has to be built again to understand " +
                "the new game version. Build it now? It builds in the background while the server keeps running, then Ahziael " +
                "restarts into it. Until then the updated game may not connect.")) {
            Say("The server still needs building for the updated Wizard101. Click Update Ahziael at the top of the window when you're ready.", true);

            return;
        }

        var exe = Path.Combine(_manager.InstallFolder, InstalledCommands.INSTALLED_EXE);
        if (!File.Exists(exe)) {
            exe = Environment.ProcessPath ?? throw new PanelException($"{exe} is missing. Run {Edition.RELEASE_ASSET} again to repair Imlight.");
        }

        _preparingUpdate = true;
        try {
            const string NAME = "The server for the updated Wizard101";
            var whenReady = _manager.AutoInstallUpdates ? "Ahziael restarts into it by itself when it is ready" : "Ahziael asks to restart when it is ready";
            Say("Wizard101 was updated, so the server is being built again for the new game version. This takes a few minutes; " +
                $"{whenReady}. Wait for it before playing, or the game may not connect.");
            await BuildPendingAsync(exe, NAME);
        }
        finally {
            _preparingUpdate = false;
            _updateButton.Content = _readyUpdate is not null ? "Restart to update" : "Update Ahziael";
        }
    }

}
