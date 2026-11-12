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
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// "Ahziael Overlay": the game overlay on its own, without Ahziael's window (Niduus, 2026-11-11: "create a version that is
/// to load WITHOUT and WITH from Ahziael"). Imlight.exe --overlay runs only <see cref="OverlayHost"/>, with a sparkle
/// icon by the clock (open the designer, stop), and the designer opens in a window of its own
/// (<see cref="OverlayDesignerWindow"/>) from that icon or from the sparkle button on the game.
/// </summary>
internal static class OverlayStandalone {

    internal const string NAME = "Ahziael Overlay";

    private static OverlayDesignerWindow? s_designer;

    internal static int Run(string installFolder) {
        AppLogs.Start(installFolder);
        var exitCode = 0;
        var ui = new Thread(() => {
            try {
                ManagerApp.OverlayOnly(installFolder);
                ManagerApp.BuildApp().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception ex) {
                AppLogs.Error($"{NAME} stopped", ex, kind: AppLogs.CRASH);
                exitCode = 1;
            }
        });
        if (OperatingSystem.IsWindows()) {
            ui.SetApartmentState(ApartmentState.STA);
        }

        ui.Start();
        ui.Join();

        return exitCode;
    }

    /// <summary>Called once the app is up: the overlay, its icon by the clock, and the designer on request.</summary>
    internal static void Begin(Application app, string installFolder) {
        OverlayHost.Start(installFolder, standalone: true);
        // The sparkle button on the game: change it right there; "Full designer" opens the designer window.
        OverlayHost.DesignRequested += () => {
            if (!OverlayGameEditor.Open(OpenDesigner)) {
                OpenDesigner();
            }
        };

        var menu = new NativeMenu();
        var design = new NativeMenuItem("Design the overlay");
        design.Click += (_, _) => OpenDesigner();
        var stop = new NativeMenuItem("Stop the overlay");
        stop.Click += (_, _) => Quit();
        menu.Items.Add(design);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(stop);
        var tray = new TrayIcon { ToolTipText = NAME + " " + OverlayHost.Version, Menu = menu, IsVisible = true };
        try {
            using var stream = AssetLoader.Open(new Uri($"avares://{typeof(OverlayStandalone).Assembly.GetName().Name}/Assets/niduus-avatar.png"));
            tray.Icon = new WindowIcon(stream);
        }
        catch (Exception) {
            // No picture: the icon is still there, blank.
        }

        tray.Clicked += (_, _) => OpenDesigner();
        TrayIcon.SetIcons(app, [tray]);
    }

    internal static void OpenDesigner() {
        if (s_designer is null) {
            s_designer = new OverlayDesignerWindow();
            s_designer.Closed += (_, _) => s_designer = null;
            s_designer.Show();
        }

        if (s_designer.WindowState == WindowState.Minimized) {
            s_designer.WindowState = WindowState.Normal;
        }

        s_designer.Activate();
    }

    /// <summary>The installed Imlight.exe, or this program when there is none.</summary>
    internal static string Exe(string installFolder) {
        var installed = Path.Combine(installFolder, InstalledCommands.INSTALLED_EXE);

        return File.Exists(installed) ? installed : Environment.ProcessPath ?? installed;
    }

    internal static void Quit() {
        OverlayHost.Stop();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.Shutdown();
        }
    }

}

/// <summary>The designer of the overlay on its own, in a window of its own.</summary>
internal sealed class OverlayDesignerWindow : Window {

    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _messageBar = new() { Padding = new Thickness(14, 8), IsVisible = false, CornerRadius = new CornerRadius(6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal OverlayDesignerWindow() {
        Title = OverlayStandalone.NAME;
        Width = 1100;
        Height = 820;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ManagerTheme.Apply(this);

        var editor = new ManagerWindow.OverlayEditor(this, Say, () => ManagerWindow.OverlayEditor.ServerFileIn(OverlayHost.InstallFolder));
        _messageBar.Child = _message;
        _timer.Tick += (_, _) => _status.Text = ManagerWindow.OverlayStatus();
        _timer.Start();
        _status.Text = ManagerWindow.OverlayStatus();
        Closed += (_, _) => _timer.Stop();

        var stop = ManagerTheme.MakeButton("Stop the overlay", primary: false);
        stop.Click += (_, _) => OverlayStandalone.Quit();

        Content = new ScrollViewer {
            Content = new StackPanel {
                Margin = new Thickness(24, 16), Spacing = 12, MaxWidth = ManagerTheme.WIDE_PAGE_MAX_WIDTH,
                Children = {
                    new DockPanel {
                        Children = {
                            WithDock(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { stop } }, Dock.Right),
                            new TextBlock { Text = OverlayStandalone.NAME + "  " + OverlayHost.Version, FontSize = ManagerTheme.FONT_HEADING + 4, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                    new TextBlock {
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                        Text = "The overlay runs without Ahziael. Closing this window keeps it running; stop it with the button above, or " +
                               "right-click the sparkle icon by the clock. Changes show on the game at once.",
                    },
                    editor.EnabledRow(),
                    _status,
                    _messageBar,
                    editor.Build(),
                },
            },
        };
    }

    private static T WithDock<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    private void Say(string text, bool isError) {
        if (isError) {
            AppLogs.Refused(text);
        }

        _message.Text = text;
        _messageBar.Background = isError ? new SolidColorBrush(Color.FromRgb(0x8E, 0x2A, 0x2A)) : ManagerTheme.SURFACE_HIGH_BRUSH;
        _messageBar.IsVisible = true;
    }

}
