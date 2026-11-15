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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.World;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What Ultimus learns from his AI answers (Niduus, 2026-11-15: "I want him to learn from his AI answers so we can be more
/// accurate in an offline setting. I want everything wiz related, even what other models have trained off of"). Each AI
/// answer to a player's Wizard101 question (spells, schools, worlds, gear, creatures, people, where to get things) is kept in
/// logs\ultimus\ai-learned.json, without what was only true for that moment (his quest, his place, his mood, the player's
/// name), and only once the safety check passed it. His normal chat then answers the same question from it when the AI is
/// off, paused, used up or can't be reached. Every answer is checked by the safety check again before it is said, so a
/// private detail added later is never repeated. Ahziael's Ultimus AI Chat page lists them and removes any.
/// [Ultimus] AILearn turns learning on or off, [Ultimus] AILearnedChat the use in normal chat; both apply at once.
/// </summary>
internal static class UltimusAiLearned {

    internal const string FILE = "ai-learned.json";
    private const int MAX = 2000;

    internal sealed class Entry {
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public string By { get; set; } = "";
        /// <summary>For a leader command: the command the line meant (Answer is then empty).</summary>
        public string Command { get; set; } = "";
        public DateTime At { get; set; }
        public int Used { get; set; }
    }

    internal static bool Learning => ConfigurationManager.GetValue("Ultimus.AILearn", true);
    internal static bool Answering => ConfigurationManager.GetValue("Ultimus.AILearnedChat", true);

    // Wizard101 words a game question carries (beside the game's own area names and the NPC book's names).
    private static readonly string[] s_wizWords = [
        "wizard101", "wizard", "spell", "spells", "school", "fire", "ice", "storm", "myth", "life", "death", "balance", "shadow", "sun", "moon",
        "star", "astral", "pip", "pips", "power pip", "shadow pip", "card", "cards", "treasure card", "deck", "wand", "hat", "robe", "boots",
        "athame", "amulet", "ring", "mount", "pet", "pets", "talent", "jewel", "socket", "gear", "crown", "crowns", "gold", "reagent",
        "crafting", "recipe", "housing", "castle", "garden", "gardening", "fishing", "fish", "trainer", "training point", "quest", "quests",
        "boss", "bosses", "creature", "minion", "dungeon", "instance", "sigil", "spiral", "world", "worlds", "area", "level", "levels",
        "health", "mana", "accuracy", "pierce", "resist", "critical", "block", "shield", "blade", "trap", "charm", "ward", "aura", "global",
        "enchantment", "polymorph", "shadow magic", "archmastery", "pvp", "arena", "derby", "badge", "elixir", "potion", "wisp", "seed",
        "headmaster", "ambrose", "malistaire", "ravenwood", "commons", "unicorn way", "triton avenue", "firecat alley", "golem court",
        "krokotopia", "marleybone", "mooshu", "dragonspyre", "celestia", "zafaria", "avalon", "azteca", "khrysalis", "polaris", "mirage",
        "empyrea", "karamelle", "lemuria", "novus", "wallaru", "selenopolis", "darkmoor", "grizzleheim", "wintertusk", "wysteria", "aquila",
        "arcanum", "tower shield", "satyr", "feint", "sprite", "pixie", "fairy", "unicorn", "scarab", "scorpion", "locust", "hydra",
        "judgement", "earthquake", "sandstorm", "helping hands", "mander", "minotaur", "kraken", "phoenix", "frost giant",
    ];

    // Words of a question that say nothing about what is asked (kept apart: the kind of question is compared on its own).
    private static readonly HashSet<string> s_stop = new(StringComparer.OrdinalIgnoreCase) {
        "a", "an", "the", "is", "are", "was", "were", "be", "do", "does", "did", "i", "you", "u", "me", "my", "your", "we", "it", "its", "of",
        "to", "in", "on", "at", "for", "from", "with", "and", "or", "can", "could", "should", "would", "will", "get", "got", "any", "some",
        "there", "this", "that", "ultimus", "hey", "hi", "pls", "please", "plz", "know", "tell", "about", "good", "best", "really", "so",
        "what", "where", "how", "who", "when", "why", "which", "whats", "wheres", "hows",
    };

    private static readonly string[] s_kinds = ["where", "how", "who", "when", "why", "which", "what"];

    // A sentence that was only true for that moment, or an answer that holds nothing.
    private static readonly string[] s_moment = [
        "currently", "right now", "at the moment", "i am working", "i'm working", "i am questing", "i'm questing", "my quest", "i am on",
        "i'm on", "i am in", "i'm in", "i am at", "i'm at", "my mood", "my health", "i feel", "i am feeling", "i'm feeling", "today",
        "just now", "my group", "your group", "with you", "i will", "i'll", "let me", "follow", "my level", "i am level", "i'm level",
    ];

    private static readonly string[] s_me = ["i", "i'm", "im", "me", "my", "mine", "myself"];

    private static readonly string[] s_empty = [
        "not sure", "don't know", "dont know", "do not know", "can't help", "cannot help", "can not help", "no idea", "can't say",
        "cannot say", "can't share", "cannot share", "i'm a bot", "i am a bot", "sorry",
    ];

    private static readonly object s_lock = new();
    private static List<Entry> s_entries = [];
    private static DateTime s_readAt = DateTime.MinValue;

    private static string Path_ => Path.Combine(UltimusWire.Folder, FILE);

    /// <summary>Every learned answer (read again when Ahziael changed the file).</summary>
    internal static List<Entry> All() {
        lock (s_lock) {
            try {
                var info = new FileInfo(Path_);
                if (!info.Exists) {
                    s_entries = [];
                    s_readAt = DateTime.MinValue;
                }
                else if (info.LastWriteTimeUtc != s_readAt) {
                    s_entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path_)) ?? [];
                    s_readAt = info.LastWriteTimeUtc;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
                // Kept as it was; read again next time.
            }

            return s_entries;
        }
    }

    /// <summary>
    /// Keeps what a player's Wizard101 question taught: the AI's answer without the parts that were only true for that moment.
    /// <paramref name="moment"/> holds the words that were only true then (his quest, his area and world); <paramref name="asker"/> is the player.
    /// Nothing is kept for a trick, a private detail, an empty answer or a question that isn't about the game.
    /// </summary>
    internal static void Learn(string question, string answer, string by, string asker, IEnumerable<string> moment) {
        if (!Learning || Keepable(question, answer, asker, moment) is not { } safe) {
            return;
        }

        lock (s_lock) {
            var entries = All().ToList();
            var words = Words(question);
            entries.RemoveAll(e => e.Command.Length == 0 && Same(Words(e.Question), words) && Kind(e.Question) == Kind(question));
            entries.Add(new Entry { Question = question.Trim(), Answer = safe.Trim(), By = by ?? "", At = DateTime.UtcNow });
            if (entries.Count > MAX) {
                entries = [.. entries.OrderByDescending(e => e.Used).ThenByDescending(e => e.At).Take(MAX)];
            }

            Save(entries);
        }

        Logger.Information("Ultimus: learned from the {0}: \"{1}\" ({2} learned answers).", Logger.Args(by, UltimusText.Short(safe, 80), All().Count));
    }

    /// <summary>What of the answer would be learned (through the safety check), or null. Public to the self-test.</summary>
    internal static string Keepable(string question, string answer, string asker, IEnumerable<string> moment) {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer) || BotAiGuard.LooksLikeTrick(question)
            || BotAiGuard.Incoming(question) is null || !question.Contains('?') && !s_kinds.Any(k => BotSocialWords.Starts(question, k))) {
            return null;
        }

        if (!AboutTheGame(question)) {
            return null;
        }

        // A world or area the question itself names stays (asked "where is Krokotopia?" while he stands in it).
        var momentWords = (moment ?? []).Where(w => !string.IsNullOrWhiteSpace(w) && w.Trim().Length >= 3).Select(w => w.Trim())
            .Where(w => !MenuChatBook.Says(question, w)).ToList();
        // A sentence about himself that names his quest, place or step was only true then; the same names in a plain game fact
        // stay ("Tower Shield is taught in Marleybone"). The player's name never stays.
        var kept = Sentences(answer).Where(s => !s_moment.Any(m => MenuChatBook.Says(s, m))
                                                && !(momentWords.Any(w => MenuChatBook.Says(s, w)) && s_me.Any(m => MenuChatBook.Says(s, m)))
                                                && !(asker is { Length: >= 2 } && s.Contains(asker, StringComparison.OrdinalIgnoreCase))).ToList();
        var text = string.Join(" ", kept).Trim();
        if (text.Length < 12 || s_empty.Any(e => text.Contains(e, StringComparison.OrdinalIgnoreCase)) || !AboutTheGame(text + " " + question)) {
            return null;
        }

        // The safety check once more on what is kept: nothing private is ever learned.
        if (BotAiGuard.Incoming(text, out _) is not { } safe || !ReferenceEquals(BotSocial.Scrub(safe), safe)) {
            return null;
        }

        return safe.Trim();
    }

    /// <summary>A learned answer to this question, already through the safety check again, or null.</summary>
    internal static string Find(string question) {
        if (!Answering || string.IsNullOrWhiteSpace(question) || BotAiGuard.LooksLikeTrick(question) || !AboutTheGame(question)) {
            return null;
        }

        var words = Words(question);
        if (words.Count == 0) {
            return null;
        }

        var kind = Kind(question);
        lock (s_lock) {
            var entries = All();
            var best = entries.Where(e => e.Command.Length == 0 && e.Answer.Length > 0).Where(e => Kind(e.Question) is var k && (k.Length == 0 || kind.Length == 0 || k == kind))
                .Select(e => (Entry: e, Score: Score(Words(e.Question), words)))
                .Where(x => x.Score >= 0.75)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.At).Select(x => x.Entry).FirstOrDefault();
            if (best is null || BotAiGuard.Incoming(best.Answer) is not { } safe) {
                return null;
            }

            best.Used++;
            Save(entries);

            return safe;
        }
    }

    // ---------------------------------------------------------------------
    // Leader commands ("Let me go first", "Stay here", "Cast X on Y"): how a player worded one
    // ---------------------------------------------------------------------

    /// <summary>
    /// Which of his leader commands the line means: a wording learned before (said the same way, or nearly), else the AI's
    /// pick from the list, which is then kept so his offline chat understands it next time. -1 when it is none of them or
    /// nobody can tell. The AI only ever picks from <paramref name="commands"/>; nothing it writes is said or done.
    /// </summary>
    internal static async System.Threading.Tasks.Task<int> UnderstandAsync(string line, IReadOnlyList<string> commands) {
        if (string.IsNullOrWhiteSpace(line) || commands is not { Count: > 0 } || BotAiGuard.LooksLikeTrick(line) || BotAiGuard.Incoming(line) is null) {
            return -1;
        }

        if (CommandFor(line, commands) is var known and >= 0) {
            return known;
        }

        var picked = await UltimusLocalAI.Meaning(line, commands).ConfigureAwait(false);
        if (picked >= 0) {
            LearnCommand(line, commands[picked], UltimusLocalAI.Who);
        }

        return picked;
    }

    /// <summary>A learned wording of one of these commands (the same words, or nearly), or -1. Works offline.</summary>
    internal static int CommandFor(string line, IReadOnlyList<string> commands) {
        if (!Answering || string.IsNullOrWhiteSpace(line) || commands is not { Count: > 0 }) {
            return -1;
        }

        var words = Words(line);
        if (words.Count == 0) {
            return -1;
        }

        lock (s_lock) {
            var entries = All();
            var best = entries.Where(e => e.Command.Length > 0 && commands.Contains(e.Command, StringComparer.OrdinalIgnoreCase))
                .Select(e => (Entry: e, Score: Score(Words(e.Question), words))).Where(x => x.Score >= 0.75)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.At).Select(x => x.Entry).FirstOrDefault();
            if (best is null) {
                return -1;
            }

            best.Used++;
            Save(entries);

            return commands.ToList().FindIndex(c => string.Equals(c, best.Command, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Keeps how a player worded a command (only the wording and the command's name; never a name or a private detail).</summary>
    internal static void LearnCommand(string line, string command, string by) {
        if (!Learning || string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(command) || BotAiGuard.LooksLikeTrick(line)
            || BotAiGuard.Incoming(line) is not { } safe || !ReferenceEquals(BotSocial.Scrub(safe), safe) || safe.Length > 120) {
            return;
        }

        lock (s_lock) {
            var entries = All().ToList();
            var words = Words(safe);
            entries.RemoveAll(e => e.Command.Length > 0 && Same(Words(e.Question), words));
            entries.Add(new Entry { Question = safe.Trim(), Command = command.Trim(), By = by ?? "", At = DateTime.UtcNow });
            if (entries.Count > MAX) {
                entries = [.. entries.OrderByDescending(e => e.Used).ThenByDescending(e => e.At).Take(MAX)];
            }

            Save(entries);
        }

        Logger.Information("Ultimus: learned that \"{0}\" means the command \"{1}\" (from the {2}).", Logger.Args(UltimusText.Short(safe, 60), command, by));
    }

    /// <summary>Removes one learned answer (Ahziael's list does it through the file; this is for the self-test).</summary>
    internal static void Forget(string question) {
        lock (s_lock) {
            var entries = All().ToList();
            if (entries.RemoveAll(e => string.Equals(e.Question, question, StringComparison.OrdinalIgnoreCase)) > 0) {
                Save(entries);
            }
        }
    }

    /// <summary>Whether the text is about Wizard101: a game word, an area of the game or a person of the game.</summary>
    internal static bool AboutTheGame(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        return s_wizWords.Any(w => MenuChatBook.Says(text, w))
               || ZoneNames.All.Any(z => z.Name is { Length: >= 5 } name && MenuChatBook.Says(text, name))
               || UltimusNpcBook.Named(text).Count > 0;
    }

    private static void Save(List<Entry> entries) {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path_, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            s_entries = entries;
            s_readAt = File.GetLastWriteTimeUtc(Path_);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Information("Ultimus: could not save what he learned from the AI ({0}).", Logger.Args(ex.GetType().Name));
        }
    }

    private static IEnumerable<string> Sentences(string text)
        => System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0).Select(s => s.Trim());

    // The words that say what is asked, lower case, without the little words.
    internal static HashSet<string> Words(string text)
        => [.. System.Text.RegularExpressions.Regex.Matches((text ?? "").ToLowerInvariant().Replace("'", ""), "[a-z0-9]+")
            .Select(m => m.Value.Length > 3 && m.Value.EndsWith('s') && !m.Value.EndsWith("ss") ? m.Value[..^1] : m.Value)
            .Where(w => !s_stop.Contains(w))];

    // The kind of question: where, how, who, when, why, which or what ("" when it is none of them).
    private static string Kind(string text)
        => s_kinds.FirstOrDefault(k => MenuChatBook.Says(text ?? "", k)) ?? "";

    private static bool Same(HashSet<string> a, HashSet<string> b) => a.SetEquals(b);

    private static double Score(HashSet<string> a, HashSet<string> b)
        => a.Count == 0 || b.Count == 0 ? 0 : (double) a.Intersect(b).Count() / a.Union(b).Count();

}

/// <summary>Small word helpers for chat lines.</summary>
internal static class BotSocialWords {

    /// <summary>Whether the line starts with the word (case ignored).</summary>
    internal static bool Starts(string text, string word) {
        var t = (text ?? "").TrimStart();
        return t.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (t.Length == word.Length || !char.IsLetter(t[word.Length]));
    }

}
