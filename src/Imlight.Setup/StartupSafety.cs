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
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Safety nets for when Ahziael cannot open (Niduus, 2026-11-12, after a player's Ahziael "appears in my taskbar for a
/// split second and then closes"; versions 0.10.793 and 0.10.794 did that for everyone). Instead of vanishing, Ahziael
/// writes down why, says so in a plain Windows message box, and offers the newest version or a zip of the logs to send.
/// </summary>
internal static class StartupSafety {

    private const uint MB_OK = 0x0;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;
    private const int IDYES = 6;

    /// <summary>
    /// Ahziael could not open (or its window stopped): written down, then a message box with the reason. When a newer
    /// version is out it offers to update (a version that can't open can't update itself from its window); otherwise it
    /// offers a zip of the logs on the desktop.
    /// </summary>
    internal static void CouldNotOpen(string installFolder, Exception ex, bool windowWasOpen) {
        var what = windowWasOpen ? $"The {Edition.NAME} window stopped" : $"{Edition.NAME} could not open";
        AppLogs.Error(what, ex, kind: AppLogs.CRASH);
        Console.WriteLine($"{what}: {ex.Message}");
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        var logs = AppLogs.Folder(installFolder);
        var newer = Wait(SelfUpdate.NewerReleaseAsync());
        var head = $"{what}.\n\nReason: {ex.Message}\n\nThis was written down in {logs}.";
        if (newer is not null) {
            if (Ask(head + $"\n\nA newer {Edition.NAME} ({newer}) is out and may fix this. Click Yes to update now " +
                    "(your settings and characters are kept), or No to close.", MB_YESNO | MB_ICONERROR)) {
                UpdateNow(installFolder);
            }

            return;
        }

        if (Ask(head + "\n\nClick Yes to put the logs in a zip on your desktop, ready to send in, or No to close.", MB_YESNO | MB_ICONERROR)) {
            MakeZip(installFolder);
        }
    }

    /// <summary>
    /// The install's server settings (Imlight.ini) are missing, so Ahziael has nothing to show: say so and how to repair
    /// it, instead of a console line that vanishes with its window.
    /// </summary>
    internal static void NotInstalled(string installFolder, string configFile) {
        var text = $"{Edition.NAME} could not find its server settings, so it can't open.\n\nIt looked for:\n{configFile}\n\n" +
            $"To repair it, download {Edition.RELEASE_ASSET} again from the newest release and run it. " +
            "It sets Imlight up again and keeps your characters.";
        AppLogs.Error($"{Edition.NAME} could not open: its server settings (Imlight.ini) are missing", null,
            $"Looked for {configFile}", AppLogs.REFUSED);
        Console.WriteLine(text);
        if (OperatingSystem.IsWindows()) {
            Ask(text, MB_OK | MB_ICONWARNING);
        }
    }

    private static void UpdateNow(string installFolder) {
        try {
            var download = Wait(SelfUpdate.DownloadNewerAsync(Path.Combine(Path.GetTempPath(), "Ahziael update")));
            if (download is not { } got) {
                Ask("The newer version could not be downloaded. Check your internet connection, or download " +
                    $"{Edition.RELEASE_ASSET} from the newest release on GitHub and run it.", MB_OK | MB_ICONWARNING);

                return;
            }

            // Its own window, so its questions and progress show (this program's window may be hidden).
            Process.Start(new ProcessStartInfo(got.Path) {
                UseShellExecute = true,
                ArgumentList = { "--update", "--dir", installFolder },
            });
        }
        catch (Exception ex) {
            AppLogs.Error("Updating after Ahziael could not open failed", ex);
            Ask($"The update could not start ({ex.Message}). Download {Edition.RELEASE_ASSET} from the newest release on GitHub and run it.",
                MB_OK | MB_ICONWARNING);
        }
    }

    private static void MakeZip(string installFolder) {
        try {
            var zip = AppLogs.MakeZip(installFolder, Path.Combine(InstalledCommands.ServerFolder(installFolder), "logs"));
            Ask($"Saved \"{Path.GetFileName(zip)}\" on your desktop. Drag it into the chat to send it.", MB_OK);
        }
        catch (Exception ex) {
            Ask($"The zip could not be made ({ex.Message}). The logs are in {AppLogs.Folder(installFolder)}.", MB_OK | MB_ICONWARNING);
        }
    }

    private static T? Wait<T>(Task<T> task) {
        try {
            return task.Wait(TimeSpan.FromSeconds(60)) ? task.Result : default;
        }
        catch (Exception) {
            return default;
        }
    }

    private static bool Ask(string text, uint buttons) {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            return MessageBoxW(IntPtr.Zero, text, Edition.NAME, buttons | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;
        }
        catch (Exception) {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

}
