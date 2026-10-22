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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// The shape of a block: a rounded box with a notch on top and a tab underneath, so blocks stacked with no gap look
/// snapped together, as in Scratch. A hat (a When block) has a rounded bump on top instead of a notch. The tab is
/// drawn below the box, over the notch of the block under it.
/// </summary>
internal class BlockShape : Decorator {

    internal const double RADIUS = 7;
    internal const double NOTCH_X = 16;
    internal const double NOTCH_WIDTH = 26;
    internal const double NOTCH_DEPTH = 6;
    internal const double HAT_HEIGHT = 14;

    internal IBrush Fill { get; set; } = BlockColors.DO;
    internal IBrush Edge { get; set; } = BlockColors.DO_EDGE;
    internal bool Hat { get; set; }
    internal bool Tab { get; set; } = true;
    internal bool Notch { get; set; } = true;

    public override void Render(DrawingContext context) {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < NOTCH_X + NOTCH_WIDTH + RADIUS * 2 || h < RADIUS * 2) {
            context.DrawRectangle(Fill, new Pen(Edge), new Rect(Bounds.Size), RADIUS, RADIUS);
            base.Render(context);

            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) {
            var top = Hat ? HAT_HEIGHT : 0;
            var corner = new Size(RADIUS, RADIUS);
            if (Hat) {
                // The hat's bump: a soft hill over the left part of the block.
                var bump = Math.Min(130, w - RADIUS * 2);
                g.BeginFigure(new Point(0, top), true);
                g.CubicBezierTo(new Point(bump * 0.2, -HAT_HEIGHT * 0.45), new Point(bump * 0.8, -HAT_HEIGHT * 0.45), new Point(bump, top));
            }
            else {
                g.BeginFigure(new Point(0, RADIUS), true);
                g.ArcTo(new Point(RADIUS, 0), corner, 0, false, SweepDirection.Clockwise);
                if (Notch) {
                    g.LineTo(new Point(NOTCH_X, 0));
                    g.LineTo(new Point(NOTCH_X + NOTCH_DEPTH, NOTCH_DEPTH));
                    g.LineTo(new Point(NOTCH_X + NOTCH_WIDTH - NOTCH_DEPTH, NOTCH_DEPTH));
                    g.LineTo(new Point(NOTCH_X + NOTCH_WIDTH, 0));
                }
            }

            g.LineTo(new Point(w - RADIUS, top));
            g.ArcTo(new Point(w, top + RADIUS), corner, 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(w, h - RADIUS));
            g.ArcTo(new Point(w - RADIUS, h), corner, 0, false, SweepDirection.Clockwise);
            if (Tab) {
                g.LineTo(new Point(NOTCH_X + NOTCH_WIDTH, h));
                g.LineTo(new Point(NOTCH_X + NOTCH_WIDTH - NOTCH_DEPTH, h + NOTCH_DEPTH));
                g.LineTo(new Point(NOTCH_X + NOTCH_DEPTH, h + NOTCH_DEPTH));
                g.LineTo(new Point(NOTCH_X, h));
            }

            g.LineTo(new Point(RADIUS, h));
            g.ArcTo(new Point(0, h - RADIUS), corner, 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }

        context.DrawGeometry(Fill, new Pen(Edge, 1.2), geometry);
        base.Render(context);
    }

}

/// <summary>
/// One block: its shape, a grip to drag it by, an icon, its sentence (words with boxes in between, which wrap when
/// the block is narrow) and small buttons to turn it off, copy it and delete it. What it means is left to its owner.
/// </summary>
internal sealed class BlockView : BlockShape {

    private readonly WrapPanel _parts = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private bool _on = true;
    private Button? _toggle;

    internal BlockKind Kind { get; }

    /// <summary>Its group in a stack (blocks are dragged within their group): 0 for When, 1 for If, 2 for Do.</summary>
    internal int Group => (int) Kind;

    /// <summary>What the block stands for, for its owner.</summary>
    internal object? Model { get; init; }

    /// <summary>The handle it is dragged by.</summary>
    internal Border Grip { get; }

    internal event Action? Deleted;
    internal event Action? Duplicated;
    internal event Action<bool>? Toggled;

    internal BlockView(BlockKind kind, string icon, string help, bool canDelete = true, bool canDuplicate = true, bool canToggle = true) {
        Kind = kind;
        Hat = kind == BlockKind.When;
        Fill = BlockColors.Fill(kind);
        Edge = BlockColors.Edge(kind);
        Padding = new Thickness(6, (Hat ? HAT_HEIGHT : NOTCH_DEPTH) + 5, 6, 7);
        MinHeight = Hat ? 52 : 40;
        Margin = new Thickness(0);

        Grip = new Border {
            Background = Brushes.Transparent, Padding = new Thickness(2, 4), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll),
            VerticalAlignment = VerticalAlignment.Center,
            Child = BlockIcons.Icon("grip", 14, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))),
        };
        ToolTip.SetTip(Grip, "Drag to move it");
        if (canToggle) {
            _toggle = Tool("eye", "Turn this block off (it stays, but does nothing) or back on", () => SetOn(!_on, notify: true));
            _tools.Children.Add(_toggle);
        }

        if (canDuplicate) {
            _tools.Children.Add(Tool("copy", "Make a copy of this block", () => Duplicated?.Invoke()));
        }

        if (canDelete) {
            _tools.Children.Add(Tool("trash", "Delete this block", () => Deleted?.Invoke()));
        }

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Grip, Dock.Left);
        dock.Children.Add(Grip);
        var iconBox = new Border { Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Child = BlockIcons.Icon(icon, 18, BlockColors.TEXT) };
        DockPanel.SetDock(iconBox, Dock.Left);
        dock.Children.Add(iconBox);
        DockPanel.SetDock(_tools, Dock.Right);
        dock.Children.Add(_tools);
        dock.Children.Add(_parts);
        Child = dock;
        if (help.Length > 0) {
            ToolTip.SetTip(iconBox, help);
        }
    }

    internal bool IsOn => _on;

    internal void SetOn(bool on, bool notify = false) {
        _on = on;
        Opacity = on ? 1 : 0.45;
        if (_toggle is not null) {
            _toggle.Content = BlockIcons.Icon(on ? "eye" : "eyeoff", 14, BlockColors.TEXT);
        }

        if (notify) {
            Toggled?.Invoke(on);
        }
    }

    /// <summary>Words of the block's sentence.</summary>
    internal BlockView Text(string text, bool bold = false) {
        _parts.Children.Add(new TextBlock {
            Text = text, Foreground = BlockColors.TEXT, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Medium, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 3, 4, 3), TextWrapping = TextWrapping.NoWrap,
        });

        return this;
    }

    /// <summary>A box of the block's sentence (BlockFields makes them).</summary>
    internal BlockView Field(Control field) {
        field.Margin = new Thickness(2, 3, 4, 3);
        field.VerticalAlignment = VerticalAlignment.Center;
        _parts.Children.Add(field);

        return this;
    }

    private static Button Tool(string icon, string tip, Action click) {
        var button = new Button {
            Content = BlockIcons.Icon(icon, 14, BlockColors.TEXT), Padding = new Thickness(4), MinWidth = 0, MinHeight = 0,
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();

        return button;
    }

}
