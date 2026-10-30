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
using System.Linq;

namespace Imlight.Setup;

/// <summary>
/// The "Clean up" button on the Ahziael tab > Privacy (Niduus, 2026-10-30: leave no traces of playing on a private server). It
/// finds what Ahziael and its server left behind (logs, command history, copies on the desktop, downloads in the
/// Windows temp folder) and removes it. Wizard101's own files are never touched; accounts, characters, customs and
/// backups are kept.
/// </summary>
internal static class PrivacyCleanup {

    /// <summary>One kind of leftover: what it is in plain words, and its files.</summary>
    internal sealed record Leftovers(string What, IReadOnlyList<string> Files);

    // The server keeps where each wizard stands in the log folder; the Position card reads it while the server runs.
    private const string POSITIONS_FILE = "positions.txt";

    /// <summary>Everything there is to clean up now, grouped; empty groups are left out.</summary>
    internal static List<Leftovers> Find(ServerManager manager) {
        var groups = new List<Leftovers> {
            new("server logs (who logged in, from which address, what happened)", ServerLogs(manager)),
            new("Ahziael's setup log and list of previous commands", Existing(
                Path.Combine(manager.InstallFolder, "setup-log.txt"),
                Path.Combine(manager.InstallFolder, "command-history.txt"))),
            new("log copies Ahziael saved on your desktop", DesktopCopies()),
            new("Ahziael downloads left in the Windows temp folder", TempLeftovers()),
        };

        return groups.Where(g => g.Files.Count > 0).ToList();
    }

    /// <summary>
    /// Deletes the files. A file still in use (today's server log while the server runs) is skipped. Returns how many
    /// went and how many were skipped.
    /// </summary>
    internal static (int Removed, int Skipped) Remove(IEnumerable<Leftovers> groups) {
        var removed = 0;
        var skipped = 0;
        foreach (var file in groups.SelectMany(g => g.Files)) {
            try {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                skipped++;
            }
        }

        return (removed, skipped);
    }

    /// <summary>Ahziael's own downloads in the Windows temp folder: they are only needed while an update or setup runs.</summary>
    internal static List<string> TempLeftovers() {
        var temp = Path.GetTempPath();

        return Matching(temp, "ImlightSetup-*.exe", "imlight-setup-*.zip", "dotnet-install.ps1", "dotnet-install.sh");
    }

    private static List<string> ServerLogs(ServerManager manager) {
        var logs = manager.LogFolder;
        var files = Directory.Exists(logs)
            ? Directory.EnumerateFiles(logs)
                .Where(f => Path.GetExtension(f) is ".txt" or ".log"
                            && !Path.GetFileName(f).Equals(POSITIONS_FILE, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

        // The Bazaar bot's log; its stock and status beside it are kept.
        files.AddRange(Matching(Path.Combine(logs, "bazaar-bot"), "bazaar-bot*.txt"));

        return files;
    }

    // The copies "Save log to desktop" and "Save game log" make (ServerManager.SaveLogToDesktop, ManagerWindow).
    private static List<string> DesktopCopies() {
        var folders = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        return folders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => Matching(f, "Imlight log *.txt", "Wizard101 game log *.txt")).ToList();
    }

    private static List<string> Matching(string folder, params string[] patterns) {
        try {
            return Directory.Exists(folder)
                ? patterns.SelectMany(p => Directory.EnumerateFiles(folder, p, SearchOption.TopDirectoryOnly)).Distinct().ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private static List<string> Existing(params string[] files) => files.Where(File.Exists).ToList();

}
