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
using System.Text.Json;

namespace Imlight.Setup;

/// <summary>
/// The spiraldb-extra files each update put in the server folder, by every version of them Imlight ever shipped
/// (Niduus, 2026-10-28: "are modifications made via Ahziael local to ones machine so when the server updates from our
/// end, their modifications are saved?"). A file that is none of those was changed on this PC, so an update keeps it,
/// and a shipped file that is gone was deleted on this PC (such as an area deleted on the Customs tab), so an update
/// does not bring it back. Before the first update with this list, every file counts as shipped.
/// </summary>
internal sealed class ShippedExtraFiles {

    internal const string FILE_NAME = "spiraldb-extra-shipped.json";
    private const string PREFIX = "spiraldb-extra/";

    private readonly Dictionary<string, HashSet<string>> _versions;
    private readonly bool _known;

    private ShippedExtraFiles(Dictionary<string, HashSet<string>> versions, bool known) {
        _versions = versions;
        _known = known;
    }

    internal static bool Covers(string relative) => relative.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

    internal static ShippedExtraFiles Load(string serverFolder) {
        var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(serverFolder, FILE_NAME);
        try {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file)) is { } saved) {
                foreach (var (path, hashes) in saved) {
                    versions[path] = new HashSet<string>(hashes, StringComparer.OrdinalIgnoreCase);
                }

                return new ShippedExtraFiles(versions, known: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            SetupLog.Detail($"Could not read {FILE_NAME} ({ex.Message}); this update replaces the shipped files as before.");
        }

        return new ShippedExtraFiles(versions, known: false);
    }

    /// <summary>
    /// Whether the update leaves the file on this PC as it is, and why. Called before the shipped file is copied.
    /// </summary>
    internal string? KeepReason(string relative, string target) {
        if (!_known || !_versions.TryGetValue(relative, out var shipped)) {
            // Not shipped by an earlier update (or no list yet): the shipped file goes in.
            return null;
        }

        if (!File.Exists(target)) {
            return "it was deleted on this PC";
        }

        return shipped.Contains(Hash(target)) ? null : "it was changed on this PC";
    }

    /// <summary>
    /// Whether a spiraldb-extra file was made or changed on this PC rather than shipped as it is (the Share tab's own
    /// customs). Without the list it cannot tell, and says fallback.
    /// </summary>
    internal bool IsOwn(string relative, string file, bool fallback) {
        if (!_known) {
            return fallback;
        }

        return !_versions.TryGetValue(relative, out var shipped) || !shipped.Contains(Hash(file));
    }

    /// <summary>Notes the version this update ships, so a later update knows it as Imlight's own.</summary>
    internal void Add(string relative, string shippedFile) {
        if (!_versions.TryGetValue(relative, out var hashes)) {
            _versions[relative] = hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        hashes.Add(Hash(shippedFile));
    }

    internal void Save(string serverFolder) {
        var file = Path.Combine(serverFolder, FILE_NAME);
        var sorted = _versions.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(v => v.Key, v => v.Value.Order(StringComparer.OrdinalIgnoreCase).ToList());
        File.WriteAllText(file + ".saving", JsonSerializer.Serialize(sorted));
        File.Move(file + ".saving", file, overwrite: true);
    }

    private static string Hash(string file) {
        using var stream = File.OpenRead(file);

        // 16 bytes are plenty to tell versions of one file apart.
        return Convert.ToHexString(SHA256.HashData(stream), 0, 16);
    }

}
