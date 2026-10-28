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
 * FISHING DAYS
 * ========================================================================
 *
 * PURPOSE:
 * The Catch of the Day and the week's fishing tournament fish, the way the game's own help text has them
 * (Locale Fishing.lang 210 and 211):
 * - The Catch of the Day changes every day at midnight US Central time. It sells for three times its price plus the
 *   wizard's Catch of the Day bonus, which grows by 100 gold each day they catch it, up to 3000.
 * - A tournament lasts one week, from midnight Sunday night (Monday 00:00 US Central). It is for one fish, and for its
 *   whoppers or its small fry: the biggest whopper or the smallest small fry wins.
 * Both are picked from every catchable fish by the date, so the whole server has the same ones, and [Fishing]
 * CatchOfTheDayFish and TournamentFish pin them to a template id. Every setting is read when it is used.
 */

using System;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Fishing;

internal static class FishingDays {

    internal const int BonusStep = 100;
    internal const int MaxBonus = 3000;

    private static readonly Lazy<TimeZoneInfo> s_central = new(() => {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" }) {
            try {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception) {
                // The other name is the one this system knows.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("US Central", TimeSpan.FromHours(-6), "US Central", "US Central");
    });

    internal static bool CatchOfTheDayOn => FishingSettings.Enabled && ConfigurationManager.GetValue("Fishing.CatchOfTheDay", true);

    internal static bool TournamentsOn => FishingSettings.Enabled && ConfigurationManager.GetValue("Fishing.Tournaments", true);

    /// <summary>Now, in US Central time.</summary>
    internal static DateTime CentralNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, s_central.Value);

    /// <summary>Today's date in US Central time, as yyyy-MM-dd.</summary>
    internal static string Today => CentralNow.ToString("yyyy-MM-dd");

    /// <summary>Today's Catch of the Day, or 0 when it is off or there are no fish.</summary>
    internal static uint CatchOfTheDay {
        get {
            if (!CatchOfTheDayOn) {
                return 0;
            }

            var pinned = (uint) Math.Max(0, ConfigurationManager.GetValue("Fishing.CatchOfTheDayFish", 0));
            if (pinned != 0 && FishingData.Fish(pinned) is not null) {
                return pinned;
            }

            var day = CentralNow.Date;

            return Pick((int) (day - DateTime.UnixEpoch.Date).TotalDays, 7919);
        }
    }

    /// <summary>Whether this fish is today's Catch of the Day.</summary>
    internal static bool IsCatchOfTheDay(uint templateId) => templateId != 0 && templateId == CatchOfTheDay;

    /// <summary>
    /// The bonus a wizard's Catch of the Day sales get today: their bonus when they caught it today, otherwise none.
    /// </summary>
    internal static int BonusToday(ServerFishingBehavior behavior)
        => behavior is not null && behavior.CatchOfTheDayCaught == Today ? Math.Clamp(behavior.CatchOfTheDayBonus, 0, MaxBonus) : 0;

    /// <summary>
    /// What a fish sells for to this wizard: its price, or three times it and the day's bonus for the Catch of the Day
    /// (the game's fish seller window shows the same).
    /// </summary>
    internal static int SalePrice(ServerFishingBehavior behavior, uint templateId, float size) {
        var price = FishingData.Fish(templateId)?.PriceOf(size) ?? 0;

        return IsCatchOfTheDay(templateId) ? price * 3 + BonusToday(behavior) : price;
    }

    /// <summary>
    /// Counts a catch of the Catch of the Day: the first one of a day raises the bonus by 100 gold, up to 3000.
    /// </summary>
    /// <returns>The new bonus when this was the first catch of it today (the catch window says so), otherwise 0.</returns>
    internal static int CountCatch(ServerFishingBehavior behavior, uint templateId) {
        if (behavior is null || !IsCatchOfTheDay(templateId)) {
            return 0;
        }

        var today = Today;
        if (behavior.CatchOfTheDayCaught == today) {
            return 0;
        }

        behavior.CatchOfTheDayCaught = today;
        behavior.CatchOfTheDayBonus = Math.Clamp(behavior.CatchOfTheDayBonus + BonusStep, BonusStep, MaxBonus);

        return behavior.CatchOfTheDayBonus;
    }

    /// <summary>The start of this week's tournament: Monday 00:00 US Central, as a UTC time.</summary>
    internal static DateTime WeekStartUtc(int weeksBack = 0) {
        var now = CentralNow;
        var daysSinceMonday = ((int) now.DayOfWeek + 6) % 7;
        var monday = now.Date.AddDays(-daysSinceMonday - 7 * weeksBack);

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(monday, DateTimeKind.Unspecified), s_central.Value);
    }

    /// <summary>The time a tournament week ends, as the game's leaderboard names the week: seconds since 1970.</summary>
    internal static uint WeekEnd(int weeksBack = 0) => ToUnix(WeekStartUtc(weeksBack - 1));

    internal static uint ToUnix(DateTime utc) => (uint) Math.Max(0, (utc - DateTime.UnixEpoch).TotalSeconds);

    /// <summary>The fish and size class of the tournament of the week that ends at <paramref name="weekEnd"/>.</summary>
    internal static (uint TemplateId, bool Whoppers) TournamentOf(uint weekEnd) {
        var week = (int) (weekEnd / (7 * 24 * 3600));
        var pinned = (uint) Math.Max(0, ConfigurationManager.GetValue("Fishing.TournamentFish", 0));
        var fish = pinned != 0 && FishingData.Fish(pinned) is not null ? pinned : Pick(week, 104729);
        var size = ConfigurationManager.GetValue("Fishing.TournamentSize", "Either").Trim().ToLowerInvariant();
        var whoppers = size switch {
            "whopper" or "whoppers" => true,
            "smallfry" or "small fry" or "small" => false,
            _ => (week & 1) == 0,
        };

        return (fish, whoppers);
    }

    /// <summary>Whether a catch is the week's tournament fish: the fish, and a whopper or small fry as the week wants.</summary>
    internal static bool IsTournamentFish(uint templateId, float size) {
        if (!TournamentsOn) {
            return false;
        }

        var (fish, whoppers) = TournamentOf(WeekEnd());
        var info = FishingData.Fish(templateId);

        return fish != 0 && templateId == fish && info is not null && (whoppers ? info.IsWhopper(size) : info.IsSmallFry(size));
    }

    // The same pick on every server start and for every wizard: a fish by a number that changes each day or week.
    private static uint Pick(int number, int salt) {
        var fish = FishingData.AllFish;
        if (fish.Count == 0) {
            return 0;
        }

        var mixed = unchecked((uint) number * 2654435761u + (uint) salt * 40503u);
        mixed ^= mixed >> 15;

        return fish[(int) (mixed % (uint) fish.Count)].TemplateId;
    }

    /// <summary>A line for chat: today's Catch of the Day and this week's tournament.</summary>
    internal static string Describe(ServerFishingBehavior behavior) {
        var lines = new System.Collections.Generic.List<string>();
        if (CatchOfTheDayOn && FishingData.Fish(CatchOfTheDay) is { } today) {
            lines.Add($"Catch of the Day: {today.Name} (rank {today.Rank} {today.School}). It sells for three times its price"
                      + (BonusToday(behavior) > 0 ? $" plus your {BonusToday(behavior)} gold bonus today." : $"; catch one today for a {Math.Min(MaxBonus, (behavior?.CatchOfTheDayBonus ?? 0) + BonusStep)} gold bonus on each one you sell."));
        }

        if (TournamentsOn && TournamentOf(WeekEnd()) is var (fishId, whoppers) && FishingData.Fish(fishId) is { } tournament) {
            var left = DateTime.UnixEpoch.AddSeconds(WeekEnd()) - DateTime.UtcNow;
            lines.Add($"This week's tournament: {(whoppers ? "whoppers" : "small fry")} of {tournament.Name} (rank {tournament.Rank} {tournament.School}),"
                      + $" {(whoppers ? "the biggest" : "the smallest")} wins. It ends in {(int) left.TotalDays} days {left.Hours} hours.");
        }

        return lines.Count == 0 ? "The Catch of the Day and fishing tournaments are off." : string.Join("<br>", lines);
    }

}
