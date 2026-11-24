#if !HELLHOUND
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The Wizard101 windows on this PC for Claude's connector: which ones are open, and a picture of one. It only looks at
/// the windows the way Windows shows them (like a screenshot); it never reads the game's memory, sends it input or touches
/// its files.
/// </summary>
internal static class ClaudeGameWindows {

    private const string GAME_PROCESS = "WizardGraphicalClient";

    internal sealed record Window(int Number, IntPtr Handle, int ProcessId, string Title, bool Minimized,
                                  int X, int Y, int Width, int Height, double Scale);

    /// <summary>The open game windows, numbered from 1 in a steady order (by process, then window).</summary>
    internal static List<Window> List() {
        var found = new List<Window>();
        if (!OperatingSystem.IsWindows()) {
            return found;
        }

        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(GAME_PROCESS)) {
            try {
                if (!process.HasExited) {
                    ids.Add((uint) process.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            }
            finally {
                process.Dispose();
            }
        }

        if (ids.Count == 0) {
            return found;
        }

        var handles = new List<(IntPtr Handle, uint Pid)>();
        Native.EnumWindows((hwnd, _) => {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && ids.Contains(pid) && OverlayHost.IsGameWindow(hwnd)) {
                handles.Add((hwnd, pid));
            }

            return true;
        }, IntPtr.Zero);

        handles.Sort((a, b) => a.Pid != b.Pid ? a.Pid.CompareTo(b.Pid) : a.Handle.ToInt64().CompareTo(b.Handle.ToInt64()));
        foreach (var (hwnd, pid) in handles) {
            var length = Native.GetWindowTextLengthW(hwnd);
            var title = new StringBuilder(length + 1);
            Native.GetWindowTextW(hwnd, title, title.Capacity);
            var minimized = Native.IsIconic(hwnd);
            var inside = OverlayHost.GameClient(hwnd);
            found.Add(new Window(found.Count + 1, hwnd, (int) pid, title.ToString(), minimized,
                inside?.X ?? 0, inside?.Y ?? 0, inside?.Width ?? 0, inside?.Height ?? 0, inside?.Scale ?? 1));
        }

        return found;
    }

    /// <summary>
    /// A PNG of a game window's inside. Without the overlay the game draws itself into the picture (Windows' PrintWindow),
    /// so another window over it doesn't matter; with the overlay (or when the game gave back an empty picture) the screen
    /// there is copied, so the window must be in view.
    /// </summary>
    internal static (byte[] Png, int Width, int Height, string How) Picture(Window window, bool withOverlay, int maxWidth) {
        if (!OperatingSystem.IsWindows()) {
            throw new PanelException("Game pictures only work on Windows.");
        }

        if (window.Minimized || window.Width <= 0 || window.Height <= 0) {
            throw new PanelException($"Game window {window.Number} is minimized, so there is nothing to see. Bring it back up first.");
        }

        var how = "drawn by the game";
        byte[]? pixels = null;
        if (!withOverlay) {
            pixels = Grab(window, printWindow: true);
            if (pixels is null || IsBlank(pixels)) {
                pixels = null;
                how = "copied from the screen (the game gave an empty picture, so anything covering the window shows too)";
            }
        }
        else {
            how = "copied from the screen, with Ahziael's overlay";
        }

        pixels ??= Grab(window, printWindow: false) ?? throw new PanelException("Windows wouldn't give a picture of that game window.");

        using var full = new WriteableBitmap(new PixelSize(window.Width, window.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = full.Lock()) {
            for (var y = 0; y < window.Height; y++) {
                Marshal.Copy(pixels, y * window.Width * 4, buffer.Address + y * buffer.RowBytes, window.Width * 4);
            }
        }

        var fit = Math.Min(1, Math.Max(200, maxWidth) / (double) window.Width);
        var size = new PixelSize(Math.Max(1, (int) (window.Width * fit)), Math.Max(1, (int) (window.Height * fit)));
        using var stream = new MemoryStream();
        if (fit < 1) {
            // Drawn smaller onto a new picture: CreateScaledBitmap refuses a WriteableBitmap on Windows ("Invalid source
            // bitmap type", Niduus's first try, 2026-11-24).
            using var small = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var ctx = small.CreateDrawingContext()) {
                using (ctx.PushRenderOptions(new Avalonia.Media.RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality })) {
                    ctx.DrawImage(full, new Rect(0, 0, window.Width, window.Height), new Rect(0, 0, size.Width, size.Height));
                }
            }

            small.Save(stream);
        }
        else {
            full.Save(stream);
        }

        return (stream.ToArray(), size.Width, size.Height, how);
    }

    // The window's inside as top-down BGRA rows (alpha set solid), or null when Windows said no.
    private static byte[]? Grab(Window window, bool printWindow) {
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) {
            return null;
        }

        var memory = Native.CreateCompatibleDC(screen);
        var bitmap = Native.CreateCompatibleBitmap(screen, window.Width, window.Height);
        var old = Native.SelectObject(memory, bitmap);
        try {
            var ok = printWindow
                ? Native.PrintWindow(window.Handle, memory, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT)
                : Native.BitBlt(memory, 0, 0, window.Width, window.Height, screen, window.X, window.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            if (!ok) {
                return null;
            }

            var info = new Native.BITMAPINFOHEADER {
                biSize = (uint) Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = window.Width, biHeight = -window.Height,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var pixels = new byte[window.Width * window.Height * 4];
            Native.SelectObject(memory, old);
            if (Native.GetDIBits(memory, bitmap, 0, (uint) window.Height, pixels, ref info, 0) == 0) {
                return null;
            }

            for (var i = 3; i < pixels.Length; i += 4) {
                pixels[i] = 255;
            }

            return pixels;
        }
        finally {
            Native.SelectObject(memory, old);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // All (nearly) black: a game that drew nothing into PrintWindow's picture.
    private static bool IsBlank(byte[] pixels) {
        var step = Math.Max(4, pixels.Length / 4 / 2000 * 4);
        for (var i = 0; i + 2 < pixels.Length; i += step) {
            if (pixels[i] > 8 || pixels[i + 1] > 8 || pixels[i + 2] > 8) {
                return false;
            }
        }

        return true;
    }

    private static class Native {

        internal const uint PW_CLIENTONLY = 1;
        internal const uint PW_RENDERFULLCONTENT = 2;
        internal const uint SRCCOPY = 0x00CC0020;
        internal const uint CAPTUREBLT = 0x40000000;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll")]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    }

}
#endif
