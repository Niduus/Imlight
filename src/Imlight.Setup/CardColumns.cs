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
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Imlight.Setup;

/// <summary>
/// A tab's cards (Niduus, 2026-11-19: "lots of empty space in the EXE and not using them up"). In a window of the usual size
/// the cards stand one under the other as before. Once the window is wide enough for two cards side by side, they go in two
/// columns: the first card left, the second right, the third left under the first, and so on, so a card never jumps to the
/// other side when one above it is folded shut. Every page starts at the left edge, under the bar that says where you are.
/// </summary>
internal sealed class CardColumns : Panel {

    // From this width two columns, each at least about 690 wide; below it, one column at most PAGE_MAX_WIDTH wide.
    internal const double TWO_FROM = 1400;

    private bool _two;
    private double _column;

    protected override Size MeasureOverride(Size availableSize) {
        var width = double.IsInfinity(availableSize.Width) ? ManagerTheme.PAGE_MAX_WIDTH : availableSize.Width;
        var shown = Children.Where(c => c.IsVisible).ToList();
        _two = shown.Count > 1 && width >= TWO_FROM;
        _column = _two ? (width - ManagerTheme.GAP) / 2 : Math.Min(width, ManagerTheme.PAGE_MAX_WIDTH);

        var heights = new double[2];
        var counts = new int[2];
        var at = 0;
        foreach (var child in Children) {
            child.Measure(new Size(_column, double.PositiveInfinity));
            if (!child.IsVisible) {
                continue;
            }

            var side = _two ? at++ % 2 : 0;
            heights[side] += (counts[side] > 0 ? ManagerTheme.GAP : 0) + child.DesiredSize.Height;
            counts[side]++;
        }

        return new Size(_two ? width : _column, Math.Max(heights[0], heights[1]));
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var column = _two ? (finalSize.Width - ManagerTheme.GAP) / 2 : Math.Min(finalSize.Width, ManagerTheme.PAGE_MAX_WIDTH);
        var tops = new double[2];
        var at = 0;
        foreach (var child in Children) {
            if (!child.IsVisible) {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var side = _two ? at++ % 2 : 0;
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(side * (column + ManagerTheme.GAP), tops[side], column, height));
            tops[side] += height + ManagerTheme.GAP;
        }

        return finalSize;
    }

}
