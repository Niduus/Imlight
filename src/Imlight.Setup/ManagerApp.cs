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
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Imlight.Setup;

/// <summary>
/// The app the installed Imlight.exe opens. It is a window drawn by this program itself (Avalonia),
/// so it needs no web browser, no web view and no account of any kind.
/// </summary>
internal sealed class ManagerApp : Application {

    private static ServerManager? s_manager;

    /// <summary>True when the app was reopened by an update that stopped the server, so it starts it again.</summary>
    internal static bool StartServerOnOpen { get; private set; }

    /// <summary>
    /// True when the app closes to restart into an update: the server keeps running through it (Niduus, 2026-10-28).
    /// </summary>
    internal static bool KeepServerRunning { get; set; }

    /// <summary>
    /// Shows the app until its window closes, then stops the server it started (unless it closes for an update).
    /// </summary>
    internal static int Run(string installFolder, bool startServer = false) {
        StartServerOnOpen = startServer;
        Console.Title = Edition.NAME;
        // Written down from the very first step, so even a start that fails before the window shows leaves a reason
        // (Niduus, 2026-11-12: a player's Ahziael flashed in the taskbar and closed, with nothing to go on).
        AppLogs.Start(installFolder);
        ServerManager manager;
        try {
            manager = new ServerManager(installFolder);
            if (!manager.IsInstalled) {
                StartupSafety.NotInstalled(installFolder, manager.ConfigFile);

                return 1;
            }
        }
        catch (Exception ex) {
            StartupSafety.CouldNotOpen(installFolder, ex, windowWasOpen: false);

            return 1;
        }

        s_manager = manager;
        // The last start closed before its window showed and before any error could be written: this time the parts
        // that hook into the game wait, and the window says so.
        SafeStart = AppLogs.LastOpenFailed(installFolder);
        if (SafeStart) {
            AppLogs.Error($"Last time {Edition.NAME} closed while it was opening, before it could say why. It opened in safe start this time.",
                null, "Safe start leaves the game overlay off until the next time Ahziael opens.", AppLogs.CRASH);
        }

        AppLogs.Opening(installFolder);
        var exitCode = 0;
        var crashed = false;

        // The window needs a thread of its own that Windows treats as a UI thread.
        var ui = new Thread(() => {
            try {
                BuildApp().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception ex) {
                exitCode = 1;
                crashed = true;
                StartupSafety.CouldNotOpen(installFolder, ex, WindowShown);
            }
        });
        if (OperatingSystem.IsWindows()) {
            ui.SetApartmentState(ApartmentState.STA);
        }

        ui.Start();
        ui.Join();
        // Closed normally, or the reason is written down already: no safe start next time.
        AppLogs.Opened(installFolder);
        RemoteSession.Deactivate();
        // A window that stopped because of an error leaves the server running: players stay in the game, and opening
        // Ahziael again shows it (a tester's server stopped each time Ahziael did, 2026-11-09).
        if (!KeepServerRunning && !crashed) {
            manager.StopServer();
        }

        return exitCode;
    }

    /// <summary>True when the last start closed before its window showed: the game overlay waits until the next start.</summary>
    internal static bool SafeStart { get; private set; }

    /// <summary>True once Ahziael's window has shown, so a later error is "the window stopped", not "could not open".</summary>
    internal static bool WindowShown { get; set; }

    internal static AppBuilder BuildApp()
        => AppBuilder.Configure<ManagerApp>().UsePlatformDetect().WithInterFont().AfterSetup(_ => KeepWindowOnErrors());

    /// <summary>
    /// An error inside the window (a button, a list, a page) is written to Logs\Ahziael and shown in a line, and the
    /// window stays open: before, any mistake in a page closed the whole app and stopped the server with it.
    /// </summary>
    private static void KeepWindowOnErrors() {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => {
            AppLogs.Error("An error in the window (Ahziael kept running)", e.Exception);
            e.Handled = true;
            ManagerWindow.ShowError(e.Exception);
        };
    }

    /// <summary>For tests, which show the window themselves.</summary>
    internal static void UseManager(ServerManager manager) => s_manager = manager;

    public override void Initialize() {
        Styles.Add(new FluentTheme());
        // Always dark, whatever Windows is set to.
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = Edition.NAME;
    }

    private static string? s_overlayOnly;

    /// <summary>The app shows only the game overlay, with no Ahziael window (Ahziael Overlay, --overlay).</summary>
    internal static void OverlayOnly(string installFolder) => s_overlayOnly = installFolder;

    public override void OnFrameworkInitializationCompleted() {
        if (s_overlayOnly is not null) {
            OverlayStandalone.Begin(this, s_overlayOnly);
            HideConsole();
            base.OnFrameworkInitializationCompleted();

            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && s_manager is not null) {
            desktop.MainWindow = new ManagerWindow(s_manager);
            HideConsole();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Hides the console window behind the app, so the app is the only window.
    /// </summary>
    private static void HideConsole() {
        if (OperatingSystem.IsWindows() && GetConsoleWindow() is var handle && handle != IntPtr.Zero) {
            ShowWindow(handle, 0);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

}
