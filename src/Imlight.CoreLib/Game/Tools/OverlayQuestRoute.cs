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
using System.Linq;
using System.Text.Json.Nodes;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The way to the quest picked in the Quest Tracker, for the Minimap only (Niduus, 2026-11-12: "you can click the quest
/// and it will visually draw lines of where you have to go ... ONLY able to be on the Mini Map UI"). The minimap's op
/// "map" says which quest is picked; the map it gets back has "route": where the quest's steps are in this area (the
/// game's own quest-arrow spots, the NPC to talk to, the creatures to defeat), or, when the steps are in another area,
/// the door or teleporter here that leads that way. Only reads; nothing is changed.
/// </summary>
internal static class OverlayQuestRoute {

    private const int MAX_TARGETS = 8;

    /// <summary>The route of <paramref name="questName"/> for this wizard in zone actor <paramref name="zone"/>, or null when it isn't held.</summary>
    internal static JsonObject Build(Wizard wizard, IActorRef zone, string questName) {
        if (string.IsNullOrWhiteSpace(questName)) {
            return null;
        }

        var held = wizard.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));
        if (held is null || QuestTemplateCollection.GetQuestByName(held.QuestName) is not { } quest) {
            return null;
        }

        var here = wizard.Location;
        string zonePath = wizard.Zone ?? "";
        var live = MinimapIndex.In(zone);
        var targets = new List<(float X, float Y, string Name)>();
        var elsewhere = new List<string>();
        foreach (var progress in held.GoalProgress ?? []) {
            if (progress is null || !held.IsGoalActive(progress.GoalName)) {
                continue;
            }

            var goal = quest.m_goals?.FirstOrDefault(g => g is not null && (string) g.m_goalName == progress.GoalName);
            if (goal is null || goal.m_hideGoal) {
                continue;
            }

            var step = OverlayQuestTracker.StepTitle(goal);
            var found = false;
            foreach (var (spotZone, spot) in QuestArrowGoals.GetSpots(quest, goal)) {
                if (OverlayQuestTracker.SameZone(spotZone, zonePath)) {
                    targets.Add((spot.X, spot.Y, step));
                    found = true;
                }
            }

            if (!found && goal is PersonaGoalTemplate persona) {
                var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                if (npc != 0 && live.FirstOrDefault(m => m.Template == npc) is { } standing) {
                    targets.Add((standing.Object.m_location.X, standing.Object.m_location.Y, step));
                    found = true;
                }
            }

            if (!found && goal is BountyGoalTemplate bounty) {
                var adjectives = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                if (adjectives.Length > 0) {
                    foreach (var mob in live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                                        && (m.Adjectives.Any(a => adjectives.Contains(a, StringComparer.Ordinal)) || Quests.BountyStandIns.Counts(adjectives, m.Template)))
                                 .OrderBy(m => Far(here.X, here.Y, m.Object.m_location.X, m.Object.m_location.Y)).Take(3)) {
                        targets.Add((mob.Object.m_location.X, mob.Object.m_location.Y, step));
                        found = true;
                    }
                }
            }

            var stepZone = OverlayQuestTracker.StepZone(goal);
            if (!found && stepZone.Length > 0 && !OverlayQuestTracker.SameZone(stepZone, zonePath)) {
                elsewhere.Add(stepZone);
            }
        }

        var route = new JsonObject {
            ["quest"] = held.QuestName,
            ["t"] = OverlayQuestTracker.English(quest.m_questTitle, held.QuestName),
            ["k"] = QuestStory.KindOf(quest),
        };

        // Nothing to do here: the way out toward the step's area, exit by exit (the tracker lists the areas; the minimap
        // draws the line to the exit here that starts it: a door, a walk-through zone exit, or the Spiral Door).
        if (targets.Count == 0 && elsewhere.Count > 0) {
            var exits = ExitsHere(zonePath, live);
            var spiralDoors = live.Where(m => m.Template == SPIRAL_DOOR).ToList();
            var hops = Way(zonePath, elsewhere[0], spiralDoors.Count > 0, exits.Select(e => e.To));
            route["to"] = ZoneNames.Shown(elsewhere[0]);
            if (hops.Count > 0) {
                var (next, world) = hops[0];
                if (world && spiralDoors.Count > 0) {
                    var door = spiralDoors.OrderBy(m => Far(here.X, here.Y, m.Object.m_location.X, m.Object.m_location.Y)).First();
                    targets.Add((door.Object.m_location.X, door.Object.m_location.Y, "Spiral Door"));
                }
                else {
                    var nextWorld = next.Split('/')[0];
                    var exit = exits.Where(e => OverlayQuestTracker.SameZone(next, e.To) || OverlayQuestTracker.SameZone(e.To, next))
                        .OrderBy(e => Far(here.X, here.Y, e.X, e.Y)).FirstOrDefault();
                    // No exit here goes there on record: one into that world, else the one toward the most places.
                    exit ??= exits.Where(e => string.Equals(e.To.Split('/')[0], nextWorld, StringComparison.OrdinalIgnoreCase)
                                              && !string.Equals(e.To.Split('/')[0], zonePath.Split('/')[0], StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => Far(here.X, here.Y, e.X, e.Y)).FirstOrDefault();
                    exit ??= exits.OrderByDescending(e => (s_arrivals ?? []).Contains(e.To) ? 1 : 0)
                        .ThenByDescending(e => Links().GetValueOrDefault(e.To)?.Count ?? 0)
                        .ThenBy(e => Far(here.X, here.Y, e.X, e.Y)).FirstOrDefault();
                    if (exit is not null) {
                        targets.Add((exit.X, exit.Y, "To " + ZoneNames.Shown(exit.To)));
                    }
                }
            }
        }

        var points = new JsonArray();
        foreach (var (x, y, name) in targets.DistinctBy(t => ((int) (t.X / 50), (int) (t.Y / 50)))
                     .OrderBy(t => Far(here.X, here.Y, t.X, t.Y)).Take(MAX_TARGETS)) {
            points.Add(new JsonObject { ["x"] = Round(x), ["y"] = Round(y), ["n"] = name.Length > 60 ? name[..60] : name });
        }

        route["points"] = points;

        // The line along the streets to the nearest one.
        if (points.Count > 0 && points[0] is JsonObject nearest) {
            var walk = new JsonArray();
            foreach (var (x, y) in OverlayQuestPath.Walk(wizard.CharId, zonePath, here.X, here.Y, (int) nearest["x"], (int) nearest["y"])) {
                walk.Add(new JsonArray(Round(x), Round(y)));
            }

            route["path"] = walk;
        }

        return route;
    }

    // ---- The areas to go through: every area's doors and exits (SpiralDB's zone data), and the Spiral Door between
    // worlds (it stands where each world's travellers arrive), searched breadth first ----

    /// <summary>The Spiral Door's object (it takes you to any world's arrival area).</summary>
    internal const ulong SPIRAL_DOOR = 84113;

    private static Dictionary<string, HashSet<string>> s_links;
    private static HashSet<string> s_arrivals;
    private static readonly System.Threading.Lock s_lock = new();

    /// <summary>
    /// The areas after <paramref name="from"/> on the way to <paramref name="to"/>, the last being the step's area; World
    /// is true for a trip through the Spiral Door. <paramref name="exitsHere"/> are the areas this area's exits lead to
    /// (doors and walk-through exits, read from the zone itself). When the whole way isn't on record it still starts the
    /// right way: toward the step's world's arrival area, else toward this world's Spiral Door, then on from there.
    /// </summary>
    internal static List<(string Zone, bool World)> Way(string from, string to, bool spiralDoorHere, IEnumerable<string> exitsHere = null) {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) {
            return [];
        }

        Links();
        var arrivals = s_arrivals ?? [];
        var extra = (exitsHere ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        var fromWorld = from.Split('/')[0];
        var toWorld = to.Split('/')[0];
        var otherWorld = !string.Equals(fromWorld, toWorld, StringComparison.OrdinalIgnoreCase);
        var toArrival = arrivals.FirstOrDefault(a => string.Equals(a.Split('/')[0], toWorld, StringComparison.OrdinalIgnoreCase));
        var homeArrival = arrivals.FirstOrDefault(a => string.Equals(a.Split('/')[0], fromWorld, StringComparison.OrdinalIgnoreCase));

        // The whole way; else to the step's world's arrival area, then the step; else to this world's Spiral Door, then
        // through it to the step's world, then the step.
        if (Walk(from, to, spiralDoorHere, extra) is { } whole) {
            return whole;
        }

        if (otherWorld && toArrival is not null && Walk(from, toArrival, spiralDoorHere, extra) is { } toWorldFirst) {
            toWorldFirst.Add((to, false));

            return toWorldFirst;
        }

        if (otherWorld && toArrival is not null && homeArrival is not null && !spiralDoorHere
            && Walk(from, homeArrival, false, extra) is { Count: > 0 } toDoor) {
            toDoor.Add((toArrival, true));
            toDoor.Add((to, false));

            return toDoor;
        }

        // Nothing on record: through the Spiral Door to the step's world first, when it is another world, then the step.
        var hopsGuess = new List<(string, bool)>();
        if (otherWorld && toArrival is not null) {
            hopsGuess.Add((toArrival, true));
        }

        if (hopsGuess.Count == 0 || !OverlayQuestTracker.SameZone(to, hopsGuess[^1].Item1)) {
            hopsGuess.Add((to, false));
        }

        return hopsGuess;
    }

    // The areas from `from` to `goal` (excluded `from`), one step at a time toward the fewest areas left; null when no
    // way is on record.
    private static List<(string Zone, bool World)> Walk(string from, string goal, bool spiralDoorHere, List<string> extra) {
        if (OverlayQuestTracker.SameZone(goal, from)) {
            return [];
        }

        var left = AreasLeft(goal);
        var arrivals = s_arrivals ?? [];
        var hops = new List<(string, bool)>();
        var at = from;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        for (var guard = 0; guard < 60; guard++) {
            var first = hops.Count == 0;
            (string Zone, bool World, int Left) best = (null, false, int.MaxValue);
            void Consider(string next, bool world) {
                if (seen.Contains(next)) {
                    return;
                }

                var count = OverlayQuestTracker.SameZone(goal, next) ? 0 : left.GetValueOrDefault(next, int.MaxValue);
                if (count < best.Left) {
                    best = (next, world, count);
                }
            }

            foreach (var next in Links().GetValueOrDefault(at) ?? []) {
                Consider(next, false);
            }

            if (first) {
                foreach (var next in extra) {
                    Consider(next, false);
                }
            }

            if (arrivals.Contains(at) || (first && spiralDoorHere)) {
                foreach (var next in arrivals) {
                    Consider(next, true);
                }
            }

            if (best.Zone is null) {
                return null;
            }

            hops.Add((best.Zone, best.World));
            if (best.Left == 0) {
                return hops;
            }

            seen.Add(best.Zone);
            at = best.Zone;
        }

        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, int>> s_left = new(StringComparer.OrdinalIgnoreCase);

    // How many areas are left from each area to `goal` (searched backwards from it over the exits on record and the
    // Spiral Door between arrival areas).
    private static Dictionary<string, int> AreasLeft(string goal) => s_left.GetOrAdd(goal, g => {
        var back = Backward();
        var arrivals = s_arrivals ?? [];
        var left = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Queue<string>();
        foreach (var start in new[] { g, AccessPassManager.GetContainedZoneName(g) }.Where(z => !string.IsNullOrEmpty(z))) {
            if (left.TryAdd(start, 0)) {
                waiting.Enqueue(start);
            }
        }

        while (waiting.Count > 0 && left.Count < 20000) {
            var zone = waiting.Dequeue();
            var count = left[zone] + 1;
            foreach (var before in back.GetValueOrDefault(zone) ?? []) {
                if (left.TryAdd(before, count)) {
                    waiting.Enqueue(before);
                }
            }

            if (arrivals.Contains(zone)) {
                foreach (var before in arrivals) {
                    if (left.TryAdd(before, count)) {
                        waiting.Enqueue(before);
                    }
                }
            }
        }

        return left;
    });

    private static Dictionary<string, HashSet<string>> s_backward;

    private static Dictionary<string, HashSet<string>> Backward() {
        lock (s_lock) {
            if (s_backward is not null) {
                return s_backward;
            }
        }

        var back = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (zone, nexts) in Links()) {
            foreach (var next in nexts) {
                if (!back.TryGetValue(next, out var set)) {
                    back[next] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(zone);
            }
        }

        lock (s_lock) {
            return s_backward ??= back;
        }
    }

    // ---- This area's exits: its doors (live) and its walk-through exits (the zone's own volumes and triggers, with
    // the destinations SpiralDB recorded for them, as the server's zones use them) ----

    internal sealed record Exit(float X, float Y, string To);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Exit>> s_walkExits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exits in this area and where they go: the doors standing here and the walk-through exits.</summary>
    internal static List<Exit> ExitsHere(string zonePath, List<MinimapIndex.Mark> live) {
        var exits = new List<Exit>();
        foreach (var door in live.Where(m => m.Kind is MinimapIndex.DOOR or MinimapIndex.SIGIL && !string.IsNullOrEmpty(m.To))) {
            exits.Add(new Exit(door.Object.m_location.X, door.Object.m_location.Y, door.To.Trim()));
        }

        if (!string.IsNullOrEmpty(zonePath)) {
            exits.AddRange(s_walkExits.GetOrAdd(zonePath, WalkExits));
        }

        return [.. exits.Where(e => !string.Equals(e.To, zonePath, StringComparison.OrdinalIgnoreCase))];
    }

    private static List<Exit> WalkExits(string zonePath) {
        var exits = new List<Exit>();
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)) {
                return exits;
            }

            var volumes = Read<WizZoneVolumes>(wad.OpenFile("volumes.xml")?.ToArray())?.m_volumes?.Where(v => v is not null).ToList() ?? [];
            var triggers = Read<WizZoneTriggers>(wad.OpenFile("triggers.xml")?.ToArray())?.m_triggers?.Where(t => t is not null).ToList() ?? [];
            var recorded = ZoneDataCollection.GetZoneData(zonePath)?.Teleports ?? [];
            foreach (var trigger in triggers) {
                string name = trigger.m_triggerName;
                string to = recorded.FirstOrDefault(t => t?.TriggerName == name)?.Teleport?.m_destinationZone;
                if (string.IsNullOrWhiteSpace(to)) {
                    to = trigger.m_results?.m_results?.OfType<ResTeleport>().Select(r => (string) r.m_destinationZone).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z));
                }

                if (string.IsNullOrWhiteSpace(to) || trigger.m_fireEvents is null) {
                    continue;
                }

                foreach (var fire in trigger.m_fireEvents.Select(e => e.ToString()).Where(e => !string.IsNullOrEmpty(e))) {
                    foreach (var volume in volumes.Where(v => v.m_enterEvents?.Any(e => (string) e == fire) == true
                                                              || "Enter_" + (string) v.m_volumeName == fire)) {
                        exits.Add(new Exit(volume.m_locationX, volume.m_locationY, to.Trim()));
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Quest tracker: the exits of {0} could not be read: {1}", Logger.Args(zonePath, ex.Message));
        }

        return [.. exits.DistinctBy(e => ((int) (e.X / 100), (int) (e.Y / 100), e.To.ToLowerInvariant()))];
    }

    private static T Read<T>(byte[] data) where T : Imcodec.ObjectProperty.PropertyClass {
        if (data is null) {
            return null;
        }

        return new Imcodec.ObjectProperty.BindSerializer().Deserialize<T>(data, 1, out var result) ? result : null;
    }

    /// <summary>The way in words, for the Quest Tracker ("Ravenwood, Spiral Door to Grizzleheim, Savarstaad Pass").</summary>
    internal static JsonArray WayWords(string from, string to, bool spiralDoorHere, IEnumerable<string> exitsHere = null) {
        var words = new JsonArray();
        foreach (var (zone, world) in Way(from, to, spiralDoorHere, exitsHere).Take(12)) {
            var shown = ZoneNames.Shown(zone);
            words.Add(world ? $"Spiral Door to {WorldName(zone)}" + (string.Equals(shown, WorldName(zone), StringComparison.OrdinalIgnoreCase) ? "" : $" ({shown})") : shown);
        }

        return words;
    }

    private static string WorldName(string zone) => OverlayQuestTracker.WorldName(zone);

    private static Dictionary<string, HashSet<string>> Links() {
        lock (s_lock) {
            if (s_links is not null) {
                return s_links;
            }
        }

        var links = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (var zone in SpiralDB.GetAllZoneData()) {
                if (string.IsNullOrEmpty(zone?.ZoneName)) {
                    continue;
                }

                foreach (var teleport in zone.Teleports ?? []) {
                    string to = teleport?.Teleport?.m_destinationZone;
                    if (string.IsNullOrWhiteSpace(to) || string.Equals(to, zone.ZoneName, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (!links.TryGetValue(zone.ZoneName, out var set)) {
                        links[zone.ZoneName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    set.Add(to.Trim());
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest tracker: the areas' doors could not be read: {0}", Logger.Args(ex.Message));
        }

        var arrivals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (var hub in WorldHubZones.All()) {
                if (!string.IsNullOrWhiteSpace(hub?.m_universeTPZone)) {
                    arrivals.Add(((string) hub.m_universeTPZone).Trim());
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Quest tracker: the worlds' arrival areas could not be read: {0}", Logger.Args(ex.Message));
        }

        lock (s_lock) {
            s_arrivals ??= arrivals;
            s_links ??= links;

            return s_links;
        }
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    private static int Round(float value) => float.IsFinite(value) ? (int) MathF.Round(value) : 0;

}
