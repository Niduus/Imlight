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
 * 
 * ========================================================================
 * LOCALE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages localization data retrieval and processing for English 
 * language resources, providing efficient lookup of localized strings.
 * 
 * USAGE EXAMPLE:
 * // Retrieve a localized name from a specific table
 * string characterName = Locale.GetEnglishName("CharacterNames", "001");
 * 
 * // Retrieve a localized name using a full key
 * string interactableName = Locale.GetEnglishName("Interactables_00000008");
 * 
 * // Retrieve first and last name keys for a full name
 * var (firstKey, lastKey) = Locale.GetEnglishNameKeys("WC-NPCs_00000024");
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using Imcodec.Wad;
using Imlight.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Imlight.CoreLib.Shared.Resources;

internal class Locale : RootDirectoryResourceSingleton<Locale>, IMemoryStreamDisposable {

    protected override string DirectoryName =>
        $"Locale/{ConfigurationManager.GetValue("Locale.Language", "en-US")}/";

    private static readonly string s_qustFilePrefix = "WizQst";
    private static Dictionary<string, Dictionary<string, string>> s_data = [];
    private static Dictionary<string, (string firstKey, string lastKey)> s_namePartsMapping = [];

    // The quest dialogue tables (WizQst...), kept only until our own quests' English lines are matched to them.
    private static Dictionary<string, Dictionary<string, string>> s_questTables = [];

    protected override void AfterLoad() {
        // There are aoubt ~5,000 files here. They take up about 200MB of memory.
        // At this point, they are all loaded into memory. This function should process the locales
        // and give us data we actually need.

        Logger.Information("Loaded {0} English locale files.",
            Logger.Args(Files.Count));

        s_data = [];
        foreach (var file in Files) {
            var stream = file.Value;
            var record = file.Key;

            var data = ProcessLocaleFile(record, stream);

            var tableName = record.FileName;
            tableName = tableName.Replace(DirectoryName, "");
            tableName = tableName.Split('.')[0];

            if (record.FileName.StartsWith(s_qustFilePrefix, StringComparison.Ordinal)) {
                s_questTables[tableName] = data;

                continue;
            }

            s_data.Add(tableName, data);
        }

        ProcessNameParts();
        DisposeStream();
    }

    /// <summary>
    /// Retrieves the English name associated with the specified key.
    /// </summary>
    /// <param name="key">The key used to retrieve the English name.</param>
    /// <returns>The English name associated with the specified key, or an empty string if the key is not found.</returns>
    public static string GetEnglishName(string key) {
        if (key == "") {
            return "";
        }

        var parts = key.Split('_');
        if (parts.Length != 2) {
            return "";
        }
        var tableName = parts[0];
        var id = parts[1];

        if (!s_data.TryGetValue(tableName, out var table)) {
            return "";
        }

        if (!table.TryGetValue(id, out var value)) {
            return "";
        }

        return value;
    }

    /// <summary>One of the game's text tables (ZoneLocName), id to line, or null when the game has no such table.</summary>
    public static IReadOnlyDictionary<string, string> Table(string tableName) {
        if (s_data.Count == 0) {
            _ = Instance;
        }

        return s_data.TryGetValue(tableName, out var table) ? table : null;
    }

    private static List<(string Key, string Text)> s_phrases;

    /// <summary>
    /// The game's short lines (100 letters or fewer, each text once), in key order, so their numbers stay the same
    /// from boot to boot. `.ca 1` shows the first; the app's Lookup tab lists them with their numbers (phrases.csv).
    /// </summary>
    public static List<(string Key, string Text)> Phrases {
        get {
            if (s_phrases is not null) {
                return s_phrases;
            }

            if (s_data.Count == 0) {
                _ = Instance;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var phrases = new List<(string Key, string Text)>();
            foreach (var (table, lines) in s_data) {
                foreach (var (id, text) in lines) {
                    var line = text?.Trim() ?? "";
                    if (line.Length > 0 && line.Length <= 100) {
                        phrases.Add(($"{table}_{id}", line));
                    }
                }
            }

            phrases.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            s_phrases = [.. phrases.Where(p => seen.Add(p.Text))];

            return s_phrases;
        }
    }

    /// <summary>
    /// The game's own lines that hold every word of <paramref name="query"/>: a line equal to it first, then the
    /// shortest. Each comes with its key (Table_Id), which is what the game shows a line by. Used by .ca.
    /// </summary>
    public static List<(string Key, string Text)> Search(string query, int maxResults) {
        if (s_data.Count == 0) {
            _ = Instance;
        }

        static string Plain(string text) => text.Trim().TrimEnd('!', '.', '?').Trim();
        var plainQuery = Plain(query ?? "");
        var words = plainQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            return [];
        }

        var found = new List<(string Key, string Text)>();
        foreach (var (table, lines) in s_data) {
            foreach (var (id, text) in lines) {
                if (!string.IsNullOrEmpty(text) && text.Length <= 200
                    && Array.TrueForAll(words, w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) {
                    found.Add(($"{table}_{id}", text));
                }
            }
        }

        found.Sort((a, b) => {
            var exactA = Plain(a.Text).Equals(plainQuery, StringComparison.OrdinalIgnoreCase);
            var exactB = Plain(b.Text).Equals(plainQuery, StringComparison.OrdinalIgnoreCase);

            return exactA != exactB ? (exactA ? -1 : 1) : a.Text.Length.CompareTo(b.Text.Length);
        });

        return found.Count > maxResults ? found.GetRange(0, maxResults) : found;
    }

    private static Dictionary<string, string> s_keyByText;

    /// <summary>
    /// The key of the game's line whose text is exactly <paramref name="text"/> (case aside), or null. A shop's own
    /// line (WizardShops) comes first, then the other tables by name. Used for the titles of the Customs tab's vendors.
    /// </summary>
    public static string KeyOf(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        if (s_keyByText is null) {
            if (s_data.Count == 0) {
                _ = Instance;
            }

            var byText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, lines) in s_data.OrderBy(t => t.Key == "WizardShops" ? 0 : 1).ThenBy(t => t.Key, StringComparer.Ordinal)) {
                foreach (var (id, line) in lines.OrderBy(l => l.Key, StringComparer.Ordinal)) {
                    if (!string.IsNullOrWhiteSpace(line) && line.Length <= 100) {
                        byText.TryAdd(line.Trim(), $"{table}_{id}");
                    }
                }
            }

            s_keyByText = byText;
        }

        return s_keyByText.TryGetValue(text.Trim(), out var key) ? key : null;
    }

    /// <summary>
    /// Retrieves the English name from the specified table and key.
    /// </summary>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="key">The key to search for in the table.</param>
    /// <returns>The English name associated with the specified key in the table, or an empty string if not found.</returns>
    public static string GetEnglishName(string tableName, string key) {
        if (key == "" || tableName == "") {
            return "";
        }

        if (!s_data.TryGetValue(tableName, out var table)) {
            return "";
        }

        if (!table.TryGetValue(key, out var value)) {
            return "";
        }

        return value;
    }

    /// <summary>
    /// Retrieves the first and last name keys for a full name entry.
    /// </summary>
    /// <param name="fullNameKey">The full key (e.g., "WC-NPCs_00000024") of the full name entry.</param>
    /// <returns>A tuple containing the first name key and last name key, or empty strings if no mapping exists.</returns>
    public static (string firstNameKey, string lastNameKey) GetEnglishNameKeys(string fullNameKey) {
        if (string.IsNullOrEmpty(fullNameKey)) {
            return ("", "");
        }

        if (s_namePartsMapping.TryGetValue(fullNameKey, out var nameParts)) {
            return nameParts;
        }

        return ("", "");
    }

    /// <summary>
    /// Every line of the game's text tables (quest dialogue tables too, while they are kept) whose text, letters and
    /// digits only, is one of <paramref name="wanted"/>: wanted text to the keys (Table_Id) that hold it. Used to give
    /// our own quests, written in English, the game's own lines.
    /// </summary>
    public static Dictionary<string, List<string>> KeysOfTexts(IReadOnlySet<string> wanted) {
        if (s_data.Count == 0) {
            _ = Instance;
        }

        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Scan(Dictionary<string, Dictionary<string, string>> tables) {
            foreach (var (table, lines) in tables) {
                foreach (var (id, text) in lines) {
                    var plain = PlainText(text);
                    if (plain.Length == 0 || !wanted.Contains(plain)) {
                        continue;
                    }

                    if (!found.TryGetValue(plain, out var keys)) {
                        keys = [];
                        found[plain] = keys;
                    }

                    keys.Add($"{table}_{id}");
                }
            }
        }

        Scan(s_questTables);
        Scan(s_data);

        return found;
    }

    /// <summary>
    /// Whether the game has the quest dialogue line <paramref name="key"/> (WizQst1625CE_00000009): null when the key
    /// is no quest line or its table is not loaded, so nothing is known of it.
    /// </summary>
    public static bool? HasQuestLine(string key) {
        if (s_data.Count == 0) {
            _ = Instance;
        }

        var bar = key?.LastIndexOf('_') ?? -1;
        // The file names carry the locale folder, so the quest tables can sit with the others in s_data
        // (v0.10.382 found none to check and left out nothing; Niduus 2026-10-25, Lady Oriel).
        if (bar <= 0 || !key.StartsWith(s_qustFilePrefix, StringComparison.Ordinal)
            || !(s_questTables.TryGetValue(key[..bar], out var lines) || s_data.TryGetValue(key[..bar], out lines))) {
            return null;
        }

        return lines.ContainsKey(key[(bar + 1)..]);
    }

    /// <summary>The quest dialogue tables are only needed once; this lets them go.</summary>
    public static void ReleaseQuestTables()
        => s_questTables = [];

    /// <summary>A line's letters and digits only, in lower case, so small differences in spaces, quotes and dashes match.</summary>
    public static string PlainText(string text) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }

        var plain = new StringBuilder(text.Length);
        foreach (var c in text) {
            if (char.IsLetterOrDigit(c)) {
                plain.Append(char.ToLowerInvariant(c));
            }
        }

        return plain.ToString();
    }

    public void DisposeStream()
        => Files.Clear();

    private static Dictionary<string, string> ProcessLocaleFile(FileEntry record, Memory<byte>? stream) {
        if (stream is null || stream.Value.IsEmpty) {
            return [];
        }
        var content = Encoding.Unicode.GetString(stream.Value.Span);
        var estimatedCount = Math.Max(16, content.Length / 40);
        var data = new Dictionary<string, string>(estimatedCount);
        using var reader = new StringReader(content);

        if (reader.ReadLine() is null) {
            return data;
        }

        while (true) {
            var key = reader.ReadLine();
            if (key is null) {
                break;
            }

            // Skip delimiter
            if (reader.ReadLine() is null) {
                break;
            }

            var value = reader.ReadLine();
            if (value is null) {
                break;
            }

            data[key] = value;
        }
        return data;
    }

    private static string[] ReadStrings(Memory<byte>? stream) {
        if (stream is null) {
            return [];
        }

        var bytes = stream?.ToArray();
        var stringArray = Encoding.Unicode
            .GetString(bytes)
            .Split([Environment.NewLine], StringSplitOptions.None);

        return stringArray;
    }

    private static void ProcessNameParts() {
        s_namePartsMapping = [];

        if (!s_data.TryGetValue("WC-NPCs", out var table)) {
            Logger.Warning("WC-NPCs locale table not found; cannot process name parts mapping.");

            return;
        }

        var tableName = "WC-NPCs";
        foreach (var entry in table) {
            var key = entry.Key;
            var value = entry.Value.Trim();

            if (string.IsNullOrEmpty(value) || !value.Contains(' ')) {
                continue;
            }

            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                continue;
            }

            var firstName = parts[0];
            var lastName = parts[1];

            string? firstNameKey = null;
            string? lastNameKey = null;

            foreach (var searchEntry in table) {
                var searchValue = searchEntry.Value.Trim();

                if (firstNameKey == null && searchValue == firstName) {
                    firstNameKey = searchEntry.Key;
                }

                if (lastNameKey == null && searchValue == lastName) {
                    lastNameKey = searchEntry.Key;
                }

                if (firstNameKey != null && lastNameKey != null) {
                    break;
                }
            }

            if (firstNameKey != null && lastNameKey != null) {
                var fullKey = $"{tableName}_{key}";
                var firstFullKey = $"{tableName}_{firstNameKey}";
                var lastFullKey = $"{tableName}_{lastNameKey}";
                s_namePartsMapping[fullKey] = (firstFullKey, lastFullKey);
            }
        }

        Logger.Information("Processed {0} NPC name part mappings.",
            Logger.Args(s_namePartsMapping.Count));
    }

}
