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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Social;

/// <summary>
/// The chat levels (Niduus, 2026-10-26), checked with the game's own word lists, the ones the client reads to colour
/// what is typed (Root.wad ChatFilter/*.txt, ChatFilter.cpp in the client):
/// - Menu chat (<see cref="ChatMode.Closed"/>): no typed chat at all, only the chat menu.
/// - Text chat (<see cref="ChatMode.Filtered"/>): a message with a yellow word (not on WhiteListBase) or a red one
///   (CarlinListBase or BlackListBase) reaches the others as "...".
/// - Open chat (<see cref="ChatMode.Open"/>, the default): yellow words go through, a red one (CarlinListBase) makes it
///   "...", unless [Chat] EnforceWhitelist is off.
/// How the lists read (worked out from the files, 2026-10-26): an entry inside dashes ("-cum-") is found anywhere, even
/// inside a word, and ExceptionListBase holds the words where that would be wrong ("document"); any other entry is a
/// whole word or words ("ass" blocks "ass", not "class"). CharacterReplacementMap turns look-alikes ("4", "ß", "*")
/// back into letters before looking. Whitelisted words are never red, so "bazaar" passes the "-aa-" entry.
/// </summary>
internal static class ChatFilter {

    /// <summary>What the others see instead of a message that broke the rules.</summary>
    internal const string Hidden = "...";

    /// <summary>
    /// The game hides red words itself, in every client that receives typed chat (WizardGraphicalClient r806919,
    /// DisplayChatTextMsgs: chat of type 8, radial, with Filter 2 goes through its Carlin list and comes out as "...").
    /// Only a line whose chat type has bit 0 (the quick chat bit, as the client gives menu chat lines) or bit 2 or bit 8
    /// is shown as sent. MSG_CHANNELCHAT's Flags become that chat type, so a line sent that way with this bit shows its
    /// red words. The sender's own copy is drawn by their own client and still shows "...".
    /// </summary>
    internal const uint ShowAsTypedChatBit = 0x01;

    /// <summary>Chat types the client uses (record +0x70): 8 for nearby chat, 0x80 for a whisper.</summary>
    internal const uint RadialChatType = 0x08;
    internal const uint DirectedChatType = 0x80;

    private const string FOLDER = "ChatFilter/";
    private const string PUNCTUATION = ".,!?;:\"'()[]{}*~-_";
    // How long a chat level read from the database counts before it is read again, so a Manager change applies quickly.
    private static readonly TimeSpan ChatModeFresh = TimeSpan.FromSeconds(10);

    private sealed class Lists {
        internal HashSet<string> White = new(StringComparer.Ordinal);
        internal List<string> WhitePhrases = [];
        internal HashSet<string> Exceptions = new(StringComparer.Ordinal);
        internal Dictionary<char, char> Replacements = [];
        // Carlin entries: the open chat list. Black entries: the extra phrases text chat is held to.
        internal List<string> CarlinAnywhere = [];
        internal HashSet<string> CarlinWhole = new(StringComparer.Ordinal);
        internal List<string> BlackAnywhere = [];
        internal HashSet<string> BlackWhole = new(StringComparer.Ordinal);
        internal int MostWords = 1;
    }

    private static readonly Lazy<Lists> s_lists = new(Load);
    private static readonly ConcurrentDictionary<ulong, (ChatMode Mode, DateTime ReadAt)> s_chatModes = new();

    /// <summary>[Chat] EnforceWhitelist, read live: off lets open chat accounts' red words through.</summary>
    internal static bool EnforceWhitelist => ConfigurationManager.GetValue("Chat.EnforceWhitelist", true);

    /// <summary>
    /// The account's chat level as the database has it now (the Manager can change it while the player is on), read at
    /// most every few seconds. Also brings the loaded account up to date, so the next login or zone change shows the
    /// game's chat box for the new level.
    /// </summary>
    internal static ChatMode CurrentChatMode(Account account) {
        if (account is null) {
            return ChatMode.Open;
        }

        var now = DateTime.UtcNow;
        if (s_chatModes.TryGetValue(account.AccountId, out var known) && now - known.ReadAt < ChatModeFresh) {
            return account.ChatMode = known.Mode;
        }

        var mode = account.ChatMode;
        try {
            mode = AccountCollection.GetChatMode(account.AccountId) ?? mode;
        }
        catch (Exception e) {
            Logger.Warning("Chat: could not read the chat level of account {0}: {1}", Logger.Args(account.AccountId, e.Message));
        }

        s_chatModes[account.AccountId] = (mode, now);

        return account.ChatMode = mode;
    }

    /// <summary>
    /// What the others get to see of a typed message: the message itself, <see cref="Hidden"/>, or null when this
    /// account may not type at all (menu chat). <paramref name="why"/> says what was found, for the log.
    /// </summary>
    internal static string ForOthers(string text, ChatMode mode, out string why) {
        why = null;
        switch (mode) {
            case ChatMode.Closed:
                why = "menu chat only";

                return null;
            case ChatMode.Filtered:
                if (FirstRedWord(text, textChat: true) is { } red) {
                    why = $"red \"{red}\"";

                    return Hidden;
                }

                if (FirstYellowWord(text) is { } yellow) {
                    why = $"yellow \"{yellow}\"";

                    return Hidden;
                }

                return text;
            default:
                if (EnforceWhitelist && FirstRedWord(text, textChat: false) is { } openRed) {
                    why = $"red \"{openRed}\"";

                    return Hidden;
                }

                return text;
        }
    }

    /// <summary>
    /// True when a message that may go through has red words the game would hide on its own: open chat with
    /// [Chat] EnforceWhitelist off. Such a line is sent with <see cref="ShowAsTypedChatBit"/>.
    /// </summary>
    internal static bool ShowsRedWords(string text, ChatMode mode)
        => mode == ChatMode.Open && !EnforceWhitelist && FirstRedWord(text, textChat: false) is not null;

    /// <summary>The first word not on the game's word list (yellow in the game), or null.</summary>
    internal static string FirstYellowWord(string text) {
        var lists = s_lists.Value;
        if (lists.White.Count == 0) {
            return null;
        }

        var low = " " + string.Join(' ', text.ToLowerInvariant().Split((char[]) null, StringSplitOptions.RemoveEmptyEntries)) + " ";
        foreach (var phrase in lists.WhitePhrases) {
            low = low.Replace(" " + phrase + " ", " ", StringComparison.Ordinal);
        }

        foreach (var word in low.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            var bare = word.Trim(PUNCTUATION.ToCharArray());
            if (bare.Length > 0 && !lists.White.Contains(word) && !lists.White.Contains(bare)) {
                return word;
            }
        }

        return null;
    }

    /// <summary>
    /// The first bad word or phrase (red in the game), or null. Open chat is held to the Carlin list; text chat also to
    /// the black list, phrases of allowed words that mean something they should not.
    /// </summary>
    internal static string FirstRedWord(string text, bool textChat) {
        var lists = s_lists.Value;
        var low = text.ToLowerInvariant().Replace('\t', ' ');
        var words = low.Split(' ');
        var noDashes = low.Replace("-", "");
        foreach (var form in new[] { low, Mapped(lists, low), noDashes, Mapped(lists, noDashes) }.Distinct()) {
            var found = FindRed(lists, form, words, lists.CarlinAnywhere, lists.CarlinWhole)
                ?? (textChat ? FindRed(lists, form, words, lists.BlackAnywhere, lists.BlackWhole) : null);
            if (found is not null) {
                return found;
            }
        }

        return null;
    }

    private static string FindRed(Lists lists, string form, string[] words, List<string> anywhere, HashSet<string> whole) {
        foreach (var entry in anywhere) {
            for (var at = form.IndexOf(entry, StringComparison.Ordinal); at >= 0; at = form.IndexOf(entry, at + 1, StringComparison.Ordinal)) {
                // An entry inside one word is fine when that word is an exception or an allowed word. Spaces are kept
                // in every form, so the word's number points at the same word as typed.
                if (!entry.Contains(' ')) {
                    var index = form.AsSpan(0, at).Count(' ');
                    var typed = index < words.Length ? words[index].Trim(PUNCTUATION.ToCharArray()) : "";
                    var start = form.LastIndexOf(' ', at) + 1;
                    var end = form.IndexOf(' ', at);
                    var seen = form[start..(end < 0 ? form.Length : end)].Trim(PUNCTUATION.ToCharArray());
                    if (IsFine(lists, typed) || IsFine(lists, seen)) {
                        continue;
                    }
                }

                return entry.Trim();
            }
        }

        var tokens = form.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
        var bare = tokens.Select(t => t.Trim(PUNCTUATION.ToCharArray())).ToArray();
        for (var count = 1; count <= lists.MostWords; count++) {
            for (var i = 0; i + count <= tokens.Length; i++) {
                var asTyped = string.Join(' ', tokens, i, count);
                if (whole.Contains(asTyped)) {
                    return asTyped;
                }

                var stripped = string.Join(' ', bare, i, count);
                if (whole.Contains(stripped)) {
                    return stripped;
                }
            }
        }

        return null;
    }

    private static bool IsFine(Lists lists, string word)
        => word.Length > 0 && (lists.Exceptions.Contains(word) || lists.White.Contains(word));

    private static string Mapped(Lists lists, string text) {
        if (lists.Replacements.Count == 0) {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            if (lists.Replacements.TryGetValue(chars[i], out var letter)) {
                chars[i] = letter;
            }
        }

        return new string(chars);
    }

    private static Lists Load() {
        var lists = new Lists();
        try {
            lists.White.UnionWith(ReadLines("WhiteListBase.txt").Select(l => l.ToLowerInvariant()));
            lists.WhitePhrases.AddRange(ReadLines("WhiteListPhrasesBase.txt").Select(l => l.ToLowerInvariant()));
            lists.Exceptions.UnionWith(ReadLines("ExceptionListBase.txt").Select(l => l.ToLowerInvariant()));
            Sort(ReadLines("CarlinListBase.txt"), lists.CarlinAnywhere, lists.CarlinWhole, lists);
            Sort(ReadLines("BlackListBase.txt"), lists.BlackAnywhere, lists.BlackWhole, lists);

            foreach (var line in ReadLines("CharacterReplacementMap.txt")) {
                // "a = ÀÁÂ...": the letter, then every look-alike. The first list a look-alike is in wins.
                var equals = line.IndexOf('=');
                if (line.StartsWith('#') || equals < 1) {
                    continue;
                }

                var letter = line[..equals].Trim();
                if (letter.Length != 1) {
                    continue;
                }

                foreach (var lookAlike in line[(equals + 1)..].Where(c => !char.IsWhiteSpace(c))) {
                    lists.Replacements.TryAdd(char.ToLowerInvariant(lookAlike), letter[0]);
                }
            }

            Logger.Information("Chat: read the game's word lists: {0} allowed words, {1} open chat bad words, {2} text chat bad phrases.",
                Logger.Args(lists.White.Count, lists.CarlinAnywhere.Count + lists.CarlinWhole.Count,
                    lists.BlackAnywhere.Count + lists.BlackWhole.Count));
        }
        catch (Exception e) {
            Logger.Error("Chat: could not read the game's word lists (Root.wad ChatFilter), so chat is not filtered: {0}", Logger.Args(e.Message));
        }

        return lists;
    }

    private static void Sort(IEnumerable<string> lines, List<string> anywhere, HashSet<string> whole, Lists lists) {
        foreach (var line in lines) {
            var entry = line.Trim(' ').ToLowerInvariant();
            if (entry.Length > 2 && entry.StartsWith('-') && entry.EndsWith('-')) {
                anywhere.Add(entry[1..^1]);
            }
            else if (entry.Length > 1 && (entry.StartsWith('-') || entry.EndsWith('-'))) {
                anywhere.Add(entry.Trim('-'));
            }
            else if (entry.Length > 0) {
                whole.Add(entry);
                lists.MostWords = Math.Max(lists.MostWords, entry.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            }
        }

        // Entries of only spaces would match every message.
        anywhere.RemoveAll(e => e.Length == 0 || (e.Trim().Length == 0 && e != " "));
    }

    private static IEnumerable<string> ReadLines(string name) {
        using var stream = RootArchiveLoader.GetFileStream(FOLDER + name);
        using var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) {
            if (line.Length > 0) {
                lines.Add(line);
            }
        }

        return lines;
    }
}
