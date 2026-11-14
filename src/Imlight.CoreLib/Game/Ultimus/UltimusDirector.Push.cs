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
using Akka.Actor;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Getting Ultimus far, the way a person would (Niduus, 2026-11-14, "all" to the wave, with: "He is not allowed to use
/// commands to JUMP to the next main quest. HIS ENTIRE PURPOSE IS TO QUEST AS IF HE IS A HUMAN. He's only exceptions of
/// this is to execute commands to unstuck"):
/// <list type="bullet">
/// <item>Push mode (playbook Push.On): the main story first. No side trips to shops except when he arrives in a new world
/// or after a lost fight; nothing is skipped or finished for him.</item>
/// <item>Step pre-check: before each step he checks his level against the quest's, and what went wrong at this step
/// before (step memory). Too far under the quest's level for a fight, he fights for XP first.</item>
/// <item>XP the human way: under-leveled for the next story quest, he fights creatures for it (the old XP command is
/// gone).</item>
/// <item>Fast travel a player has: before a trip to a trainer or a shop he marks his spot (Mark Location) and recalls
/// back after (Recall Location), paying the mana for it as a player does.</item>
/// <item>Overnight watchdog: no step done and no XP gained for Watchdog.Minutes, he says so loudly and uses the unstuck
/// moves a player has (back to the area start, then .stuck go). Never a skip.</item>
/// <item>Progress: logs/ultimus/progress.txt (and worlds.json) say where he is, how long each world took and how today
/// went, for Ahziael's Ultimus tab.</item>
/// <item>Solo tags: every lesson and mistake he writes says "solo" or "with players" (BotKnowledge.SetParty).</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Each tick
    // ---------------------------------------------------------------------

    private void PushTick(UltimusMember.Status leader) {
        // Who he plays with right now, on every lesson (solo data and team data never mix).
        var withPlayers = _target?.Key?.EndsWith("|join", StringComparison.Ordinal) == true
                          || CombatDuelComponent.UltimusWithPlayers(leader.GlobalId);
        BotKnowledge.SetParty("Ultimus", withPlayers ? "with players" : "solo");

        if (UltimusPlanner.HasCreatures(leader)) {
            _xpZone = leader.Zone;
        }

        // A new level: his training plan again (UltimusDirector.Life.cs).
        var level = leader.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        if (level != _planLevel) {
            if (_planLevel > 0) {
                WriteTrainingPlan(leader, _storyWorld ?? (leader.Zone ?? "").Split('/')[0]);
            }

            _planLevel = level;
        }

        Watchdog(leader);
        WriteProgress(leader);
        // Story order, level milestones and the self-test after an update (UltimusDirector.Story.cs).
        ThinkStory(leader);
        LevelMilestone(leader);
        MaybeSelfTest(leader);
    }

    // ---------------------------------------------------------------------
    // XP the human way
    // ---------------------------------------------------------------------

    private string _xpZone;
    private int _planLevel;

    /// <summary>A creature to fight for XP toward <paramref name="level"/>: here, or back where creatures were.</summary>
    private UltimusPlanner.Target XpTarget(UltimusMember.Status leader, int level, string why) {
        var have = leader.Wizard?.MagicSchoolBehavior?.Level ?? 1;
        Think($"I am level {have} and {why}. I don't take shortcuts: I fight creatures for the XP, as any player would.", "xp|" + level);
        _doing = $"fighting for XP toward level {level} ({why})";
        if (UltimusPlanner.HasCreatures(leader)) {
            _xpZone = leader.Zone;
        }

        // A side quest of about his level given right here beats fighting one creature at a time (UltimusDirector.Story.cs).
        if (SideQuestForXp(leader) is { } side) {
            return side;
        }

        if (UltimusPlanner.ForXp(leader, level, why) is { } fight) {
            return fight;
        }

        if (!string.IsNullOrEmpty(_xpZone) && !OverlayQuestTracker.SameZone(_xpZone, leader.Zone)
            && UltimusPlanner.Travel(leader, _xpZone, "back to the creatures, for XP", "xp") is { } way) {
            return way;
        }

        Issue("team", null, $"{leader.Name} needs level {level} ({why}) and sees no creature to fight for it here, and knows no area with some.",
            key: "xp-nowhere|" + leader.Zone, level: UltimusLink.ABNORMAL);

        return null;
    }

    /// <summary>
    /// Before a fight step he is far under the quest's level for (Push.LevelGap), or a fight he lost here while under it:
    /// XP first. Null when he is ready.
    /// </summary>
    private UltimusPlanner.Target LevelFirst(UltimusMember.Status leader, UltimusPlanner.Target step) {
        // Never out of a dungeon for it.
        if (step is null || step.Kind != UltimusPlanner.FIGHT || string.IsNullOrEmpty(step.Quest) || leader.Wizard?.ZoneIsInstance == true
            || QuestTemplateCollection.GetQuestByName(step.Quest) is not { } quest || quest.m_questLevel <= 0) {
            return null;
        }

        var have = leader.Wizard?.MagicSchoolBehavior?.Level ?? 1;
        var gap = (int) UltimusPlaybook.Value("Push.LevelGap");
        var lostHere = _losses.GetValueOrDefault(step.Key) > 0;
        var want = lostHere ? quest.m_questLevel : quest.m_questLevel - gap;
        if (have >= want) {
            return null;
        }

        var why = lostHere
            ? $"I lost this fight before and the quest is level {quest.m_questLevel}"
            : $"the quest is level {quest.m_questLevel}, more than {gap} over me ({UltimusPlaybook.Cite("Push.LevelGap")})";

        return XpTarget(leader, want, why);
    }

    // ---------------------------------------------------------------------
    // Push mode: no side trips
    // ---------------------------------------------------------------------

    private string _shopWorld = "";
    private DateTime _shopWorldSince = DateTime.MinValue;

    /// <summary>Whether a shopping trip is allowed now (always, with push mode off).</summary>
    private bool ShopAllowed(UltimusMember.Status leader) {
        if (UltimusPlaybook.Value("Push.On") < 0.5) {
            return true;
        }

        var world = (leader.Zone ?? "").Split('/')[0];
        if (!string.Equals(world, _shopWorld, StringComparison.OrdinalIgnoreCase)) {
            _shopWorld = world;
            _shopWorldSince = DateTime.UtcNow;
        }

        // New in this world (its shops have the next gear), or a lost fight in the last hour (he needs to be stronger).
        return DateTime.UtcNow - _shopWorldSince < TimeSpan.FromMinutes(UltimusPlaybook.Value("Push.ShopMinutes"))
               || UltimusMood.LostRecently;
    }

    // ---------------------------------------------------------------------
    // Mark and Recall around trips
    // ---------------------------------------------------------------------

    private string _markedZone;
    private DateTime _markedAt = DateTime.MinValue;

    /// <summary>
    /// A trip away from the quest (a trainer or a shop in another area) starts: he marks his spot first, as a player
    /// does before running to Ravenwood. Mark Location costs mana; with too little he walks back instead.
    /// </summary>
    private void MarkBeforeTrip(UltimusMember.Status leader, UltimusPlanner.Target trip) {
        if (trip is null || _markedZone is not null || trip.Kind is not (UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL)
            || leader.Wizard?.ZoneIsInstance == true || (leader.Wizard?.GameStats?.m_currentMana ?? 0) < 10) {
            return;
        }

        _markedZone = leader.Zone;
        _markedAt = DateTime.UtcNow;
        _actors[0].Tell(new UltimusMember.MarkSpot());
        Note("Ultimus", $"marks his spot in {UltimusText.Zone(leader.Zone)} before the trip ({trip.What}), to recall back after.");
        Think("I mark my spot before I go: when I am done, Recall brings me back here, like any player does.", "mark");
    }

    /// <summary>The trip is over: back to the marked spot.</summary>
    private void RecallAfterTrip(UltimusMember.Status leader) {
        if (_markedZone is null) {
            return;
        }

        if (OverlayQuestTracker.SameZone(_markedZone, leader.Zone) || DateTime.UtcNow - _markedAt > TimeSpan.FromHours(1)) {
            _markedZone = null;
            return;
        }

        if (leader.Wizard?.ZoneIsInstance == true || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            return;
        }

        Note("Ultimus", $"recalls to the spot he marked in {UltimusText.Zone(_markedZone)}.");
        Count("recalls to a marked spot");
        _actors[0].Tell(new UltimusMember.RecallSpot());
        _markedZone = null;
    }

    // ---------------------------------------------------------------------
    // Step pre-check and step memory
    // ---------------------------------------------------------------------

    private const string STEP_MEMORY_FILE = "step-memory.json";
    private static readonly object s_stepMemoryLock = new();
    private static Dictionary<string, List<string>> s_stepMemory;

    private static string StepKey(string quest, string goal) => (quest ?? "") + "|" + (goal ?? "");

    /// <summary>
    /// Something that went wrong at a step, kept for good (logs/ultimus/step-memory.json), even when it happened once: the
    /// next time he (or a new Ultimus) comes to that step, he reads it first.
    /// </summary>
    private static void RememberStep(string quest, string goal, string text) {
        if (string.IsNullOrEmpty(quest) || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        lock (s_stepMemoryLock) {
            var memory = StepMemory();
            var key = StepKey(quest, goal);
            if (!memory.TryGetValue(key, out var notes)) {
                memory[key] = notes = [];
            }

            var line = $"{DateTime.Now:yyyy-MM-dd} ({BotKnowledge.PartyOf("Ultimus")}): {text}";
            if (notes.Any(n => n.EndsWith(text, StringComparison.Ordinal))) {
                return;
            }

            notes.Add(line);
            if (notes.Count > 8) {
                notes.RemoveAt(0);
            }

            try {
                File.WriteAllText(Path.Combine(UltimusWire.Folder, STEP_MEMORY_FILE), JsonSerializer.Serialize(memory, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(STEP_MEMORY_FILE, e.Message));
            }
        }
    }

    private static List<string> RecallStep(string quest, string goal) {
        lock (s_stepMemoryLock) {
            return StepMemory().TryGetValue(StepKey(quest, goal), out var notes) ? [.. notes] : [];
        }
    }

    private static Dictionary<string, List<string>> StepMemory() {
        if (s_stepMemory is not null) {
            return s_stepMemory;
        }

        try {
            var path = Path.Combine(UltimusWire.Folder, STEP_MEMORY_FILE);
            s_stepMemory = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(STEP_MEMORY_FILE, e.Message));
        }

        return s_stepMemory ??= [];
    }

    /// <summary>Before a new step: his level against the quest's, and what went wrong here before.</summary>
    private void PreCheck(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (string.IsNullOrEmpty(target.Quest)) {
            return;
        }

        var checks = new List<string>();
        if (QuestTemplateCollection.GetQuestByName(target.Quest) is { } quest && quest.m_questLevel > 0) {
            var have = leader.Wizard?.MagicSchoolBehavior?.Level ?? 1;
            checks.Add(have >= quest.m_questLevel
                ? $"my level {have} is fine for this level {quest.m_questLevel} quest"
                : $"I am level {have}, the quest is level {quest.m_questLevel}" + (target.Kind == UltimusPlanner.FIGHT ? ", so I watch my health closely" : ""));
        }

        var stats = leader.Wizard?.GameStats;
        if (stats is not null) {
            var most = Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
            checks.Add($"health {stats.m_currentHitpoints * 100 / most}%");
        }

        var before = RecallStep(target.Quest, target.Goal);
        if (before.Count > 0) {
            checks.Add($"last time here: {before[^1]}");
            Count("step memories read");
        }

        Think($"Before \"{target.What}\" I check: {string.Join("; ", checks)}.", "precheck|" + target.Key);
    }

    // ---------------------------------------------------------------------
    // Overnight watchdog
    // ---------------------------------------------------------------------

    private string _progressMark = "";
    private DateTime _progressAt = DateTime.UtcNow;
    private int _watchdogStage;

    private void Watchdog(UltimusMember.Status leader) {
        var school = leader.Wizard?.MagicSchoolBehavior;
        var mark = $"{_seenGoals.Count}|{school?.Level}|{school?.ExperiencePoints}|{_runs.Count}";
        if (mark != _progressMark) {
            _progressMark = mark;
            _progressAt = DateTime.UtcNow;
            if (_watchdogStage > 0) {
                Note("Ultimus", "WATCHDOG: moving forward again.", UltimusLink.NOTABLE);
            }

            _watchdogStage = 0;
            return;
        }

        if (_paused || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            _progressAt = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Min((DateTime.UtcNow - _progressAt).TotalSeconds, 60));
            return;
        }

        var limit = TimeSpan.FromMinutes(Math.Max(5, UltimusPlaybook.Value("Watchdog.Minutes")));
        var idle = DateTime.UtcNow - _progressAt;
        var stage = (int) (idle.TotalMinutes / limit.TotalMinutes);
        if (stage <= _watchdogStage) {
            return;
        }

        _watchdogStage = stage;
        var where = $"{UltimusText.Zone(leader.Zone)}, while {_doing}";
        Issue("team", _currentQuest, $"WATCHDOG: no step done and no XP gained for {idle.TotalMinutes:0} minutes ({where}).",
            key: $"watchdog|{_progressMark}|{stage}", level: UltimusLink.CRITICAL);
        Count("watchdog alarms");
        if (stage >= 2) {
            Milestone("stuck", $"Ultimus is badly stuck: no step done and no XP gained for {idle.TotalMinutes:0} minutes in {where}.");
        }

        // Only the unstuck moves a player has: back to the area's start, then .stuck go; never a skip.
        switch (stage) {
            case 1:
                Think("I have not moved my quest forward in a long time. I go back to where this area starts and try again from there.", "watchdog");
                _actors[0].Tell(new UltimusMember.Recall());
                break;
            case 2 when !string.IsNullOrEmpty(_currentQuest):
                Think("Still nothing. I use .stuck go, as a player would, to get to the area of my step.", "watchdog");
                _actors[0].Tell(new UltimusMember.GoToStep());
                break;
            default:
                Think("Still nothing, and every unstuck move is used. I keep trying and wait for Niduus to look.", "watchdog");
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Progress (Ahziael's Ultimus tab)
    // ---------------------------------------------------------------------

    private const string PROGRESS_FILE = "progress.txt";
    private const string WORLDS_FILE = "worlds.json";
    private DateTime _nextProgress = DateTime.MinValue;

    internal sealed class WorldTime {
        public string World { get; set; } = "";
        public DateTime Started { get; set; }
        public DateTime? Finished { get; set; }
        public int LevelIn { get; set; }
        public int LevelOut { get; set; }
    }

    private List<WorldTime> _worlds;

    private List<WorldTime> Worlds() {
        if (_worlds is not null) {
            return _worlds;
        }

        try {
            var path = Path.Combine(UltimusWire.Folder, WORLDS_FILE);
            _worlds = File.Exists(path) ? JsonSerializer.Deserialize<List<WorldTime>>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(WORLDS_FILE, e.Message));
        }

        return _worlds ??= [];
    }

    /// <summary>A new story world begun (UltimusDirector.NoteWorld): the last one's time written down.</summary>
    private void WorldStarted(string world, int level) {
        var worlds = Worlds();
        if (worlds.LastOrDefault() is { Finished: null } last && !string.Equals(last.World, world, StringComparison.OrdinalIgnoreCase)) {
            last.Finished = DateTime.UtcNow;
            last.LevelOut = level;
        }

        if (worlds.LastOrDefault()?.World is not { } now || !string.Equals(now, world, StringComparison.OrdinalIgnoreCase)) {
            worlds.Add(new WorldTime { World = world, Started = DateTime.UtcNow, LevelIn = level });
        }

        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, WORLDS_FILE), JsonSerializer.Serialize(worlds, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(WORLDS_FILE, e.Message));
        }
    }

    /// <summary>A new Ultimus (Ahziael's Restart): his world times start over.</summary>
    private void ForgetWorlds() {
        _worlds = [];
        try {
            File.Delete(Path.Combine(UltimusWire.Folder, WORLDS_FILE));
        }
        catch (IOException) {
            // Written again with the first world.
        }
    }

    private void WriteProgress(UltimusMember.Status leader) {
        if (DateTime.UtcNow < _nextProgress) {
            return;
        }

        _nextProgress = DateTime.UtcNow.AddSeconds(30);
        var school = leader.Wizard?.MagicSchoolBehavior;
        var text = new StringBuilder();
        text.AppendLine($"ULTIMUS PROGRESS ({DateTime.Now:dddd d MMMM yyyy, HH:mm})");
        text.AppendLine($"Level {school?.Level ?? 0}, in {UltimusText.Zone(leader.Zone)}; story world: {_storyWorld ?? "not started"}.");
        text.AppendLine($"Story: {StoryPlace(leader)}.");
        text.AppendLine($"Quest: {(_currentQuest is null ? "none" : UltimusText.Title(_currentQuest))}. Now: {_doing}.");
        text.AppendLine($"Mood: {UltimusMood.Now} ({UltimusMood.Why()}). Plays: {BotKnowledge.PartyOf("Ultimus")}.");
        text.AppendLine($"Last step done or XP gained: {Minutes((DateTime.UtcNow - _progressAt).TotalSeconds)} ago.");
        text.AppendLine($"Push mode: {(UltimusPlaybook.Value("Push.On") >= 0.5 ? "on (main story first, no side trips)" : "off")}.");
        text.AppendLine();
        text.AppendLine("WORLDS");
        foreach (var world in Worlds()) {
            var took = (world.Finished ?? DateTime.UtcNow) - world.Started;
            text.AppendLine($"  {world.World}: {(world.Finished is null ? "playing for" : "took")} {took.TotalHours:0.0} h, " +
                            $"level {world.LevelIn}{(world.Finished is null ? "" : $" to {world.LevelOut}")}");
        }

        text.AppendLine();
        text.AppendLine($"TODAY: {Summary()}");
        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, PROGRESS_FILE), text.ToString());
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(PROGRESS_FILE, e.Message));
        }
    }

}
