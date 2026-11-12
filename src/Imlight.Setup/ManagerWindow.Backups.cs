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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Server > Ahziael > Backups (Niduus, 2026-11-12: "Implement features that preserve a servers custom data to avoid a
/// total wipe"): the backups in Documents\Ahziael backups, a Back up now button, and Restore beside each one.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int BACKUPS_SHOWN = 20;

    private readonly StackPanel _backupRows = new() { Spacing = 6 };
    private readonly TextBlock _backupSummary = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private bool _backingUp;

    private Control BackupsCard() {
        _ = ShowBackupsAsync();

        return Fold("shield", "Customs", "Backups",
            "Everything you made on this server, saved where an uninstall or a broken install can't reach it.",
            Sub($"A backup is made by itself each day Ahziael opens, before every update and before an uninstall, in " +
                $"{ServerBackup.Folder(_manager.InstallFolder)}. It holds your settings, customs, areas, cheats, drop tables, Crown Shop, " +
                "events and codes, your overlay designs and add-ons, and Ahziael's own settings. Accounts and characters go in too " +
                "whenever the server is stopped (a database in use can't be copied safely). Installing Ahziael again offers the " +
                "newest backup back. The newest 7 daily and 7 before-update backups are kept; the rest stay until you delete them."),
            _backupSummary,
            _backupRows,
            Row(Action(Primary("Back up now"), BackUpNowAsync),
                Action(Plain("Open backups folder"), () => {
                    Directory.CreateDirectory(ServerBackup.Folder(_manager.InstallFolder));

                    return Open(ServerBackup.Folder(_manager.InstallFolder));
                })));
    }

    /// <summary>Today's backup, once, in the background when Ahziael opens (on this PC's own server).</summary>
    private void DailyBackup() {
        if (IsRemote || !_manager.IsInstalled) {
            return;
        }

        var install = _manager.InstallFolder;
        _ = Task.Run(() => {
            try {
                if (ServerBackup.MakeIfDue(install) is not null) {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowBackupsAsync());
                }
            }
            catch (Exception ex) {
                AppLogs.Error("Today's backup could not be made", ex);
            }
        });
    }

    private async Task ShowBackupsAsync() {
        var install = _manager.InstallFolder;
        var backups = await Task.Run(() => ServerBackup.List(install));
        _backupSummary.Text = backups.Count == 0
            ? "No backups yet. Click Back up now to make the first one."
            : $"{backups.Count} {(backups.Count == 1 ? "backup" : "backups")}, the newest from {backups[0].Made:yyyy-MM-dd HH:mm}." +
              (backups.Any(b => b.HasDatabase) ? "" : " None has the characters yet: stop the server and click Back up now.");
        _backupRows.Children.Clear();
        foreach (var backup in backups.Take(BACKUPS_SHOWN)) {
            _backupRows.Children.Add(BackupRow(backup));
        }
    }

    private Control BackupRow(ServerBackup.Backup backup) {
        var text = new StackPanel {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = {
                new TextBlock { Text = $"{backup.Made:yyyy-MM-dd HH:mm}  ·  {backup.Kind}", TextWrapping = TextWrapping.Wrap },
                Sub((backup.HasDatabase ? "With accounts and characters" : "Without characters (the server was running)") +
                    $"  ·  {Size(backup.Bytes)}"),
            },
        };
        var buttons = Row(Action(Plain("Restore"), () => RestoreBackupAsync(backup)),
            Action(Plain("Show"), () => {
                ShowInFolder(backup.Path);

                return Task.CompletedTask;
            }));

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
            Child = new DockPanel { Children = { WithDock(WithMargin(buttons, new Thickness(12, 0, 0, 0)), Dock.Right), text } },
        };

        static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }

    private async Task BackUpNowAsync() {
        if (_backingUp) {
            Say("A backup is being made already. It shows in the list when it is done.");

            return;
        }

        _backingUp = true;
        try {
            var install = _manager.InstallFolder;
            var running = _manager.Status().Running;
            Say("Making a backup…");
            var file = await Task.Run(() => ServerBackup.Make(install, ServerBackup.BY_HAND));
            await ShowBackupsAsync();
            Say($"Saved \"{Path.GetFileName(file)}\"." + (running
                ? " The server is running, so the accounts and characters are not in it: stop the server and click Back up now again to have them."
                : ""));
        }
        finally {
            _backingUp = false;
        }
    }

    private async Task RestoreBackupAsync(ServerBackup.Backup backup) {
        if (_manager.Status().Running) {
            throw new PanelException("Stop the server first (Server > Server > Stop server), then click Restore again.");
        }

        if (!await ConfirmAsync($"Bring back the backup from {backup.Made:yyyy-MM-dd HH:mm}? Your settings, customs, overlay and add-ons " +
                (backup.HasDatabase ? "and every account and character " : "") +
                "go back to how they were then. A backup of how things are now is made first, so you can go back.")) {
            return;
        }

        var install = _manager.InstallFolder;
        Say("Restoring…");
        var count = await Task.Run(() => ServerBackup.Restore(install, backup.Path));
        await ShowBackupsAsync();
        Say($"Brought back {count} files. Close and open Ahziael again so every page shows them, then start the server.");
    }

    private static void ShowInFolder(string file) {
        if (OperatingSystem.IsWindows()) {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file }, UseShellExecute = false })?.Dispose();
        }
        else {
            ServerManager.Open(Path.GetDirectoryName(file)!);
        }
    }

}
