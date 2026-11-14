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
using Avalonia.Media.Imaging;

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
    /// The whole overlay, filling a game window of this size (in device-independent pixels), in three parts (Niduus,
    /// 2026-11-11: "Ahziael is always loaded first and always in it's designated position first ... Server always loads
    /// first and positions itself first. Client loads second"): the Ahziael line, then the server's texts and boxes
    /// (<paramref name="server"/>), then the player's own. Each part is placed around the ones before it and drawn under
    /// them, so the player's never cover the server's and nothing covers the Ahziael line.
    /// </summary>
    internal static Control Build(OverlayDesign design, Words words, OverlayElement? highlight = null, bool showMine = true,
        IReadOnlyList<OverlayElement>? server = null, long window = 0) {
        var panel = new OverlayPanel { Background = Brushes.Transparent, ClipToBounds = true };
        // A Quest Tracker's picked quest shows its way on a minimap only, so it needs to know if one is drawn.
        s_buildHasMap = (showMine && HasMinimap(design.Elements)) || HasMinimap(server ?? []);
        // The Ultimus Spectator needs the Minimap add-on downloaded, not on the overlay.
        s_buildMinimapAddon = OverlayAddon.IsInstalled(OverlayHost.InstallFolder, "Minimap");
        if (window == 0) {
            OverlayQuestData.DesignHasTracker = (showMine && HasQuestTracker(design.Elements)) || HasQuestTracker(server ?? []);
        }

        // Grey smoothing for words: coloured ClearType smoothing leaves dark streaks on a see-through overlay.
        RenderOptions.SetTextRenderingMode(panel, TextRenderingMode.Antialias);
        // Hidden, the player's own pieces go, except a button that shows them again (or it could never come back).
        Add(panel, showMine ? design.Elements : design.Elements.Where(e => e.IsButton && e.Action == OverlayAction.TOGGLE_MINE),
            words, highlight, OverlayPanel.CLIENT, window);

        Add(panel, server ?? [], words, highlight, OverlayPanel.SERVER, window);
        var credit = Element(design.Credit, words, design.Credit == highlight, OverlayDesign.CreditLine(words.Version));
        credit.Classes.Add(OverlayPanel.CREDIT);
        panel.Children.Add(credit);
        if (window != 0) {
            AddMapButtons(panel, window);
        }

        return panel;
    }

    /// <summary>
    /// Over a game: a small full screen button on the minimap's corner (Niduus, 2026-11-12: "Allow a full screen button"),
    /// and while it is full screen, the big map with X (back to the corner), + (closer) and - (farther). They go under the
    /// Ahziael line, over everything else, and never push anything aside.
    /// </summary>
    private static void AddMapButtons(Panel panel, long window) {
        if (panel.Children.FirstOrDefault(c => c.Tag is OverlayElement { IsMinimap: true }) is not { Tag: OverlayElement minimap } corner) {
            return;
        }

        var at = panel.Children.Count - 1;
        if (OverlayMinimapData.FullZoom(window) is null) {
            var slot = 0;
            if (minimap.Map?.FullButton != false) {
                panel.Children.Insert(at++, new MapButton(corner, slot++, OverlayAction.MAP_FULL, minimap));
            }

            // + and - on the corner minimap too (Niduus, 2026-11-12: "custom zoom options").
            if (minimap.Map?.ZoomButtons != false) {
                panel.Children.Insert(at++, new MapButton(corner, slot++, OverlayAction.MAP_IN, minimap));
                panel.Children.Insert(at, new MapButton(corner, slot, OverlayAction.MAP_OUT, minimap));
            }


            return;
        }

        // The minimap in the corner stays where it is (so nothing else moves) but isn't seen under the big one.
        corner.Opacity = 0;
        var full = new FullMap { Child = new MinimapView(minimap, OverlayMinimapData.For(window), window, full: true), Space = minimap.Map?.FullMargin ?? FullMap.MARGIN };
        full.Classes.Add(OverlayPanel.FULL);
        panel.Children.Insert(at, full);
        panel.Children.Insert(at + 1, new MapButton(full, 0, OverlayAction.MAP_FULL, minimap));
        panel.Children.Insert(at + 2, new MapButton(full, 1, OverlayAction.MAP_IN, minimap));
        panel.Children.Insert(at + 3, new MapButton(full, 2, OverlayAction.MAP_OUT, minimap));
    }

    /// <summary>The full screen map: it fills the game window, less a margin, whatever its design.</summary>
    internal sealed class FullMap : Border {

        internal const double MARGIN = 36;

        /// <summary>Space round it (the minimap's Full screen margin).</summary>
        internal double Space { get; init; } = MARGIN;

    }

    /// <summary>
    /// One of the minimap's own buttons, on the top right corner of <see cref="Over"/> (<see cref="Slot"/> 0 is the
    /// right-most). Its tag is a button piece, so the overlay puts a click window over it like any other button.
    /// </summary>
    internal sealed class MapButton : Border {

        internal const double SIZE = 22;

        internal Control Over { get; }

        internal int Slot { get; }

        internal MapButton(Control over, int slot, string action, OverlayElement minimap) {
            Over = over;
            Slot = slot;
            Width = Height = SIZE;
            CornerRadius = new CornerRadius(5);
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x14, 0x24));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1);
            Tag = new OverlayElement { Kind = OverlayElement.BUTTON, Action = action, Map = minimap.Map, Visible = true };
            Child = new MapIcon(action == OverlayAction.MAP_FULL && over is FullMap ? "close" : action);
            Classes.Add(OverlayPanel.FULL);
        }

    }

    // The drawing on a minimap button: corners (full screen), X (close), + and -.
    private sealed class MapIcon(string what) : Control {

        public override void Render(DrawingContext context) {
            var (w, h) = (Bounds.Width, Bounds.Height);
            var pen = new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round);
            var (l, t, r, b) = (w * 0.27, h * 0.27, w * 0.73, h * 0.73);
            switch (what) {
                case OverlayAction.MAP_FULL:
                    var k = w * 0.14;
                    foreach (var (x, y, dx, dy) in new[] { (l, t, 1, 1), (r, t, -1, 1), (l, b, 1, -1), (r, b, -1, -1) }) {
                        context.DrawLine(pen, new Point(x, y), new Point(x + dx * k, y));
                        context.DrawLine(pen, new Point(x, y), new Point(x, y + dy * k));
                    }

                    break;
                case "close":
                    context.DrawLine(pen, new Point(l, t), new Point(r, b));
                    context.DrawLine(pen, new Point(r, t), new Point(l, b));
                    break;
                case OverlayAction.MAP_IN:
                    context.DrawLine(pen, new Point(l, h / 2), new Point(r, h / 2));
                    context.DrawLine(pen, new Point(w / 2, t), new Point(w / 2, b));
                    break;
                default:
                    context.DrawLine(pen, new Point(l, h / 2), new Point(r, h / 2));
                    break;
            }
        }

    }

    // Lowest layer first (same layer: list order), so the higher one is drawn over.
    // A child is drawn over its parent (at least its parent's layer) and only while its parent shows.
    private static void Add(Panel panel, IEnumerable<OverlayElement> elements, Words words, OverlayElement? highlight, string part, long window) {
        var list = elements.ToList();
        var byId = list.Where(e => e.Id.Length > 0).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        (bool Shown, int Layer, int Depth) Family(OverlayElement e) {
            var (shown, layer, depth) = (e.Visible, e.Layer, 0);
            var at = e;
            for (var guard = 0; guard < OverlayDesign.MAX_ELEMENTS && at.Parent.Length > 0 && byId.TryGetValue(at.Parent, out var up); guard++) {
                shown &= up.Visible;
                layer = Math.Max(layer, up.Layer);
                depth++;
                at = up;
            }

            return (shown, layer, depth);
        }

        foreach (var element in list.Select((e, i) => (e, i, f: Family(e))).Where(p => p.f.Shown)
                     .OrderBy(p => p.f.Layer).ThenBy(p => p.f.Depth).ThenBy(p => p.i).Select(p => p.e)) {
            var control = Element(element, words, element == highlight, window: window);
            control.Classes.Add(part);
            panel.Children.Add(control);
        }
    }

    /// <summary>Which part a drawn text or box belongs to.</summary>
    internal static string PartOf(Control control)
        => control.Classes.Contains(OverlayPanel.CREDIT) ? OverlayPanel.CREDIT : control.Classes.Contains(OverlayPanel.SERVER) ? OverlayPanel.SERVER
            : control.Classes.Contains(OverlayPanel.FULL) ? OverlayPanel.FULL : OverlayPanel.CLIENT;

    /// <summary>
    /// Places each text and box from its corner or side, then moves any that laps over one placed before it off that
    /// one, leaving its Spacing between them (Niduus, 2026-11-11: "Introduce layers and spacing as well so if a UI is
    /// lapping over another, the spacing allows it to move the overlaying UI over some = to the spacing value"). The
    /// Ahziael line is placed first and never moves, then the server's, then the player's; a later part always keeps
    /// clear of an earlier one.
    /// </summary>
    internal sealed class OverlayPanel : Panel {

        internal const string CREDIT = "credit";
        internal const string SERVER = "server";
        internal const string CLIENT = "client";

        /// <summary>The minimap's own buttons and its full screen map: placed last, and never moved or moving anything.</summary>
        internal const string FULL = "fullmap";

        private static int Rank(string part) => part switch { CREDIT => 0, SERVER => 1, FULL => 3, _ => 2 };

        protected override Size MeasureOverride(Size available) {
            foreach (var child in Children) {
                child.Measure(Size.Infinity);
            }

            return available;
        }

        protected override Size ArrangeOverride(Size final) {
            var bounds = new Rect(final);
            // The Ahziael line, the server's, the player's; within each, from the lowest layer up: the one that laps
            // over another (drawn over it) is the one that moves.
            var order = Children.Select((c, i) => (c, i)).OrderBy(p => Rank(PartOf(p.c))).ThenBy(p => p.i).Select(p => p.c).ToList();
            var placed = new List<(Rect Rect, int Rank, string Part, string Id)>();
            var where = new Dictionary<Control, Rect>();
            // Parents by part and Id: a child is placed from its parent's place, so the parent goes first.
            var parents = new Dictionary<(string, string), Control>();
            foreach (var child in order) {
                if (child.Tag is OverlayElement { Id.Length: > 0 } tagged) {
                    parents.TryAdd((PartOf(child), tagged.Id), child);
                }
            }

            void Place(Control child, int depth) {
                if (where.ContainsKey(child)) {
                    return;
                }

                if (child is FullMap) {
                    var inside = bounds.Deflate(Math.Min(((FullMap) child).Space, Math.Min(bounds.Width, bounds.Height) / 3));
                    where[child] = inside;
                    child.Arrange(inside);
                    return;
                }

                if (child is MapButton button) {
                    Place(button.Over, depth + 1);
                    var over = where.GetValueOrDefault(button.Over, button.Over.Bounds);
                    var size = MapButton.SIZE;
                    var spot = new Rect(over.Right - ((button.Slot + 1) * (size + 4)), over.Top + 4, size, size);
                    where[child] = spot;
                    child.Arrange(spot);
                    return;
                }

                if (child.Tag is not OverlayElement e) {
                    child.Arrange(new Rect(child.DesiredSize));
                    where[child] = new Rect(child.DesiredSize);
                    return;
                }

                var part = PartOf(child);
                var area = bounds;
                if (e.Parent.Length > 0 && depth < OverlayDesign.MAX_ELEMENTS && parents.TryGetValue((part, e.Parent), out var parent)) {
                    Place(parent, depth + 1);
                    area = where[parent];
                }

                var rank = Rank(part);
                var rect = Inside(Start(e, child.DesiredSize, area), bounds);
                if (rank > 0) {
                    // Never pushed off its own parents (a child sits on them).
                    var family = Ancestors(e, part);
                    var others = placed.Where(p => !(p.Part == part && family.Contains(p.Id))).Select(p => (p.Rect, p.Rank)).ToList();
                    rect = Inside(Clear(rect, e.Spacing, rank, others, bounds), bounds);
                }

                placed.Add((rect, rank, part, e.Id));
                where[child] = rect;
                child.Arrange(rect);
            }

            HashSet<string> Ancestors(OverlayElement e, string part) {
                var ids = new HashSet<string>();
                var at = e;
                while (at.Parent.Length > 0 && ids.Add(at.Parent) && parents.TryGetValue((part, at.Parent), out var up) && up.Tag is OverlayElement next) {
                    at = next;
                }

                return ids;
            }

            foreach (var child in order) {
                Place(child, 0);
            }

            return final;
        }

        /// <summary>Where each drawn piece ended up, after placing and moving: for the designers.</summary>
        internal static Rect AreaOf(Control child) => child.Bounds;

        // Always inside the game window (Niduus, 2026-11-11: "only be able to be placed within the regions of the client UI
        // size"): one that would stick out is moved in; one bigger than the window starts at its top left.
        private static Rect Inside(Rect rect, Rect bounds) => new(
            Math.Max(bounds.Left, Math.Min(rect.X, bounds.Right - rect.Width)),
            Math.Max(bounds.Top, Math.Min(rect.Y, bounds.Bottom - rect.Height)), rect.Width, rect.Height);

        // Where its corner or side and X and Y put it.
        // Where its corner or side and X and Y put it, in the window or in its parent.
        internal static Rect Start(OverlayElement e, Size size, Rect area) {
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

            return new Rect(area.X + x, area.Y + y, size.Width, size.Height);
        }

        // Moved the shortest way off anything it laps over, a few rounds, so moving off one doesn't leave it on another.
        private static Rect Clear(Rect rect, double spacing, int rank, List<(Rect Rect, int Rank)> placed, Rect bounds) {
            for (var round = 0; round < 8; round++) {
                var moved = false;
                foreach (var (other, otherRank) in placed) {
                    var gap = otherRank < rank ? Math.Max(spacing, OverlayDesign.CREDIT_CLEARANCE) : spacing;
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

    /// <summary>Whether a minimap shows in this design (the overlay then asks the server for the map).</summary>
    internal static bool HasMinimap(IEnumerable<OverlayElement> elements) => elements.Any(e => e.IsMinimap && e.Visible);

    /// <summary>Whether an Ultimus Spectator piece shows in this design (the overlay then asks the server for Ultimus, if it has a Minimap too).</summary>
    internal static bool HasUltimus(IEnumerable<OverlayElement> elements) => elements.Any(e => e.IsUltimus && e.Visible);

    /// <summary>Whether a Quest Tracker shows in this design (the overlay then asks the server for the quests).</summary>
    internal static bool HasQuestTracker(IEnumerable<OverlayElement> elements) => elements.Any(e => e.IsQuestTracker && e.Visible);

    // Whether the overlay being built has a minimap (for its Quest Trackers' hint).
    private static bool s_buildHasMap;

    // Whether the Minimap add-on is downloaded (the Ultimus Spectator needs it installed to work).
    private static bool s_buildMinimapAddon;

    private static Control Element(OverlayElement e, Words words, bool highlight, string? fixedText = null, long window = 0) {
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

        if (e.IsMinimap) {
            // The minimap draws its own background and edge (round or square), and the quest lines under it.
            var map = new MinimapView(e, OverlayMinimapData.For(window), window);
            border.Background = null;
            border.BorderThickness = default;
            border.Padding = default;
            border.CornerRadius = default;
            border.Child = map;
            border.Width = map.Width;
            border.Height = map.Height;
        }

        if (e.IsUltimusMap) {
            // Ultimus's own minimap: the Minimap's drawing, centred on Ultimus, with his quest's way (never the player's).
            // It needs the Minimap add-on downloaded (Niduus, 2026-11-14: required "for install, for Spectate to work"),
            // not the player's Minimap on the overlay.
            var key = window == 0 ? 0 : OverlayUltimusData.KEY;
            border.Background = null;
            border.BorderThickness = default;
            border.Padding = default;
            border.CornerRadius = default;
            if (s_buildMinimapAddon) {
                var map = new MinimapView(e, OverlayMinimapData.For(key), key);
                border.Child = map;
                border.Width = map.Width;
                border.Height = map.Height;
            }
            else {
                border.Background = Brush(e.Background) ?? new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x14, 0x24));
                border.Padding = new Thickness(10);
                border.CornerRadius = new CornerRadius(10);
                border.Width = e.Width > 0 ? e.Width : 200;
                border.Child = new TextBlock { Text = UltimusViews.NEEDS_MINIMAP, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.White };
            }
        }

        if (e.Kind == OverlayElement.ULTIMUS_QUEST) {
            border.Child = UltimusViews.Quest(e, window, s_buildMinimapAddon);
            border.ClipToBounds = true;
            if (e.Width <= 0) {
                border.Width = 240;
            }
        }

        if (e.Kind == OverlayElement.ULTIMUS_SPELLS) {
            // Over a game it shows only while Ultimus is in a battle; the designers always show a made-up battle.
            if (UltimusViews.Spells(e, window, s_buildMinimapAddon) is not { } spells) {
                border.IsVisible = false;

                return border;
            }

            border.Child = spells;
            border.ClipToBounds = true;
            if (e.Width <= 0) {
                border.Width = 300;
            }
        }

        if (e.IsBotAlerts) {
            // Over a game the alert itself is drawn by OverlayHost.WithNotice, at the top in the middle; in the designer a
            // sample shows where and how.
            if (window != 0) {
                border.IsVisible = false;

                return border;
            }

            fixedText ??= "Quest Team and Ultimus alerts show here, at the top in the middle, with an Acknowledge button.";
            if (e.Width <= 0) {
                border.Width = 600;
            }
        }

        if (e.IsQuestTracker) {
            // The tracker draws its quests inside the box's own background, edge and padding; with no height it grows to fit.
            border.Child = new QuestTrackerView(e, window, s_buildHasMap);
            border.ClipToBounds = true;
            if (e.Width <= 0) {
                border.Width = 260;
            }
        }

        if (e.IsPicture && Picture(e.Picture) is { } bitmap) {
            border.Child = new Image {
                Source = bitmap,
                Stretch = e.PictureFit switch { "Fill" => Stretch.UniformToFill, "Stretch" => Stretch.Fill, _ => Stretch.Uniform },
            };
            border.ClipToBounds = true;
            if (e.Width <= 0) {
                border.Width = Math.Min(bitmap.Size.Width, 400);
            }

            if (e.Height <= 0) {
                border.Height = border.Width * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width);
            }
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

            // Words that don't fit a button, or a text given both a width and a height, shrink until they all show
            // (Niduus, 2026-11-11: "text should shrink to size so it always shows what it's intention is"). Never bigger.
            var shrink = (e.IsButton && (e.Width > 0 || e.Height > 0)) || (e.Width > 0 && e.Height > 0);
            if (shrink) {
                text.TextWrapping = e.IsButton ? TextWrapping.NoWrap : TextWrapping.Wrap;
                if (!e.IsButton) {
                    // Wrapped at the box's width, then made smaller only if the lines still don't fit its height.
                    text.MaxWidth = Math.Max(1, e.Width - 2 * e.Padding - 2 * e.BorderWidth);
                }

                border.Child = new Viewbox {
                    Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = text,
                    HorizontalAlignment = e.IsButton ? HorizontalAlignment.Center : e.Align switch {
                        "Center" => HorizontalAlignment.Center, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Left,
                    },
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else {
                border.Child = text;
            }
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

    private static readonly Dictionary<string, Bitmap?> s_pictures = [];

    /// <summary>A picture from its file's base64 (made once and kept); null when it can't be read.</summary>
    internal static Bitmap? Picture(string base64) {
        if (string.IsNullOrEmpty(base64)) {
            return null;
        }

        lock (s_pictures) {
            if (s_pictures.TryGetValue(base64, out var known)) {
                return known;
            }

            if (s_pictures.Count > 64) {
                s_pictures.Clear();
            }

            Bitmap? bitmap = null;
            try {
                using var stream = new System.IO.MemoryStream(Convert.FromBase64String(base64));
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.IO.IOException or NotSupportedException) {
            }

            return s_pictures[base64] = bitmap;
        }
    }

    internal static Color? Parse(string text) => !string.IsNullOrWhiteSpace(text) && Color.TryParse(text, out var color) ? color : null;

}
