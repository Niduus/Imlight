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
using System.Text.RegularExpressions;

namespace Imlight.Setup;

/// <summary>
/// The searches of the cheat editor and the Bosses tab (Niduus, 2026-10-25: "Expand on existing cheats and search
/// methods"): words that mean the same (a jinx is a trap, a curse a weakness), names typed partly or with a slip
/// ("Krakon", "supnova"), and filters typed in the box: world:Darkmoor, zone:DM_, has:cheats, uses:Supernova,
/// school:Fire, kind:trap, pips:4 or pips:4-6.
/// </summary>
internal static partial class CheatSearch {

    // Words that mean the same in a search, Wizard404's and the players' words with the game's.
    private static readonly string[][] s_sameWords = [
        ["jinx", "jinxes", "trap", "traps", "negative ward"],
        ["curse", "curses", "weakness", "weaknesses", "negative charm"],
        ["blade", "blades", "buff"],
        ["shield", "shields", "absorb"],
        ["ward", "wards"],
        ["charm", "charms"],
        ["dot", "dots", "damage over time", "over time", "burn", "poison"],
        ["hot", "hots", "heal over time", "regen"],
        ["global", "globals", "bubble"],
        ["aura", "auras"],
        ["minion", "minions", "summon", "summons"],
        ["pip", "pips"],
        ["remove", "removes", "removed", "clear", "clears", "cleared", "cleanse", "strip", "take off", "lose", "loses"],
        ["damage", "hit", "hits", "attack", "hurt", "hurts"],
        ["heal", "heals", "healing", "cure"],
        ["polymorph", "transform", "form", "shape"],
        ["stun", "stuns", "stunned", "freeze"],
        ["dispel", "dispels", "dispelled"],
        ["enchant", "enchanted", "enchantment"],
        ["defeat", "defeated", "dies", "die", "dead", "killed"],
        ["player", "players", "wizard", "wizards", "enemy", "enemies"],
        ["ally", "allies", "friend", "teammate"],
        ["round", "rounds", "turn", "turns"],
        ["say", "line", "speak", "talk", "banner"],
        ["announce", "announcement", "message", "chat", "tell"],
        ["cap", "capped", "limit", "max", "equalize"],
        ["circle", "circles", "position", "spot", "slot"],
        ["cast", "casts", "casting"],
        ["random", "chance", "sometimes"],
        ["health", "hp"],
    ];

    private static readonly Dictionary<string, string[]> s_same = BuildSame();

    private static Dictionary<string, string[]> BuildSame() {
        var same = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in s_sameWords) {
            foreach (var word in group) {
                same[word] = same.TryGetValue(word, out var had) ? [.. had.Union(group)] : group;
            }
        }

        return same;
    }

    /// <summary>The word and the words that mean the same.</summary>
    internal static IReadOnlyList<string> SameAs(string word)
        => s_same.TryGetValue(word.Trim(), out var group) ? group : [word.Trim().ToLowerInvariant()];

    // The words as every search box reads them (SearchText): any case, apostrophes and punctuation dropped.
    private static string[] Words(string text) => SearchText.Words(text);

    /// <summary>
    /// Whether a text holds every word of a query: as written, as a word that means the same, as the start of a
    /// word, or a word with one slip (two for a long word).
    /// </summary>
    internal static bool Matches(string text, string query) {
        var words = Words(query);
        if (words.Length == 0) {
            return true;
        }

        var lower = SearchText.Normalize(text);
        var textWords = Words(text);

        return words.All(w => WordIn(w, lower, textWords));
    }

    private static bool WordIn(string word, string lower, string[] textWords)
        => SameAs(word).Any(s => lower.Contains(s, StringComparison.Ordinal))
           || (word.Length >= 4 && textWords.Any(t => Close(word, t)));

    // One slip in a word of 4 to 7 letters, two in a longer one; a word typed as the start of a longer one counts too.
    private static bool Close(string typed, string word) {
        if (word.StartsWith(typed, StringComparison.Ordinal)) {
            return true;
        }

        var most = typed.Length >= 8 ? 2 : 1;
        if (Math.Abs(typed.Length - word.Length) > most) {
            // "supnova" for "supernova": the letters typed in order.
            return typed.Length >= 5 && InOrder(typed, word) && word.Length - typed.Length <= 3;
        }

        return Distance(typed, word, most) <= most;
    }

    private static bool InOrder(string typed, string word) {
        var at = 0;
        foreach (var c in word) {
            if (at < typed.Length && typed[at] == c) {
                at++;
            }
        }

        return at == typed.Length && typed[0] == word[0];
    }

    // The edit distance, stopping once it is past the most allowed.
    private static int Distance(string a, string b, int most) {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++) {
            var previous = row[0];
            row[0] = i;
            var best = row[0];
            for (var j = 1; j <= b.Length; j++) {
                var keep = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = keep;
                best = Math.Min(best, row[j]);
            }

            if (best > most) {
                return most + 1;
            }
        }

        return row[b.Length];
    }

    /// <summary>
    /// How well a name fits a query, for ordering: 0 when it does not, then higher for a closer fit (the very name,
    /// its start, every word as written, every word with words that mean the same or slips).
    /// </summary>
    internal static int NameScore(string name, string query) {
        var q = string.Join(' ', Words(query));
        var n = string.Join(' ', Words(name));
        if (q.Length == 0) {
            return 1;
        }

        if (n == q) {
            return 100;
        }

        if (n.StartsWith(q, StringComparison.Ordinal)) {
            return 80;
        }

        var words = Words(q);
        if (words.All(w => n.Contains(w, StringComparison.Ordinal))) {
            return 60;
        }

        return Matches(name, query) ? 30 : 0;
    }

    /// <summary>A query's filters (world:, zone:, has:, uses:, school:, kind:, pips:) and the words left.</summary>
    internal sealed record Query(string Words, Dictionary<string, string> Filters) {

        internal string Filter(string key) => Filters.GetValueOrDefault(key, "");

        internal bool Has(string key) => Filters.ContainsKey(key);

    }

    [GeneratedRegex("""(?<key>world|zone|has|uses|school|kind|pips|does):(?:"(?<value>[^"]*)"|(?<value>\S+))""", RegexOptions.IgnoreCase)]
    private static partial Regex FilterToken();

    /// <summary>Takes the filters out of a query: world:Darkmoor, uses:"cap damage", pips:4-6...</summary>
    internal static Query Parse(string text) {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = FilterToken().Replace(text ?? "", m => {
            filters[m.Groups["key"].Value.ToLowerInvariant()] = m.Groups["value"].Value.Trim();

            return " ";
        });

        return new Query(string.Join(' ', rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)), filters);
    }

    /// <summary>A pips filter ("4", "4-6", "4+", "x"): its least and most (-1 most: no top), or null when there is none.</summary>
    internal static (int Min, int Max, bool X)? PipRange(string text) {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) {
            return null;
        }

        if (t == "x") {
            return (0, 0, true);
        }

        if (t.EndsWith('+') && int.TryParse(t[..^1], out var least)) {
            return (least, -1, false);
        }

        var parts = t.Split('-', 2);
        return int.TryParse(parts[0], out var min)
            ? (min, parts.Length > 1 && int.TryParse(parts[1], out var max) ? max : min, false)
            : null;
    }

    internal static bool CostFits((int Min, int Max, bool X)? range, int pips, bool xPips)
        => range is not { } r || (r.X ? xPips : !xPips && pips >= r.Min && (r.Max < 0 || pips <= r.Max));

    // The worlds as the game's template files name them (ObjectData/DM/..., WL_Dingo...): their codes and names.
    private static readonly (string Code, string Name)[] s_worlds = [
        ("WC", "Wizard City"), ("KT", "Krokotopia"), ("MB", "Marleybone"), ("MS", "MooShu"), ("DS", "Dragonspyre"),
        ("GH", "Grizzleheim"), ("CL", "Celestia"), ("WY", "Wysteria"), ("ZF", "Zafaria"), ("AV", "Avalon"), ("AZ", "Azteca"),
        ("KR", "Khrysalis"), ("PL", "Polaris"), ("MR", "Mirage"), ("EM", "Empyrea"), ("KA", "Karamelle"), ("LM", "Lemuria"),
        ("NV", "Novus"), ("WL", "Wallaru"), ("DM", "Darkmoor"), ("AQ", "Aquila"),
    ];

    /// <summary>The world of a creature from its template file ("ObjectData/DM/DM_WingedSorrow_Boss_A_01.xml" is Darkmoor), or "".</summary>
    internal static string WorldOfPath(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return "";
        }

        foreach (var (_, name) in s_worlds) {
            if (path.Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) || path.Contains(name, StringComparison.OrdinalIgnoreCase)) {
                return name;
            }
        }

        // A folder or the start of the file's name: "DM/", "WL_".
        foreach (var part in path.Replace('\\', '/').Split('/').Skip(1)) {
            var code = new string(part.TakeWhile(char.IsLetter).ToArray());
            if (s_worlds.FirstOrDefault(w => w.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is { Name: { } found }) {
                return found;
            }
        }

        return "";
    }

    // The world on the first line of a cheat file: "// Winged Sorrow (Rank 24, Death, 55,037 health), Darkmoor. ...".
    [GeneratedRegex(@"^\s*//[^\n]*?\),\s*(?<world>[^.,\n]+)\.")]
    private static partial Regex HeaderWorld();

    /// <summary>The world a cheat file's first line names, or "" (or "Unknown").</summary>
    internal static string WorldOfCheatFile(string text) {
        var line = (text ?? "").TrimStart('﻿', ' ', '\r', '\n', '{').TrimStart();
        var m = HeaderWorld().Match(line);

        return m.Success && !m.Groups["world"].Value.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? m.Groups["world"].Value.Trim() : "";
    }

}
