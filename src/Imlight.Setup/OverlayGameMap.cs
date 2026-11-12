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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Wad;

namespace Imlight.Setup;

/// <summary>
/// The game's own map page of an area, for under the minimap (Niduus, 2026-11-12: "USe the in-game maps for layout
/// help"). Everything is read from the player's game files and never written: Root.wad's DoodleMapMap.xml names the
/// area's map, GUI-WorldData.wad holds it (MapInfo: picture and turn) and the area's own .wad its Compass.xml, whose
/// markers pin spots of the world to spots of the map page. The pages are drawn by hand, so they line up roughly.
/// </summary>
internal static class OverlayGameMap {

    /// <summary>The map page and how a spot of it maps to the world (x right, y up).</summary>
    internal sealed record Picture(WriteableBitmap Bitmap, Rect Inside, double A, double B, double C, double D, double E, double F) {

        /// <summary>The world spot under picture pixel (px, py): x = A px + B py + C, y = D px + E py + F.</summary>
        internal (double X, double Y) World(double px, double py) => ((A * px) + (B * py) + C, (D * px) + (E * py) + F);

    }

    // The compass markers' page spots are in the map window, which shows the page at about this size and place
    // (measured on Ravenwood, the Commons and Triton Avenue: within about 5% of the page).
    private const double PAGE_SCALE = 0.68;
    private const double PAGE_LEFT = 13;
    private const double PAGE_TOP = 27;

    private static readonly ConcurrentDictionary<(string Zone, bool WithGround), Task<Picture?>> s_pictures = new();
    private static readonly ConcurrentDictionary<string, Archive?> s_archives = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? s_mapOfZone;

    /// <summary>
    /// The map page of this area when it is ready, else null (it is read in the background; the minimap draws again
    /// when it is). <paramref name="ground"/> helps when the area's compass markers are missing or out of date.
    /// </summary>
    internal static Picture? For(string zone, OverlayMinimapData.Ground? ground) {
        if (string.IsNullOrWhiteSpace(zone)) {
            return null;
        }

        var useGround = ground is { IsEmpty: false };
        var task = s_pictures.GetOrAdd((zone.ToLowerInvariant(), useGround), _ => Task.Run(() => {
            Picture? picture = null;
            try {
                picture = Load(zone, useGround ? ground : null);
            }
            catch (Exception) {
                // No page for this area.
            }

            OverlayMinimapData.Redraw();

            return picture;
        }));

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static string? Folder() {
        try {
            return InstallInfo.Load(OverlayHost.InstallFolder).GameDataFolder is { } folder && File.Exists(Path.Combine(folder, "Root.wad"))
                ? folder
                : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static Archive? ArchiveIn(string folder, string name)
        => s_archives.GetOrAdd(Path.Combine(folder, name), path => {
            try {
                return File.Exists(path)
                    ? ArchiveParser.Parse(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    : null;
            }
            catch (Exception) {
                return null;
            }
        });

    private static T? Decode<T>(Archive? archive, string name) where T : PropertyClass {
        var data = archive?.OpenFile(name);

        return data is null ? null : new BindSerializer().Deserialize<PropertyClass>(data.Value.ToArray(), out var decoded) ? decoded as T : null;
    }

    // "|GUI|WorldData|Maps/A.xml" lives in GUI-WorldData.wad as "Maps/A.xml".
    private static (Archive? Archive, string Path) WorldData(string folder, string name) {
        var parts = name.Split('|', 4);

        return parts.Length == 4 && parts[2] == "WorldData" ? (ArchiveIn(folder, $"{parts[1]}-WorldData.wad"), parts[3]) : (ArchiveIn(folder, "Root.wad"), name);
    }

    private static Picture? Load(string zone, OverlayMinimapData.Ground? ground) {
        if (Folder() is not { } folder) {
            return null;
        }

        if (s_mapOfZone is null) {
            var list = Decode<DoodleMapMap>(ArchiveIn(folder, "Root.wad"), "DoodleMapMap.xml");
            var maps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in list?.m_mapList ?? []) {
                if (pair is not null) {
                    maps.TryAdd(pair.m_zoneName.ToString().Replace('\\', '/'), pair.m_mapName.ToString());
                }
            }

            s_mapOfZone = maps;
        }

        if (!s_mapOfZone.TryGetValue(zone.Replace('\\', '/'), out var mapName)) {
            return null;
        }

        var (mapWad, mapPath) = WorldData(folder, mapName);
        if (Decode<MapInfo>(mapWad, mapPath) is not { } info) {
            return null;
        }

        var (imageWad, imagePath) = WorldData(folder, info.m_mapImage.ToString());
        if (imageWad?.OpenFile(imagePath) is not { } imageData || GameImage.Decode(imageData.Span) is not { } image) {
            return null;
        }

        // Where the world lands on the page: from the compass markers when they fit the area, else roughly from the
        // map's turn and the area's size.
        var size = image.Width / 512.0;
        var toPage = FromCompass(folder, zone, ground, size) ?? FromGround(ground, info.m_mapRotation, image.Width, image.Height);
        if (toPage is not { } m) {
            return null;
        }

        // world -> page is (a b c / d e f); the picture needs page -> world.
        var det = (m.A * m.E) - (m.B * m.D);
        if (Math.Abs(det) < 1e-12) {
            return null;
        }

        var (ia, ib, id, ie) = (m.E / det, -m.B / det, -m.D / det, m.A / det);
        var (ic, iF) = (-(ia * m.C) - (ib * m.F), -(id * m.C) - (ie * m.F));

        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        using (var buffer = bitmap.Lock()) {
            var row = new byte[image.Width * 4];
            for (var y = 0; y < image.Height; y++) {
                for (var x = 0; x < image.Width; x++) {
                    var at = ((y * image.Width) + x) * 4;
                    var alpha = image.Pixels[at + 3];
                    row[x * 4] = (byte) (image.Pixels[at + 2] * alpha / 255);
                    row[(x * 4) + 1] = (byte) (image.Pixels[at + 1] * alpha / 255);
                    row[(x * 4) + 2] = (byte) (image.Pixels[at] * alpha / 255);
                    row[(x * 4) + 3] = alpha;
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + (y * buffer.RowBytes), row.Length);
            }
        }

        // The page inside its book cover and red frame.
        var inside = new Rect(image.Width * 0.125, image.Height * 0.08, image.Width * 0.83, image.Height * 0.83);

        return new Picture(bitmap, inside, ia, ib, ic, id, ie, iF);
    }

    private readonly record struct Affine(double A, double B, double C, double D, double E, double F);

    // The area's Compass.xml markers: a world spot and its spot in the map window (0, 0 when it has none).
    private static Affine? FromCompass(string folder, string zone, OverlayMinimapData.Ground? ground, double size) {
        var wad = ArchiveIn(folder, zone.Replace('\\', '/').Replace('/', '-') + ".wad");
        if (wad?.OpenFile("Compass.xml") is not { } compass) {
            return null;
        }

        var pins = Markers(compass.Span).Where(p => p.MapX != 0 || p.MapY != 0).ToList();
        if (pins.Count < 3) {
            return null;
        }

        // Out of date markers (Unicorn Way's are from before it was rebuilt) lie off the area's ground.
        if (ground is { IsEmpty: false } g) {
            var right = g.Left + (g.Width * g.Cell);
            var bottom = g.Top - (g.Height * g.Cell);
            var margin = 4 * g.Cell;
            var on = pins.Count(p => p.X >= g.Left - margin && p.X <= right + margin && p.Y <= g.Top + margin && p.Y >= bottom - margin);
            if (on * 4 < pins.Count * 3) {
                return null;
            }
        }

        if (Fit(pins.Select(p => (p.X, p.Y, (double) p.MapX)).ToList()) is not { } fx
            || Fit(pins.Select(p => (p.X, p.Y, (double) p.MapY)).ToList()) is not { } fy) {
            return null;
        }

        var worst = pins.Max(p => Math.Max(Math.Abs((fx.A * p.X) + (fx.B * p.Y) + fx.C - p.MapX), Math.Abs((fy.A * p.X) + (fy.B * p.Y) + fy.C - p.MapY)));
        if (worst > 40) {
            return null;
        }

        var k = PAGE_SCALE * size;

        return new Affine(fx.A * k, fx.B * k, (fx.C * k) + (PAGE_LEFT * size), fy.A * k, fy.B * k, (fy.C * k) + (PAGE_TOP * size));
    }

    // Least squares v = a x + b y + c.
    private static (double A, double B, double C)? Fit(List<(double X, double Y, double V)> points) {
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = points.Count, sxv = 0, syv = 0, sv = 0;
        foreach (var (x, y, v) in points) {
            sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y; sxv += x * v; syv += y * v; sv += v;
        }

        // Solve [[sxx sxy sx] [sxy syy sy] [sx sy n]] (a b c) = (sxv syv sv).
        var m = new[,] { { sxx, sxy, sx, sxv }, { sxy, syy, sy, syv }, { sx, sy, n, sv } };
        for (var col = 0; col < 3; col++) {
            var pivot = Enumerable.Range(col, 3 - col).MaxBy(r => Math.Abs(m[r, col]));
            if (Math.Abs(m[pivot, col]) < 1e-9) {
                return null;
            }

            for (var c = 0; c < 4; c++) {
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }

            for (var r = 0; r < 3; r++) {
                if (r == col) {
                    continue;
                }

                var f = m[r, col] / m[col, col];
                for (var c = 0; c < 4; c++) {
                    m[r, c] -= f * m[col, c];
                }
            }
        }

        var (a, b, cc) = (m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]);

        return double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(cc) ? (a, b, cc) : null;
    }

    // Read straight from the file: each marker's m_location (3 numbers) and m_mapLocation (2 whole numbers).
    private static List<(double X, double Y, int MapX, int MapY)> Markers(ReadOnlySpan<byte> data) {
        ReadOnlySpan<byte> location = [0x1B, 0xDC, 0x7E, 0x85];
        ReadOnlySpan<byte> mapLocation = [0x79, 0xB2, 0x82, 0x81];
        var found = new List<(double, double, int, int)>();
        var at = 0;
        while (found.Count < 500) {
            var here = data[at..].IndexOf(location);
            if (here < 0) {
                break;
            }

            here += at;
            if (here + 16 > data.Length) {
                break;
            }

            var x = BitConverter.ToSingle(data.Slice(here + 4, 4));
            var y = BitConverter.ToSingle(data.Slice(here + 8, 4));
            var next = data[(here + 16)..].IndexOf(mapLocation);
            if (next < 0 || next > 64) {
                at = here + 16;
                continue;
            }

            next += here + 16;
            if (next + 12 > data.Length) {
                break;
            }

            var mx = BitConverter.ToInt32(data.Slice(next + 4, 4));
            var my = BitConverter.ToInt32(data.Slice(next + 8, 4));
            if (float.IsFinite(x) && float.IsFinite(y) && Math.Abs(mx) < 5000 && Math.Abs(my) < 5000) {
                found.Add((x, y, mx, my));
            }

            at = next + 12;
        }

        return found;
    }

    // Without good markers: the map turned by its own turn (clockwise, in degrees), the area's ground filling about
    // 61% of the page, in the middle (measured on Unicorn Way, Ravenwood, the Commons and Triton Avenue).
    private static Affine? FromGround(OverlayMinimapData.Ground? ground, int turnDegrees, int width, int height) {
        if (ground is not { IsEmpty: false } g) {
            return null;
        }

        var turn = turnDegrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(turn);
        // On the page (y down): sx = x, sy = -y, turned clockwise.
        (double X, double Y) Turn(double x, double y) => ((x * cos) + (y * sin), (x * sin) - (y * cos));

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (var row = 0; row < g.Height; row += 2) {
            for (var col = 0; col < g.Width; col += 2) {
                var kind = g.Cells[(row * g.Width) + col];
                if (kind is 0 or 4) {
                    continue;
                }

                var (tx, ty) = Turn(g.Left + (col * g.Cell), g.Top - (row * g.Cell));
                minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx); minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
            }
        }

        if (minX >= maxX || minY >= maxY) {
            return null;
        }

        var scale = 0.61 * Math.Min(width, height) / Math.Max(maxX - minX, maxY - minY);
        var (midX, midY) = ((minX + maxX) / 2, (minY + maxY) / 2);

        // page x = (x cos + y sin - midX) scale + width/2; page y = (x sin - y cos - midY) scale + height/2.
        return new Affine(cos * scale, sin * scale, (width / 2.0) - (midX * scale), sin * scale, -cos * scale, (height / 2.0) - (midY * scale));
    }

}
