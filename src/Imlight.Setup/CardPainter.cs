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
internal sealed class CardCanvas(int width, int height, int originX = 0, int originY = 0) {

    private readonly float[] _p = new float[width * height * 4];

    // What has been drawn on so far, so a text layer's edge is only worked out where there is text.
    private int _left = int.MaxValue, _top = int.MaxValue, _right = -1, _bottom = -1;

    internal int Width { get; } = width;

    internal int Height { get; } = height;

    /// <summary>Where this canvas's top left is in the card it is part of: drawing takes the card's coordinates.</summary>
    internal int OriginX { get; } = originX;

    internal int OriginY { get; } = originY;

    /// <summary>
    /// Draws part of a picture (sx, sy, sw, sh in its pixels) into a box on the canvas, tinted by an ARGB colour.
    /// </summary>
    internal void DrawImage(GameImage image, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh,
            uint tint = 0xFFFFFFFF, float opacity = 1) {
        if (dw <= 0 || dh <= 0 || sw <= 0 || sh <= 0) {
            return;
        }

        dx -= OriginX;
        dy -= OriginY;

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
    /// then the canvas itself. Text drawn this way gets the dark edge the game's card text has. The layer may be a
    /// small piece of this canvas (see <see cref="OriginX"/>), so text costs only the room it takes.
    /// </summary>
    internal void DrawLayer(CardCanvas layer, float outline, uint outlineColor) {
        if (layer._right < 0) {
            return;
        }

        int ox = layer.OriginX - OriginX, oy = layer.OriginY - OriginY;
        float LayerAlpha(int x, int y) {
            var lx = x - ox;
            var ly = y - oy;

            return lx < 0 || ly < 0 || lx >= layer.Width || ly >= layer.Height ? 0 : layer._p[(ly * layer.Width + lx) * 4 + 3];
        }

        if (outline > 0) {
            var (r, g, b, a) = Unpack(outlineColor);
            var reach = (int) Math.Ceiling(outline);
            for (var y = Math.Max(0, layer._top + oy - reach); y <= Math.Min(Height - 1, layer._bottom + oy + reach); y++) {
                for (var x = Math.Max(0, layer._left + ox - reach); x <= Math.Min(Width - 1, layer._right + ox + reach); x++) {
                    float most = 0;
                    for (var j = -reach; j <= reach && most < 1; j++) {
                        for (var i = -reach; i <= reach; i++) {
                            var falloff = Math.Clamp(outline + 0.5f - MathF.Sqrt(i * i + j * j), 0, 1);
                            if (falloff > 0) {
                                most = Math.Max(most, LayerAlpha(x + i, y + j) * falloff);
                            }
                        }
                    }

                    if (most > 0) {
                        Blend(x, y, r * a * most, g * a * most, b * a * most, a * most);
                    }
                }
            }
        }

        // Only the part the layer drew on.
        for (var ly = layer._top; ly <= layer._bottom; ly++) {
            var y = ly + oy;
            if (y < 0 || y >= Height) {
                continue;
            }

            for (var lx = layer._left; lx <= layer._right; lx++) {
                var x = lx + ox;
                var lo = (ly * layer.Width + lx) * 4;
                var a = layer._p[lo + 3];
                if (x < 0 || x >= Width || a <= 0) {
                    continue;
                }

                Blend(x, y, layer._p[lo], layer._p[lo + 1], layer._p[lo + 2], a);
            }
        }
    }

    /// <summary>A ring (a circle's edge) of the given thickness in an ARGB colour, with smooth edges.</summary>
    internal void Ring(float cx, float cy, float radius, float thickness, uint color) {
        var (r, g, b, a) = Unpack(color);
        var reach = radius + thickness;
        for (var y = Math.Max(0, (int) (cy - reach)); y < Math.Min(Height, (int) Math.Ceiling(cy + reach)); y++) {
            for (var x = Math.Max(0, (int) (cx - reach)); x < Math.Min(Width, (int) Math.Ceiling(cx + reach)); x++) {
                var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                var cover = Math.Clamp(thickness / 2 + 0.5f - Math.Abs(d - radius), 0, 1);
                Blend(x, y, r * a * cover, g * a * cover, b * a * cover, a * cover);
            }
        }
    }

    /// <summary>A straight line of the given thickness in an ARGB colour, with smooth edges.</summary>
    internal void Line(float x0, float y0, float x1, float y1, float thickness, uint color) {
        var (r, g, b, a) = Unpack(color);
        float dx = x1 - x0, dy = y1 - y0;
        var length = dx * dx + dy * dy;
        var reach = thickness;
        for (var y = Math.Max(0, (int) (Math.Min(y0, y1) - reach)); y < Math.Min(Height, (int) Math.Ceiling(Math.Max(y0, y1) + reach)); y++) {
            for (var x = Math.Max(0, (int) (Math.Min(x0, x1) - reach)); x < Math.Min(Width, (int) Math.Ceiling(Math.Max(x0, x1) + reach)); x++) {
                float px = x + 0.5f, py = y + 0.5f;
                var t = length <= 0 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / length, 0, 1);
                var ex = px - (x0 + t * dx);
                var ey = py - (y0 + t * dy);
                var cover = Math.Clamp(thickness / 2 + 0.5f - MathF.Sqrt(ex * ex + ey * ey), 0, 1);
                Blend(x, y, r * a * cover, g * a * cover, b * a * cover, a * cover);
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
/// Draws a spell's card the way the game does (Niduus, 2026-10-21: "previewing what the card looks like in game";
/// 2026-10-22: "Improve Render of Spell Cards (Also including the x1 symbol)"): the card frame from GUI/Cards (the
/// school's, or the treasure or item frame), the spell's art behind it, the name across the top in the game's card
/// font, the pip cost in the triangle, the school icon in the round socket, the spell's own accuracy on the target,
/// the type icon in the square, the "can't be enchanted" and PvP or PvE only marks over the art, an "x1" count on the
/// right, and the description on the parchment with the game's own small icons. A piece that is not in the game files
/// is drawn as a plain stand-in, so a card always comes out whole.
/// </summary>
internal static class CardPainter {

    /// <summary>The card frame's size in the game's pixels; the card is drawn this many times bigger.</summary>
    internal const int CARD_WIDTH = 128;
    internal const int CARD_HEIGHT = 196;
    internal const int SCALE = 3;

    /// <summary>Count to pass to show "x1" on the cards that come as counted copies (treasure and item cards) only.</summary>
    internal const int AUTO_COUNT = -1;

    private const uint TEXT_OUTLINE = 0xFF1E1208;
    // The pip cost and the accuracy are yellow on the game's cards.
    private const uint ACCURACY_COLOR = 0xFFFFE45A;
    private const uint DESCRIPTION_COLOR = 0xFF20140A;

    private static readonly Dictionary<string, uint> s_schoolColors = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = 0xFFC0201A, ["Ice"] = 0xFF2C8FD8, ["Storm"] = 0xFF8E2BA8, ["Myth"] = 0xFFD8C01E, ["Life"] = 0xFF2E9E3A,
        ["Death"] = 0xFF505050, ["Balance"] = 0xFFC8B89A, ["Star"] = 0xFF6E6ED8, ["Sun"] = 0xFFE09A28, ["Moon"] = 0xFF8A7CC8,
        ["Shadow"] = 0xFF2A2A30,
    };

    // The game's names for the "can't be enchanted" mark differ between versions; the first one found is used.
    private static readonly string[] s_noEnchantIcons = [
        "Icon_NoEnchant.dds", "Icon_No_Enchant.dds", "Type_NoEnchant.dds", "Type_Card_NoEnchant.dds", "Card_NoEnchant.dds",
        "NoEnchant.dds", "Icon_Enchant_Not.dds",
    ];

    // Type icons whose file is not named after the type.
    private static readonly Dictionary<string, string> s_typeIcons = new(StringComparer.OrdinalIgnoreCase) {
        ["Shadow_Creature"] = "Type_Shadow_Self.dds", ["Cloak"] = "Type_Trap.dds", ["Shadow"] = "Type_Shadow_Self.dds",
    };

    /// <summary>The colour of a school, for list rows and stand-ins.</summary>
    internal static uint SchoolColor(string school) => s_schoolColors.TryGetValue(school, out var color) ? color : 0xFF8A7A62;

    /// <summary>
    /// The card as a picture <see cref="CARD_WIDTH"/> by <see cref="CARD_HEIGHT"/> times <paramref name="scale"/>.
    /// <paramref name="count"/> is the "xN" on the right: 0 for none, <see cref="AUTO_COUNT"/> for "x1" on treasure
    /// and item cards, or how many copies a deck holds.
    /// </summary>
    internal static GameImage Paint(SpellBook book, SpellCard card, int count = AUTO_COUNT, int scale = SCALE)
        => new Painter(book, card, Math.Clamp(scale, 1, 6)).Paint(count == AUTO_COUNT ? card.Counted ? 1 : 0 : count);

    /// <summary>The frame picture the game uses for this card, or null when the game files have none.</summary>
    internal static GameImage? Frame(SpellBook book, SpellCard card) {
        if (card.CardFront.Length > 0 && book.Image(card.CardFront) is { } own) {
            return own;
        }

        if (card.Treasure && book.Image("School_Treasure_Blnk.dds") is { } treasure) {
            return treasure;
        }

        if (card.Item && book.Image("School_Item_Blnk.dds") is { } item) {
            return item;
        }

        if (book.Image($"School_{card.School}_Blnk.dds") is { } school) {
            return school;
        }

        // Schools without a frame of their own in the game files.
        return card.School.Equals("Shadow", StringComparison.OrdinalIgnoreCase)
            ? book.Image("School_BW_Blnk.dds") ?? book.Image("School_Item_Blnk.dds")
            : book.Image("School_Item_Blnk.dds");
    }

    private sealed class Painter(SpellBook book, SpellCard card, int scale) {

        private readonly CardCanvas _canvas = new(CARD_WIDTH * scale, CARD_HEIGHT * scale);
        private readonly GlyphFont? _fancy = book.Font("WizardFancy.xml") ?? book.Font("SpellTitle_sm.xml");
        private readonly GlyphFont? _normal = book.Font("WizardNormal.xml") ?? book.Font("SpellDescription.xml");
        private readonly GlyphFont? _text = book.Font("SpellDescription.xml") ?? book.Font("WizardNormal.xml");

        private float N(float value) => value * scale;

        internal GameImage Paint(int count) {
            Art();
            if (Frame(book, card) is { } frame) {
                _canvas.DrawImage(frame, 0, 0, CARD_WIDTH, CARD_HEIGHT, 0, 0, _canvas.Width, _canvas.Height);

                // Some copies of the game have a treasure or item frame without the parchment the words sit on;
                // the school's own frame lends its parchment so the description stays readable.
                if (!Parchment(frame) && book.Image($"School_{card.School}_Blnk.dds") is { } plain && plain != frame && Parchment(plain)) {
                    _canvas.DrawImage(plain, 7, 129, 114, 61, N(7), N(129), N(114), N(61));
                }
            }
            else {
                StandInFrame(SchoolColor(card.School));
            }

            SchoolIcon();
            TypeIcon();
            Restrictions();
            Cost();
            Accuracy();
            if (count > 0) {
                Count(count);
            }

            if ((_fancy ?? _normal) is { } titleFont) {
                Title(titleFont, card.Name.ToUpperInvariant());
            }

            if (_text is not null) {
                Description(_text);
            }

            return _canvas.ToImage();
        }

        // -----------------------------------------------------------------
        // Pieces of the card
        // -----------------------------------------------------------------

        private void Art() {
            _canvas.FillRect(N(7), N(23), N(114), N(101), 0xFF2A1C12);
            if (book.Image(card.ImageName) is { } art) {
                Cover(art, N(7), N(23), N(114), N(101));
            }
            else if (book.Image($"Art_School_{card.School}.dds") is { } schoolArt) {
                // The school's own picture, dimmed, where the spell's art is missing.
                Cover(schoolArt, N(7), N(23), N(114), N(101), 0.55f);
            }
            else if (book.SchoolIcon(card.School) is { } icon && book.Image(icon) is { } big) {
                Fit(big, N(64), N(70), N(56), 0.35f);
            }
            else {
                _canvas.FillCircle(N(64), N(72), N(26), (SchoolColor(card.School) & 0x00FFFFFF) | 0x50000000);
            }
        }

        /// <summary>Whether a frame's description box is parchment (pale, not strongly coloured) as the game's school frames are.</summary>
        private static bool Parchment(GameImage frame) {
            if (frame.Width < CARD_WIDTH || frame.Height < CARD_HEIGHT) {
                return true;
            }

            var parchment = 0;
            var count = 0;
            for (var y = 140; y < 180; y += 4) {
                for (var x = 20; x < 108; x += 4) {
                    var o = (y * frame.Width + x) * 4;
                    var max = Math.Max(frame.Pixels[o], Math.Max(frame.Pixels[o + 1], frame.Pixels[o + 2]));
                    var min = Math.Min(frame.Pixels[o], Math.Min(frame.Pixels[o + 1], frame.Pixels[o + 2]));
                    // Warm and pale: red over blue, and not strongly coloured.
                    var pale = max > 0 && (max - min) / (double) max < 0.55 && frame.Pixels[o] > frame.Pixels[o + 2] + 16;
                    parchment += pale ? 1 : 0;
                    count++;
                }
            }

            return parchment > count * 0.6;
        }

        private void StandInFrame(uint color) {
            // The school's colour around the art window, a parchment box under it and the sockets.
            _canvas.FillRect(N(1), N(1), N(126), N(24), color, N(4));
            _canvas.FillRect(N(1), N(24), N(8), N(100), color);
            _canvas.FillRect(N(119), N(24), N(8), N(100), color);
            _canvas.FillRect(N(1), N(121), N(126), N(74), color, N(4));
            _canvas.FillRect(N(7), N(128), N(114), N(62), 0xFFE2CFA0, N(2));
            _canvas.FillCircle(N(111), N(39), N(12), 0xFF6A5236);
            _canvas.FillCircle(N(111), N(39), N(10), 0xFFC4A878);
            _canvas.FillRect(N(99), N(98), N(25), N(25), 0xFF6A5236, N(2));
            _canvas.FillRect(N(101), N(100), N(21), N(21), 0xFFC4A878, N(1));
            _canvas.FillCircle(N(14), N(110), N(12), 0xFF4A4A40);
            foreach (var (radius, ring) in new[] { (9.5f, 0xFF3C3CA0u), (7f, 0xFFD03C28u), (4.5f, 0xFFF0A020u), (2f, 0xFFF8E070u) }) {
                _canvas.FillCircle(N(14), N(110), N(radius), ring);
            }

            // The pip triangle.
            for (var y = 0f; y < 25; y += 0.5f) {
                var half = (25 - y) * 0.62f;
                _canvas.FillRect(N(16.5f - half), N(26 + y), N(half * 2), N(0.5f), 0xFFB09060);
            }
        }

        private void SchoolIcon() {
            if (book.SchoolIcon(card.School) is { } file && book.Image(file) is { } school) {
                Fit(school, N(111), N(39), N(19));
            }
            else if ((_fancy ?? _normal) is { } font && card.School.Length > 0) {
                Outlined(font, card.School[..1].ToUpperInvariant(), N(111), N(31.5f), N(14), 0xFFFFFFFF, center: true);
            }
        }

        private void TypeIcon() {
            var icon = book.Image($"Type_{card.Type}.dds")
                       ?? (s_typeIcons.TryGetValue(card.Type, out var other) ? book.Image(other) : null)
                       ?? (card.Minion ? book.Image("Icons_Minion.dds") : null);
            if (icon is not null) {
                Fit(icon, N(111.5f), N(110.5f), N(20));
            }
            else if (_normal is not null && card.Type.Length > 0) {
                var word = card.Type.Replace('_', ' ');
                var size = Math.Min(N(9), N(21) / Math.Max(1, _normal.Measure(word, 1)) * _normal.CellHeight);
                Outlined(_normal, word, N(111.5f), N(110.5f) - size / 2, size, 0xFFFFFFFF, center: true, outline: 0.5f);
            }
        }

        /// <summary>The marks the game shows over the art: can't be enchanted, and PvP or PvE only.</summary>
        private void Restrictions() {
            var marks = new List<Action<float, float, float>>();
            if (card.NoPvEEnchant || card.NoPvPEnchant) {
                marks.Add(NoEnchant);
            }

            // PvP only shows "not in PvE"; PvE only (such as most item and pet cards) shows "not in PvP".
            if (card.PvP && !card.PvE) {
                marks.Add((x, y, size) => Mark("Type_Card_PVP.dds", "PvE", x, y, size));
            }
            else if (card.PvE && !card.PvP) {
                marks.Add((x, y, size) => Mark("Type_Card_PVE.dds", "PvP", x, y, size));
            }

            const float SIZE = 24;
            var left = 64 - (marks.Count - 1) * (SIZE + 2) / 2;
            foreach (var mark in marks) {
                mark(N(left), N(106), N(SIZE));
                left += SIZE + 2;
            }
        }

        private void NoEnchant(float x, float y, float size) {
            if (s_noEnchantIcons.Select(book.Image).FirstOrDefault(i => i is not null) is { } own) {
                Fit(own, x, y, size);

                return;
            }

            // The enchantment type icon struck out with the game's red "not" circle (GUI/Art/Art_Not.dds).
            if (book.Image("Type_Enchantment.dds") is { } enchant) {
                Fit(enchant, x, y, size * 0.7f);
            }
            else {
                DrawnEnchantCard(x, y, size * 0.62f);
            }

            if (book.Image("Art_Not.dds") is { } not) {
                Fit(not, x, y, size);
            }
            else {
                DrawnNot(x, y, size);
            }
        }

        private void Mark(string file, string word, float x, float y, float size) {
            if (book.Image(file) is { } icon) {
                Fit(icon, x, y, size);

                return;
            }

            // The word it rules out, struck through with the game's red "not" circle.
            _canvas.FillCircle(x, y, size * 0.42f, 0xC0201810);
            if (_normal is not null) {
                Outlined(_normal, word, x, y - size * 0.2f, size * 0.38f, 0xFFFFFFFF, center: true, outline: 0.4f);
            }

            if (book.Image("Art_Not.dds") is { } not) {
                Fit(not, x, y, size);
            }
            else {
                DrawnNot(x, y, size);
            }
        }

        private void DrawnEnchantCard(float x, float y, float size) {
            // A little card with a star on it.
            _canvas.FillRect(x - size * 0.36f, y - size / 2, size * 0.72f, size, 0xFF6A4A20, size * 0.12f);
            _canvas.FillRect(x - size * 0.3f, y - size * 0.44f, size * 0.6f, size * 0.88f, 0xFFF0B040, size * 0.1f);
            _canvas.FillCircle(x, y, size * 0.18f, 0xFFFFF4C0);
        }

        private void DrawnNot(float x, float y, float size) {
            var radius = size * 0.42f;
            var thick = size * 0.11f;
            _canvas.Ring(x, y, radius, thick + N(0.8f), 0xFF3A0A06);
            _canvas.Ring(x, y, radius, thick, 0xFFD8231A);
            var d = radius * 0.7071f;
            _canvas.Line(x - d, y - d, x + d, y + d, thick + N(0.8f), 0xFF3A0A06);
            _canvas.Line(x - d, y - d, x + d, y + d, thick, 0xFFD8231A);
        }

        private void Cost() {
            if (_normal is null) {
                return;
            }

            // Pip cost in the triangle; school and shadow pips as small pips under it.
            Outlined(_normal, card.XPips ? "X" : card.Pips.ToString(CultureInfo.InvariantCulture), N(16.5f), N(27), N(15), ACCURACY_COLOR,
                center: true);
            var pipY = 54f;
            foreach (var (pipSchool, pips) in card.SchoolPips) {
                for (var i = 0; i < pips; i++, pipY += 9) {
                    Pip(book.PipIcon(pipSchool), SchoolColor(pipSchool), pipY);
                }
            }

            for (var i = 0; i < card.ShadowPips; i++, pipY += 9) {
                Pip("Icon_Shadow_Pip.dds", 0xFF3A2A50, pipY);
            }
        }

        private void Accuracy() {
            if (_normal is null) {
                return;
            }

            // The spell's own accuracy, centred on the target in the bottom left, as the game shows it.
            var text = $"{card.Accuracy}%";
            var height = N(11.5f);
            var room = N(24);
            if (_normal.Measure(text, height / _normal.CellHeight) > room) {
                height = room / _normal.Measure(text, 1) * _normal.CellHeight;
            }

            Outlined(_normal, text, N(14), N(110.5f) - height * 0.5f, height, ACCURACY_COLOR, center: true, outline: 0.8f);
        }

        /// <summary>The "x1" on the right, between the school icon and the type icon.</summary>
        private void Count(int count) {
            if ((_fancy ?? _normal) is not { } font) {
                return;
            }

            var number = count.ToString(CultureInfo.InvariantCulture);
            var big = N(19);
            var small = N(13);
            var widthX = font.Measure("x", small / font.CellHeight);
            var widthN = font.Measure(number, big / font.CellHeight);
            var x = Math.Min(N(112) - (widthX + widthN) / 2, N(126.5f) - widthX - widthN);
            Outlined(font, "x", x, N(73) - small * 0.42f, small, 0xFFFFFFFF, outline: 0.9f);
            Outlined(font, number, x + widthX, N(71) - big * 0.5f, big, 0xFFFFFFFF, outline: 0.9f);
        }

        private void Pip(string? icon, uint color, float y) {
            if (book.Image(icon) is { } image) {
                Fit(image, N(9), N(y), N(9));
            }
            else {
                _canvas.FillCircle(N(9), N(y), N(3.6f), 0xFF1E1208);
                _canvas.FillCircle(N(9), N(y), N(3f), color);
            }
        }

        private void Title(GlyphFont font, string name) {
            var height = N(14);
            var fontScale = height / font.CellHeight;
            var room = N(110);
            if (font.Measure(name, fontScale) > room) {
                fontScale = room / font.Measure(name, 1);
                height = fontScale * font.CellHeight;
            }

            // Centre the letters on the banner, which runs from 3 to 23.
            Outlined(font, name, _canvas.Width / 2f, N(13.5f) - height * 0.5f, height, 0xFFFFFBEA, center: true, outline: 0.75f);
        }

        // -----------------------------------------------------------------
        // Drawing helpers
        // -----------------------------------------------------------------

        /// <summary>Fills a box with a picture, cutting off what does not fit, like the game's art window.</summary>
        private void Cover(GameImage image, float x, float y, float w, float h, float opacity = 1) {
            float sw = image.Width, sh = image.Height;
            if (sw / sh > w / h) {
                sw = sh * w / h;
            }
            else {
                sh = sw * h / w;
            }

            _canvas.DrawImage(image, (image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh, x, y, w, h, opacity: opacity);
        }

        /// <summary>Draws a picture as big as fits in a square around a centre point.</summary>
        private void Fit(GameImage image, float cx, float cy, float size, float opacity = 1) {
            var fit = size / Math.Max(image.Width, image.Height);
            var w = image.Width * fit;
            var h = image.Height * fit;
            _canvas.DrawImage(image, 0, 0, image.Width, image.Height, cx - w / 2, cy - h / 2, w, h, opacity: opacity);
        }

        /// <summary>A canvas just big enough for text in this box, with room for letters that reach outside it.</summary>
        private static CardCanvas Layer(float x, float y, float width, float height) {
            var margin = height * 0.5f + 2;
            var left = (int) Math.Floor(x - margin);
            var top = (int) Math.Floor(y - margin);

            return new CardCanvas((int) Math.Ceiling(width + margin * 2) + 2, (int) Math.Ceiling(height + margin * 2) + 2, left, top);
        }

        /// <summary>Text with a dark edge; <paramref name="height"/> is the height of a letter's cell.</summary>
        private void Outlined(GlyphFont font, string text, float x, float y, float height, uint color,
                bool center = false, bool right = false, float outline = 0.7f) {
            var fontScale = height / font.CellHeight;
            var width = font.Measure(text, fontScale);
            if (center) {
                x -= width / 2;
            }
            else if (right) {
                x -= width;
            }

            var layer = Layer(x, y, width, height);
            font.Draw(layer, text, x, y, fontScale, color);
            _canvas.DrawLayer(layer, N(outline), TEXT_OUTLINE);
        }

        // -----------------------------------------------------------------
        // The description on the parchment
        // -----------------------------------------------------------------

        // Small pictures in the description are this much of a line's height.
        private const float ICON = 0.86f;
        private const int GENERATED_ON_CARD = 120;
        private const uint GENERATED_COLOR = 0xFF6E5A40;

        private sealed record Piece(string Text, uint Color, GameImage? Icon, bool Space, bool Break, float Aspect);

        private void Description(GlyphFont font) {
            var pieces = new List<Piece>();
            // A card with no game text shows Imlight's own words (when short enough to read on the card), in a faded
            // brown so they don't pass for the game's.
            var parts = card.Blank && card.Explained.Length is > 0 and <= GENERATED_ON_CARD
                ? [new DescriptionPart(card.Explained.TrimEnd('.'), GENERATED_COLOR)]
                : book.Describe(card);
            foreach (var part in parts) {
                var color = part.Color == 0xFF000000 ? DESCRIPTION_COLOR : part.Color;
                if (part.Break) {
                    pieces.Add(new Piece("", 0, null, false, true, 1));
                }
                else if (part.Icon is not null && book.Image(part.Icon) is { } icon) {
                    pieces.Add(new Piece("", color, icon, false, false, (float) icon.Width / icon.Height));
                }
                else {
                    // Words, or the name of a small picture this copy of the game does not have.
                    var words = (part.Icon is null ? part.Text : $" {part.Text} ").Split(' ');
                    for (var i = 0; i < words.Length; i++) {
                        if (i > 0) {
                            pieces.Add(new Piece(" ", color, null, true, false, 1));
                        }

                        if (words[i].Length > 0) {
                            pieces.Add(new Piece(words[i], color, null, false, false, 1));
                        }
                    }
                }
            }

            if (pieces.Count == 0) {
                return;
            }

            float boxX = N(10), boxY = N(130), boxW = N(108), boxH = N(58);
            var height = N(17.5f);
            List<List<(Piece Piece, float Width)>> lines;
            float step;
            while (true) {
                step = height * 0.86f;
                lines = Wrap(pieces, font, height / font.CellHeight, height, boxW);
                if (lines.Count * step <= boxH || height <= N(6)) {
                    break;
                }

                height *= 0.93f;
            }

            var fontScale = height / font.CellHeight;
            var y = boxY + (boxH - lines.Count * step) / 2 - (height - step) / 2;

            // The words on a layer of their own with a faint light edge, so they stay crisp on the parchment.
            var layer = new CardCanvas((int) (boxW + height * 2), (int) (boxH + height * 3), (int) (boxX - height), (int) (boxY - height * 1.5f));
            var icons = new List<(GameImage Icon, float X, float Y, float W, float H)>();
            foreach (var line in lines) {
                var x = boxX + (boxW - line.Sum(p => p.Width)) / 2;
                foreach (var (piece, width) in line) {
                    if (piece.Icon is { } icon) {
                        var iconHeight = height * ICON;
                        icons.Add((icon, x + width * 0.05f, y + height * 0.48f - iconHeight / 2, iconHeight * piece.Aspect, iconHeight));
                    }
                    else if (!piece.Space) {
                        font.Draw(layer, piece.Text, x, y, fontScale, piece.Color);
                    }

                    x += width;
                }

                y += step;
            }

            _canvas.DrawLayer(layer, N(0.35f), 0x40FFF4D8);
            foreach (var (icon, x, iy, w, h) in icons) {
                _canvas.DrawImage(icon, 0, 0, icon.Width, icon.Height, x, iy, w, h);
            }
        }

        /// <summary>
        /// Breaks the pieces into lines no wider than <paramref name="room"/>, only at spaces: pieces with no space
        /// between them, such as "290", "-" and "330" or a number and its icon, stay together.
        /// </summary>
        private static List<List<(Piece Piece, float Width)>> Wrap(List<Piece> pieces, GlyphFont font, float fontScale, float height, float room) {
            var lines = new List<List<(Piece, float)>> { new() };
            float used = 0;
            var run = new List<(Piece, float)>();
            float runWidth = 0;
            (Piece Piece, float Width)? space = null;

            void EndRun() {
                if (run.Count == 0) {
                    return;
                }

                var line = lines[^1];
                if (line.Count > 0 && used + (space?.Width ?? 0) + runWidth > room) {
                    line = [];
                    lines.Add(line);
                    used = 0;
                }
                else if (line.Count > 0 && space is { } gap) {
                    line.Add(gap);
                    used += gap.Width;
                }

                line.AddRange(run);
                used += runWidth;
                run.Clear();
                runWidth = 0;
                space = null;
            }

            foreach (var piece in pieces) {
                if (piece.Break) {
                    EndRun();
                    lines.Add([]);
                    used = 0;
                    space = null;
                }
                else if (piece.Space) {
                    EndRun();
                    space = lines[^1].Count > 0 ? (piece, font.Measure(piece.Text, fontScale)) : null;
                }
                else {
                    var width = piece.Icon is not null ? height * ICON * piece.Aspect * 1.1f : font.Measure(piece.Text, fontScale);
                    run.Add((piece, width));
                    runWidth += width;
                }
            }

            EndRun();
            if (lines.Count > 1 && lines[^1].Count == 0) {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

    }

}
