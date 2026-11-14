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
using System.IO;
using System.Linq;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's mission and the limits only Niduus changes (Niduus, 2026-11-14, approved guardrails: "keep him completely on
/// track with his goal").
/// </summary>
internal static class UltimusMission {

    internal const string STATEMENT = "Finish the main story solo, like a human.";

    /// <summary>The hard limits, in plain words (also in his playbook's know-how).</summary>
    internal static readonly string[] HardLimits = [
        "no step is skipped or finished for me",
        "no free XP, gold or items",
        "no farming creatures for XP: the main story comes first",
        "no crafting or pet training",
        "I stay in bounds: on the floor, in the main story's worlds",
        "side areas (dungeons, events, other worlds) only when a quest names them",
    ];

    // Playbook rules his daily review may never change, whatever self-tuning.txt says: the hard limits' numbers.
    private static readonly HashSet<string> s_untunable = new(StringComparer.OrdinalIgnoreCase) {
        "Push.On", "Gold.Gifts", "Guard.StepMinutes", "Guard.QuestFactor", "Guard.SideTripMinutes", "Guard.NoStoryMinutes",
        "Guard.CheckMinutes", "Guard.MinWinChance", "Push.SeekFights", "Quest.HuntHealBelow",
    };

    /// <summary>Whether a playbook rule is one only Niduus changes.</summary>
    internal static bool HardLimit(string rule) => s_untunable.Contains(rule ?? "");

}

/// <summary>
/// The guardrails (Niduus, 2026-11-14, all 9 approved, top priority):
/// <list type="number">
/// <item>Mission check: every new move is checked against the mission; one that does not serve it is turned down, with why.</item>
/// <item>Off-track alarm: on a quest that is not the story's, or no story progress for Guard.NoStoryMinutes: stop, say
/// why, plan again (leaving a side area is UltimusDirector.Bounds.cs).</item>
/// <item>Hard limits: UltimusMission; the daily review can't change them.</item>
/// <item>Time budgets: a step, a quest and a side trip each have one; past it he drops it or plans again.</item>
/// <item>Risk budget: a fight he trusts under Guard.MinWinChance is prepared for first (full health and mana, a deck).</item>
/// <item>Self-tuning only inside Niduus's ranges, every change logged, and locked rules left alone (UltimusDirector.Plans.cs).</item>
/// <item>Progress check every Guard.CheckMinutes: closer than last time? Two noes in a row: another strategy.</item>
/// <item>Every move has a "because" in his thoughts; one he can't give a reason for, he doesn't make.</item>
/// <item>Ahziael: Pause, "Back to story" and per-rule locks.</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Side trips and their time budget
    // ---------------------------------------------------------------------

    private DateTime _sideTripsClosedUntil = DateTime.MinValue;
    private string _sideTripKey;
    private DateTime _sideTripSince = DateTime.MaxValue;

    /// <summary>Whether side trips (shops, the Bazaar, trainers, selling) are open: closed for a while after one ran over its budget.</summary>
    // One intention at a time (approved 2026-11-14): no side trip starts while he is healing (it marked, travelled and sold
    // on top of a heal trip before).
    private bool SideTripsOpen => DateTime.UtcNow >= _sideTripsClosedUntil && !(_team.Count > 0 && _team[0].Wizard?.ZoneIsInstance == true)
                                  && DateTime.UtcNow - _healingAt > TimeSpan.FromSeconds(5);

    /// <summary>A side trip picked: its time budget runs from the first pick. Past it, it is dropped and side trips close for a while.</summary>
    private UltimusPlanner.Target SideTrip(UltimusPlanner.Target trip, string kind) {
        if (trip is null) {
            return null;
        }

        if (_sideTripKey != kind) {
            _sideTripKey = kind;
            _sideTripSince = DateTime.UtcNow;
        }

        var budget = TimeSpan.FromMinutes(Math.Max(1, UltimusPlaybook.Value("Guard.SideTripMinutes")));
        if (DateTime.UtcNow - _sideTripSince <= budget) {
            return trip;
        }

        CloseSideTrips(TimeSpan.FromMinutes(30));
        Note("Ultimus", $"TIME BUDGET: the side trip ({kind}: {trip.What}) took over {budget.TotalMinutes:0} minutes; drops it and goes back to the story.",
            UltimusLink.ABNORMAL);
        Think($"My side trip ({trip.What}) ran over its time budget of {budget.TotalMinutes:0} minutes ({UltimusPlaybook.Cite("Guard.SideTripMinutes")}). " +
              $"It does not serve my mission ({UltimusMission.STATEMENT}) any more, so I drop it and go back to the story; no side trips for 30 minutes.",
            "budget-side|" + kind);

        return null;
    }

    private void CloseSideTrips(TimeSpan forHow) {
        _sideTripsClosedUntil = DateTime.UtcNow + forHow;
        _sideTripKey = null;
        _sideTripSince = DateTime.MaxValue;
        _bazaarTrip = null;
    }

    // A pick that is not a side trip ends the one before.
    private void NoSideTrip() {
        _sideTripKey = null;
        _sideTripSince = DateTime.MaxValue;
    }

    // ---------------------------------------------------------------------
    // Mission check and "because"
    // ---------------------------------------------------------------------

    /// <summary>
    /// Why this move serves the mission, or null when it does not (then it is turned down). Said in his thoughts with each
    /// new move.
    /// </summary>
    private string Because(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target is null || string.IsNullOrWhiteSpace(target.What)) {
            return null;
        }

        var key = target.Key ?? "";
        if (key.StartsWith("wisp", StringComparison.Ordinal)) {
            return "health and mana keep my story's fights winnable";
        }

        if (key.StartsWith("potionshop", StringComparison.Ordinal)) {
            return "potions keep me fighting through my story";
        }

        if (_sideTripKey is { } side) {
            return side switch {
                "training" => "new spells make my story's fights winnable",
                "selling" => "room in my backpack for the gear my story gives",
                _ => "better gear makes my story's fights winnable",
            };
        }

        if (target.What.StartsWith("a fight for XP", StringComparison.Ordinal)) {
            return "my story's next quest needs a higher level, and XP is earned, never given";
        }

        if (string.IsNullOrEmpty(target.Quest)) {
            // Leaving a side area, backing out of a wrong one, a safe spot: on the way back to the story.
            return "it gets me back on my way to the story";
        }

        if (QuestTemplateCollection.GetQuestByName(target.Quest) is { } quest && !UltimusPlanner.IsMainStoryWorld(quest)) {
            return null;
        }

        return string.Equals(target.Quest, _currentQuest, StringComparison.OrdinalIgnoreCase)
            ? $"it is my story quest's step ({UltimusText.Title(target.Quest)})"
            : $"it is a step of another story quest of mine ({UltimusText.Title(target.Quest)}), right on my way";
    }

    /// <summary>The mission check on a new move: the move, or null when it is turned down (an off-track alarm then plans again).</summary>
    private UltimusPlanner.Target MissionCheck(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target is null || target.Same(_target)) {
            return target;
        }

        if (Because(leader, target) is { } because) {
            Think($"Next: {Describe(target)}, because {because}. (My mission: {UltimusMission.STATEMENT})", "because|" + target.Key);

            return target;
        }

        var why = string.IsNullOrWhiteSpace(target.What)
            ? "I can't say what it is for"
            : $"\"{target.What}\" is for {UltimusText.Title(target.Quest)}, which is not a main story quest";
        Note("Ultimus", $"MISSION CHECK: turns down {Describe(target)}: {why}.", UltimusLink.ABNORMAL);
        Think($"I turn down {Describe(target)}: {why}, and my mission is to {UltimusMission.STATEMENT.ToLowerInvariant()} " +
              "A move I can't give a story reason for, I don't make.", "mission|" + target.Key);
        Ban(target.Key, TimeSpan.FromMinutes(10));
        OffTrack($"my next move did not serve the story ({why})");

        return null;
    }

    // ---------------------------------------------------------------------
    // Off-track alarm and plans again
    // ---------------------------------------------------------------------

    private DateTime _storyProgressAt = DateTime.UtcNow;
    private DateTime _offTrackAt = DateTime.MinValue;

    /// <summary>Story progress: a step, a quest, a level, or a won fight while levelling for the story.</summary>
    private void StoryProgress() => _storyProgressAt = DateTime.UtcNow;

    /// <summary>The off-track alarm: he stops, says why, and plans again back to the story.</summary>
    private void OffTrack(string why) {
        if (DateTime.UtcNow - _offTrackAt < TimeSpan.FromSeconds(30)) {
            return;
        }

        _offTrackAt = DateTime.UtcNow;
        Count("off-track alarms");
        Note("Ultimus", $"OFF-TRACK ALARM: {why}. Stops and plans again, back to the story.", UltimusLink.ABNORMAL);
        Think($"Off-track alarm: {why}. I stop, drop any side trip, and plan again from where I stand toward my story.", "offtrack|" + why);
        BotShared.BotMistakes.Record("Ultimus", BotShared.BotMistakes.OFF_STORY, _team.Count > 0 ? _team[0].Zone : "", _team.Count > 0 ? _team[0].X : 0,
            _team.Count > 0 ? _team[0].Y : 0, _target is null ? _doing : $"{_target.Quest}|{_target.Goal}|{_target.What}",
            $"Off-track alarm: {why}.", _lastReasoning, "Plan again toward the story; side trips closed for a while.", "plan again");
        CloseSideTrips(TimeSpan.FromMinutes(15));
        Replan();
    }

    // A fresh plan: the old move and the old walk forgotten.
    private void Replan() {
        _target = null;
        _sent.Remove(0);
        if (_team.Count > 0) {
            OverlayQuestPath.Forget(_team[0].CharId);
        }

        StoryProgress();
    }

    // A move he set aside for a while (another open step goes first; the planner comes back to it after).
    private static void Ban(string key, TimeSpan forHow) {
        if (!string.IsNullOrEmpty(key)) {
            UltimusPlanner.SetAside(key, forHow);
        }
    }

    // ---------------------------------------------------------------------
    // The watch: budgets, no story progress, the progress check
    // ---------------------------------------------------------------------

    private DateTime _nextCheck = DateTime.UtcNow.AddMinutes(5);
    private float _checkDistance = float.MaxValue;
    private string _checkKey;
    private DateTime _checkProgress = DateTime.MinValue;
    private int _checkNoes;

    /// <summary>Each tick, not in a battle or paused: the step budget, the no-progress alarm, the progress check.</summary>
    private void GuardTick(UltimusMember.Status leader) {
        if (_paused || leader.Wizard is null || CombatDuelComponent.IsInBattle(leader.GlobalId) || InScene(leader)) {
            return;
        }

        var now = DateTime.UtcNow;

        // A step over its time budget: another open step first, or another way (the unstick round).
        var stepBudget = TimeSpan.FromMinutes(Math.Max(2, UltimusPlaybook.Value("Guard.StepMinutes")));
        if (_target is { } step && _targetSince != DateTime.MinValue && now - _targetSince > stepBudget && !_healWaiting) {
            _targetSince = now;
            Note("Ultimus", $"TIME BUDGET: \"{step.What}\" took over {stepBudget.TotalMinutes:0} minutes; sets it aside a while and plans again.", UltimusLink.ABNORMAL);
            Think($"\"{step.What}\" ran over its time budget of {stepBudget.TotalMinutes:0} minutes ({UltimusPlaybook.Cite("Guard.StepMinutes")}). " +
                  "I set it aside for 10 minutes: another open step of my story goes first, or I find another way to it.", "budget-step|" + step.Key);
            Ban(step.Key, TimeSpan.FromMinutes(10));
            Unstick(0, $"ran over the {stepBudget.TotalMinutes:0}-minute budget for one step");

            return;
        }

        // No story progress for a long while: the off-track alarm.
        var noStory = TimeSpan.FromMinutes(Math.Max(3, UltimusPlaybook.Value("Guard.NoStoryMinutes")));
        if (now - _storyProgressAt > noStory) {
            OffTrack($"no story progress (a step, a quest, a level, or XP for one) in {noStory.TotalMinutes:0} minutes ({UltimusPlaybook.Cite("Guard.NoStoryMinutes")})");

            return;
        }

        // Every few minutes: am I closer to my goal than last time? Two noes in a row: another strategy.
        if (now < _nextCheck) {
            return;
        }

        _nextCheck = now.AddMinutes(Math.Max(1, UltimusPlaybook.Value("Guard.CheckMinutes")));
        var target = _target ?? _leaderLastTarget;
        var distance = target is null ? float.MaxValue : UltimusPlanner.WayCost(leader, target);
        // The first check after a start only sets where he stands (audit A7: it always said "yes" after a restart).
        if (_checkProgress == DateTime.MinValue) {
            _checkKey = target?.Key;
            _checkDistance = distance;
            _checkProgress = _storyProgressAt;

            return;
        }

        var closer = _storyProgressAt > _checkProgress
                     || (target?.Key == _checkKey && distance < _checkDistance * 0.9f)
                     || (target is not null && target.Key != _checkKey);
        var said = _storyProgressAt > _checkProgress ? "yes: I made story progress"
            : target?.Key != _checkKey ? "yes: I am on a new move"
            : closer ? $"yes: {_checkDistance:0} to {distance:0} from my goal" : $"no: still {distance:0} from my goal ({_checkDistance:0} last time)";
        _checkKey = target?.Key;
        _checkDistance = distance;
        _checkProgress = _storyProgressAt;
        _checkNoes = closer ? 0 : _checkNoes + 1;
        Think($"Progress check: am I closer to my goal than {UltimusPlaybook.Value("Guard.CheckMinutes"):0} minutes ago? {said}." +
              (_checkNoes == 1 ? " One no: I keep going, and look again next time." : ""), "check|" + now.Ticks / TimeSpan.TicksPerMinute);
        if (_checkNoes >= 2) {
            _checkNoes = 0;
            Note("Ultimus", "PROGRESS CHECK: no closer to the goal twice in a row; switches strategy.", UltimusLink.ABNORMAL);
            Think("Two noes in a row: what I am doing is not working, so I switch strategy: this move set aside for 10 minutes if " +
                  "another step is open, and the next fix in my unstick round.", "check-switch|" + target?.Key);
            Ban(target?.Key, TimeSpan.FromMinutes(10));
            Unstick(0, "was no closer to the goal at two progress checks in a row");
        }
    }

    // ---------------------------------------------------------------------
    // Risk budget
    // ---------------------------------------------------------------------

    private bool _riskPrepare;

    /// <summary>A fight trusted under Guard.MinWinChance: prepared for first (full health and mana; its deck is made in Lead).</summary>
    private void RiskBudget(UltimusPlanner.Target target, int confidence) {
        var least = UltimusPlaybook.Value("Guard.MinWinChance");
        _riskPrepare = target.Kind == UltimusPlanner.FIGHT && confidence < least;
        if (_riskPrepare) {
            Think($"I trust \"{Describe(target)}\" {confidence}/100, under my risk budget of {least:0} ({UltimusPlaybook.Cite("Guard.MinWinChance")}): " +
                  "I don't start it until I have prepared: full health and mana, and a deck made for it.", "risk|" + target.Key);
        }
    }

    // ---------------------------------------------------------------------
    // Ahziael's "Back to story"
    // ---------------------------------------------------------------------

    private DateTime _nextBackLook = DateTime.MinValue;

    /// <summary>Ahziael's "Back to story": side trips dropped, a pause lifted, and a fresh plan toward the story.</summary>
    private void MaybeBackToStory() {
        if (DateTime.UtcNow < _nextBackLook) {
            return;
        }

        _nextBackLook = DateTime.UtcNow.AddSeconds(1);
        var request = Path.Combine(UltimusWire.Folder, UltimusWire.BACK_TO_STORY_FILE);
        if (!File.Exists(request)) {
            return;
        }

        try {
            File.Delete(request);
            var pause = Path.Combine(UltimusWire.Folder, UltimusWire.PAUSE_FILE);
            if (File.Exists(pause)) {
                File.Delete(pause);
            }
        }
        catch (IOException) {
            return;
        }

        _paused = false;
        Note("Ultimus", "BACK TO STORY (Ahziael): drops any side trip and plans again toward the story.", UltimusLink.COMMAND);
        Think("Niduus sent me back to the story from Ahziael: I drop any side trip (no side trips for 30 minutes) and plan again " +
              "toward my story quest.", "back-to-story|" + DateTime.UtcNow.Ticks);
        CloseSideTrips(TimeSpan.FromMinutes(30));
        _wispSweep = null;
        Replan();
    }

}
