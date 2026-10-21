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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Imlight.Setup;

/// <summary>
/// One of the game's bitmap fonts (Fonts/*.xml in Root.wad): where each letter sits on the font's picture pages and
/// how far it moves the pen. The card's name, cost and description are drawn with these, as in the game.
/// </summary>
internal sealed class GlyphFont {

    internal readonly record struct Glyph(int A, int B, int C, int Page, int Top, int Bottom, int Left, int Right);

    private readonly Dictionary<int, Glyph> _glyphs = [];
    private readonly List<GameImage?> _pages = [];

    /// <summary>The height of a letter's cell on the page, in the page's pixels.</summary>
    internal int CellHeight { get; private set; } = 1;

    internal static GlyphFont? Read(byte[] xml, Func<string, GameImage?> page) {
        try {
            var doc = XDocument.Load(new MemoryStream(xml));
            var font = new GlyphFont();
            foreach (var p in doc.Descendants("Page")) {
                font._pages.Add(page((string?) p.Attribute("FileName") ?? ""));
            }

            foreach (var g in doc.Descendants("Glyph")) {
                int Get(string name) => int.Parse((string?) g.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
                font._glyphs[Get("ID")] = new Glyph(Get("A"), Get("B"), Get("C"), Get("Page"), Get("Top"), Get("Bottom"), Get("Left"), Get("Right"));
            }

            if (font._glyphs.TryGetValue('A', out var a)) {
                font.CellHeight = a.Bottom - a.Top;
            }

            return font._pages.Any(p => p is not null) && font._glyphs.Count > 0 ? font : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private Glyph? Find(char c) => _glyphs.TryGetValue(c, out var g) ? g : _glyphs.TryGetValue('?', out var q) ? q : null;

    /// <summary>How wide the text is at a scale of 1 page pixel to <paramref name="scale"/> card pixels.</summary>
    internal float Measure(string text, float scale) => text.Sum(c => Find(c) is { } g ? (g.A + g.B + g.C) * scale : 0);

    /// <summary>Draws the text with its top left at x, y; returns how far the pen moved.</summary>
    internal float Draw(CardCanvas canvas, string text, float x, float y, float scale, uint color) {
        var start = x;
        foreach (var c in text) {
            if (Find(c) is not { } g) {
                continue;
            }

            if (g.Page < _pages.Count && _pages[g.Page] is { } page && g.Right > g.Left && g.Bottom > g.Top) {
                // Each letter's box on the page has a pixel spare on either side of its ink.
                canvas.DrawImage(page, g.Left, g.Top, g.Right - g.Left, g.Bottom - g.Top,
                    x + (g.A - 1) * scale, y, (g.Right - g.Left) * scale, (g.Bottom - g.Top) * scale, color);
            }

            x += (g.A + g.B + g.C) * scale;
        }

        return x - start;
    }

}

/// <summary>
/// A picture being drawn: premultiplied red, green, blue and alpha for each pixel, with smooth scaling.
/// </summary>
internal sealed class CardCanvas(int width, int height) {

    private readonly float[] _p = new float[width * height * 4];

    // What has been drawn on so far, so a text layer's edge is only worked out where there is text.
    private int _left = int.MaxValue, _top = int.MaxValue, _right = -1, _bottom = -1;

    internal int Width { get; } = width;

    internal int Height { get; } = height;

    /// <summary>
    /// Draws part of a picture (sx, sy, sw, sh in its pixels) into a box on the canvas, tinted by an ARGB colour.
    /// </summary>
    internal void DrawImage(GameImage image, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh,
            uint tint = 0xFFFFFFFF, float opacity = 1) {
        if (dw <= 0 || dh <= 0 || sw <= 0 || sh <= 0) {
            return;
        }

        var tr = ((tint >> 16) & 0xFF) / 255f;
        var tg = ((tint >> 8) & 0xFF) / 255f;
        var tb = (tint & 0xFF) / 255f;
        var ta = ((tint >> 24) & 0xFF) / 255f * opacity;

        // A few samples a pixel, more when shrinking, so edges and small text stay smooth.
        var n = Math.Clamp((int) Math.Ceiling(Math.Max(sw / dw, sh / dh)), 2, 4);
        var x0 = Math.Max(0, (int) Math.Floor(dx));
        var y0 = Math.Max(0, (int) Math.Floor(dy));
        var x1 = Math.Min(Width, (int) Math.Ceiling(dx + dw));
        var y1 = Math.Min(Height, (int) Math.Ceiling(dy + dh));
        var px = image.Pixels;
        var maxX = Math.Min(image.Width - 1, sx + sw - 0.5f);
        var maxY = Math.Min(image.Height - 1, sy + sh - 0.5f);
        var minX = Math.Max(0, sx);
        var minY = Math.Max(0, sy);
        for (var y = y0; y < y1; y++) {
            for (var x = x0; x < x1; x++) {
                float r = 0, g = 0, b = 0, a = 0;
                for (var j = 0; j < n; j++) {
                    var cy = y + (j + 0.5f) / n;
                    if (cy < dy || cy >= dy + dh) {
                        continue;
                    }

                    var v = Math.Clamp(sy + (cy - dy) / dh * sh - 0.5f, minY, maxY);
                    for (var i = 0; i < n; i++) {
                        var cx = x + (i + 0.5f) / n;
                        if (cx < dx || cx >= dx + dw) {
                            continue;
                        }

                        var u = Math.Clamp(sx + (cx - dx) / dw * sw - 0.5f, minX, maxX);
                        Sample(px, image.Width, image.Height, u, v, ref r, ref g, ref b, ref a);
                    }
                }

                var k = 1f / (n * n);
                Blend(x, y, r * k * tr * ta, g * k * tg * ta, b * k * tb * ta, a * k * ta);
            }
        }
    }

    private static void Sample(byte[] px, int w, int h, float u, float v, ref float r, ref float g, ref float b, ref float a) {
        var ix = (int) Math.Floor(u);
        var iy = (int) Math.Floor(v);
        var fx = u - ix;
        var fy = v - iy;
        for (var k = 0; k < 4; k++) {
            var xx = Math.Clamp(ix + (k & 1), 0, w - 1);
            var yy = Math.Clamp(iy + (k >> 1), 0, h - 1);
            var weight = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
            var o = (yy * w + xx) * 4;
            var alpha = px[o + 3] / 255f * weight;
            r += px[o] / 255f * alpha;
            g += px[o + 1] / 255f * alpha;
            b += px[o + 2] / 255f * alpha;
            a += alpha;
        }
    }

    private void Blend(int x, int y, float r, float g, float b, float a) {
        if (a <= 0) {
            return;
        }

        _left = Math.Min(_left, x);
        _right = Math.Max(_right, x);
        _top = Math.Min(_top, y);
        _bottom = Math.Max(_bottom, y);
        var o = (y * Width + x) * 4;
        var keep = 1 - a;
        _p[o] = r + _p[o] * keep;
        _p[o + 1] = g + _p[o + 1] * keep;
        _p[o + 2] = b + _p[o + 2] * keep;
        _p[o + 3] = a + _p[o + 3] * keep;
    }

    /// <summary>Fills a rounded box with an ARGB colour.</summary>
    internal void FillRect(float dx, float dy, float dw, float dh, uint color, float radius = 0) {
        var (r, g, b, a) = Unpack(color);
        for (var y = Math.Max(0, (int) dy); y < Math.Min(Height, (int) Math.Ceiling(dy + dh)); y++) {
            for (var x = Math.Max(0, (int) dx); x < Math.Min(Width, (int) Math.Ceiling(dx + dw)); x++) {
                var cover = Cover(x + 0.5f, y + 0.5f, dx, dy, dw, dh, radius);
                Blend(x, y, r * a * cover, g * a * cover, b * a * cover, a * cover);
            }
        }
    }

    /// <summary>Fills a circle with an ARGB colour.</summary>
    internal void FillCircle(float cx, float cy, float radius, uint color) => FillRect(cx - radius, cy - radius, radius * 2, radius * 2, color, radius);

    private static float Cover(float x, float y, float dx, float dy, float dw, float dh, float radius) {
        var inside = Math.Min(Math.Min(x - dx, dx + dw - x), Math.Min(y - dy, dy + dh - y));
        if (radius > 0) {
            var qx = Math.Max(Math.Max(dx + radius - x, x - (dx + dw - radius)), 0);
            var qy = Math.Max(Math.Max(dy + radius - y, y - (dy + dh - radius)), 0);
            if (qx > 0 && qy > 0) {
                inside = radius - MathF.Sqrt(qx * qx + qy * qy);
            }
            else if (qx > 0 || qy > 0) {
                inside = Math.Min(inside, radius - Math.Max(qx, qy));
            }
        }

        return Math.Clamp(inside + 0.5f, 0, 1);
    }

    private static (float R, float G, float B, float A) Unpack(uint color)
        => (((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f, ((color >> 24) & 0xFF) / 255f);

    /// <summary>
    /// Draws another canvas on top: first its shape grown by <paramref name="outline"/> pixels in the outline colour,
    /// then the canvas itself. Text drawn this way gets the dark edge the game's card text has.
    /// </summary>
    internal void DrawLayer(CardCanvas layer, float outline, uint outlineColor) {
        if (layer._right < 0) {
            return;
        }

        if (outline > 0) {
            var (r, g, b, a) = Unpack(outlineColor);
            var reach = (int) Math.Ceiling(outline);
            for (var y = Math.Max(0, layer._top - reach); y <= Math.Min(Height - 1, layer._bottom + reach); y++) {
                for (var x = Math.Max(0, layer._left - reach); x <= Math.Min(Width - 1, layer._right + reach); x++) {
                    float most = 0;
                    for (var j = -reach; j <= reach && most < 1; j++) {
                        var yy = y + j;
                        if (yy < 0 || yy >= Height) {
                            continue;
                        }

                        for (var i = -reach; i <= reach; i++) {
                            var xx = x + i;
                            if (xx < 0 || xx >= Width) {
                                continue;
                            }

                            var falloff = Math.Clamp(outline + 0.5f - MathF.Sqrt(i * i + j * j), 0, 1);
                            most = Math.Max(most, layer._p[(yy * Width + xx) * 4 + 3] * falloff);
                        }
                    }

                    if (most > 0) {
                        Blend(x, y, r * a * most, g * a * most, b * a * most, a * most);
                    }
                }
            }
        }

        for (var o = 0; o < _p.Length; o += 4) {
            var a = layer._p[o + 3];
            if (a > 0) {
                var keep = 1 - a;
                _p[o] = layer._p[o] + _p[o] * keep;
                _p[o + 1] = layer._p[o + 1] + _p[o + 1] * keep;
                _p[o + 2] = layer._p[o + 2] + _p[o + 2] * keep;
                _p[o + 3] = a + _p[o + 3] * keep;
            }
        }
    }

    /// <summary>The finished picture, not premultiplied.</summary>
    internal GameImage ToImage() {
        var bytes = new byte[Width * Height * 4];
        for (var o = 0; o < _p.Length; o += 4) {
            var a = Math.Clamp(_p[o + 3], 0, 1);
            if (a <= 0) {
                continue;
            }

            bytes[o] = (byte) Math.Clamp(_p[o] / a * 255 + 0.5f, 0, 255);
            bytes[o + 1] = (byte) Math.Clamp(_p[o + 1] / a * 255 + 0.5f, 0, 255);
            bytes[o + 2] = (byte) Math.Clamp(_p[o + 2] / a * 255 + 0.5f, 0, 255);
            bytes[o + 3] = (byte) (a * 255 + 0.5f);
        }

        return new GameImage(Width, Height, bytes);
    }

}

/// <summary>
/// Draws a spell's card the way the game does (Justin, 2026-10-21: "previewing what the card looks like in game"):
/// the school's card frame from GUI/Cards, the spell's art behind it, the name across the top in the game's card font,
/// the pip cost in the triangle, the school icon in the circle, the accuracy over the target, the spell type icon in
/// the square and the description on the parchment, with the game's own small icons. A piece that is not in the
/// game files is drawn as a plain stand-in.
/// </summary>
internal static class CardPainter {

    /// <summary>The card frame's size in the game's pixels; the card is drawn this many times bigger.</summary>
    internal const int CARD_WIDTH = 128;
    internal const int CARD_HEIGHT = 196;
    internal const int SCALE = 3;

    private const uint TEXT_OUTLINE = 0xFF1E1208;

    private static readonly Dictionary<string, uint> s_schoolColors = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = 0xFFC0201A, ["Ice"] = 0xFF2C8FD8, ["Storm"] = 0xFF8E2BA8, ["Myth"] = 0xFFD8C01E, ["Life"] = 0xFF2E9E3A,
        ["Death"] = 0xFF505050, ["Balance"] = 0xFFC8B89A, ["Star"] = 0xFF6E6ED8, ["Sun"] = 0xFFE09A28, ["Moon"] = 0xFF8A7CC8,
        ["Shadow"] = 0xFF2A2A30,
    };

    /// <summary>The colour of a school, for list rows and stand-ins.</summary>
    internal static uint SchoolColor(string school) => s_schoolColors.TryGetValue(school, out var color) ? color : 0xFF8A7A62;

    private static float N(float value) => value * SCALE;

    internal static GameImage Paint(SpellBook book, SpellCard card) {
        var canvas = new CardCanvas(CARD_WIDTH * SCALE, CARD_HEIGHT * SCALE);
        var fancy = book.Font("WizardFancy.xml") ?? book.Font("SpellTitle_sm.xml");
        var normal = book.Font("WizardNormal.xml") ?? book.Font("SpellDescription.xml");

        // The art sits behind the frame's window.
        canvas.FillRect(N(7), N(23), N(114), N(101), 0xFF2A1C12);
        if (book.Image(card.ImageName) is { } art) {
            Cover(canvas, art, N(7), N(23), N(114), N(101));
        }
        else if (book.SchoolIcon(card.School) is { } schoolArt && book.Image(schoolArt) is { } big) {
            Fit(canvas, big, N(64), N(70), N(56), 0.35f);
        }

        var frame = Frame(book, card.School);
        if (frame is not null) {
            canvas.DrawImage(frame, 0, 0, CARD_WIDTH, CARD_HEIGHT, 0, 0, canvas.Width, canvas.Height);
        }
        else {
            DrawStandInFrame(canvas, SchoolColor(card.School));
        }

        // School icon in the round socket, type icon in the square one.
        if (book.SchoolIcon(card.School) is { } schoolIcon && book.Image(schoolIcon) is { } school) {
            Fit(canvas, school, N(111), N(39), N(19));
        }
        else if (fancy is not null && card.School.Length > 0) {
            Outlined(canvas, fancy, card.School[..1].ToUpperInvariant(), N(111), N(31), N(14), 0xFFFFFFFF, center: true);
        }

        if (book.Image($"Type_{card.Type}.dds") is { } type) {
            Fit(canvas, type, N(111.5f), N(110.5f), N(20));
        }
        else if (normal is not null && card.Type.Length > 0) {
            var word = card.Type.Replace('_', ' ');
            var size = Math.Min(N(9), N(21) / Math.Max(1, normal.Measure(word, 1)) * normal.CellHeight);
            Outlined(canvas, normal, word, N(111.5f), N(110.5f) - size / 2, size, 0xFFFFFFFF, center: true, outline: 0.5f);
        }

        if (normal is not null) {
            // Pip cost in the triangle; school and shadow pips as small pips under it.
            Outlined(canvas, normal, card.XPips ? "X" : card.Pips.ToString(CultureInfo.InvariantCulture), N(17), N(27), N(15), 0xFFFFFFFF, center: true);
            var pipY = 52f;
            foreach (var (pipSchool, count) in card.SchoolPips) {
                for (var i = 0; i < count; i++, pipY += 9) {
                    Pip(book, canvas, book.PipIcon(pipSchool), SchoolColor(pipSchool), pipY);
                }
            }

            for (var i = 0; i < card.ShadowPips; i++, pipY += 9) {
                Pip(book, canvas, "Icon_Shadow_Pip.dds", 0xFF3A2A50, pipY);
            }

            Outlined(canvas, normal, $"{card.Accuracy}%", N(2), N(99), N(12), 0xFFFFF2A0);
            if (card.Treasure) {
                Outlined(canvas, normal, "x1", N(120), N(70), N(19), 0xFFFFFFFF, right: true);
            }
        }

        if (fancy is not null) {
            Title(canvas, fancy, card.Name.ToUpperInvariant());
        }

        if (normal is not null) {
            Description(book, canvas, normal, card);
        }

        return canvas.ToImage();
    }

    private static GameImage? Frame(SpellBook book, string school) {
        if (book.Image($"School_{school}_Blnk.dds") is { } own) {
            return own;
        }

        // Schools without a frame of their own in the game files.
        return school.ToLowerInvariant() switch {
            "shadow" => book.Image("School_BW_Blnk.dds"),
            _ => book.Image("School_Item_Blnk.dds"),
        };
    }

    private static void DrawStandInFrame(CardCanvas canvas, uint color) {
        // The school's colour around the art window, and a parchment box under it.
        canvas.FillRect(N(1), N(1), N(126), N(24), color, N(4));
        canvas.FillRect(N(1), N(24), N(8), N(100), color);
        canvas.FillRect(N(119), N(24), N(8), N(100), color);
        canvas.FillRect(N(1), N(121), N(126), N(74), color, N(4));
        canvas.FillRect(N(7), N(128), N(114), N(62), 0xFFE2CFA0, N(2));
        canvas.FillCircle(N(111), N(39), N(12), 0xFF8A6A40);
        canvas.FillRect(N(99), N(98), N(25), N(25), 0xFF8A6A40);
        canvas.FillCircle(N(14), N(110), N(12), 0xFF606060);
    }

    /// <summary>Fills a box with a picture, cutting off what does not fit, like the game's art window.</summary>
    private static void Cover(CardCanvas canvas, GameImage image, float x, float y, float w, float h) {
        float sw = image.Width, sh = image.Height;
        if (sw / sh > w / h) {
            sw = sh * w / h;
        }
        else {
            sh = sw * h / w;
        }

        canvas.DrawImage(image, (image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh, x, y, w, h);
    }

    /// <summary>Draws a picture as big as fits in a square around a centre point.</summary>
    private static void Fit(CardCanvas canvas, GameImage image, float cx, float cy, float size, float opacity = 1) {
        var scale = size / Math.Max(image.Width, image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        canvas.DrawImage(image, 0, 0, image.Width, image.Height, cx - w / 2, cy - h / 2, w, h, opacity: opacity);
    }

    private static void Pip(SpellBook book, CardCanvas canvas, string? icon, uint color, float y) {
        if (book.Image(icon) is { } image) {
            Fit(canvas, image, N(9), N(y), N(9));
        }
        else {
            canvas.FillCircle(N(9), N(y), N(3.6f), 0xFF1E1208);
            canvas.FillCircle(N(9), N(y), N(3f), color);
        }
    }

    /// <summary>Text with a dark edge; <paramref name="height"/> is the height of a letter's cell.</summary>
    private static void Outlined(CardCanvas canvas, GlyphFont font, string text, float x, float y, float height, uint color,
            bool center = false, bool right = false, float outline = 0.7f) {
        var scale = height / font.CellHeight;
        var width = font.Measure(text, scale);
        if (center) {
            x -= width / 2;
        }
        else if (right) {
            x -= width;
        }

        var layer = new CardCanvas(canvas.Width, canvas.Height);
        font.Draw(layer, text, x, y, scale, color);
        canvas.DrawLayer(layer, N(outline), TEXT_OUTLINE);
    }

    private static void Title(CardCanvas canvas, GlyphFont font, string name) {
        var height = N(15);
        var scale = height / font.CellHeight;
        var room = N(114);
        if (font.Measure(name, scale) > room) {
            scale = room / font.Measure(name, 1);
            height = scale * font.CellHeight;
        }

        // Centre the letters on the banner, which runs from 3 to 23.
        Outlined(canvas, font, name, canvas.Width / 2f, N(13.5f) - height * 0.5f, height, 0xFFFFFBEA, center: true, outline: 0.75f);
    }

    // ---------------------------------------------------------------------
    // The description on the parchment
    // ---------------------------------------------------------------------

    // Small pictures in the description are this much of a line's height.
    private const float ICON = 0.78f;

    private sealed record Piece(string Text, uint Color, GameImage? Icon, bool Space, bool Break, float Aspect);

    private static void Description(SpellBook book, CardCanvas canvas, GlyphFont font, SpellCard card) {
        var pieces = new List<Piece>();
        foreach (var part in book.Describe(card)) {
            if (part.Break) {
                pieces.Add(new Piece("", 0, null, false, true, 1));
            }
            else if (part.Icon is not null && book.Image(part.Icon) is { } icon) {
                pieces.Add(new Piece("", part.Color, icon, false, false, (float) icon.Width / icon.Height));
            }
            else {
                // Words, or the name of a small picture this copy of the game does not have.
                var words = (part.Icon is null ? part.Text : $" {part.Text} ").Split(' ');
                for (var i = 0; i < words.Length; i++) {
                    if (i > 0) {
                        pieces.Add(new Piece(" ", part.Color, null, true, false, 1));
                    }

                    if (words[i].Length > 0) {
                        pieces.Add(new Piece(words[i], part.Color, null, false, false, 1));
                    }
                }
            }
        }

        if (pieces.Count == 0) {
            return;
        }

        float boxX = N(9), boxY = N(131), boxW = N(110), boxH = N(56);
        var height = N(14);
        List<List<(Piece Piece, float Width)>> lines;
        float step;
        while (true) {
            var scale = height / font.CellHeight;
            step = height * 0.8f;
            lines = Wrap(pieces, font, scale, height, boxW);
            if (lines.Count * step <= boxH || height <= N(6)) {
                break;
            }

            height *= 0.92f;
        }

        var fontScale = height / font.CellHeight;
        var y = boxY + (boxH - lines.Count * step) / 2 - (height - step) / 2;
        foreach (var line in lines) {
            var x = boxX + (boxW - line.Sum(p => p.Width)) / 2;
            foreach (var (piece, width) in line) {
                if (piece.Icon is { } icon) {
                    var iconHeight = height * ICON;
                    canvas.DrawImage(icon, 0, 0, icon.Width, icon.Height, x + width * 0.05f, y + height * 0.5f - iconHeight / 2,
                        iconHeight * piece.Aspect, iconHeight);
                }
                else if (!piece.Space) {
                    font.Draw(canvas, piece.Text, x, y, fontScale, piece.Color);
                }

                x += width;
            }

            y += step;
        }
    }

    private static List<List<(Piece Piece, float Width)>> Wrap(List<Piece> pieces, GlyphFont font, float scale, float height, float room) {
        var lines = new List<List<(Piece, float)>> { new() };
        float used = 0;
        foreach (var piece in pieces) {
            if (piece.Break) {
                lines.Add([]);
                used = 0;
                continue;
            }

            var width = piece.Icon is not null ? height * ICON * piece.Aspect * 1.1f : font.Measure(piece.Text, scale);
            var line = lines[^1];
            if (piece.Space && (line.Count == 0 || line[^1].Item1.Space)) {
                continue;
            }

            if (!piece.Space && used + width > room && line.Count > 0) {
                // Start a new line, leaving the space that ended this one behind.
                while (line.Count > 0 && line[^1].Item1.Space) {
                    used -= line[^1].Item2;
                    line.RemoveAt(line.Count - 1);
                }

                line = [];
                lines.Add(line);
                used = 0;
            }

            line.Add((piece, width));
            used += width;
        }

        foreach (var line in lines) {
            while (line.Count > 0 && line[^1].Item1.Space) {
                line.RemoveAt(line.Count - 1);
            }
        }

        if (lines.Count > 1 && lines[^1].Count == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

}
