#if HELLHOUND
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Hellhound, the bots' own app (Niduus, 2026-11-22: "Everything bot related goes in this"; card "Built from Ahziael's own bot
/// pages"): Ahziael's bot pages in a window of its own, drawn by the app itself (no browser, nothing from Microsoft), with
/// the kill switch, a live Watch page and its own updates. It reads the server Ahziael installed and never starts or stops it.
/// <para>
/// Hellhound.exe                  opens the window
/// Hellhound.exe --dir "D:\Ahz"   another Ahziael install folder than %LOCALAPPDATA%\ImlightManager
/// </para>
/// </summary>
internal static class HellhoundStart {

    private static Mutex? s_single;

    /// <summary>Where Hellhound keeps its own things (its log and its GitHub key): %LOCALAPPDATA%\Hellhound.</summary>
    internal static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hellhound");

    // The open window's name for "show yourself": a second start of Hellhound asks the open one to come to the front.
    // (The web page Hellhound of v0.2 and v0.3 held a lock called Local\Hellhound and kept running hidden after its browser
    // window closed, so v0.4 quietly closed at once on a PC that had run it: Niduus, 2026-11-22 "it's not loading".)
    private const string LOCK = "Local\\Hellhound.App";
    private const string SHOW = "Local\\Hellhound.Show";

    internal static int Run(string[] args) {
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.INSTALL_FOLDER_NAME);
        try {
            var afterUpdate = false;
            for (var i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--dir" when i + 1 < args.Length:
                        install = Path.GetFullPath(args[++i]);
                        break;
                    case "--after-update":
                        afterUpdate = true;
                        break;
                }
            }

            StopOlderCopies();

            // One Hellhound at a time. After it updated itself, the new copy waits for the old one to step aside.
            s_single = new Mutex(true, LOCK, out var first);
            if (!first) {
                try {
                    first = s_single.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
                }
                catch (AbandonedMutexException) {
                    first = true;
                }
            }

            if (!first) {
                // Hellhound is open already: bring its window to the front instead of closing without a word.
                Log("Hellhound was opened again while it was already open: brought the open window to the front.");
                AskOpenOneToShow();

                return 0;
            }

            HellhoundUpdate.Args = args;
            HellhoundUpdate.Tidy();
            Log($"Hellhound {HellhoundUpdate.CurrentVersion} opened for {install}.");
            ListenForShow();

            return ManagerApp.Run(install);
        }
        catch (Exception ex) {
            // Anything that goes wrong before the window shows is written down and said in a box, never a silent close.
            Log($"Hellhound could not open: {ex}");
            AppLogs.Start(install);
            StartupSafety.CouldNotOpen(install, ex, ManagerApp.WindowShown);

            return 1;
        }
    }

    /// <summary>
    /// Closes Hellhounds older than this one that are still running: the web page Hellhound (v0.2, v0.3) had no window of
    /// its own and kept running hidden, holding its lock and its port. A copy of the same or a newer version is left alone.
    /// </summary>
    private static void StopOlderCopies() {
        if (typeof(HellhoundStart).Assembly.GetName().Version is not { } mine || (mine.Major == 0 && mine.Minor == 0)) {
            return;
        }

        var me = Environment.ProcessId;
        // Downloaded again, the exe can be "Hellhound (1).exe", so every Hellhound* program counts.
        foreach (var other in Process.GetProcesses()) {
            using (other) {
                try {
                    if (other.Id == me || !other.ProcessName.StartsWith("Hellhound", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    var info = other.MainModule?.FileVersionInfo;
                    var known = info is not null && (info.FileMajorPart != 0 || info.FileMinorPart != 0);
                    var older = known && (info!.FileMajorPart, info.FileMinorPart).CompareTo((mine.Major, mine.Minor)) < 0;
                    // No version to read and no window: only the old web page Hellhound runs like that.
                    var hiddenUnknown = !known && other.MainWindowHandle == IntPtr.Zero;
                    if (!older && !hiddenUnknown) {
                        continue;
                    }

                    other.Kill();
                    other.WaitForExit(5000);
                    Log($"Closed an older Hellhound that was still running ({(known ? $"v{info!.FileMajorPart}.{info.FileMinorPart}" : "old web page version")}).");
                }
                catch (Exception ex) {
                    Log($"Could not close an older Hellhound that is still running: {ex.Message}");
                }
            }
        }
    }

    private static void AskOpenOneToShow() {
        try {
            if (EventWaitHandle.TryOpenExisting(SHOW, out var show)) {
                using (show) {
                    show.Set();
                }
            }
        }
        catch (Exception) {
            // The open window stays where it is.
        }
    }

    /// <summary>Waits for a second start of Hellhound and brings this window to the front when it asks.</summary>
    private static void ListenForShow() {
        EventWaitHandle show;
        try {
            show = new EventWaitHandle(false, EventResetMode.AutoReset, SHOW);
        }
        catch (Exception) {
            return;
        }

        var listen = new Thread(() => {
            while (show.WaitOne()) {
                Dispatcher.UIThread.Post(() => {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) {
                        if (window.WindowState == WindowState.Minimized) {
                            window.WindowState = WindowState.Normal;
                        }

                        window.Show();
                        window.Activate();
                    }
                });
            }
        }) { IsBackground = true, Name = "Hellhound show" };
        listen.Start();
    }

    /// <summary>Lets a newer copy take over (after an update).</summary>
    internal static void LetGo() {
        try {
            s_single?.ReleaseMutex();
        }
        catch (ApplicationException) {
        }
    }

    /// <summary>A line in %LOCALAPPDATA%\Hellhound\hellhound.log: what it changed and when (switches, pauses, updates).</summary>
    internal static void Log(string line) {
        try {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(Path.Combine(DataFolder, "hellhound.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception) {
            // Only the note is lost.
        }
    }

}
#endif
