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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The game's own Menu Chat phrases (the QuickChatEntry tree in Root.wad), so server-played wizards speak only in Menu
/// Chat, as a menu-chat player does (Niduus, 2026-11-13: "Able to use Menu Chat to conversate and only Menu Chat"). A
/// phrase goes out by its chat ID (MSG_REQUESTRADIALQUICKCHAT), and every game shows it from its own menu.
/// </summary>
internal static class MenuChatBook {

    internal sealed record Phrase(uint Id, string Text);

    private static readonly object s_lock = new();
    private static List<Phrase> s_phrases;

    /// <summary>All phrases with an ID and a readable text (read once from Root.wad).</summary>
    internal static IReadOnlyList<Phrase> Phrases {
        get {
            lock (s_lock) {
                return s_phrases ??= Load();
            }
        }
    }

    internal static Phrase ById(uint id) => Phrases.FirstOrDefault(p => p.Id == id);

    /// <summary>A phrase whose text says one of the words (the first word that has any wins), picked at random; or null.</summary>
    internal static Phrase Find(Random random, params string[] words) => Find(random, [], words);

    /// <summary>The same, leaving out phrases that say any of <paramref name="leaveOut"/> (whole words, any case).</summary>
    internal static Phrase Find(Random random, string[] leaveOut, string[] words) {
        foreach (var word in words) {
            var matches = Phrases.Where(p => Says(p.Text, word) && !leaveOut.Any(l => Says(p.Text, l))).ToList();
            if (matches.Count > 0) {
                return matches[random.Next(matches.Count)];
            }
        }

        return null;
    }

    /// <summary>Whether the phrase says the words (whole words, any case).</summary>
    internal static bool Says(string text, string words) {
        var at = (text ?? "").IndexOf(words, StringComparison.OrdinalIgnoreCase);
        while (at >= 0) {
            var before = at == 0 || !char.IsLetter(text[at - 1]);
            var end = at + words.Length;
            var after = end >= text.Length || !char.IsLetter(text[end]);
            if (before && after) {
                return true;
            }

            at = text.IndexOf(words, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static List<Phrase> Load() {
        var phrases = new List<Phrase>();
        try {
            var wad = RootArchiveLoader.GetRootWad();
            if (wad is null) {
                RootArchiveLoader.ReloadRootWad();
                wad = RootArchiveLoader.GetRootWad();
            }

            var names = wad?.Files.Keys.Where(k => k.Contains("chat", StringComparison.OrdinalIgnoreCase)
                                                   && k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            foreach (var name in names) {
                QuickChatEntry root;
                try {
                    root = RootArchiveLoader.GetFile<QuickChatEntry>(name);
                }
                catch (Exception) {
                    continue;
                }

                if (root is null) {
                    continue;
                }

                var before = phrases.Count;
                Walk(root, phrases);
                Logger.Information("Menu Chat: {0} phrases read from {1}.", Logger.Args(phrases.Count - before, name));
            }
        }
        catch (Exception ex) {
            Logger.Warning("Menu Chat: the phrases could not be read: {0}", Logger.Args(ex.Message));
        }

        if (phrases.Count == 0) {
            Logger.Warning("Menu Chat: no Menu Chat phrases were found in Root.wad, so server-played wizards stay silent.");
        }

        return [.. phrases.GroupBy(p => p.Id).Select(g => g.First())];
    }

    private static void Walk(QuickChatEntry entry, List<Phrase> into) {
        if (entry is null) {
            return;
        }

        string text = entry.m_text;
        string label = entry.m_label;
        var shown = Readable(text) ?? Readable(label);
        if (entry.m_chatID != 0 && !string.IsNullOrWhiteSpace(shown) && !entry.m_membersOnly && string.IsNullOrEmpty((string) entry.m_unlockKey)) {
            into.Add(new Phrase(entry.m_chatID, shown.Trim()));
        }

        foreach (var child in entry.m_childEntries ?? []) {
            Walk(child, into);
        }
    }

    // A text key ("Table_00001") becomes its English line; plain text stays as it is.
    private static string Readable(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var english = Locale.GetEnglishName(text);
        // A phrase names its text by a bare key ("chatEntryKey73"): it was taken for the text itself, so Ultimus heard
        // "chatEntryKey73" for every phrase and never answered a hello (Niduus, 2026-11-13: "bots do not respond to Menu chats").
        if (english.Length == 0 && !text.Contains(' ')) {
            english = Locale.FindInAnyTable(text);
            if (english.Length == 0 && text.StartsWith("chatEntryKey", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
        }

        return english.Length > 0 ? english : text.Contains('_') && !text.Contains(' ') ? null : text;
    }

}
