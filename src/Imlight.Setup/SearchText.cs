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
using System.Text;

namespace Imlight.Setup;

/// <summary>
/// How every search box in the app reads what is typed (Niduus, 2026-10-25: "Expand on existing ... search methods"):
/// the words in any order, any case, and without apostrophes or punctuation, so "trickster jacks", "Trickster Jack's"
/// and "jack trickster" all find Trickster Jack's, and "cl main" finds CL-MAIN.
/// </summary>
internal static class SearchText {

    /// <summary>Text as searches see it: lower case, apostrophes dropped, other punctuation as spaces.</summary>
    internal static string Normalize(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text) {
            if (char.IsLetterOrDigit(c)) {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c is not ('\'' or '’' or '‘' or '`')) {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    /// <summary>A setting's key as words, so "unlimited mana" finds UnlimitedMana: "UnlimitedMana" becomes "Unlimited Mana".</summary>
    internal static string SplitCamel(string key) {
        var builder = new StringBuilder(key.Length + 8);
        for (var i = 0; i < key.Length; i++) {
            if (i > 0 && char.IsUpper(key[i]) && (char.IsLower(key[i - 1]) || (i + 1 < key.Length && char.IsLower(key[i + 1]) && char.IsUpper(key[i - 1])))) {
                builder.Append(' ');
            }

            builder.Append(key[i]);
        }

        return builder.ToString();
    }

    /// <summary>The words of a search, as Normalize leaves them.</summary>
    internal static string[] Words(string? query) => Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether the text holds every word of the search (an empty search matches everything).</summary>
    internal static bool Matches(string? text, string? query) {
        var words = Words(query);
        if (words.Length == 0) {
            return true;
        }

        var haystack = Normalize(text);
        foreach (var word in words) {
            if (!haystack.Contains(word, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

}
