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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The credit (Niduus, 2026-10-22: "Add my Github picture and credit 'Created by Niduus'"): his GitHub picture in a
/// circle and the line beside it, next to the app's name in the header since 2026-10-25. It opens his GitHub page.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string CREDIT_URL = "https://github.com/Niduus";
    private const double CREDIT_AVATAR_SIZE = 22;

    /// <summary>The credit beside the app's name in the header: a pill with the picture and "Created by Niduus".</summary>
    private static Border CreditChip() {
        var avatar = new Ellipse {
            Width = CREDIT_AVATAR_SIZE, Height = CREDIT_AVATAR_SIZE,
            Stroke = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 150), StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var picture = LoadAvatar();
        avatar.Fill = picture is null
            ? ManagerTheme.SURFACE_HIGH_BRUSH
            : new ImageBrush(picture) { Stretch = Stretch.UniformToFill };

        var idle = ManagerTheme.Brush(ManagerTheme.ACCENT, 34);
        var chip = new Border {
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL), Padding = new Thickness(3, 3, 12, 3), Background = idle,
            BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 90), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = {
                    avatar,
                    new TextBlock {
                        Text = "Created by Niduus", FontSize = ManagerTheme.FONT_SMALL, FontWeight = FontWeight.SemiBold,
                        Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        ToolTip.SetTip(chip, "github.com/Niduus");
        chip.PointerEntered += (_, _) => chip.Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 80);
        chip.PointerExited += (_, _) => chip.Background = idle;
        chip.PointerReleased += (_, e) => {
            if (e.InitialPressMouseButton == MouseButton.Left) {
                OpenInBrowser(CREDIT_URL);
            }
        };

        return chip;
    }

    private static Bitmap? LoadAvatar() {
        try {
            // The assembly is ImlightSetup or ImlightManager depending on the edition.
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/niduus-avatar.png"));

            return new Bitmap(stream);
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>Opens a web page in the default browser.</summary>
    private static void OpenInBrowser(string url) {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) {
            // No browser to open it with; the tooltip still shows where it goes.
        }
    }

}
