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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' shared brain (Niduus, 2026-11-14, "All" to: "Ultimus, the Quest Team and Bot Players all write to one
/// knowledge base, so a lesson learned by any bot helps all of them"). Two things are kept:
/// <list type="bullet">
/// <item>Each creature's habits, seen in every battle a server-run wizard (Ultimus, the Quest Team, a Bot Player) is in:
/// what it casts on which round (hit, heal, shield, blade, trap), the schools it hits with and how much of a bot's health a
/// hit takes. A bot reads them before and during a fight and plays round them ("it shields on round 2", "its hit takes a
/// third of my health").</item>
/// <item>Lessons in words, from any bot: what surprised it, what worked, what it learned today.</item>
/// </list>
/// Kept in logs/bots/knowledge.json, with an easy-to-read copy in logs/bots/knowledge.txt (both survive restarts).
/// The mistakes book (BotMistakes) and the walks that work (RouteMemory) are the rest of the shared brain.
/// </summary>
internal static class BotKnowledge {

    private const string FILE = "knowledge.json";
    private const string READABLE = "knowledge.txt";
    private const int MOST_LESSONS = 400;

    internal const string HIT = "hit";
    internal const string HEAL = "heal";
    internal const string SHIELD = "shield";
    internal const string BLADE = "blade";
    internal const string TRAP = "trap";
    internal const string OTHER = "other";
    private static readonly string[] s_kinds = [HIT, HEAL, SHIELD, BLADE, TRAP, OTHER];

    internal sealed class Habit {
        public uint Template { get; set; }
        public string Name { get; set; } = "";
        public int Fights { get; set; }
        public int Casts { get; set; }
        // Round (1, 2, 3, 4 and 5 for "5 and later") -> kind -> times.
        public Dictionary<int, Dictionary<string, int>> ByRound { get; set; } = [];
        public Dictionary<string, int> Schools { get; set; } = [];
        public int HitsOnBots { get; set; }
        public double HitShareSum { get; set; }
        public double BiggestHitShare { get; set; }
        public DateTime Last { get; set; }

        internal int Times(string kind) => ByRound.Values.Sum(r => r.GetValueOrDefault(kind));

        internal double Chance(string kind) => Casts == 0 ? 0 : Times(kind) / (double) Casts;

        /// <summary>How likely it casts this kind on this round: that round's own count when seen at least twice.</summary>
        internal double ChanceOn(string kind, int round) {
            var at = Math.Clamp(round, 1, 5);
            if (ByRound.TryGetValue(at, out var counts) && counts.Values.Sum() >= 2) {
                return counts.GetValueOrDefault(kind) / (double) counts.Values.Sum();
            }

            return Chance(kind);
        }

        internal double AverageHitShare => HitsOnBots == 0 ? 0 : HitShareSum / HitsOnBots;

        /// <summary>About how much of a bot's health it takes on this round (its chance to hit times its average hit).</summary>
        internal double ExpectedHitOn(int round) => ChanceOn(HIT, round) * AverageHitShare;

        /// <summary>The round it most likely shields or heals on, for a habit worth playing round (0 for none).</summary>
        internal int HabitRound(string kind) {
            var best = ByRound.Where(r => r.Value.Values.Sum() >= 2)
                .Select(r => (Round: r.Key, Share: r.Value.GetValueOrDefault(kind) / (double) r.Value.Values.Sum()))
                .Where(x => x.Share >= 0.5).OrderByDescending(x => x.Share).FirstOrDefault();

            return best.Round;
        }

        internal string Describe() {
            if (Casts == 0) {
                return $"{Name}: nothing seen yet";
            }

            var parts = s_kinds.Where(k => Times(k) > 0).Select(k => $"{k}s {Chance(k):P0}").ToList();
            var text = $"{Name}: {Casts} casts seen in {Fights} fight(s); {string.Join(", ", parts)}";
            foreach (var kind in new[] { SHIELD, HEAL, BLADE, TRAP }) {
                if (HabitRound(kind) is var round and > 0) {
                    text += $"; mostly {kind}s on round {(round >= 5 ? "5+" : round)}";
                }
            }

            if (HitsOnBots > 0) {
                text += $"; a hit takes about {AverageHitShare:P0} of a bot's health (the biggest {BiggestHitShare:P0})";
            }

            if (Schools.Count > 0) {
                text += $"; hits with {string.Join(", ", Schools.OrderByDescending(s => s.Value).Take(3).Select(s => s.Key))}";
            }

            return text;
        }
    }

    internal sealed class Lesson {
        public string Bot { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Text { get; set; } = "";
        public int Times { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class Book {
        public List<Habit> Habits { get; set; } = [];
        public List<Lesson> Lessons { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static Book s_book;
    private static DateTime s_savedAt = DateTime.MinValue;
    private static bool s_dirty;

    // A creature is counted once per battle.
    private static readonly ConcurrentDictionary<(ulong Duel, uint Template), byte> s_counted = new();

    /// <summary>What the bots know of this creature's habits, or null.</summary>
    internal static Habit Of(uint template) {
        if (template == 0) {
            return null;
        }

        lock (s_lock) {
            return Load().Habits.FirstOrDefault(h => h.Template == template);
        }
    }

    /// <summary>Whether a battle circle is a server-run wizard (Ultimus, a Quest Team wizard or a Bot Player).</summary>
    internal static bool IsServerBot(CombatDuelSubCircle circle) {
        if (circle is null || !circle.IsHumanPlayer || circle.ParticipantObject is not { } who) {
            return false;
        }

        var gid = who.m_globalID.Full;
        var charId = circle._wizard?.CharId ?? 0;

        return BotPlayers.BotIds.IsBotObject(gid) || Ultimus.UltimusDirector.RoleOf(gid) is not null
               || QuestTeam.QuestTeamRoster.IsMember(charId) || Ultimus.UltimusRoster.IsMember(charId);
    }

    /// <summary>
    /// Before a creature's move in a battle a bot is in: the bot it aims at and that bot's health, to see what the move did.
    /// Null when there is nothing to watch.
    /// </summary>
    internal static (CombatDuelSubCircle Bot, int Health)? Before(QueuedCombatAction action, IEnumerable<CombatDuelSubCircle> circles) {
        try {
            if (action?.SpellCaster is not { } caster || caster.IsHumanPlayer || action.Spell is null) {
                return null;
            }

            var bot = action.SelectedTarget is { } target && IsServerBot(target) ? target
                : circles.FirstOrDefault(c => c.OccupiedTeam != caster.OccupiedTeam && IsServerBot(c));

            return bot is null ? null : (bot, RewrittenAI.Health(bot));
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>After that move: what the creature cast, on which round, and how much of the bot's health it took.</summary>
    internal static void After(QueuedCombatAction action, (CombatDuelSubCircle Bot, int Health)? watched, ulong duelId, int round) {
        if (watched is not { } watch || action?.SpellCaster is not { } caster || caster.ParticipantObject is not { } creature) {
            return;
        }

        try {
            var card = RewrittenAI.Read(action.Spell);
            if (card is null) {
                return;
            }

            var kind = card.Hit ? HIT : card.Heal ? HEAL : card.Shield ? SHIELD : card.Blade ? BLADE : card.Trap ? TRAP : OTHER;
            var hitBot = card.Hit && (action.SelectedTarget == watch.Bot || card.HitsAll);
            var share = hitBot ? Math.Max(0, watch.Health - RewrittenAI.Health(watch.Bot)) / (double) RewrittenAI.MostHealth(watch.Bot) : 0;
            var template = (uint) Math.Min((ulong) creature.m_templateID, uint.MaxValue);
            Saw(template, CombatEffectApplicator.TargetName(caster), duelId, round, kind, card.Hit ? card.DamageSchool : "", hitBot, share);
        }
        catch (Exception e) {
            Logger.Debug("Bots: a creature's move could not be learned: {0}", Logger.Args(e.Message));
        }
    }

    private static void Saw(uint template, string name, ulong duelId, int round, string kind, string school, bool hitBot, double share) {
        if (template == 0) {
            return;
        }

        lock (s_lock) {
            var book = Load();
            var habit = book.Habits.FirstOrDefault(h => h.Template == template);
            if (habit is null) {
                book.Habits.Add(habit = new Habit { Template = template });
            }

            if (!string.IsNullOrWhiteSpace(name)) {
                habit.Name = name;
            }

            if (s_counted.TryAdd((duelId, template), 0)) {
                habit.Fights++;
                if (s_counted.Count > 5000) {
                    s_counted.Clear();
                }
            }

            var at = Math.Clamp(round, 1, 5);
            if (!habit.ByRound.TryGetValue(at, out var counts)) {
                habit.ByRound[at] = counts = [];
            }

            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            habit.Casts++;
            if (!string.IsNullOrEmpty(school)) {
                habit.Schools[school] = habit.Schools.GetValueOrDefault(school) + 1;
            }

            if (hitBot) {
                habit.HitsOnBots++;
                habit.HitShareSum += Math.Clamp(share, 0, 1);
                habit.BiggestHitShare = Math.Max(habit.BiggestHitShare, Math.Clamp(share, 0, 1));
            }

            habit.Last = DateTime.UtcNow;
            SaveSoon();
        }
    }

    /// <summary>A lesson in words, from any bot (counted again when the same lesson comes up).</summary>
    internal static void Learn(string bot, string topic, string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        lock (s_lock) {
            var book = Load();
            var lesson = book.Lessons.FirstOrDefault(l => l.Topic == (topic ?? "") && l.Text == text);
            if (lesson is null) {
                book.Lessons.Add(lesson = new Lesson { Bot = bot ?? "", Topic = topic ?? "", Text = text, First = DateTime.UtcNow });
                if (book.Lessons.Count > MOST_LESSONS) {
                    book.Lessons.Remove(book.Lessons.OrderBy(l => l.Last).First());
                }
            }

            lesson.Times++;
            lesson.Last = DateTime.UtcNow;
            s_dirty = true;
            Save();
        }
    }

    /// <summary>The newest lessons on a topic (any topic when null).</summary>
    internal static List<Lesson> Lessons(string topic = null, int most = 10) {
        lock (s_lock) {
            return [.. Load().Lessons.Where(l => topic is null || l.Topic == topic).OrderByDescending(l => l.Last).Take(most)];
        }
    }

    /// <summary>The lessons of the last day, for the daily review.</summary>
    internal static List<Lesson> Since(DateTime since) {
        lock (s_lock) {
            return [.. Load().Lessons.Where(l => l.Last >= since).OrderByDescending(l => l.Last)];
        }
    }

    private static Book Load() {
        if (s_book is not null) {
            return s_book;
        }

        s_book = new Book();
        try {
            var path = Path.Combine(Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<Book>(File.ReadAllText(path)) is { } read) {
                s_book.Habits = [.. (read.Habits ?? []).Where(h => h is not null && h.Template != 0).Select(h => {
                    h.Name ??= "";
                    h.ByRound ??= [];
                    h.Schools ??= [];
                    return h;
                })];
                s_book.Lessons = [.. (read.Lessons ?? []).Where(l => l is not null).Select(l => {
                    l.Bot ??= "";
                    l.Topic ??= "";
                    l.Text ??= "";
                    return l;
                })];
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bots: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_book;
    }

    private static string Folder => Path.GetFullPath(BotMistakes.FOLDER);

    // A battle sees many moves: written at most every 10 seconds.
    private static void SaveSoon() {
        s_dirty = true;
        if (DateTime.UtcNow - s_savedAt > TimeSpan.FromSeconds(10)) {
            Save();
        }
    }

    private static void Save() {
        if (!s_dirty) {
            return;
        }

        s_dirty = false;
        s_savedAt = DateTime.UtcNow;
        try {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(s_book, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
            File.WriteAllText(Path.Combine(Folder, READABLE), Readable());
        }
        catch (Exception ex) {
            Logger.Debug("Bots: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static string Readable() {
        var text = new StringBuilder();
        text.AppendLine("The bots' shared brain: what Ultimus, the Quest Team and the Bot Players learned. (logs/bots/knowledge.json holds the same.)");
        text.AppendLine();
        text.AppendLine("LESSONS, newest first");
        foreach (var lesson in s_book.Lessons.OrderByDescending(l => l.Last)) {
            text.AppendLine($"  [{lesson.Last.ToLocalTime():yyyy-MM-dd HH:mm}] {lesson.Topic} x{lesson.Times} ({lesson.Bot}): {lesson.Text}");
        }

        text.AppendLine();
        text.AppendLine("CREATURE HABITS, most seen first");
        foreach (var habit in s_book.Habits.OrderByDescending(h => h.Casts)) {
            text.AppendLine($"  {habit.Describe()}");
        }

        return text.ToString();
    }

}
