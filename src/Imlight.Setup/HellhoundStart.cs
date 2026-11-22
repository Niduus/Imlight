#if HELLHOUND
using System;
using System.IO;
using System.Threading;

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

    internal static int Run(string[] args) {
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.INSTALL_FOLDER_NAME);
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

        // One Hellhound at a time. After it updated itself, the new copy waits for the old one to step aside.
        s_single = new Mutex(true, "Local\\Hellhound", out var first);
        if (!first) {
            try {
                first = s_single.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException) {
                first = true;
            }
        }

        if (!first) {
            return 0;
        }

        HellhoundUpdate.Args = args;
        HellhoundUpdate.Tidy();
        Log($"Hellhound {HellhoundUpdate.CurrentVersion} opened for {install}.");

        return ManagerApp.Run(install);
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
