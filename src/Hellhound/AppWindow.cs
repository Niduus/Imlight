using System.Diagnostics;

namespace Hellhound;

/// <summary>
/// Hellhound's own window (Niduus, 2026-11-22: "the exe isn't an actual program. I was expecting a program"). The page opens
/// as an app window of its own (no tabs, no address bar) in Google Chrome or another Chromium browser (Brave, Vivaldi,
/// Chromium), with a profile folder of its own under %LOCALAPPDATA%\Hellhound that never signs in or syncs. Closing the
/// window closes Hellhound.
///
/// Never Microsoft Edge (the standing "no Microsoft stuff in the apps" rule; Hellhound v0.2 opened Edge, which signed
/// Niduus in and started syncing: "I DO NOT WANT THIS"). Without a Chromium browser it opens Firefox, or the default
/// browser unless that is Edge; failing all of those it says where the page is.
/// </summary>
internal static class AppWindow {

    internal static string DataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hellhound");

    /// <summary>Opens the window and returns its process (null when it opened in an ordinary browser window or not at all).</summary>
    internal static Process? Open(string url) {
        ForgetEdgeProfiles();
        if (Find(s_appBrowsers) is { } browser) {
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var arg in new[] {
                         "--app=" + url, "--user-data-dir=" + Path.Combine(DataFolder, "app-window"), "--window-size=1240,940",
                         "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-signin-promo",
                         "--disable-features=Translate", "--disable-background-networking" }) {
                start.ArgumentList.Add(arg);
            }

            try {
                return Process.Start(start);
            }
            catch (Exception e) {
                Log("Could not open the window: " + e.Message);
            }
        }

        if (Find(s_otherBrowsers) is { } firefox) {
            try {
                Process.Start(new ProcessStartInfo(firefox) { UseShellExecute = false, ArgumentList = { "-new-window", url } })?.Dispose();
                return null;
            }
            catch (Exception e) {
                Log("Could not open Firefox: " + e.Message);
            }
        }

        if (!DefaultIsEdge()) {
            try {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return null;
            }
            catch (Exception e) {
                Log("Could not open the default browser: " + e.Message);
            }
        }

        Log("No browser to open Hellhound in (Edge is never used). Open " + url + " in Chrome or Firefox.");
        if (OperatingSystem.IsWindows()) {
            _ = MessageBoxW(IntPtr.Zero, "Hellhound needs Google Chrome or Firefox to show its window (it never uses Microsoft Edge).\n\n" +
                "Install one of them, or open this address in one: " + url, "Hellhound", 0x40);
        }

        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    // Chromium browsers that open a page as an app window (--app), Microsoft's left out on purpose.
    private static readonly string[] s_appBrowsers = [
        @"Google\Chrome\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe", @"Vivaldi\Application\vivaldi.exe",
        @"Chromium\Application\chrome.exe",
    ];

    private static readonly string[] s_otherBrowsers = [@"Mozilla Firefox\firefox.exe"];

    private static string? Find(string[] relatives) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var roots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var relative in relatives) {
            foreach (var root in roots.Where(r => r.Length > 0)) {
                var path = Path.Combine(root, relative);
                if (File.Exists(path)) {
                    return path;
                }
            }
        }

        return null;
    }

    // Whether web links open in Edge on this PC (then Hellhound won't hand its page to the default browser).
    private static bool DefaultIsEdge() {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var id = key?.GetValue("ProgId") as string ?? "";

            return id.Length == 0 || id.Contains("MSEdge", StringComparison.OrdinalIgnoreCase) || id.Contains("AppX", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) {
            return true;
        }
    }

    // The Edge profile folders Hellhound v0.2 made (Edge signed them in): removed, they are Hellhound's own.
    private static void ForgetEdgeProfiles() {
        foreach (var name in new[] { "window", "private-window" }) {
            var old = Path.Combine(DataFolder, name);
            try {
                if (Directory.Exists(old)) {
                    Directory.Delete(old, recursive: true);
                    Log($"Removed the old Edge profile folder {name}.");
                }
            }
            catch (Exception e) {
                Log($"Could not remove the old Edge profile folder {name} yet: {e.Message}");
            }
        }
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
