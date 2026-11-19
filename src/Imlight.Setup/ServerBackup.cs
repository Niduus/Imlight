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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Imlight.Setup;

/// <summary>
/// Backups of everything a server owner made (Niduus, 2026-11-12: "Implement features that preserve a servers custom data
/// to avoid a total wipe"), after a player whose Ahziael would not open asked whether to uninstall it: uninstalling
/// deleted every setting, custom, cheat, overlay and add-on. Backups go to Documents\Ahziael backups, outside Ahziael's
/// folder, so an uninstall or a broken install never takes them along. One is made each day Ahziael opens, before every
/// update, before an uninstall and before a restore; a new install offers the newest one back.
/// </summary>
/// <remarks>
/// A backup is one zip with three parts: <c>Ahziael/</c> (Ahziael's own files: favorites, overlay designs, add-ons,
/// servers and the rest), <c>Server/</c> (Imlight.ini, the server-side files the tabs save, and the spiraldb-extra files
/// made or changed on this PC: customs, areas, cheats, drop tables) and <c>Database/</c> (every account and character,
/// only while the server is stopped, because a database in use can't be copied safely).
/// </remarks>
internal static class ServerBackup {

    internal const string FOLDER_NAME = "Ahziael backups";
    internal const string DAILY = "Daily";
    internal const string BEFORE_UPDATE = "Before update";
    internal const string BEFORE_UNINSTALL = "Before uninstall";
    internal const string BEFORE_RESTORE = "Before restore";
    internal const string BY_HAND = "Saved by you";

    private const string APP_PART = "Ahziael/";
    private const string SERVER_PART = "Server/";
    private const string DATABASE_PART = "Database/";
    private const string README = "What is in this backup.txt";

    // Daily and before-update backups pile up; the newest few of each are enough.
    private const int KEEP_AUTOMATIC = 7;

    // Not Ahziael's own work: made again by setup, by the update or by the game, or only logs. Source (the server's
    // code and its build files, downloaded fresh by every update) and Build were most of every backup: about 430 of
    // its 437 MB on Niduus's PC (2026-11-19, "too much space being taken over for Backups"), and zipping them took
    // nearly three minutes of each update. AppWindow is the old app window's browser data, no longer used.
    private static readonly HashSet<string> s_skippedAppFolders = new(StringComparer.OrdinalIgnoreCase) {
        "Server", "Game files", "Pending", "Logs", "Aurorium", "Import backups", "Source", "Build", "AppWindow",
    };

    // Programs, archives, logs and the leftovers of a copy or save cut short (Imlight.exe.old is a whole old app).
    private static readonly HashSet<string> s_skippedExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".exe", ".dll", ".pdb", ".zip", ".wad", ".log", ".old", ".part", ".tmp", ".new", ".saving",
    };

    // A backup cut short (Ahziael closed while it was being made) leaves its .part behind; one this old is not still going.
    private static readonly TimeSpan s_staleUnfinished = TimeSpan.FromHours(1);

    /// <summary>One backup on this PC.</summary>
    internal sealed record Backup(string Path, DateTime Made, string Kind, bool HasDatabase, long Bytes);

    /// <summary>Where backups go: Documents\Ahziael backups (or next to Ahziael's folder when there is no Documents folder).</summary>
    internal static string Folder(string installFolder) {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents)) {
            documents = Path.GetDirectoryName(Path.GetFullPath(installFolder).TrimEnd(Path.DirectorySeparatorChar)) ?? installFolder;
        }

        return Path.Combine(documents, FOLDER_NAME);
    }

    /// <summary>The backups on this PC, newest first.</summary>
    internal static List<Backup> List(string installFolder) {
        var folder = Folder(installFolder);
        if (!Directory.Exists(folder)) {
            return [];
        }

        return new DirectoryInfo(folder).EnumerateFiles("Ahziael backup *.zip")
            .Select(f => {
                var (made, kind, db) = ReadName(f.Name, f.LastWriteTime);

                return new Backup(f.FullName, made, kind, db, f.Length);
            })
            .OrderByDescending(b => b.Made)
            .ToList();
    }

    /// <summary>
    /// Makes today's backup when there is none yet. Returns it, or null when one was already made today. A backup of any
    /// kind made today counts: right after an update, the one made before it already holds everything (an update used to
    /// make a second, same backup three minutes later when Ahziael opened again).
    /// </summary>
    internal static string? MakeIfDue(string installFolder) {
        var newest = List(installFolder).FirstOrDefault();
        if (newest is not null && newest.Made.Date == DateTime.Now.Date) {
            return null;
        }

        return Make(installFolder, DAILY);
    }

    /// <summary>
    /// Backs up everything made on this PC into one zip in the backups folder and returns it. The accounts and
    /// characters go in too while the server is stopped.
    /// </summary>
    internal static string Make(string installFolder, string kind) {
        var server = InstalledCommands.ServerFolder(installFolder);
        var database = InstalledCommands.DatabaseFolder(installFolder);
        var withDatabase = database is not null && Directory.Exists(database) && !InstalledCommands.IsServerRunning(installFolder);
        var folder = Folder(installFolder);
        Directory.CreateDirectory(folder);
        var name = $"Ahziael backup {DateTime.Now:yyyy-MM-dd HH-mm-ss} - {kind}{(withDatabase ? "" : " (no characters)")}.zip";
        var target = Path.Combine(folder, name);
        var temp = target + ".part";
        var counts = new Dictionary<string, int>();
        // The backup made while an update installs is packed the quick way, so the update waits less (Niduus, 2026-11-19:
        // "make restarts and applications faster"); it is a little bigger. The daily one is packed tightest.
        var level = kind == BEFORE_UPDATE ? CompressionLevel.Fastest : CompressionLevel.Optimal;
        try {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create)) {
                void Add(string file, string entry, string part) {
                    try {
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var to = zip.CreateEntry(entry, level).Open();
                        source.CopyTo(to);
                        counts[part] = counts.GetValueOrDefault(part) + 1;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        // A file in use or gone is left out; the rest still counts.
                    }
                }

                // Ahziael's own files and folders, minus what setup and updates make again.
                var install = Path.GetFullPath(installFolder);
                if (Directory.Exists(install)) {
                    foreach (var file in Directory.EnumerateFiles(install).Where(Keep)) {
                        Add(file, APP_PART + Path.GetFileName(file), "Ahziael");
                    }

                    foreach (var sub in Directory.EnumerateDirectories(install)) {
                        var subName = Path.GetFileName(sub);
                        if (s_skippedAppFolders.Contains(subName) || Same(sub, server) || (database is not null && Same(sub, database))) {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Where(Keep)) {
                            Add(file, APP_PART + Relative(install, file), "Ahziael");
                        }
                    }
                }

                if (Directory.Exists(server)) {
                    // The settings, and the files the tabs save beside the server's program (Crown Shop, events, codes...).
                    var ini = Path.Combine(server, "Config", "Imlight.ini");
                    if (File.Exists(ini)) {
                        Add(ini, SERVER_PART + "Config/Imlight.ini", "Server");
                    }

                    foreach (var file in Directory.EnumerateFiles(server).Where(IsServerSave)) {
                        Add(file, SERVER_PART + Path.GetFileName(file), "Server");
                    }

                    // Customs, areas, cheats, drop tables: every spiraldb-extra file made or changed on this PC.
                    var extra = Path.Combine(server, "spiraldb-extra");
                    if (Directory.Exists(extra)) {
                        var shipped = ShippedExtraFiles.Load(server);
                        foreach (var file in Directory.EnumerateFiles(extra, "*", SearchOption.AllDirectories)) {
                            var relative = Relative(server, file);
                            if (shipped.IsOwn(relative, file, fallback: true)) {
                                Add(file, SERVER_PART + relative, "Server");
                            }
                        }
                    }
                }

                if (withDatabase) {
                    foreach (var file in Directory.EnumerateFiles(database!, "*", SearchOption.AllDirectories)) {
                        Add(file, DATABASE_PART + Relative(database!, file), "Database");
                    }
                }

                using var readme = new StreamWriter(zip.CreateEntry(README).Open(), Encoding.UTF8);
                readme.Write(
                    $"Backup made by {Edition.NAME} {SelfUpdate.CurrentVersion} on {DateTime.Now:yyyy-MM-dd HH:mm} ({kind}).\r\n\r\n" +
                    $"Ahziael\\  {counts.GetValueOrDefault("Ahziael")} files: favorites, overlay designs and add-ons, servers and Ahziael's other settings.\r\n" +
                    $"Server\\   {counts.GetValueOrDefault("Server")} files: Imlight.ini, customs, areas, cheats, drop tables, Crown Shop, events, codes.\r\n" +
                    (withDatabase
                        ? $"Database\\ {counts.GetValueOrDefault("Database")} files: every account and character.\r\n"
                        : "Database\\ not in this one: the server was running. Backups made while it is stopped have the characters.\r\n") +
                    "\r\nTo bring it back: in Ahziael go to Server > Ahziael > Backups and click Restore beside it.\r\n" +
                    "A new install of Ahziael also offers the newest backup.\r\n");
            }

            File.Move(temp, target, overwrite: true);
        }
        catch {
            TryDelete(temp);
            throw;
        }

        Prune(installFolder);

        return target;
    }

    /// <summary>
    /// Puts a backup back: first a backup of how things are now (so a restore can be undone), then every file of the
    /// backup at its place. The server must be stopped. Returns how many files came back.
    /// </summary>
    internal static int Restore(string installFolder, string backup) {
        if (InstalledCommands.IsServerRunning(installFolder)) {
            throw new PanelException("Stop the server first, then restore. A running server would write over what comes back.");
        }

        if (!File.Exists(backup)) {
            throw new PanelException("That backup is not there any more.");
        }

        if (File.Exists(InstalledCommands.ConfigFile(installFolder))) {
            Make(installFolder, BEFORE_RESTORE);
        }

        var install = Path.GetFullPath(installFolder);
        var server = Path.GetFullPath(InstalledCommands.ServerFolder(installFolder));
        var database = InstalledCommands.DatabaseFolder(installFolder)
            ?? Path.GetFullPath(Path.Combine(server, "..", "ImlightEmbeddedDatabase"));
        var restored = 0;
        using var zip = ZipFile.OpenRead(backup);
        // The characters come back whole: what is in the database folder now is replaced, not mixed with the backup.
        // The old one is moved aside first and only deleted once everything came back.
        string? setAside = null;
        if (zip.Entries.Any(e => e.FullName.StartsWith(DATABASE_PART, StringComparison.Ordinal)) && Directory.Exists(database)) {
            setAside = database.TrimEnd(Path.DirectorySeparatorChar) + $" (before restore {DateTime.Now:yyyy-MM-dd HH-mm-ss})";
            Directory.Move(database, setAside);
        }

        try {
            foreach (var entry in zip.Entries) {
                if (entry.FullName.EndsWith('/')) {
                    continue;
                }

                var (root, relative) = entry.FullName switch {
                    var n when n.StartsWith(APP_PART, StringComparison.Ordinal) => (install, n[APP_PART.Length..]),
                    var n when n.StartsWith(SERVER_PART, StringComparison.Ordinal) => (server, n[SERVER_PART.Length..]),
                    var n when n.StartsWith(DATABASE_PART, StringComparison.Ordinal) => (database, n[DATABASE_PART.Length..]),
                    _ => (null, ""),
                };
                if (root is null) {
                    continue;
                }

                // Only inside its own folder: a name with ".." in it can't write anywhere else.
                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                restored++;
            }
        }
        catch when (setAside is not null) {
            // Something went wrong part way: the characters as they were go back in place.
            try {
                if (Directory.Exists(database)) {
                    Directory.Delete(database, recursive: true);
                }

                Directory.Move(setAside, database);
            }
            catch (Exception) {
                // They stay in the "before restore" folder beside it, and in the Before restore backup.
            }

            throw;
        }

        if (setAside is not null) {
            try {
                Directory.Delete(setAside, recursive: true);
            }
            catch (Exception) {
                // Only takes room; the restored database is in place.
            }
        }

        return restored;
    }

    /// <summary>A backup's when, kind and whether it has the characters, from its name.</summary>
    private static (DateTime Made, string Kind, bool HasDatabase) ReadName(string name, DateTime fallback) {
        // "Ahziael backup 2026-11-12 10-30-00 - Daily (no characters).zip"
        var core = Path.GetFileNameWithoutExtension(name)["Ahziael backup ".Length..];
        var hasDatabase = !core.EndsWith(" (no characters)", StringComparison.Ordinal);
        if (!hasDatabase) {
            core = core[..^" (no characters)".Length];
        }

        var split = core.IndexOf(" - ", StringComparison.Ordinal);
        var made = split > 0 && DateTime.TryParseExact(core[..split], "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : fallback;

        return (made, split > 0 ? core[(split + 3)..] : BY_HAND, hasDatabase);
    }

    private static void Prune(string installFolder) {
        foreach (var kind in new[] { DAILY, BEFORE_UPDATE, BEFORE_RESTORE }) {
            foreach (var old in List(installFolder).Where(b => b.Kind == kind).Skip(KEEP_AUTOMATIC)) {
                TryDelete(old.Path);
            }
        }

        // Half-made backups from a closed Ahziael: never readable, and 130 to 330 MB each on Niduus's PC.
        var folder = Folder(installFolder);
        if (Directory.Exists(folder)) {
            foreach (var part in new DirectoryInfo(folder).EnumerateFiles("Ahziael backup *.part")) {
                if (DateTime.Now - part.LastWriteTime > s_staleUnfinished) {
                    TryDelete(part.FullName);
                }
            }
        }
    }

    /// <summary>
    /// Makes the backups made before 2026-11-19 small: each held a copy of the server's code and build files (Source),
    /// which an update downloads again anyway. Every other file in them stays exactly as it is, still readable by Restore.
    /// Returns how many were made smaller and how many bytes that freed.
    /// </summary>
    internal static (int Slimmed, long Freed) SlimOldBackups(string installFolder) {
        var slimmed = 0;
        var freed = 0L;
        foreach (var backup in List(installFolder)) {
            try {
                var before = backup.Bytes;
                if (Slim(backup.Path)) {
                    slimmed++;
                    freed += before - new FileInfo(backup.Path).Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) {
                // Left as it is: a backup in use or damaged is never thrown away here.
            }
        }

        Prune(installFolder);

        return (slimmed, freed);
    }

    /// <summary>Whether a backup entry is a file today's backups leave out (the skipped Ahziael folders and files).</summary>
    internal static bool IsLeftOut(string entry) {
        if (!entry.StartsWith(APP_PART, StringComparison.Ordinal)) {
            return false;
        }

        var relative = entry[APP_PART.Length..];
        var slash = relative.IndexOf('/');
        if (slash > 0 && s_skippedAppFolders.Contains(relative[..slash])) {
            return true;
        }

        return !Keep(relative[(relative.LastIndexOf('/') + 1)..]);
    }

    /// <summary>Copies a backup without its left-out files and puts the copy in its place. False when nothing is left out.</summary>
    private static bool Slim(string path) {
        var temp = path + ".slimming.part";
        try {
            using (var old = ZipFile.OpenRead(path)) {
                if (!old.Entries.Any(e => IsLeftOut(e.FullName))) {
                    return false;
                }

                using var slim = ZipFile.Open(temp, ZipArchiveMode.Create);
                foreach (var entry in old.Entries) {
                    if (IsLeftOut(entry.FullName)) {
                        continue;
                    }

                    var copy = slim.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using var from = entry.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
            }

            // The name carries when it was made, but the file's own time is kept too, for anyone sorting by it.
            var made = File.GetLastWriteTime(path);
            File.Move(temp, path, overwrite: true);
            File.SetLastWriteTime(path, made);

            return true;
        }
        catch {
            TryDelete(temp);
            throw;
        }
    }

    private static bool Keep(string file) => !s_skippedExtensions.Contains(Path.GetExtension(file))
                                             && !Path.GetFileName(file).StartsWith('.');

    /// <summary>A file the tabs save beside the server's program: data, not the program itself.</summary>
    private static bool IsServerSave(string file) {
        var name = Path.GetFileName(file);

        return Path.GetExtension(name).ToLowerInvariant() is ".json" or ".jsonl" or ".txt"
               && !name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
               && !name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static void TryDelete(string file) {
        try {
            File.Delete(file);
        }
        catch (Exception) {
            // Left for next time.
        }
    }

}
