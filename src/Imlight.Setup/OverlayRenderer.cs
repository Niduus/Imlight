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
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Turns an <see cref="OverlayDesign"/> into controls: the same picture for the designer's preview and for the overlay
/// drawn over the game.
/// </summary>
internal static class OverlayRenderer {

    /// <summary>What the {tokens} in a text stand for.</summary>
    internal sealed record Words(string Version, string Server);

    internal static bool UsesClock(OverlayDesign design)
        => design.Elements.Any(e => e.Visible && !e.IsBox && (e.Text.Contains("{time}") || e.Text.Contains("{date}")));

    /// <summary>The whole overlay, filling a game window of this size (in device-independent pixels).</summary>
    internal static Control Build(OverlayDesign design, Words words, OverlayElement? highlight = null) {
        var grid = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        foreach (var element in design.Elements.Where(e => e.Visible)) {
            grid.Children.Add(Element(element, words, element == highlight));
        }

        // The credit line is drawn last, so nothing covers it.
        grid.Children.Add(Element(design.Credit, words, design.Credit == highlight, OverlayDesign.CreditLine(words.Version)));

        return grid;
    }

    internal static string Fill(string text, Words words) => text
        .Replace("{version}", words.Version, StringComparison.OrdinalIgnoreCase)
        .Replace("{server}", words.Server, StringComparison.OrdinalIgnoreCase)
        .Replace("{time}", DateTime.Now.ToString("h:mm tt"), StringComparison.OrdinalIgnoreCase)
        .Replace("{date}", DateTime.Now.ToString("MMM d, yyyy"), StringComparison.OrdinalIgnoreCase);

    private static Control Element(OverlayElement e, Words words, bool highlight, string? fixedText = null) {
        var border = new Border {
            Background = Brush(e.Background),
            BorderBrush = Brush(new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = e.BorderColor }),
            BorderThickness = new Thickness(e.BorderWidth),
            CornerRadius = new CornerRadius(e.CornerRadius),
            Padding = new Thickness(e.Padding),
            Opacity = e.Opacity,
            HorizontalAlignment = e.Anchor switch {
                OverlayAnchor.TopLeft or OverlayAnchor.Left or OverlayAnchor.BottomLeft => HorizontalAlignment.Left,
                OverlayAnchor.TopRight or OverlayAnchor.Right or OverlayAnchor.BottomRight => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = e.Anchor switch {
                OverlayAnchor.TopLeft or OverlayAnchor.Top or OverlayAnchor.TopRight => VerticalAlignment.Top,
                OverlayAnchor.BottomLeft or OverlayAnchor.Bottom or OverlayAnchor.BottomRight => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center,
            },
        };

        // X and Y push it in from the side it is pinned to; for a centred one they move it right and down.
        var left = border.HorizontalAlignment == HorizontalAlignment.Right ? 0 : e.X;
        var right = border.HorizontalAlignment == HorizontalAlignment.Right ? e.X : 0;
        var top = border.VerticalAlignment == VerticalAlignment.Bottom ? 0 : e.Y;
        var bottom = border.VerticalAlignment == VerticalAlignment.Bottom ? e.Y : 0;
        if (border.HorizontalAlignment == HorizontalAlignment.Center) {
            (left, right) = (Math.Max(0, e.X) * 2, Math.Max(0, -e.X) * 2);
        }

        if (border.VerticalAlignment == VerticalAlignment.Center) {
            (top, bottom) = (Math.Max(0, e.Y) * 2, Math.Max(0, -e.Y) * 2);
        }

        border.Margin = new Thickness(left, top, right, bottom);

        if (e.Width > 0) {
            border.Width = e.Width;
        }

        if (e.Height > 0) {
            border.Height = e.Height;
        }

        if (e.Rotation != 0) {
            border.RenderTransform = new RotateTransform(e.Rotation);
            border.RenderTransformOrigin = RelativePoint.Center;
        }

        if (e.Shadow && Parse(e.ShadowColor) is { } shadow) {
            border.Effect = new DropShadowEffect {
                Color = shadow, BlurRadius = e.ShadowBlur, OffsetX = e.ShadowX, OffsetY = e.ShadowY, Opacity = 1,
            };
        }

        if (!e.IsBox) {
            var decorations = new TextDecorationCollection();
            if (e.Underline) {
                decorations.AddRange(TextDecorations.Underline);
            }

            if (e.Strikethrough) {
                decorations.AddRange(TextDecorations.Strikethrough);
            }

            var text = new TextBlock {
                Text = fixedText ?? Fill(e.Text, words),
                FontSize = e.Size,
                FontWeight = e.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = e.Italic ? FontStyle.Italic : FontStyle.Normal,
                TextDecorations = decorations,
                LetterSpacing = e.LetterSpacing,
                Foreground = Brush(e.Fill) ?? Brushes.Transparent,
                TextWrapping = e.Width > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextAlignment = e.Align switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left },
            };
            if (!string.IsNullOrWhiteSpace(e.Font)) {
                text.FontFamily = new FontFamily(e.Font);
            }

            if (e.LineHeight > 0) {
                text.LineHeight = e.LineHeight;
            }

            border.Child = text;
        }

        if (!highlight) {
            return border;
        }

        // The designer's dashed outline around the one being changed.
        return new Panel {
            HorizontalAlignment = border.HorizontalAlignment, VerticalAlignment = border.VerticalAlignment, Margin = border.Margin,
            Children = {
                Reset(border),
                new Border {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)), BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(Math.Max(2, e.CornerRadius)), Margin = new Thickness(-3), IsHitTestVisible = false,
                    RenderTransform = border.RenderTransform, RenderTransformOrigin = RelativePoint.Center,
                },
            },
        };

        static Border Reset(Border b) {
            b.Margin = default;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.VerticalAlignment = VerticalAlignment.Stretch;

            return b;
        }
    }

    internal static IBrush? Brush(OverlayPaint paint) {
        if (paint.IsNone || Parse(paint.Color1) is not { } one) {
            return null;
        }

        if (paint.Kind == OverlayPaint.SOLID) {
            return new SolidColorBrush(one);
        }

        var two = Parse(paint.Color2) ?? one;
        var stops = new GradientStops { new GradientStop(one, 0) };
        if (Parse(paint.Color3) is { } middle) {
            stops.Add(new GradientStop(middle, 0.5));
        }

        stops.Add(new GradientStop(two, 1));
        if (paint.Kind == OverlayPaint.RADIAL) {
            return new RadialGradientBrush { GradientStops = stops };
        }

        // The angle as a line through the middle: 0 runs left to right, 90 top to bottom.
        var radians = paint.Angle * Math.PI / 180;
        var dx = Math.Cos(radians) / 2;
        var dy = Math.Sin(radians) / 2;

        return new LinearGradientBrush {
            GradientStops = stops,
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
        };
    }

    internal static Color? Parse(string text) => !string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var color) ? color : null;

}
