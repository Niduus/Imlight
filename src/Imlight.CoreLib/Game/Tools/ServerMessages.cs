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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Every [Server Message] line and message box passes through here just before it is sent (SocketSender), so the
/// Manager's switches and words apply to all of them without changing where they are sent. Each message is matched
/// against ServerMessageCatalog: its {fields} are read back out of the sent words and put into the Manager's words. A
/// message on no list belongs to "Other messages"; it is noted in server-messages-seen.json so the Manager can show it.
/// Announcements (Announcements.Text) and cheat words are the Manager's words already and go out as they are.
/// </summary>
internal static class ServerMessages {

    private sealed record Pattern(AnnouncementLine Line, Regex Match, string Longest, int Literal);

    private sealed class Seen {
        public string Text { get; set; } = "";
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    private const int MAX_SEEN = 300;
    private const int MAX_SEEN_LENGTH = 600;

    private static readonly Lazy<Pattern[]> s_patterns = new(Compile);
    // Words that are the Manager's already, with when they were made; they are let through as they are.
    private static readonly ConcurrentDictionary<string, long> s_own = new(StringComparer.Ordinal);
    // What each sent text matched (null: no list), as the same words are often sent again.
    private static readonly ConcurrentDictionary<string, Pattern> s_matched = new(StringComparer.Ordinal);
    private static readonly object s_seenLock = new();
    private static Dictionary<string, Seen> s_seen;
    private static bool s_seenChanged;
    private static Timer s_seenTimer;

    /// <summary>Marks words as the Manager's own (an announcement or a cheat's line), so they go out as they are.</summary>
    internal static string Own(string text) {
        if (!string.IsNullOrEmpty(text)) {
            if (s_own.Count > 2000) {
                var old = Environment.TickCount64 - 600_000;
                foreach (var (key, made) in s_own) {
                    if (made < old) {
                        s_own.TryRemove(key, out _);
                    }
                }
            }

            s_own[text] = Environment.TickCount64;
        }

        return text;
    }

    /// <summary>The message as the Manager wants it sent, or null when it is turned off.</summary>
    internal static IMessage Apply(EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE message) {
        string text = message.Message.ToString();
        try {
            var shown = Apply(text);
            if (shown is null) {
                return null;
            }

            // A new message: the same one can be on its way to other players too.
            return ReferenceEquals(shown, text) ? message : new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = shown, Modal = message.Modal };
        }
        catch (Exception ex) {
            Logger.Error("Server messages: could not apply the Manager's settings to \"{0}\": {1}", Logger.Args(text, ex.Message));

            return message;
        }
    }

    /// <summary>The words as the Manager wants them sent, the same string when unchanged, or null when turned off.</summary>
    internal static string Apply(string text) {
        if (string.IsNullOrWhiteSpace(text) || s_own.ContainsKey(text)) {
            return text;
        }

        var pattern = s_matched.GetOrAdd(text, Find);
        if (s_matched.Count > 5000) {
            s_matched.Clear();
        }

        if (pattern is null) {
            var key = ServerMessageCatalog.SeenKey(text);
            NoteSeen(text);
            if (Announcements.IsOff(key, ServerMessageCatalog.OTHER_GROUP)) {
                return null;
            }

            var own = Announcements.Changed(key);

            return string.IsNullOrWhiteSpace(own) ? text : own;
        }

        var line = pattern.Line;
        if (Announcements.IsOff(line.Key, line.Group)) {
            return null;
        }

        var words = Announcements.Changed(line.Key);
        if (string.IsNullOrWhiteSpace(words)) {
            return text;
        }

        var found = pattern.Match.Match(text);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < line.Fields.Length; i++) {
            values[line.Fields[i].Field.Trim('{', '}')] = found.Groups[i + 1].Value;
        }

        return AnnouncementCatalog.Fill(words, values);
    }

    private static Pattern Find(string text) {
        foreach (var pattern in s_patterns.Value) {
            if (text.Contains(pattern.Longest, StringComparison.Ordinal) && pattern.Match.IsMatch(text)) {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>Each listed message as a pattern: its words, with (.*) where each {field} is. Most words first.</summary>
    private static Pattern[] Compile() {
        var patterns = new List<Pattern>();
        foreach (var line in ServerMessageCatalog.All) {
            var fields = line.Fields.Select(f => f.Field).ToList();
            var pieces = new List<string>();
            var regex = "^";
            var rest = line.Default;
            while (rest.Length > 0) {
                var next = fields.Select(f => (Field: f, At: rest.IndexOf(f, StringComparison.Ordinal)))
                    .Where(f => f.At >= 0).OrderBy(f => f.At).FirstOrDefault();
                if (next.Field is null) {
                    pieces.Add(rest);
                    regex += Regex.Escape(rest);
                    break;
                }

                pieces.Add(rest[..next.At]);
                regex += Regex.Escape(rest[..next.At]) + "(.*?)";
                rest = rest[(next.At + next.Field.Length)..];
            }

            regex += "$";
            var longest = pieces.OrderByDescending(p => p.Length).FirstOrDefault() ?? "";
            patterns.Add(new Pattern(line, new Regex(regex, RegexOptions.Singleline | RegexOptions.CultureInvariant), longest,
                pieces.Sum(p => p.Length)));
        }

        return [.. patterns.OrderByDescending(p => p.Literal)];
    }

    // ----- Messages on no list -----

    private static void NoteSeen(string text) {
        if (text.Length > MAX_SEEN_LENGTH) {
            return;
        }

        lock (s_seenLock) {
            s_seen ??= LoadSeen();
            if (!s_seen.TryGetValue(text, out var seen)) {
                if (s_seen.Count >= MAX_SEEN) {
                    s_seen.Remove(s_seen.Values.OrderBy(s => s.Last).First().Text);
                }

                seen = new Seen { Text = text };
                s_seen[text] = seen;
            }

            seen.Count++;
            seen.Last = DateTime.UtcNow;
            s_seenChanged = true;
            s_seenTimer ??= new Timer(_ => SaveSeen(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private static Dictionary<string, Seen> LoadSeen() {
        try {
            if (File.Exists(ServerMessageCatalog.SEEN_FILE)) {
                var list = JsonSerializer.Deserialize<List<Seen>>(File.ReadAllText(ServerMessageCatalog.SEEN_FILE)) ?? [];

                return list.Where(s => !string.IsNullOrEmpty(s.Text)).GroupBy(s => s.Text).ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("Server messages: could not read {0}: {1}", Logger.Args(ServerMessageCatalog.SEEN_FILE, ex.Message));
        }

        return [];
    }

    private static void SaveSeen() {
        string json;
        lock (s_seenLock) {
            if (!s_seenChanged || s_seen is null) {
                return;
            }

            s_seenChanged = false;
            json = JsonSerializer.Serialize(s_seen.Values.OrderByDescending(s => s.Last).ToList(), new JsonSerializerOptions { WriteIndented = true });
        }

        try {
            var temp = $"{ServerMessageCatalog.SEEN_FILE}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ServerMessageCatalog.SEEN_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Server messages: could not write {0}: {1}", Logger.Args(ServerMessageCatalog.SEEN_FILE, ex.Message));
        }
    }

}
