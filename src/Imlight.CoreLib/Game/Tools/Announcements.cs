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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The words of every announcement (AnnouncementCatalog), with the Manager's changes from announcements.json:
/// { "Texts": { "event.on": "..." }, "Off": [ "raid.sigil" ], "OffGroups": [ "Guild raids" ] }. Server messages
/// (ServerMessageCatalog, applied in ServerMessages.cs) keep their settings in the same file. The file is looked at
/// again at most once a second, so a change applies without a restart. A line with no words of its own uses the
/// catalog's; a line that is off, or whose group is off, is not sent.
/// </summary>
internal static class Announcements {

    private sealed class AnnouncementFile {
        public Dictionary<string, string> Texts { get; set; } = [];
        public List<string> Off { get; set; } = [];
        public List<string> OffGroups { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, string> s_texts = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> s_off = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> s_offGroups = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_fileTime;
    private static DateTime s_lastLook;

    /// <summary>
    /// The line for <paramref name="key"/> with its {fields} filled in (field names without the braces), or null when
    /// the line is turned off in the Manager.
    /// </summary>
    internal static string Text(string key, params (string Field, object Value)[] values) {
        var line = AnnouncementCatalog.Find(key);
        if (IsOff(key, line?.Group)) {
            return null;
        }

        var text = Changed(key);
        if (string.IsNullOrWhiteSpace(text)) {
            text = line?.Default ?? key;
        }

        var fields = values.ToDictionary(v => v.Field, v => Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            StringComparer.OrdinalIgnoreCase);

        // Already the Manager's words: ServerMessages lets it go out as it is.
        return ServerMessages.Own(AnnouncementCatalog.Fill(text, fields));
    }

    /// <summary>True when the line, or its whole group, is turned off in the Manager.</summary>
    internal static bool IsOff(string key, string group) {
        Refresh();
        lock (s_lock) {
            return s_off.Contains(key) || (group is not null && s_offGroups.Contains(group));
        }
    }

    /// <summary>The Manager's own words for a line, or null when it keeps the first ones.</summary>
    internal static string Changed(string key) {
        Refresh();
        lock (s_lock) {
            return s_texts.GetValueOrDefault(key);
        }
    }

    private static void Refresh() {
        var now = DateTime.UtcNow;
        lock (s_lock) {
            if (now - s_lastLook < TimeSpan.FromSeconds(1)) {
                return;
            }

            s_lastLook = now;
        }

        try {
            var time = File.Exists(AnnouncementCatalog.FILE) ? File.GetLastWriteTimeUtc(AnnouncementCatalog.FILE) : default;
            if (time == s_fileTime) {
                return;
            }

            var file = time == default ? new AnnouncementFile()
                : JsonSerializer.Deserialize<AnnouncementFile>(File.ReadAllText(AnnouncementCatalog.FILE),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AnnouncementFile();
            lock (s_lock) {
                s_texts = new Dictionary<string, string>(file.Texts ?? [], StringComparer.OrdinalIgnoreCase);
                s_off = new HashSet<string>(file.Off ?? [], StringComparer.OrdinalIgnoreCase);
                s_offGroups = new HashSet<string>(file.OffGroups ?? [], StringComparer.OrdinalIgnoreCase);
                s_fileTime = time;
            }

            if (time != default) {
                Logger.Information("Announcements and server messages: {0} changed lines, {1} turned off and {2} groups turned off are in use now.",
                    Logger.Args(s_texts.Count, s_off.Count, s_offGroups.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // The app is still writing the file; it is read on the next look.
        }
    }

}
