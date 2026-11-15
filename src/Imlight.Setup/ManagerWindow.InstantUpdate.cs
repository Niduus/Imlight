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
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Instant Update (Niduus, 2026-11-15: "check the repo every 5 minutes for a update and if one is found, will automatically
/// begin to update without question"). Off by default. While Ahziael is open it looks at the Niduus/Imlight releases every
/// 5 minutes; a newer one downloads and builds in the background and Ahziael restarts into it without asking. The server
/// keeps running: an update that changes the server's program restarts it only after a 2-minute warning in the game,
/// once nobody is in a battle (ManagerWindow.ServerUpdate.cs), the same as "Install updates automatically". Ahziael
/// never restarts by itself over unsaved work or an open question box; it waits and tries again.
/// </summary>
internal sealed partial class ManagerWindow {

    private static readonly TimeSpan s_instantUpdateEvery = TimeSpan.FromMinutes(5);
    // A release that would not download or build is tried again after this, not every 5 minutes with the same error.
    private static readonly TimeSpan s_instantUpdateRetry = TimeSpan.FromMinutes(30);

    private readonly DispatcherTimer _instantUpdateTimer = new() { Interval = s_instantUpdateEvery };
    private bool _instantUpdateStarted;
    private bool _instantChecking;
    private DateTime _instantRetryAfter = DateTime.MinValue;

    /// <summary>Whether a ready update installs without asking: Install updates automatically, or Instant Update.</summary>
    private bool InstallsWithoutAsking => _manager.AutoInstallUpdates || _manager.InstantUpdate;

    private void StartInstantUpdate() {
        if (_instantUpdateStarted) {
            return;
        }

        _instantUpdateStarted = true;
        _instantUpdateTimer.Tick += (_, _) => _ = Guard(InstantUpdateCheckAsync);
        _instantUpdateTimer.Start();
    }

    /// <summary>
    /// Every 5 minutes while Instant Update is on: restarts into an update that is already ready, or gets a newer release
    /// ready (it then installs by itself).
    /// </summary>
    private async Task InstantUpdateCheckAsync() {
        // Never two at once, never on another PC's server, and never while an update or a server restart is under way.
        if (!_manager.InstantUpdate || IsRemote || _instantChecking || _preparingUpdate || _restartingForUpdate) {
            return;
        }

        _instantChecking = true;
        try {
            if (_readyUpdate is not null) {
                await OfferRestartAsync(withoutAsking: true);

                return;
            }

            if (DateTime.Now < _instantRetryAfter || await Task.Run(() => UpdateLock.IsHeld(_manager.InstallFolder))) {
                return;
            }

            if (await SelfUpdate.NewerReleaseAsync() is not { } newer) {
                return;
            }

            _newerRelease = newer;
            RefreshUpdatePanel();
            await PrepareUpdateAsync();
            if (_readyUpdate is null) {
                // Did not download or build (the reason was already said): try this one again later, not every 5 minutes.
                _instantRetryAfter = DateTime.Now + s_instantUpdateRetry;
            }
        }
        finally {
            _instantChecking = false;
        }
    }

    /// <summary>Why Ahziael should not restart by itself right now, in plain words, or null when it can.</summary>
    private string? WhyRestartWaits() {
        if (_pending.Count > 0) {
            return "the Settings tab has changes that aren't saved";
        }

        if (_areaEdits.Values.Any(edit => edit.Dirty)) {
            return "a custom area has changes that aren't saved";
        }

        if (OwnedWindows.Count > 0) {
            return "a question box is open";
        }

        return null;
    }

    private Control InstantUpdateSwitch() {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = _manager.InstantUpdate };
        toggle.IsCheckedChanged += (_, _) => {
            var on = toggle.IsChecked == true;
            SaveUpdateChoice(() => _manager.InstantUpdate = on, on
                ? "Instant Update is on: Ahziael looks for a new version every 5 minutes and installs it without asking. " +
                  "The server keeps running; if an update changes the server's program, it restarts after a 2-minute warning " +
                  "in the game, once nobody is in a battle. This is saved on this PC."
                : "Instant Update is off. Ahziael no longer looks for new versions every 5 minutes.");
            if (on) {
                // Look straight away rather than in 5 minutes.
                _instantRetryAfter = DateTime.MinValue;
                _ = Guard(InstantUpdateCheckAsync);
            }
        };

        var row = SwitchRow("Instant Update", toggle,
            "Looks for a new version every 5 minutes while Ahziael is open and installs it right away, without asking. Off by default.");
        ToolTip.SetTip(row, "Ahziael restarts by itself into each new version (a few seconds; the game overlay blinks off and back). " +
            "The server keeps running. When an update changes the server's program, the server restarts after a 2-minute warning " +
            "in the game, once nobody is in a battle, so anyone playing is disconnected for a moment. It waits while you have " +
            "unsaved changes or a question box open.");

        return row;
    }
}
