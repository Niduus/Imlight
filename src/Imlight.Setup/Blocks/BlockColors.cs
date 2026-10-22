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

using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// The kinds of block (Justin, 2026-10-22: "'block'-like programming for custom stuff"): a When block starts a script
/// (a hat), If blocks narrow it and Do blocks are what happens. One colour for each kind, as Scratch does.
/// </summary>
internal enum BlockKind { When, If, Do }

/// <summary>The colours of the blocks and their pieces, kept in one place for every editor that uses blocks.</summary>
internal static class BlockColors {

    private static SolidColorBrush Brush(uint argb) => new(Color.FromUInt32(argb));

    internal static readonly IBrush WHEN = Brush(0xFFE0A323);
    internal static readonly IBrush WHEN_EDGE = Brush(0xFFB37F12);
    internal static readonly IBrush IF = Brush(0xFF3A8FD9);
    internal static readonly IBrush IF_EDGE = Brush(0xFF2468A6);
    internal static readonly IBrush DO = Brush(0xFF8B5CD6);
    internal static readonly IBrush DO_EDGE = Brush(0xFF6538A8);

    /// <summary>Text on a block.</summary>
    internal static readonly IBrush TEXT = Brushes.White;

    /// <summary>An inline box on a block: a light chip with dark text.</summary>
    internal static readonly IBrush FIELD = Brush(0xF2FFFFFF);
    internal static readonly IBrush FIELD_TEXT = Brush(0xFF1D2330);

    /// <summary>Where a dragged block will land.</summary>
    internal static readonly IBrush DROP_LINE = Brush(0xFFFFFFFF);

    /// <summary>The empty place under a script that blocks can be dropped on.</summary>
    internal static readonly IBrush SLOT = Brush(0x22FFFFFF);

    internal static IBrush Fill(BlockKind kind) => kind switch { BlockKind.When => WHEN, BlockKind.If => IF, _ => DO };

    internal static IBrush Edge(BlockKind kind) => kind switch { BlockKind.When => WHEN_EDGE, BlockKind.If => IF_EDGE, _ => DO_EDGE };

    /// <summary>The word a kind is shown with in a palette.</summary>
    internal static string Word(BlockKind kind) => kind switch { BlockKind.When => "When…", BlockKind.If => "If…", _ => "Do…" };

    /// <summary>The section colours of an editor page (its icon tiles), by name.</summary>
    internal static IBrush Accent(string name) => name switch {
        "identity" => Brush(0xFF3FA7A0),
        "stats" => Brush(0xFFD9653B),
        "deck" => Brush(0xFF3A8FD9),
        "drops" => Brush(0xFFC9A227),
        "forms" => Brush(0xFF9A5CC9),
        "cheats" => Brush(0xFFE0A323),
        _ => Brush(0xFF6C7A8C),
    };

}
