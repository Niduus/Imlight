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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Changing the overlay right on the game (Niduus, 2026-11-11: "I was also hoping to click the edit button over the game
/// to make the UI in the game ... I want both methods to work"). The sparkle button opens a see-through window exactly over
/// that game window's inside, with the same designer as Ahziael's (pick, drag, size, add, the Server and Client parts) and
/// a small bar on top. It changes the same design, so Ahziael's designer follows along. While it is open the usual overlay
/// steps aside, and "Done" (or Escape) puts it back.
/// </summary>
internal sealed class OverlayGameEditor : Window {

    private static OverlayGameEditor? s_open;

    private readonly IntPtr _game;
    private readonly ManagerWindow.OverlayEditor _editor;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private readonly Border _messageBar = new() {
        Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(6), IsVisible = false, Margin = new Thickness(0, 0, 0, 60),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, MaxWidth = 700,
    };
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private (int X, int Y, int Width, int Height, double Scale) _placed;

    /// <summary>Opens the editor over the game whose sparkle button was clicked; false when there is no such game window.</summary>
    internal static bool Open(Action fullDesigner) {
        var game = OverlayHost.RequestedGame;
        if (OverlayHost.GameClient(game) is null) {
            return false;
        }

        if (s_open is not null) {
            if (s_open._game == game) {
                s_open.Activate();
                return true;
            }

            s_open.Close();
        }

        s_open = new OverlayGameEditor(game, fullDesigner);
        s_open.Show();
        s_open.Activate();

        return true;
    }

    private OverlayGameEditor(IntPtr game, Action fullDesigner) {
        _game = game;
        Title = "Ahziael overlay: change it on the game";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ManagerTheme.Apply(this);
        Background = Brushes.Transparent;

        _editor = new ManagerWindow.OverlayEditor(this, Say, () => ManagerWindow.OverlayEditor.ServerFileIn(OverlayHost.InstallFolder), inGame: true);
        _messageBar.Child = _message;
        Content = new Panel {
            Children = {
                _editor.BuildInGame(() => {
                    Close();
                    fullDesigner();
                }, Close),
                _messageBar,
            },
        };

        KeyDown += (_, e) => {
            if (e.Key == Avalonia.Input.Key.Escape) {
                Close();
            }
        };
        Place();
        _follow.Tick += (_, _) => Place();
        _follow.Start();
        OverlayHost.EditingInGame = true;
        Closed += (_, _) => {
            _follow.Stop();
            OverlayHost.EditingInGame = false;
            if (s_open == this) {
                s_open = null;
            }
        };
    }

    // Exactly over the game window's inside, following it if it moves or changes size; gone with the game.
    private void Place() {
        if (OverlayHost.GameClient(_game) is not { } client) {
            Close();
            return;
        }

        if (client == _placed) {
            return;
        }

        _placed = client;
        Position = new PixelPoint(client.X, client.Y);
        Width = client.Width / client.Scale;
        Height = client.Height / client.Scale;
        _editor.SetGameArea(client.Width / client.Scale, client.Height / client.Scale);
    }

    private void Say(string text, bool isError) {
        _message.Text = text;
        _messageBar.Background = isError ? new SolidColorBrush(Color.FromRgb(0x8E, 0x2A, 0x2A)) : new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x16, 0x22));
        _messageBar.IsVisible = true;
    }

}
