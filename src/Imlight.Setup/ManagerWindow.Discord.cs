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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Discord button in the header (Niduus, 2026-11-28: "add the discord link as a button to the launcher, you click it,
/// opens default window to join the server"): Discord's own blue, its logo and the word, between the search box and the
/// server light, so it shows on every page. Clicking it opens the invite in the default browser.
/// </summary>
internal sealed partial class ManagerWindow {

    /// <summary>The project's Discord invite: news, updates and bug reports.</summary>
    internal const string DISCORD_URL = "https://discord.gg/FtVU6dEerP";

    // Discord's brand blue ("Blurple"), and a lighter one for its edge and when pointed at.
    private static readonly Color s_discordBlue = Color.FromRgb(88, 101, 242);
    private static readonly Color s_discordLight = Color.FromRgb(121, 131, 245);

    private TextBlock? _discordWord;

    /// <summary>The header's Discord button.</summary>
    private Border DiscordButton() {
        _discordWord = new TextBlock {
            Text = "Discord", FontSize = ManagerTheme.FONT_SMALL + 1, FontWeight = FontWeight.Bold,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        };
        var idle = new SolidColorBrush(s_discordBlue);
        var lit = new SolidColorBrush(s_discordLight);
        var button = new Border {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 0), Height = 34, Margin = new Thickness(10, 0, 0, 0),
            Background = idle, BorderBrush = lit, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, Color = Color.FromArgb(110, 88, 101, 242) }),
            Child = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { ManagerTheme.Icon("discord", 18, Brushes.White), _discordWord },
            },
        };
        ToolTip.SetTip(button, "Join the Ahziael Discord\nNews, updates and bug reports. Opens in your web browser.");
        button.PointerEntered += (_, _) => button.Background = lit;
        button.PointerExited += (_, _) => button.Background = idle;
        button.PointerReleased += (_, e) => {
            if (e.InitialPressMouseButton == MouseButton.Left) {
                OpenInBrowser(DISCORD_URL);
            }
        };

        return button;
    }
}
