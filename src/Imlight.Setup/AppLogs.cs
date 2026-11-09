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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// One folder for every crash and error (Niduus, 2026-11-09: "Make a logs directory for everything related to Imlight,
/// Aurorium, Imcodec, spiraldb and Ahziael"): <c>&lt;install folder&gt;\Logs</c>. Ahziael writes its own errors and crashes
/// to <c>Logs\Ahziael</c>, a server that stops by itself leaves a crash report in <c>Logs\Server</c> (Imcodec and SpiralDB
/// run inside the server, so their errors are in the server's log), and "Make a zip to send" puts all of it together with
/// the newest server logs, the setup log and Aurorium's logs in one zip.
/// </summary>
/// <remarks>
/// Nothing leaves the computer by itself. Before anything is written, the Windows user name in folder paths and
/// internet addresses (other than this computer's own) are blanked out, so a zip can be sent without personal details.
/// </remarks>
internal static partial class AppLogs {

    internal const string FOLDER = "Logs";
    private const int KEEP_DAYS = 30;
    private const int SERVER_LOG_DAYS = 3;

    private static string? s_installFolder;
    private static readonly object s_lock = new();

    internal static string Folder(string installFolder) => Path.Combine(installFolder, FOLDER);

    /// <summary>
    /// Starts writing this program's crashes to <c>Logs\&lt;part&gt;</c>, such as Ahziael's window or the program that runs
    /// the server. Errors nobody waited for are written down too, so nothing goes missing.
    /// </summary>
    internal static void Start(string installFolder) {
        if (s_installFolder is not null) {
            return;
        }

        s_installFolder = installFolder;
        WriteReadme(installFolder);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error("Ahziael stopped because of an error", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) => {
            Error("An error in the background (Ahziael kept running)", e.Exception);
            e.SetObserved();
        };
        _ = Task.Run(CleanUp);
    }

    private const string STOP_ASKED = ".server-stop-asked";

    /// <summary>Notes that the server is being stopped on purpose, so its stopping is not reported as a crash.</summary>
    internal static void ServerStopAsked(string installFolder) {
        try {
            Directory.CreateDirectory(Folder(installFolder));
            File.WriteAllText(Path.Combine(Folder(installFolder), STOP_ASKED), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception) {
            // Without the note a stop may show up as a crash report; nothing else changes.
        }
    }

    /// <summary>True (once) when the server was stopped on purpose in the last few minutes.</summary>
    private static bool TakeStopAsked(string installFolder) {
        var file = Path.Combine(Folder(installFolder), STOP_ASKED);
        try {
            if (!File.Exists(file)) {
                return false;
            }

            var recent = File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-5);
            File.Delete(file);

            return recent;
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>Writes an error to today's Ahziael log (Logs\Ahziael\ahziael-yyyy-MM-dd.txt).</summary>
    internal static void Error(string what, Exception? ex, string? extra = null) {
        if (s_installFolder is not { } install) {
            return;
        }

        try {
            var text = new StringBuilder()
                .Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Edition.NAME} {SelfUpdate.CurrentVersion}: {what}").AppendLine()
                .AppendLine(ex?.ToString() ?? extra ?? "(no details)")
                .AppendLine();
            var folder = Path.Combine(Folder(install), "Ahziael");
            lock (s_lock) {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"ahziael-{DateTime.Now:yyyy-MM-dd}.txt"), Scrub(text.ToString()));
            }
        }
        catch (Exception) {
            // Writing the log must never be what stops the app.
        }
    }

    /// <summary>
    /// A crash report after the server stopped by itself: its exit code and the last lines of its log, in
    /// Logs\Server\server-stopped-yyyy-MM-dd HH-mm-ss.txt. Returns the file, or null when it could not be written.
    /// </summary>
    internal static string? ServerStopped(string installFolder, int exitCode, string serverLogFolder) {
        if (TakeStopAsked(installFolder)) {
            return null;
        }

        try {
            var lines = NewestLines(serverLogFolder, 400);
            var text = new StringBuilder()
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} The Imlight server stopped by itself (exit code {exitCode}).")
                .AppendLine($"{Edition.NAME} {SelfUpdate.CurrentVersion}")
                .AppendLine()
                .AppendLine("The last lines the server wrote:")
                .AppendLine(string.Join(Environment.NewLine, lines));
            var folder = Path.Combine(Folder(installFolder), "Server");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"server-stopped-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt");
            File.WriteAllText(file, Scrub(text.ToString()));

            return file;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Puts every log worth sending into one zip on the desktop: the Logs folder, the newest server logs (and the
    /// Bazaar bot's), the setup log and Aurorium's logs, each with personal details blanked out.
    /// </summary>
    internal static string MakeZip(string installFolder, string serverLogFolder) {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) {
            desktop = Folder(installFolder);
            Directory.CreateDirectory(desktop);
        }

        var target = Path.Combine(desktop, $"Ahziael logs {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        var files = new List<(string Name, string Path)>();
        void AddFolder(string folder, string into, int? days, string pattern = "*") {
            if (!Directory.Exists(folder)) {
                return;
            }

            var since = days is { } d ? DateTime.Now.AddDays(-d) : DateTime.MinValue;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles(pattern, SearchOption.AllDirectories)
                         .Where(f => f.LastWriteTime >= since && IsText(f.Name))) {
                files.Add((into + "/" + Path.GetRelativePath(folder, file.FullName).Replace('\\', '/'), file.FullName));
            }
        }

        AddFolder(Folder(installFolder), "Logs", null);
        AddFolder(serverLogFolder, "Server logs", SERVER_LOG_DAYS);
        if (File.Exists(Path.Combine(installFolder, "setup-log.txt"))) {
            files.Add(("setup-log.txt", Path.Combine(installFolder, "setup-log.txt")));
        }

        if ((Aurorium.Load(installFolder).Folder ?? Aurorium.DefaultFolder(installFolder)) is { } aurorium && Directory.Exists(aurorium)) {
            foreach (var file in new DirectoryInfo(aurorium).EnumerateFiles("*", SearchOption.AllDirectories)
                         .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                                     || f.DirectoryName?.Contains("log", StringComparison.OrdinalIgnoreCase) == true && IsText(f.Name))
                         .Where(f => f.LastWriteTime >= DateTime.Now.AddDays(-SERVER_LOG_DAYS))) {
                files.Add(("Aurorium/" + Path.GetRelativePath(aurorium, file.FullName).Replace('\\', '/'), file.FullName));
            }
        }

        if (files.Count == 0) {
            throw new PanelException("There are no logs yet. Start the server or use Ahziael for a while, then try again.");
        }

        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create)) {
            using (var readme = zip.CreateEntry("README.txt").Open()) {
                readme.Write(Encoding.UTF8.GetBytes(
                $"Logs from {Edition.NAME} {SelfUpdate.CurrentVersion}, made {DateTime.Now:yyyy-MM-dd HH:mm}.\r\n" +
                    "Windows user names and internet addresses were blanked out before they went in.\r\n"));
            }

            foreach (var (name, path) in files.DistinctBy(f => f.Name)) {
                try {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = Scrub(reader.ReadToEnd());
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    entry.Write(Encoding.UTF8.GetBytes(text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // A log that is locked or gone is left out.
                }
            }
        }

        return target;
    }

    /// <summary>Blanks out the Windows user name in paths and every internet address that is not this computer's own.</summary>
    internal static string Scrub(string text) {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        text = UserFolder().Replace(text, m => m.Groups[1].Value + "<you>");
        text = Address().Replace(text, m => m.Value.StartsWith("127.", StringComparison.Ordinal) || m.Value == "0.0.0.0" ? m.Value : "x.x.x.x");

        return Address6().Replace(text, "x:x:x:x");
    }

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)(?!Public\b)(?!<you>)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Address();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b")]
    private static partial Regex Address6();

    private static bool IsText(string name)
        => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".log" or ".json" or ".jsonl" or "";

    private static IEnumerable<string> NewestLines(string folder, int count) {
        var file = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (file is null) {
            return ["(the server wrote no log)"];
        }

        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd().Split('\n').TakeLast(count).ToList();
    }

    private static void WriteReadme(string installFolder) {
        try {
            var file = Path.Combine(Folder(installFolder), "README.txt");
            if (File.Exists(file)) {
                return;
            }

            Directory.CreateDirectory(Folder(installFolder));
            File.WriteAllText(file,
                "Crash and error logs for Imlight, Ahziael, Aurorium, Imcodec and SpiralDB.\r\n\r\n" +
                "Ahziael\\  errors and crashes of the Ahziael app.\r\n" +
                "Server\\   a report each time the server stopped by itself, with the last lines it wrote.\r\n" +
                "          (Imcodec and SpiralDB run inside the server, so their errors are in these reports and in Server\\logs.)\r\n\r\n" +
                "To send them: in Ahziael go to Server > Logs and click \"Make a zip to send\". The zip lands on your desktop\r\n" +
                "with the newest server logs, the setup log and Aurorium's logs too. Windows user names and internet\r\n" +
                "addresses are blanked out. Logs older than 30 days are deleted by themselves.\r\n");
        }
        catch (Exception) {
            // The folder works without its note.
        }
    }

    /// <summary>Ahziael's and the server's crash reports older than a month are deleted, so the folder stays small.</summary>
    private static void CleanUp() {
        try {
            if (s_installFolder is not { } install || !Directory.Exists(Folder(install))) {
                return;
            }

            foreach (var file in new DirectoryInfo(Folder(install)).EnumerateFiles("*.txt", SearchOption.AllDirectories)
                         .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-KEEP_DAYS))) {
                file.Delete();
            }
        }
        catch (Exception) {
            // Old logs that can't be deleted now are tried again next time.
        }
    }

}
