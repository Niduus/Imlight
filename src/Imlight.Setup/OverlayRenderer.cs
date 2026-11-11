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

    /// <summary>
    /// The whole overlay, filling a game window of this size (in device-independent pixels). Your texts and boxes are drawn
    /// lowest layer first (same layer: list order), the Ahziael line last, so nothing covers it.
    /// </summary>
    internal static Control Build(OverlayDesign design, Words words, OverlayElement? highlight = null, bool showMine = true) {
        var panel = new OverlayPanel { Background = Brushes.Transparent, ClipToBounds = true };
        if (showMine) {
            foreach (var element in design.Elements.Where(e => e.Visible).Select((e, i) => (e, i)).OrderBy(p => p.e.Layer).ThenBy(p => p.i).Select(p => p.e)) {
                panel.Children.Add(Element(element, words, element == highlight));
            }
        }

        panel.Children.Add(Element(design.Credit, words, design.Credit == highlight, OverlayDesign.CreditLine(words.Version)));

        return panel;
    }

    /// <summary>
    /// Places each text and box from its corner or side, then moves any that laps over one placed before it off that
    /// one, leaving its Spacing between them (Niduus, 2026-11-11: "Introduce layers and spacing as well so if a UI is
    /// lapping over another, the spacing allows it to move the overlaying UI over some = to the spacing value"). The
    /// Ahziael line is placed first and never moves; everything keeps clear of it.
    /// </summary>
    private sealed class OverlayPanel : Panel {

        protected override Size MeasureOverride(Size available) {
            foreach (var child in Children) {
                child.Measure(Size.Infinity);
            }

            return available;
        }

        protected override Size ArrangeOverride(Size final) {
            var bounds = new Rect(final);
            // The Ahziael line first (it is the last child), then from the lowest layer up: the one that laps over
            // another (drawn over it) is the one that moves.
            var order = new List<Control> { Children[^1] };
            order.AddRange(Children.Take(Children.Count - 1));
            var placed = new List<(Rect Rect, double Gap, bool Credit)>();
            foreach (var child in order) {
                if (child.Tag is not OverlayElement e) {
                    child.Arrange(new Rect(child.DesiredSize));
                    continue;
                }

                var credit = child == Children[^1];
                var rect = Start(e, child.DesiredSize, final);
                if (!credit) {
                    rect = Clear(rect, e.Spacing, placed, bounds);
                }

                placed.Add((rect, e.Spacing, credit));
                child.Arrange(rect);
            }

            return final;
        }

        // Where its corner or side and X and Y put it.
        private static Rect Start(OverlayElement e, Size size, Size area) {
            var x = e.Anchor switch {
                OverlayAnchor.TopLeft or OverlayAnchor.Left or OverlayAnchor.BottomLeft => e.X,
                OverlayAnchor.TopRight or OverlayAnchor.Right or OverlayAnchor.BottomRight => area.Width - size.Width - e.X,
                _ => (area.Width - size.Width) / 2 + e.X,
            };
            var y = e.Anchor switch {
                OverlayAnchor.TopLeft or OverlayAnchor.Top or OverlayAnchor.TopRight => e.Y,
                OverlayAnchor.BottomLeft or OverlayAnchor.Bottom or OverlayAnchor.BottomRight => area.Height - size.Height - e.Y,
                _ => (area.Height - size.Height) / 2 + e.Y,
            };

            return new Rect(x, y, size.Width, size.Height);
        }

        // Moved the shortest way off anything it laps over, a few rounds, so moving off one doesn't leave it on another.
        private static Rect Clear(Rect rect, double spacing, List<(Rect Rect, double Gap, bool Credit)> placed, Rect bounds) {
            for (var round = 0; round < 8; round++) {
                var moved = false;
                foreach (var (other, _, credit) in placed) {
                    var gap = credit ? Math.Max(spacing, OverlayDesign.CREDIT_CLEARANCE) : spacing;
                    if (gap <= 0 || !rect.Intersects(other.Inflate(gap - 0.01))) {
                        continue;
                    }

                    // The four ways out, the shortest first, preferring one that stays on the window.
                    var ways = new[] {
                        new Vector(other.Left - gap - rect.Right, 0), new Vector(other.Right + gap - rect.Left, 0),
                        new Vector(0, other.Top - gap - rect.Bottom), new Vector(0, other.Bottom + gap - rect.Top),
                    };
                    var best = ways.OrderBy(v => bounds.Contains(rect.Translate(v)) ? 0 : 1).ThenBy(v => Math.Abs(v.X) + Math.Abs(v.Y)).First();
                    rect = rect.Translate(best);
                    moved = true;
                }

                if (!moved) {
                    break;
                }
            }

            return rect;
        }

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
            Tag = e,
        };

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

        // The designer's outline around the one being changed.
        return new Panel {
            Tag = e,
            Children = {
                border,
                new Border {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)), BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(Math.Max(2, e.CornerRadius)), Margin = new Thickness(-3), IsHitTestVisible = false,
                    RenderTransform = border.RenderTransform, RenderTransformOrigin = RelativePoint.Center,
                },
            },
        };
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
