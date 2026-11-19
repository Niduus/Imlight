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
using System.Text.Json;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How likely each wizard of Ultimus's brain (Ultimus and the TCO7) is to play each card, learned from his own fights
/// (Niduus, 2026-11-18: "I want him to log likely hood of cards based on his usage as he continues to quest. For example,
/// if his likely hood of using cleanse charm is basically 0% and has low usage (like 5 to 0 for example) then he'd disable
/// it"). Every round he counts the cards in his hand and the one he casts; per card he keeps the fights it was in his hand,
/// the fights he cast it in, the rounds in hand and the casts. A card in his hand in <see cref="MIN_FIGHTS"/> or more
/// fights and never cast is "rarely played": an item card is turned off, a treasure card stays in his book, a main deck
/// spell makes way for another. Heals never count as rarely played (an easy fight needs none, a boss does). Every
/// <see cref="RETRY_LEVELS"/> levels such a card gets a new try, its count starting over. Kept in card-use.json and written
/// for Niduus in card-use.txt in each wizard's own folder.
/// </summary>
internal static class UltimusCardUse {

    /// <summary>The fights a card must have been in his hand before "never cast" means anything.</summary>
    internal const int MIN_FIGHTS = 5;

    /// <summary>Levels after which a rarely played card is tried again.</summary>
    internal const int RETRY_LEVELS = 5;

    internal sealed class Use {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Fights { get; set; }
        public int FightsCast { get; set; }
        public int Rounds { get; set; }
        public int Casts { get; set; }
        public int SinceLevel { get; set; }
        public string LastSeen { get; set; } = "";
    }

    private sealed class Book {
        internal readonly object Lock = new();
        internal Dictionary<uint, Use> Cards = [];
        // The round being counted: its fight, round, hand and the card picked last for it (it is counted once over).
        internal ulong Duel;
        internal int Round = -1;
        internal HashSet<uint> Hand = [];
        internal uint Cast;
        internal readonly HashSet<uint> SeenThisFight = [];
        internal readonly HashSet<uint> CastThisFight = [];
        internal int Level;
        internal List<string> Deck = [];
        internal DateTime Written = DateTime.MinValue;
        internal bool Dirty;
    }

    private static readonly ConcurrentDictionary<string, Book> s_books = new(StringComparer.OrdinalIgnoreCase);

    private static Book BookOf(string folder) => s_books.GetOrAdd(folder, f => {
        var book = new Book();
        try {
            var file = Path.Combine(f, "card-use.json");
            if (File.Exists(file)) {
                book.Cards = JsonSerializer.Deserialize<Dictionary<uint, Use>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception) {
            // A broken file starts the counts over; it never stops play.
        }

        return book;
    });

    /// <summary>
    /// A round of his: the cards in his hand and the one he means to cast (0 for a pass). Called at each of his picks in
    /// the round; the last pick of a round is what counts, when the next round (or fight) comes.
    /// </summary>
    internal static void Round(string folder, ulong duel, int round, int level, IEnumerable<uint> hand, uint cast) {
        if (string.IsNullOrEmpty(folder)) {
            return;
        }

        var book = BookOf(folder);
        lock (book.Lock) {
            if (book.Duel != duel || book.Round != round) {
                Commit(book);
                if (book.Duel != duel) {
                    book.SeenThisFight.Clear();
                    book.CastThisFight.Clear();
                }

                book.Duel = duel;
                book.Round = round;
                book.Hand = [.. hand.Where(id => id != 0)];
            }

            book.Cast = cast;
            book.Level = level;
        }

        Save(folder, book, force: false);
    }

    // The round just over goes into the counts.
    private static void Commit(Book book) {
        if (book.Round < 0 || book.Hand.Count == 0) {
            return;
        }

        foreach (var id in book.Hand) {
            if (!book.Cards.TryGetValue(id, out var use)) {
                var card = Template(id);
                use = new Use { Name = Shown(id), Kind = card?.Kind ?? "other", SinceLevel = book.Level };
                book.Cards[id] = use;
            }

            use.Rounds++;
            use.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (book.SeenThisFight.Add(id)) {
                use.Fights++;
            }

            if (id == book.Cast) {
                use.Casts++;
                if (book.CastThisFight.Add(id)) {
                    use.FightsCast++;
                }
            }
        }

        book.Hand = [];
        book.Dirty = true;
    }

    /// <summary>
    /// Why he no longer plays this card, from his own fights ("Cleanse Charm: in my hand in 7 fights, cast in none (0%)"),
    /// or null. A card past <see cref="RETRY_LEVELS"/> levels since it was last tried starts over.
    /// </summary>
    internal static string Rarely(string folder, uint id, int level) {
        if (string.IsNullOrEmpty(folder) || id == 0) {
            return null;
        }

        var book = BookOf(folder);
        lock (book.Lock) {
            if (!book.Cards.TryGetValue(id, out var use) || use.Fights < MIN_FIGHTS || use.FightsCast > 0) {
                return null;
            }

            if (Template(id) is { Heal: true }) {
                return null;
            }

            if (level > 0 && level >= use.SinceLevel + RETRY_LEVELS) {
                // A new try: what he fights now is not what he fought then.
                use.Fights = use.FightsCast = use.Rounds = use.Casts = 0;
                use.SinceLevel = level;
                book.Dirty = true;
                return null;
            }

            return $"rarely played: in my hand in {use.Fights} fights, cast in none (0%); tried again at level {use.SinceLevel + RETRY_LEVELS}";
        }
    }

    /// <summary>How likely he plays it, in words, for his notes: "in hand 12 fights, cast in 9 (75%), 14 casts", or "".</summary>
    internal static string Likelihood(string folder, uint id) {
        var book = BookOf(folder);
        lock (book.Lock) {
            return book.Cards.TryGetValue(id, out var use) && use.Fights > 0
                ? $"in hand {use.Fights} fights, cast in {use.FightsCast} ({Percent(use)}%), {use.Casts} casts in {use.Rounds} rounds held"
                : "";
        }
    }

    /// <summary>His main deck as he packed it last, a line per spell with why it is in, for card-use.txt.</summary>
    internal static void Deck(string folder, IEnumerable<string> lines) {
        if (string.IsNullOrEmpty(folder)) {
            return;
        }

        var book = BookOf(folder);
        lock (book.Lock) {
            book.Deck = [.. lines];
            book.Dirty = true;
        }

        Save(folder, book, force: true);
    }

    private static int Percent(Use use) => use.Fights == 0 ? 0 : (int) Math.Round(100.0 * use.FightsCast / use.Fights);

    /// <summary>
    /// card-use.json (the counts) and card-use.txt (for Niduus: every card he has held, most played first, with the ones he
    /// no longer plays and why), at most every 30 seconds unless forced.
    /// </summary>
    internal static void Save(string folder, bool force = false) => Save(folder, BookOf(folder), force);

    private static void Save(string folder, Book book, bool force) {
        List<(uint Id, Use Use)> cards;
        List<string> deck;
        lock (book.Lock) {
            if (!book.Dirty || (!force && DateTime.UtcNow - book.Written < TimeSpan.FromSeconds(30))) {
                return;
            }

            book.Dirty = false;
            book.Written = DateTime.UtcNow;
            cards = [.. book.Cards.Select(c => (c.Key, new Use {
                Name = c.Value.Name, Kind = c.Value.Kind, Fights = c.Value.Fights, FightsCast = c.Value.FightsCast, Rounds = c.Value.Rounds,
                Casts = c.Value.Casts, SinceLevel = c.Value.SinceLevel, LastSeen = c.Value.LastSeen,
            }))];
            deck = [.. book.Deck];
        }

        try {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "card-use.json"), JsonSerializer.Serialize(cards.ToDictionary(c => c.Id, c => c.Use)));
            var lines = new List<string> {
                $"CARD USE, {DateTime.Now:yyyy-MM-dd HH:mm}: how likely I am to play each card, from my own fights.",
                $"A card in my hand in {MIN_FIGHTS} or more fights that I never cast is rarely played: an item card is turned off,",
                $"a treasure card stays in my book, a spell of my deck makes way for another. Heals are always kept. Every {RETRY_LEVELS} levels",
                "a rarely played card gets a new try.",
                "",
            };
            if (deck.Count > 0) {
                lines.Add("MY DECK, and why each spell is in it:");
                lines.AddRange(deck);
                lines.Add("");
            }

            lines.Add("EVERY CARD I HAVE HELD, most played first:");
            foreach (var (id, use) in cards.OrderByDescending(c => Percent(c.Use)).ThenByDescending(c => c.Use.Casts)) {
                var rarely = use.Fights >= MIN_FIGHTS && use.FightsCast == 0 && use.Kind != "heal";
                lines.Add($"  {use.Name} ({use.Kind}): {Percent(use)}% (in hand {use.Fights} fights, cast in {use.FightsCast}; {use.Casts} casts in {use.Rounds} rounds held)"
                          + (rarely ? " RARELY PLAYED, out until level " + (use.SinceLevel + RETRY_LEVELS) : ""));
            }

            File.WriteAllLines(Path.Combine(folder, "card-use.txt"), lines);
        }
        catch (Exception) {
            // Notes only: never in the way of play.
        }
    }

    private static RewrittenAI.Card Template(uint id) {
        try {
            return RewrittenAI.ReadTemplate(id);
        }
        catch (Exception) {
            return null;
        }
    }

    private static string Shown(uint id) {
        var card = Template(id);
        if (card is { Shown.Length: > 0 }) {
            return card.Shown;
        }

        try {
            return CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate t ? (string) t.m_name ?? id.ToString() : id.ToString();
        }
        catch (Exception) {
            return id.ToString();
        }
    }
}
