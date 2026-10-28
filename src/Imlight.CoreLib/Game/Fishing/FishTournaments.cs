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
 *
 * ========================================================================
 * FISH TOURNAMENTS
 * ========================================================================
 *
 * PURPOSE:
 * The fish entered in each week's fishing tournament, their places and the prizes. Kept in fish-tournaments.json
 * beside the server, so they last through restarts; the last eight weeks are kept.
 *
 * HOW THE GAME DOES IT (read from WizardGraphicalClient.exe, 2026-10-28):
 * - A catch the server marks IsTournamentFish asks "enter this fish in the tournament?"; yes sends
 *   MSG_ENTERTOURNAMENTFISH (template, size) and the answer's Position (from 0) shows "#Position+1" up to 99, and only
 *   "entered" past that (FishingManager, 0x1405b9850).
 * - The leaderboard window asks for a page of ten with MSG_FISHTOURNAMENTLEADERBOARDREQUEST: ExpireTime names the week
 *   (the two the open message gave), PageNumber the page, with 0x8000 for "My Fish". The answer is the same message
 *   with Data = a FishTournamentUpdate (SerializerBinary, no sizes or flags): the entry count, the fish and up to ten
 *   FishTournamentEntry. On "My Fish" pages an entry's m_characterGID holds its place instead (0x1405959e0).
 * - Medals as the window colors them: gold for the top count/100 places (at least 1), silver to three times that,
 *   bronze to six times that (0x1405958a9). Every other fish entered gets a consolation prize (Fishing.lang 211).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Fishing;

internal enum TournamentMedal {
    Gold,
    Silver,
    Bronze,
    Consolation,
}

internal static class FishTournaments {

    private const string FILE = "fish-tournaments.json";
    private const int WEEKS_KEPT = 8;
    internal const int PAGE_SIZE = 10;
    internal const int BOARD_SIZE = 100;

    internal sealed class Entry {

        public ulong CharId { get; set; }
        public string Name { get; set; }
        public string NameHex { get; set; }
        public float Size { get; set; }
        public DateTime Entered { get; set; }

    }

    internal sealed class Week {

        public uint FishTemplateId { get; set; }
        public bool Whoppers { get; set; }
        public List<Entry> Entries { get; set; } = [];
        public List<ulong> Rewarded { get; set; } = [];

    }

    private sealed class Data {

        public Dictionary<string, Week> Weeks { get; set; } = [];

    }

    private static readonly object s_lock = new();
    private static Data s_data;

    private static Data Load() {
        if (s_data is not null) {
            return s_data;
        }

        try {
            s_data = File.Exists(FILE) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FILE)) ?? new Data() : new Data();
        }
        catch (Exception e) {
            Logger.Warning("Fishing tournaments: could not read {0}: {1}", Logger.Args(FILE, e.Message));
            s_data = new Data();
        }

        s_data.Weeks ??= [];

        return s_data;
    }

    private static void Save() {
        try {
            string json;
            lock (s_lock) {
                var data = Load();
                foreach (var old in data.Weeks.Keys.OrderByDescending(k => uint.TryParse(k, out var end) ? end : 0).Skip(WEEKS_KEPT).ToList()) {
                    data.Weeks.Remove(old);
                }

                json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            }

            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Fishing tournaments: could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    // The week ending at weekEnd; a new one gets the fish the calendar has for it.
    private static Week WeekOf(uint weekEnd, bool create) {
        var data = Load();
        var key = weekEnd.ToString();
        if (data.Weeks.TryGetValue(key, out var week)) {
            week.Entries ??= [];
            week.Rewarded ??= [];

            return week;
        }

        if (!create) {
            return null;
        }

        var (fish, whoppers) = FishingDays.TournamentOf(weekEnd);
        week = new Week { FishTemplateId = fish, Whoppers = whoppers };
        data.Weeks[key] = week;

        return week;
    }

    /// <summary>The fish and size class of a week, from its saved entry when it has one.</summary>
    internal static (uint TemplateId, bool Whoppers) FishOf(uint weekEnd) {
        lock (s_lock) {
            var week = WeekOf(weekEnd, create: false);

            return week is null ? FishingDays.TournamentOf(weekEnd) : (week.FishTemplateId, week.Whoppers);
        }
    }

    private static List<Entry> Ranked(Week week)
        => week.Whoppers
            ? [.. week.Entries.OrderByDescending(e => e.Size).ThenBy(e => e.Entered)]
            : [.. week.Entries.OrderBy(e => e.Size).ThenBy(e => e.Entered)];

    /// <summary>Enters a fish in this week's tournament.</summary>
    /// <returns>The fish's place, from 0.</returns>
    internal static int Enter(ulong charId, string name, string nameHex, float size) {
        int place;
        lock (s_lock) {
            var week = WeekOf(FishingDays.WeekEnd(), create: true);
            var entry = new Entry { CharId = charId, Name = name, NameHex = nameHex, Size = size, Entered = DateTime.UtcNow };
            week.Entries.Add(entry);
            place = Ranked(week).IndexOf(entry);
        }

        Save();

        return place;
    }

    /// <summary>
    /// A page of a week's board: the top hundred, or with <paramref name="mine"/> the wizard's own fish, each with its place.
    /// </summary>
    internal static (int Count, uint FishTemplateId, List<(int Place, Entry Entry)> Page) Board(uint weekEnd, int page, bool mine, ulong charId) {
        lock (s_lock) {
            var week = WeekOf(weekEnd, create: weekEnd == FishingDays.WeekEnd());
            if (week is null) {
                return (0, FishingDays.TournamentOf(weekEnd).TemplateId, []);
            }

            var ranked = Ranked(week).Select((entry, place) => (Place: place, Entry: entry));
            var shown = mine ? ranked.Where(e => e.Entry.CharId == charId) : ranked.Take(BOARD_SIZE);

            return (week.Entries.Count, week.FishTemplateId, [.. shown.Skip(Math.Max(page, 0) * PAGE_SIZE).Take(PAGE_SIZE)]);
        }
    }

    /// <summary>The medal of a place among this many entries, as the leaderboard colors it.</summary>
    internal static TournamentMedal MedalOf(int place, int count) {
        var gold = Math.Max(1, count / 100);
        if (place < gold) {
            return TournamentMedal.Gold;
        }

        if (place < gold * 3) {
            return TournamentMedal.Silver;
        }

        return place < gold * 6 ? TournamentMedal.Bronze : TournamentMedal.Consolation;
    }

    /// <summary>
    /// The prizes a wizard has not had yet: one medal or consolation per fish they entered in a tournament that is over.
    /// They are counted as given.
    /// </summary>
    internal static List<(uint WeekEnd, uint FishTemplateId, int Place, TournamentMedal Medal)> TakePrizes(ulong charId) {
        var prizes = new List<(uint, uint, int, TournamentMedal)>();
        var now = FishingDays.ToUnix(DateTime.UtcNow);
        lock (s_lock) {
            foreach (var (key, week) in Load().Weeks) {
                if (!uint.TryParse(key, out var end) || end > now || week.Entries is null || (week.Rewarded ??= []).Contains(charId)) {
                    continue;
                }

                var ranked = Ranked(week);
                for (var place = 0; place < ranked.Count; place++) {
                    if (ranked[place].CharId == charId) {
                        prizes.Add((end, week.FishTemplateId, place, MedalOf(place, ranked.Count)));
                    }
                }

                if (ranked.Any(e => e.CharId == charId)) {
                    week.Rewarded.Add(charId);
                }
            }
        }

        if (prizes.Count > 0) {
            Save();
        }

        return prizes;
    }

}
