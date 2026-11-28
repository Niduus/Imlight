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
using Imcodec.Math;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

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
    // Where each goal's arrow points: the zone and the spot of its target (an NPC, an object, a place).
    private static Dictionary<ulong, List<(string Zone, Imcodec.Math.Vector3 Spot)>> s_poiSpots = [];
    // The template each goal's arrow points at (the record's GID is 2^41 + the template ID; 0 for a bare place).
    private static Dictionary<ulong, HashSet<uint>> s_poiTargets = [];
    // Steps already logged as having no arrow spot, so each is logged once.
    private static readonly ConcurrentDictionary<string, byte> s_noSpotLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, uint>> s_questGoalIds
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The goal ID to send for this goal of this quest.</summary>
    internal static uint GetGoalNameId(QuestTemplate quest, GoalTemplate goal) {
        if (goal is null) {
            return 0;
        }
        if (quest?.m_goals is null) {
            return goal.m_goalNameID;
        }

        // A goal ID from the quest data that poi.dat has no spot for gives no arrow either: it is matched like a goal
        // with no ID.
        if (goal.m_goalNameID != 0 && PoiHas(quest, goal.m_goalNameID)) {
            return goal.m_goalNameID;
        }

        var ids = s_questGoalIds.GetOrAdd((string) quest.m_questName ?? "", _ => ResolveGoalIds(quest));
        var id = ids.TryGetValue((string) goal.m_goalName ?? "", out var found) ? found : goal.m_goalNameID;
        if (!PoiHas(quest, id) && s_noSpotLogged.TryAdd($"{quest.m_questName}/{goal.m_goalName}", 0)) {
            Logger.Information("Quest arrow: {0} step {1} ({2}) has no spot in poi.dat; the game shows no arrow for it (poi.dat steps of the quest: {3}).",
                Logger.Args(quest.m_questName, goal.m_goalName, goal.m_goalType, DescribePoi(quest)));
        }

        return id;
    }

    /// <summary>True when the game's arrow file (poi.dat) has the quest: a quest the live game has.</summary>
    internal static bool IsLiveQuest(string questName)
        => !string.IsNullOrEmpty(questName) && GetPoiGoals() is { } poi && poi.ContainsKey(StringHash.Compute(questName));

    private static bool PoiHas(QuestTemplate quest, uint id)
        => id != 0 && GetPoiGoals() is { } poi
           && poi.TryGetValue(StringHash.Compute((string) quest.m_questName ?? ""), out var questPoi)
           && questPoi.ContainsKey(id);

    private static string DescribePoi(QuestTemplate quest) {
        var questHash = StringHash.Compute((string) quest.m_questName ?? "");
        if (GetPoiGoals() is not { } poi || !poi.TryGetValue(questHash, out var questPoi) || questPoi.Count == 0) {
            return "none";
        }

        return string.Join(", ", questPoi.Keys.Select(id => {
            var targets = TargetsOf(questHash, id);
            return targets.Count > 0 ? $"{id} -> {string.Join("/", targets)}" : id.ToString();
        }));
    }

    private static HashSet<uint> TargetsOf(uint questHash, uint goalId) {
        lock (s_lock) {
            return s_poiTargets.TryGetValue(((ulong) goalId << 32) | questHash, out var targets) ? targets : [];
        }
    }

    // The NPC a talk step is about, or 0 when the step is not a talk step or its NPC is not known.
    private static uint TalkTarget(GoalTemplate goal)
        => goal is PersonaGoalTemplate persona
            ? (uint) Imlight.CoreLib.Game.Madlibs.QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0)
            : 0u;

    /// <summary>
    /// The spots the game's quest arrow points at for this goal (zone and position), the goal's own zone first. Empty
    /// when poi.dat has none. The Manager's step buttons teleport there (Niduus, 2026-10-28: "TP me to EXACTLY the place").
    /// </summary>
    internal static List<(string Zone, Imcodec.Math.Vector3 Spot)> GetSpots(QuestTemplate quest, GoalTemplate goal) {
        if (quest is not null && goal is not null) {
            var destination = (string)goal.m_destinationZone;
            if (!string.IsNullOrEmpty(destination)
                && ResourceManager.TryLoadArchive(destination, out var instanceWad)) {
                var instanceSpot = GetExactPlacedSpot(destination, quest, goal);
                if (instanceSpot.Count > 0) {
                    return instanceSpot;
                }
            }
        }

        var id = GetGoalNameId(quest, goal);
        GetPoiGoals();
        if (id == 0 || quest is null) {
            return [];
        }

        var key = ((ulong) id << 32) | StringHash.Compute((string) quest.m_questName ?? "");
        lock (s_lock) {
            if (!s_poiSpots.TryGetValue(key, out var spots)) {
                return [];
            }

            string zone = goal.m_destinationZone;
            return [.. spots.OrderBy(s => string.Equals(s.Zone, zone, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        }
    }

    /// <summary>
    /// Exact placed-object fallback for active scavenge/usage goals. POI data names rebuilt goal steps by several
    /// possible IDs, while these goals carry the exact client tags of the object the player must use.
    /// </summary>
    private static string NormalizeInstanceTag(string tag)
        => tag?.EndsWith(" instance", StringComparison.OrdinalIgnoreCase) == true
            ? tag[..^" instance".Length]
            : tag;

    private static List<(string Zone, Vector3 Spot)> GetExactPlacedSpot(string zoneName, QuestTemplate quest, GoalTemplate goal) {
        var result = new List<(string Zone, Vector3 Spot)>();
        if (string.IsNullOrEmpty(zoneName) || quest is null || goal is null
            || !ResourceManager.TryLoadArchive(zoneName, out var wad)) {
            return result;
        }

        var data = ResourceManager.OpenZoneData(wad)?.ToArray();
        if (data is null
            || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zoneData)
            || zoneData?.m_objectList is null) {
            return result;
        }

        HashSet<string> tags = [];
        if (goal is ScavengeGoalTemplate scavenge) {
            tags = scavenge.m_clientTags?.Select(t => NormalizeInstanceTag((string)t)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }
        ulong personaTemplate = 0;
        if (goal is PersonaGoalTemplate persona) {
            personaTemplate = Imlight.CoreLib.Game.Madlibs.QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
        }

        foreach (var placed in zoneData.m_objectList) {
            if (placed is null) continue;
            if (personaTemplate != 0 && placed.m_templateID.Full == personaTemplate) {
                result.Add((zoneName, placed.m_location));
                continue;
            }
            var objectTag = NormalizeInstanceTag((string?)placed.m_zoneTag);
            if (string.IsNullOrEmpty(objectTag)) continue;
            if (tags.Any(tag => string.Equals(tag, objectTag, StringComparison.OrdinalIgnoreCase)
                               || objectTag.Contains(tag, StringComparison.OrdinalIgnoreCase)
                               || tag.Contains(objectTag, StringComparison.OrdinalIgnoreCase))) {
                result.Add((zoneName, placed.m_location));
            }
        }

        return result;
    }

    internal static (string Zone, Vector3 Spot, string Tag)? GetExactActiveSpot(Wizard wizard) {
        if (wizard?.QuestBehavior?.CurrentQuestInstances is null || string.IsNullOrEmpty(wizard.Zone)) {
            return null;
        }

        foreach (var instance in wizard.QuestBehavior.CurrentQuestInstances) {
            var quest = WizardData.Collections.QuestTemplateCollection.GetQuestByName(instance.QuestName);
            if (quest?.m_goals is null) {
                continue;
            }

            foreach (var progress in instance.GoalProgress ?? []) {
                if (!instance.IsGoalActive(progress.GoalName)) {
                    continue;
                }

                var goal = quest.m_goals.FirstOrDefault(g => g is not null && g.m_goalName == progress.GoalName);
                if (goal is not ScavengeGoalTemplate scavenge || scavenge.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE
                    || scavenge.m_clientTags is not { Count: > 0 }) {
                    continue;
                }

                var zone = (string)scavenge.m_destinationZone;
                if (string.IsNullOrEmpty(zone)
                    || !string.Equals(zone, wizard.Zone, StringComparison.OrdinalIgnoreCase)
                    || !ResourceManager.TryLoadArchive(zone, out var wad)) {
                    continue;
                }

                var data = ResourceManager.OpenZoneData(wad)?.ToArray();
                if (data is null
                    || !new BindSerializer().Deserialize<WizZoneData>(data, 1u, out var zoneData)
                    || zoneData?.m_objectList is null) {
                    continue;
                }

                var tags = scavenge.m_clientTags.Select(t => (string)t).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var exact = zoneData.m_objectList.FirstOrDefault(o => {
                    if (o is null) return false;
                    var objectTag = (string?)o.m_zoneTag;
                    if (string.IsNullOrEmpty(objectTag)) return false;
                    var normalized = NormalizeInstanceTag(objectTag);
                    return tags.Any(tag =>
                        string.Equals(NormalizeInstanceTag(tag), normalized, StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains(tag, StringComparison.OrdinalIgnoreCase)
                        || tag.Contains(normalized, StringComparison.OrdinalIgnoreCase));
                });
                if (exact is not null) {
                    return (wizard.Zone, exact.m_location, (string)exact.m_zoneTag);
                }
            }
        }

        return null;
    }

    private static Dictionary<string, uint> ResolveGoalIds(QuestTemplate quest) {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        var poi = GetPoiGoals();
        if (poi is null || !poi.TryGetValue(StringHash.Compute((string) quest.m_questName ?? ""), out var questPoi)) {
            return result;
        }

        var questHash = StringHash.Compute((string) quest.m_questName ?? "");
        var goals = quest.m_goals.Where(g => g is not null && !string.IsNullOrEmpty(g.m_goalName)
                                             && (g.m_goalNameID == 0 || !questPoi.ContainsKey(g.m_goalNameID))).ToList();
        var used = new HashSet<uint>(quest.m_goals.Where(g => g is not null && g.m_goalNameID != 0 && questPoi.ContainsKey(g.m_goalNameID))
            .Select(g => g.m_goalNameID));

        // The NPCs this quest's talk steps are about. A poi.dat step pointing at one of them belongs to that talk step.
        var talkNpcs = quest.m_goals.Select(TalkTarget).Where(t => t != 0).ToHashSet();

        // Whether a poi.dat step plainly points at something else than this goal: a talk step and a step at another
        // NPC, or a step that is not about talking and a step at an NPC this quest is talked to. Matching steps by their
        // place in the quest gave a step the arrow of the one after it whenever the game's quest had a step ours lacks
        // (Last Wood's Planning the Attack, 2026-11-25: the game has no Goal 2, so the talk step's arrow went astray).
        bool PointsElsewhere(GoalTemplate goal, uint poiId) {
            var targets = TargetsOf(questHash, poiId);
            if (targets.Count == 0) {
                return false;
            }

            var npc = TalkTarget(goal);
            return npc != 0 ? !targets.Contains(npc) && targets.Overlaps(talkNpcs) : targets.Overlaps(talkNpcs);
        }

        // A real goal name: the game keys the goal by its hash.
        foreach (var goal in goals.ToList()) {
            var hash = StringHash.Compute((string) goal.m_goalName);
            if (questPoi.ContainsKey(hash) && used.Add(hash)) {
                result[goal.m_goalName] = hash;
                goals.Remove(goal);
            }
        }

        // A talk step: the poi.dat step that points at its NPC, the one in the goal's zone when there are several.
        foreach (var goal in goals.ToList()) {
            var npc = TalkTarget(goal);
            if (npc == 0) {
                continue;
            }

            string zone = goal.m_destinationZone;
            var candidates = questPoi.Where(p => !used.Contains(p.Key) && TargetsOf(questHash, p.Key).Contains(npc))
                .OrderBy(p => !string.IsNullOrEmpty(zone) && p.Value.Contains(zone) ? 0 : 1)
                .Select(p => p.Key).ToList();
            if (candidates.Count > 0) {
                result[goal.m_goalName] = candidates[0];
                used.Add(candidates[0]);
                goals.Remove(goal);
            }
        }

        // A made-up goal name: the game's name for the step at its place in the quest ("Goal", "Goal 1", ...), when
        // poi.dat has that step in the goal's zone. Two or more steps in one zone (Zafaria's In The Cold Distance: the
        // tower and Captain Blackhoof, both in the Savannah) found no arrow at all by the zone alone: the arrow came up
        // and went away (2026-11-19).
        foreach (var goal in goals.ToList()) {
            string zone = goal.m_destinationZone;
            foreach (var gameName in Quests.VanillaGoalNames.GameNames(quest, (string) goal.m_goalName)) {
                var hash = StringHash.Compute(gameName);
                if (used.Contains(hash) || !questPoi.TryGetValue(hash, out var gameZones)
                    || (!string.IsNullOrEmpty(zone) && gameZones.Count > 0 && !gameZones.Contains(zone))
                    || PointsElsewhere(goal, hash)) {
                    continue;
                }

                result[goal.m_goalName] = hash;
                used.Add(hash);
                goals.Remove(goal);

                break;
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

                var candidates = questPoi.Where(p => !used.Contains(p.Key) && p.Value.Contains(zone) && !PointsElsewhere(goal, p.Key))
                    .Select(p => p.Key).ToList();
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
            var spots = new Dictionary<ulong, List<(string Zone, Imcodec.Math.Vector3 Spot)>>();
            var targets = new Dictionary<ulong, HashSet<uint>>();
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
                    var target = reader.ReadUInt64();
                    if (target != 0) {
                        if (!targets.TryGetValue(key, out var keyTargets)) {
                            targets[key] = keyTargets = [];
                        }

                        keyTargets.Add((uint) (target & 0xFFFF_FFFFUL));
                    }

                    var spot =new Imcodec.Math.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    reader.ReadBytes(2);
                    if (zone < zones.Length) {
                        if (!spots.TryGetValue(key, out var keySpots)) {
                            spots[key] = keySpots = [];
                        }

                        keySpots.Add((zones[zone], spot));
                    }

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
            s_poiSpots = spots;
            s_poiTargets = targets;

            return s_poiGoals;
        }
    }

}
