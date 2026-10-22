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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Aurorium card on the Tools tab: install Aurorium into any folder, run it so it downloads the game files, and have
/// the server take its files instead of the ones in the player's Wizard101 folder.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly TextBox _auroriumPath = new() { Watermark = "The folder to put Aurorium in" };
    private readonly TextBlock _auroriumNow = Sub("");
    private readonly Button _auroriumInstall = Primary("Install Aurorium");
    private readonly Button _auroriumStart = Plain("Start Aurorium");
    private readonly Button _auroriumStop = Plain("Stop Aurorium");
    private readonly ToggleSwitch _auroriumUse = new() { OnContent = "On", OffContent = "Off" };
    private bool _showingAurorium;
    private bool _installingAurorium;

    private Control AuroriumCard() {
        var settings = Aurorium.Load(_manager.InstallFolder);
        _auroriumPath.Text = settings.Folder ?? Aurorium.DefaultFolder(_manager.InstallFolder);
        _auroriumUse.IsChecked = settings.UseForServer;
        _auroriumPath.TextChanged += (_, _) => ShowAurorium();
        _auroriumUse.IsCheckedChanged += async (_, _) => {
            if (!_showingAurorium) {
                await Guard(SetAuroriumUseAsync);
            }
        };
        Action(_auroriumInstall, InstallAuroriumAsync);
        Action(_auroriumStart, StartAuroriumAsync);
        Action(_auroriumStop, StopAuroriumAsync);

        // While Tools is open, how far Aurorium has got with its downloads.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => {
            if (_auroriumNow.IsEffectivelyVisible && !_installingAurorium) {
                ShowAurorium();
            }
        };
        timer.Start();
        ShowAurorium();

        return Shut(Fold("download", "Server", "Aurorium (game files)",
            "Aurorium is a free program that downloads all of Wizard101's game files by itself. Your server can use them " +
            "instead of the ones in your own Wizard101 folder.",
            _auroriumNow,
            WithHint(new DockPanel {
                Children = {
                    WithDock(WithMargin(Action(Plain("Choose folder…"), ChooseAuroriumFolderAsync), new Thickness(8, 0, 0, 0)), Dock.Right),
                    _auroriumPath,
                },
            }, "Aurorium can go in any folder, on any drive. Its game files are big (several GB), so pick a drive with room."),
            WithHint(Row(_auroriumInstall, _auroriumStart, _auroriumStop, Action(Plain("Open folder"), OpenAuroriumFolderAsync)),
                "1. Install Aurorium. 2. Start it: it opens in its own window and downloads the game files. The first time takes " +
                "a long while; leave it open until it stops downloading. 3. Turn on the switch below. You can close Aurorium " +
                "after that; start it again now and then to get new game files after a Wizard101 update."),
            SwitchRow("Server uses Aurorium's files", _auroriumUse,
                "On: the server takes its game files from Aurorium, and from your Wizard101 folder for any Aurorium doesn't " +
                "have yet. Off: only from your Wizard101 folder. Applies the next time the server starts.")));
    }

    private string AuroriumFolder() {
        var folder = (_auroriumPath.Text ?? "").Trim().Trim('"');

        return folder.Length == 0 ? Aurorium.DefaultFolder(_manager.InstallFolder) : Path.GetFullPath(folder);
    }

    /// <summary>Whether Aurorium is in the folder, running, and how many game files it has (looked up away from the window).</summary>
    private async void ShowAurorium() {
        string folder;
        try {
            folder = AuroriumFolder();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            _auroriumNow.Text = "That isn't a folder path.";

            return;
        }

        var installFolder = _manager.InstallFolder;
        var (installed, running, files, bytes, use) = await Task.Run(() => {
            try {
                var installed = Aurorium.FindExe(folder) is not null;
                var running = installed && Aurorium.IsRunning(folder);
                var (files, bytes, _) = installed ? Aurorium.CountFiles(folder) : (0, 0L, 0);

                return (installed, running, files, bytes, Aurorium.Load(installFolder).UseForServer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return (false, false, 0, 0L, false);
            }
        });
        if (folder != AuroriumFolder()) {
            // The box changed meanwhile; that look-up shows it.
            return;
        }

        _auroriumInstall.Content = installed ? "Update Aurorium" : "Install Aurorium";
        _auroriumStart.IsEnabled = installed && !running;
        _auroriumStop.IsEnabled = running;
        var have = files == 0 ? "no game files yet" : $"{files} game files ({GameFiles.Size(bytes)})";
        _auroriumNow.Text = !installed ? $"Not installed in {folder} yet."
            : running ? $"Running in {folder}. It has {have} so far."
            : $"Installed in {folder}, not running. It has {have}.";

        _showingAurorium = true;
        _auroriumUse.IsChecked = use;
        _showingAurorium = false;
    }

    private async Task ChooseAuroriumFolderAsync() {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Pick a folder for Aurorium", AllowMultiple = false,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) {
            _auroriumPath.Text = path;
            SaveAuroriumFolder();
        }
    }

    // The folder in the box, kept with the switch as it is.
    private void SaveAuroriumFolder() {
        var settings = Aurorium.Load(_manager.InstallFolder);
        Aurorium.Save(_manager.InstallFolder, settings with { Folder = AuroriumFolder() });
    }

    private async Task InstallAuroriumAsync() {
        var folder = AuroriumFolder();
        if (await Task.Run(() => Aurorium.IsRunning(folder))) {
            throw new PanelException("Aurorium is running. Click Stop Aurorium first, then update it.");
        }

        var progress = new Progress<int>(p => _auroriumNow.Text = p >= 0 ? $"Downloading Aurorium… {p}%" : "Downloading Aurorium…");
        string version;
        _installingAurorium = true;
        try {
            version = await Task.Run(() => Aurorium.InstallAsync(folder, progress));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or System.Text.Json.JsonException) {
            ShowAurorium();
            throw new PanelException($"Aurorium could not be downloaded: {ex.Message} Check your internet connection and try again, " +
                $"or get it yourself from {Aurorium.RELEASES_PAGE} and put it in {folder}.");
        }
        catch (InvalidOperationException ex) {
            ShowAurorium();
            throw new PanelException(ex.Message);
        }
        finally {
            _installingAurorium = false;
        }

        SaveAuroriumFolder();
        ShowAurorium();
        if (await ConfirmAsync($"Aurorium {version} is installed in {folder}. Start it now? It opens in its own window and " +
                "downloads all of Wizard101's game files, which takes a long while the first time. Leave it open until it finishes.")) {
            await StartAuroriumAsync();

            return;
        }

        Say($"Aurorium {version} is installed in {folder}. Click Start Aurorium when you want it to download the game files.");
    }

    private Task StartAuroriumAsync() {
        var folder = AuroriumFolder();
        if (Aurorium.FindExe(folder) is null) {
            throw new PanelException($"Aurorium isn't in {folder}. Click Install Aurorium first.");
        }

        if (Aurorium.IsRunning(folder)) {
            Say("Aurorium is already running.");

            return Task.CompletedTask;
        }

        Aurorium.Start(folder);
        SaveAuroriumFolder();
        Say("Aurorium opened in its own window and is getting the game files. Leave it open until it stops downloading. " +
            "Then turn on \"Server uses Aurorium's files\" if it isn't on yet.");
        DispatcherTimer.RunOnce(ShowAurorium, TimeSpan.FromSeconds(2));

        return Task.CompletedTask;
    }

    private async Task StopAuroriumAsync() {
        var folder = AuroriumFolder();
        var closed = await Task.Run(() => Aurorium.Stop(folder));
        ShowAurorium();
        Say(closed > 0 ? "Aurorium is closed. The game files it already got are kept." : "Aurorium wasn't running.");
    }

    private Task OpenAuroriumFolderAsync() {
        var folder = AuroriumFolder();
        Directory.CreateDirectory(folder);

        return Open(folder);
    }

    private async Task SetAuroriumUseAsync() {
        var on = _auroriumUse.IsChecked == true;
        var folder = AuroriumFolder();
        Aurorium.Save(_manager.InstallFolder, new Aurorium.Settings(folder, on));
        var (files, _, _) = Aurorium.CountFiles(folder);
        var note = !on ? "The server takes its game files from your Wizard101 folder again."
            : files == 0 ? "The server will take Aurorium's game files, but Aurorium has none yet, so it uses your Wizard101 folder " +
                "until Aurorium has downloaded them. Install and start Aurorium above."
            : "The server now takes its game files from Aurorium, and from your Wizard101 folder for any Aurorium doesn't have.";
        Say(note);
        if (await Task.Run(() => InstalledCommands.IsServerRunning(_manager.InstallFolder))
                && await ConfirmAsync(note + " Restart the server now to use it? Everyone playing is disconnected.")) {
            Say(await _manager.RestartServerAsync());
        }
    }

}
