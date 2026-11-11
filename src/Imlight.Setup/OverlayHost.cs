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
/// The game overlay (Niduus, 2026-11-11): a see-through window lies on top of every Wizard101 window, sticks to it, and
/// shows the <see cref="OverlayDesign"/> ("Ahziael made by Niduus | version" in the bottom-left at all times), with a
/// small button in the bottom-right that opens the designer. Clicks go straight through the overlay to the game; only
/// the button takes them. Nothing is put into the game and no game file is touched: these are Ahziael's own windows,
/// drawn with Windows' layered windows (no Microsoft add-ons). Windows can't put any window over a game in true
/// (exclusive) full screen, so there it only shows in windowed or borderless mode.
///
/// It runs inside Ahziael, or on its own ("Ahziael Overlay", Imlight.exe --overlay) without Ahziael open (Niduus,
/// 2026-11-11: "create a version that is to load WITHOUT and WITH from Ahziael"). Only one of them draws at a time
/// (a named mutex); they share the design (overlay.json) and the server to tell (overlay-link.json) through files.
///
/// It sits just above the game in the stacking order rather than above everything, so it stays on the game through
/// loading screens and clicks elsewhere ("always stick to Wiz, even through loading and click outs"): another window
/// moved over the game covers it like it covers the game, and it only goes away while the game is minimized.
///
/// It also tells the server the game was started on that it is running (<see cref="Link"/>), for servers with
/// [Overlay] RequiredToPlay on.
/// </summary>
internal static class OverlayHost {

    private const string GAME_PROCESS = "WizardGraphicalClient";
    private const string MUTEX = "Local\\AhziaelGameOverlay";

    /// <summary>How long a game window that went away (a loading screen that remakes it) keeps its overlay.</summary>
    private static readonly TimeSpan s_vanishGrace = TimeSpan.FromSeconds(4);

    private static string s_installFolder = "";
    private static OverlayDesign s_design = new();
    private static int s_designVersion;
    private static DispatcherTimer? s_timer;
    private static readonly Dictionary<IntPtr, LayeredWindow> s_windows = [];
    private static readonly Dictionary<IntPtr, DateTime> s_missingSince = [];
    private static DateTime s_lastScan = DateTime.MinValue;
    private static List<IntPtr> s_games = [];
    private static string s_clock = "";
    private static Mutex? s_mutex;
    private static bool s_drawing;
    private static DateTime s_designFileTime;
    private static DateTime s_serverFileTime;
    private static readonly List<(uint Pid, IntPtr Hook)> s_processHooks = [];
    private static readonly List<IntPtr> s_globalHooks = [];
    private static Native.WinEventProc? s_hookProc;

    /// <summary>True when this is the overlay on its own, without Ahziael's window.</summary>
    internal static bool Standalone { get; private set; }

    /// <summary>The designer button on the game was clicked.</summary>
    internal static event Action? DesignRequested;

    /// <summary>The game window whose sparkle button was clicked last.</summary>
    internal static IntPtr RequestedGame { get; private set; }

    /// <summary>Opens the designer for this game window, as its sparkle button does (an overlay button can too).</summary>
    internal static void RequestDesigner(IntPtr game) {
        RequestedGame = game;
        DesignRequested?.Invoke();
    }

    private static readonly Dictionary<IntPtr, IntPtr> s_buttonGame = [];

    /// <summary>Each overlay button's click window: the button, whether it is the server's, and its game window.</summary>
    private static readonly Dictionary<IntPtr, (OverlayElement Element, bool Server, IntPtr Game)> s_hitTargets = [];

    /// <summary>
    /// True while the design is being changed right on the game (<see cref="OverlayGameEditor"/>): the overlay steps
    /// aside, the editing window over the game shows it instead.
    /// </summary>
    internal static bool EditingInGame { get; set; }

    /// <summary>Goes up whenever the design changes (here or in the other copy's file), so an open designer can follow.</summary>
    internal static int DesignStamp { get; private set; }

    /// <summary>Where a game window's inside is on the screen, in pixels, and its scale; null when it is gone or minimized.</summary>
    internal static (int X, int Y, int Width, int Height, double Scale)? GameClient(IntPtr game) {
        if (!OperatingSystem.IsWindows() || game == IntPtr.Zero || !Native.IsWindow(game) || Native.IsIconic(game) || !Native.GetClientRect(game, out var r)) {
            return null;
        }

        var origin = new Native.POINT();
        Native.ClientToScreen(game, ref origin);
        var dpi = Native.GetDpiForWindow(game);

        return (origin.X, origin.Y, r.Right - r.Left, r.Bottom - r.Top, dpi == 0 ? 1.0 : dpi / 96.0);
    }

    /// <summary>The size of each open game window (device-independent pixels), for the designer's picture.</summary>
    internal static IReadOnlyList<(double Width, double Height)> GameSizes { get; private set; } = [];

    /// <summary>Game windows the overlay is drawing on now.</summary>
    internal static int GameWindows { get; private set; }

    /// <summary>False while the other copy (Ahziael, or Ahziael Overlay on its own) is the one drawing.</summary>
    internal static bool IsDrawing => s_drawing;

    internal static string Version => SelfUpdate.CurrentVersion == new Version(0, 0, 0) ? "test build" : $"v{SelfUpdate.CurrentVersion}";

    internal static OverlayDesign Design => s_design;

    /// <summary>The texts and boxes the linked server sent (drawn after the Ahziael line, before the player's own).</summary>
    internal static IReadOnlyList<OverlayElement> ServerElements { get; private set; } = [];

    /// <summary>True when the linked server needs the overlay: then the player's switch can't hide anything.</summary>
    internal static bool ServerRequires { get; private set; }

    /// <summary>Whether the player's own texts and boxes show: always while the server needs the overlay.</summary>
    internal static bool ShowsMine(OverlayDesign design) => design.Enabled || ServerRequires;

    // What the link last heard from the server, from the file the drawing copy writes, so both copies know it.
    private static void LoadFromServer() {
        var path = Path.Combine(s_installFolder, OverlayDesign.FROM_SERVER_FILE);
        var time = FileTime(path);
        if (time == s_serverFileTime) {
            return;
        }

        s_serverFileTime = time;
        var (elements, required) = (new List<OverlayElement>(), false);
        try {
            if (time != DateTime.MinValue && new FileInfo(path).Length < 2 * 1024 * 1024) {
                var json = JsonNode.Parse(File.ReadAllText(path));
                required = (bool?) json?["required"] ?? false;
                if (json?["design"] is JsonObject design && OverlayDesign.FromJson(design.ToJsonString()) is { } parsed) {
                    elements = parsed.Elements;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) {
            s_serverFileTime = DateTime.MinValue;
        }

        ServerElements = elements;
        ServerRequires = required;
        s_designVersion++;
    }

    internal static string InstallFolder => s_installFolder;

    /// <summary>Starts the overlay with the saved design. Only on Windows; elsewhere it does nothing.</summary>
    internal static void Start(string installFolder, bool standalone = false) {
        s_installFolder = installFolder;
        Standalone = standalone;
        s_design = OverlayDesign.Load(installFolder);
        s_designFileTime = FileTime(OverlayDesign.PathIn(installFolder));
        LoadFromServer();
        if (!OperatingSystem.IsWindows() || s_timer is not null) {
            return;
        }

        Link.Load(installFolder);
        s_hookProc = OnWinEvent;
        // The game coming to the front, being minimized and coming back: the overlay follows at once.
        s_globalHooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, s_hookProc, 0, 0, 0));
        s_globalHooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, s_hookProc, 0, 0, 0));
        s_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        s_timer.Tick += (_, _) => Tick();
        s_timer.Start();
        Tick();
    }

    /// <summary>Shows a changed design at once and saves it (the copy on its own picks it up from the file).</summary>
    internal static void Apply(OverlayDesign design) {
        s_design = design.Copy();
        s_designVersion++;
        DesignStamp++;
        try {
            s_design.Save(s_installFolder);
            s_designFileTime = FileTime(OverlayDesign.PathIn(s_installFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    internal static void Stop() {
        s_timer?.Stop();
        s_timer = null;
        foreach (var hook in s_globalHooks.Concat(s_processHooks.Select(h => h.Hook))) {
            Native.UnhookWinEvent(hook);
        }

        s_globalHooks.Clear();
        s_processHooks.Clear();
        StopDrawing();
    }

    private static void StopDrawing() {
        foreach (var window in s_windows.Values) {
            window.Dispose();
        }

        s_windows.Clear();
        s_missingSince.Clear();
        GameWindows = 0;
        Link.Stop();
        if (s_drawing) {
            try {
                s_mutex?.ReleaseMutex();
            }
            catch (ApplicationException) {
            }

            s_drawing = false;
        }

        s_mutex?.Dispose();
        s_mutex = null;
    }

    // Only one copy draws: the first to hold the mutex. The other tries again every scan.
    private static bool TryTakeTurn() {
        if (s_drawing) {
            return true;
        }

        try {
            s_mutex ??= new Mutex(false, MUTEX);
            s_drawing = s_mutex.WaitOne(0);
        }
        catch (AbandonedMutexException) {
            // The other copy closed without letting go: it is ours now.
            s_drawing = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) {
            s_drawing = false;
        }

        if (s_drawing) {
            Link.Start();
        }

        return s_drawing;
    }

    private static DateTime FileTime(string path) {
        try {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return DateTime.MinValue;
        }
    }

    private static void Tick() {
        try {
            if (DateTime.UtcNow - s_lastScan > TimeSpan.FromSeconds(1)) {
                s_lastScan = DateTime.UtcNow;
                Scan();
            }

            FollowAll();
        }
        catch (Exception ex) {
            Log($"Overlay: {ex.Message}");
        }
    }

    // Once a second: the design and server files the other copy may have changed, whose turn it is, the game windows,
    // and the clock.
    // A note shown on one game window for a while (an overlay button's ".link" code), by game window.
    private static readonly Dictionary<IntPtr, (string Text, DateTime Until)> s_notices = [];

    /// <summary>Shows <paramref name="text"/> on that game window for two minutes (empty: takes it away).</summary>
    internal static void ShowNotice(IntPtr game, string text) {
        if (game == IntPtr.Zero) {
            return;
        }

        if (text.Length == 0 ? s_notices.Remove(game) : true) {
            if (text.Length > 0) {
                s_notices[game] = (text, DateTime.UtcNow + TimeSpan.FromMinutes(2));
            }

            s_designVersion++;
        }
    }

    // The server's pieces, and this window's note on top in the middle when it has one.
    private static IReadOnlyList<OverlayElement> WithNotice(IntPtr game) {
        if (!s_notices.TryGetValue(game, out var notice)) {
            return ServerElements;
        }

        var note = new OverlayElement {
            Kind = OverlayElement.TEXT, Name = "Ahziael note", Text = notice.Text, Anchor = OverlayAnchor.Top, Y = 60, Size = 20, Bold = true,
            Padding = 12, CornerRadius = 10, Layer = 99, Shadow = true, BorderColor = "#C0FFD24A", BorderWidth = 1.5,
            Fill = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#FFFFFFFF" },
            Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#E0140F28" },
        };

        return [note, .. ServerElements];
    }

    private static void Scan() {
        foreach (var (game, notice) in s_notices.ToList()) {
            if (notice.Until < DateTime.UtcNow || !Native.IsWindow(game)) {
                s_notices.Remove(game);
                s_designVersion++;
            }
        }

        var designTime = FileTime(OverlayDesign.PathIn(s_installFolder));
        if (designTime != s_designFileTime) {
            s_designFileTime = designTime;
            s_design = OverlayDesign.Load(s_installFolder);
            s_designVersion++;
            DesignStamp++;
        }

        Link.Load(s_installFolder);
        LoadFromServer();
        if (!TryTakeTurn()) {
            return;
        }

        // The Ahziael line always shows, so the game windows are followed whatever the design says.
        s_games = FindGameWindows();
        GameSizes = s_games.Where(g => !Native.IsIconic(g) && Native.GetClientRect(g, out _)).Select(g => {
            Native.GetClientRect(g, out var r);
            var dpi = Native.GetDpiForWindow(g);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            return ((r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
        }).ToList();
        HookGames();
        var clock = OverlayRenderer.UsesClock(s_design) ? DateTime.Now.ToString("h:mm yyyy-MM-dd") : "";
        if (clock != s_clock) {
            s_clock = clock;
            s_designVersion++;
        }
    }

    private static void FollowAll() {
        if (!s_drawing) {
            return;
        }

        // Being changed right on the game: the editing window shows it there.
        if (EditingInGame) {
            foreach (var layered in s_windows.Values) {
                layered.Hide();
            }

            return;
        }

        var now = DateTime.UtcNow;
        foreach (var hwnd in s_windows.Keys.ToList()) {
            var there = s_games.Contains(hwnd) && Native.IsWindow(hwnd);
            if (there) {
                s_missingSince.Remove(hwnd);
                continue;
            }

            // The game is closed, or its window is still there but no longer on the screen: gone now.
            if (!s_gameRunning || (Native.IsWindow(hwnd) && !Native.IsHungAppWindow(hwnd))) {
                s_windows[hwnd].Dispose();
                s_windows.Remove(hwnd);
                s_missingSince.Remove(hwnd);
                continue;
            }

            // A window that went for a moment (a loading screen) keeps its overlay where it was for a few seconds.
            if (!s_missingSince.TryGetValue(hwnd, out var since)) {
                s_missingSince[hwnd] = now;
            }
            else if (now - since > s_vanishGrace) {
                s_windows[hwnd].Dispose();
                s_windows.Remove(hwnd);
                s_missingSince.Remove(hwnd);
            }
        }

        foreach (var game in s_games) {
            if (!Native.IsWindow(game)) {
                continue;
            }

            if (!s_windows.TryGetValue(game, out var window)) {
                // Never a new overlay on anything but a game window that is on the screen now.
                if (!IsGameWindow(game)) {
                    continue;
                }

                window = s_windows[game] = new LayeredWindow();
            }

            window.Follow(game, s_design, s_designVersion);
        }

        GameWindows = s_windows.Count;
    }

    // The game's own moves, resizes, shows and hides reach the overlay as they happen, not only on the next tick.
    private static void HookGames() {
        var pids = new HashSet<uint>();
        foreach (var game in s_games) {
            Native.GetWindowThreadProcessId(game, out var pid);
            pids.Add(pid);
        }

        foreach (var old in s_processHooks.Where(h => !pids.Contains(h.Pid)).ToList()) {
            Native.UnhookWinEvent(old.Hook);
            s_processHooks.Remove(old);
        }

        foreach (var pid in pids.Where(p => s_processHooks.All(h => h.Pid != p))) {
            var hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, s_hookProc!, pid, 0, 0);
            if (hook != IntPtr.Zero) {
                s_processHooks.Add((pid, hook));
            }
        }
    }

    private static void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) {
        // Only whole windows, and only when one of them is a game window or the foreground changed.
        if (idObject != 0 || !s_drawing) {
            return;
        }

        if (type == Native.EVENT_SYSTEM_FOREGROUND || s_windows.ContainsKey(hwnd)) {
            try {
                FollowAll();
            }
            catch (Exception ex) {
                Log($"Overlay: {ex.Message}");
            }
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
    /// <summary>When each game window was first seen; one shows its overlay only once it has been there a moment.</summary>
    private static readonly Dictionary<IntPtr, DateTime> s_firstSeen = [];

    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(2);

    /// <summary>False once no WizardGraphicalClient.exe runs: then every overlay goes at once, no waiting.</summary>
    private static bool s_gameRunning;

    /// <summary>
    /// The Wizard101 game windows (Niduus, 2026-11-11: "Have the hook wait until the EXE is found"): only windows of a
    /// running WizardGraphicalClient.exe that are really on the screen (shown, not cloaked or on another desktop, with a
    /// title, a sensible size) and have been there for a moment. Never a window of Ahziael or any other program; with no
    /// game running there is nothing to draw on, so nothing is drawn.
    /// </summary>
    private static List<IntPtr> FindGameWindows() {
        var ids = new HashSet<uint>();
        var self = (uint) Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(GAME_PROCESS)) {
            try {
                if (!process.HasExited && process.Id != self) {
                    ids.Add((uint) process.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            }
            finally {
                process.Dispose();
            }
        }

        var found = new List<IntPtr>();
        s_gameRunning = ids.Count > 0;
        if (ids.Count == 0) {
            s_firstSeen.Clear();
            return found;
        }

        var candidates = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) => {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && ids.Contains(pid) && IsGameWindow(hwnd)) {
                candidates.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        var now = DateTime.UtcNow;
        foreach (var gone in s_firstSeen.Keys.Except(candidates).ToList()) {
            s_firstSeen.Remove(gone);
        }

        foreach (var hwnd in candidates) {
            if (!s_firstSeen.TryGetValue(hwnd, out var since)) {
                s_firstSeen[hwnd] = since = now;
            }

            // Already drawn on (a loading screen that comes back) or there long enough.
            if (s_windows.ContainsKey(hwnd) || now - since >= s_settle) {
                found.Add(hwnd);
            }
        }

        return found;
    }

    /// <summary>A game window really on the screen: shown, not cloaked, not owned by another, titled, big enough.</summary>
    internal static bool IsGameWindow(IntPtr hwnd) {
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) {
            return false;
        }

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint) Environment.ProcessId) {
            return false;
        }

        // Cloaked: hidden by Windows (another virtual desktop, a window still being made).
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) {
            return false;
        }

        if (Native.GetWindowTextLengthW(hwnd) == 0) {
            return false;
        }

        // Minimized counts (its overlay waits hidden until it comes back).
        return Native.IsIconic(hwnd) || (Native.GetClientRect(hwnd, out var r) && r.Right - r.Left > 200 && r.Bottom - r.Top > 150);
    }

    /// <summary>Draws any control into a picture of this many pixels, as premultiplied BGRA rows.</summary>
    internal static byte[] Render(Control control, int width, int height, double scale) {
        // Plain (grey) smoothing for words: Windows' coloured ClearType smoothing only works on a solid background, and on
        // the see-through overlay it leaves dark streaks at the letters' upright edges over the game (Niduus, 2026-11-11).
        Avalonia.Media.RenderOptions.SetTextRenderingMode(control, Avalonia.Media.TextRenderingMode.Antialias);
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

        // Every colour within its own see-through amount, as Windows needs for premultiplied pixels: anything brighter
        // than that is drawn wrong over the game.
        for (var i = 0; i < pixels.Length; i += 4) {
            var alpha = pixels[i + 3];
            if (pixels[i] > alpha) {
                pixels[i] = alpha;
            }

            if (pixels[i + 1] > alpha) {
                pixels[i + 1] = alpha;
            }

            if (pixels[i + 2] > alpha) {
                pixels[i + 2] = alpha;
            }
        }

        return pixels;
    }

    /// <summary>The designer button: a small round button with a sparkle.</summary>
    private static Control ButtonPicture() => new Border {
        CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
        BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
        Background = new Avalonia.Media.LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = {
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(0xF0, 0x7A, 0x5C, 0xFF), 0),
                new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromArgb(0xF0, 0x60, 0x1E, 0x70), 1),
            },
        },
        Child = new Avalonia.Controls.Shapes.Path {
            Data = Avalonia.Media.Geometry.Parse("M12 2l2.4 6.6L21 11l-6.6 2.4L12 20l-2.4-6.6L3 11l6.6-2.4z M19 2l.8 2.2L22 5l-2.2.8L19 8l-.8-2.2L16 5l2.2-.8z"),
            Fill = Avalonia.Media.Brushes.White, Stretch = Avalonia.Media.Stretch.Uniform, Margin = new Thickness(6),
        },
    };

    private const int BUTTON_SIZE = 28;
    private const int BUTTON_MARGIN = 10;

    // ---------------------------------------------------------------------
    // The windows over one game window
    // ---------------------------------------------------------------------

    private sealed class LayeredWindow : IDisposable {

        private static Native.WndProc? s_proc;
        private static Native.WndProc? s_buttonProc;
        private static Native.WndProc? s_hitProc;
        private const string HIT_CLASS = "AhziaelOverlayHit";
        private static bool s_registered;
        private const string CLASS = "AhziaelOverlay";
        private const string BUTTON_CLASS = "AhziaelOverlayButton";

        // The picture (clicks go through) and the designer button (takes clicks).
        private readonly IntPtr _hwnd;
        private readonly IntPtr _button;

        // One small window over each overlay button (Niduus, 2026-11-11): the picture shows the button, and this almost
        // invisible window right over it takes its clicks. Everywhere else, clicks still go through to the game.
        private readonly List<Hit> _hits = [];
        private Native.POINT _origin;
        private int _drawnVersion = -1;
        private (int W, int H, double Scale) _drawnSize;
        private double _buttonScale;
        private Native.RECT _placed;
        private bool _shown;

        internal LayeredWindow() {
            var instance = Native.GetModuleHandleW(null);
            if (!s_registered) {
                s_proc = Native.DefWindowProcW;
                s_buttonProc = ButtonProc;
                s_hitProc = HitProc;
                var wc = new Native.WNDCLASSEX {
                    cbSize = (uint) Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                    hInstance = instance, lpszClassName = CLASS,
                };
                Native.RegisterClassExW(ref wc);
                var bc = new Native.WNDCLASSEX {
                    cbSize = (uint) Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_buttonProc),
                    hInstance = instance, lpszClassName = BUTTON_CLASS, hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_HAND),
                };
                Native.RegisterClassExW(ref bc);
                var hc = new Native.WNDCLASSEX {
                    cbSize = (uint) Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_hitProc),
                    hInstance = instance, lpszClassName = HIT_CLASS, hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_HAND),
                };
                Native.RegisterClassExW(ref hc);
                s_registered = true;
            }

            _hwnd = Native.CreateWindowExW(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                CLASS, "Ahziael overlay", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            _button = Native.CreateWindowExW(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW,
                BUTTON_CLASS, "Design the Ahziael overlay", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }

        private sealed class Hit {

            internal IntPtr Hwnd;
            internal Native.RECT Rect;

        }

        // Clicking an overlay button: never takes the keyboard from the game; shows it pressed, then runs it.
        private static IntPtr HitProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) {
            switch (msg) {
                case Native.WM_MOUSEACTIVATE:
                    return new IntPtr(Native.MA_NOACTIVATE);
                case Native.WM_LBUTTONDOWN:
                    Native.SetCapture(hwnd);
                    Shade(hwnd, 0x40, 0xFF, 0xFF, 0xFF);

                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP: {
                    Native.ReleaseCapture();
                    Shade(hwnd, 0x01, 0, 0, 0);
                    // Let go inside the button: it runs. Dragged off it first: nothing happens.
                    var x = (short) (lParam.ToInt64() & 0xFFFF);
                    var y = (short) ((lParam.ToInt64() >> 16) & 0xFFFF);
                    if (Native.GetClientRect(hwnd, out var r) && x >= 0 && y >= 0 && x < r.Right && y < r.Bottom
                        && s_hitTargets.TryGetValue(hwnd, out var target)) {
                        Dispatcher.UIThread.Post(() => OverlayButtons.Click(target.Element, target.Server, target.Game, ok => Flash(hwnd, ok)));
                    }

                    return IntPtr.Zero;
                }
            }

            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        // A quick tint over the button: green when it worked, red when it didn't.
        private static void Flash(IntPtr hwnd, bool ok) {
            if (!s_hitTargets.ContainsKey(hwnd)) {
                return;
            }

            if (ok) {
                Shade(hwnd, 0x50, 0x40, 0xE0, 0x70);
            }
            else {
                Shade(hwnd, 0x60, 0xF0, 0x40, 0x40);
            }

            DispatcherTimer.RunOnce(() => {
                if (s_hitTargets.ContainsKey(hwnd)) {
                    Shade(hwnd, 0x01, 0, 0, 0);
                }
            }, TimeSpan.FromMilliseconds(500));
        }

        // Fills a click window with one colour at this see-through (1 of 255: invisible, but it still takes clicks).
        private static void Shade(IntPtr hwnd, byte alpha, byte red, byte green, byte blue) {
            if (!Native.GetWindowRect(hwnd, out var r)) {
                return;
            }

            var (w, h) = (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            var pixels = new byte[w * h * 4];
            // Premultiplied blue, green, red, alpha.
            byte Pre(byte c) => (byte) (c * alpha / 255);
            var (b, g, rd) = (Pre(blue), Pre(green), Pre(red));
            for (var i = 0; i < pixels.Length; i += 4) {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = rd;
                pixels[i + 3] = alpha;
            }

            Draw(hwnd, pixels, w, h, new Native.POINT { X = r.Left, Y = r.Top });
        }

        // The overlay buttons of the picture just drawn: a click window over each, where it was drawn.
        private void PlaceHits(Control picture, double pixelsPerUnit, IntPtr game) {
            var found = new List<(OverlayElement Element, bool Server, Native.RECT Rect)>();
            foreach (var child in picture is Panel panel ? panel.Children : []) {
                if (child.Tag is OverlayElement { IsButton: true } element && child.Bounds.Width > 0 && child.Bounds.Height > 0) {
                    var b = child.Bounds;
                    found.Add((element, OverlayRenderer.PartOf(child) == OverlayRenderer.OverlayPanel.SERVER, new Native.RECT {
                        Left = (int) Math.Floor(b.X * pixelsPerUnit), Top = (int) Math.Floor(b.Y * pixelsPerUnit),
                        Right = (int) Math.Ceiling(b.Right * pixelsPerUnit), Bottom = (int) Math.Ceiling(b.Bottom * pixelsPerUnit),
                    }));
                }
            }

            // Drawn on top: the last one drawn is the one clicked where two lap over.
            found.Reverse();
            while (_hits.Count > found.Count) {
                var gone = _hits[^1];
                _hits.RemoveAt(_hits.Count - 1);
                s_hitTargets.Remove(gone.Hwnd);
                Native.DestroyWindow(gone.Hwnd);
            }

            var instance = Native.GetModuleHandleW(null);
            while (_hits.Count < found.Count) {
                var hwnd = Native.CreateWindowExW(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                    HIT_CLASS, "Ahziael overlay button", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) {
                    break;
                }

                _hits.Add(new Hit { Hwnd = hwnd });
            }

            for (var i = 0; i < _hits.Count; i++) {
                var (element, server, rect) = found[i];
                var hit = _hits[i];
                hit.Rect = rect;
                s_hitTargets[hit.Hwnd] = (element, server, game);
                var (w, h) = (Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
                var pixels = new byte[w * h * 4];
                for (var p = 3; p < pixels.Length; p += 4) {
                    pixels[p] = 1;
                }

                Draw(hit.Hwnd, pixels, w, h, new Native.POINT { X = _origin.X + rect.Left, Y = _origin.Y + rect.Top });
            }

            // New click windows go into the stacking order on the next StackOver.
            _stackedHits = -1;
        }

        private int _stackedHits = -1;

        private bool IsMine(IntPtr hwnd) => hwnd == _hwnd || hwnd == _button || _hits.Any(h => h.Hwnd == hwnd);

        private static IntPtr ButtonProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) {
            if (msg == Native.WM_LBUTTONUP) {
                RequestedGame = s_buttonGame.TryGetValue(hwnd, out var game) ? game : IntPtr.Zero;
                Dispatcher.UIThread.Post(() => DesignRequested?.Invoke());

                return IntPtr.Zero;
            }

            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        internal void Follow(IntPtr game, OverlayDesign design, int version) {
            if (_hwnd == IntPtr.Zero) {
                return;
            }

            s_buttonGame[_button] = game;

            // Only a minimized game hides it. A busy game (a loading screen) keeps it where it is.
            if (Native.IsIconic(game) || !Native.IsWindowVisible(game)) {
                Hide();

                return;
            }

            if (Native.IsHungAppWindow(game) && _shown) {
                return;
            }

            if (!Native.GetClientRect(game, out var client)) {
                return;
            }

            var origin = new Native.POINT();
            Native.ClientToScreen(game, ref origin);
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (width < 50 || height < 50) {
                return;
            }

            var dpi = Native.GetDpiForWindow(game);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var place = new Native.RECT { Left = origin.X, Top = origin.Y, Right = origin.X + width, Bottom = origin.Y + height };
            var buttonPixels = (int) Math.Round(BUTTON_SIZE * scale);
            var buttonAt = new Native.POINT {
                X = place.Right - buttonPixels - (int) Math.Round(BUTTON_MARGIN * scale),
                Y = place.Bottom - buttonPixels - (int) Math.Round(BUTTON_MARGIN * scale),
            };

            if (version != _drawnVersion || _drawnSize != (width, height, scale)) {
                var picture = OverlayRenderer.Build(design, new OverlayRenderer.Words(Version, Link.ServerLabel), showMine: ShowsMine(design), server: WithNotice(game));
                // Laid out on the design's 1280 x 720 terms and drawn bigger or smaller to fit this window.
                var fit = OverlayDesign.Fit(width / scale, height / scale);
                Draw(_hwnd, Render(picture, width, height, scale * fit), width, height, origin);
                _origin = origin;
                PlaceHits(picture, scale * fit, game);
                _drawnVersion = version;
                _drawnSize = (width, height, scale);
                _placed = place;
            }

            if (_buttonScale != scale && _button != IntPtr.Zero) {
                Draw(_button, Render(ButtonPicture(), buttonPixels, buttonPixels, scale), buttonPixels, buttonPixels, buttonAt);
                _buttonScale = scale;
            }

            if (!_placed.Equals(place)) {
                Native.SetWindowPos(_hwnd, IntPtr.Zero, origin.X, origin.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                Native.SetWindowPos(_button, IntPtr.Zero, buttonAt.X, buttonAt.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                _origin = origin;
                foreach (var hit in _hits) {
                    Native.SetWindowPos(hit.Hwnd, IntPtr.Zero, origin.X + hit.Rect.Left, origin.Y + hit.Rect.Top, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                }

                _placed = place;
            }

            StackOver(game);
        }

        // Button, picture, game: right on top of each other, wherever the game is in the stacking order.
        private void StackOver(IntPtr game) {
            var flags = Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            if (!_shown) {
                flags |= Native.SWP_SHOWWINDOW;
            }

            if (_shown && _stackedHits == _hits.Count && NextBelow(_hwnd) == game && Native.GetWindow(game, Native.GW_HWNDPREV) == _hwnd
                && Native.GetWindow(_hwnd, Native.GW_HWNDPREV) == (_hits.Count > 0 ? _hits[0].Hwnd : _button)) {
                return;
            }

            var above = Native.GetWindow(game, Native.GW_HWNDPREV);
            while (above != IntPtr.Zero && IsMine(above)) {
                above = Native.GetWindow(above, Native.GW_HWNDPREV);
            }

            // Nothing over the game, or only always-on-top windows: the top of the ordinary windows.
            var gameTopmost = (Native.GetWindowLongW(game, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            var after = above == IntPtr.Zero || (!gameTopmost && (Native.GetWindowLongW(above, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0)
                ? Native.HWND_TOP : above;
            // Each one goes right under "after", so the last placed is the highest: button, click windows, picture, game.
            Native.SetWindowPos(_hwnd, after, 0, 0, 0, 0, flags);
            foreach (var hit in _hits) {
                Native.SetWindowPos(hit.Hwnd, after, 0, 0, 0, 0, flags | Native.SWP_SHOWWINDOW);
            }

            Native.SetWindowPos(_button, after, 0, 0, 0, 0, flags);
            _shown = true;
            _stackedHits = _hits.Count;

            // Windows may refuse the order asked for. The overlay is shown only right over the game, never over
            // another window (Ahziael's or any other): if it didn't land there, it waits hidden and tries again.
            if (NextBelow(_hwnd) != game) {
                Hide();
            }
        }

        // The next window under this one that is not the button.
        private IntPtr NextBelow(IntPtr hwnd) {
            var below = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
            while (below != IntPtr.Zero && IsMine(below) && below != _hwnd) {
                below = Native.GetWindow(below, Native.GW_HWNDNEXT);
            }

            return below;
        }

        internal void Hide() {
            if (_shown) {
                Native.ShowWindow(_hwnd, Native.SW_HIDE);
                Native.ShowWindow(_button, Native.SW_HIDE);
                foreach (var hit in _hits) {
                    Native.ShowWindow(hit.Hwnd, Native.SW_HIDE);
                }

                _shown = false;
            }
        }

        private static void Draw(IntPtr hwnd, byte[] pixels, int width, int height, Native.POINT origin) {
            var screen = Native.GetDC(IntPtr.Zero);
            var memory = Native.CreateCompatibleDC(screen);
            var info = new Native.BITMAPINFO {
                biSize = (uint) Marshal.SizeOf<Native.BITMAPINFO>() - 4, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
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
                Native.UpdateLayeredWindow(hwnd, screen, ref origin, ref size, memory, ref source, 0, ref blend, Native.ULW_ALPHA);
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

            if (_button != IntPtr.Zero) {
                s_buttonGame.Remove(_button);
                Native.DestroyWindow(_button);
            }

            foreach (var hit in _hits) {
                s_hitTargets.Remove(hit.Hwnd);
                Native.DestroyWindow(hit.Hwnd);
            }

            _hits.Clear();
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
            ? "Not linked to a server yet: start the game with Play in Ahziael and it links to that server."
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

        private const string FILE = "overlay-link.json";
        private static DateTime s_fileTime = DateTime.MinValue;

        /// <summary>
        /// Links to the server the game was just started on: its Remote Admin port at the same address. Written to a
        /// file too, so the overlay on its own (or Ahziael) links there as well.
        /// </summary>
        internal static void Target(string installFolder, string host, int remoteAdminPort) {
            SetTarget(host, remoteAdminPort);
            try {
                var path = Path.Combine(installFolder, FILE);
                File.WriteAllText(path, new JsonObject { ["host"] = host.Trim(), ["port"] = remoteAdminPort }.ToJsonString());
                s_fileTime = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            }
        }

        /// <summary>The server the game was last started on, from the file; before any, this PC's own server.</summary>
        internal static void Load(string installFolder) {
            s_folder = installFolder;
            var path = Path.Combine(installFolder, FILE);
            var time = FileTime(path);
            if (time == DateTime.MinValue) {
                if (s_target is null && File.Exists(InstalledCommands.ConfigFile(installFolder))) {
                    SetTarget("127.0.0.1", Setup.GameClient.OverlayPort(installFolder, "127.0.0.1"));
                }

                return;
            }

            if (time == s_fileTime) {
                return;
            }

            s_fileTime = time;
            try {
                var json = JsonNode.Parse(File.ReadAllText(path));
                var host = (string?) json?["host"];
                var port = (int?) json?["port"] ?? RemoteAdminWire.DEFAULT_PORT;
                if (!string.IsNullOrWhiteSpace(host) && host.Length < 256 && port is > 0 and < 65536) {
                    SetTarget(host, port);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) {
            }
        }

        private static void SetTarget(string host, int port) {
            bool other;
            lock (s_lock) {
                if (s_target == (host.Trim(), port)) {
                    return;
                }

                other = s_target is not null;
                s_target = (host.Trim(), port);
                s_retarget?.Cancel();
            }

            // Another server: the last one's texts and boxes go until this one sends its own.
            if (other) {
                Heard(false, "", null);
            }
        }

        private static string s_have = "";
        private static bool s_heardRequired;
        private static string s_folder = "";

        /// <summary>
        /// Keeps what the server said (whether it needs the overlay, its own texts and boxes) in a file, so the copy
        /// that is not linking (Ahziael or the overlay on its own) shows the same.
        /// </summary>
        private static void Heard(bool required, string stamp, JsonObject? design) {
            if (stamp == s_have && required == s_heardRequired && stamp.Length > 0) {
                return;
            }

            // The same design as before, kept as it is in the file.
            if (stamp == s_have && design is null && stamp.Length > 0) {
                try {
                    var old = JsonNode.Parse(File.ReadAllText(Path.Combine(s_folder, OverlayDesign.FROM_SERVER_FILE)));
                    design = old?["design"]?.DeepClone() as JsonObject;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) {
                }
            }

            s_have = stamp;
            s_heardRequired = required;
            if (s_folder.Length == 0) {
                return;
            }

            try {
                var path = Path.Combine(s_folder, OverlayDesign.FROM_SERVER_FILE);
                File.WriteAllText(path + ".tmp", new JsonObject { ["required"] = required, ["design"] = design }.ToJsonString());
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
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

            var header = new JsonObject { ["op"] = "overlay", ["windows"] = GameWindows, ["version"] = Version, ["have"] = s_have };
            try {
                while (!stop.IsCancellationRequested) {
                    await RemoteAdminWire.WriteAsync(tls, header, null, stop);
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    wait.CancelAfter(TimeSpan.FromSeconds(30));
                    var (reply, _) = await RemoteAdminWire.ReadAsync(tls, 1 << 20, 0, wait.Token);
                    if (reply["ok"]?.GetValue<bool>() != true) {
                        return;
                    }

                    Connected = true;
                    Listen(reply);

                    // Until the next beat, overlay buttons' commands go over this same connection, each answered at once.
                    var every = Math.Clamp((int?) reply["every"] ?? 5, 2, 30);
                    var beatAt = DateTime.UtcNow + TimeSpan.FromSeconds(every);
                    while (DateTime.UtcNow < beatAt) {
                        Pending press;
                        using (var nap = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                            nap.CancelAfter(beatAt - DateTime.UtcNow);
                            try {
                                press = await s_presses.Reader.ReadAsync(nap.Token);
                            }
                            catch (OperationCanceledException) when (!stop.IsCancellationRequested) {
                                break;
                            }
                        }

                        // A click from before a dropped connection is not run late.
                        if (DateTime.UtcNow - press.At > TimeSpan.FromSeconds(20)) {
                            press.Answer.TrySetResult(new OverlayCommands.Result(false, "The server didn't answer in time.", []));
                            continue;
                        }

                        s_current = press;
                        await RemoteAdminWire.WriteAsync(tls, new JsonObject {
                            ["op"] = "cmd", ["window"] = press.Window, ["kind"] = press.Kind, ["value"] = press.Value, ["have"] = s_have,
                        }, null, stop);
                        using var answerWait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        answerWait.CancelAfter(TimeSpan.FromSeconds(20));
                        var (answer, _) = await RemoteAdminWire.ReadAsync(tls, 1 << 20, 0, answerWait.Token);
                        s_current = null;
                        press.Answer.TrySetResult(OverlayCommands.Read(answer));
                    }

                    header = new JsonObject { ["op"] = "beat", ["windows"] = GameWindows, ["have"] = s_have };
                }
            }
            finally {
                Connected = false;
                // A server from before overlay buttons hangs up on them.
                s_current?.Answer.TrySetResult(new OverlayCommands.Result(false,
                    "The server hung up. Its Imlight may be too old for command and cheat buttons: ask the host to update it.", []));
                s_current = null;
            }
        }

        // What a beat's answer says: whether the server needs the overlay, and its own texts and boxes.
        private static void Listen(JsonObject reply) {
            Required = reply["required"]?.GetValue<bool>();
            // The server's own texts and boxes; a server without any (or from before they existed) says "none".
            var stamp = (string?) reply["designStamp"] ?? "none";
            if (stamp != s_have || Required == true != s_heardRequired) {
                Heard(Required == true, stamp, stamp == "none" ? null : reply["design"]?.DeepClone() as JsonObject);
            }
        }

        // ---- Overlay buttons' commands (OverlayCommands) ----

        private sealed record Pending(long Window, string Kind, string Value, TaskCompletionSource<OverlayCommands.Result> Answer) {

            internal DateTime At { get; } = DateTime.UtcNow;

        }

        private static readonly System.Threading.Channels.Channel<Pending> s_presses =
            System.Threading.Channels.Channel.CreateBounded<Pending>(new System.Threading.Channels.BoundedChannelOptions(8) {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
            });

        private static Pending? s_current;

        /// <summary>Sends a button's command over the link, for the wizard in game window <paramref name="window"/> (0: any).</summary>
        internal static async Task<OverlayCommands.Result> SendAsync(long window, string kind, string value, CancellationToken stop) {
            if (!s_drawing || !Connected) {
                var target = s_target;

                return new OverlayCommands.Result(false, !s_drawing
                    ? "The other Ahziael copy is drawing the overlay: use the buttons on the game."
                    : target is null
                        ? "Not linked to a server yet: start the game with Play in Ahziael."
                        : $"Can't reach {target.Value.Host} right now. The host needs Imlight running, with its Remote Admin port reachable.", []);
            }

            var pending = new Pending(window, kind, value, new TaskCompletionSource<OverlayCommands.Result>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!s_presses.Writer.TryWrite(pending)) {
                return new OverlayCommands.Result(false, "Too many clicks at once. Wait a moment.", []);
            }

            try {
                return await pending.Answer.Task.WaitAsync(TimeSpan.FromSeconds(30), stop);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) {
                return new OverlayCommands.Result(false, "The server didn't answer in time.", []);
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
        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOOWNERZORDER = 0x0200;
        internal const uint GW_HWNDPREV = 3;
        internal const uint GW_HWNDNEXT = 2;
        internal const int DWMWA_CLOAKED = 14;

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        internal static extern int GetWindowTextLengthW(IntPtr hwnd);
        internal const int GWL_EXSTYLE = -20;
        internal const uint WM_LBUTTONUP = 0x0202;
        internal const uint WM_LBUTTONDOWN = 0x0201;
        internal const uint WM_MOUSEACTIVATE = 0x0021;
        internal const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        internal static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        internal static readonly IntPtr IDC_HAND = new(32649);
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        internal const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        internal const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        internal const uint EVENT_OBJECT_SHOW = 0x8002;
        internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

        internal delegate void WinEventProc(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern bool IsHungAppWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int GetWindowLongW(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        internal static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

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
