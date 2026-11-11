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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>
/// The game overlay (Niduus, 2026-11-11): while Ahziael is open, a see-through window lies on top of every Wizard101
/// window, follows it, and shows the <see cref="OverlayDesign"/> ("Ahziael made by Niduus | version" in the bottom-left
/// at all times). Clicks go straight through it to the game. Nothing is put into the game and no game file is touched:
/// it is Ahziael's own window, drawn with Windows' layered windows (no Microsoft add-ons). Windows can't put any window
/// over a game in true (exclusive) full screen, so there it only shows in windowed or borderless mode.
///
/// It also tells the server the game was started on that it is running (<see cref="Link"/>), for servers with
/// [Overlay] RequiredToPlay on.
/// </summary>
internal static class OverlayHost {

    private const string GAME_PROCESS = "WizardGraphicalClient";

    private static string s_installFolder = "";
    private static OverlayDesign s_design = new();
    private static int s_designVersion;
    private static DispatcherTimer? s_timer;
    private static readonly Dictionary<IntPtr, LayeredWindow> s_windows = [];
    private static DateTime s_lastScan = DateTime.MinValue;
    private static List<IntPtr> s_games = [];
    private static string s_clock = "";

    /// <summary>Game windows the overlay is drawing on now.</summary>
    internal static int GameWindows { get; private set; }

    internal static string Version => SelfUpdate.CurrentVersion == new Version(0, 0, 0) ? "test build" : $"v{SelfUpdate.CurrentVersion}";

    internal static OverlayDesign Design => s_design;

    /// <summary>Starts the overlay with the saved design. Only on Windows; elsewhere it does nothing.</summary>
    internal static void Start(string installFolder) {
        s_installFolder = installFolder;
        s_design = OverlayDesign.Load(installFolder);
        if (!OperatingSystem.IsWindows() || s_timer is not null) {
            return;
        }

        s_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        s_timer.Tick += (_, _) => Tick();
        s_timer.Start();
        // Until the game is started from Ahziael, the server on this PC.
        Link.Target("127.0.0.1", GameClient.OverlayPort(installFolder, "127.0.0.1"), onlyIfNone: true);
        Link.Start();
    }

    /// <summary>Shows a changed design at once and saves it.</summary>
    internal static void Apply(OverlayDesign design) {
        s_design = design.Copy();
        s_designVersion++;
        try {
            s_design.Save(s_installFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    internal static void Stop() {
        s_timer?.Stop();
        foreach (var window in s_windows.Values) {
            window.Dispose();
        }

        s_windows.Clear();
        Link.Stop();
    }

    private static void Tick() {
        try {
            if (DateTime.UtcNow - s_lastScan > TimeSpan.FromSeconds(1)) {
                s_lastScan = DateTime.UtcNow;
                s_games = s_design.Enabled ? FindGameWindows() : [];
                var clock = OverlayRenderer.UsesClock(s_design) ? DateTime.Now.ToString("h:mm yyyy-MM-dd") : "";
                if (clock != s_clock) {
                    s_clock = clock;
                    s_designVersion++;
                }
            }

            foreach (var gone in s_windows.Keys.Where(h => !s_games.Contains(h)).ToList()) {
                s_windows[gone].Dispose();
                s_windows.Remove(gone);
            }

            var foreground = Native.GetForegroundWindow();
            foreach (var game in s_games) {
                if (!Native.IsWindow(game)) {
                    continue;
                }

                if (!s_windows.TryGetValue(game, out var window)) {
                    window = s_windows[game] = new LayeredWindow();
                }

                window.Follow(game, foreground == game && !Native.IsIconic(game), s_design, s_designVersion);
            }

            GameWindows = s_windows.Count;
        }
        catch (Exception ex) {
            Log($"Overlay: {ex.Message}");
        }
    }

    private static readonly HashSet<string> s_logged = [];

    // Each problem once, so a tick that keeps failing doesn't fill the log.
    private static void Log(string line) {
        lock (s_logged) {
            if (s_logged.Count < 50 && s_logged.Add(line)) {
                SetupLog.Info(line);
            }
        }
    }

    // The visible main windows of the game's processes.
    private static List<IntPtr> FindGameWindows() {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(GAME_PROCESS)) {
            ids.Add((uint) process.Id);
            process.Dispose();
        }

        var found = new List<IntPtr>();
        if (ids.Count == 0) {
            return found;
        }

        Native.EnumWindows((hwnd, _) => {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (ids.Contains(pid) && Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero
                && Native.GetClientRect(hwnd, out var r) && r.Right - r.Left > 200 && r.Bottom - r.Top > 150) {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Draws the design into a picture of this many pixels, as premultiplied BGRA rows.</summary>
    internal static byte[] Render(OverlayDesign design, int width, int height, double scale) {
        var control = OverlayRenderer.Build(design, new OverlayRenderer.Words(Version, Link.ServerLabel));
        var size = new Size(width / scale, height / scale);
        control.Measure(size);
        control.Arrange(new Rect(size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally {
            handle.Free();
        }

        return pixels;
    }

    // ---------------------------------------------------------------------
    // The window over one game window
    // ---------------------------------------------------------------------

    private sealed class LayeredWindow : IDisposable {

        private static Native.WndProc? s_proc;
        private static bool s_registered;
        private const string CLASS = "AhziaelOverlay";

        private readonly IntPtr _hwnd;
        private int _drawnVersion = -1;
        private (int W, int H, double Scale) _drawnSize;
        private Native.RECT _placed;
        private bool _shown;

        internal LayeredWindow() {
            if (!s_registered) {
                s_proc = Native.DefWindowProcW;
                var wc = new Native.WNDCLASSEX {
                    cbSize = (uint) Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                    hInstance = Native.GetModuleHandleW(null), lpszClassName = CLASS,
                };
                Native.RegisterClassExW(ref wc);
                s_registered = true;
            }

            _hwnd = Native.CreateWindowExW(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                CLASS, "Ahziael overlay", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        }

        internal void Follow(IntPtr game, bool show, OverlayDesign design, int version) {
            if (_hwnd == IntPtr.Zero) {
                return;
            }

            if (!show || !Native.GetClientRect(game, out var client)) {
                Hide();

                return;
            }

            var origin = new Native.POINT();
            Native.ClientToScreen(game, ref origin);
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            var dpi = Native.GetDpiForWindow(game);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var place = new Native.RECT { Left = origin.X, Top = origin.Y, Right = origin.X + width, Bottom = origin.Y + height };

            if (version != _drawnVersion || _drawnSize != (width, height, scale)) {
                Draw(Render(design, width, height, scale), width, height, origin);
                _drawnVersion = version;
                _drawnSize = (width, height, scale);
                _placed = place;
            }
            else if (!_placed.Equals(place)) {
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, origin.X, origin.Y, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                _placed = place;
            }

            if (!_shown) {
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, origin.X, origin.Y, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _shown = true;
            }
            else {
                // The game may have been brought to the front over it.
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
            }
        }

        private void Hide() {
            if (_shown) {
                Native.ShowWindow(_hwnd, Native.SW_HIDE);
                _shown = false;
            }
        }

        private void Draw(byte[] pixels, int width, int height, Native.POINT origin) {
            var screen = Native.GetDC(IntPtr.Zero);
            var memory = Native.CreateCompatibleDC(screen);
            var info = new Native.BITMAPINFO {
                biSize = (uint) Marshal.SizeOf<Native.BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
            };
            var dib = Native.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            try {
                if (dib == IntPtr.Zero) {
                    return;
                }

                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var old = Native.SelectObject(memory, dib);
                var size = new Native.SIZE { cx = width, cy = height };
                var source = new Native.POINT();
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(_hwnd, screen, ref origin, ref size, memory, ref source, 0, ref blend, Native.ULW_ALPHA);
                Native.SelectObject(memory, old);
            }
            finally {
                if (dib != IntPtr.Zero) {
                    Native.DeleteObject(dib);
                }

                Native.DeleteDC(memory);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Dispose() {
            if (_hwnd != IntPtr.Zero) {
                Native.DestroyWindow(_hwnd);
            }
        }

    }

    // ---------------------------------------------------------------------
    // Telling the server the overlay runs
    // ---------------------------------------------------------------------

    /// <summary>
    /// One connection to the Remote Admin port of the server Ahziael last started the game on, open while Ahziael is.
    /// Every few seconds it says how many game windows the overlay draws on; the server's [Overlay] RequiredToPlay lets
    /// in that many game connections from this PC's address. It sends nothing else: no account, no password, no files.
    /// </summary>
    internal static class Link {

        private static readonly object s_lock = new();
        private static CancellationTokenSource? s_stop;
        private static (string Host, int Port)? s_target;
        private static CancellationTokenSource? s_retarget;

        /// <summary>The server's address, for {server}.</summary>
        internal static string ServerLabel => s_target?.Host ?? "";

        internal static bool Connected { get; private set; }

        /// <summary>What the server said last: true when it needs the overlay, null when it has not answered.</summary>
        internal static bool? Required { get; private set; }

        internal static string Status => s_target is not { } target
            ? "Not linked to a server yet: click Play on the Play tab (or Play Wizard101) and it links to that server."
            : Connected
                ? $"Linked to {target.Host}. " + (Required == true ? "That server needs the overlay, and it sees yours." : "That server doesn't need the overlay right now.")
                : $"Trying to reach {target.Host} on port {target.Port}. A server that doesn't need the overlay may not answer, which is fine.";

        internal static void Start() {
            lock (s_lock) {
                if (s_stop is not null) {
                    return;
                }

                s_stop = new CancellationTokenSource();
            }

            var token = s_stop.Token;
            _ = Task.Run(() => RunAsync(token), CancellationToken.None);
        }

        internal static void Stop() {
            lock (s_lock) {
                s_stop?.Cancel();
                s_stop = null;
            }
        }

        /// <summary>Links to the server the game was just started on: its Remote Admin port at the same address.</summary>
        internal static void Target(string host, int remoteAdminPort, bool onlyIfNone = false) {
            lock (s_lock) {
                if (onlyIfNone && s_target is not null) {
                    return;
                }

                s_target = (host.Trim(), remoteAdminPort);
                s_retarget?.Cancel();
            }
        }

        private static async Task RunAsync(CancellationToken stop) {
            while (!stop.IsCancellationRequested) {
                (string Host, int Port)? target;
                CancellationTokenSource retarget;
                lock (s_lock) {
                    target = s_target;
                    s_retarget = retarget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                }

                try {
                    if (target is { } t) {
                        await ConnectAsync(t.Host, t.Port, retarget.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException
                                               or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException) {
                }
                catch (Exception ex) {
                    Log($"Overlay link: {ex.Message}");
                }

                Connected = false;
                try {
                    await Task.Delay(TimeSpan.FromSeconds(target is null ? 1 : 10), retarget.Token);
                }
                catch (OperationCanceledException) {
                }
            }
        }

        private static async Task ConnectAsync(string host, int port, CancellationToken stop) {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                connect.CancelAfter(TimeSpan.FromSeconds(8));
                // IPv4 first: the game connects over IPv4, and the server matches the two by address.
                var address = IPAddress.TryParse(host, out var ip) ? ip
                    : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, connect.Token)).FirstOrDefault()
                      ?? throw new SocketException((int) SocketError.HostNotFound);
                await tcp.ConnectAsync(address, port, connect.Token);
            }

            tcp.NoDelay = true;
            // Nothing secret goes over this, so any certificate the server made for itself will do.
            await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "Imlight Remote Admin", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, handshake.Token);
            }

            var header = new JsonObject { ["op"] = "overlay", ["windows"] = GameWindows, ["version"] = Version };
            while (!stop.IsCancellationRequested) {
                await RemoteAdminWire.WriteAsync(tls, header, null, stop);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                var (reply, _) = await RemoteAdminWire.ReadAsync(tls, 64 << 10, 0, wait.Token);
                if (reply["ok"]?.GetValue<bool>() != true) {
                    return;
                }

                Connected = true;
                Required = reply["required"]?.GetValue<bool>();
                var every = Math.Clamp((int?) reply["every"] ?? 5, 2, 30);
                await Task.Delay(TimeSpan.FromSeconds(every), stop);
                header = new JsonObject { ["op"] = "beat", ["windows"] = GameWindows };
            }
        }

    }

    // ---------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------

    private static class Native {

        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const uint WS_POPUP = 0x80000000;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SW_HIDE = 0;
        internal const uint GW_OWNER = 4;
        internal const uint ULW_ALPHA = 2;
        internal static readonly IntPtr HWND_TOPMOST = new(-1);

        internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT {

            public int Left, Top, Right, Bottom;

        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT {

            public int X, Y;

        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE {

            public int cx, cy;

        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION {

            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;

        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFO {

            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
            public uint bmiColors;

        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX {

            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;

        }

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowExW(int exStyle, string className, string title, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref POINT dst, ref SIZE size, IntPtr source,
            ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr obj);

    }

}
