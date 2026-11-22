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
using System.Text;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Staying on the story and on the map (Niduus, 2026-11-14: "our boy is on a journey in Sinbad. WAIT. THAT'S NOT A MAIN
/// QUEST!!", "MAke sure he respects the world/games boundaries"), and the approved extras of that day:
///   Story areas only   an area off the story (an event, a gauntlet, a house, a side world) that his step does not name
///                      is left first, before anything else: the way out on foot, else Ahziael takes him back to the
///                      last story area he was in (an unstuck move). It is written in the mistakes book.
///   Bounds watchdog    off the walkable ground (in a wall, on the Minimap's black) for a few seconds: back to the last
///                      spot on the floor he stood on, written in the mistakes book.
///   Arrival check      after each zone change: the area he meant and the spot the zone transfer book says he lands on;
///                      a wrong area is backed out of the same way (once for each way, so it can't loop).
///   Time-waste map     where he loses the most time (standing still, getting unstuck, a step he can't work out), by
///                      area and spot: logs/ultimus/time-waste.txt.
///   Nightly coach      [Ultimus] AutoCoach (off by default, live): each night all his logs in a dated folder
///                      (logs/ultimus/coach/date), each on its own plus one joint file.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Story areas only
    // ---------------------------------------------------------------------

    private string _lastStoryZone;
    private (string Zone, DateTime Since)? _offStory;
    private DateTime _offStoryCarried = DateTime.MinValue;

    /// <summary>Whether his quest's steps name this area (an area off the story he may be in).</summary>
    private bool StepNamesZone(string zone) {
        var quest = _currentQuest ?? _target?.Quest;
        if (string.IsNullOrEmpty(quest) || QuestTemplateCollection.GetQuestByName(quest) is not { } template) {
            return false;
        }

        return (template.m_goals ?? []).Where(g => g is not null)
            .Any(g => OverlayQuestTracker.StepZone(g) is { Length: > 0 } stepZone
                      && (OverlayQuestTracker.SameZone(stepZone, zone) || OverlayQuestTracker.SameZone(zone, stepZone)));
    }

    /// <summary>
    /// He is in an area off the story his step does not name: the way out first (a player who walked into the wrong
    /// portal walks back out). Null when he is on the story.
    /// </summary>
    private UltimusPlanner.Target LeaveOffStory(UltimusMember.Status leader) {
        var zone = leader.Zone ?? "";
        if (zone.Length == 0 || leader.State != "playing") {
            return null;
        }

        if (ResetCopy(leader, zone) is { } reset) {
            return reset;
        }

        var side = IsSideDungeon(zone);
        if (OverlayQuestRoute.OnStory(zone) && !side) {
            _lastStoryZone = zone;
            _offStory = null;
            return null;
        }

        if (!side && StepNamesZone(zone)) {
            return null;
        }

        if (side) {
            return LeaveSideDungeon(leader, zone);
        }

        if (_offStory is not { } off || !string.Equals(off.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            _offStory = off = (zone, DateTime.UtcNow);
            var why = $"{UltimusText.Zone(zone)} is not on the main story and my quest does not send me here";
            Note("Ultimus", $"ABNORMAL: is in {UltimusText.Zone(zone)}, off the main story: {why}. Leaving it comes first.", UltimusLink.ABNORMAL);
            Think($"I am in {UltimusText.Zone(zone)}. That is not a main story area and my quest does not send me here, so I leave it before " +
                  "anything else and write it down so the way that brought me here is never taken again.", "offstory|" + zone);
            BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.OFF_STORY, zone, leader.X, leader.Y, _currentQuest ?? "",
                $"Ended up in {UltimusText.Zone(zone)}, an area off the main story (from {UltimusText.Zone(_lastStoryZone ?? "")}).",
                _lastReasoning, "The route finder never goes through an event, a gauntlet, a house or a side world.", "leave it first");
        }

        var back = _lastStoryZone ?? "WizardCity/WC_Hub";
        // On foot, as a player walks back out. Niduus, 2026-11-14: "I don't want Ahziael to move Ultimus. I want Ultimus to
        // search and find his way": with no way out on foot he uses the buttons a player has (the Commons button, his
        // marked spot), and is never carried.
        if (UltimusPlanner.Travel(leader, back, $"leaving {UltimusText.Zone(zone)}: not on the main story", "offstory") is { } walk) {
            return walk;
        }

        if (DateTime.UtcNow - _offStoryCarried > TimeSpan.FromSeconds(60) && !Unsticking(0)) {
            _offStoryCarried = DateTime.UtcNow;
            Note("Ultimus", $"ABNORMAL: found no way out of {UltimusText.Zone(zone)} on foot: presses the Commons button to get back to the story, as a player does.",
                UltimusLink.ABNORMAL);
            if (!PressHubButton(leader, $"to get out of {UltimusText.Zone(zone)}")) {
                Aside(0, leader, 600f, 1600f, "looks around the area for a way out");
            }
        }

        return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"leaving {UltimusText.Zone(zone)}", Key: "offstory|wait");
    }

    // ---------------------------------------------------------------------
    // Side dungeons: a dungeon his story step is not in is left (Niduus, 2026-11-16: "he didn't leave Counterweight. I had
    // to help him finish it to get him to let go of it. This is a huge problem that MUST be addressed")
    // ---------------------------------------------------------------------

    private (string Zone, bool Side, DateTime At)? _sideCheck;
    private string _sideNoted;

    /// <summary>
    /// Whether this dungeon is a side dungeon for him now: he holds a story quest whose step is in another area, no quest
    /// of this dungeon is one the story needs, and he is not partway through a dungeon quest started where his story sent
    /// him (a dungeon's next room). Such a dungeon's own quest (given on the way in) is not his to finish: he walks out.
    /// </summary>
    private bool IsSideDungeon(string zone) {
        if (string.IsNullOrEmpty(zone) || !OverlayQuestRoute.IsDungeon(zone) || _team.Count == 0 || _team[0].Wizard is not { } wizard) {
            return false;
        }

        if (_sideCheck is { } check && string.Equals(check.Zone, zone, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - check.At < TimeSpan.FromSeconds(20)) {
            return check.Side;
        }

        var side = SideDungeonNow(zone, wizard);
        _sideCheck = (zone, side, DateTime.UtcNow);

        return side;
    }

    /// <summary>
    /// Whether two areas are rooms of the same gauntlet (".../DS_Plaza_Gauntlet_7Room" and
    /// ".../DS_Plaza_Gauntlet_7Room_Sub/DS_Plaza_Gauntlet_7Room_7"). Kept here rather than calling the server's own helper
    /// (OverlayQuestRoute.SameGauntlet), because a live-patched server can't show the bots a helper it gained in the patch
    /// (bots-v106 did not build on v0.10.951, 2026-11-21).
    /// </summary>
    private static bool SameGauntletRooms(string zone, string goal) {
        var a = GauntletRoot(zone);
        return a.Length > 0 && string.Equals(a, GauntletRoot(goal), StringComparison.OrdinalIgnoreCase);
    }

    private static string GauntletRoot(string zone) {
        if (string.IsNullOrWhiteSpace(zone) || OverlayQuestRoute.EventArea(zone)) {
            return "";
        }

        var parts = zone.Trim().Split('/');
        for (var i = 0; i < parts.Length; i++) {
            if (parts[i].Contains("Gauntlet", StringComparison.OrdinalIgnoreCase)) {
                var name = parts[i].EndsWith("_Sub", StringComparison.OrdinalIgnoreCase) ? parts[i][..^4] : parts[i];
                return string.Join('/', parts[..i].Append(name));
            }
        }

        return "";
    }

    private bool SideDungeonNow(string zone, Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) {
        // A dungeon quest started in a room his story sent him to goes on in the dungeon's other rooms (Ironworks and
        // Nail's Hideout, 2026-11-15).
        if (_dungeonFirstQuest is { Length: > 0 } going && wizard.HasQuest(going)) {
            return false;
        }

        // A room whose own spawn files hold the creature of one of his open fight steps (the Tree of Life's Spirit World
        // room holds Nomoonaga): his step is played here (BotShared/BotRoomFoes).
        if (BotShared.BotRoomFoes.StepFoeRoom(wizard, zone) is not null) {
            return false;
        }

        var held = wizard.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).Select(q => q.QuestName).ToList() ?? [];
        // A quest of this dungeon the story needs done first: the dungeon is on the story.
        var own = DungeonQuestIndex.GetQuestsForZones([zone]).Select(q => (string) q.m_questName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (held.Any(name => own.Contains(name) && BotStory.NeededByStory(name))) {
            return false;
        }

        var story = held.Where(name => !DungeonQuestIndex.IsDungeonQuest(name)
            && QuestTemplateCollection.GetQuestByName(name) is { } t && QuestStory.IsStory(t)).ToList();
        if (story.Count == 0) {
            return false;
        }

        foreach (var name in story) {
            var template = QuestTemplateCollection.GetQuestByName(name);
            if ((template?.m_goals ?? []).Where(g => g is not null).Any(g => OverlayQuestTracker.StepZone(g) is { Length: > 0 } stepZone
                    && (OverlayQuestTracker.SameZone(stepZone, zone) || OverlayQuestTracker.SameZone(zone, stepZone)
                        || SameGauntletRooms(zone, stepZone)))) {
                // A gauntlet's rooms are one dungeon: room 2 of Halina Warhawk's Tower is on the way to the book in room 7
                // ("The Art of War", 2026-11-21), not a side trip.
                return false;
            }

            // Where the step really is; when that can't be told, he stays (no leaving on a guess).
            var step = UltimusPlanner.ForQuest(_team, name);
            if (step is null || step.Problem.Length > 0 || string.IsNullOrEmpty(step.To) || OverlayQuestTracker.SameZone(step.To, zone)
                || SameGauntletRooms(zone, step.To)) {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // A dungeon copy that can't give his step any more: out and back in, for a fresh copy (Niduus, 2026-11-20, "Yes, leave
    // and come back": Kurrent, Ra and Mythis stood 2 hours in The Ironworks by a Pops O'Leary that never fought them)
    // ---------------------------------------------------------------------

    // How long his step's creature must be missing (with no story progress) before he walks out to start the copy over.
    private static readonly TimeSpan s_resetAfter = TimeSpan.FromMinutes(20);
    private DateTime? _noFoeSince;
    private string _noFoeZone;
    private (string Zone, DateTime At)? _resetting;
    // When his step's own creature in a dungeon was last stood on with no battle (NoticeOddities), and how many quick
    // fresh copies each dungeon had this hour: a boss that won't fight after a lost battle (Tulkar by Jacques in Knight's
    // Tower) or after a mate's win (Kurrent and Mythis by Pops O'Leary) needs no 20-minute wait, and bot updates every
    // few minutes restart that wait anyway.
    private (string Zone, DateTime At, string Step)? _dudStepAt;
    // The step set aside while he rests beside it (UltimusDirector.Aside.cs), so the rest still counts for that step.
    private UltimusPlanner.Target _asideStep;
    private readonly Dictionary<string, List<DateTime>> _quickResets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In a dungeon whose step's creature is not there to fight (none in the area, or only one that starts no battle) for
    /// 20 minutes with no story progress: he walks out the way he came in, so walking in again makes a fresh copy, as a
    /// player does. Not fleeing: no fight is left. Null otherwise.
    /// </summary>
    private UltimusPlanner.Target ResetCopy(UltimusMember.Status leader, string zone) {
        var now = DateTime.UtcNow;
        if (_resetting is { } going) {
            if (!OverlayQuestTracker.SameZone(going.Zone, zone) || now - going.At > TimeSpan.FromMinutes(10)) {
                _resetting = null;
                _noFoeSince = null;
            }
            else if (now - going.At > s_resetWalkMost && ResetByButton(leader, zone, now)) {
                return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"leaving {UltimusText.Zone(zone)} by the Commons button to start it over",
                    Key: "resetcopy|button");
            }
            else if (leader.Wizard?.SafeZone is { Length: > 0 } outside
                     && UltimusPlanner.Travel(leader, outside, $"leaving {UltimusText.Zone(zone)} to start it over (its creature for my step is not there)", "resetcopy") is { } walk) {
                return walk;
            }
            else {
                _resetting = null;
            }
        }

        if (leader.Wizard?.ZoneIsInstance != true || _partyMate != 0 || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            _noFoeSince = null;
            return null;
        }

        // While he rests beside a set-aside step, the step is what counts (the rest itself names no quest): Idunn rested by
        // a Bellows that would not fight for 40 minutes, and the rest never counted as "its creature is not there".
        var step = _target?.Key?.StartsWith("aside|wait|", StringComparison.Ordinal) == true && _asideStep is { } aside ? aside : _target;
        var parked = ParkedInCopy(zone, now);
        var noFoe = parked || FirstQuestDud(zone, now) || step is { } last && !string.IsNullOrEmpty(last.Quest)
                    && (last.Problem == "no creature for this step is in the area"
                        || UseDud(zone, last, now)
                        || (last.Kind == UltimusPlanner.WAIT && last.What.StartsWith("resting while", StringComparison.Ordinal))
                        || last.Key.EndsWith("|spawns", StringComparison.Ordinal));
        if (!noFoe) {
            return null;
        }

        if (_noFoeSince is null || !string.Equals(_noFoeZone, zone, StringComparison.OrdinalIgnoreCase)) {
            _noFoeSince = now;
            _noFoeZone = zone;
            return null;
        }

        // Things to use found all used up at the step's own spot is plain to see, so it waits 3 minutes, not 20: every bots
        // update restarts the 20 minutes, and Cyndr stood by Katz Lab's used crates through four updates (2026-11-20).
        var quick = !parked && QuickReset(zone, now, step)
                    || (step is not null && UseDud(zone, step, now) && now - _noFoeSince >= TimeSpan.FromMinutes(3) && QuickLeft(zone, now));
        // Real progress only (a step, a quest, a level, XP, a won quest fight): a stuck fix that "got free" or a group check
        // counts as progress too, and kept Cyndr's Katz Lab copy from ever starting over (2026-11-20, two hours by used-up crates).
        // Every fix failing twice over already took many minutes of trying; it waits 5 more, not 20 with no real progress:
        // the 20 minutes start over at each bots update, and those came every 20 minutes while Garm and Ra stood behind Big
        // Ben's shut Door43 (2026-11-20, 10:34 to past 12:00).
        var parkedLongEnough = parked && now - _noFoeSince >= s_parkedResetAfter;
        if (!quick && !parkedLongEnough && (now - _noFoeSince < s_resetAfter || now - _realProgressAt < s_resetAfter)) {
            return null;
        }

        if (leader.Wizard.SafeZone is not { Length: > 0 } safe) {
            return null;
        }

        if (quick) {
            _quickResets[zone].Add(now);
            _dudStepAt = null;
        }

        _resetting = (zone, now);
        if (parked) {
            Note("Ultimus", $"ABNORMAL: in {UltimusText.Zone(zone)} every fix failed twice over for \"{step?.What ?? "his step"}\" and nothing moved on for " +
                            $"{(now - _noFoeSince.Value).TotalMinutes:0} minutes: walks out to {UltimusText.Zone(safe)} and back in for a fresh copy.", UltimusLink.ABNORMAL);
            Think($"Nothing I tried in {UltimusText.Zone(zone)} got me on, twice over. A player walks out and comes back in, and the dungeon " +
                  "starts fresh with its doors and creatures back, so I do that.", "resetcopy|" + zone);

            return UltimusPlanner.Travel(leader, safe, $"leaving {UltimusText.Zone(zone)} to start it over (every fix failed here)", "resetcopy");
        }

        Note("Ultimus", $"ABNORMAL: in {UltimusText.Zone(zone)} " + (UseDud(zone, step, now) ? "what is used" : "the creature") + $" for \"{step.What}\" " + (quick ? (UseDud(zone, step, now) ? "was all used up" : "stood still and started no battle") : $"has not been there to fight for {(now - _noFoeSince.Value).TotalMinutes:0} minutes") +
                        $": walks out to {UltimusText.Zone(safe)} and back in for a fresh copy.", UltimusLink.ABNORMAL);
        Think($"The creature my step needs in {UltimusText.Zone(zone)} has not been there to fight for a long time, and nothing else here is " +
              "left to do. A player walks out and comes back in, and the dungeon starts fresh, so I do that.", "resetcopy|" + zone);

        return UltimusPlanner.Travel(leader, safe, $"leaving {UltimusText.Zone(zone)} to start it over (its creature for my step is not there)", "resetcopy");
    }

    // How long the walk out to start a copy over may go on before he presses the Commons button instead.
    private static readonly TimeSpan s_resetWalkMost = TimeSpan.FromMinutes(4);
    private (string Zone, DateTime At)? _resetPressed;

    /// <summary>
    /// The walk out of a dungeon copy to start it over has gone on for minutes and he is still in it: the way back is shut
    /// (Big Ben is a climb of one-way floor teleporters, and a floor's only way on is the door its creature opens). A player
    /// presses the Commons button there and walks back in for a fresh copy, so he does too, once per walk out. Kitharas
    /// Hexation and Sentence (2026-11-22 00:47-01:30) stood on Big Ben's 7th floor by a shut Door14 whose Golem Brass
    /// Elite never came: "leaving Big Ben to start it over" walked them back to that same door 18 times. True when pressed.
    /// </summary>
    private bool ResetByButton(UltimusMember.Status leader, string zone, DateTime now) {
        if (_resetPressed is { } pressed && OverlayQuestTracker.SameZone(pressed.Zone, zone) && now - pressed.At < TimeSpan.FromMinutes(10)) {
            return now - pressed.At < TimeSpan.FromSeconds(30);
        }

        if (leader.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(leader.GlobalId) || (wizard.MagicSchoolBehavior?.Level ?? 1) < 2
            || now - _hubPressedAt < TimeSpan.FromSeconds(20)) {
            return false;
        }

        _resetPressed = (zone, now);
        _hubPressedAt = now;
        _buttonFromZone = leader.Zone;
        StopWalking(0);
        Note("Ultimus", $"ABNORMAL: {leader.Name} has been walking out of {UltimusText.Zone(zone)} to start it over for " +
                        $"{(now - (_resetting?.At ?? now)).TotalMinutes:0} minutes and is still in it (the way back is shut): presses the Commons button, " +
                        "as a player does, and walks back in for a fresh copy.", UltimusLink.ABNORMAL);
        Think($"I can't walk out of {UltimusText.Zone(zone)}: the way back is shut. A player presses the Commons button and comes back in, " +
              "and the dungeon starts fresh, so I do that.", "resetbutton|" + zone);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0], new UltimusMember.PressCommons(), Self);

        return true;
    }

    /// <summary>
    /// True when his step's creature here was stood on with no battle in the last 5 minutes and this dungeon had fewer
    /// than 2 quick fresh copies in the last hour (so a creature that never fights in any copy falls back to the 20-minute
    /// wait instead of a walk out every minute).
    /// </summary>
    private bool QuickReset(string zone, DateTime now, UltimusPlanner.Target step) {
        // Only for the step whose creature it was: Garm (2026-11-20 01:06) had stood on a Metal Madness creature in Counterweight
        // East that started no battle, finished Metal Madness, and the very next step ("Stolen Away", Sprockets a floor up)
        // read that as its own and walked him out at once; the fresh copy's levers no longer work for him, and he stood
        // at the first shut gate for over an hour.
        // Or the dungeon's own quest done first (DungeonQuestFirst), whose boss started no battle while his main step is
        // the way on: Idunn rested beside the way out of Shirataki Temple while Shoji, the dud, was River Temple's step.
        if (_dudStepAt is not { } dud || !OverlayQuestTracker.SameZone(dud.Zone, zone) || now - dud.At > TimeSpan.FromMinutes(5)
            || dud.Step != StepKey(step) && !FirstQuestDud(zone, now)) {
            return false;
        }

        return QuickLeft(zone, now);
    }

    /// <summary>
    /// The step's creature that started no battle here (in the last 5 minutes) belongs to the dungeon's own quest that
    /// DungeonQuestFirst does first: that quest can't go on in this copy, so the copy starts over even though his main step
    /// (the way on) is what he is at.
    /// </summary>
    private bool FirstQuestDud(string zone, DateTime now)
        => _dudStepAt is { } dud && OverlayQuestTracker.SameZone(dud.Zone, zone) && now - dud.At <= TimeSpan.FromMinutes(5)
           && _dungeonFirstQuest is { Length: > 0 } first && dud.Step.StartsWith(first + "|", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this dungeon had fewer than 2 quick fresh copies in the last hour.</summary>
    private bool QuickLeft(string zone, DateTime now) {
        if (!_quickResets.TryGetValue(zone, out var times)) {
            _quickResets[zone] = times = [];
        }

        times.RemoveAll(t => now - t > TimeSpan.FromHours(1));
        return times.Count < 2;
    }

    // When his step's things to use were last found all used up in a dungeon copy (nothing offered at the spot, or nothing
    // more found looking around): Cyndr and Kurrent stood hours in Katzenstein's Lab by "Collect Crates", whose crates in
    // that copy were already used while the step (an instance quest, started over by a restart) still wanted all three.
    private (string Zone, DateTime At, string Step)? _useDudAt;

    /// <summary>The step's things to use are used up here: noted for the fresh-copy check (ResetCopy).</summary>
    private void NoteUseDud(int index, UltimusMember.Status member, UltimusPlanner.Target target) {
        if (index == 0 && member.Wizard?.ZoneIsInstance == true && !string.IsNullOrEmpty(target.Quest)) {
            _useDudAt = (member.Zone ?? "", DateTime.UtcNow, StepKey(target));
        }
    }

    /// <summary>Whether this step's things to use were found used up in this dungeon in the last 10 minutes.</summary>
    private bool UseDud(string zone, UltimusPlanner.Target step, DateTime now)
        => _useDudAt is { } dud && OverlayQuestTracker.SameZone(dud.Zone, zone) && now - dud.At < TimeSpan.FromMinutes(10) && dud.Step == StepKey(step);

    // When every fix failed twice over in a dungeon copy (ParkStep), for the fresh-copy check.
    private (string Zone, DateTime At)? _parkedInCopyAt;
    private static readonly TimeSpan s_parkedResetAfter = TimeSpan.FromMinutes(5);

    /// <summary>Whether every fix failed twice over in this dungeon copy in the last 30 minutes.</summary>
    private bool ParkedInCopy(string zone, DateTime now)
        => _parkedInCopyAt is { } p && OverlayQuestTracker.SameZone(p.Zone, zone) && now - p.At < TimeSpan.FromMinutes(30);

    /// <summary>The quest and goal a target belongs to, so a dud creature counts only for its own step.</summary>
    private static string StepKey(UltimusPlanner.Target target) => target is null ? "" : $"{target.Quest}|{target.Goal}";

    // Fight steps whose creature was stood on with no battle, by step (quest|goal), until when they are left alone. The
    // creature's own mark (BotShared.DudCreatures) misses a step whose target has no live creature (GlobalId 0) or a new
    // one each look: Idunn, Shirataki Temple (2026-11-22 03:34-03:46, v160) set Shoji aside, then "another quest right
    // here" picked "Defeat and Collect Shoji" again every 5 seconds, back and forth with the way out, about 140 times.
    private readonly Dictionary<string, DateTime> _dudSteps = new(StringComparer.OrdinalIgnoreCase);

    private bool DudStep(UltimusPlanner.Target step) {
        if (step is null || string.IsNullOrEmpty(step.Quest) || !_dudSteps.TryGetValue(StepKey(step), out var until)) {
            return false;
        }

        if (DateTime.UtcNow < until) {
            return true;
        }

        _dudSteps.Remove(StepKey(step));
        return false;
    }

    // How long the walk out of a side dungeon may go on (with no real story progress) before he presses the Commons button.
    private static readonly TimeSpan s_sideWalkMost = TimeSpan.FromMinutes(10);
    private (string Zone, DateTime At, DateTime Last)? _sideSince;
    private DateTime _sidePressedAt = DateTime.MinValue;

    /// <summary>
    /// The walk out of a side dungeon has gone on for minutes and he is still in it: the way out on foot is shut or never
    /// fires (Big Ben is a climb of one-way floor teleporters). A player presses the Commons button there, and it leads to
    /// this world's hub, so he does too, at most once every 5 minutes. Unlike a trip for potions, his spot is not marked:
    /// Recall would only bring him back into the dungeon he is leaving. Kitharas Rock (2026-11-22 00:00-03:31, Marleybone
    /// finished, next step in Wizard City) walked "to Regent's Square (leaving Big Ben)" for three and a half hours: the
    /// map showed no way, the guided run took the floor teleporter back up, and the Commons button was only pressed when
    /// no walk was found. Moon (since 02:15) and Hart did the same. True when pressed (or just pressed).
    /// </summary>
    private bool SideByButton(UltimusMember.Status leader, string zone, TimeSpan walked) {
        var now = DateTime.UtcNow;
        if (now - _sidePressedAt < TimeSpan.FromMinutes(5)) {
            return now - _sidePressedAt < TimeSpan.FromSeconds(30);
        }

        if (leader.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(leader.GlobalId) || (wizard.MagicSchoolBehavior?.Level ?? 1) < 2
            || now - _hubPressedAt < TimeSpan.FromSeconds(20)) {
            return false;
        }

        _sidePressedAt = now;
        _hubPressedAt = now;
        _buttonFromZone = leader.Zone;
        StopWalking(0);
        Note("Ultimus", $"ABNORMAL: {leader.Name} has been walking out of {UltimusText.Zone(zone)} (a side dungeon) for {walked.TotalMinutes:0} minutes " +
                        "and is still in it: presses the Commons button, as a player does, to get back to the story.", UltimusLink.ABNORMAL);
        Think($"I have tried to walk out of {UltimusText.Zone(zone)} for {walked.TotalMinutes:0} minutes and I am still here. A player presses " +
              "the Commons button, so I do that and go on with my story from the hub.", "sidebutton|" + zone);
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[0], new UltimusMember.PressCommons(), Self);

        return true;
    }

    /// <summary>Out of a side dungeon: on foot toward his story step, as a player walks back out of the wrong dungeon.</summary>
    private UltimusPlanner.Target LeaveSideDungeon(UltimusMember.Status leader, string zone) {
        var quest = _team[0].Wizard?.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).Select(q => q.QuestName)
            .FirstOrDefault(name => !DungeonQuestIndex.IsDungeonQuest(name) && QuestTemplateCollection.GetQuestByName(name) is { } t && QuestStory.IsStory(t));
        var step = quest is null ? null : UltimusPlanner.ForQuest(_team, quest);
        var to = step?.To is { Length: > 0 } stepTo ? stepTo : _lastStoryZone ?? "WizardCity/WC_Hub";
        _dungeonFirstQuest = null;

        if (!string.Equals(_sideNoted, zone, StringComparison.OrdinalIgnoreCase)) {
            _sideNoted = zone;
            Note("Ultimus", $"ABNORMAL: is in {UltimusText.Zone(zone)}, a side dungeon: his story step " +
                (step is null ? "" : $"(\"{step.What}\" in {UltimusText.Title(quest)}) ") + $"is in {UltimusText.Zone(to)}. He walks out; its own quest is left for later.",
                UltimusLink.ABNORMAL);
            Think($"I am in {UltimusText.Zone(zone)}, but my story step is in {UltimusText.Zone(to)}. This dungeon is a side trip, and its quest " +
                  "is not mine to finish now, so I walk out and go on with my story. Leaving a dungeon my story is not in is not fleeing.", "sidedungeon|" + zone);
            BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.OFF_STORY, zone, leader.X, leader.Y, quest ?? "",
                $"Was in {UltimusText.Zone(zone)}, a side dungeon his story step is not in.", _lastReasoning,
                "Never go into a dungeon the story step is not in (not for wisps, not for potions).", "walk out");
        }

        var now = DateTime.UtcNow;
        if (_sideSince is not { } since || !OverlayQuestTracker.SameZone(since.Zone, zone) || now - since.Last > TimeSpan.FromMinutes(8)) {
            since = (zone, now, now);
        }

        _sideSince = (zone, since.At, now);
        if (now - since.At > s_sideWalkMost && now - _realProgressAt > s_sideWalkMost && SideByButton(leader, zone, now - since.At)) {
            return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"leaving {UltimusText.Zone(zone)} by the Commons button (a side dungeon)",
                Key: "sidedungeon|button");
        }

        if (UltimusPlanner.Travel(leader, to, $"leaving {UltimusText.Zone(zone)}: a side dungeon, my story is in {UltimusText.Zone(to)}", "sidedungeon") is { } walk) {
            return walk;
        }

        if (to != (_lastStoryZone ?? "") && _lastStoryZone is { Length: > 0 } last
            && UltimusPlanner.Travel(leader, last, $"leaving {UltimusText.Zone(zone)}: a side dungeon", "sidedungeon") is { } back) {
            return back;
        }

        if (DateTime.UtcNow - _offStoryCarried > TimeSpan.FromSeconds(60) && !Unsticking(0)) {
            _offStoryCarried = DateTime.UtcNow;
            Note("Ultimus", $"ABNORMAL: found no way out of {UltimusText.Zone(zone)} on foot: presses the Commons button, as a player does.", UltimusLink.ABNORMAL);
            if (!PressHubButton(leader, $"to get out of {UltimusText.Zone(zone)}")) {
                Aside(0, leader, 600f, 1600f, "looks around the area for a way out");
            }
        }

        return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"leaving {UltimusText.Zone(zone)}", Key: "sidedungeon|wait");
    }

    // ---------------------------------------------------------------------
    // Arrival check: the area he meant, and the landing the zone transfer book says
    // ---------------------------------------------------------------------

    private readonly HashSet<string> _backedOut = new(StringComparer.OrdinalIgnoreCase);
    private (string To, string Wrong, DateTime At)? _backOut;

    /// <summary>
    /// A zone change: did he land where the book says? And when the way went to another area than he meant, back out the
    /// same way (once for each way and area: a second time he plans on from where he is).
    /// </summary>
    private void CheckLanding(string fromZone, float fromX, float fromY, UltimusMember.Status leader, string meant) {
        if (string.IsNullOrEmpty(fromZone) || string.IsNullOrEmpty(leader.Zone)) {
            return;
        }

        if (UltimusTransfers.Expected(fromZone, leader.Zone, fromX, fromY) is { } expected) {
            var off = UltimusPlanner.Far(expected.LandX, expected.LandY, leader.X, leader.Y);
            if (off > 600) {
                Think($"The zone transfer book says this way lands at ({expected.LandX:0}, {expected.LandY:0}, {expected.LandZ:0}) in " +
                      $"{UltimusText.Zone(leader.Zone)}, but I am at ({leader.X:0}, {leader.Y:0}), {off:0} away. I note it.", "landing|" + leader.Zone);
                BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.SURPRISE, leader.Zone, leader.X, leader.Y, meant ?? "",
                    $"Landed {off:0} away from where the zone transfer book says ({expected.Loc}) coming from {UltimusText.Zone(fromZone)}.",
                    "Each zone change is checked against the book.", "The book's landing spot or the game's needs a look.");
            }
        }

        if (string.IsNullOrEmpty(meant) || OverlayQuestTracker.SameZone(meant, leader.Zone) || OverlayQuestTracker.SameZone(leader.Zone, meant)) {
            return;
        }

        // Into another world, the one he meant (the Spiral Door sets travellers down where its door stands, not always
        // the world's main area): the right world, so he plans on from here instead of backing out.
        if (OverlayQuestRoute.SameWorld(meant, leader.Zone) && !OverlayQuestRoute.SameWorld(fromZone, leader.Zone)) {
            return;
        }

        // A room of the same dungeon that his fight step's creature waits in (it comes out on the room's battle circle): the
        // right way in, not a wrong turn. The Tree of Life, 2026-11-20: the Spirit World room holds Nomoonaga, and backing
        // out of it each time sent him round in a loop for 20 minutes (BotShared/BotRoomFoes).
        if (leader.Wizard?.ZoneIsInstance == true && OverlayQuestRoute.IsDungeon(fromZone)
            && BotShared.BotRoomFoes.StepFoeRoom(leader.Wizard, leader.Zone) is { } fightQuest) {
            Think($"I meant to stay in {UltimusText.Zone(meant)}, but this room ({UltimusText.Zone(leader.Zone)}) is where the creature " +
                  $"of my fight step in {UltimusText.Title(fightQuest)} waits, so this is the right way in. I stay and find it.",
                "room-foe|" + leader.Zone);
            return;
        }

        // The spot he left from is by a door that goes elsewhere: the way finder bends round it from now on, for him and the
        // other bots (RouteMemory's stuck spots), so the walk back out does not take the same door again. Kitharas Hydra,
        // 2026-11-21: on the way to Nightside the walk through The Commons went into Merle Ambrose's Tower 9 times in 3
        // minutes, and each walk out set off through the same door.
        if (fromX != 0 || fromY != 0) {
            RouteMemory.Stuck(leader.CharId, fromZone, fromX, fromY);
        }

        var key = fromZone + ">" + leader.Zone;
        if (!_backedOut.Add(key)) {
            return;
        }

        _backOut = (fromZone, leader.Zone, DateTime.UtcNow);
        Note("Ultimus", $"meant to go to {UltimusText.Zone(meant)} but is in {UltimusText.Zone(leader.Zone)}: backs out the same way to {UltimusText.Zone(fromZone)}.",
            UltimusLink.ABNORMAL);
    }

    /// <summary>Backing out of an area the way went to by mistake: the way back to where he came from.</summary>
    private UltimusPlanner.Target BackOut(UltimusMember.Status leader) {
        if (_backOut is not { } back) {
            return null;
        }

        if (!string.Equals(leader.Zone, back.Wrong, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - back.At > TimeSpan.FromMinutes(3)) {
            _backOut = null;
            return null;
        }

        return UltimusPlanner.Travel(leader, back.To, $"backing out of {UltimusText.Zone(back.Wrong)}", "backout");
    }

    // ---------------------------------------------------------------------
    // Bounds watchdog
    // ---------------------------------------------------------------------

    private (string Zone, float X, float Y, float Z)? _legalSpot;
    private DateTime? _offFloorSince;
    private DateTime _boundsFixedAt = DateTime.MinValue;

    private void BoundsTick(UltimusMember.Status leader) {
        if (string.IsNullOrEmpty(leader.Zone) || leader.State != "playing" || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || DateTime.UtcNow - leader.AttachedAt < TimeSpan.FromSeconds(6)) {
            _offFloorSince = null;
            return;
        }

        if (OverlayQuestPath.OnFloor(leader.Zone, leader.X, leader.Y)) {
            _legalSpot = (leader.Zone, leader.X, leader.Y, leader.Z);
            _offFloorSince = null;
            return;
        }

        // Just by the floor (a doorway, the grid is coarse): not out of bounds.
        if (OverlayQuestPath.NearestFloor(leader.Zone, leader.X, leader.Y) is { } near && UltimusPlanner.Far(near.X, near.Y, leader.X, leader.Y) < 90) {
            _offFloorSince = null;
            return;
        }

        _offFloorSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _offFloorSince < TimeSpan.FromSeconds(3) || DateTime.UtcNow - _boundsFixedAt < TimeSpan.FromSeconds(20)) {
            return;
        }

        _boundsFixedAt = DateTime.UtcNow;
        _offFloorSince = null;
        var where = $"{UltimusText.Zone(leader.Zone)} at ({leader.X:0}, {leader.Y:0})";
        if (_legalSpot is not { } spot || !string.Equals(spot.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            Note("Ultimus", $"ABNORMAL: is off the walkable ground in {where} and has no spot on the floor here to go back to.", UltimusLink.ABNORMAL);
            return;
        }

        Note("Ultimus", $"ABNORMAL: OUT OF BOUNDS in {where} (off the walkable ground): walks back to ({spot.X:0}, {spot.Y:0}), the last spot on the floor he stood on.",
            UltimusLink.ABNORMAL);
        Think("I am off the walkable ground, where no player can stand. I go back to the last spot on the floor I stood on and write it down.",
            "bounds|" + leader.Zone);
        BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.OUT_OF_BOUNDS, leader.Zone, leader.X, leader.Y, _target?.What ?? _doing ?? "",
            $"Walked off the walkable ground in {where}; walked back to ({spot.X:0}, {spot.Y:0}).", _lastReasoning,
            "Every walk is checked against the floor; this spot's walk data or the step's spot needs a look.", "walk back");
        // He walks back himself (Niduus, 2026-11-14: Ahziael never moves him).
        OverlayQuestPath.Forget(leader.CharId);
        _actors[0].Tell(new UltimusMember.WalkTo([(spot.X, spot.Y)], "walks back onto the walkable ground"));
    }

    // ---------------------------------------------------------------------
    // Time-waste map
    // ---------------------------------------------------------------------

    private const string WASTE_FILE = "time-waste.txt";
    private const string WASTE_DATA = "time-waste.json";
    private const float WASTE_CELL = 500f;
    private Dictionary<string, WasteSpot> _waste;
    private DateTime _wasteTick = DateTime.MinValue;
    private DateTime _wasteWritten = DateTime.UtcNow;
    private (float X, float Y, DateTime At) _wasteStill = (0, 0, DateTime.UtcNow);

    internal sealed class WasteSpot {
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public double Seconds { get; set; }
        public Dictionary<string, double> Why { get; set; } = [];
        public string LastStep { get; set; } = "";
        public string LastQuest { get; set; } = "";
    }

    private void WasteTick(UltimusMember.Status leader) {
        var now = DateTime.UtcNow;
        var dt = (now - _wasteTick).TotalSeconds;
        _wasteTick = now;
        if (dt <= 0 || dt > 10 || string.IsNullOrEmpty(leader.Zone) || leader.State != "playing" || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || leader.Talking) {
            return;
        }

        if (UltimusPlanner.Far(leader.X, leader.Y, _wasteStill.X, _wasteStill.Y) > 60) {
            _wasteStill = (leader.X, leader.Y, now);
        }

        string why = null;
        if (Unsticking(0)) {
            why = "getting unstuck";
        }
        else if (_target?.Kind == UltimusPlanner.NONE) {
            why = "a step he can't work out";
        }
        else if (now - _wasteStill.At > TimeSpan.FromSeconds(15) && !Pausing() && now >= _breakUntil && !_grabWaiting) {
            why = "standing still";
        }

        if (why is null) {
            return;
        }

        _waste ??= ReadWaste();
        var (cx, cy) = ((int) MathF.Floor(leader.X / WASTE_CELL), (int) MathF.Floor(leader.Y / WASTE_CELL));
        var key = $"{leader.Zone}|{cx}|{cy}";
        if (!_waste.TryGetValue(key, out var spot)) {
            _waste[key] = spot = new WasteSpot { Zone = leader.Zone, X = (cx + 0.5f) * WASTE_CELL, Y = (cy + 0.5f) * WASTE_CELL };
        }

        spot.Seconds += dt;
        spot.Why[why] = spot.Why.GetValueOrDefault(why) + dt;
        spot.LastStep = _target?.What ?? _doing ?? "";
        StuckToday(key, leader.Zone, spot.X, spot.Y, why, dt, spot.LastStep, _target?.Quest ?? _currentQuest ?? "");
        if (now - _wasteWritten > TimeSpan.FromMinutes(5)) {
            _wasteWritten = now;
            WriteWaste();
            WriteStuckDays();
        }
    }

    // ---------------------------------------------------------------------
    // The day's "most stuck" (Niduus, 2026-11-14): the 3 spots he lost the most time at today, with a suggested fix,
    // for his Daily diary in Ahziael (stuck-days.json, the last 14 days).
    // ---------------------------------------------------------------------

    private const string STUCK_DAYS_FILE = "stuck-days.json";

    internal sealed class StuckDays {
        public string Date { get; set; } = "";
        public Dictionary<string, WasteSpot> Today { get; set; } = [];
        public Dictionary<string, List<string>> Days { get; set; } = [];

        // The spots (area|cell) of each day's top 3, to see one that is "most stuck" 3 days running.
        public Dictionary<string, List<string>> DayKeys { get; set; } = [];

        // Each day's 10 worst spots with their minutes, summed into the weekly report (Niduus, 2026-11-14).
        public Dictionary<string, Dictionary<string, WasteSpot>> DaySpots { get; set; } = [];

        // The last 7 days' 5 worst spots, for the top of his Daily diary in Ahziael.
        public List<string> Week { get; set; } = [];

        // The times the watchdog stopped him (two hours with nothing done), "yyyy-MM-dd|HH:mm|what", for the weekly report
        // (Niduus, 2026-11-14).
        public List<string> Stops { get; set; } = [];

        // The Monday of the week the weekly report was last written to his log.
        public string WeekNoted { get; set; } = "";

        // Back-and-forth trips by day (yyyy-MM-dd -> count), for the stuck chart in Ahziael (Niduus, 2026-11-15).
        public Dictionary<string, int> BackAndForth { get; set; } = [];
    }

    // ---------------------------------------------------------------------
    // Back-and-forth trips (Niduus, 2026-11-15, "all"): the same area reached a third time within 15 minutes on the same
    // quest is one trip there and back that led nowhere (his log, 2026-11-14: The Ironworks and Digmoore Station again and
    // again on "Out of Control"). Counted per day for the stuck chart.
    // ---------------------------------------------------------------------

    private readonly List<(string Zone, string Quest, DateTime At)> _zoneTrail = [];

    private void CountBackAndForth(UltimusMember.Status leader) {
        var zone = leader.Zone;
        if (string.IsNullOrEmpty(zone)) {
            return;
        }

        var now = DateTime.UtcNow;
        var quest = _currentQuest ?? "";
        _zoneTrail.RemoveAll(z => now - z.At > TimeSpan.FromMinutes(15) || !string.Equals(z.Quest, quest, StringComparison.Ordinal));
        _zoneTrail.Add((zone, quest, now));
        if (quest.Length == 0 || _zoneTrail.Count(z => string.Equals(z.Zone, zone, StringComparison.OrdinalIgnoreCase)) < 3) {
            return;
        }

        _zoneTrail.RemoveAll(z => string.Equals(z.Zone, zone, StringComparison.OrdinalIgnoreCase));
        _zoneTrail.Add((zone, quest, now));
        _stuckDays ??= ReadStuckDays();
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _stuckDays.BackAndForth ??= [];
        _stuckDays.BackAndForth[today] = _stuckDays.BackAndForth.GetValueOrDefault(today) + 1;
        Count("back-and-forth trips");
        Think($"I am back in {UltimusText.Zone(zone)} for the third time in a few minutes on {UltimusText.Title(quest)}: going back and forth " +
              "is not getting me anywhere, so I count it as time lost and think about what I missed.", "back-and-forth|" + zone + "|" + now.Ticks / TimeSpan.TicksPerMinute);
        // The alarm acts (Niduus, 2026-11-15): it only counted before, and he went on 221 times. Two goals taking turns: the
        // flip-flop breaker holds to one; one goal whose way keeps bouncing: the loop guard breaks out with a new plan.
        if (now - _realProgressAt > TimeSpan.FromMinutes(5) && _hold is null) {
            if (UltimusRules.On(UltimusRules.FLIP_FLOP) && FlipFlop(now, need: 3) is var (a, b, switches)) {
                StartHold(leader, a, b, switches, round: 1);
            }
            else if (now - _loopBrokeAt > TimeSpan.FromMinutes(5)) {
                BreakLoop(leader, "area|" + zone, 3);
            }
        }
    }

    private StuckDays _stuckDays;

    private void StuckToday(string key, string zone, float x, float y, string why, double seconds, string step, string quest) {
        _stuckDays ??= ReadStuckDays();
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_stuckDays.Date != today) {
            _stuckDays.Date = today;
            _stuckDays.Today = [];
        }

        if (!_stuckDays.Today.TryGetValue(key, out var spot)) {
            _stuckDays.Today[key] = spot = new WasteSpot { Zone = zone, X = x, Y = y };
        }

        spot.Seconds += seconds;
        spot.Why[why] = spot.Why.GetValueOrDefault(why) + seconds;
        spot.LastStep = step;
        if (!string.IsNullOrEmpty(quest)) {
            spot.LastQuest = quest;
        }
    }

    // A spot in the day's top 3 that was also in the top 3 on each of the 2 days before: filed as a broken quest by
    // itself (Niduus, 2026-11-14), once a day, with the quest he was on there.
    private void StuckThreeDays(List<KeyValuePair<string, WasteSpot>> today) {
        if (!DateTime.TryParseExact(_stuckDays.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) {
            return;
        }

        string Day(int back) => date.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, spot) in today) {
            if (!Enumerable.Range(1, 2).All(back => _stuckDays.DayKeys.TryGetValue(Day(back), out var keys) && keys.Contains(key))) {
                continue;
            }

            if (!_reported.TryAdd($"stuck3|{key}|{_stuckDays.Date}", DateTime.UtcNow)) {
                continue;
            }

            var quest = spot.LastQuest ?? "";
            var text = $"MOST STUCK 3 DAYS RUNNING: {UltimusText.Zone(spot.Zone)} near ({spot.X:0}, {spot.Y:0}) was one of the 3 places he lost " +
                       $"the most time on each of the last 3 days ({spot.Seconds / 60:0} min today), last on \"{UltimusText.Short(spot.LastStep, 70)}\". " +
                       "Something there keeps holding him back.";
            Count("issues raised");
            Note("Ultimus", text, UltimusLink.NOTABLE);
            UltimusLink.Issue("quest", quest, quest.Length == 0 ? "" : UltimusText.Title(quest), WorldOf(spot.Zone), text,
                "A player gets past this spot in a few minutes.", null, spot.LastStep,
                [$"Area: {spot.Zone}", $"Spot: ({spot.X:0}, {spot.Y:0}), a 500 by 500 square",
                 .. spot.Why.OrderByDescending(w => w.Value).Select(w => $"Today: {w.Key} {w.Value / 60:0.0} min")]);
        }
    }

    // What held him back most at a spot, and the fix he would suggest for it.
    private static (string Why, string Fix) StuckFix(WasteSpot s) {
        var why = s.Why.Count == 0 ? "standing still" : s.Why.OrderByDescending(w => w.Value).First().Key;
        var fix = why switch {
            "getting unstuck" => "a wall, ledge or narrow spot there: look at the walkable map at that spot",
            "a step he can't work out" => "the step itself (its data, or a creature or object missing): see the Broken quests list",
            _ => "he was waiting there (to heal, for wisps, or with nothing to do for the step): see his log at that time",
        };
        return (why, fix);
    }

    // ---------------------------------------------------------------------
    // The weekly stuck report (Niduus, 2026-11-14): the 5 spots he lost the most time at over 7 days, summed from each
    // day's worst spots, on top of his Daily diary in Ahziael; once a week it is also written to his log and to
    // weekly-stuck.txt in his folder.
    // ---------------------------------------------------------------------

    /// <summary>The 5 worst spots over the 7 days ending <paramref name="back"/> days before today, as plain lines.</summary>
    private List<string> StuckWeek(int back) {
        if (!DateTime.TryParseExact(_stuckDays.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) {
            return [];
        }

        var days = Enumerable.Range(back, 7).Select(b => date.AddDays(-b).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var summed = new Dictionary<string, (WasteSpot Spot, int Days)>(StringComparer.OrdinalIgnoreCase);
        // Oldest first, so the newest day's step and quest are the ones kept.
        foreach (var day in Enumerable.Reverse(days)) {
            if (!_stuckDays.DaySpots.TryGetValue(day, out var spots)) {
                continue;
            }

            foreach (var (key, spot) in spots) {
                var (sum, count) = summed.TryGetValue(key, out var known)
                    ? known
                    : (new WasteSpot { Zone = spot.Zone, X = spot.X, Y = spot.Y }, 0);
                sum.Seconds += spot.Seconds;
                foreach (var (why, seconds) in spot.Why) {
                    sum.Why[why] = sum.Why.GetValueOrDefault(why) + seconds;
                }

                sum.LastStep = spot.LastStep;
                sum.LastQuest = string.IsNullOrEmpty(spot.LastQuest) ? sum.LastQuest : spot.LastQuest;
                summed[key] = (sum, count + 1);
            }
        }

        // The watchdog's stops in those days come after the spots: each one is two hours lost in one go.
        var stops = _stuckDays.Stops.Select(l => l.Split('|', 3)).Where(p => p.Length == 3 && days.Contains(p[0]))
            .Select(p => $"Stopped himself on {DayName(p[0])} at {p[1]} (two hours with no step done and no XP): {p[2]}");

        return [.. summed.Values.OrderByDescending(v => v.Spot.Seconds).Take(5).Select(v => {
            var (why, fix) = StuckFix(v.Spot);
            var quest = string.IsNullOrEmpty(v.Spot.LastQuest) ? "" : $" ({UltimusText.Title(v.Spot.LastQuest)})";
            return $"{v.Spot.Seconds / 60:0} min over {(v.Days == 1 ? "1 day" : $"{v.Days} days")} in {UltimusText.Zone(v.Spot.Zone)} near " +
                   $"({v.Spot.X:0}, {v.Spot.Y:0}), mostly {why}, last on \"{UltimusText.Short(v.Spot.LastStep, 70)}\"{quest}. Likely fix: {fix}.";
        }), .. stops];
    }

    private static string DayName(string day) => DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture) : day;

    /// <summary>The watchdog stopped him: written down for the weekly stuck report and his Daily diary.</summary>
    private void StuckStop(string what) {
        _stuckDays ??= ReadStuckDays();
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_stuckDays.Date != today) {
            _stuckDays.Date = today;
            _stuckDays.Today = [];
        }

        _stuckDays.Stops.Add($"{today}|{now:HH:mm}|{what.Replace('|', '/')}");
        WriteStuckDays();
    }

    // Once a week (the first time he plays in a new week): last week's report goes in his log and weekly-stuck.txt.
    private void StuckWeekNote() {
        if (!DateTime.TryParseExact(_stuckDays.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) {
            return;
        }

        var monday = date.AddDays(-(((int) date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_stuckDays.WeekNoted == monday) {
            return;
        }

        // The very first week only starts the clock (there is no full week behind it yet).
        var first = _stuckDays.WeekNoted.Length == 0;
        _stuckDays.WeekNoted = monday;
        var lastWeek = StuckWeek(1);
        if (first || lastWeek.Count == 0) {
            return;
        }

        var text = new System.Text.StringBuilder($"ULTIMUS WEEKLY STUCK REPORT, the 7 days before {date:dddd d MMMM yyyy}\n");
        text.AppendLine("The places he lost the most time, with the fix he would suggest:");
        for (var i = 0; i < lastWeek.Count; i++) {
            text.AppendLine($"  {i + 1}. {lastWeek[i]}");
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, "weekly-stuck.txt"), text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: weekly-stuck.txt could not be written: {0}", Logger.Args(ex.Message));
        }

        var stopCount = lastWeek.Count(l => l.StartsWith("Stopped himself", StringComparison.Ordinal));
        Note("Ultimus", "WEEKLY STUCK REPORT: the places he lost the most time last week: " +
                        string.Join(" ", lastWeek.Take(3).Select((l, i) => $"{i + 1}. {l}")) +
                        (stopCount > 0 ? $" The watchdog stopped him {stopCount} time(s)." : "") + " (all of it in weekly-stuck.txt and his Daily diary).",
            UltimusLink.NOTABLE);
    }

    private static StuckDays ReadStuckDays() {
        try {
            var path = Path.Combine(UltimusWire.Folder, STUCK_DAYS_FILE);
            if (File.Exists(path) && UltimusWire.Read<StuckDays>(File.ReadAllText(path)) is { } read) {
                return read;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(STUCK_DAYS_FILE, ex.Message));
        }

        return new StuckDays();
    }

    private void WriteStuckDays() {
        if (_stuckDays is null || _stuckDays.Date.Length == 0) {
            return;
        }

        var topSpots = _stuckDays.Today.Where(s => s.Value.Seconds >= 60).OrderByDescending(s => s.Value.Seconds).Take(3).ToList();
        _stuckDays.DayKeys[_stuckDays.Date] = [.. topSpots.Select(s => s.Key)];
        StuckThreeDays(topSpots);
        var top = topSpots.Select(t => t.Value).Select(s => {
            var (why, fix) = StuckFix(s);
            return $"{s.Seconds / 60:0} min in {UltimusText.Zone(s.Zone)} near ({s.X:0}, {s.Y:0}), mostly {why}, on \"{UltimusText.Short(s.LastStep, 70)}\". Likely fix: {fix}.";
        }).ToList();
        _stuckDays.Days[_stuckDays.Date] = top;
        _stuckDays.DaySpots[_stuckDays.Date] = _stuckDays.Today.Where(s => s.Value.Seconds >= 60).OrderByDescending(s => s.Value.Seconds).Take(10)
            .ToDictionary(s => s.Key, s => s.Value);
        _stuckDays.Week = StuckWeek(0);
        StuckWeekNote();
        foreach (var old in _stuckDays.Days.Keys.Concat(_stuckDays.DaySpots.Keys).Distinct().OrderByDescending(d => d, StringComparer.Ordinal).Skip(14).ToList()) {
            _stuckDays.Days.Remove(old);
            _stuckDays.DayKeys.Remove(old);
            _stuckDays.DaySpots.Remove(old);
        }

        _stuckDays.BackAndForth ??= [];
        foreach (var old in _stuckDays.BackAndForth.Keys.OrderByDescending(d => d, StringComparer.Ordinal).Skip(14).ToList()) {
            _stuckDays.BackAndForth.Remove(old);
        }

        var keepFrom = DateTime.Now.AddDays(-14).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _stuckDays.Stops.RemoveAll(l => string.CompareOrdinal(l, keepFrom) < 0);

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, STUCK_DAYS_FILE), UltimusWire.Write(_stuckDays));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(STUCK_DAYS_FILE, ex.Message));
        }
    }

    private static Dictionary<string, WasteSpot> ReadWaste() {
        try {
            var path = Path.Combine(UltimusWire.Folder, WASTE_DATA);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, WasteSpot>>(File.ReadAllText(path)) is { } read) {
                return new Dictionary<string, WasteSpot>(read, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(WASTE_DATA, ex.Message));
        }

        return new Dictionary<string, WasteSpot>(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteWaste() {
        if (_waste is null) {
            return;
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, WASTE_DATA), UltimusWire.Write(_waste));
            var text = new StringBuilder();
            text.AppendLine($"WHERE ULTIMUS LOSES THE MOST TIME (written {DateTime.Now:yyyy-MM-dd HH:mm})");
            text.AppendLine("Time spent standing still, getting unstuck or on a step he can't work out, by area and spot (500 x 500), most first.");
            text.AppendLine();
            text.AppendLine("BY AREA");
            foreach (var area in _waste.Values.GroupBy(w => w.Zone, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Zone: g.Key, Minutes: g.Sum(w => w.Seconds) / 60)).OrderByDescending(a => a.Minutes).Take(15)) {
                text.AppendLine($"  {area.Minutes,7:0.0} min  {UltimusText.Zone(area.Zone)} ({area.Zone})");
            }

            text.AppendLine();
            text.AppendLine("BY SPOT");
            foreach (var spot in _waste.Values.OrderByDescending(w => w.Seconds).Take(30)) {
                var whys = string.Join(", ", spot.Why.OrderByDescending(w => w.Value).Select(w => $"{w.Key} {w.Value / 60:0.0} min"));
                text.AppendLine($"  {spot.Seconds / 60,7:0.0} min  {UltimusText.Zone(spot.Zone)} near ({spot.X:0}, {spot.Y:0}): {whys}; last step: {spot.LastStep}");
            }

            File.WriteAllText(Path.Combine(UltimusWire.Folder, WASTE_FILE), text.ToString());
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(WASTE_FILE, ex.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Nightly auto-coach (off by default)
    // ---------------------------------------------------------------------

    private static bool AutoCoachOn => UltimusSeat.Current.Setting("AutoCoach", false);
    private DateTime _coachCheck = DateTime.MinValue;
    private const int COACH_HOUR = 4;
    private const int COACH_KEEP = 14;

    // Every Ultimus log in <paramref name="folder"/>, each on its own plus one joint file and a README (the nightly coach and
    // the needs-help report). The files written.
    private List<string> WriteCoachBundle(string folder, string readme) {
        WriteWaste();
        var files = new List<string>();
        files.AddRange(new DirectoryInfo(UltimusWire.Folder).EnumerateFiles()
            .Where(f => f.Extension is ".txt" or ".json" or ".jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName));
        var bots = Path.GetFullPath(BotMistakes.FOLDER);
        if (Directory.Exists(bots)) {
            files.AddRange(new DirectoryInfo(bots).EnumerateFiles().Where(f => f.Extension is ".txt" or ".json").Select(f => f.FullName));
        }

        var joint = new StringBuilder();
        joint.AppendLine($"All of Ultimus's logs, saved {DateTime.Now:yyyy-MM-dd HH:mm}. Each line starts with its file.");
        foreach (var path in files) {
            var name = (Path.GetDirectoryName(path) ?? "").EndsWith("bots", StringComparison.OrdinalIgnoreCase) ? "bots-" + Path.GetFileName(path) : Path.GetFileName(path);
            string body;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) {
                body = reader.ReadToEnd();
            }

            File.WriteAllText(Path.Combine(folder, name), body);
            joint.AppendLine().AppendLine($"===== [{name}] =====");
            foreach (var line in body.Split('\n')) {
                joint.Append("    [").Append(name).Append("] ").AppendLine(line.TrimEnd('\r'));
            }
        }

        File.WriteAllText(Path.Combine(folder, "ALL LOGS (joint file).txt"), joint.ToString());
        File.WriteAllText(Path.Combine(folder, "README.txt"), readme + "\n");

        return files;
    }

    private void MaybeAutoCoach() {
        if (DateTime.UtcNow - _coachCheck < TimeSpan.FromMinutes(5)) {
            return;
        }

        _coachCheck = DateTime.UtcNow;
        if (!AutoCoachOn || DateTime.Now.Hour < COACH_HOUR) {
            return;
        }

        try {
            var root = Path.Combine(UltimusWire.Folder, "coach");
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(folder)) {
                return;
            }

            Directory.CreateDirectory(folder);
            var files = WriteCoachBundle(folder, "Ultimus's logs, saved each night by the auto-coach ([Ultimus] AutoCoach). Attach this folder (or the joint file) in the chat.");
            foreach (var old in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.Name).Skip(COACH_KEEP)) {
                old.Delete(true);
            }

            Note("Ultimus", $"auto-coach: saved {files.Count} logs for the coach in {folder}.");
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the nightly coach folder could not be written: {0}", Logger.Args(ex.Message));
        }
    }

}
