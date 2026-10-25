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
using System.Text;
using System.Threading;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The goal ID the game's quest arrow looks a goal up by (MSG_SENDGOAL GoalNameID).
/// </summary>
/// <remarks>
/// The game finds where the arrow points in Root.wad poi.dat, keyed by StringHash(quest name) and the goal ID. On live
/// the goal ID is StringHash(goal name) ("Goal", "Goal 2", ...). Most of SpiralDB's quests carry m_goalNameID 0, and the
/// game then finds no spot, so no arrow shows (Niduus's arrow log, 2026-10-25: "Found 0 locations for goal"). A goal
/// with no ID gets the hash of its name when poi.dat knows it. Our own quests (Wizard404) only have made-up goal names,
/// so their goal is matched to the quest's poi.dat goal in the goal's zone, when only one is left there.
/// </remarks>
internal static class QuestArrowGoals {

    private const string POI_FILE = "poi.dat";

    private static readonly Lock s_lock = new();
    private static Dictionary<uint, Dictionary<uint, HashSet<string>>> s_poiGoals;
    private static readonly ConcurrentDictionary<string, Dictionary<string, uint>> s_questGoalIds
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The goal ID to send for this goal of this quest.</summary>
    internal static uint GetGoalNameId(QuestTemplate quest, GoalTemplate goal) {
        if (goal is null) {
            return 0;
        }
        if (goal.m_goalNameID != 0 || quest?.m_goals is null) {
            return goal.m_goalNameID;
        }

        var ids = s_questGoalIds.GetOrAdd((string) quest.m_questName ?? "", _ => ResolveGoalIds(quest));

        return ids.TryGetValue((string) goal.m_goalName ?? "", out var id) ? id : 0;
    }

    private static Dictionary<string, uint> ResolveGoalIds(QuestTemplate quest) {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        var poi = GetPoiGoals();
        if (poi is null || !poi.TryGetValue(StringHash.Compute((string) quest.m_questName ?? ""), out var questPoi)) {
            return result;
        }

        var goals = quest.m_goals.Where(g => g is not null && !string.IsNullOrEmpty(g.m_goalName) && g.m_goalNameID == 0).ToList();
        var used = new HashSet<uint>(quest.m_goals.Where(g => g is not null && g.m_goalNameID != 0).Select(g => g.m_goalNameID));

        // A real goal name: the game keys the goal by its hash.
        foreach (var goal in goals.ToList()) {
            var hash = StringHash.Compute((string) goal.m_goalName);
            if (questPoi.ContainsKey(hash) && used.Add(hash)) {
                result[goal.m_goalName] = hash;
                goals.Remove(goal);
            }
        }

        // A made-up goal name: the only poi.dat goal of the quest left in the goal's zone.
        bool found;
        do {
            found = false;
            foreach (var goal in goals.ToList()) {
                string zone = goal.m_destinationZone;
                if (string.IsNullOrEmpty(zone)) {
                    continue;
                }

                var candidates = questPoi.Where(p => !used.Contains(p.Key) && p.Value.Contains(zone)).Select(p => p.Key).ToList();
                if (candidates.Count == 1) {
                    result[goal.m_goalName] = candidates[0];
                    used.Add(candidates[0]);
                    goals.Remove(goal);
                    found = true;
                }
            }
        } while (found);

        return result;
    }

    // poi.dat: the zone names, then one record per quest goal spot: key (goal ID << 32 | quest hash), a byte, the zone
    // index, a GID, the position and two bytes.
    private static Dictionary<uint, Dictionary<uint, HashSet<string>>> GetPoiGoals() {
        lock (s_lock) {
            if (s_poiGoals is not null) {
                return s_poiGoals;
            }

            var goals = new Dictionary<uint, Dictionary<uint, HashSet<string>>>();
            try {
                using var stream = RootArchiveLoader.GetFileStream(POI_FILE);
                using var reader = new BinaryReader(stream, Encoding.Latin1);

                var zones = new string[reader.ReadUInt32()];
                for (var i = 0; i < zones.Length; i++) {
                    zones[i] = Encoding.Latin1.GetString(reader.ReadBytes((int) reader.ReadUInt32()));
                }

                var count = reader.ReadUInt32();
                for (var i = 0; i < count; i++) {
                    var key = reader.ReadUInt64();
                    reader.ReadByte();
                    var zone = reader.ReadUInt16();
                    reader.ReadBytes(8 + 12 + 2);

                    var questHash = (uint) key;
                    var goalId = (uint) (key >> 32);
                    if (!goals.TryGetValue(questHash, out var questGoals)) {
                        questGoals = [];
                        goals[questHash] = questGoals;
                    }
                    if (!questGoals.TryGetValue(goalId, out var goalZones)) {
                        goalZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        questGoals[goalId] = goalZones;
                    }
                    if (zone < zones.Length) {
                        goalZones.Add(zones[zone]);
                    }
                }

                Logger.Information("Quest arrow: read {Count} goal spots of {Quests} quests from poi.dat.",
                    Logger.Args(count, goals.Count));
            }
            catch (Exception e) {
                Logger.Warning("Quest arrow: poi.dat could not be read: {Error}", Logger.Args(e.Message));
            }

            s_poiGoals = goals;

            return s_poiGoals;
        }
    }

}
