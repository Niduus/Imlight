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
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Where the Ultimus goes next and what it does there, from what a player can know: the quest book (each wizard's
/// quests and steps), the quest arrow's spots, the NPCs and creatures standing in the area, and the area's doors and
/// exits (the same reading the Quest Tracker's line uses, OverlayQuestRoute). Only reads.
/// </summary>
internal static class UltimusPlanner {

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
    // Nothing to do this moment but wait (the Quest Team has the creatures): not a problem.
    internal const string WAIT = "wait";

    /// <summary>
    /// Where to go and what to do there. <paramref name="Problem"/> is set when the step can't be done from what is in the
    /// game (no such NPC, no creature, no way there): the quest is broken there.
    /// </summary>
    internal sealed record Target(string Kind, float X, float Y, string What, string Quest = "", string Goal = "", ulong Template = 0,
                                  ulong GlobalId = 0, string To = "", string Problem = "", string Key = "", float Z = float.NaN) {

        /// <summary>The same target as another (so a wizard on its way there is not sent again).</summary>
        internal bool Same(Target other) => other is not null && other.Kind == Kind && other.Key == Key
                                            && MathF.Abs(other.X - X) < 60 && MathF.Abs(other.Y - Y) < 60;
    }

    // Forked from the Quest Team's planner (Game/QuestTeam/QuestTeamPlanner): the same reading of quests and areas, for one
    // wizard, plus its own rules (leave the Quest Team's creatures alone, join players' battles).

    // The main story's worlds (Niduus, 2026-11-12: "I want them in Main Story worlds. Not Grizzle, Wintertusk or the Academy
    // world", "Wizard City, Krok, Marlybone, Mooshu, Dragonspyre, these types of worlds"), by their zone folder.
    private static readonly HashSet<string> s_storyWorlds = OverlayQuestRoute.StoryWorlds;

    // Quest codes of the side worlds (Grizzleheim, Wintertusk, Wysteria's academy, Aquila, Selenopolis). Darkmoor is a main
    // world (Niduus, 2026-11-15).
    private static readonly HashSet<string> s_sideWorldCodes = new(StringComparer.OrdinalIgnoreCase) { "GH", "WT", "PA", "WY", "AQ", "SL" };

    // The story's worlds in the order they are played (Arcanum is a hub, not a step of the ladder).
    // Niduus's main worlds list (BotShared.MainWorlds, 2026-11-15); Ahmarra has no questing data yet and is left out.
    private static readonly string[] s_ladder = BotShared.MainWorlds.Questable;

    /// <summary>The story's worlds in the order they are played, by zone folder.</summary>
    internal static IReadOnlyList<string> Ladder => s_ladder;

    /// <summary>
    /// Whether the leader has played the last world of the story this server's data has (Niduus, 2026-11-12: "Stop. But
    /// that's assuming they finish ... the Ladder depends on the data at the time"): a story quest given there is done.
    /// </summary>
    internal static bool FinishedLastWorld(Wizard leader, out string world) {
        world = "";
        var mains = QuestService.QuestFinderGivers().Where(g => QuestStory.IsStory(g.Quest) && IsMainStoryWorld(g.Quest))
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
    internal static Target Shortcut(UltimusMember.Status leader, Target goal, ICollection<ulong> avoid) {
        if (goal is null || goal.Problem.Length > 0 || goal.Kind is STONE or SPIRAL or NONE || string.IsNullOrEmpty(goal.Quest)
            || QuestTemplateCollection.GetQuestByName(goal.Quest) is not { } quest || !QuestStory.IsStory(quest)) {
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

    /// <summary>Whether a quest belongs to a main story world (never Grizzleheim, Wintertusk, Wysteria, Aquila or Selenopolis, nor a
    /// main world without questing data yet, such as Ahmarra).</summary>
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
            if (!s_storyWorlds.Contains(world) || !BotShared.MainWorlds.QuestsIn(world)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The quest the team works on: a story quest first, then a quest the story needs, the one most of them hold.</summary>
    internal static string PickQuest(IReadOnlyList<UltimusMember.Status> team) {
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

        var ordered = held.OrderByDescending(h => h.Value.Rank).ThenByDescending(h => h.Value.Count).ThenBy(h => h.Value.Order).ToList();
        if (ordered.Count == 0) {
            return null;
        }

        // Several story quests open at the same rank (Niduus, 2026-11-14: "do the nearest first" instead of crisscrossing):
        // the one whose next step is nearest by the real way. He stays on the one he was doing unless another is clearly
        // nearer, so he does not turn back and forth between two.
        var tied = ordered.Where(h => h.Value.Rank == ordered[0].Value.Rank && h.Value.Count == ordered[0].Value.Count).Select(h => h.Key).ToList();
        var pick = ordered[0].Key;
        if (tied.Count > 1 && team.Count > 0) {
            var costs = new List<(string Quest, float Cost)>();
            foreach (var quest in tied) {
                if (ForQuest(team, quest) is { Problem.Length: 0 } target) {
                    costs.Add((quest, WayCost(team[0], target, target.To ?? "")));
                }
            }

            if (costs.Count > 0) {
                var best = costs.MinBy(c => c.Cost);
                var before = costs.FirstOrDefault(c => string.Equals(c.Quest, s_lastQuest, StringComparison.OrdinalIgnoreCase));
                pick = before.Quest is not null && before.Cost <= (best.Cost * 1.3f) + 200 ? before.Quest : best.Quest;
            }
        }

        s_lastQuest = pick;

        return pick;
    }

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<string> s_lastQuestSeat = new();
    private static string s_lastQuest { get => s_lastQuestSeat.Value; set => s_lastQuestSeat.Value = value; }

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
                     .Where(g => QuestStory.IsStory(g.Quest) && IsMainStoryWorld(g.Quest))
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
    internal static Target ForQuest(IReadOnlyList<UltimusMember.Status> group, string questName) {
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
        // Several steps open at once ("Gather the Troops": talk to Private Farnsworth and Private Primwell, in any order):
        // the nearest one by the real way there first (Niduus, 2026-11-14: "prioritize the closest target"), then the next.
        // A step that must come first (the rest are behind it) keeps its place: only steps open together are compared.
        Target unresolved = null;
        var open = new List<(Target Target, float Cost)>();
        foreach (var goal in ordered) {
            var target = ForGoal(group, quest, goal);
            if (target is not null && target.Problem.Length == 0) {
                open.Add((target, WayCost(group[0], target, OverlayQuestTracker.StepZone(goal))));
                continue;
            }

            unresolved ??= target;
        }

        if (open.Count == 0) {
            return unresolved;
        }

        // A step set aside for a while (its time budget ran out, or two progress checks said no): another open one first.
        var allowed = open.Where(o => !SetAsideNow(o.Target.Key)).ToList();
        if (allowed.Count > 0) {
            open = allowed;
        }

        // Kept on the one picked before unless another is clearly nearer, so he does not turn back and forth between two.
        var best = open.MinBy(o => o.Cost);
        var before = open.FirstOrDefault(o => o.Target.Key == s_lastPick.GetValueOrDefault(questName));
        var pick = before.Target is not null && before.Cost <= (best.Cost * 1.3f) + 200 ? before : best;
        s_lastPick[questName] = pick.Target.Key;

        return pick.Target;
    }

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<System.Collections.Concurrent.ConcurrentDictionary<string, string>> s_lastPickSeat = new(() => new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> s_lastPick { get => s_lastPickSeat.Value; set => s_lastPickSeat.Value = value; }
    // The area where the creatures of a "defeat" step were last seen ("quest|goal" to zone).
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<System.Collections.Concurrent.ConcurrentDictionary<string, string>> s_seenInSeat = new(() => new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> s_seenIn { get => s_seenInSeat.Value; set => s_seenInSeat.Value = value; }
    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>> s_setAsideSeat = new(() => new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
    private static System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> s_setAside { get => s_setAsideSeat.Value; set => s_setAsideSeat.Value = value; }

    /// <summary>What the planner had picked, for picking up exactly there after a restart (UltimusDirector.Resume.cs).</summary>
    internal static (string Quest, Dictionary<string, string> Picks, Dictionary<string, DateTime> Asides) Saved()
        => (s_lastQuest, s_lastPick.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            s_setAside.Where(a => a.Value > DateTime.UtcNow).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    /// <summary>Picks up the planner's picks saved before a restart.</summary>
    internal static void Restore(string quest, IReadOnlyDictionary<string, string> picks, IReadOnlyDictionary<string, DateTime> asides) {
        s_lastQuest ??= quest;
        foreach (var (k, v) in picks ?? new Dictionary<string, string>()) {
            s_lastPick.TryAdd(k, v);
        }

        foreach (var (k, v) in asides ?? new Dictionary<string, DateTime>()) {
            if (v > DateTime.UtcNow) {
                s_setAside.TryAdd(k, v);
            }
        }
    }

    /// <summary>Ahziael's "Retry now": this one step set aside is open again.</summary>
    internal static void ClearAsideKey(string key) => s_setAside.TryRemove(key, out _);

    /// <summary>Ahziael's "Fixed it": every step of <paramref name="quest"/> set aside is open again.</summary>
    internal static void ClearAside(string quest) {
        foreach (var key in s_setAside.Keys.Where(k => k.StartsWith(quest + "|", StringComparison.OrdinalIgnoreCase)).ToList()) {
            s_setAside.TryRemove(key, out _);
        }
    }

    /// <summary>A step set aside for a while: when the quest has another open step, that one goes first (the guardrails).</summary>
    internal static void SetAside(string key, TimeSpan forHow) => s_setAside[key] = DateTime.UtcNow + forHow;

    private static bool SetAsideNow(string key) => key is not null && s_setAside.TryGetValue(key, out var until) && DateTime.UtcNow < until;

    /// <summary>Whether the step is set aside now, and until when (UltimusDirector.Aside.cs).</summary>
    internal static bool IsSetAside(string key, out DateTime until) {
        until = DateTime.MinValue;
        return key is not null && s_setAside.TryGetValue(key, out until) && DateTime.UtcNow < until;
    }

    /// <summary>Ends a step's set-aside early (nothing else to do meanwhile, after a rest).</summary>
    internal static void EndAside(string key) {
        if (key is not null) {
            s_setAside.TryRemove(key, out _);
        }
    }

    // How far a target is by the real way: the walk on the floor in this area, plus a large share for each area between.
    private const float AREA_COST = 4000f;

    internal static float WayCost(UltimusMember.Status leader, Target target, string stepZone = "") {
        var zone = leader.Zone ?? "";
        var walk = WalkLength(leader, target.X, target.Y);
        if (target.Kind is not (EXIT or DOOR or SPIRAL) || string.IsNullOrEmpty(stepZone)) {
            return walk;
        }

        var hops = OverlayQuestTracker.SameZone(stepZone, zone) ? 0 : Math.Max(1, OverlayQuestRoute.Way(zone, stepZone, false, storyOnly: true).Count);

        return walk + (hops * AREA_COST);
    }

    /// <summary>The length of the walk on the floor from the leader to (x, y), the straight distance when no walk is known.</summary>
    internal static float WalkLength(UltimusMember.Status leader, float x, float y)
        => OverlayQuestRoute.WalkLength(leader.Zone ?? "", leader.X, leader.Y, x, y);

    private static Target ForGoal(IReadOnlyList<UltimusMember.Status> group, QuestTemplate quest, GoalTemplate goal) {
        var leader = group[0];
        var zone = leader.Zone ?? "";
        var live = MinimapIndex.In(leader.ZoneActor);
        UltimusPads.NoteDoors(zone, live);
        var step = OverlayQuestTracker.StepTitle(goal);
        string questName = quest.m_questName;
        string goalName = goal.m_goalName;
        var key = questName + "|" + goalName;
        var stepZone = OverlayQuestTracker.StepZone(goal);
        var spots = QuestArrowGoals.GetSpots(quest, goal).Where(s => OverlayQuestTracker.SameZone(s.Zone, zone)).Select(s => s.Spot).ToList();
        // A "use" step clicks the things the step names where they stand, not only the arrow's one spot (Niduus's log,
        // 2026-11-14: "Get Some Bling" sent Ultimus round the Hall of Champions for 15 minutes; its four gems were never at the arrow).
        // Only in the area the step is in: the quest's own area, else the one its arrow points into. The Frozen Forge of "Taking
        // it to the Tuts" stands in Odji's Chamber, where the arrow points; the Entrance Hall's braziers carry the same "use the
        // forge" tag, and Ultimus clicked them and then searched the Entrance Hall for 10 hours (his PC log, 2026-11-14).
        var arrowZones = QuestArrowGoals.GetSpots(quest, goal).Select(s => s.Zone).Where(z => !string.IsNullOrEmpty(z)).ToList();
        var usageHere = stepZone.Length > 0 ? OverlayQuestTracker.SameZone(stepZone, zone)
            : arrowZones.Count == 0 || arrowZones.Any(z => OverlayQuestTracker.SameZone(z, zone));
        if (goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE && usageHere
            && QuestStepSpots.UsageObjects(zone, questName, goalName) is { Count: > 0 } things) {
            spots = things;
            // In the room with the thing: the tracker says what to do here, not where to go (Niduus's screenshot, 2026-11-14:
            // "Now: Go to Krokotopia,Hall of Champions" while he stood by the Frozen Forge in Odji's Chamber).
            if (OverlayQuestTracker.GameStepText(goal) is { Length: > 0 } useText && !useText.StartsWith("Go to", StringComparison.OrdinalIgnoreCase)) {
                step = $"{useText} here";
            }
        }

        // A "use" step that names no area, with its arrow in another area: that area is where the step is (the forge in Odji's
        // Chamber, after he had left it), so he goes back there as a player follows the arrow.
        if (goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE && stepZone.Length == 0 && spots.Count == 0 && arrowZones.Count > 0
            && !arrowZones.Any(z => OverlayQuestTracker.SameZone(z, zone))) {
            stepZone = arrowZones[0];
        }

        var nearest = spots.OrderBy(s => Far(leader.X, leader.Y, s.X, s.Y)).Select(s => ((float, float)?) (s.X, s.Y)).FirstOrDefault();

        switch (goal) {
            case PersonaGoalTemplate persona: {
                var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                var standing = live.Where(m => m.Template == npc && npc != 0)
                    .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault()
                    // The same person under another of the game's objects (an area's own copy of them): matched by name, as a
                    // player knows them by name (his log, 2026-11-14: Ms. Marphely of "Out of Control" never found in The Ironworks).
                    ?? BotShared.BotPeople.ByName(live, npc, leader.X, leader.Y, key, leader.Zone);
                if (standing is not null) {
                    return new Target(TALK, standing.Object.m_location.X, standing.Object.m_location.Y, step, questName, goalName, npc,
                        standing.Object.m_globalID.Full, Key: key);
                }

                // The Quest Atlas (UltimusAtlas): the zone files place this person in one other story area only, so the step is
                // done there, known before he gets stuck looking for them where the quest says.
                if (UltimusAtlas.Of(questName, goalName) is { RealZone.Length: > 0 } atlas && !OverlayQuestTracker.SameZone(atlas.RealZone, zone)
                    && !OverlayQuestTracker.SameZone(atlas.RealZone, stepZone)) {
                    if (UltimusRules.On(UltimusAtlas.RULE)) {
                        stepZone = atlas.RealZone;
                    }
                    else if (UltimusRules.Watching(UltimusAtlas.RULE)) {
                        UltimusRules.WouldHave(UltimusAtlas.RULE, $"gone to {UltimusText.Zone(atlas.RealZone)} for \"{step}\": {atlas.Why}.", key);
                    }
                }

                // Not here and the step names no area: where this person stands, as a player knows (Niduus, 2026-11-13:
                // "Run and Done" had its Cyrus Drake, Victor Darkwood and Merle Ambrose steps skipped in seconds).
                // The same name here offered nothing for the step (another person of that name): where the right one
                // stands is looked up too, even with the quest's arrow in this area (his log, 2026-11-15: two Baxters).
                var wrongCopy = BotShared.BotPeople.HadWrongCopy(key) && (stepZone.Length == 0 || OverlayQuestTracker.SameZone(stepZone, zone));
                if ((stepZone.Length == 0 && spots.Count == 0) || wrongCopy) {
                    var where = NpcZone(persona.m_personaName, npc, zone, out var looking);
                    if (looking) {
                        return new Target(WAIT, leader.X, leader.Y, $"{step} (works out where they stand)", questName, goalName, Key: key + "|lookup");
                    }

                    if (where.Length > 0 && !OverlayQuestTracker.SameZone(where, zone)) {
                        stepZone = where;
                    }
                }

                // Looked for in the step's own area and not there, and the game's zone files place them in one other story
                // area only: that area is where the step is done, from wherever the wizard stands (his log, 2026-11-15:
                // "Out of Control" names The Ironworks, while Ms. Marphely stands only in Chelsea Court, and he rode between
                // The Ironworks and Digmoore Station for over half an hour). The tracker's area would pull him back otherwise.
                if (stepZone.Length > 0 && npc != 0 && BotShared.BotPeople.Absent(key, stepZone)
                    && UltimusNpcBook.Of(npc, zone) is { Count: > 0 } placed && !placed.Any(p => OverlayQuestTracker.SameZone(p.Zone, stepZone))
                    && placed.Select(p => p.Zone).Where(OverlayQuestRoute.OnStory).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: 1 } only) {
                    stepZone = only[0];
                }

                break;
            }

            case BountyGoalTemplate bounty: {
                var adjectives = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                // Creatures that counted for this step before count too, even when the step's tags match nothing here, and the
                // ones beaten 3 times for nothing go last (UltimusDrops: learned from his own fights).
                var wanted = live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                            && (m.Adjectives.Any(a => adjectives.Contains(a, StringComparer.Ordinal))
                                                || Quests.BountyStandIns.Counts(adjectives, m.Template)
                                                || UltimusDrops.Score(questName, goalName, m.Template) > 0))
                    .OrderBy(m => UltimusDrops.Score(questName, goalName, m.Template) < 0 ? 1 : 0)
                    .ThenBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).ToList();
                var free = wanted.Where(m => BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full)).ToList();
                // Where he saw them, for when he is somewhere else and the step names no area (his PC log, 2026-11-15: out of The
                // Vault of Ice he searched the Entrance Hall for Keeper of the Fang, "the quest names no other area").
                if (wanted.Count > 0) {
                    s_seenIn[key] = zone;
                }
                else if (stepZone.Length == 0 && s_seenIn.TryGetValue(key, out var seenZone) && !OverlayQuestTracker.SameZone(seenZone, zone)) {
                    stepZone = seenZone;
                }
                else if (stepZone.Length == 0 && arrowZones.Count > 0 && !arrowZones.Any(z => OverlayQuestTracker.SameZone(z, zone))) {
                    stepZone = arrowZones[0];
                }

                // The Quest Team's creatures are left to it (Niduus, 2026-11-12: Ultimus "will never attempt to join QT and
                // they will always attempt to avoid colliding"): a creature near a Quest Team wizard is not Ultimus's.
                var notTheTeams = free.Where(m => !NearQuestTeam(leader, m.Object.m_location.X, m.Object.m_location.Y)).ToList();
                // The nearest by the real way (of the few nearest in a line), not one behind a wall (Niduus, 2026-11-14).
                // The best group (approved by Niduus, 2026-11-14): alone he picks one standing apart, as other creatures by it
                // join the battle; strong for the step with more to beat, a few wanted ones together are taken at once.
                var others = MinimapIndex.In(leader.ZoneActor).Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                                                          && BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full)).ToList();
                var level = leader.Wizard?.MagicSchoolBehavior?.Level ?? 1;
                var strong = level >= UltimusDirectorActor.QuestLevelOf(quest) + 2 && bounty.m_bountyTotal > 1
                             && (leader.Wizard?.GameStats is { } st && st.m_currentHitpoints >= (st.m_baseHitpoints + st.m_bonusHitpoints) * 0.8);
                float GroupCost(MinimapIndex.Mark m) {
                    var (mx, my) = (m.Object.m_location.X, m.Object.m_location.Y);
                    var near = others.Where(o => o != m && Far(mx, my, o.Object.m_location.X, o.Object.m_location.Y) < 350).ToList();
                    var wantedNear = near.Count(o => notTheTeams.Contains(o));
                    var otherNear = near.Count - wantedNear;

                    return WalkLength(leader, mx, my) + (otherNear * 600) + (strong ? -300 * wantedNear : 500 * wantedNear)
                           + (UltimusDrops.Score(questName, goalName, m.Template) < 0 ? 3000 : 0);
                }

                notTheTeams = [.. notTheTeams.Take(5).OrderBy(GroupCost).Concat(notTheTeams.Skip(5))];
                if (notTheTeams.FirstOrDefault() is { } foe) {
                    return new Target(FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y, step, questName, goalName, foe.Template,
                        foe.Object.m_globalID.Full, Key: key);
                }

                // A player is already fighting one: Ultimus joins them when there is room (Niduus: "Ultimus will join players
                // if it's convenient for quests"), as a player walks into a battle.
                if (UltimusWire.JoinPlayers) {
                    foreach (var busy in wanted.Where(m => CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))) {
                        if (CombatDuelComponent.UltimusMayJoin(busy.Object.m_globalID.Full, out var who)) {
                            return new Target(FIGHT, busy.Object.m_location.X, busy.Object.m_location.Y, $"{step} (joins {who}'s battle)", questName, goalName,
                                busy.Template, busy.Object.m_globalID.Full, Key: key + "|join");
                        }
                    }
                }

                // Only the Quest Team's are free: Ultimus waits off to the side for them to be done.
                if (free.Count > 0) {
                    return new Target(WAIT, leader.X, leader.Y, $"{step} (waits: the Quest Team is fighting these creatures)", questName, goalName, Key: key + "|wait");
                }

                // A creature that comes once a puzzle is solved (the Great Wurm of "Cavern of the Wurm" comes once the Sand
                // Thumpers are used in the right order): the things the quest's people name are used in order, as a player
                // works it out from what they said (BotPuzzle; Niduus, 2026-11-15).
                if (wanted.Count == 0 && (stepZone.Length == 0 || OverlayQuestTracker.SameZone(stepZone, zone))) {
                    var puzzleHolders = group.Where(m => m.Wizard?.QuestBehavior?.CurrentQuestInstances?.Any(q => q is not null
                            && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase) && q.IsGoalActive(goalName)) == true)
                        .Select(m => m.CharId).ToList();
                    if (BotShared.BotPuzzle.ForCreatureStep(zone, quest, goal, puzzleHolders, leader.X, leader.Y,
                            live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS or MinimapIndex.NPC or MinimapIndex.DOOR or MinimapIndex.SIGIL)
                                .Select(m => m.Template)) is { } thing) {
                        return new Target(USE, thing.X, thing.Y, $"{step} (works out the puzzle that brings them)", questName, goalName, Key: key + "|order");
                    }
                }

                // None up right now: into the street area where they spawn, to meet the next one (MobSpawnAreas).
                if (wanted.Count == 0 && (stepZone.Length == 0 || OverlayQuestTracker.SameZone(stepZone, zone)) && Tools.RouteMobs.SeekFor(leader.ZoneActor, leader.X, leader.Y, adjectives) is { } area) {
                    var (ax, ay) = Tools.RouteMobs.Nearest(area, leader.X, leader.Y);
                    if (Far(leader.X, leader.Y, ax, ay) > 300) {
                        return new Target(SPOT, ax, ay, $"{step} (where they spawn)", questName, goalName, Key: key + "|spawns");
                    }
                }

                break;
            }

            // A "use" step written as a waypoint (Finding Out Why: open the fairy cages) is clicked, not walked onto (found
            // by the Quest Team, Niduus, 2026-11-13).
            case WaypointGoalTemplate when nearest is { } spot && goal.m_goalType != GOAL_TYPE.GOAL_TYPE_USAGE:
                return new Target(SPOT, spot.Item1, spot.Item2, step, questName, goalName, Key: key);
        }

        // A thing to use or pick up where the quest arrow points: the next one not used yet, the free ones before the ones
        // creatures stand by (Niduus, 2026-11-13: "redeem one and then seek out other's ... Not revisiting collected ones").
        var usage = goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE;
        if ((usage || goal is not (PersonaGoalTemplate or BountyGoalTemplate or WaypointGoalTemplate)) && nearest is not null) {
            var holders = group.Where(m => m.Wizard?.QuestBehavior?.CurrentQuestInstances?.Any(q => q is not null
                    && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase) && q.IsGoalActive(goalName)) == true)
                .Select(m => m.CharId).ToList();
            var creatures = MinimapIndex.In(leader.ZoneActor).Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                                                          && BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full))
                .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();
            // A puzzle (the quest's people speak of an order or a combination): the things in the order worked out for it,
            // the next order when nothing happened (BotPuzzle; Niduus, 2026-11-15).
            if (BotShared.BotPuzzle.Next(zone, quest, goal, spots.Select(p => (p.X, p.Y)).ToList(), holders, leader.X, leader.Y) is { } inOrder) {
                return new Target(USE, inOrder.X, inOrder.Y, $"{step} (in the right order)", questName, goalName, Key: key + "|order");
            }

            var next = BotShared.BotCollect.Order(spots.Select(p => (p.X, p.Y)).ToList(), holders, questName, goalName, leader.X, leader.Y, creatures)[0];
            return new Target(USE, next.X, next.Y, step, questName, goalName, Key: key);
        }

        // The step is somewhere else: the way there.
        if (stepZone.Length > 0 && !OverlayQuestTracker.SameZone(stepZone, zone)) {
            return Travel(leader, stepZone, step, key) ?? new Target(NONE, leader.X, leader.Y, step, questName, goalName,
                Problem: $"no way from {UltimusText.Zone(zone)} to {UltimusText.Zone(stepZone)} could be found", Key: key);
        }

        // A person to talk to who is not standing in sight yet, but the NPC location book knows their spot in this area:
        // walk there, as a player who knows the story walks to them.
        if (goal is PersonaGoalTemplate talkTo && UltimusNpcBook.In(QuestMadlibs.SpeakerTemplateOf(talkTo.m_personaName, 0), zone) is { } booked
            && Far(leader.X, leader.Y, booked.X, booked.Y) > 150) {
            return new Target(SPOT, booked.X, booked.Y, step, questName, goalName, Key: key + "|book");
        }

        // The step's person is not here, and there is nowhere left to walk to: written down for the Issues tab (BotPeople).
        if (goal is PersonaGoalTemplate absent && (nearest is not { } near || Far(leader.X, leader.Y, near.Item1, near.Item2) <= 150)) {
            var who = QuestMadlibs.SpeakerTemplateOf(absent.m_personaName, 0);
            var name = Shared.Resources.CoreObjectFactory.GetCoreTemplate(who) is GameObjectTemplate person ? Tools.MinimapIndex.NameOf(person) : "";
            BotShared.BotPeople.NotThere(name.Length > 0 ? name : step, questName, step, zone, key,
                who == 0 ? "" : Ultimus.UltimusPlanner.NpcZone(absent.m_personaName, who, zone, out _));
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
            PersonaGoalTemplate => $"the person to talk to is not in {UltimusText.Zone(zone)} and the quest names no other area",
            BountyGoalTemplate => $"no creature for this step is in {UltimusText.Zone(zone)} and the quest names no other area",
            _ => $"the quest arrow has no spot for this step and the quest names no area",
        }, Key: key);
    }

    // Where each "talk to" person stands, by persona and the area asked from ("" none known, null still being found).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_npcZones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The area a quest's person stands in: the area the other quests' "talk to" steps name for them, else the nearest
    /// area of the game's files that places them (found in the background; <paramref name="looking"/> while it is).
    /// "" when nobody knows.
    /// </summary>
    internal static string NpcZone(string persona, ulong npc, string from, out bool looking) {
        looking = false;
        var named = Imlight.CoreLib.Game.Tools.QuestGuide.PersonaZone(persona);
        if (named.Length == 0 && CoreObjectFactory.GetCoreTemplate(npc) is GameObjectTemplate template && !template.m_objectName.IsEmpty) {
            named = Imlight.CoreLib.Game.Tools.QuestGuide.PersonaZone(template.m_objectName);
        }

        if (named.Length > 0 || npc == 0) {
            return named;
        }

        // The NPC location book (the story's NPCs, from the zone files) first: its own world's spot, else the nearest.
        if (UltimusNpcBook.Of(npc, from) is { Count: > 0 } booked) {
            return booked.OrderByDescending(p => OverlayQuestRoute.OnStory(p.Zone))
                .ThenBy(p => OverlayQuestTracker.SameZone(p.Zone, from) ? 0 : OverlayQuestRoute.Way(from, p.Zone, false, storyOnly: true).Count)
                .First().Zone;
        }

        var key = npc + "|" + (from ?? "").Split('/')[0];
        if (s_npcZones.TryGetValue(key, out var known)) {
            looking = known is null;

            return known ?? "";
        }

        if (s_npcZones.TryAdd(key, null)) {
            looking = true;
            System.Threading.Tasks.Task.Run(() => s_npcZones[key] = PlacedZone(npc, from));
        }

        return "";
    }

    // The area of the game's files that places this NPC fewest areas away (its own world first).
    private static string PlacedZone(ulong npc, string from) {
        try {
            var world = (from ?? "").Split('/')[0];
            var zones = SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(z => !string.IsNullOrEmpty(z))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(z => z.StartsWith(world + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            var best = (Zone: "", Hops: int.MaxValue);
            foreach (var zone in zones) {
                if (!QuestStepSpots.Zone(zone).Objects.Any(o => o.Template == npc)) {
                    continue;
                }

                var hops = OverlayQuestTracker.SameZone(zone, from) ? 0 : OverlayQuestRoute.Way(from, zone, false).Count;
                if (best.Zone.Length == 0 || (hops > 0 && hops < best.Hops)) {
                    best = (zone, hops > 0 ? hops : int.MaxValue - 1);
                }
            }

            return best.Zone;
        }
        catch (Exception) {
            return "";
        }
    }

    /// <summary>The way out of this area toward <paramref name="toZone"/>: a door, a walk-through exit, or the Spiral Door.</summary>
    internal static Target Travel(UltimusMember.Status leader, string toZone, string why, string key = "") {
        var zone = leader.Zone ?? "";
        var live = MinimapIndex.In(leader.ZoneActor);
        var exits = OverlayQuestRoute.ExitsHere(zone, live);
        var spiralDoors = live.Where(m => m.Template == OverlayQuestRoute.SPIRAL_DOOR).ToList();
        // Only the story's own areas (Niduus, 2026-11-14, "THAT'S NOT A MAIN QUEST!!"): an exit into an event, a gauntlet,
        // a house or a side world is never taken unless it is the step's own area.
        exits = [.. exits.Where(e => OverlayQuestRoute.OnStory(e.To) || OverlayQuestTracker.SameZone(toZone, e.To) || OverlayQuestTracker.SameZone(e.To, toZone))];
        var hops = OverlayQuestRoute.Way(zone, toZone, spiralDoors.Count > 0, exits.Select(e => e.To), storyOnly: OverlayQuestRoute.OnStory(toZone));
        if (hops.Count == 0) {
            return null;
        }

        var (next, world) = hops[0];
        if (world && spiralDoors.Count > 0) {
            var door = spiralDoors.OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).First();

            return new Target(SPIRAL, door.Object.m_location.X, door.Object.m_location.Y, $"Spiral Door to {UltimusText.Zone(next)} ({why})",
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

        // The Tomb of Storms' way into the Well of Spirits opens once the three storm obelisks are lit: lit first, one by
        // one, as a player does (StormObelisks).
        if (StormObelisks.IsCrypt(zone) && StormObelisks.IsInside(exit.To) && StormObelisks.Shut(leader.Wizard)) {
            return ObeliskFirst(leader, live, why, key);
        }

        // A door (or sigil) is clicked; anything else is walked into.
        var door2 = live.Where(m => m.Kind is MinimapIndex.DOOR or MinimapIndex.SIGIL && string.Equals(m.To?.Trim(), exit.To, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => Far(exit.X, exit.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        // A door that leads to the same area is that exit's door even when its model stands away from the exit's own point
        // (a shop front: the trigger is behind the wall, the door on the street). It is clicked, as a player does, at the
        // door itself (his log, 2026-11-15: 5 fix rounds 109 from Jack Russell Jewelers' point, walking into the wall).
        var doorFar = door2 is null ? float.MaxValue : Far(exit.X, exit.Y, door2.Object.m_location.X, door2.Object.m_location.Y);
        if (door2 is not null && doorFar >= 50 && doorFar <= 600) {
            return new Target(DOOR, door2.Object.m_location.X, door2.Object.m_location.Y, $"to {UltimusText.Zone(exit.To)} ({why})",
                GlobalId: door2.Object.m_globalID.Full, To: exit.To, Key: key + "|door|" + exit.To, Z: door2.Object.m_location.Z);
        }

        if (door2 is not null && doorFar < 50) {
            return new Target(DOOR, exit.X, exit.Y, $"to {UltimusText.Zone(exit.To)} ({why})", GlobalId: door2.Object.m_globalID.Full, To: exit.To,
                Key: key + "|door|" + exit.To, Z: exit.Z);
        }

        return new Target(EXIT, exit.X, exit.Y, $"to {UltimusText.Zone(exit.To)} ({why})", To: exit.To, Key: key + "|exit|" + exit.To, Z: exit.Z);
    }

    /// <summary>The giver of a quest: here, or the way to its area.</summary>
    // The nearest storm obelisk the leader has not lit yet, to press on the way into the Well of Spirits.
    private static Target ObeliskFirst(UltimusMember.Status leader, IReadOnlyList<MinimapIndex.Mark> live, string why, string key) {
        if (!StormObelisks.MayLight(leader.Wizard)) {
            return new Target(NONE, leader.X, leader.Y, $"the storm obelisks of the Tomb of Storms ({why})",
                Problem: "the way into the Well of Spirits is sealed until the 3 storm obelisks are lit, and they only light after \"Back to Balance\"",
                Key: key + "|obelisks");
        }

        var obelisk = StormObelisks.Unlit(leader.Wizard).OrderBy(o => Far(leader.X, leader.Y, o.X, o.Y)).First();
        var mark = live.Where(m => m.Template == obelisk.Template)
            .OrderBy(m => Far(obelisk.X, obelisk.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        var left = StormObelisks.Unlit(leader.Wizard).Count();
        var what = $"storm obelisk {obelisk.Number} of the Tomb of Storms ({left} of 3 still to light before the way into the Well of Spirits opens; {why})";

        return mark is null
            ? new Target(SPOT, obelisk.X, obelisk.Y, what, Key: key + "|obelisk|" + obelisk.Number)
            : new Target(STONE, mark.Object.m_location.X, mark.Object.m_location.Y, what, GlobalId: mark.Object.m_globalID.Full,
                Key: key + "|obelisk|" + obelisk.Number);
    }

    internal static Target ForGiver(UltimusMember.Status leader, QuestTemplate quest, ulong giver) {
        var live = MinimapIndex.In(leader.ZoneActor);
        string questName = quest.m_questName;
        var what = $"get {UltimusText.Title(questName)}";
        var standing = live.Where(m => m.Template == giver)
            .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (standing is not null) {
            return new Target(OFFER, standing.Object.m_location.X, standing.Object.m_location.Y, what, questName, Template: giver,
                GlobalId: standing.Object.m_globalID.Full, Key: "offer|" + questName);
        }

        var zone = QuestGuide.GiverZone(quest);
        if (zone.Length > 0 && !OverlayQuestTracker.SameZone(zone, leader.Zone)) {
            return Travel(leader, zone, what, "offer|" + questName)
                   ?? new Target(NONE, leader.X, leader.Y, what, questName, Problem: $"no way to {UltimusText.Zone(zone)}, where its giver is", Key: "offer|" + questName);
        }

        // The giver should be here: its spot from the area's file.
        var placed = QuestStepSpots.Zone(leader.Zone).Objects.FirstOrDefault(o => o.Template == giver);
        if (placed.Template == giver && Far(leader.X, leader.Y, placed.Spot.X, placed.Spot.Y) > 150) {
            return new Target(SPOT, placed.Spot.X, placed.Spot.Y, what, questName, Key: "offer|" + questName + "|spot");
        }

        return new Target(NONE, leader.X, leader.Y, what, questName, Problem: "the NPC who gives it is not in the area", Key: "offer|" + questName);
    }

    /// <summary>
    /// A creature to fight for XP, as a player who is under-leveled does (Niduus, 2026-11-14: he quests as a human; no XP
    /// command): the nearest free creature in this area that is not a boss and not the Quest Team's. Null when there is none.
    /// </summary>
    internal static Target ForXp(UltimusMember.Status leader, int level, string why) {
        if (leader.Wizard?.ZoneIsInstance == true) {
            return null;
        }

        var foe = MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind == MinimapIndex.MOB && BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full)
                        && !NearQuestTeam(leader, m.Object.m_location.X, m.Object.m_location.Y))
            .OrderBy(m => Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y))
            .FirstOrDefault();

        return foe is null ? null : new Target(FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y, $"a fight for XP toward level {level} ({why})",
            Template: foe.Template, GlobalId: foe.Object.m_globalID.Full, Key: "xp|" + level);
    }

    /// <summary>Whether this area has creatures to fight (for remembering where XP can be had).</summary>
    internal static bool HasCreatures(UltimusMember.Status leader)
        => leader.Wizard?.ZoneIsInstance != true && MinimapIndex.In(leader.ZoneActor).Any(m => m.Kind == MinimapIndex.MOB);

    // A Quest Team wizard stands within 900 of the spot, in the same area.
    private static bool NearQuestTeam(UltimusMember.Status leader, float x, float y) {
        if (leader.ZoneActor is not { } zone) {
            return false;
        }

        foreach (var member in QuestTeam.QuestTeamDirector.Members) {
            if (member.ZoneActor is { } theirs && theirs.Equals(zone) && Far(member.X, member.Y, x, y) < 900) {
                return true;
            }
        }

        return false;
    }

    internal static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

}
