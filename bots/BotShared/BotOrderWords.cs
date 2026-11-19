/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Text.RegularExpressions;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The words every bot reads an order with (Niduus, 2026-11-19: "Bots need better obedience to commands and to better
/// identify them and their meaning"). Ultimus, the TCO7 and True Form read their group leader's orders through
/// UltimusOrders, and True Form's own asks through TrueForm.Order; both use these: typos fixed by one letter, polite
/// questions read as asks ("can you blade Mindy next round?"), and lines that are no order at all ("kill yourself") left alone.
/// </summary>
internal static class BotOrderWords {

    /// <summary>Whether two words differ by one letter: one changed, added, left out, or two side by side swapped.</summary>
    internal static bool OneOff(string a, string b) {
        if (a == b) {
            return true;
        }

        if (a is null || b is null || Math.Abs(a.Length - b.Length) > 1) {
            return false;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++) {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++) {
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length] <= 1;
    }

    /// <summary>
    /// The order words a typo is fixed to ("sheild" is "shield", "folow" is "follow", "atack" is "attack"). Only words of
    /// four letters or more are fixed, and only toward these, so a name is never turned into something else.
    /// </summary>
    internal static readonly string[] Vocabulary = [
        "heal", "shield", "blade", "trap", "attack", "follow", "stay", "flee", "pass", "draw", "discard", "cast", "defensive",
        "offensive", "defensively", "offensively", "leave", "group", "teleport", "focus", "wait", "come", "enter", "fight",
        "battle", "sigil", "protect", "boost", "target", "first", "quest", "story", "escape", "skip", "throw", "aggressive",
        "careful", "treasure", "card", "cards", "behind", "here", "over", "with", "join", "start", "please",
    ];

    // Words a typo of which would read as an order word but are words of their own ("heat", "head", "tray", "lead").
    private static readonly HashSet<string> s_keep = ["heat", "head", "held", "hell", "real", "deal", "meal", "seal", "tray", "pray",
        "past", "mass", "fast", "last", "cash", "case", "cost", "tracks", "fill", "fold", "stay", "star", "stat", "comb", "came",
        "some", "home", "hope", "here", "hero", "were", "where", "there", "with", "wish", "wait", "want", "what", "when", "then",
        "than", "that", "this", "those", "these", "card", "care", "cart", "hard", "draws", "drew", "grow", "draw", "dead", "lead",
        "read", "leaf", "lean", "leaves", "over", "ever", "even", "oven", "join", "coin", "skin", "ship", "slip", "fight", "night",
        "light", "right", "might", "sight", "tight", "first", "flee", "free", "feel", "fees", "glee", "flew", "plea", "pass", "boss",
        "loss", "less", "kiss", "miss", "mess", "trap", "wrap", "trip", "camp", "lamp", "blame", "blames", "flame", "bladed", "cats", "east", "vast",
        "dear", "bear", "near", "year", "hear", "team", "teal", "steal", "stays", "comes", "draft", "fights", "stars", "start", "card", "hello", "wear"];

    /// <summary>A word as the order word it was meant to be, one letter off, or as it was.</summary>
    internal static string Fix(string word) {
        if (string.IsNullOrEmpty(word) || word.Length < 4 || Vocabulary.Contains(word) || s_keep.Contains(word) || word.Any(char.IsDigit)) {
            return word;
        }

        var near = Vocabulary.Where(v => v.Length >= 4 && OneOff(word, v)).ToList();
        // Two words equally near ("stat": "stay", "start"): neither is guessed.
        return near.Count == 1 ? near[0] : word;
    }

    /// <summary>
    /// The first words of a line with their typos fixed ("sheild me" is "shield me", "pls folow me" is "pls follow me"). Words
    /// after "cast", "use", "on", "at" and the like are a card's or someone's name and are kept as said.
    /// </summary>
    internal static string FixTypos(string lower) {
        var words = (lower ?? "").Split(' ');
        var naming = false;
        for (var i = 0; i < words.Length; i++) {
            var w = words[i];
            if (w.Length == 0) {
                continue;
            }

            if (!naming && i < 4) {
                words[i] = Fix(w);
            }

            if (words[i] is "cast" or "use" or "throw" or "discard" or "toss" or "drop" or "on" or "at" or "onto" or "to" or "for"
                or "after" or "find" or "focus" or "kill" or "target") {
                naming = true;
            }
        }

        return string.Join(' ', words);
    }

    private static readonly Regex s_polite = new(
        @"^\s*(?:hey\s+|so\s+|ok\s+|okay\s+)?(?:can|could|would|will|cud|can't|cant)\s+(?:you|u|ya|yall|y'all)\b|^\s*(?:please|pls|plz)\b|^\s*(?:mind|would you mind)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A question that is really an ask ("can you blade Mindy next round?", "could you heal me?", "please shield us?"), with
    /// the question mark taken off; null for a real question ("what is in your hand?").
    /// </summary>
    internal static string AskOf(string text) {
        var t = (text ?? "").Trim();
        if (!t.EndsWith('?')) {
            return t;
        }

        var bare = t.TrimEnd('?', ' ', '!');
        // A bot's name first ("Garm, can you heal me?") does not change what the line is.
        var afterName = Regex.Replace(bare, @"^\s*[A-Za-z][A-Za-z' ]{0,24}[,:]\s*", "");
        return s_polite.IsMatch(bare) || s_polite.IsMatch(afterName) ? bare : null;
    }

    private static readonly Regex s_rude = new(
        @"\b(kill|hurt|shoot|delete)\s+(yo)?ur\s*self\b|\bkill\s+yourself\b|\bkys\b|\bunalive\b|\bcommit\s+(die|sudoku|suicide)\b|\bgo\s+die\b|\bdrop\s+dead\b|\bend\s+yourself\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A line that sounds like an order but is not one to follow ("kill yourself", "go die"): no bot acts on it.</summary>
    internal static bool NotAnOrder(string text) => s_rude.IsMatch(text ?? "");

    /// <summary>Lower case, letters and digits only, for comparing names ("Mitsu-Ko" is "mitsuko").</summary>
    internal static string Flat(string text) => Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>
    /// Whether a name as said is meant for a name ("mitsu" for Mitsuko, "mindy" for Mindy Moon, "mtisu" for Mitsu): the same,
    /// its start, its first word, or one letter off either of those.
    /// </summary>
    internal static bool NameMatches(string said, string name) {
        var want = Flat(said);
        var full = Flat(name);
        if (want.Length < 2 || full.Length == 0) {
            return false;
        }

        if (full == want || full.StartsWith(want, StringComparison.Ordinal)) {
            return true;
        }

        var first = Flat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        return want.Length >= 4 && (OneOff(want, full) || (first.Length >= 4 && OneOff(want, first)));
    }

}
