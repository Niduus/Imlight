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

using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// Small drawn icons (24 by 24) for blocks, palettes and section headers. They live in the Manager's one theme
/// (ManagerTheme.cs) with every other icon of the app; this keeps the names the block editor uses. The game's own pictures
/// (school icons, spell cards) are used where the app has them.
/// </summary>
internal static class BlockIcons {

    internal static bool Has(string name) => ManagerTheme.HasIcon(name);

    /// <summary>An icon as a control, in the given colour and size.</summary>
    internal static Control Icon(string name, double size, IBrush brush) => ManagerTheme.Icon(name, size, brush);

    /// <summary>An icon on a rounded coloured tile, for section headers and palette groups.</summary>
    internal static Border Tile(string name, IBrush background, double size = 34) => ManagerTheme.Tile(name, background, size);

}
