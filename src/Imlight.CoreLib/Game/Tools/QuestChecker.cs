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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Checks every main quest against the game's own zone files, so only the quests that need a person are left to play
/// (Niduus, 2026-10-27: "there is a LOT of quests to test ... I really don't want to quest all those 2k quests"). For
/// each step it reads the step's zone: a "talk to" NPC must stand there, a "defeat" step's creatures must spawn there,
/// a "go to" place must be one of the zone's areas, a "use" or "collect" object must be in it; and the quest's giver
/// must stand somewhere. The Manager's Quest Testing tab asks for it (quest-check-request.txt beside the server) and
/// shows quest-check.json from the ID lookup folder: quests that pass tick themselves. Nothing in the game changes.
/// </summary>
internal static class QuestChecker {

    internal const string FILE_NAME = "quest-check.json";
    internal const string PROGRESS_NAME = "quest-check-progress.txt";
    /// <summary>The Manager writes this file beside the server to start a check (ManagerCommands).</summary>
    internal const string REQUEST_FILE = "quest-check-request.txt";
    private const string NPC_ZONES_FILE = "Shops/npc-zones.json";
    private const string AUTO_START_TAG = "ImlightAutoStart";

    private static int s_running;

    private sealed record QuestResult(string Code, bool Ok, List<string> Problems, List<string> Unchecked, List<string> Notes);

    private sealed record CheckFile(string Written, int Zones, int ZonesNotRead, List<string> NotRead, List<QuestResult> Quests);

    // What a zone holds, from its files.
    private sealed class ZoneFacts {
        public readonly HashSet<ulong> Templates = [];
        public readonly HashSet<string> Names = new(StringComparer.Ordinal);
        public readonly HashSet<string> Adjectives = new(StringComparer.Ordinal);
        public readonly HashSet<string> Areas = new(StringComparer.Ordinal);
    }

    /// <summary>Starts a check on a low-priority thread. False when one is running already.</summary>
    internal static bool Start(string folder) {
        if (Interlocked.CompareExchange(ref s_running, 1, 0) != 0) {
            return false;
        }

        var thread = new Thread(() => {
            try {
                Run(folder);
            }
            catch (Exception ex) {
                Logger.Error("Quest checker stopped: {0}", Logger.Args(ex));
                Progress(folder, $"Stopped: {ex.Message}");
            }
            finally {
                Volatile.Write(ref s_running, 0);
            }
        }) {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Quest checker",
        };
        thread.Start();

        return true;
    }

    private static void Run(string folder) {
        var timer = Stopwatch.StartNew();
        _ = AccessPassManager.Zones;
        var quests = QuestTemplateCollection.GetAllQuests()
            .Where(q => q is not null && !string.IsNullOrEmpty(q.m_questName) && !Quests.QuestOptions.IsOff(q.m_questName))
            .Where(q => q.m_mainline || ((string) q.m_questName).Contains("MAIN", StringComparison.OrdinalIgnoreCase))
            .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Every zone a step happens in, read once.
        var zoneNames = quests.SelectMany(q => q.m_goals ?? []).Where(g => g is not null)
            .Select(ZoneOf).Where(z => z.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(z => z, StringComparer.OrdinalIgnoreCase).ToList();
        Logger.Information("Quest checker: checking {0} main quests, reading {1} zones.", Logger.Args(quests.Count, zoneNames.Count));

        var zones = new Dictionary<string, ZoneFacts>(StringComparer.OrdinalIgnoreCase);
        var notRead = new List<string>();
        for (var i = 0; i < zoneNames.Count; i++) {
            if (i % 10 == 0) {
                Progress(folder, $"Reading zone {i + 1} of {zoneNames.Count}");
            }

            var facts = ReadZone(zoneNames[i]);
            if (facts is null) {
                notRead.Add(zoneNames[i]);
            }
            else {
                zones[zoneNames[i]] = facts;
            }
        }

        var (extraByZone, extraAnywhere) = ReadExtraPlacements();
        foreach (var (zone, ids) in extraByZone) {
            if (zones.TryGetValue(zone, out var facts)) {
                foreach (var id in ids) {
                    AddTemplate(facts, id);
                }
            }
        }

        var anywhere = new ZoneFacts();
        foreach (var facts in zones.Values) {
            anywhere.Templates.UnionWith(facts.Templates);
            anywhere.Names.UnionWith(facts.Names);
        }

        foreach (var id in extraAnywhere) {
            AddTemplate(anywhere, id);
        }

        var npcZones = ReadNpcZones();

        // Every usage step some object of the read zones counts for.
        Progress(folder, "Checking quest steps");
        var usable = new HashSet<(string, string)>();
        foreach (var id in anywhere.Templates) {
            if (Template(id) is { } template) {
                foreach (var (quest, goal) in InteractQuestSelectComponent.UsageGoalsOf(template)) {
                    if (goal is not null) {
                        usable.Add((quest, (string) goal.m_goalName));
                    }
                }
            }
        }

        var results = quests.Select(q => Check(q, zones, anywhere, npcZones, usable)).ToList();
        var file = new CheckFile(DateTime.UtcNow.ToString("o"), zones.Count, notRead.Count, notRead, results);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FILE_NAME);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file));
        File.Move(path + ".tmp", path, overwrite: true);
        Progress(folder, $"Done: {results.Count(r => r.Ok)} of {results.Count} quests passed");
        Logger.Information("Quest checker: {0} of {1} main quests passed, {2} have problems; {3} zones could not be read ({4:0} s).",
            Logger.Args(results.Count(r => r.Ok), results.Count, results.Count(r => r.Problems.Count > 0), notRead.Count,
                timer.Elapsed.TotalSeconds));
    }

    private static QuestResult Check(QuestTemplate quest, Dictionary<string, ZoneFacts> zones, ZoneFacts anywhere,
                                     Dictionary<ulong, List<string>> npcZones, HashSet<(string, string)> usable) {
        var problems = new List<string>();
        var unchecked = new List<string>();
        var notes = new List<string>();
        string questName = quest.m_questName;

        try {
            // The giver must stand somewhere.
            var giver = QuestMadlibs.GiverTemplateOf(quest);
            if (giver != 0 && !IsPlaced(giver, anywhere, npcZones)) {
                problems.Add($"The quest giver {NameOf(giver)} doesn't stand anywhere in the game's zones.");
            }
            else if (giver == 0 && !Quests.QuestOptions.StartsByItself(questName)
                     && quest.m_clientTags?.Any(t => (string) t == AUTO_START_TAG) != true) {
                notes.Add("Nobody offers it: another quest or a game event has to hand it out.");
            }

            var number = 0;
            foreach (var goal in quest.m_goals ?? []) {
                if (goal is null) {
                    continue;
                }

                number++;
                if (Quests.QuestOptions.SkipsGoal(questName, (string) goal.m_goalName)) {
                    continue;
                }

                var step = $"Step {number} ({StepTitle(goal)})";
                var zoneName = ZoneOf(goal);
                var rawZone = RawZoneOf(goal);
                if (zoneName.Length == 0 && !string.IsNullOrWhiteSpace(rawZone)) {
                    problems.Add($"{step}: its place \"{rawZone}\" isn't a zone the server has.");
                    continue;
                }

                var zone = zoneName.Length > 0 ? zones.GetValueOrDefault(zoneName) : null;
                if (zoneName.Length > 0 && zone is null) {
                    unchecked.Add($"{step}: the zone {zoneName} could not be read.");
                    continue;
                }

                switch (goal) {
                    case PersonaGoalTemplate persona:
                        CheckPersona(step, persona, zoneName, zone, anywhere, npcZones, problems);
                        break;
                    case BountyGoalTemplate bounty:
                        CheckBounty(step, questName, bounty, zoneName, zone, zones, problems, notes);
                        break;
                    case WaypointGoalTemplate waypoint:
                        string area = waypoint.m_proximityTag;
                        if (!string.IsNullOrEmpty(area) && zone is not null && !zone.Areas.Contains(area)) {
                            problems.Add($"{step}: {zoneName} has no area \"{area}\" to walk into.");
                        }
                        break;
                    case ScavengeGoalTemplate scavenge when goal.m_goalType == GOAL_TYPE.GOAL_TYPE_SCAVENGE:
                        var wanted = (scavenge.m_itemAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToList();
                        if (wanted.Count > 0 && !wanted.Any((zone ?? anywhere).Adjectives.Contains)
                            && !usable.Contains((questName, (string) goal.m_goalName))) {
                            problems.Add($"{step}: nothing to pick up{In(zoneName)} is marked {string.Join(" or ", wanted)}.");
                        }
                        break;
                    default:
                        if (goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE) {
                            if (!usable.Contains((questName, (string) goal.m_goalName))) {
                                problems.Add($"{step}: no object in the game's zones can be used for it.");
                            }
                        }
                        else if (goal.m_goalType != GOAL_TYPE.GOAL_TYPE_COMPLETEQUEST) {
                            unchecked.Add($"{step}: a {goal.m_goalType.ToString().Replace("GOAL_TYPE_", "").ToLowerInvariant()} step, which the computer can't check.");
                        }
                        break;
                }
            }
        }
        catch (Exception ex) {
            unchecked.Add($"The check stopped on this quest: {ex.Message}");
        }

        return new QuestResult(questName, problems.Count == 0 && unchecked.Count == 0, problems, unchecked, notes);
    }

    private static void CheckPersona(string step, PersonaGoalTemplate persona, string zoneName, ZoneFacts zone, ZoneFacts anywhere,
                                     Dictionary<ulong, List<string>> npcZones, List<string> problems) {
        string personaName = persona.m_personaName;
        var template = QuestMadlibs.SpeakerTemplateOf(personaName, 0);
        bool HasIn(ZoneFacts facts) => facts is not null
            && ((template != 0 && facts.Templates.Contains(template)) || facts.Names.Any(n => IsPersonaOf(personaName, n)));

        if (zone is not null && HasIn(zone)) {
            return;
        }

        var who = template != 0 ? NameOf(template) : personaName;
        var elsewhere = template != 0 ? npcZones.GetValueOrDefault(template) : null;
        if (zone is null && (HasIn(anywhere) || elsewhere is { Count: > 0 })) {
            return;
        }

        if (template == 0 && !HasIn(anywhere)) {
            problems.Add($"{step}: no NPC called {personaName} is in the game files.");
        }
        else if (elsewhere is { Count: > 0 }) {
            problems.Add($"{step}: {who} isn't in {zoneName}; the game has them in {string.Join(", ", elsewhere.Take(3))}.");
        }
        else {
            problems.Add($"{step}: {who} doesn't stand in {(zone is null ? "any zone" : zoneName)}.");
        }
    }

    private static void CheckBounty(string step, string questName, BountyGoalTemplate bounty, string zoneName, ZoneFacts zone,
                                    Dictionary<string, ZoneFacts> zones, List<string> problems, List<string> notes) {
        if (Quests.QuestOptions.AnyCreatureCounts(questName, (string) bounty.m_goalName)) {
            return;
        }

        var wanted = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToList();
        if (wanted.Count == 0) {
            problems.Add($"{step}: it names no kind of creature, so no battle counts.");
            return;
        }

        if (zone is not null && wanted.Any(zone.Adjectives.Contains)) {
            return;
        }

        var others = zones.Where(z => wanted.Any(z.Value.Adjectives.Contains)).Select(z => z.Key).Take(3).ToList();
        if (zone is null) {
            if (others.Count == 0) {
                problems.Add($"{step}: no creature in the quest's zones counts for it (it needs one marked {string.Join(" or ", wanted)}).");
            }

            return;
        }

        problems.Add($"{step}: no creature in {zoneName} counts for it (it needs one marked {string.Join(" or ", wanted)})"
            + (others.Count > 0 ? $"; some in {string.Join(", ", others)} do." : "."));
    }

    // ---------------------------------------------------------------------
    // Reading the zones
    // ---------------------------------------------------------------------

    private static ZoneFacts ReadZone(string zonePath) {
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) {
                return null;
            }

            var facts = new ZoneFacts();
            var zoneData = Read<WizZoneData>(wad.OpenFile("gamedata.bin")?.ToArray());
            foreach (var info in zoneData?.m_objectList ?? []) {
                if (info is not null) {
                    AddTemplate(facts, info.m_templateID);
                }
            }

            var spawnData = Read<SpawnManager>(wad.OpenFile("spawnData.xml")?.ToArray());
            foreach (var spawner in spawnData?.m_spawners ?? []) {
                foreach (var spawn in spawner?.m_spawnList ?? []) {
                    if (spawn?.m_objectInfo is { } info) {
                        AddTemplate(facts, info.m_templateID);
                    }
                }
            }

            var volumes = Read<WizZoneVolumes>(wad.OpenFile("volumes.xml")?.ToArray());
            foreach (var volume in volumes?.m_volumes ?? []) {
                if (volume is null) {
                    continue;
                }

                if ((string) volume.m_volumeName is { Length: > 0 } name) {
                    facts.Areas.Add(name);
                }

                foreach (var enter in volume.m_enterEvents ?? []) {
                    if ((string) enter is { Length: > 0 } enterEvent) {
                        facts.Areas.Add(enterEvent);
                    }
                }
            }

            return zoneData is null && spawnData is null ? null : facts;
        }
        catch (Exception ex) {
            Logger.Debug("Quest checker: zone {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));

            return null;
        }
    }

    private static T Read<T>(byte[] data) where T : PropertyClass {
        if (data is null) {
            return null;
        }

        return new BindSerializer().Deserialize<T>(data, 1, out var result) ? result : null;
    }

    private static void AddTemplate(ZoneFacts facts, ulong id) {
        if (id == 0 || !facts.Templates.Add(id) || Template(id) is not { } template) {
            return;
        }

        if ((string) template.m_objectName is { Length: > 0 } name) {
            facts.Names.Add(name);
        }

        foreach (var adjective in template.m_adjectiveList ?? []) {
            if ((string) adjective is { Length: > 0 } text) {
                facts.Adjectives.Add(text);
            }
        }
    }

    private static GameObjectTemplate Template(ulong id) {
        try {
            return CoreObjectFactory.GetCoreTemplate(id) as GameObjectTemplate;
        }
        catch (Exception) {
            return null;
        }
    }

    // What the server adds to zones itself (spiraldb-extra ZoneObjects and the Customs tab's NPCs and placements):
    // every "TemplateID" of each file, by the file's "Zone" when it names one.
    private static (Dictionary<string, List<ulong>> ByZone, List<ulong> Anywhere) ReadExtraPlacements() {
        var byZone = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
        var anywhere = new List<ulong>();
        foreach (var folderName in new[] { "ZoneObjects", "Customs" }) {
            var dir = Path.Combine(SpiralDB.ExtraPath, folderName);
            if (!Directory.Exists(dir)) {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.json")) {
                try {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var root = document.RootElement;
                    var ids = new List<ulong>();
                    CollectTemplateIds(root, ids, 0);
                    var zone = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Zone", out var z)
                        && z.ValueKind == JsonValueKind.String ? AccessPassManager.GetContainedZoneName(z.GetString() ?? "") : null;
                    if (zone is null) {
                        anywhere.AddRange(ids);
                    }
                    else {
                        if (!byZone.TryGetValue(zone, out var list)) {
                            byZone[zone] = list = [];
                        }

                        list.AddRange(ids);
                        anywhere.AddRange(ids);
                    }
                }
                catch (Exception) {
                    // A file being written, or not a placement file: left out.
                }
            }
        }

        return (byZone, anywhere);
    }

    private static void CollectTemplateIds(JsonElement element, List<ulong> ids, int depth) {
        if (depth > 8) {
            return;
        }

        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) {
                    if (property.Name.Equals("TemplateID", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetUInt64(out var id)) {
                        ids.Add(id);
                    }
                    else {
                        CollectTemplateIds(property.Value, ids, depth + 1);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) {
                    CollectTemplateIds(item, ids, depth + 1);
                }
                break;
        }
    }

    // Where the live game places each NPC (spiraldb-extra/Shops/npc-zones.json, from Wizard404): template id to zones.
    private static Dictionary<ulong, List<string>> ReadNpcZones() {
        var result = new Dictionary<ulong, List<string>>();
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SpiralDB.ExtraPath, NPC_ZONES_FILE)));
            var names = document.RootElement.GetProperty("Zones").EnumerateArray().Select(z => z[0].GetString() ?? "").ToList();
            foreach (var npc in document.RootElement.GetProperty("Npcs").EnumerateObject()) {
                if (ulong.TryParse(npc.Name, out var id)) {
                    result[id] = [.. npc.Value.EnumerateArray().Select(i => i.GetInt32()).Where(i => i >= 0 && i < names.Count).Select(i => names[i])];
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest checker: the NPC places could not be read: {0}", Logger.Args(ex.Message));
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // The zone a step happens in, as the server names it, or "".
    private static string ZoneOf(GoalTemplate goal) {
        var zone = RawZoneOf(goal).Trim();

        return zone.Length == 0 ? "" : AccessPassManager.GetContainedZoneName(zone) ?? "";
    }

    // A "go to" step's zone tag, or the step's destination zone.
    private static string RawZoneOf(GoalTemplate goal) {
        string tag = goal is WaypointGoalTemplate waypoint ? waypoint.m_zoneTag : null;
        string destination = goal.m_destinationZone;

        return string.IsNullOrEmpty(tag) ? destination ?? "" : tag;
    }

    private static bool IsPlaced(ulong template, ZoneFacts anywhere, Dictionary<ulong, List<string>> npcZones)
        => anywhere.Templates.Contains(template)
            || npcZones.ContainsKey(template)
            || (Template(template) is { } t && (string) t.m_objectName is { Length: > 0 } name && anywhere.Names.Contains(name));

    private const string PERSONA_SUFFIX = "_Persona";

    private static bool IsPersonaOf(string personaName, string npcName)
        => !string.IsNullOrEmpty(npcName)
            && (string.Equals(personaName, npcName, StringComparison.Ordinal)
                || string.Equals(personaName, npcName + PERSONA_SUFFIX, StringComparison.Ordinal));

    private static string NameOf(ulong id) {
        if (Template(id) is not { } npc) {
            return id.ToString();
        }

        string displayKey = npc.m_displayName;
        var name = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

        return string.IsNullOrEmpty(name) ? (string) npc.m_objectName ?? id.ToString() : name;
    }

    private static string StepTitle(GoalTemplate goal) {
        string key = goal.m_goalTitle;
        var title = string.IsNullOrEmpty(key) || key.Contains(' ') ? key : Locale.GetEnglishName(key);

        return string.IsNullOrEmpty(title) ? (string) goal.m_goalName ?? "" : title.Replace('\n', ' ').Trim();
    }

    private static string In(string zoneName) => zoneName.Length > 0 ? $" in {zoneName}" : "";

    private static void Progress(string folder, string text) {
        try {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, PROGRESS_NAME), $"{DateTime.UtcNow:o}\t{text}");
        }
        catch (Exception) {
            // Only the Manager's progress line; the check goes on.
        }
    }

}
