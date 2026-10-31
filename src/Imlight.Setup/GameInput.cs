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
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Sends a command to the Wizard101 game running on this PC by typing it into the game's chat, the way a player would:
/// Enter, the command, Enter (Niduus, 2026-10-31: members on someone else's server "easily execute their commands via
/// hook to game client from the base application"). It works on any server, as the server sees an ordinary chat
/// command and checks the account's rank itself. Nothing in the game's files or memory is changed: only key presses go
/// to its window, which is brought to the front for a moment and then given back.
/// </summary>
internal static class GameInput {

    private const string CLIENT_PROCESS = "WizardGraphicalClient";

    // One command at a time, in the order sent, away from the app's window.
    private static readonly BlockingCollection<string> s_queue = [];
    private static Thread? s_worker;
    private static readonly object s_lock = new();

    /// <summary>A problem found while typing (the game closed, Windows refused the window); taken by the app to show.</summary>
    internal static string? Problem { get; private set; }

    internal static string? TakeProblem() {
        var problem = Problem;
        Problem = null;

        return problem;
    }

    /// <summary>Whether Wizard101 is open on this PC with a window to type into.</summary>
    internal static bool GameIsOpen() => GameWindow() != IntPtr.Zero;

    /// <summary>
    /// Queues commands to type into the game's chat. Throws when the game is not open, so the person is told at once.
    /// </summary>
    internal static void Send(params string[] commands) {
        if (!OperatingSystem.IsWindows()) {
            throw new PanelException("Sending commands to the game works on Windows only.");
        }

        if (!GameIsOpen()) {
            throw new PanelException("Open Wizard101 and log in first: Ahziael types the command into the game's chat for you.");
        }

        lock (s_lock) {
            if (s_worker is null) {
                s_worker = new Thread(Work) { IsBackground = true, Name = "Ahziael game input" };
                s_worker.Start();
            }
        }

        foreach (var command in commands) {
            // Chat commands start with a dot.
            var line = command.Trim().ReplaceLineEndings(" ");
            s_queue.Add(line.StartsWith('.') ? line : "." + line);
        }
    }

    private static void Work() {
        foreach (var line in s_queue.GetConsumingEnumerable()) {
            try {
                Type(line);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
                Problem = ex.Message;
            }

            // The game reads one chat line at a time; a short gap keeps quick commands in order.
            Thread.Sleep(250);
        }
    }

    // The game's main window: the first open game that has one.
    private static IntPtr GameWindow() {
        foreach (var process in Process.GetProcessesByName(CLIENT_PROCESS)) {
            using (process) {
                try {
                    if (process.MainWindowHandle != IntPtr.Zero) {
                        return process.MainWindowHandle;
                    }
                }
                catch (InvalidOperationException) {
                    // Closed meanwhile.
                }
            }
        }

        return IntPtr.Zero;
    }

    private static void Type(string line) {
        var window = GameWindow();
        if (window == IntPtr.Zero) {
            throw new InvalidOperationException($"Wizard101 closed before \"{line}\" was sent.");
        }

        var before = GetForegroundWindow();
        Bring(window);
        Thread.Sleep(120);
        if (GetForegroundWindow() != window) {
            throw new InvalidOperationException("Windows did not let Ahziael bring Wizard101 to the front. Click the game once, then send the command again.");
        }

        // Enter opens the chat box; give it a moment before the letters arrive.
        SendAll(Key(VK_RETURN));
        Thread.Sleep(80);
        SendAll([.. line.SelectMany(Unicode)]);
        Thread.Sleep(30);
        SendAll(Key(VK_RETURN));
        Thread.Sleep(80);

        // Back to where the person was, such as Ahziael.
        if (before != IntPtr.Zero && before != window) {
            Bring(before);
        }
    }

    private static void SendAll(INPUT[] inputs) {
        if (inputs.Length > 0 && SendInput((uint) inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not pass the keys to Wizard101. If the game runs as administrator, run Ahziael as administrator too.");
        }
    }

    // Windows only lets the program in front pass the front on; a press of Alt and joining input with the window in
    // front lets Ahziael do it, as Windows' own task switcher does.
    private static void Bring(IntPtr window) {
        if (IsIconic(window)) {
            ShowWindow(window, SW_RESTORE);
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(window);
        }
        finally {
            if (attached) {
                AttachThreadInput(ownThread, foregroundThread, false);
            }
        }
    }

    private static INPUT[] Key(ushort key) {
        var scan = (ushort) MapVirtualKey(key, 0);

        return [
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = scan } } },
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = scan, dwFlags = KEYEVENTF_KEYUP } } },
        ];
    }

    // A letter as itself, whatever the keyboard layout.
    private static INPUT[] Unicode(char c) => [
        new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } },
        new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } },
    ];

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
    private const byte VK_MENU = 0x12;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT {

        public uint type;
        public InputUnion u;

    }

    // The union is as big as its biggest member (MOUSEINPUT), so SendInput gets the size it expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion {

        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT {

        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT {

        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;

    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool join);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

}
