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
using System.Buffers.Binary;
using System.Numerics;

namespace Imlight.Setup;

/// <summary>
/// A picture from the game files as plain pixels: red, green, blue and alpha bytes, row by row from the top, not
/// premultiplied. Reads the two kinds the game uses for its menus and cards: DDS textures (DXT1, DXT3, DXT5, plain
/// RGB or RGBA, and the alpha-only pages of its fonts) and TGA files. Avalonia cannot open either, so this does.
/// </summary>
internal sealed class GameImage(int width, int height, byte[] pixels) {

    internal int Width { get; } = width;

    internal int Height { get; } = height;

    /// <summary>Four bytes per pixel: red, green, blue, alpha.</summary>
    internal byte[] Pixels { get; } = pixels;

    /// <summary>Reads a .dds or .tga file, or returns null when it is neither or uses a kind this does not read.</summary>
    internal static GameImage? Decode(ReadOnlySpan<byte> data) {
        try {
            if (data.Length >= 128 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ') {
                return DecodeDds(data);
            }

            return DecodeTga(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // DDS
    // ---------------------------------------------------------------------

    private const uint DDPF_ALPHAPIXELS = 0x1;
    private const uint DDPF_ALPHA = 0x2;
    private const uint DDPF_FOURCC = 0x4;
    private const uint DDPF_RGB = 0x40;
    private const uint DDPF_LUMINANCE = 0x20000;

    private static GameImage? DecodeDds(ReadOnlySpan<byte> data) {
        var height = (int) U32(data, 12);
        var width = (int) U32(data, 16);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) {
            return null;
        }

        var flags = U32(data, 80);
        var fourCc = data.Slice(84, 4);
        var bits = (int) U32(data, 88);
        var offset = 128;
        var pixels = new byte[width * height * 4];

        if ((flags & DDPF_FOURCC) != 0) {
            var format = System.Text.Encoding.ASCII.GetString(fourCc);
            if (format == "DX10") {
                // The newer header names the format by number after the old one.
                format = U32(data, 128) switch {
                    70 or 71 or 72 => "DXT1",
                    73 or 74 or 75 => "DXT3",
                    76 or 77 or 78 => "DXT5",
                    27 or 28 or 29 => "RGBA",
                    87 or 88 or 90 or 91 => "BGRA",
                    _ => "",
                };
                offset += 20;
            }

            switch (format) {
                case "DXT1":
                    DecodeBlocks(data[offset..], width, height, pixels, 8, DecodeDxt1Block);
                    break;
                case "DXT2" or "DXT3":
                    DecodeBlocks(data[offset..], width, height, pixels, 16, DecodeDxt3Block);
                    break;
                case "DXT4" or "DXT5":
                    DecodeBlocks(data[offset..], width, height, pixels, 16, DecodeDxt5Block);
                    break;
                case "RGBA":
                    data.Slice(offset, pixels.Length).CopyTo(pixels);
                    break;
                case "BGRA":
                    DecodeMasked(data[offset..], width, height, pixels, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
                    break;
                default:
                    return null;
            }

            return new GameImage(width, height, pixels);
        }

        if ((flags & DDPF_RGB) != 0 || (flags & DDPF_LUMINANCE) != 0) {
            var alphaMask = (flags & DDPF_ALPHAPIXELS) != 0 ? U32(data, 104) : 0;
            if ((flags & DDPF_LUMINANCE) != 0) {
                // Grey: the red mask is the brightness.
                var grey = U32(data, 92);
                DecodeMasked(data[offset..], width, height, pixels, bits, grey, grey, grey, alphaMask);
            }
            else {
                DecodeMasked(data[offset..], width, height, pixels, bits, U32(data, 92), U32(data, 96), U32(data, 100), alphaMask);
            }

            return new GameImage(width, height, pixels);
        }

        if ((flags & DDPF_ALPHA) != 0 && bits == 8) {
            // Alpha only, such as a font page: white with the alpha as its shape.
            for (var i = 0; i < width * height; i++) {
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 255;
                pixels[i * 4 + 3] = data[offset + i];
            }

            return new GameImage(width, height, pixels);
        }

        return null;
    }

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> rgba);

    private static void DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, byte[] pixels, int blockSize, BlockDecoder decode) {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < blocksHigh; by++) {
            for (var bx = 0; bx < blocksWide; bx++) {
                var at = (by * blocksWide + bx) * blockSize;
                if (at + blockSize > data.Length) {
                    return;
                }

                decode(data.Slice(at, blockSize), block);
                for (var y = 0; y < 4; y++) {
                    var py = by * 4 + y;
                    if (py >= height) {
                        break;
                    }

                    for (var x = 0; x < 4; x++) {
                        var px = bx * 4 + x;
                        if (px >= width) {
                            break;
                        }

                        block.Slice((y * 4 + x) * 4, 4).CopyTo(pixels.AsSpan((py * width + px) * 4, 4));
                    }
                }
            }
        }
    }

    private static void DecodeDxt1Block(ReadOnlySpan<byte> block, Span<byte> rgba) => DecodeColors(block, rgba, allowTransparent: true);

    private static void DecodeDxt3Block(ReadOnlySpan<byte> block, Span<byte> rgba) {
        DecodeColors(block[8..], rgba, allowTransparent: false);
        for (var i = 0; i < 16; i++) {
            var nibble = (block[i / 2] >> (i % 2 * 4)) & 0xF;
            rgba[i * 4 + 3] = (byte) (nibble * 17);
        }
    }

    private static void DecodeDxt5Block(ReadOnlySpan<byte> block, Span<byte> rgba) {
        DecodeColors(block[8..], rgba, allowTransparent: false);
        Span<byte> alphas = stackalloc byte[8];
        alphas[0] = block[0];
        alphas[1] = block[1];
        if (alphas[0] > alphas[1]) {
            for (var i = 1; i < 7; i++) {
                alphas[i + 1] = (byte) (((7 - i) * alphas[0] + i * alphas[1]) / 7);
            }
        }
        else {
            for (var i = 1; i < 5; i++) {
                alphas[i + 1] = (byte) (((5 - i) * alphas[0] + i * alphas[1]) / 5);
            }

            alphas[6] = 0;
            alphas[7] = 255;
        }

        ulong indices = 0;
        for (var i = 0; i < 6; i++) {
            indices |= (ulong) block[2 + i] << (8 * i);
        }

        for (var i = 0; i < 16; i++) {
            rgba[i * 4 + 3] = alphas[(int) ((indices >> (3 * i)) & 7)];
        }
    }

    private static void DecodeColors(ReadOnlySpan<byte> block, Span<byte> rgba, bool allowTransparent) {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<int> colors = stackalloc int[16];
        Unpack565(c0, colors[..4]);
        Unpack565(c1, colors.Slice(4, 4));
        if (c0 > c1 || !allowTransparent) {
            for (var k = 0; k < 3; k++) {
                colors[8 + k] = (2 * colors[k] + colors[4 + k]) / 3;
                colors[12 + k] = (colors[k] + 2 * colors[4 + k]) / 3;
            }

            colors[11] = colors[15] = 255;
        }
        else {
            for (var k = 0; k < 3; k++) {
                colors[8 + k] = (colors[k] + colors[4 + k]) / 2;
                colors[12 + k] = 0;
            }

            colors[11] = 255;
            colors[15] = 0;
        }

        var codes = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (var i = 0; i < 16; i++) {
            var code = (int) ((codes >> (2 * i)) & 3);
            for (var k = 0; k < 4; k++) {
                rgba[i * 4 + k] = (byte) colors[code * 4 + k];
            }
        }
    }

    private static void Unpack565(ushort color, Span<int> rgba) {
        var r = (color >> 11) & 31;
        var g = (color >> 5) & 63;
        var b = color & 31;
        rgba[0] = (r << 3) | (r >> 2);
        rgba[1] = (g << 2) | (g >> 4);
        rgba[2] = (b << 3) | (b >> 2);
        rgba[3] = 255;
    }

    private static void DecodeMasked(ReadOnlySpan<byte> data, int width, int height, byte[] pixels, int bits,
            uint red, uint green, uint blue, uint alpha) {
        var bytes = bits / 8;
        if (bytes is < 1 or > 4) {
            throw new ArgumentException("Unsupported pixel size.");
        }

        var pitch = width * bytes;
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = y * pitch + x * bytes;
                uint value = 0;
                for (var b = 0; b < bytes; b++) {
                    value |= (uint) data[at + b] << (8 * b);
                }

                var o = (y * width + x) * 4;
                pixels[o] = Channel(value, red);
                pixels[o + 1] = Channel(value, green);
                pixels[o + 2] = Channel(value, blue);
                pixels[o + 3] = alpha == 0 ? (byte) 255 : Channel(value, alpha);
            }
        }
    }

    private static byte Channel(uint value, uint mask) {
        if (mask == 0) {
            return 0;
        }

        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;

        return (byte) (((value & mask) >> shift) * 255 / max);
    }

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    // ---------------------------------------------------------------------
    // TGA
    // ---------------------------------------------------------------------

    private static GameImage? DecodeTga(ReadOnlySpan<byte> data) {
        if (data.Length < 18) {
            return null;
        }

        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var bits = data[16];
        var topDown = (data[17] & 0x20) != 0;
        if (colorMapType != 0 || imageType is not (2 or 3 or 10 or 11) || width == 0 || height == 0 || bits is not (8 or 24 or 32)) {
            return null;
        }

        var bytes = bits / 8;
        var count = width * height;
        var raw = new byte[count * bytes];
        var at = 18 + idLength;
        if (imageType is 2 or 3) {
            data.Slice(at, raw.Length).CopyTo(raw);
        }
        else {
            // Run-length packed.
            var written = 0;
            while (written < raw.Length) {
                var header = data[at++];
                var run = (header & 0x7F) + 1;
                if ((header & 0x80) != 0) {
                    var pixel = data.Slice(at, bytes);
                    at += bytes;
                    for (var i = 0; i < run && written < raw.Length; i++, written += bytes) {
                        pixel.CopyTo(raw.AsSpan(written));
                    }
                }
                else {
                    var length = Math.Min(run * bytes, raw.Length - written);
                    data.Slice(at, length).CopyTo(raw.AsSpan(written));
                    at += run * bytes;
                    written += length;
                }
            }
        }

        var pixels = new byte[count * 4];
        for (var y = 0; y < height; y++) {
            var sourceRow = topDown ? y : height - 1 - y;
            for (var x = 0; x < width; x++) {
                var s = (sourceRow * width + x) * bytes;
                var o = (y * width + x) * 4;
                if (bytes == 1) {
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = raw[s];
                    pixels[o + 3] = 255;
                }
                else {
                    pixels[o] = raw[s + 2];
                    pixels[o + 1] = raw[s + 1];
                    pixels[o + 2] = raw[s];
                    pixels[o + 3] = bytes == 4 ? raw[s + 3] : (byte) 255;
                }
            }
        }

        return new GameImage(width, height, pixels);
    }

}
