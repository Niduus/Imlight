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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.DailyAssignments;

/// <summary>
/// The Daily Assignments of the Gravulum Order (Aegon Statz in Ravenwood). The assignments and the day rewards are the
/// game's own: DailyQuestData/DailyQuestData.xml (623 assignments in 8 circles, each a creature to defeat or a place to
/// visit) and DailyQuestData/DailyQuestRewards.xml (one reward per day of a 15 day streak), read from Root.wad.
/// What the server keeps per wizard is in the quest registry under ImlightDaily.*: the day an assignment was handed out,
/// which one, the day it was last done, the streak and the total done.
/// </summary>
/// <remarks>
/// Guesses (no client code was read): QuestIndex is the assignment's place in dailyQuestList, Flags 1 means done today,
/// circle N+1 opens with the registry entry circleRegistryList[N-1] (AccessToMooShu ...) or, as the server keeps no
/// world access entries, with a level ([DailyAssignments] CircleLevels).
/// </remarks>
internal static class DailyAssignmentData {

    internal const string DATA_FILE = "DailyQuestData/DailyQuestData.xml";
    internal const string REWARD_FILE = "DailyQuestData/DailyQuestRewards.xml";

    // The quest registry keys of a wizard's daily state.
    internal const string KEY_DAY = "ImlightDaily.Day";
    internal const string KEY_INDEX = "ImlightDaily.Index";      // assignment index + 1 (0 = none)
    internal const string KEY_DONE_DAY = "ImlightDaily.DoneDay";
    internal const string KEY_STREAK = "ImlightDaily.Streak";
    internal const string KEY_TOTAL = "ImlightDaily.Total";

    // DailyQuestReward::RewardTypes as the type dump numbers them.
    internal const int RT_GOLD = 0;
    internal const int RT_CROWNS = 1;
    internal const int RT_CHEST = 5;
    internal const int RT_QUEST = 6;

    internal const byte FLAG_DONE_TODAY = 1;

    internal static bool IsEnabled => ConfigurationManager.GetValue("DailyAssignments.Enabled", true);
    internal static int MinLevel => ConfigurationManager.GetValue("DailyAssignments.MinLevel", 1);
    internal static bool RequireIntro => ConfigurationManager.GetValue("DailyAssignments.RequireIntroQuest", false);
    internal static bool DefeatAnywhere => ConfigurationManager.GetValue("DailyAssignments.DefeatAnywhere", true);
    internal static int ChestFallbackGold => ConfigurationManager.GetValue("DailyAssignments.ChestFallbackGold", 500);
    internal static int ResetHour => Math.Clamp(ConfigurationManager.GetValue("DailyAssignments.ResetHour", 0), 0, 23);
    internal static string NpcObjectNames => ConfigurationManager.GetValue("DailyAssignments.NpcObjectNames", "WC-RAV-NPC11");

    internal const string INTRO_QUEST = "WC-GRAV-INTRO-001";

    // The level that opens circles 2 to 8 when the wizard has no AccessTo... registry entry: about the level the next
    // world starts at (MooShu, Celestia, Avalon, Khrysalis, Mirage, Karamelle, Lemuria).
    private static int[] s_circleLevels => s_circleLevelsLive.Value;
    private static readonly LiveValue<int[]> s_circleLevelsLive = new(() => ParseLevels(
        ConfigurationManager.GetValue("DailyAssignments.CircleLevels", "20,50,65,80,110,130,140")));

    private static readonly object s_lock = new();
    private static List<DailyQuestInfo> s_assignments;
    private static List<DailyQuestReward> s_rewards;
    private static List<string> s_circleEntries;
    private static List<string> s_loreQuests;
    private static HashSet<string> s_volumeNames;

    /// <summary>The volumes of each zone with a name some assignment visits, as the zones load them (VolumeComponent).</summary>
    private static readonly ConcurrentDictionary<(string Zone, string Volume), (float X, float Y, float Z, float Radius)> s_volumes = new();

    internal static IReadOnlyList<DailyQuestInfo> Assignments { get { Load(); return s_assignments; } }
    internal static IReadOnlyList<DailyQuestReward> Rewards { get { Load(); return s_rewards; } }

    private static int[] ParseLevels(string text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, out var n) ? n : int.MaxValue).ToArray();

    private static void Load() {
        if (s_assignments is not null) {
            return;
        }

        lock (s_lock) {
            if (s_assignments is not null) {
                return;
            }

            var assignments = new List<DailyQuestInfo>();
            var rewards = new List<DailyQuestReward>();
            var circleEntries = new List<string>();
            var loreQuests = new List<string>();
            try {
                var data = RootArchiveLoader.GetFile<DailyQuestData>(DATA_FILE);
                assignments = data?.m_dailyQuestList ?? [];
                var rewardData = RootArchiveLoader.GetFile<DailyQuestRewardData>(REWARD_FILE);
                rewards = rewardData?.m_dailyQuestRewardList?.Where(r => r is not null).ToList() ?? [];
                circleEntries = rewardData?.m_circleRegistryList?.Select(s => (string) s).ToList() ?? [];
                loreQuests = rewardData?.m_questRewardsList?.Select(s => (string) s).ToList() ?? [];
            }
            catch (Exception ex) {
                Logger.Error("Daily Assignments | Reading the game's daily assignment data failed. {0}", Logger.Args(ex.Message));
            }

            s_volumeNames = assignments
                .Where(a => a is not null && !a.m_activatorVolumeName.IsEmpty)
                .Select(a => (string) a.m_activatorVolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            s_rewards = rewards;
            s_circleEntries = circleEntries;
            s_loreQuests = loreQuests;
            s_assignments = assignments;
            Logger.Information("Daily Assignments | {0} assignments, {1} day rewards ({2}).", Logger.Args(
                assignments.Count, rewards.Count, string.Join(", ", rewards.Select(DescribeReward))));
        }
    }

    /// <summary>The day the wizard's assignment belongs to: days since 0001-01-01, turning over at ResetHour (server clock).</summary>
    internal static ulong Today() => (ulong) DateOnly.FromDateTime(DateTime.Now.AddHours(-ResetHour)).DayNumber;

    /// <summary>Seconds until the next assignment.</summary>
    internal static uint SecondsLeft() {
        var now = DateTime.Now;
        var next = now.Date.AddHours(ResetHour);
        while (next <= now) {
            next = next.AddDays(1);
        }

        return (uint) Math.Max(1, (next - now).TotalSeconds);
    }

    /// <summary>The highest circle open to the wizard (1 to 8).</summary>
    internal static int HighestCircle(Wizard wizard) {
        Load();
        var level = wizard?.MagicSchoolBehavior?.Level ?? 1;
        var circle = 1;
        for (var i = 0; i < 7; i++) {
            var entry = i < s_circleEntries.Count ? s_circleEntries[i] : null;
            var byEntry = !string.IsNullOrEmpty(entry) && wizard?.HasRegistryValue(entry) == true;
            var byLevel = i < s_circleLevels.Length && level >= s_circleLevels[i];
            if (!byEntry && !byLevel) {
                break;
            }

            circle = i + 2;
        }

        return circle;
    }

    /// <summary>Whether the wizard may get assignments, and if not, why.</summary>
    internal static bool CanTakeAssignments(Wizard wizard, out string why) {
        why = null;
        if (!IsEnabled) {
            why = "Daily Assignments are turned off on this server.";
        }
        else if (Assignments.Count == 0) {
            why = "The server could not read the game's Daily Assignment list (see the server log).";
        }
        else if ((wizard?.MagicSchoolBehavior?.Level ?? 1) < MinLevel) {
            why = $"Daily Assignments start at level {MinLevel}.";
        }
        else if (RequireIntro && wizard?.HasCompletedQuest(INTRO_QUEST) != true) {
            why = "Finish The Gravulum Order for Aegon Statz in Ravenwood first.";
        }

        return why is null;
    }

    /// <summary>
    /// Picks an assignment: a random circle the wizard has open, then a random assignment of it. Assignments whose
    /// creature or place this server does not know are skipped when others exist.
    /// </summary>
    internal static int Pick(Wizard wizard, int avoid = -1) {
        var circle = HighestCircle(wizard);
        var random = new Random();
        var open = Enumerable.Range(0, Assignments.Count)
            .Where(i => Assignments[i] is not null && Assignments[i].m_circleNumber >= 1 && Assignments[i].m_circleNumber <= circle)
            .ToList();
        if (open.Count == 0) {
            return -1;
        }

        var pickedCircle = open.Select(i => Assignments[i].m_circleNumber).Distinct().OrderBy(_ => random.Next()).First();
        var choices = open.Where(i => Assignments[i].m_circleNumber == pickedCircle && i != avoid).ToList();
        var from = (choices.Count > 0 ? choices : open).OrderBy(_ => random.Next()).ToList();

        return from.FirstOrDefault(i => Assignments[i].m_templateID == 0
            || CoreObjectFactory.GetCoreTemplate(Assignments[i].m_templateID) is not null, from[0]);
    }

    internal static DailyQuestInfo Get(int index)
        => index >= 0 && index < Assignments.Count ? Assignments[index] : null;

    /// <summary>The reward for the given day of a streak (1 = first day), going round after the last one.</summary>
    internal static DailyQuestReward RewardFor(ulong streakDay)
        => Rewards.Count == 0 ? null : Rewards[(int) ((Math.Max(1UL, streakDay) - 1) % (ulong) Rewards.Count)];

    /// <summary>The Gravulum lore quest of a circle (questRewardsList), for RT_Quest rewards.</summary>
    internal static string LoreQuestFor(int circle) {
        Load();
        return circle >= 1 && circle <= s_loreQuests.Count ? s_loreQuests[circle - 1] : null;
    }

    internal static bool IsAssignmentVolume(string volumeName) {
        Load();
        return !string.IsNullOrEmpty(volumeName) && s_volumeNames.Contains(volumeName);
    }

    internal static void RememberVolume(string zone, string volumeName, float x, float y, float z, float radius) {
        if (!string.IsNullOrEmpty(zone) && IsAssignmentVolume(volumeName)) {
            s_volumes[(zone.ToLowerInvariant(), volumeName.ToLowerInvariant())] = (x, y, z, radius);
        }
    }

    internal static bool TryGetVolume(string zone, string volumeName, out (float X, float Y, float Z, float Radius) volume) {
        volume = default;
        return !string.IsNullOrEmpty(zone) && !string.IsNullOrEmpty(volumeName)
            && s_volumes.TryGetValue((zone.ToLowerInvariant(), volumeName.ToLowerInvariant()), out volume);
    }

    /// <summary>A line for chat: what today's assignment asks.</summary>
    internal static string Describe(DailyQuestInfo info) {
        if (info is null) {
            return "no assignment";
        }

        var zone = Text(info.m_zoneDisplayName, info.m_zonePath);
        if (info.m_templateID != 0) {
            var mob = CoreObjectFactory.GetCoreTemplate(info.m_templateID) is GameObjectTemplate t ? Text(t.m_displayName, t.m_objectName) : $"creature {info.m_templateID}";

            return $"defeat one {mob} in {zone}";
        }

        var place = Text(info.m_locationDisplayName, null);

        return string.IsNullOrEmpty(place) || place == zone ? $"visit {zone}" : $"visit {place} in {zone}";
    }

    internal static string DescribeReward(DailyQuestReward reward) {
        if (reward is null) {
            return "nothing";
        }

        return (int) reward.m_rewardType switch {
            RT_GOLD => $"{reward.m_quantity} Gold",
            RT_CROWNS => $"{reward.m_quantity} Crowns",
            RT_CHEST => $"a chest ({(string) reward.m_lootTable})",
            RT_QUEST => "a Gravulum Order quest",
            var other => $"reward type {other} x{reward.m_quantity}",
        };
    }

    private static string Text(string key, string fallback) {
        if (string.IsNullOrEmpty(key)) {
            return fallback ?? "";
        }

        try {
            var shown = Locale.GetEnglishName(key);
            if (!string.IsNullOrWhiteSpace(shown)) {
                return shown;
            }
        }
        catch (Exception) {
            // A key the locale does not have shows as the fallback.
        }

        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

}
