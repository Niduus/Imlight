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
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The designers' grid and snapping (Niduus, 2026-11-11: "allow for grid and grid snapping with customizations and so
/// on"). Only for designing: the grid is never drawn on the game. Kept in overlay-grid.json beside overlay.json, so the
/// designer in Ahziael and the one on the game use the same.
/// </summary>
internal sealed class OverlayGrid {

    internal const string FILE = "overlay-grid.json";

    public bool Show { get; set; } = true;
    public double Size { get; set; } = 20;
    public string Color { get; set; } = "#FFFFFFFF";
    public double Opacity { get; set; } = 0.15;

    /// <summary>Every Nth line drawn stronger (0: none).</summary>
    public int MajorEvery { get; set; } = 5;

    public bool SnapToGrid { get; set; } = true;
    public bool SnapToPieces { get; set; } = true;

    /// <summary>Lines that show what a dragged piece lines up with.</summary>
    public bool Guides { get; set; } = true;

    /// <summary>How near (design pixels) an edge or middle has to come to snap.</summary>
    public double SnapDistance { get; set; } = 6;

    public string GuideColor { get; set; } = "#FFFF3EA5";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 4 };

    internal OverlayGrid Clean() {
        Size = OverlayElement.Range(Size, 2, 400, 20);
        Opacity = OverlayElement.Range(Opacity, 0, 1, 0.15);
        MajorEvery = Math.Clamp(MajorEvery, 0, 50);
        SnapDistance = OverlayElement.Range(SnapDistance, 1, 40, 6);
        Color = OverlayPaint.CleanColor(Color, "#FFFFFFFF");
        GuideColor = OverlayPaint.CleanColor(GuideColor, "#FFFF3EA5");

        return this;
    }

    internal static OverlayGrid Load(string installFolder) {
        try {
            var path = Path.Combine(installFolder, FILE);
            if (File.Exists(path) && new FileInfo(path).Length < 16 * 1024) {
                return (JsonSerializer.Deserialize<OverlayGrid>(File.ReadAllText(path), s_json) ?? new OverlayGrid()).Clean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
        }

        return new OverlayGrid();
    }

    internal void Save(string installFolder) {
        try {
            File.WriteAllText(Path.Combine(installFolder, FILE), JsonSerializer.Serialize(Clean(), s_json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    /// <summary>The nearest grid line to a value.</summary>
    internal double ToGrid(double value) => Math.Round(value / Size) * Size;

}

/// <summary>The grid and the guide lines, drawn over the designer's picture; clicks go through it.</summary>
internal sealed class OverlayGridLayer : Control {

    private readonly OverlayGrid _grid;
    private readonly IReadOnlyList<(Point From, Point To)> _guides;

    internal OverlayGridLayer(OverlayGrid grid, IReadOnlyList<(Point From, Point To)> guides) {
        _grid = grid;
        _guides = guides;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context) {
        var size = Bounds.Size;
        if (_grid.Show && _grid.Size >= 2 && OverlayRenderer.Parse(_grid.Color) is { } color) {
            var minor = new Pen(new SolidColorBrush(color, _grid.Opacity), 1);
            var major = new Pen(new SolidColorBrush(color, Math.Min(1, _grid.Opacity * 2.2)), 1);
            var i = 0;
            for (var x = 0.0; x <= size.Width; x += _grid.Size, i++) {
                context.DrawLine(_grid.MajorEvery > 0 && i % _grid.MajorEvery == 0 ? major : minor, new Point(x + 0.5, 0), new Point(x + 0.5, size.Height));
            }

            i = 0;
            for (var y = 0.0; y <= size.Height; y += _grid.Size, i++) {
                context.DrawLine(_grid.MajorEvery > 0 && i % _grid.MajorEvery == 0 ? major : minor, new Point(0, y + 0.5), new Point(size.Width, y + 0.5));
            }
        }

        if (_grid.Guides && _guides.Count > 0 && OverlayRenderer.Parse(_grid.GuideColor) is { } guide) {
            var pen = new Pen(new SolidColorBrush(guide), 1.5, new DashStyle([4, 3], 0));
            foreach (var (from, to) in _guides) {
                context.DrawLine(pen, from, to);
            }
        }
    }

}
