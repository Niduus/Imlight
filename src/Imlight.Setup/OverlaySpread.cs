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
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Imlight.Setup;

/// <summary>
/// "Spread out" (Niduus, 2026-11-19: "Overlay UI not really being fanned out and thus taking up a bit more space"): each
/// piece goes to the corner of the game window it is nearest, and the pieces in one corner are laid side by side along
/// the top or bottom edge (a new row only when a row would pass the middle of the window), close together, so they keep
/// out of the middle of the game and never lap over each other. The room left at the window's edges stays as it was. Pieces set in the middle of an edge on purpose (like
/// Bot Alerts at the top), locked pieces and pieces inside another piece stay where they are.
/// </summary>
internal static class OverlaySpread {

    /// <summary>The space between two pieces, and between a piece and the window's edge (in design pixels).</summary>
    internal const double GAP = 8;

    /// <summary>
    /// Moves <paramref name="pieces"/> (each with where it is drawn now, in design pixels) as above, in a window of
    /// <paramref name="width"/> by <paramref name="height"/>, keeping clear of the Ahziael line (<paramref name="credit"/>).
    /// Returns how many pieces moved.
    /// </summary>
    internal static int Apply(IReadOnlyList<(OverlayElement Element, Rect Area)> pieces, double width, double height, Rect credit) {
        var moving = pieces.Where(p => Movable(p.Element) && p.Area.Width > 0 && p.Area.Height > 0).ToList();
        var corners = new Dictionary<OverlayAnchor, List<Block>>();
        foreach (var block in Blocks(moving)) {
            var c = block.Area.Center;
            var corner = (c.X < width / 2, c.Y < height / 2) switch {
                (true, true) => OverlayAnchor.TopLeft,
                (false, true) => OverlayAnchor.TopRight,
                (true, false) => OverlayAnchor.BottomLeft,
                _ => OverlayAnchor.BottomRight,
            };
            if (!corners.TryGetValue(corner, out var list)) {
                corners[corner] = list = [];
            }

            list.Add(block);
        }

        var moved = 0;
        foreach (var (corner, list) in corners) {
            var left = corner is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft;
            var top = corner is OverlayAnchor.TopLeft or OverlayAnchor.TopRight;
            // Nearest the corner first, so the piece already in the corner stays in it.
            var ordered = list.OrderBy(b => Math.Abs(left ? b.Area.Left : width - b.Area.Right) + Math.Abs(top ? b.Area.Top : height - b.Area.Bottom)).ToList();
            // The room left at the edges stays (it keeps the game's own buttons and health free): the corner's pieces
            // start as far from the edges as the nearest one is now. Bottom left: always above the Ahziael line.
            var side = Math.Max(GAP, ordered.Min(b => left ? b.Area.Left : width - b.Area.Right));
            var start = Math.Max(GAP, ordered.Min(b => top ? b.Area.Top : height - b.Area.Bottom));
            if (!top && left && credit.Width > 0) {
                start = Math.Max(start, height - credit.Top + GAP);
            }

            var rowLimit = Math.Max(width / 2 - GAP, side + 1);
            var (across, down, rowHeight) = (side, start, 0.0);
            foreach (var block in ordered) {
                var area = block.Area;
                if (across > side && across + area.Width > rowLimit) {
                    // This row is full: the next one, further from the edge.
                    down += rowHeight + GAP;
                    across = side;
                    rowHeight = 0;
                }

                // Where the block goes, in the window.
                var bx = left ? across : width - across - area.Width;
                var by = top ? down : height - down - area.Height;
                foreach (var (element, was) in block.Members) {
                    // Each piece keeps its place in the block (a map with its buttons and quest box under it).
                    var at = was.Translate(new Vector(bx - area.X, by - area.Y));
                    var x = Math.Round(left ? at.X : width - at.Right);
                    var y = Math.Round(top ? at.Y : height - at.Bottom);
                    if (element.Anchor != corner || Math.Abs(element.X - x) > 0.5 || Math.Abs(element.Y - y) > 0.5) {
                        moved++;
                    }

                    element.Anchor = corner;
                    element.X = Math.Max(0, x);
                    element.Y = Math.Max(0, y);
                    // Kept apart on the game too, even when a box grows (a quest box with more steps).
                    element.Spacing = Math.Max(element.Spacing, GAP);
                }

                across += area.Width + GAP;
                rowHeight = Math.Max(rowHeight, area.Height);
            }
        }

        return moved;
    }

    private sealed record Block(Rect Area, List<(OverlayElement Element, Rect Area)> Members);

    // A button of an add-on widget stays with the piece of that widget it sits by (a spectator's Go to and Next buttons
    // by its map), so the block moves as one. Every other piece is a block of its own, so boxes fan out side by side.
    private static List<Block> Blocks(List<(OverlayElement Element, Rect Area)> pieces) {
        var blocks = pieces.Where(p => !p.Element.IsButton || p.Element.From.Length == 0).Select(p => new Block(p.Area, [p])).ToList();
        foreach (var button in pieces.Where(p => p.Element.IsButton && p.Element.From.Length > 0)) {
            var by = blocks.Where(b => b.Members.Any(m => m.Element.From == button.Element.From && m.Area.Inflate(NEAR).Intersects(button.Area)))
                .OrderBy(b => b.Members.Any(m => !m.Element.IsButton) ? 0 : 1).FirstOrDefault();
            if (by is null) {
                blocks.Add(new Block(button.Area, [button]));
                continue;
            }

            by.Members.Add(button);
            blocks[blocks.IndexOf(by)] = by with { Area = by.Area.Union(button.Area) };
        }

        return blocks;
    }

    // How close two pieces of one widget are to count as together (design pixels).
    private const double NEAR = 24;

    // Only pieces set from a corner or a side; not one placed in the middle of an edge on purpose, a locked one, or one
    // inside another piece (it moves with its parent).
    private static bool Movable(OverlayElement e) => e.Visible && !e.Locked && e.Parent.Length == 0
        && e.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight or OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight or OverlayAnchor.Left or OverlayAnchor.Right;

}
