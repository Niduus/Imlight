/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using Avalonia.Media;

namespace Imlight.Setup;

internal sealed partial class ManagerWindow {

    // Niduus, 2026-11-14: "add notices to Bots, QT and Ultimus tabs that tell you that they may take up extra space on the
    // computer and may take extra ram as well. In the case that someone complains about no notice later". Always shown, at
    // the top of each of those pages.
    private static Control SpaceNotice(string what, string? text = null) => new Border {
        Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = WARN, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
        Child = new TextBlock {
            TextWrapping = TextWrapping.Wrap, Foreground = WARN,
            Text = text ?? $"Notice: {what} may take up extra space on this computer (their logs, notes and saved data grow while they " +
                   "play) and may use extra memory (RAM) and processor while switched on. Turn them off when you don't need them.",
        },
    };

}
