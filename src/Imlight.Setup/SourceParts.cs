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
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Imlight.Setup;

/// <summary>
/// One fingerprint of every file in a server source folder that goes into the server's program, except the server's own
/// code (src\Imlight.CoreLib, which a live patch puts in) and what doesn't build into the server (Ahziael, the bots, game
/// data, notes). Two sources with the same fingerprint build the same program around the server's own code, so an update
/// between them can go into the running server before it is built (LiveUpdate.EarlyWhyNot).
/// </summary>
internal static class SourceParts {

    // Folders (from the source's top) that are not part of the server's program, or that go in by themselves.
    private static readonly string[] s_outside = [
        ".git/", ".github/", ".vs/", ".idea/", "bots/", "docs/", "Hive mind/", "data/", "logs/", "tools/", "installer/", "overlay-addons/",
        "src/Imlight.Setup/", "src/Imlight.Launcher/", "src/Imlight.Director/Config/Imlight.ini",
    ];

    internal static string Hash(string sourceFolder) {
        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(sourceFolder, f).Replace('\\', '/')))
            .Where(f => Counts(f.Rel))
            .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (full, rel) in files) {
            all.AppendData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\n"));
            // Text the same whatever line endings the download gave it.
            var bytes = File.ReadAllBytes(full);
            if (IsText(rel)) {
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).TrimStart('﻿').ReplaceLineEndings("\n"));
            }

            all.AppendData(SHA256.HashData(bytes));
        }

        return Convert.ToHexString(all.GetHashAndReset());
    }

    private static bool Counts(string rel) {
        var parts = rel.Split('/');
        // Build output, and hidden files (git's, editors').
        if (parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase) || p.StartsWith('.'))) {
            return false;
        }

        if (s_outside.Any(o => rel.StartsWith(o, StringComparison.OrdinalIgnoreCase))) {
            return false;
        }

        // Top-level notes.
        if (parts.Length == 1 && (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || rel.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))) {
            return false;
        }

        // The server's own code goes in live; its project file (packages, settings) does not.
        if (rel.StartsWith("src/Imlight.CoreLib/", StringComparison.OrdinalIgnoreCase)) {
            return !rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsText(string rel)
        => rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
           || rel.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
           || rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
           || rel.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);

}
