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

    private const string CREDITS = "Credits";

    /// <summary>
    /// Niduus, 2026-11-09: "add credits to Ahziael", with these lines, word for word (and Atmoplex, added the same day). Help > Credits shows them, each with
    /// a heart, and the side bar and the search window find it like every other tab.
    /// </summary>
    private static readonly string[] s_credits = [
        "Credit to the Revive101 Team for Imlight (Myself included)",
        "Credit to Phill for Aurorium",
        "Credit to Revive101 Community for bug finding",
        "Credit to Revive101 Team and QA for bug hunting",
        // Niduus, 2026-11-09: "Add credits to Atmoplex for Wizard404 for Wiki Data".
        "Credit to Atmoplex for Wizard404 for Wiki Data",
        // Niduus, 2026-11-12 (after a tester's Ahziael on 0.10.793 would not open at all).
        "Shout out to Mitsu for being the first confirmed case to break Ahziael to the point of having to uninstall and reinstall to fix it. Top tier QA member!",
        "Created by Niduus (of course)",
    ];

    /// <summary>The Credits tab: a card with each credit line, and the "Created by Niduus" chip under them.</summary>
    private Control CreditsTab() {
        var lines = new StackPanel { Spacing = 12 };
        lines.Children.Add(new TextBlock {
            Text = "Thank you", FontSize = ManagerTheme.FONT_SECTION, FontWeight = FontWeight.SemiBold,
        });
        foreach (var credit in s_credits) {
            var heart = WithCenter(ManagerTheme.Icon("heart", 16, ManagerTheme.Accent(CREDITS)));
            heart.Margin = new Thickness(0, 0, 10, 0);
            lines.Children.Add(new DockPanel {
                Children = {
                    WithDock(heart, Dock.Left),
                    new TextBlock {
                        Text = credit, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                        FontSize = ManagerTheme.FONT_SECTION - 2,
                    },
                },
            });
        }

        lines.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = CreditChip() });

        return Page(Card(lines));
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
