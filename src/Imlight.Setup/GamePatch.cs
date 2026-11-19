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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Imcodec.Wad;

namespace Imlight.Setup;

/// <summary>
/// Tells when Wizard101 patched in a way the server has to be built again for.
/// </summary>
/// <remarks>
/// The server speaks the game's messages exactly as the game's own message files (the *Messages*.xml files in
/// Root.wad) describe them, and they are built into the server (SetupSteps.PrepareGeneratorInputs). When KingsIsle
/// adds or changes a message, a server built before the patch no longer understands the patched game. The Ahmarra
/// update of 2026-11-12 does this: 13 new messages and 6 changed ones (Test Realm r811339 against live r806919).
/// The build writes what it read into <see cref="STAMP_FILE"/> next to the server; the app compares it with the game
/// and builds the server again when they differ. The game files are only read, never changed.
/// </remarks>
internal static class GamePatch {

    /// <summary>Written next to the server by the build: the fingerprint of the message files it was built with.</summary>
    internal const string STAMP_FILE = "game-messages.txt";

    /// <summary>
    /// Written next to the server by the build: the type dump (list of the game program's data types) it was built
    /// with, such as r813697_Wizard_1_620.json.
    /// </summary>
    internal const string TYPE_DUMP_STAMP_FILE = "game-typedump.txt";

    /// <summary>Whether a Root.wad file name is one of the game's message files the server is built from.</summary>
    internal static bool IsMessageFile(string name) {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));

        return fileName.Contains("Messages", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fingerprint of the message files in <paramref name="rootWadPath"/>: how many there are and a hash of their
    /// names and contents. Null when Root.wad can't be read.
    /// </summary>
    internal static string? Fingerprint(string rootWadPath) {
        try {
            using var stream = new FileStream(rootWadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var archive = ArchiveParser.Parse(stream);
            if (archive is null) {
                return null;
            }

            var files = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in archive.Files.Keys) {
                if (!IsMessageFile(name) || archive.OpenFile(name) is not { } data) {
                    continue;
                }

                // Named as the build names them: the file name alone.
                files[Path.GetFileName(name.Replace('\\', '/'))] = data.ToArray();
            }

            return Fingerprint(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) {
            return null;
        }
    }

    /// <summary>The fingerprint of message files already read, by file name.</summary>
    internal static string Fingerprint(IReadOnlyDictionary<string, byte[]> files) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) {
            hash.AppendData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
            hash.AppendData([0]);
            hash.AppendData(data);
        }

        return $"{files.Count} files {Convert.ToHexString(hash.GetHashAndReset())}";
    }

    /// <summary>The fingerprint the server in <paramref name="serverFolder"/> was built with, or null for a build from before this check.</summary>
    internal static string? BuiltWith(string serverFolder) {
        try {
            var file = Path.Combine(serverFolder, STAMP_FILE);

            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // The last Root.wad looked at, so the check every few minutes only reads it again after it changed.
    private static (string Path, long Length, DateTime Written, string? Fingerprint)? s_last;

    /// <summary>
    /// Whether the game at <paramref name="rootWadPath"/> has message files other than the server in
    /// <paramref name="serverFolder"/> was built with. False when either can't be told (an older build has no stamp).
    /// </summary>
    internal static bool ServerIsBehind(string serverFolder, string rootWadPath) {
        if (BuiltWith(serverFolder) is not { } built) {
            return false;
        }

        FileInfo info;
        try {
            info = new FileInfo(rootWadPath);
            if (!info.Exists) {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return false;
        }

        string? now;
        if (s_last is { } last && last.Path == rootWadPath && last.Length == info.Length && last.Written == info.LastWriteTimeUtc) {
            now = last.Fingerprint;
        }
        else {
            now = Fingerprint(rootWadPath);
            s_last = (rootWadPath, info.Length, info.LastWriteTimeUtc, now);
        }

        return now is not null && !string.Equals(now, built, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The revision number in a name such as r814353.Wizard_1_620_0_Live or r813697_Wizard_1_620.json, or -1.</summary>
    internal static long RevisionNumber(string? name) {
        var match = name is null ? null : Regex.Match(name.Trim(), @"^r(\d+)", RegexOptions.IgnoreCase);

        return match is { Success: true } && long.TryParse(match.Groups[1].Value, out var number) ? number : -1;
    }

    /// <summary>
    /// The game program's revision (Bin\revision.dat, such as r814353.Wizard_1_620_0_Live) of the game whose Root.wad
    /// is <paramref name="rootWadPath"/>, or null when it can't be read. Only reads.
    /// </summary>
    internal static string? GameRevision(string rootWadPath) {
        try {
            var gameData = Path.GetDirectoryName(Path.GetFullPath(rootWadPath));
            var game = gameData is null ? null : Directory.GetParent(gameData)?.Parent?.FullName;
            var file = game is null ? null : Path.Combine(game, "Bin", "revision.dat");
            if (file is null || !File.Exists(file)) {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim('\0', ' ', '\r', '\n', '\t');

            return RevisionNumber(text) > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>The type dump the server in <paramref name="serverFolder"/> was built with, or null for a build from before this stamp.</summary>
    internal static string? TypeDumpBuiltWith(string serverFolder) {
        try {
            var file = Path.Combine(serverFolder, TYPE_DUMP_STAMP_FILE);

            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>
    /// When the game program is a newer revision than the type dump the server was built with: the two names, such as
    /// (r814353.Wizard_1_620_0_Live, r813697_Wizard_1_620.json). Null when it isn't, or either can't be told. A newer
    /// program does not always change its types (most patches don't), so this is a heads-up, not an error: the Ahmarra
    /// update (r813697, 2026-11-12) changed players, creatures and battles, and only a new dump fixed that.
    /// </summary>
    internal static (string Game, string TypeDump)? TypeDumpIsBehind(string serverFolder, string rootWadPath) {
        if (TypeDumpBuiltWith(serverFolder) is not { } dump || GameRevision(rootWadPath) is not { } game) {
            return null;
        }

        var dumpNumber = RevisionNumber(dump);

        return dumpNumber > 0 && RevisionNumber(game) > dumpNumber ? (game, dump) : null;
    }

}
