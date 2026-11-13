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
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Where the Quest Team goes next and what it does there, from what a player can know: the quest book (each wizard's
/// quests and steps), the quest arrow's spots, the NPCs and creatures standing in the area, and the area's doors and
/// exits (the same reading the Quest Tracker's line uses, OverlayQuestRoute). Only reads.
/// </summary>
internal static class QuestTeamPlanner {

    internal const string TALK = "talk";
    internal const string OFFER = "offer";
    internal const string SPOT = "spot";
    internal const string FIGHT = "fight";
    internal const string USE = "use";
    internal const string EXIT = "exit";
    internal const string DOOR = "door";
    internal const string SPIRAL = "spiral";
    internal const string TRAIN = "train";
    internal const string SHOP = "shop";
    internal const string STONE = "stone";
    internal const string NONE = "none";

    /// <summary>
    /// Where to go and what to do there. <paramref name="Problem"/> is set when the step can't be done from what is in the
    /// game (no such NPC, no creature, no way there): the quest is broken there.
    /// </summary>
    internal sealed record Target(string Kind, float X, float Y, string What, string Quest = "", string Goal = "", ulong Template = 0,
                                  ulong GlobalId = 0, string To = "", string Problem = "", string Key = "") {

        /// <summary>The same target as another (so a wizard on its way there is not sent again).</summary>
        internal bool Same(Target other) => other is not null && other.Kind == Kind && other.Key == Key
                                            && MathF.Abs(other.X - X) < 60 && MathF.Abs(other.Y - Y) < 60;
    }

    // The main story's worlds (Niduus, 2026-11-12: "I want them in Main Story worlds. Not Grizzle, Wintertusk or the Academy
    // world", "Wizard City, Krok, Marlybone, Mooshu, Dragonspyre, these types of worlds"), by their zone folder.
    private static readonly HashSet<string> s_storyWorlds = new(StringComparer.OrdinalIgnoreCase) {
        "WizardCity", "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Celestia", "Zafaria", "Avalon", "Azteca", "Khrysalis",
        "Arcanum", "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria", "Novus", "Wallaru", "Selenopolis", "Ahmarra",
    };

    // Quest codes of the side worlds (Grizzleheim, Wintertusk, Wysteria's academy, Aquila, Darkmoor).
    private static readonly HashSet<string> s_sideWorldCodes = new(StringComparer.OrdinalIgnoreCase) { "GH", "WT", "PA", "WY", "AQ", "DM" };

    // The story's worlds in the order they are played (Arcanum is a hub, not a step of the ladder).
    private static readonly string[] s_ladder = [
        "WizardCity", "Krokotopia", "Marleybone", "MooShu", "DragonSpire", "Celestia", "Zafaria", "Avalon", "Azteca", "Khrysalis",
        "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria", "Novus", "Wallaru", "Selenopolis", "Ahmarra",
    ];

    /// <summary>
    /// Whether the leader has played the last world of the story this server's data has (Niduus, 2026-11-12: "Stop. But
    /// that's assuming they finish ... the Ladder depends on the data at the time"): a story quest given there is done.
    /// </summary>
    internal static bool FinishedLastWorld(Wizard leader, out string world) {
        world = "";
        var mains = QuestService.QuestFinderGivers().Where(g => QuestStory.IsMain(g.Quest) && IsMainStoryWorld(g.Quest))
            .Select(g => (Quest: g.Quest, World: QuestGuide.GiverZone(g.Quest).Split('/')[0])).ToList();
        world = s_ladder.LastOrDefault(w => mains.Any(m => string.Equals(m.World, w, StringComparison.OrdinalIgnoreCase))) ?? "";
        var last = world;

        return last.Length > 0 && mains.Any(m => string.Equals(m.World, last, StringComparison.OrdinalIgnoreCase)
                                                 && leader.HasCompletedQuest(m.Quest.m_questName));
    }

    /// <summary>
    /// A street teleporter that brings the team much closer to a story quest's goal in this area (Niduus, 2026-11-12: "Bots
    /// can use teleporters on streets if it gets them closer to their objective for the STORY quest. This is the only type
    /// of teleportation that can be used"), or null. <paramref name="avoid"/>: stones just used, never used straight back.
    /// </summary>
    internal static Target Shortcut(QuestTeamMember.Status leader, Target goal, ICollection<ulong> avoid) {
        if (goal is null || goal.Problem.Length > 0 || goal.Kind is STONE or SPIRAL or NONE || string.IsNullOrEmpty(goal.Quest)
            || QuestTemplateCollection.GetQuestByName(goal.Quest) is not { } quest || !QuestStory.IsMain(quest)) {
            return null;
        }

        var direct = Far(leader.X, leader.Y, goal.X, goal.Y);
        if (direct < 2500) {
            return null;
        }

        Target best = null;
        var bestWay = direct * 0.6f;
        foreach (var mark in MinimapIndex.In(leader.ZoneActor).Where(m => m.Kind == MinimapIndex.DOOR && m.Entity is not null)) {
            if (mark.Entity.GetComponentOfType<StreetTeleporterComponent>()?.PartnerSpot is not { } lands || avoid.Contains(mark.Object.m_globalID.Full)) {
                continue;
            }

            var way = Far(leader.X, leader.Y, mark.Object.m_location.X, mark.Object.m_location.Y) + Far(lands.X, lands.Y, goal.X, goal.Y);
            if (way < bestWay) {
                bestWay = way;
                best = new Target(STONE, mark.Object.m_location.X, mark.Object.m_location.Y, $"the street teleporter ({mark.Name}) toward {goal.What}",
                    goal.Quest, goal.Goal, mark.Template, mark.Object.m_globalID.Full, Key: "stone|" + mark.Object.m_globalID.Full);
            }
        }

        return best;
    }

    /// <summary>Whether a quest belongs to a main story world (never Grizzleheim, Wintertusk, Wysteria, Aquila or Darkmoor).</summary>
    internal static bool IsMainStoryWorld(QuestTemplate quest) {
        if (quest is null) {
            return false;
        }

        var name = (string) quest.m_questName ?? "";
        var dash = name.IndexOf('-');
        if (dash is > 0 and <= 5 && s_sideWorldCodes.Contains(name[..dash])) {
            return false;
        }

        if (name.Contains("Wintertusk", StringComparison.OrdinalIgnoreCase) || name.Contains("Grizzle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Wysteria", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // Where its giver stands and where its steps go: all in story worlds (a Wizard City giver can send them to Grizzleheim).
        var zones = (quest.m_goals ?? []).Where(g => g is not null).Select(g => (string) g.m_destinationZone)
            .Append(QuestGuide.GiverZone(quest));
        foreach (var zone in zones) {
            if (string.IsNullOrWhiteSpace(zone)) {
                continue;
            }

            var world = zone.Split('/')[0];
            if (!s_storyWorlds.Contains(world)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The quest the team works on: a story quest first, then a quest the story needs, the one most of them hold.</summary>
    internal static string PickQuest(IReadOnlyList<QuestTeamMember.Status> team) {
        var held = new Dictionary<string, (int Count, int Rank, int Order)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < team.Count; i++) {
            foreach (var quest in team[i].Wizard?.QuestBehavior?.CurrentQuestInstances?.ToList() ?? []) {
                if (quest is null || QuestTemplateCollection.GetQuestByName(quest.QuestName) is not { } template) {
                    continue;
                }

                if (!IsMainStoryWorld(template)) {
                    continue;
                }

                // Story first, then dungeon quests, then what the story needs first; other quests held are ignored.
                var rank = BotShared.BotStory.Rank(template);
                if (rank <= 0) {
                    continue;
                }

                held[quest.QuestName] = held.TryGetValue(quest.QuestName, out var known)
                    ? (known.Count + 1, rank, Math.Min(known.Order, i))
                    : (1, rank, i);
            }
        }

        return held.OrderByDescending(h => h.Value.Rank).ThenByDescending(h => h.Value.Count).ThenBy(h => h.Value.Order)
            .Select(h => h.Key).FirstOrDefault();
    }

    /// <summary>The next story quest (or quest the story needs) the team can get, and its giver (a story quest of this world first).</summary>
    internal static (QuestTemplate Quest, ulong Giver)? NextQuest(Wizard leader) {
        var world = (leader.Zone ?? "").Split('/')[0];
        return QuestService.QuestFinderGivers()
            .Where(g => IsMainStoryWorld(g.Quest) && BotShared.BotStory.Plays(g.Quest) && MapService.IsOfferedTo(g.Quest, leader))
            .OrderByDescending(g => BotShared.BotStory.Rank(g.Quest))
            .ThenByDescending(g => string.Equals(QuestGuide.GiverZone(g.Quest).Split('/')[0], world, StringComparison.OrdinalIgnoreCase))
            .Select(g => ((QuestTemplate, ulong)?) (g.Quest, g.Giver))
            .FirstOrDefault();
    }

    /// <summary>
    /// The next story quest the leader could take if only they were a higher level, and the lowest level that opens it
    /// (Niduus, 2026-11-12: "If under-leveled ... execute a command to gain XP to get to the level required"). Null when
    /// no story quest is waiting on a level alone.
    /// </summary>
    internal static (QuestTemplate Quest, int Level)? LevelBlocked(Wizard leader) {
        var have = leader.MagicSchoolBehavior?.Level ?? 1;
        var max = Imlight.CoreLib.Shared.Character.MagicLevelsConfig.MaxLevel;
        var world = (leader.Zone ?? "").Split('/')[0];
        foreach (var g in QuestService.QuestFinderGivers()
                     .Where(g => QuestStory.IsMain(g.Quest) && IsMainStoryWorld(g.Quest))
                     .OrderByDescending(g => QuestStory.KindOf(g.Quest) == QuestStory.STORY)
                     .ThenByDescending(g => string.Equals(QuestGuide.GiverZone(g.Quest).Split('/')[0], world, StringComparison.OrdinalIgnoreCase))) {
            var quest = g.Quest;
            if (quest.m_requirements is null || DungeonQuestIndex.IsDungeonQuest(quest.m_questName) || Imlight.CoreLib.Game.Quests.QuestOptions.IsOff(quest.m_questName)
                || leader.HasQuest(quest.m_questName) || leader.HasCompletedQuest(quest.m_questName)) {
                continue;
            }

            var context = new Imlight.CoreLib.Game.Requirements.Contexts.QuestRequirementContext(quest.m_requirements, null, null, leader, quest.m_questName);
            if (!QuestService.FastForwardMeets(quest.m_requirements, context, true, have)) {
                continue;
            }

            for (var level = have + 1; level <= max; level++) {
                if (QuestService.FastForwardMeets(quest.m_requirements, context, false, level)) {
                    return (quest, level);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What the wizards in <paramref name="group"/> (all in the same area, the first leading) do next for
    /// <paramref name="questName"/>; null when none of them holds it.
    /// </summary>
    internal static Target ForQuest(IReadOnlyList<QuestTeamMember.Status> group, string questName) {
        if (QuestTemplateCollection.GetQuestByName(questName) is not { } quest) {
            return null;
        }

        // The step most of them are on (the one behind first, so nobody is left with a step undone).
        var goals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in group) {
            var held = member.Wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
                && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));
            foreach (var progress in held?.GoalProgress ?? []) {
                if (progress is not null && held.IsGoalActive(progress.GoalName)) {
                    goals[progress.GoalName] = goals.GetValueOrDefault(progress.GoalName) + 1;
                }
            }
        }

        if (goals.Count == 0) {
            return null;
        }

        var ordered = goals.OrderByDescending(g => g.Value)
            .Select(g => quest.m_goals?.FirstOrDefault(t => t is not null && (string) t.m_goalName == g.Key))
            .Where(g => g is not null)
            .OrderBy(g => g.m_hideGoal ? 1 : 0)
            .ToList();
        Target unresolved = null;
        foreach (var goal in ordered) {
            var target = ForGoal(group, quest, goal);
            if (target is not null && target.Problem.Length == 0) {
                return target;
            }

            unresolved ??= target;
        }

        return unresolved;
    }

    private static Target ForGoal(IReadOnlyList<QuestTeamMember.Status> group, QuestTemplate quest, GoalTemplate goal) {
        var leader = group[0];
        var zone = leader.Zone ?? "";
        var live = MinimapIndex.In(leader.ZoneActor);
        Ultimus.UltimusPads.NoteDoors(zone, live);
        var step = OverlayQuestTracker.StepTitle(goal);
        string questName = quest.m_questName;
        string goalName = goal.m_goalName;
        var key = questName + "|" + goalName;
        var stepZone = OverlayQuestTracker.StepZone(goal);
        var spots = QuestArrowGoals.GetSpots(quest, goal).Where(s => OverlayQuestTracker.SameZone(s.Zone, zone)).Select(s => s.Spot).ToList();
        var nearest = spots.OrderBy(s => Far(leader.X, leader.Y, s.X, s.Y)).Select(s => ((float, float)?) (s.X, s.Y)).FirstOrDefault();

        switch (goal) {
            case PersonaGoalTemplate persona: {
                var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                var standing = live.Where(m => m.Template == npc && npc != 0)
                    .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
                if (standing is not null) {
                    return new Target(TALK, standing.Object.m_location.X, standing.Object.m_location.Y, step, questName, goalName, npc,
                        standing.Object.m_globalID.Full, Key: key);
                }

                break;
            }

            case BountyGoalTemplate bounty: {
                var adjectives = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                var foe = live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                         && (m.Adjectives.Any(a => adjectives.Contains(a, StringComparer.Ordinal))
                                             || Quests.BountyStandIns.Counts(adjectives, m.Template))
                                         && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full)
                                         // Not a creature Ultimus is fighting or heading for: the two stay out of each other's way.
                                         && !Ultimus.UltimusDirector.IsBusyNear(leader.ZoneActor, m.Object.m_location.X, m.Object.m_location.Y))
                    .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
                if (foe is not null) {
                    return new Target(FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y, step, questName, goalName, foe.Template,
                        foe.Object.m_globalID.Full, Key: key);
                }

                // None up right now: into the street area where they spawn, to meet the next one.
                if (Tools.RouteMobs.SeekFor(leader.ZoneActor, leader.X, leader.Y, adjectives) is { } area) {
                    var (ax, ay) = Tools.RouteMobs.Nearest(area, leader.X, leader.Y);
                    if (Far(leader.X, leader.Y, ax, ay) > 300) {
                        return new Target(SPOT, ax, ay, $"{step} (where they spawn)", questName, goalName, Key: key + "|spawns");
                    }
                }

                break;
            }

            // A "use" step written as a waypoint (Finding Out Why: open the fairy cages) is clicked, not walked onto
            // (Niduus, 2026-11-13: the team stood 8 minutes on "Go to Wizard City, Unicorn Way").
            case WaypointGoalTemplate when nearest is { } spot && goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE:
                return new Target(SPOT, spot.Item1, spot.Item2, step, questName, goalName, Key: key);
        }

        // A thing to use or pick up where the quest arrow points.
        var usage = goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE;
        if ((usage || goal is not (PersonaGoalTemplate or BountyGoalTemplate or WaypointGoalTemplate)) && nearest is not null) {
            // One after another (Niduus, 2026-11-13: "redeem one and then seek out other's ... Not revisiting collected
            // ones"): the next spot someone still has to do, the free ones before the ones creatures stand by.
            var holders = group.Where(m => m.Wizard?.QuestBehavior?.CurrentQuestInstances?.Any(q => q is not null
                    && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase) && q.IsGoalActive(goalName)) == true)
                .Select(m => m.CharId).ToList();
            var creatures = live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
                .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();
            var next = BotShared.BotCollect.Order(spots.Select(p => (p.X, p.Y)).ToList(), holders, questName, goalName, leader.X, leader.Y, creatures)[0];
            return new Target(USE, next.X, next.Y, step, questName, goalName, Key: key);
        }

        // The step is somewhere else: the way there.
        if (stepZone.Length > 0 && !OverlayQuestTracker.SameZone(stepZone, zone)) {
            return Travel(leader, stepZone, step, key) ?? new Target(NONE, leader.X, leader.Y, step, questName, goalName,
                Problem: $"no way from {QuestTeamText.Zone(zone)} to {QuestTeamText.Zone(stepZone)} could be found", Key: key);
        }

        // In the right area, but nothing to go to yet: the arrow's spot (the NPC or creatures may show up there).
        if (nearest is { } arrow) {
            var kind = goal is BountyGoalTemplate ? FIGHT : goal is PersonaGoalTemplate ? TALK : SPOT;
            if (Far(leader.X, leader.Y, arrow.Item1, arrow.Item2) > 150) {
                return new Target(SPOT, arrow.Item1, arrow.Item2, step, questName, goalName, Key: key + "|arrow");
            }

            return new Target(NONE, arrow.Item1, arrow.Item2, step, questName, goalName,
                Problem: kind switch {
                    TALK => "the person to talk to is not in the area",
                    FIGHT => "no creature for this step is in the area",
                    _ => "nothing happens at the quest arrow's spot",
                }, Key: key);
        }

        return new Target(NONE, leader.X, leader.Y, step, questName, goalName, Problem: goal switch {
            PersonaGoalTemplate => $"the person to talk to is not in {QuestTeamText.Zone(zone)} and the quest names no other area",
            BountyGoalTemplate => $"no creature for this step is in {QuestTeamText.Zone(zone)} and the quest names no other area",
            _ => $"the quest arrow has no spot for this step and the quest names no area",
        }, Key: key);
    }

    /// <summary>The way out of this area toward <paramref name="toZone"/>: a door, a walk-through exit, or the Spiral Door.</summary>
    internal static Target Travel(QuestTeamMember.Status leader, string toZone, string why, string key = "") {
        var zone = leader.Zone ?? "";
        var live = MinimapIndex.In(leader.ZoneActor);
        var exits = OverlayQuestRoute.ExitsHere(zone, live);
        var spiralDoors = live.Where(m => m.Template == OverlayQuestRoute.SPIRAL_DOOR).ToList();
        var hops = OverlayQuestRoute.Way(zone, toZone, spiralDoors.Count > 0, exits.Select(e => e.To));
        if (hops.Count == 0) {
            return null;
        }

        var (next, world) = hops[0];
        if (world && spiralDoors.Count > 0) {
            var door = spiralDoors.OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).First();

            return new Target(SPIRAL, door.Object.m_location.X, door.Object.m_location.Y, $"Spiral Door to {QuestTeamText.Zone(next)} ({why})",
                GlobalId: door.Object.m_globalID.Full, To: next, Key: key + "|spiral");
        }

        var nextWorld = next.Split('/')[0];
        var exit = exits.Where(e => OverlayQuestTracker.SameZone(next, e.To) || OverlayQuestTracker.SameZone(e.To, next))
            .OrderBy(e => Far(leader.X, leader.Y, e.X, e.Y)).FirstOrDefault();
        exit ??= exits.Where(e => string.Equals(e.To.Split('/')[0], nextWorld, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(e.To.Split('/')[0], zone.Split('/')[0], StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Far(leader.X, leader.Y, e.X, e.Y)).FirstOrDefault();
        if (exit is null) {
            return null;
        }

        // A door (or sigil) is clicked; anything else is walked into.
        var door2 = live.Where(m => m.Kind is MinimapIndex.DOOR or MinimapIndex.SIGIL && string.Equals(m.To?.Trim(), exit.To, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => Far(exit.X, exit.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (door2 is not null && Far(exit.X, exit.Y, door2.Object.m_location.X, door2.Object.m_location.Y) < 50) {
            return new Target(DOOR, exit.X, exit.Y, $"to {QuestTeamText.Zone(exit.To)} ({why})", GlobalId: door2.Object.m_globalID.Full, To: exit.To,
                Key: key + "|door|" + exit.To);
        }

        return new Target(EXIT, exit.X, exit.Y, $"to {QuestTeamText.Zone(exit.To)} ({why})", To: exit.To, Key: key + "|exit|" + exit.To);
    }

    /// <summary>The giver of a quest: here, or the way to its area.</summary>
    internal static Target ForGiver(QuestTeamMember.Status leader, QuestTemplate quest, ulong giver) {
        var live = MinimapIndex.In(leader.ZoneActor);
        string questName = quest.m_questName;
        var what = $"get {QuestTeamText.Title(questName)}";
        var standing = live.Where(m => m.Template == giver)
            .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (standing is not null) {
            return new Target(OFFER, standing.Object.m_location.X, standing.Object.m_location.Y, what, questName, Template: giver,
                GlobalId: standing.Object.m_globalID.Full, Key: "offer|" + questName);
        }

        var zone = QuestGuide.GiverZone(quest);
        if (zone.Length > 0 && !OverlayQuestTracker.SameZone(zone, leader.Zone)) {
            return Travel(leader, zone, what, "offer|" + questName)
                   ?? new Target(NONE, leader.X, leader.Y, what, questName, Problem: $"no way to {QuestTeamText.Zone(zone)}, where its giver is", Key: "offer|" + questName);
        }

        // The giver should be here: its spot from the area's file.
        var placed = QuestStepSpots.Zone(leader.Zone).Objects.FirstOrDefault(o => o.Template == giver);
        if (placed.Template == giver && Far(leader.X, leader.Y, placed.Spot.X, placed.Spot.Y) > 150) {
            return new Target(SPOT, placed.Spot.X, placed.Spot.Y, what, questName, Key: "offer|" + questName + "|spot");
        }

        return new Target(NONE, leader.X, leader.Y, what, questName, Problem: "the NPC who gives it is not in the area", Key: "offer|" + questName);
    }

    internal static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

}
