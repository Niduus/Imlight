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
using System.Linq;
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.setting`: Ahziael's Settings and Game Rules switches from the game's chat (Niduus, 2026-11-04: "implement a majority
/// of what ahziael can do as commands"). It reads and changes Imlight.ini the way Ahziael does (only keys already in the
/// file, every comment kept), and the server reads the file again by itself, so a change works at once. Administrators
/// only. Passwords and keys are never shown, and the sections that hold them can only be changed in Ahziael.
/// <list type="bullet">
/// <item>.settings: the sections.</item>
/// <item>.setting Battle: the section's settings and values (.setting Battle 2 for the next page).</item>
/// <item>.setting Battle NoAggroSeconds: one setting with its note from the file.</item>
/// <item>.setting Battle NoAggroSeconds 8: changes it.</item>
/// <item>.setting find aggro: every setting whose name has the word.</item>
/// </list>
/// </summary>
internal class CommandSettingProtocol : CommandProtocol {

    private const int PAGE_SIZE = 10;
    private const int NOTE_LINES = 6;

    // Sections that hold passwords or connection details: shown without values and changed only in Ahziael.
    private static readonly HashSet<string> s_lockedSections = new(StringComparer.OrdinalIgnoreCase) {
        "Database", "Remote Admin", "Agent Bridge",
    };

    // Only read when the server starts, as Ahziael's ServerManager.NeedsRestart lists them.
    private static readonly HashSet<string> s_startupSections = new(StringComparer.OrdinalIgnoreCase) {
        "Logging", "Locale", "Patch Server", "ID Lookup", "Database", "Advanced", "Agent Bridge",
    };

    private static readonly HashSet<string> s_startupKeys = new(StringComparer.OrdinalIgnoreCase) {
        "Global Settings.GameRevision", "Login Server.LoginServerName", "Login Server.LoginServerPort",
        "Game Server.GameServerName", "Game Server.GameServerPort", "Game Server.GameServerIP", "Game Server.GameServerCount",
        "Game Server.MaxGameServersAllowed", "Game Server.RealmNames", "Game Server.SessionKeyValidityTime",
    };

    private static readonly string[] s_secretWords = ["password", "secret", "token", "apikey", "api key"];

    private static readonly object s_writeLock = new();

    internal override string Group { get; set; } = "";

    private sealed record IniSetting(string Section, string Key, string Value, int Line, List<string> Note);

    private static string IniPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Imlight.ini");

    [Command("settings")]
    [AuthRequired(AuthLevel.Administrator)]
    private void SettingsCommand([Remainder] string text = "") {
        if (!string.IsNullOrWhiteSpace(text)) {
            SettingCommand(text);

            return;
        }

        var sections = Read().Select(s => s.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        InformSenderClient("<center>Settings sections</center>\n\n" + string.Join(",  ", sections)
            + "\n\nType .setting and a section to see its settings, such as .setting Battle. "
            + ".setting find and a word searches every setting.", true);
    }

    [Command("setting")]
    [Alias("config")]
    [AuthRequired(AuthLevel.Administrator)]
    private void SettingCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) {
            SettingsCommand();

            return;
        }

        List<IniSetting> all;
        try {
            all = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            InformSenderClient($"Imlight.ini could not be read: {ex.Message}");

            return;
        }

        if (words[0].Equals("find", StringComparison.OrdinalIgnoreCase) || words[0].Equals("search", StringComparison.OrdinalIgnoreCase)) {
            Find(all, words.Skip(1).ToList());

            return;
        }

        // A section name can have spaces ("Login Server"), so the longest one the first words make wins.
        var sections = all.Select(s => s.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string section = null;
        var used = 0;
        for (var count = Math.Min(3, words.Count); count >= 1 && section is null; count--) {
            var wanted = Squash(string.Join(' ', words.Take(count)));
            section = sections.FirstOrDefault(s => Squash(s) == wanted);
            used = count;
        }

        if (section is null) {
            var guess = sections.Where(s => Squash(s).Contains(Squash(words[0]), StringComparison.Ordinal)).ToList();
            InformSenderClient($"There is no settings section called \"{words[0]}\"."
                + (guess.Count > 0 ? $" Did you mean {string.Join(", ", guess.Take(5))}?" : " Type .settings to see them all."));

            return;
        }

        var rest = words.Skip(used).ToList();
        var inSection = all.Where(s => s.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();

        // ".setting Battle" or ".setting Battle 2".
        if (rest.Count == 0 || (rest.Count == 1 && int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))) {
            var page = rest.Count == 1 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 1;
            ShowList($"[{section}]", inSection, page, $".setting {section}");

            return;
        }

        var setting = inSection.FirstOrDefault(s => s.Key.Equals(rest[0], StringComparison.OrdinalIgnoreCase));
        if (setting is null) {
            var guess = inSection.Where(s => s.Key.Contains(rest[0], StringComparison.OrdinalIgnoreCase)).Select(s => s.Key).Take(5).ToList();
            InformSenderClient($"[{section}] has no setting called \"{rest[0]}\"."
                + (guess.Count > 0 ? $" Did you mean {string.Join(", ", guess)}?" : $" Type .setting {section} to see them."));

            return;
        }

        if (rest.Count == 1) {
            ShowOne(setting);

            return;
        }

        Change(setting, string.Join(' ', rest.Skip(1)));
    }

    private void ShowOne(IniSetting setting) {
        var text = new StringBuilder()
            .AppendLine($"<center>[{setting.Section}] {setting.Key}</center>")
            .AppendLine()
            .AppendLine($"Now: {Shown(setting)}");
        foreach (var line in setting.Note.TakeLast(NOTE_LINES)) {
            text.AppendLine(Plain(line));
        }

        text.AppendLine()
            .Append(Locked(setting.Section)
                ? "Change this one in Ahziael (Server > Settings)."
                : $"To change it, type .setting {setting.Section} {setting.Key} and the new value."
                  + (NeedsRestart(setting) ? " It needs a server restart to count." : " It counts at once."));
        InformSenderClient(text.ToString(), true);
    }

    private void Change(IniSetting setting, string value) {
        value = value.Trim().ReplaceLineEndings(" ");
        if (Locked(setting.Section)) {
            InformSenderClient($"[{setting.Section}] can only be changed in Ahziael (Server > Settings), because it holds passwords or connection details.");

            return;
        }

        if (!Fits(setting.Value, ref value, out var why)) {
            InformSenderClient($"{setting.Key} can't be \"{value}\": {why}");

            return;
        }

        if (string.Equals(setting.Value, value, StringComparison.Ordinal)) {
            InformSenderClient($"[{setting.Section}] {setting.Key} is already {value}.");

            return;
        }

        try {
            lock (s_writeLock) {
                var lines = File.ReadAllLines(IniPath).ToList();
                var now = Read(lines).FirstOrDefault(s => s.Section.Equals(setting.Section, StringComparison.OrdinalIgnoreCase)
                                                         && s.Key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase));
                if (now is null) {
                    InformSenderClient($"[{setting.Section}] {setting.Key} is no longer in Imlight.ini.");

                    return;
                }

                lines[now.Line] = $"{now.Key} = {value}";
                File.WriteAllLines(IniPath, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            InformSenderClient($"Imlight.ini could not be saved: {ex.Message}");

            return;
        }

        Logger.Information("Setting changed in game by {0}: [{1}] {2} = {3} (was {4}).",
            Logger.Args(Context.Account?.Username, setting.Section, setting.Key, value, setting.Value));
        InformSenderClient($"[{setting.Section}] {setting.Key}: {Plain(setting.Value)} is now {Plain(value)}. "
            + (NeedsRestart(setting) ? "Restart the server for it to count." : "It counts at once, no restart needed."));
    }

    private void Find(List<IniSetting> all, List<string> words) {
        if (words.Count == 0) {
            InformSenderClient("Type .setting find and a word, such as .setting find aggro.");

            return;
        }

        var page = 1;
        if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var asked)) {
            page = asked;
            words.RemoveAt(words.Count - 1);
        }

        var found = all.Where(s => words.All(w => $"{s.Section} {s.Key}".Contains(w, StringComparison.OrdinalIgnoreCase)
                                                   || string.Join(' ', s.Note).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (found.Count == 0) {
            InformSenderClient($"No setting matches \"{string.Join(' ', words)}\".");

            return;
        }

        ShowList($"Settings about \"{string.Join(' ', words)}\"", found, page, $".setting find {string.Join(' ', words)}", withSection: true);
    }

    private void ShowList(string title, List<IniSetting> settings, int page, string again, bool withSection = false) {
        if (settings.Count == 0) {
            InformSenderClient($"{title} has no settings.");

            return;
        }

        var pages = (settings.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        page = Math.Clamp(page, 1, pages);
        var text = new StringBuilder()
            .AppendLine($"<center>{title}{(pages > 1 ? $" (page {page} of {pages})" : "")}</center>")
            .AppendLine();
        foreach (var setting in settings.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE)) {
            text.AppendLine($"{(withSection ? $"[{setting.Section}] " : "")}{setting.Key} = {Shown(setting)}");
        }

        text.AppendLine();
        if (page < pages) {
            text.AppendLine($"More: type {again} {page + 1}");
        }

        text.Append("Type .setting, the section and a name to read what it does.");
        InformSenderClient(text.ToString(), true);
    }

    // Every "Key = Value" line of Imlight.ini, with the ; comment lines just above it.
    private static List<IniSetting> Read() => Read(File.ReadAllLines(IniPath).ToList());

    private static List<IniSetting> Read(List<string> lines) {
        var settings = new List<IniSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = null;
        var note = new List<string>();
        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                section = trimmed[1..^1].Trim();
                note = [];

                continue;
            }

            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) {
                note.Add(trimmed.TrimStart(';', '#').Trim());

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (section is not null && equals > 0) {
                var key = trimmed[..equals].Trim();
                // The first one counts, as the server reads it.
                if (seen.Add($"{section}.{key}")) {
                    settings.Add(new IniSetting(section, key, trimmed[(equals + 1)..].Trim(), i, note));
                }
            }

            note = [];
        }

        return settings;
    }

    // Whether a new value is the same kind as the old one: a switch stays true/false and a number stays a number.
    private static bool Fits(string old, ref string value, out string why) {
        why = "";
        if (bool.TryParse(old, out _)) {
            var on = value.ToLowerInvariant() switch {
                "true" or "on" or "yes" or "1" => "true",
                "false" or "off" or "no" or "0" => "false",
                _ => null,
            };
            if (on is null) {
                why = "it is a switch, so type on or off (or true or false).";

                return false;
            }

            value = on;

            return true;
        }

        if (long.TryParse(old, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) {
            why = "it is a whole number, such as 5.";

            return false;
        }

        if (double.TryParse(old, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) {
            why = "it is a number, such as 1.5.";

            return false;
        }

        return true;
    }

    private static bool Locked(string section) => s_lockedSections.Contains(section);

    private static bool NeedsRestart(IniSetting setting)
        => s_startupSections.Contains(setting.Section) || s_startupKeys.Contains($"{setting.Section}.{setting.Key}");

    private static string Shown(IniSetting setting)
        => Locked(setting.Section) || s_secretWords.Any(w => setting.Key.Contains(w.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            ? "(hidden)"
            : setting.Value.Length == 0 ? "(empty)" : Plain(setting.Value.Length > 80 ? setting.Value[..80] + "..." : setting.Value);

    // The game reads <...> as its own markup.
    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<([^<>]*)>", "[$1]");

    private static string Squash(string text) => string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

}
