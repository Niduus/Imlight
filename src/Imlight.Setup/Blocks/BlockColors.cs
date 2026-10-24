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
/// The kinds of block (Niduus, 2026-10-22: "'block'-like programming for custom stuff"): a When block starts a script
/// (a hat), If blocks narrow it and Do blocks are what happens. One colour for each kind, as Scratch does.
/// </summary>
// Passive (Niduus, 2026-10-24: "a passive block, this will be a new block type"): a block that starts a cheat as a When
// does, but is always true of the combatant rather than set off by something. It comes before If so a stack's groups
// keep their order (the hat, its Ifs, its Dos).
internal enum BlockKind { When, Passive, If, Do }

/// <summary>The colours of the blocks and their pieces, kept in one place for every editor that uses blocks.</summary>
internal static class BlockColors {

    private static SolidColorBrush Brush(uint argb) => new(Color.FromUInt32(argb));

    // The colours themselves are the Manager's theme (ManagerTheme.cs), so the blocks and the rest of the app match.
    internal static readonly IBrush WHEN = ManagerTheme.Brush(ManagerTheme.BLOCK_WHEN);
    internal static readonly IBrush WHEN_EDGE = ManagerTheme.Brush(ManagerTheme.BLOCK_WHEN_EDGE);
    internal static readonly IBrush IF = ManagerTheme.Brush(ManagerTheme.BLOCK_IF);
    internal static readonly IBrush IF_EDGE = ManagerTheme.Brush(ManagerTheme.BLOCK_IF_EDGE);
    internal static readonly IBrush DO = ManagerTheme.Brush(ManagerTheme.BLOCK_DO);
    internal static readonly IBrush DO_EDGE = ManagerTheme.Brush(ManagerTheme.BLOCK_DO_EDGE);
    internal static readonly IBrush PASSIVE = ManagerTheme.Brush(ManagerTheme.BLOCK_PASSIVE);
    internal static readonly IBrush PASSIVE_EDGE = ManagerTheme.Brush(ManagerTheme.BLOCK_PASSIVE_EDGE);

    /// <summary>Whether a kind starts a cheat (the hat at the top of its stack): When or Passive.</summary>
    internal static bool IsHat(BlockKind kind) => kind is BlockKind.When or BlockKind.Passive;

    /// <summary>Text on a block.</summary>
    internal static readonly IBrush TEXT = Brushes.White;

    /// <summary>An inline box on a block: a light chip with dark text.</summary>
    internal static readonly IBrush FIELD = Brush(0xF2FFFFFF);
    internal static readonly IBrush FIELD_TEXT = Brush(0xFF1D2330);

    /// <summary>Where a dragged block will land.</summary>
    internal static readonly IBrush DROP_LINE = Brush(0xFFFFFFFF);

    /// <summary>The empty place under a script that blocks can be dropped on.</summary>
    internal static readonly IBrush SLOT = Brush(0x22FFFFFF);

    internal static IBrush Fill(BlockKind kind) => kind switch { BlockKind.When => WHEN, BlockKind.Passive => PASSIVE, BlockKind.If => IF, _ => DO };

    internal static IBrush Edge(BlockKind kind)
        => kind switch { BlockKind.When => WHEN_EDGE, BlockKind.Passive => PASSIVE_EDGE, BlockKind.If => IF_EDGE, _ => DO_EDGE };

    /// <summary>The word a kind is shown with in a palette.</summary>
    internal static string Word(BlockKind kind) => kind switch { BlockKind.When => "When…", BlockKind.Passive => "Passive…", BlockKind.If => "If…", _ => "Do…" };

    /// <summary>The section colours of an editor page (its icon tiles), by name, from the theme.</summary>
    internal static IBrush Accent(string name) => ManagerTheme.Accent(name);

}
