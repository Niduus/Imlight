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
using System.Linq;

namespace Imlight.Setup;

/// <summary>
/// Reads and edits values in Imlight.ini, keeping every comment and every other line as it was.
/// </summary>
internal static class IniFile {

    private static readonly System.Threading.Lock s_fillLock = new();
    private static readonly Dictionary<string, (DateTime Written, long Size)> s_filled = new(StringComparer.OrdinalIgnoreCase);
    private static string[]? s_shipped;

    /// <summary>
    /// Adds every section and key the Imlight.ini built into Ahziael has that the file at <paramref name="path"/> lacks,
    /// with its comments and shipped default. Values already there are never changed. It runs again only when the file
    /// changed since the last look, and never fails a read: a file that is busy is tried again next time.
    /// </summary>
    internal static void FillFromShipped(string path) {
        try {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists) {
                return;
            }

            lock (s_fillLock) {
                var stamp = (info.LastWriteTimeUtc, info.Length);
                if (s_filled.TryGetValue(path, out var seen) && seen == stamp) {
                    return;
                }

                s_shipped ??= Shipped();
                var lines = System.IO.File.ReadAllLines(path).ToList();
                if (s_shipped.Length > 0 && AddMissing(lines, s_shipped) > 0) {
                    System.IO.File.WriteAllLines(path, lines);
                    info.Refresh();
                    stamp = (info.LastWriteTimeUtc, info.Length);
                }

                s_filled[path] = stamp;
            }
        } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) {
            // Busy or read-only: the next read tries again.
        }
    }

    private static string[] Shipped() {
        using var stream = typeof(IniFile).Assembly.GetManifestResourceStream("Imlight.Defaults.ini");
        if (stream is null) {
            return [];
        }

        using var reader = new System.IO.StreamReader(stream);

        return reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
    }

    /// <summary>
    /// Adds the sections and keys a newer default file has that the person's file lacks, with their comments,
    /// so new settings show up after an update. Values already there are never changed.
    /// </summary>
    /// <returns>How many keys were added.</returns>
    internal static int AddMissing(List<string> lines, IReadOnlyList<string> defaults) {
        var added = 0;
        string? section = null;
        var comments = new List<string>();

        foreach (var line in defaults) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                section = trimmed[1..^1].Trim();
                comments.Clear();

                continue;
            }

            if (trimmed.StartsWith(';')) {
                comments.Add(line);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (section is null || equals <= 0) {
                comments.Clear();

                continue;
            }

            var key = trimmed[..equals].Trim();
            if (Find(lines, section, key) < 0) {
                Set(lines, section, key, trimmed[(equals + 1)..].Trim());
                var at = Find(lines, section, key);
                lines.InsertRange(at, comments);
                added++;
            }

            comments.Clear();
        }

        return added;
    }

    /// <summary>
    /// Settings whose shipped default changed: a kept file that still has the old default gets the new one, and a
    /// value the person picked stays.
    /// </summary>
    private static readonly (string Section, string Key, string Old, string New)[] s_changedDefaults = [
        // Niduus, 2026-10-25: "Default should be 80 btw for cap without elixir".
        ("Character", "MaxInventoryItems", "75", "80"),
        // Niduus, 2026-10-27 ("globals should display like this"): sent in the turn order since v0.10.451, a global's told
        // size shows (the bubble was missing as an interrupt, not for its size), so the own number is back on.
        ("BossCheats", "ShowOwnValueGlobals", "false", "true"),
        // Niduus, 2026-10-27: "Set all # to max amount so that way, buy more can actually buy more".
        ("Bazaar", "StartStock", "100", "999"),
        // Niduus, 2026-10-27 ("bazaar no stocked"): the whole Bazaar in 6 parts showed nothing; each tab gets its own items.
        ("Bazaar", "SendEverything", "true", "false"),
        // Niduus, 2026-10-27 ("Willcasts crash the game"): the pet card sent as a Spell crashed the game.
        ("Pets", "WillCastCardData", "Spell", "Power"),
        // Niduus, 2026-11-04: "backpack elixir is giving +10 when it should be +50".
        ("Character", "ExpandBackpackElixirAmount", "10", "50"),
    ];

    /// <summary>
    /// Moves settings still at an old shipped default to the new default, see s_changedDefaults.
    /// </summary>
    /// <returns>How many values were changed.</returns>
    internal static int UpdateChangedDefaults(List<string> lines) {
        var changed = 0;
        foreach (var (section, key, old, value) in s_changedDefaults) {
            var at = Find(lines, section, key);
            if (at < 0) {
                continue;
            }

            var line = lines[at];
            var current = line[(line.IndexOf('=') + 1)..].Trim();
            if (current == old) {
                Set(lines, section, key, value);
                changed++;
            }
        }

        return changed;
    }

    private static int Find(List<string> lines, string section, string key) {
        var inSection = false;
        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (inSection && !trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns a value from an ini file, or null when the file, section or key is missing.
    /// </summary>
    internal static string? Get(string path, string section, string key) {
        if (!System.IO.File.Exists(path)) {
            return null;
        }

        var inSection = false;
        foreach (var line in System.IO.File.ReadLines(path)) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (inSection && !trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                return trimmed[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Every value in an ini file by section and key, read in one go; each holds what <see cref="Get"/> would return
    /// for it. Empty when the file is missing.
    /// </summary>
    internal static Dictionary<(string Section, string Key), string> ReadAll(string path) {
        var values = new Dictionary<(string Section, string Key), string>(SectionKeyComparer.INSTANCE);
        if (!System.IO.File.Exists(path)) {
            return values;
        }

        string? section = null;
        foreach (var line in System.IO.File.ReadLines(path)) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                section = trimmed[1..^1].Trim();

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (section is not null && !trimmed.StartsWith(';') && equals > 0) {
                // The first one wins, as Get finds it.
                values.TryAdd((section, trimmed[..equals].Trim()), trimmed[(equals + 1)..].Trim());
            }
        }

        return values;
    }

    private sealed class SectionKeyComparer : IEqualityComparer<(string Section, string Key)> {

        internal static readonly SectionKeyComparer INSTANCE = new();

        public bool Equals((string Section, string Key) x, (string Section, string Key) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Section, y.Section) && StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key);

        public int GetHashCode((string Section, string Key) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));

    }

    internal static void Set(List<string> lines, string section, string key, string value) {
        var inSection = false;
        var sectionEnd = -1;

        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                if (inSection) {
                    break;
                }

                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inSection) {
                    sectionEnd = i + 1;
                }

                continue;
            }

            if (!inSection) {
                continue;
            }

            if (trimmed.Length > 0) {
                sectionEnd = i + 1;
            }

            var equals = trimmed.IndexOf('=');
            if (!trimmed.StartsWith(';') && equals > 0
                && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                lines[i] = $"{key} = {value}";

                return;
            }
        }

        if (sectionEnd < 0) {
            lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");

            return;
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
    }

}
