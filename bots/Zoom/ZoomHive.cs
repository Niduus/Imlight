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
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The Zoom Hive Mind (Niduus, 2026-11-22: "This will all go into Zoom Hive Mind, a mini-Hive Mind on his data for us to merge
/// later"): logs/zoom/hive. It holds what Zoom found about the bosses' cheats (ZoomCheats): for every cheat of every boss he
/// fought, how often he set it off on purpose, how often it went off, how often it should have and did not, and each time in
/// detail (battle, round, what he did, health, the server log line to look for). cheats.json is the data, cheats.txt the same
/// in words, cheat-log.jsonl every single observation as it came. It is his alone: the main Hive mind never reads it, so it
/// can be merged in by hand later.
/// </summary>
internal static class ZoomHive {

    internal const string HIVE = "hive";
    internal const string CHEATS_JSON = "cheats.json";
    internal const string CHEATS_TXT = "cheats.txt";
    internal const string CHEAT_LOG = "cheat-log.jsonl";

    /// <summary>What he found works: it went off each time he set it off or it was due.</summary>
    internal const string WORKS = "works";
    /// <summary>It should have gone off and never did.</summary>
    internal const string BROKEN = "did not fire";
    /// <summary>It went off some times and not others when it should have.</summary>
    internal const string SOMETIMES = "sometimes misses";
    /// <summary>A cheat with a chance that went off at least once.</summary>
    internal const string FIRED = "fired";
    /// <summary>Never set off: he could not, or it never came up.</summary>
    internal const string UNTESTED = "not tested";

    /// <summary>One cheat of one boss, and all he saw of it.</summary>
    internal sealed class Cheat {
        public string Boss { get; set; } = "";
        public ulong Template { get; set; }
        public int Index { get; set; }
        public string When { get; set; } = "";
        public string Rule { get; set; } = "";
        public int Chance { get; set; } = 100;
        public int Battles { get; set; }
        public int SetOff { get; set; }
        public int Fired { get; set; }
        public int Missed { get; set; }
        public int ChanceTries { get; set; }
        public string Verdict { get; set; } = UNTESTED;
        public string Why { get; set; } = "";
        public DateTime Last { get; set; }
        public List<string> Seen { get; set; } = [];
    }

    internal sealed class Book {
        public List<Cheat> Cheats { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<string, Book> s_books = [];

    // Each scout has his own hive (Zoom: logs/zoom/hive; Savitar: logs/savitar/hive).
    private static UltimusSeat Me => UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom;

    internal static string Folder => Path.Combine(Me.Folder, HIVE);

    private static Book Load() {
        if (s_books.TryGetValue(Folder, out var known)) {
            return known;
        }

        Book book;
        try {
            var path = Path.Combine(Folder, CHEATS_JSON);
            book = File.Exists(path) ? JsonSerializer.Deserialize<Book>(File.ReadAllText(path)) ?? new Book() : new Book();
        }
        catch (Exception e) {
            Logger.Warning("{0}: his hive's cheat book could not be read ({1}); a new one is started.", Logger.Args(Me.Name, e.Message));
            book = new Book();
        }

        book.Cheats ??= [];
        s_books[Folder] = book;
        return book;
    }

    private static Cheat Get(Book book, string boss, ulong template, int index, string when, string rule, int chance) {
        var cheat = book.Cheats.FirstOrDefault(c => c.Template == template && c.Index == index);
        if (cheat is null) {
            cheat = new Cheat { Boss = boss, Template = template, Index = index };
            book.Cheats.Add(cheat);
        }

        // The cheat file can change (the Bosses tab): the newest words count.
        cheat.Boss = boss;
        cheat.When = when;
        cheat.Rule = rule;
        cheat.Chance = chance;
        return cheat;
    }

    /// <summary>A boss with these cheats came into one of his battles (each cheat is listed, tested or not).</summary>
    internal static void Met(string boss, ulong template, IEnumerable<(int Index, string When, string Rule, int Chance, string Untestable)> cheats) {
        lock (s_lock) {
            var book = Load();
            foreach (var (index, when, rule, chance, untestable) in cheats) {
                var cheat = Get(book, boss, template, index, when, rule, chance);
                cheat.Battles++;
                if (cheat.SetOff == 0 && cheat.Fired == 0 && cheat.Missed == 0 && untestable.Length > 0) {
                    cheat.Why = untestable;
                }
            }

            Save(book);
        }

        // His mind page (Ahziael, Zoom's mind) shows the short version; written outside this lock (the page reads it back).
        ZoomBook.Refresh();
    }

    /// <summary>
    /// One thing he saw of a cheat: it went off (<paramref name="fired"/>), or it should have and did not (<paramref name="missed"/>),
    /// or he set off one with a chance and it did not come (neither). <paramref name="setOff"/>: he did it on purpose.
    /// </summary>
    internal static void Saw(string boss, ulong template, int index, string when, string rule, int chance, bool setOff, bool fired, bool missed, string detail) {
        lock (s_lock) {
            var book = Load();
            var cheat = Get(book, boss, template, index, when, rule, chance);
            cheat.SetOff += setOff ? 1 : 0;
            cheat.Fired += fired ? 1 : 0;
            cheat.Missed += missed ? 1 : 0;
            cheat.ChanceTries += !fired && !missed && setOff ? 1 : 0;
            cheat.Last = DateTime.UtcNow;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm}  {(fired ? "FIRED" : missed ? "MISSED" : "no roll")}: {detail}";
            cheat.Seen.Add(line);
            if (cheat.Seen.Count > 12) {
                cheat.Seen.RemoveRange(0, cheat.Seen.Count - 12);
            }

            (cheat.Verdict, cheat.Why) = Judge(cheat);
            Save(book);
            try {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, CHEAT_LOG), JsonSerializer.Serialize(new {
                    At = DateTime.UtcNow, Boss = boss, Template = template, Cheat = index, When = when, Rule = rule, Chance = chance,
                    SetOff = setOff, Fired = fired, Missed = missed, Detail = detail,
                }) + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("Zoom: could not add to his cheat log: {0}", Logger.Args(e.Message));
            }
        }

        ZoomBook.Refresh();
    }

    // What all he saw of a cheat comes to.
    private static (string Verdict, string Why) Judge(Cheat c) {
        if (c.Missed > 0 && c.Fired == 0) {
            return (BROKEN, $"should have gone off {c.Missed} time(s) and never did");
        }

        if (c.Missed > 0) {
            return (SOMETIMES, $"went off {c.Fired} time(s), but not {c.Missed} other time(s) it should have");
        }

        if (c.Fired > 0) {
            return c.Chance < 100 ? (FIRED, $"went off {c.Fired} of {c.Fired + c.ChanceTries} time(s) at {c.Chance}%") : (WORKS, $"went off all {c.Fired} time(s)");
        }

        // A chance cheat he set off so often that never seeing it is under a 1-in-20 chance.
        if (c.Chance < 100 && c.ChanceTries > 0 && Math.Pow(1 - (c.Chance / 100.0), c.ChanceTries) < 0.05) {
            return (BROKEN, $"set off {c.ChanceTries} times at {c.Chance}% and never went off (under a 1 in 20 chance)");
        }

        return (UNTESTED, c.ChanceTries > 0 ? $"set off {c.ChanceTries} time(s) at {c.Chance}%, not yet enough to tell" : c.Why);
    }

    private static void Save(Book book) {
        try {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, CHEATS_JSON), JsonSerializer.Serialize(book, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllLines(Path.Combine(Folder, CHEATS_TXT), Words(book, full: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("Zoom: could not write his hive's cheat book: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// Whether this boss's cheats are already known well: every one he can test was set off, or was due, at least
    /// <paramref name="times"/> times. Then he does not study it again (a scout's time goes to new bosses).
    /// </summary>
    internal static bool Known(ulong template, int count, int times = 2) {
        lock (s_lock) {
            var mine = Load().Cheats.Where(c => c.Template == template).ToList();
            return mine.Count >= count && mine.All(c => c.Why.Length > 0 && c.SetOff + c.Fired + c.Missed == 0 && c.Verdict == UNTESTED
                                                        ? true
                                                        : c.SetOff + c.Fired + c.Missed >= times);
        }
    }

    /// <summary>His cheat findings in words: broken ones first, by boss. Short: only the counts and the broken ones.</summary>
    internal static List<string> Summary() {
        lock (s_lock) {
            return Words(Load(), full: false);
        }
    }

    private static List<string> Words(Book book, bool full) {
        var all = book.Cheats;
        var lines = new List<string> {
            $"BOSS CHEATS ({Me.Name} Hive Mind): {all.Count} cheat(s) of {all.Select(c => c.Template).Distinct().Count()} boss(es). " +
            $"{all.Count(c => c.Verdict == WORKS || c.Verdict == FIRED)} work, {all.Count(c => c.Verdict == BROKEN)} did not fire, " +
            $"{all.Count(c => c.Verdict == SOMETIMES)} sometimes miss, {all.Count(c => c.Verdict == UNTESTED)} not tested yet.",
        };
        int Rank(Cheat c) => c.Verdict switch { BROKEN => 0, SOMETIMES => 1, UNTESTED => 3, _ => 2 };
        foreach (var boss in all.GroupBy(c => (c.Template, c.Boss)).OrderBy(g => g.Min(Rank)).ThenBy(g => g.Key.Boss)) {
            var shown = full ? boss.OrderBy(c => c.Index).ToList() : [.. boss.Where(c => Rank(c) <= 1).OrderBy(c => c.Index)];
            if (shown.Count == 0) {
                continue;
            }

            lines.Add("");
            lines.Add($"{(boss.Key.Boss.Length > 0 ? boss.Key.Boss : "a creature")} (template {boss.Key.Template})");
            foreach (var c in shown) {
                lines.Add($"  #{c.Index} {c.Verdict.ToUpperInvariant()}: {c.Rule}");
                if (c.Why.Length > 0) {
                    lines.Add($"      {c.Why}. Set off on purpose {c.SetOff}x, went off {c.Fired}x, missed {c.Missed}x, in {c.Battles} battle(s).");
                }

                foreach (var seen in full ? c.Seen.TakeLast(6) : c.Seen.Where(s => s.Contains("MISSED")).TakeLast(2)) {
                    lines.Add($"      {seen}");
                }
            }
        }

        return lines;
    }

}
