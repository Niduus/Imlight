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

        // Nothing to do here: the way out toward the step's area.
        if (targets.Count == 0 && elsewhere.Count > 0) {
            var goingTo = elsewhere[0];
            route["to"] = ZoneNames.Shown(goingTo);
            if (NextZone(zonePath, goingTo) is { } next) {
                var doorName = "To " + ZoneNames.Shown(next);
                var door = live.Where(m => m.Kind == MinimapIndex.DOOR && string.Equals(m.Name, doorName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => Far(here.X, here.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
                if (door is not null) {
                    targets.Add((door.Object.m_location.X, door.Object.m_location.Y, doorName));
                }
                else {
                    route["via"] = ZoneNames.Shown(next);
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

    // ---- Which area to go through next: every area's doors and exits (SpiralDB's zone data), searched breadth first ----

    private static Dictionary<string, HashSet<string>> s_links;
    private static readonly System.Threading.Lock s_lock = new();

    /// <summary>The area right after <paramref name="from"/> on the shortest way to <paramref name="to"/>, or null.</summary>
    internal static string NextZone(string from, string to) {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) {
            return null;
        }

        var links = Links();
        var cameFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = null };
        var waiting = new Queue<string>([from]);
        while (waiting.Count > 0 && cameFrom.Count < 20000) {
            var zone = waiting.Dequeue();
            if (OverlayQuestTracker.SameZone(to, zone)) {
                var step = zone;
                while (cameFrom[step] is { } before && !string.Equals(before, from, StringComparison.OrdinalIgnoreCase)) {
                    step = before;
                }

                return string.Equals(step, from, StringComparison.OrdinalIgnoreCase) ? null : step;
            }

            foreach (var next in links.GetValueOrDefault(zone) ?? []) {
                if (cameFrom.TryAdd(next, zone)) {
                    waiting.Enqueue(next);
                }
            }
        }

        // No known way: a door straight to it, if there is one.
        return to;
    }

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

        lock (s_lock) {
            s_links ??= links;

            return s_links;
        }
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    private static int Round(float value) => float.IsFinite(value) ? (int) MathF.Round(value) : 0;

}
