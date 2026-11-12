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

        // Nothing to do here: the way out toward the step's area, door by door (the tracker lists the areas; the minimap
        // draws the line to the door here that starts it).
        if (targets.Count == 0 && elsewhere.Count > 0) {
            var hops = Way(zonePath, elsewhere[0], live.Any(m => m.Template == SPIRAL_DOOR));
            route["to"] = ZoneNames.Shown(elsewhere[0]);
            if (hops.Count > 0) {
                var (next, world) = hops[0];
                var doorName = "To " + ZoneNames.Shown(next);
                var door = live.Where(m => world ? m.Template == SPIRAL_DOOR
                                             : m.Kind == MinimapIndex.DOOR && string.Equals(m.Name, doorName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => Far(here.X, here.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
                if (door is not null) {
                    targets.Add((door.Object.m_location.X, door.Object.m_location.Y, world ? "Spiral Door" : doorName));
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
    /// The areas after <paramref name="from"/> on the shortest way to <paramref name="to"/>, the last being the step's area;
    /// World is true for a trip through the Spiral Door. Empty when no way is known.
    /// </summary>
    internal static List<(string Zone, bool World)> Way(string from, string to, bool spiralDoorHere) {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) {
            return [];
        }

        var links = Links();
        var arrivals = s_arrivals ?? [];
        var cameFrom = new Dictionary<string, (string Zone, bool World)>(StringComparer.OrdinalIgnoreCase) { [from] = (null, false) };
        var waiting = new Queue<string>([from]);
        while (waiting.Count > 0 && cameFrom.Count < 20000) {
            var zone = waiting.Dequeue();
            if (OverlayQuestTracker.SameZone(to, zone)) {
                var hops = new List<(string, bool)>();
                for (var at = zone; cameFrom[at].Zone is { } before; at = before) {
                    hops.Add((at, cameFrom[at].World));
                }

                hops.Reverse();

                return hops;
            }

            foreach (var next in links.GetValueOrDefault(zone) ?? []) {
                if (cameFrom.TryAdd(next, (zone, false))) {
                    waiting.Enqueue(next);
                }
            }

            // The Spiral Door: from any world's arrival area (or here, when one stands here) to every other's.
            if (arrivals.Contains(zone) || (spiralDoorHere && string.Equals(zone, from, StringComparison.OrdinalIgnoreCase))) {
                foreach (var next in arrivals) {
                    if (cameFrom.TryAdd(next, (zone, true))) {
                        waiting.Enqueue(next);
                    }
                }
            }
        }

        // No way known through the doors on record: through the Spiral Door to the step's world first, when it is another
        // world, then the step's area.
        var hopsGuess = new List<(string, bool)>();
        var toWorld = to.Split('/')[0];
        if (!string.Equals(from.Split('/')[0], toWorld, StringComparison.OrdinalIgnoreCase)
            && arrivals.FirstOrDefault(a => string.Equals(a.Split('/')[0], toWorld, StringComparison.OrdinalIgnoreCase)) is { } arrival) {
            hopsGuess.Add((arrival, true));
        }

        if (hopsGuess.Count == 0 || !OverlayQuestTracker.SameZone(to, hopsGuess[^1].Item1)) {
            hopsGuess.Add((to, false));
        }

        return hopsGuess;
    }

    /// <summary>The way in words, for the Quest Tracker ("Ravenwood, Spiral Door to Grizzleheim, Savarstaad Pass").</summary>
    internal static JsonArray WayWords(string from, string to, bool spiralDoorHere) {
        var words = new JsonArray();
        foreach (var (zone, world) in Way(from, to, spiralDoorHere).Take(12)) {
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
