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
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Imlight.Setup.Blocks;

/// <summary>
/// A script: blocks stacked with no gap so they snap together, the When block on top, then the If blocks, then the Do
/// blocks. A block is dragged by its grip within its own group; a block from a palette (BlockDrag) can be dropped in
/// where the white line shows. The stack only reports the moves; its owner changes its model and shows it again.
/// </summary>
internal sealed class BlockStack : Grid {

    private readonly StackPanel _list = new() { Spacing = 0 };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _marker = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = BlockColors.DROP_LINE, IsVisible = false };
    private readonly Border _slot;

    private BlockView? _dragging;
    private Point _dragStart;
    private bool _dragMoved;

    /// <summary>A block was dragged to a new place in its group (its index among the blocks of its kind).</summary>
    internal event Action<BlockView, int>? Moved;

    /// <summary>A palette block was dropped in: what it is, its kind and its index among the blocks of its kind.</summary>
    internal event Action<object, BlockKind, int>? Dropped;

    internal BlockStack(string emptyHint = "Drop blocks here, or click one in the palette") {
        _slot = new Border {
            Margin = new Thickness(0, BlockShape.NOTCH_DEPTH + 6, 0, 0), Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8),
            BorderBrush = BlockColors.SLOT, BorderThickness = new Thickness(2), Background = Brushes.Transparent,
            Child = new TextBlock { Text = emptyHint, Foreground = new SolidColorBrush(Color.FromArgb(150, 200, 200, 200)), FontSize = 12 },
        };
        _overlay.Children.Add(_marker);
        Children.Add(new StackPanel { Children = { _list, _slot } });
        Children.Add(_overlay);
        BlockDrag.Register(this);
    }

    internal IReadOnlyList<BlockView> Blocks => [.. _list.Children.OfType<BlockView>()];

    internal void SetBlocks(IEnumerable<BlockView> blocks) {
        _list.Children.Clear();
        foreach (var block in blocks.OrderBy(b => b.Group)) {
            Attach(block);
            _list.Children.Add(block);
        }
    }

    private void Attach(BlockView block) {
        block.Grip.PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(block.Grip).Properties.IsLeftButtonPressed) {
                return;
            }

            _dragging = block;
            _dragStart = e.GetPosition(this);
            _dragMoved = false;
            e.Pointer.Capture(block.Grip);
            e.Handled = true;
        };
        block.Grip.PointerMoved += (_, e) => {
            if (_dragging != block) {
                return;
            }

            var p = e.GetPosition(this);
            if (!_dragMoved && Math.Abs(p.Y - _dragStart.Y) < 4) {
                return;
            }

            _dragMoved = true;
            block.RenderTransform = new TranslateTransform(0, p.Y - _dragStart.Y);
            block.ZIndex = 10;
            block.Opacity = 0.85;
            ShowMarker(p.Y, block.Kind);
        };
        block.Grip.PointerReleased += (_, e) => {
            if (_dragging != block) {
                return;
            }

            var p = e.GetPosition(this);
            _dragging = null;
            e.Pointer.Capture(null);
            block.RenderTransform = null;
            block.ZIndex = 0;
            block.SetOn(block.IsOn);
            HideMarker();
            if (!_dragMoved) {
                return;
            }

            var blocks = Blocks.ToList();
            var from = blocks.IndexOf(block);
            var (lo, hi) = Range(block.Kind);
            var gap = Math.Clamp(GapAt(p.Y), lo, hi);
            var to = (gap > from ? gap - 1 : gap) - lo;
            if (from - lo != to) {
                Moved?.Invoke(block, to);
            }
        };
    }

    // The blocks before the group, and the end of the group: where a block of that kind may go.
    private (int Lo, int Hi) Range(BlockKind kind) {
        var blocks = Blocks;

        return (blocks.Count(b => b.Group < (int) kind), blocks.Count(b => b.Group <= (int) kind));
    }

    // The gap nearest a height in the stack: 0 is above the first block.
    private int GapAt(double y) {
        var blocks = Blocks;
        for (var i = 0; i < blocks.Count; i++) {
            var top = blocks[i].TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
            if (y < top + blocks[i].Bounds.Height / 2) {
                return i;
            }
        }

        return blocks.Count;
    }

    private double GapY(int gap) {
        var blocks = Blocks;
        if (blocks.Count == 0) {
            return 0;
        }

        return gap < blocks.Count
            ? blocks[gap].TranslatePoint(new Point(0, 0), this)?.Y ?? 0
            : (blocks[^1].TranslatePoint(new Point(0, 0), this)?.Y ?? 0) + blocks[^1].Bounds.Height;
    }

    internal void ShowMarker(double y, BlockKind kind) {
        var (lo, hi) = Range(kind);
        var gap = BlockColors.IsHat(kind) ? 0 : Math.Clamp(GapAt(y), lo, hi);
        _marker.Width = Math.Max(40, Bounds.Width);
        Canvas.SetLeft(_marker, 0);
        Canvas.SetTop(_marker, Math.Max(0, GapY(gap) - 2));
        _marker.IsVisible = true;
    }

    internal void HideMarker() => _marker.IsVisible = false;

    /// <summary>Whether a point (in this stack's own space) is over it, with a little room around it.</summary>
    internal bool Covers(Point p) => IsEffectivelyVisible && this.GetVisualRoot() is not null
                                     && p.X >= -20 && p.Y >= -20 && p.X <= Bounds.Width + 20 && p.Y <= Bounds.Height + 30;

    internal void DropAt(Point p, object payload, BlockKind kind) {
        HideMarker();
        var (lo, hi) = Range(kind);
        var gap = Math.Clamp(GapAt(p.Y), lo, hi);
        Dropped?.Invoke(payload, kind, gap - lo);
    }

}

/// <summary>
/// Dragging a block out of a palette: a copy of it follows the pointer over the window, the stack under it shows where
/// it would go, and letting go there adds it. A press with no drag is a click.
/// </summary>
internal static class BlockDrag {

    private static readonly List<WeakReference<BlockStack>> s_stacks = [];

    internal static void Register(BlockStack stack) {
        s_stacks.RemoveAll(w => !w.TryGetTarget(out _));
        s_stacks.Add(new WeakReference<BlockStack>(stack));
    }

    private static IEnumerable<BlockStack> Stacks() {
        foreach (var weak in s_stacks.ToList()) {
            if (weak.TryGetTarget(out var stack)) {
                yield return stack;
            }
        }
    }

    /// <summary>Makes a palette chip draggable; click runs when it is pressed and let go without moving.</summary>
    internal static void MakeDraggable(Control chip, BlockKind kind, object payload, Func<Control> ghost, Action click) {
        Point start = default;
        var pressed = false;
        var moved = false;
        Control? shown = null;
        OverlayLayer? layer = null;

        BlockStack? Over(PointerEventArgs e) => Stacks().FirstOrDefault(s => s.Covers(e.GetPosition(s)));

        chip.PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) {
                return;
            }

            pressed = true;
            moved = false;
            start = e.GetPosition(null);
            e.Pointer.Capture(chip);
            e.Handled = true;
        };
        chip.PointerMoved += (_, e) => {
            if (!pressed) {
                return;
            }

            var p = e.GetPosition(null);
            if (!moved && Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 6) {
                return;
            }

            if (!moved) {
                moved = true;
                layer = OverlayLayer.GetOverlayLayer(chip);
                shown = ghost();
                shown.Opacity = 0.85;
                shown.IsHitTestVisible = false;
                layer?.Children.Add(shown);
            }

            if (shown is not null && layer is not null) {
                var at = e.GetPosition(layer);
                Canvas.SetLeft(shown, at.X - 20);
                Canvas.SetTop(shown, at.Y - 18);
            }

            var over = Over(e);
            foreach (var stack in Stacks()) {
                if (stack == over) {
                    stack.ShowMarker(e.GetPosition(stack).Y, kind);
                }
                else {
                    stack.HideMarker();
                }
            }
        };
        chip.PointerReleased += (_, e) => {
            if (!pressed) {
                return;
            }

            pressed = false;
            e.Pointer.Capture(null);
            if (shown is not null) {
                layer?.Children.Remove(shown);
                shown = null;
            }

            if (!moved) {
                click();

                return;
            }

            var over = Over(e);
            foreach (var stack in Stacks()) {
                stack.HideMarker();
            }

            over?.DropAt(e.GetPosition(over), payload, kind);
        };
        chip.PointerCaptureLost += (_, _) => {
            pressed = false;
            if (shown is not null) {
                layer?.Children.Remove(shown);
                shown = null;
            }
        };
    }

}
