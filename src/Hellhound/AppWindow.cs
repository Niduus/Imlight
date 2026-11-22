using System.Diagnostics;

namespace Hellhound;

/// <summary>
/// Hellhound's own window (Niduus, 2026-11-22: "the exe isn't an actual program. I was expecting a program"). The page opens
/// as an app window of its own (Windows' built-in Edge engine in app mode: no tabs, no address bar, Hellhound's icon and
/// name), with a profile folder of its own under %LOCALAPPDATA%\Hellhound, so the window is Hellhound's alone. Closing the
/// window closes Hellhound. Without Edge or Chrome it falls back to the default browser and keeps running until the PC
/// signs out or it is ended in Task Manager.
/// </summary>
internal static class AppWindow {

    internal static string DataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hellhound");

    /// <summary>Opens the window and returns its process (null when only the default browser could open it).</summary>
    internal static Process? Open(string url) {
        if (Engine() is { } engine) {
            var start = new ProcessStartInfo(engine) { UseShellExecute = false };
            foreach (var arg in new[] {
                         "--app=" + url, "--user-data-dir=" + Path.Combine(DataFolder, "window"), "--window-size=1240,940",
                         "--no-first-run", "--no-default-browser-check", "--disable-features=Translate" }) {
                start.ArgumentList.Add(arg);
            }

            try {
                return Process.Start(start);
            }
            catch (Exception e) {
                Log("Could not open the window: " + e.Message);
            }
        }

        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) {
            Log("Could not open the browser either: " + e.Message);
        }

        return null;
    }

    // Edge first (on every Windows 10 and 11 PC), then Chrome.
    private static string? Engine() {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var roots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var relative in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" }) {
            foreach (var root in roots.Where(r => r.Length > 0)) {
                var path = Path.Combine(root, relative);
                if (File.Exists(path)) {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>A line in %LOCALAPPDATA%\Hellhound\hellhound.log (the app has no console window to say it in).</summary>
    internal static void Log(string line) {
        try {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(Path.Combine(DataFolder, "hellhound.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception) {
        }
    }
}
