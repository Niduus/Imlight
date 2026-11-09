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
        var manager = new ServerManager(installFolder);
        if (!manager.IsInstalled) {
            Console.WriteLine($"Imlight is not installed in {installFolder} yet. Run {Edition.RELEASE_ASSET} first.");

            return 1;
        }

        s_manager = manager;
        AppLogs.Start(installFolder);
        var exitCode = 0;
        var crashed = false;

        // The window needs a thread of its own that Windows treats as a UI thread.
        var ui = new Thread(() => {
            try {
                BuildApp().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception ex) {
                AppLogs.Error($"The {Edition.NAME} window stopped", ex);
                Console.WriteLine($"The {Edition.NAME} window could not be opened: {ex.Message}");
                exitCode = 1;
                crashed = true;
            }
        });
        if (OperatingSystem.IsWindows()) {
            ui.SetApartmentState(ApartmentState.STA);
        }

        ui.Start();
        ui.Join();
        RemoteSession.Deactivate();
        // A window that stopped because of an error leaves the server running: players stay in the game, and opening
        // Ahziael again shows it (a tester's server stopped each time Ahziael did, 2026-11-09).
        if (!KeepServerRunning && !crashed) {
            manager.StopServer();
        }

        return exitCode;
    }

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

    public override void OnFrameworkInitializationCompleted() {
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
