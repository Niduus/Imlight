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
/// The credit at the bottom of the side bar (Niduus, 2026-10-22: "Add my Github picture and credit 'Created by Niduus'
/// in this section of the manager"): his GitHub picture in a circle and the line beside it. It opens his GitHub page.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string CREDIT_URL = "https://github.com/Niduus";
    private const double CREDIT_AVATAR_SIZE = 30;

    /// <summary>The credit row, pinned to the bottom of the side bar over its background.</summary>
    private static Border CreditFooter() {
        var avatar = new Ellipse {
            Width = CREDIT_AVATAR_SIZE, Height = CREDIT_AVATAR_SIZE,
            Stroke = LINE, StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var picture = LoadAvatar();
        avatar.Fill = picture is null
            ? ManagerTheme.SURFACE_HIGH_BRUSH
            : new ImageBrush(picture) { Stretch = Stretch.UniformToFill };

        var row = new Border {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = {
                    avatar,
                    new TextBlock {
                        Text = "Created by Niduus", FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        ToolTip.SetTip(row, "github.com/Niduus");
        row.PointerEntered += (_, _) => row.Background = ManagerTheme.SURFACE_BRUSH;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, e) => {
            if (e.InitialPressMouseButton == MouseButton.Left) {
                OpenInBrowser(CREDIT_URL);
            }
        };

        return new Border {
            Width = ManagerTheme.SIDEBAR_WIDTH - 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = ManagerTheme.SIDEBAR_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 6),
            Child = row,
        };
    }

    /// <summary>
    /// Shows the credit only while the side bar has room for it under its lowest tab, so on a short window (where the
    /// tabs run on into a second column) it never covers a tab.
    /// </summary>
    private static void FitCreditFooter(Panel body, TabControl tabs, Border footer) {
        // Its height while shown (a hidden control measures as 0), kept so showing and hiding it never see-saw.
        var footerHeight = 55.0;

        void Fit() {
            if (footer.IsVisible && footer.DesiredSize.Height > 0) {
                footerHeight = footer.DesiredSize.Height;
            }

            var lowest = 0.0;
            for (var i = 0; i < tabs.ItemCount; i++) {
                if (tabs.ContainerFromIndex(i) is not Control tab
                    || tab.TranslatePoint(new Point(0, tab.Bounds.Height), body) is not { } point) {
                    return;
                }

                if (point.X < ManagerTheme.SIDEBAR_WIDTH) {
                    lowest = Math.Max(lowest, point.Y);
                }
            }

            var fits = lowest + 2 + footerHeight <= body.Bounds.Height;
            if (footer.IsVisible != fits) {
                footer.IsVisible = fits;
            }
        }

        body.SizeChanged += (_, _) => Fit();
        tabs.LayoutUpdated += (_, _) => Fit();
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
