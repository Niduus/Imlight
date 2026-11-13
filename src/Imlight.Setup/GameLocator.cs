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
using System.Text.RegularExpressions;

namespace Imlight.Setup;

/// <summary>
/// Finds the player's Wizard101 (its Root.wad) without asking: the saved place first, then the running game, the
/// installer's and Steam's records, and the usual folders on every drive. The background update has no window to ask
/// in, so it stopped with "Wizard101 was not found" whenever the game was anywhere but the five usual folders
/// (a player's report, 2026-11-04). Only reads; nothing in the game folder is changed.
/// </summary>
internal static partial class GameLocator {

    private const string STEAM_APP_ID = "22320";

    /// <summary>The Root.wad in a Wizard101 folder, its Data\GameData folder, or the file itself; null when it isn't there.</summary>
    internal static string? ResolveRootWad(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        try {
            path = path.Trim('"', ' ');
            if (File.Exists(path) && Path.GetFileName(path).Equals("Root.wad", StringComparison.OrdinalIgnoreCase)) {
                return Path.GetFullPath(path);
            }

            foreach (var candidate in new[] {
                Path.Combine(path, "Data", "GameData", "Root.wad"),
                Path.Combine(path, "GameData", "Root.wad"),
                Path.Combine(path, "Root.wad"),
            }) {
                if (File.Exists(candidate)) {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) {
            // Not a usable path.
        }

        return null;
    }

    /// <summary>
    /// True when this Root.wad is in a downloaded copy of the game files that the game can't run from, such as one of
    /// Aurorium's revision folders (data\V_r…, with Bin and Data inside, so it looks like an install). Its Bin has the
    /// game program but no PatchConfig.xml, and the game started from there stops at once with the
    /// "LoadMessageModule ... Base Messages" error (a player's report, 2026-11-13).
    /// </summary>
    internal static bool IsDownloadCopy(string? rootWad) {
        if (string.IsNullOrWhiteSpace(rootWad)) {
            return false;
        }

        try {
            var gameData = Path.GetDirectoryName(Path.GetFullPath(rootWad));
            var game = gameData is null ? null : Directory.GetParent(gameData)?.Parent?.FullName;
            if (game is null) {
                return false;
            }

            // Aurorium keeps the patch server's file list beside Bin and Data; an installed game has none there.
            if (File.Exists(Path.Combine(game, "LatestFileList.bin")) || File.Exists(Path.Combine(game, "LatestFileList.xml"))) {
                return true;
            }

            var bin = Path.Combine(game, "Bin");

            return File.Exists(Path.Combine(bin, "WizardGraphicalClient.exe")) && !File.Exists(Path.Combine(bin, "PatchConfig.xml"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) {
            return false;
        }
    }

    /// <summary>What to tell the player when the folder they gave is a download copy, not their Wizard101.</summary>
    internal const string DOWNLOAD_COPY_MESSAGE = "That folder is a download of the game files (Aurorium's), not your Wizard101. " +
        "Wizard101 can't start from it. Pick the folder Wizard101 is installed in instead, often C:\\ProgramData\\KingsIsle Entertainment\\Wizard101.";

    /// <summary>The saved Root.wad when it is still there, otherwise the first one found; saves a new find.</summary>
    internal static string? FindAndRemember(string installFolder) {
        var info = InstallInfo.Load(installFolder);
        if (info.RootWad is { } saved && File.Exists(saved) && !IsDownloadCopy(saved)) {
            return saved;
        }

        if (Search(info.RootWad) is not { } found) {
            return null;
        }

        Remember(installFolder, found);

        return found;
    }

    /// <summary>Saves where Wizard101 is, so Start, Play and Update use it.</summary>
    internal static void Remember(string installFolder, string rootWad) {
        var info = InstallInfo.Load(installFolder);
        info.RootWad = Path.GetFullPath(rootWad);
        info.Save(installFolder);
    }

    /// <summary>Looks everywhere Wizard101 is usually installed. <paramref name="lastKnown"/> is the saved place that is gone.</summary>
    internal static string? Search(string? lastKnown = null) {
        foreach (var folder in Places(lastKnown)) {
            if (ResolveRootWad(folder) is { } found && !IsDownloadCopy(found)) {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A Root.wad Imlight already has a copy of (the Game Files tab's copy, or Aurorium's newest one). Good enough to
    /// build the server from when the game itself can't be found, but never saved as the game's place: Play needs the real one.
    /// </summary>
    internal static string? BuildOnlyCopy(string installFolder) {
        var places = new List<string> { Path.Combine(installFolder, "Game files", "Copy of GameData", "Root.wad") };
        try {
            if (Aurorium.Load(installFolder).Folder is { } aurorium) {
                places.AddRange(Aurorium.GameDataFolders(aurorium).Select(gameData => Path.Combine(gameData, "Root.wad")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            // No Aurorium files to fall back on.
        }

        return places.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Places(string? lastKnown) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in AllPlaces(lastKnown)) {
            if (!string.IsNullOrWhiteSpace(place) && seen.Add(place)) {
                yield return place;
            }
        }
    }

    private static IEnumerable<string> AllPlaces(string? lastKnown) {
        // The same Wizard101 folder on another drive letter (a moved or re-lettered drive).
        if (lastKnown is not null && Path.GetPathRoot(lastKnown) is { Length: > 0 } root) {
            var rest = lastKnown[root.Length..];
            foreach (var drive in Drives()) {
                yield return Path.Combine(drive, rest);
            }
        }

        // The game running right now.
        foreach (var running in RunningGameFolders()) {
            yield return running;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programData, "KingsIsle Entertainment", "Wizard101");
        yield return Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Wizard101");
        yield return Path.Combine(programFiles, "Steam", "steamapps", "common", "Wizard101");
        yield return Path.Combine(programFilesX86, "KingsIsle Entertainment", "Wizard101");
        yield return Path.Combine(programFiles, "KingsIsle Entertainment", "Wizard101");
        yield return Path.Combine(localAppData, "KingsIsle Entertainment", "Wizard101");

        // What the installers recorded.
        foreach (var recorded in RegistryFolders()) {
            yield return recorded;
        }

        // Every Steam library.
        foreach (var library in SteamLibraries()) {
            yield return Path.Combine(library, "steamapps", "common", "Wizard101");
        }

        // The usual folders on every drive.
        foreach (var drive in Drives()) {
            yield return Path.Combine(drive, "ProgramData", "KingsIsle Entertainment", "Wizard101");
            yield return Path.Combine(drive, "Program Files (x86)", "KingsIsle Entertainment", "Wizard101");
            yield return Path.Combine(drive, "Program Files", "KingsIsle Entertainment", "Wizard101");
            yield return Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "Program Files", "Steam", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "Steam", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "Games", "SteamLibrary", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "Games", "Steam", "steamapps", "common", "Wizard101");
            yield return Path.Combine(drive, "KingsIsle Entertainment", "Wizard101");
            yield return Path.Combine(drive, "Games", "KingsIsle Entertainment", "Wizard101");
            yield return Path.Combine(drive, "Games", "Wizard101");
            yield return Path.Combine(drive, "Wizard101");
        }
    }

    private static List<string> Drives() {
        try {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private static List<string> RunningGameFolders() {
        var folders = new List<string>();
        foreach (var process in Process.GetProcessesByName("WizardGraphicalClient")) {
            try {
                if (process.MainModule?.FileName is { } exe && Path.GetDirectoryName(Path.GetDirectoryName(exe)) is { } game) {
                    folders.Add(game);
                }
            }
            catch (Exception) {
                // Not allowed to look at it; carry on.
            }
            finally {
                process.Dispose();
            }
        }

        return folders;
    }

    private static List<string> RegistryFolders() {
        var folders = new List<string>();
        if (!OperatingSystem.IsWindows()) {
            return folders;
        }

        try {
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser }) {
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 }) {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames()) {
                        using var app = uninstall.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string ?? "";
                        if (name.Equals($"Steam App {STEAM_APP_ID}", StringComparison.OrdinalIgnoreCase)
                            || display.Contains("Wizard101", StringComparison.OrdinalIgnoreCase)) {
                            if (app?.GetValue("InstallLocation") is string location) {
                                folders.Add(location);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
            // The records can't be read; the folders are still searched.
        }

        return folders;
    }

    private static List<string> SteamLibraries() {
        var steamFolders = new List<string> {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };
        if (OperatingSystem.IsWindows()) {
            try {
                using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (steam?.GetValue("SteamPath") is string path) {
                    steamFolders.Insert(0, path.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
                // Use the usual Steam folders.
            }
        }

        var libraries = new List<string>(steamFolders);
        foreach (var steam in steamFolders) {
            var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try {
                if (File.Exists(list)) {
                    libraries.AddRange(LibraryPath().Matches(File.ReadAllText(list))
                        .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Can't read this Steam's library list.
            }
        }

        return libraries;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

}
