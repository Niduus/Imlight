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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Puzzles for the wizards the server plays (Ultimus and the Quest Team), solved the way a player solves them (Niduus,
/// 2026-11-15: "Is Ultimus able to do puzzles or fed the answers?", then "All" to: work click orders out from the quest's
/// hints and remember them once they work, pick the right answer, play mini-game steps, flag puzzle steps when reading ahead).
/// Only what a player sees is used: the lines the quest's people say and the names of the things in the room. Nothing is
/// fed from the server's side: a wrong order is found by trying it, as a player finds it, and the next order is tried.
/// What worked is written down (bot-puzzles.json next to the ID lookup) and used again by every wizard after.
/// </summary>
internal static class BotPuzzle {

    internal const string FILE = "bot-puzzles.json";

    /// <summary>Two spots this close are the same thing.</summary>
    private const float SAME_SPOT = 150f;

    /// <summary>Orders tried before the step is written up for Niduus (4 things have 24 orders).</summary>
    internal const int MOST_TRIES = 24;

    /// <summary>A player waits a moment after the last click for something to happen before trying again.</summary>
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(20);

    // Words a quest's people use for a puzzle ("Some combination of using these Sand Thumpers will summon her. The trick is
    // just figuring out what order in which to activate them", Cavern of the Wurm).
    // Only words that mean a puzzle: "congratulations are in order" and "the take-off sequence" are not one.
    private static readonly Regex s_puzzleWords = new(
        @"\b(puzzles?|riddles?|mini-?games?|(what|which|right|correct|proper|same|that|this) order|order in which|(some|right|correct|secret) combination)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex s_words = new(@"[A-Za-z']{3,}", RegexOptions.Compiled);

    // Words too common to tell two answers or two things apart.
    private static readonly HashSet<string> s_common = new(StringComparer.OrdinalIgnoreCase) {
        "the", "and", "you", "your", "for", "with", "that", "this", "what", "which", "are", "was", "will", "have", "has",
        "not", "but", "can", "from", "they", "them", "their", "there", "here", "into", "out", "about", "just", "all", "one",
    };

    private sealed class Book {
        public Dictionary<string, List<float[]>> Orders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Answers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object s_lock = new();
    private static Book s_book;

    // The quest's lines a player reads, by quest name.
    private static readonly ConcurrentDictionary<string, List<string>> s_lines = new(StringComparer.OrdinalIgnoreCase);

    // What each wizard clicked for a puzzle step, in order, and the answer it picked last ("charId|quest|goal").
    private static readonly ConcurrentDictionary<string, List<(float X, float Y)>> s_clicked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_chose = new(StringComparer.OrdinalIgnoreCase);

    // The order a step is tried in, which try it is on, and its last click ("quest|goal").
    private static readonly ConcurrentDictionary<string, List<(float X, float Y)>> s_base = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> s_try = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> s_lastClick = new(StringComparer.OrdinalIgnoreCase);

    // What the wizards have to say about it, for their own thoughts and notes.
    private static readonly ConcurrentQueue<(ulong CharId, string Key, string Text)> s_news = new();

    private static string FilePath => Path.Combine(IdCatalog.OutputPath, FILE);

    // -----------------------------------------------------------------
    // Reading the quest as a player reads it
    // -----------------------------------------------------------------

    /// <summary>The lines the quest's people say, in plain English, in the order of its steps.</summary>
    internal static List<string> Lines(QuestTemplate quest) {
        if (quest is null || string.IsNullOrEmpty(quest.m_questName)) {
            return [];
        }

        return s_lines.GetOrAdd(quest.m_questName, _ => {
            var lines = new List<string>();
            void Add(ActorDialogListBase list) {
                foreach (var entry in ((list as ActorDialogList)?.m_dialogs ?? []).SelectMany(d => d?.m_dialogEntries ?? [])) {
                    var text = English(entry?.m_dialog);
                    if (text.Length > 0 && !lines.Contains(text)) {
                        lines.Add(text);
                    }
                }
            }

            try {
                // The quest's name is read too ("The Right Combination").
                if (English(quest.m_questTitle) is { Length: > 0 } title) {
                    lines.Add(title);
                }

                Add(quest.m_dialogList);
                foreach (var goal in quest.m_goals ?? []) {
                    Add(goal?.m_dialogList);
                }
            }
            catch (Exception ex) {
                Logger.Debug("Bot puzzles: the lines of {0} could not be read: {1}", Logger.Args((string) quest.m_questName, ex.Message));
            }

            return lines;
        });
    }

    /// <summary>The lines that speak of a puzzle (a riddle, an order, a combination), with the line before and after each.</summary>
    internal static List<string> PuzzleLines(QuestTemplate quest) {
        var lines = Lines(quest);
        var picked = new List<string>();
        for (var i = 0; i < lines.Count; i++) {
            if (!s_puzzleWords.IsMatch(lines[i])) {
                continue;
            }

            for (var j = Math.Max(0, i - 1); j <= Math.Min(lines.Count - 1, i + 1); j++) {
                if (!picked.Contains(lines[j])) {
                    picked.Add(lines[j]);
                }
            }
        }

        return picked;
    }

    /// <summary>Whether the quest's people speak of a puzzle.</summary>
    internal static bool IsPuzzle(QuestTemplate quest) => PuzzleLines(quest).Count > 0;

    /// <summary>What kind of puzzle the quest speaks of, in plain words ("" when none).</summary>
    internal static string Kind(QuestTemplate quest) {
        var text = string.Join(" ", PuzzleLines(quest));
        if (text.Length == 0) {
            return "";
        }

        if (Regex.IsMatch(text, @"\briddles?\b", RegexOptions.IgnoreCase)) {
            return "a riddle";
        }

        if (Regex.IsMatch(text, @"\bmini-?games?\b", RegexOptions.IgnoreCase)) {
            return "a mini-game";
        }

        return Regex.IsMatch(text, @"\b(order|combination)\b", RegexOptions.IgnoreCase)
            ? "things to use in the right order"
            : "a puzzle";
    }

    // -----------------------------------------------------------------
    // Click orders
    // -----------------------------------------------------------------

    /// <summary>
    /// The things in the area a puzzle's lines name, by their name in the game ("Sand Thumper"), for a step that names no
    /// thing to use itself (the Great Wurm comes once the Sand Thumpers are used in the right order). None when the lines
    /// name nothing that stands here.
    /// </summary>
    internal static List<(float X, float Y)> NamedThings(string zone, QuestTemplate quest, IEnumerable<ulong> notThese) {
        var text = string.Join(" ", PuzzleLines(quest)).ToLowerInvariant();
        if (text.Length == 0 || string.IsNullOrEmpty(zone)) {
            return [];
        }

        var skip = new HashSet<ulong>(notThese ?? []);
        var found = new List<(float X, float Y)>();
        try {
            foreach (var group in QuestStepSpots.Zone(zone).Objects.GroupBy(o => o.Template)) {
                if (skip.Contains(group.Key) || ThingName(group.Key) is not { Length: >= 4 } name || !Names(text, name)) {
                    continue;
                }

                found.AddRange(group.Select(o => (o.Spot.X, o.Spot.Y)));
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bot puzzles: the things of {0} could not be read: {1}", Logger.Args(zone, ex.Message));
        }

        // Ten or more of one name is the room's decoration, not a puzzle.
        return found.Count is >= 2 and <= 9 ? found : [];
    }

    /// <summary>
    /// For a creature step whose creature is nowhere in the area and whose quest speaks of a puzzle: the next of the things
    /// the puzzle's lines name to use, in order (the Great Wurm of "Cavern of the Wurm" comes once the Sand Thumpers are used
    /// in the right order). Null when there is no such thing here.
    /// </summary>
    internal static (float X, float Y)? ForCreatureStep(string zone, QuestTemplate quest, GoalTemplate goal, IReadOnlyList<ulong> holders,
        float fromX, float fromY, IEnumerable<ulong> notThese) {
        if (quest is null || goal is null || !IsPuzzle(quest)) {
            return null;
        }

        var things = NamedThings(zone, quest, notThese);

        return things.Count >= 2 ? Next(zone, quest, goal, things, holders, fromX, fromY) : null;
    }

    /// <summary>
    /// The next thing to use for a puzzle step, in the order worked out for it: the order that worked before, else the order
    /// the quest's lines name them in, else nearest first. When every one was used and nothing happened, the next order is
    /// tried, as a player tries again. Null when the step is no puzzle (the usual nearest-first picking goes on).
    /// </summary>
    internal static (float X, float Y)? Next(string zone, QuestTemplate quest, GoalTemplate goal, IReadOnlyList<(float X, float Y)> spots,
        IReadOnlyList<ulong> holders, float fromX, float fromY) {
        if (quest is null || goal is null || spots is null || spots.Count < 2 || holders is null || holders.Count == 0 || !IsPuzzle(quest)) {
            return null;
        }

        string questName = quest.m_questName;
        string goalName = goal.m_goalName;
        var key = questName + "|" + goalName;
        var order = Order(zone, quest, key, spots, fromX, fromY);
        bool Open((float X, float Y) s) => holders.Any(h => !BotCollect.HasDone(h, questName, goalName, s.X, s.Y));
        if (order.Any(Open)) {
            return order.First(Open);
        }

        // All used and the step is still open: a moment for something to happen, then the next order.
        if (s_lastClick.TryGetValue(key, out var last) && DateTime.UtcNow - last < s_settle) {
            return order[^1];
        }

        var tries = s_try.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (tries >= MOST_TRIES || tries >= Factorial(order.Count)) {
            return null;
        }

        foreach (var holder in holders) {
            BotCollect.ForgetStep(holder, questName, goalName);
            s_clicked.TryRemove(holder + "|" + key, out _);
            s_news.Enqueue((holder, "puzzle-retry|" + key + "|" + tries,
                $"I used all {order.Count} of them for \"{OverlayQuestTracker.StepTitle(goal)}\" and nothing happened, so the order must be wrong. " +
                $"I try another order, as a player would (try {tries + 1})."));
        }

        s_lastClick[key] = DateTime.UtcNow;

        return Order(zone, quest, key, spots, fromX, fromY)[0];
    }

    /// <summary>The order the step's things are tried in right now.</summary>
    private static List<(float X, float Y)> Order(string zone, QuestTemplate quest, string key, IReadOnlyList<(float X, float Y)> spots, float fromX, float fromY) {
        // The order that worked before, when its spots all stand here (and it was not just tried for nothing).
        if (!s_try.ContainsKey(key) && Known(key) is { Count: > 0 } known) {
            var mapped = known.Select(k => spots.Where(s => Far(s, k) < SAME_SPOT).OrderBy(s => Far(s, k)).Select(s => ((float X, float Y)?) s).FirstOrDefault())
                .ToList();
            if (mapped.All(m => m is not null)) {
                var list = mapped.Select(m => m!.Value).Distinct().ToList();
                list.AddRange(spots.Where(s => !list.Any(l => Far(l, s) < 1)));

                return list;
            }
        }

        // The first order: the things the quest's lines name first go first, else nearest first. Kept, so each later try
        // is a new order and none is tried twice.
        var first = s_base.GetOrAdd(key, _ => Hinted(zone, quest, spots, fromX, fromY));
        if (first.Count != spots.Count || first.Any(f => !spots.Any(s => Far(s, f) < 1))) {
            first = s_base[key] = Hinted(zone, quest, spots, fromX, fromY);
        }

        return Permutation(first, s_try.TryGetValue(key, out var n) ? n : 0);
    }

    // The things, the ones the quest's lines name first going first (by a word that tells them apart: "Fire", "Ice"), the
    // rest nearest first.
    private static List<(float X, float Y)> Hinted(string zone, QuestTemplate quest, IReadOnlyList<(float X, float Y)> spots, float fromX, float fromY) {
        var text = string.Join(" ", Lines(quest)).ToLowerInvariant();
        var names = spots.Select(s => ThingAt(zone, s)).ToList();
        var shared = names.Where(n => n.Length > 0).Select(n => Words(n).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((HashSet<string>) null, (all, w) => all is null ? w : [.. all.Intersect(w, StringComparer.OrdinalIgnoreCase)]) ?? [];
        int Mention(int i) {
            var own = Words(names[i]).Where(w => !shared.Contains(w)).ToList();
            var at = own.Select(w => Regex.Match(text, $@"\b{Regex.Escape(w.ToLowerInvariant())}\b")).Where(m => m.Success).Select(m => m.Index).DefaultIfEmpty(int.MaxValue).Min();

            return at;
        }

        return [.. spots.Select((s, i) => (s, at: Mention(i))).OrderBy(x => x.at).ThenBy(x => Far(x.s, (fromX, fromY))).Select(x => x.s)];
    }

    // The n-th order of the things (0 is the first order itself), each a new one.
    private static List<(float X, float Y)> Permutation(List<(float X, float Y)> first, int n) {
        var pool = first.ToList();
        var result = new List<(float X, float Y)>();
        n %= Math.Max(1, Factorial(pool.Count));
        for (var size = pool.Count; size > 0; size--) {
            var block = Factorial(size - 1);
            var pick = n / block;
            n %= block;
            result.Add(pool[pick]);
            pool.RemoveAt(pick);
        }

        return result;
    }

    private static int Factorial(int n) {
        var f = 1;
        for (var i = 2; i <= Math.Min(n, 10); i++) {
            f *= i;
        }

        return f;
    }

    /// <summary>A wizard used the thing at (x, y) for a step (BotCollect.Used).</summary>
    internal static void Clicked(ulong charId, string quest, string goal, float x, float y) {
        var key = quest + "|" + goal;
        s_lastClick[key] = DateTime.UtcNow;
        var list = s_clicked.GetOrAdd(charId + "|" + key, _ => []);
        lock (list) {
            if (!list.Any(s => Far(s, (x, y)) < SAME_SPOT)) {
                list.Add((x, y));
            }
        }
    }

    /// <summary>A wizard picked an NPC's answer for a step (BotOptions.Pick).</summary>
    internal static void Chose(ulong charId, string quest, string goal, string answer) {
        if (charId != 0 && !string.IsNullOrEmpty(quest) && !string.IsNullOrEmpty(answer)) {
            s_chose[$"{charId}|{quest}|{goal ?? ""}"] = answer;
        }
    }

    /// <summary>
    /// A wizard finished a step: for a puzzle, the order it used the things in, or the answer it picked, worked and is
    /// written down for next time.
    /// </summary>
    internal static void StepDone(ulong charId, string quest, string goal) {
        var key = quest + "|" + goal;
        var answerPrefix = charId + "|" + quest + "|";
        if (!s_clicked.ContainsKey(charId + "|" + key) && !s_chose.Keys.Any(k => k.StartsWith(answerPrefix, StringComparison.OrdinalIgnoreCase))) {
            return;
        }

        var q = WizardData.Collections.QuestTemplateCollection.GetQuestByName(quest);
        if (s_clicked.TryRemove(charId + "|" + key, out var clicked) && clicked.Count >= 2 && q is not null && IsPuzzle(q)) {
            lock (s_lock) {
                var book = Load();
                var had = book.Orders.ContainsKey(key);
                book.Orders[key] = [.. clicked.Select(c => new[] { c.X, c.Y })];
                Save(book);
                if (!had) {
                    s_news.Enqueue((charId, "puzzle-learned|" + key,
                        $"That was the right order for \"{Title(q, goal)}\". I wrote it down, so the next time it is done right away."));
                }
            }

            s_try.TryRemove(key, out _);
            s_base.TryRemove(key, out _);
        }

        // The answer: any wizard's answer for this quest (an answer step may be the step before the one that finishes).
        foreach (var chose in s_chose.Where(c => c.Key.StartsWith(charId + "|" + quest + "|", StringComparison.OrdinalIgnoreCase)).ToList()) {
            s_chose.TryRemove(chose.Key, out _);
            if (q is null || !IsPuzzle(q)) {
                continue;
            }

            var answerKey = chose.Key[(chose.Key.IndexOf('|') + 1)..];
            lock (s_lock) {
                var book = Load();
                if (!book.Answers.TryGetValue(answerKey, out var had) || had != chose.Value) {
                    book.Answers[answerKey] = chose.Value;
                    Save(book);
                    s_news.Enqueue((charId, "puzzle-answer|" + answerKey,
                        $"\"{English(chose.Value)}\" was the right answer for {Title(q, goal)}. I wrote it down for next time."));
                }
            }
        }
    }

    /// <summary>A finished quest: its puzzle steps are done (the last step leaves no step behind to see finish).</summary>
    internal static void Settle(ulong charId, Wizard wizard) {
        if (wizard is null) {
            return;
        }

        var prefix = charId + "|";
        foreach (var key in s_clicked.Keys.Concat(s_chose.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Distinct().ToList()) {
            var parts = key.Split('|');
            if (parts.Length >= 3 && wizard.HasCompletedQuest(parts[1])) {
                StepDone(charId, parts[1], parts[2]);
            }
        }
    }

    /// <summary>What the puzzles gave a wizard to say: taken once, by the director whose wizard it is.</summary>
    internal static List<(string Key, string Text)> TakeNews(IEnumerable<ulong> charIds) {
        var mine = new HashSet<ulong>(charIds ?? []);
        var kept = new List<(ulong, string, string)>();
        var taken = new List<(string, string)>();
        while (s_news.TryDequeue(out var item)) {
            if (mine.Contains(item.CharId)) {
                if (!taken.Any(t => t.Item1 == item.Key)) {
                    taken.Add((item.Key, item.Text));
                }
            }
            else {
                kept.Add(item);
            }
        }

        foreach (var item in kept.Take(200)) {
            s_news.Enqueue(item);
        }

        return taken;
    }

    // -----------------------------------------------------------------
    // Answers
    // -----------------------------------------------------------------

    /// <summary>
    /// Among an NPC's answers, the one to pick for a puzzle step: the one that worked before, else the ones that share the
    /// most words with what the quest's people said first. The order the answers are tried in; null when the quest has no puzzle.
    /// </summary>
    internal static List<T> RankAnswers<T>(string questName, string goalName, List<T> answers, Func<T, string> display) {
        if (string.IsNullOrEmpty(questName) || answers is null || answers.Count < 2
            || WizardData.Collections.QuestTemplateCollection.GetQuestByName(questName) is not { } quest || !IsPuzzle(quest)) {
            return null;
        }

        var known = KnownAnswer(questName + "|" + (goalName ?? ""));
        var hints = Words(string.Join(" ", PuzzleLines(quest).Concat(Lines(quest)))).Where(w => !s_common.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Score(T answer) {
            var text = English(display(answer));
            if (known is not null && (string.Equals(display(answer), known, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(text, English(known), StringComparison.OrdinalIgnoreCase))) {
                return 1000;
            }

            return Words(text).Where(w => !s_common.Contains(w)).Count(hints.Contains);
        }

        return [.. answers.Select((a, i) => (a, i, s: Score(a))).OrderByDescending(x => x.s).ThenBy(x => x.i).Select(x => x.a)];
    }

    // -----------------------------------------------------------------
    // Reading ahead
    // -----------------------------------------------------------------

    /// <summary>The puzzle quests among <paramref name="quests"/>, with what kind of puzzle each is and whether it was solved before.</summary>
    internal static List<(string Quest, string Kind, bool Known)> Ahead(IEnumerable<QuestTemplate> quests) {
        var found = new List<(string, string, bool)>();
        foreach (var quest in quests ?? []) {
            if (quest is null || Kind(quest) is not { Length: > 0 } kind) {
                continue;
            }

            string name = quest.m_questName;
            var book = BookCopy();
            var known = book.Orders.Keys.Concat(book.Answers.Keys).Any(k => k.StartsWith(name + "|", StringComparison.OrdinalIgnoreCase));
            found.Add((name, kind, known));
        }

        return found;
    }

    // -----------------------------------------------------------------
    // The written-down book
    // -----------------------------------------------------------------

    private static List<float[]> Known(string key) {
        var book = BookCopy();

        return book.Orders.TryGetValue(key, out var order) ? order.Where(o => o is { Length: 2 }).ToList() : null;
    }

    private static string KnownAnswer(string key) => BookCopy().Answers.GetValueOrDefault(key);

    private static Book BookCopy() {
        lock (s_lock) {
            return Load();
        }
    }

    private static Book Load() {
        if (s_book is not null) {
            return s_book;
        }

        try {
            if (File.Exists(FilePath)) {
                s_book = JsonSerializer.Deserialize<Book>(File.ReadAllText(FilePath)) ?? new Book();
                s_book.Orders = new Dictionary<string, List<float[]>>(s_book.Orders ?? [], StringComparer.OrdinalIgnoreCase);
                s_book.Answers = new Dictionary<string, string>(s_book.Answers ?? [], StringComparer.OrdinalIgnoreCase);

                return s_book;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Debug("Bot puzzles: {0} could not be read: {1}", Logger.Args(FilePath, ex.Message));
        }

        return s_book = new Book();
    }

    private static void Save(Book book) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(book, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Bot puzzles: {0} could not be written: {1}", Logger.Args(FilePath, ex.Message));
        }
    }

    // -----------------------------------------------------------------
    // Words
    // -----------------------------------------------------------------

    private static string Title(QuestTemplate quest, string goal) {
        var step = (quest?.m_goals ?? []).FirstOrDefault(g => g is not null && string.Equals((string) g.m_goalName, goal, StringComparison.Ordinal));

        return step is null ? English(quest?.m_questTitle) : OverlayQuestTracker.StepTitle(step);
    }

    private static readonly ConcurrentDictionary<ulong, string> s_thingNames = new();

    private static string ThingName(ulong template) => s_thingNames.GetOrAdd(template, id => {
        try {
            if (CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate t) {
                string key = t.m_displayName;
                var name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

                return string.IsNullOrWhiteSpace(name) || name.Contains('_') ? "" : name.Trim();
            }
        }
        catch (Exception) {
            // No name.
        }

        return "";
    });

    // The name of the thing standing at a spot ("" when none is known).
    private static string ThingAt(string zone, (float X, float Y) spot) {
        try {
            var near = QuestStepSpots.Zone(zone ?? "").Objects.Where(o => Far((o.Spot.X, o.Spot.Y), spot) < 40)
                .OrderBy(o => Far((o.Spot.X, o.Spot.Y), spot)).Select(o => ThingName(o.Template)).FirstOrDefault(n => n.Length > 0);

            return near ?? "";
        }
        catch (Exception) {
            return "";
        }
    }

    // Whether the text names the thing (its name, or its name without a plural "s" in the text: "Sand Thumpers").
    private static bool Names(string lowerText, string name) {
        var n = name.ToLowerInvariant();

        return Regex.IsMatch(lowerText, $@"\b{Regex.Escape(n)}(s|es)?\b");
    }

    private static IEnumerable<string> Words(string text) => s_words.Matches(text ?? "").Select(m => m.Value);

    private static string English(string keyOrText) {
        if (string.IsNullOrEmpty(keyOrText)) {
            return "";
        }

        var english = keyOrText.Contains(' ') ? null : Locale.GetEnglishName(keyOrText);
        var text = string.IsNullOrEmpty(english) ? keyOrText : english;

        return s_tags.Replace(text, "").Replace('|', ',').Trim();
    }

    private static float Far((float X, float Y) a, (float X, float Y) b)
        => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static float Far((float X, float Y) a, float[] b) => Far(a, (b[0], b[1]));

}
