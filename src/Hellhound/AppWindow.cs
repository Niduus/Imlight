using System.Diagnostics;

namespace Hellhound;

/// <summary>
/// Hellhound's own window (Niduus, 2026-11-22: "the exe isn't an actual program. I was expecting a program"). The page opens
/// as an app window of its own (Windows' built-in Edge engine in app mode: no tabs, no address bar, Hellhound's icon and
/// name), with a profile folder of its own under %LOCALAPPDATA%\Hellhound, so the window is Hellhound's alone. Closing the
/// window closes Hellhound. Without Edge or Chrome it falls back to the default browser and keeps running until the PC
/// signs out or it is ended in Task Manager.
///
/// Never signed in, never synced (Niduus, 2026-11-22, on Edge's "we are now syncing your browsing data" popup: "I DO NOT
/// WANT THIS"): Edge signs a new profile in with the Windows account by itself, so Hellhound opens Edge InPrivate (an
/// InPrivate window is never signed in and never syncs), with sync and sign-in switched off as well, and removes the
/// old "window" profile folder that Edge had signed in.
/// </summary>
internal static class AppWindow {

    internal static string DataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hellhound");

    /// <summary>Opens the window and returns its process (null when only the default browser could open it).</summary>
    internal static Process? Open(string url) {
        if (Engine() is { } engine) {
            ForgetSignedInProfile();
            var start = new ProcessStartInfo(engine) { UseShellExecute = false };
            var edge = Path.GetFileName(engine).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase);
            var args = new List<string> {
                "--app=" + url, "--user-data-dir=" + Path.Combine(DataFolder, "private-window"), "--window-size=1240,940",
                "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-signin-promo",
                "--disable-features=Translate,msImplicitSignin,msEdgeSync,msSigninPromo,EdgeSignin",
            };
            if (edge) {
                args.Add("--inprivate");
            }

            foreach (var arg in args) {
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

    // The first Hellhound window used %LOCALAPPDATA%\Hellhound\window, which Edge signed in to the Windows account.
    private static void ForgetSignedInProfile() {
        var old = Path.Combine(DataFolder, "window");
        try {
            if (Directory.Exists(old)) {
                Directory.Delete(old, recursive: true);
                Log("Removed the old window profile that Edge had signed in.");
            }
        }
        catch (Exception e) {
            Log("Could not remove the old window profile yet: " + e.Message);
        }
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
