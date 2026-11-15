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
using System.Linq;
using System.Collections.Generic;
using Akka.Actor;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Never stuck in one place (Niduus, 2026-11-13: "Parameters should be in place to prevent that"), as the Quest Team: when
/// Ultimus should be getting somewhere and has stood still over a minute it steps aside and finds its way again, the next
/// time it goes further off with a fresh try at every click, and after that it is written up pink (ABNORMAL).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    /// <summary>How long a wizard that is stepping aside is left to it before orders come again.</summary>
    private static readonly TimeSpan UNSTICK_FOR = TimeSpan.FromSeconds(6);

    // Ultimus acts on standing still a quarter sooner than the Quest Team (Niduus, 2026-11-13: "Ultimus needs to engage
    // with its restart timer 25% sooner if idle for too long"), and on standing still at all after 30 seconds, not 75
    // ("I need him more responsive and more active to correct mistakes").
    internal const double SOONER = 0.75;
    private static readonly TimeSpan STILL_FOR = TimeSpan.FromSeconds(30);
    private readonly BotStuck _stuck = new(STILL_FOR);
    private readonly Dictionary<int, DateTime> _unstickUntil = [];

    // Stepping aside: the other orders wait a few seconds.
    private bool Unsticking(int index) => _unstickUntil.TryGetValue(index, out var until) && DateTime.UtcNow < until;

    private void LookForStuck() {
        var resting = _healWaiting;
        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            RouteMemory.Track("Ultimus", member.CharId, member.Zone, member.X, member.Y);
            // Where he is headed, so a walk that ends by going out through an exit is remembered too (RouteMemory).
            if (_target is { Problem.Length: 0 } headed && member.Zone == _team[0].Zone) {
                RouteMemory.Heading(member.CharId, member.Zone, headed.X, headed.Y,
                    headed.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR);
            }

            NoteStartLanding(i, member);
            NoteFreed(i, member);
            if (i == 0) {
                CheckReasonedTry(member);
            }

            // Standing still while it works a walk-through exit is the exit work, not being stuck (it flagged itself stuck
            // over and over at The Oasis's exit, v0.10.851).
            if (_stuck.Look(i, member.Zone, member.X, member.Y, resting || (i == 0 && (WorkingExit || _grabWaiting))) is var tries and > 0) {
                Unstick(i);
            }
        }
    }

    /// <summary>
    /// Stuck: written down as a flag, and the next fix for that spot made (UltimusFixes). <paramref name="why"/>: what was
    /// seen ("has stood still over 30 seconds", "made no progress for 6 minutes").
    /// </summary>
    private void Unstick(int index, string why = null) {
        var member = _team[index];
        var where = $"{UltimusText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0}, height {member.Z:0})";
        why ??= $"has stood still over {(int) _stuck.Still.TotalSeconds} seconds";
        NoteDayStuck();
        var quest = _target?.Quest ?? _currentQuest;
        var goal = _target ?? _leaderLastTarget;
        var step = goal is not null ? $"{goal.Quest}|{goal.Goal}|{goal.What}" : _doing;

        // Written down (RouteMemory): the way finder bends round this spot from now on, for every bot, and a proven walk
        // through it no longer counts (Niduus, 2026-11-13: "adapt to new paths to get to his quest goal").
        RouteMemory.Stuck(member.CharId, member.Zone, member.X, member.Y);

        // Stuck on a side trip (a shop, a trainer, selling, the Bazaar): it is not worth a single fix round. He drops it at
        // once, remembers that place as out of reach for a while, and goes back to the story (his log, 2026-11-15: stuck
        // 5 rounds at the door of Jack Russell Jewelers for a ring, filed as a BROKEN STEP of his story quest).
        if (index == 0 && DropStuckSideTrip(member, goal, where)) {
            return;
        }

        // Ten alarms at one spot on one step: the same moves again are no use (Niduus, 2026-11-14, approved: at the Frozen
        // Forge he tried the same four fixes 75 times). He changes his approach: the creatures around him are fought, or,
        // with none, the step is set aside for a while.
        if (index == 0 && TenAlarms(member, goal, step, quest, where)) {
            return;
        }

        // A fresh look at the way: the old walk and the old order are forgotten.
        _sent.Remove(index);
        OverlayQuestPath.Forget(member.CharId);
        _target = null;
        _clicks.Clear();

        // Every flag is written down with what was done about it, and the same or a nearby flag for the same step never
        // gets the same fix twice (Niduus, 2026-11-13: "It should never allow itself to make the same or adjacent abnormal
        // flags more than once. I want Ultimus to strictly improve"): what worked there before first, else a fix not
        // tried there yet (UltimusFixes). Boxed in (a small patch cut off by a fence or a ledge with no teleporter on it):
        // stepping aside is no use. Stuck where the area starts: going back there is no use.
        var skip = new List<string>();
        var boxedIn = BoxedIn(member);
        if (boxedIn) {
            skip.Add(UltimusFixes.ASIDE);
        }

        // Walking but no closer (a progress check, no progress, a time budget): a small step aside freed him 7 times in 26
        // tries in his logs of 2026-11-13/14 (1 in 13 after two progress checks), the Commons button 8 in 11, so it is
        // skipped and the longer moves come first (Niduus, 2026-11-15: "speed up his ability to fix problems ... according
        // to log data").
        var notCloser = BotLessons.Of(false, "", "", why).Seen == BotLessons.NOT_CLOSER;
        if (notCloser) {
            skip.Add(UltimusFixes.ASIDE);
        }

        if (_startLanding.TryGetValue(member.Zone ?? "", out var start) && UltimusPlanner.Far(start.X, start.Y, member.X, member.Y) <= 400) {
            skip.Add(UltimusFixes.START);
        }

        // Inside a dungeon the Commons button throws the dungeon away (Niduus's log, 2026-11-14: it took him out of Odji's
        // Chamber while his step was in it), so it is never one of his fixes there.
        var inDungeon = InDungeon(member);
        if (inDungeon) {
            skip.Add(UltimusFixes.START);
        }

        // The line of sight circle's hop: only by a goal in sight with a clear spot next to it.
        // Not to a goal on another floor: in sight from above or below is not a way there.
        var otherFloor = index == 0 && goal is not null && OtherFloor(member, goal);
        if (otherFloor) {
            Think($"I am stuck and my step is on another floor ({FloorWords(member, goal)}). A hop or a step aside on this floor " +
                  "can't get me there: I look for the stairs or ramp between the floors.", "stuck-floor|" + goal.Key);
        }

        var hop = goal is not null && !otherFloor && UltimusSight.InSight(member.X, member.Y, goal.X, goal.Y)
            ? UltimusSight.HopBy(member.Zone, goal.X, goal.Y, member.Z) : null;
        if (hop is null) {
            skip.Add(UltimusFixes.HOP);
        }

        if (string.IsNullOrEmpty(quest)) {
            skip.Add(UltimusFixes.GO);
        }

        // The creature (often a boss) for his step never showed up in this area: walking about can't make it come. Leaving
        // and coming back in can (the room is entered again, as a player does when a boss doesn't show), so going back to
        // where the area starts comes first, and the spot is reported (Niduus, 2026-11-14: Krokenkahmen never spawned
        // in the Throne Room of Fire and he waited at his spot for 15 minutes). Nothing is skipped: he still has to win the fight.
        var absent = (_doing ?? "").Contains("no creature for this step is in", StringComparison.OrdinalIgnoreCase);
        if (absent) {
            skip.AddRange([UltimusFixes.ASIDE, UltimusFixes.OTHER_WAY, UltimusFixes.HOP, UltimusFixes.AWAY]);
            if (inDungeon) {
                // No Commons button in a dungeon: walking well away and back is the nearest to going out and in.
                skip.Remove(UltimusFixes.AWAY);
            }
            else {
                skip.Remove(UltimusFixes.START);
            }

            Issue("quest", quest, $"The creature for \"{goal?.What ?? _doing}\" never showed up in {UltimusText.Zone(member.Zone)}: "
                                  + "he leaves the area and comes back in, as a player would, and keeps checking. Nothing is skipped.",
                key: $"absent|{member.Zone}|{step}", level: UltimusLink.ABNORMAL);
        }

        // The kind of situation, for the lessons from places like it in every world (BotLessons).
        var shape = absent ? "creature not there" : boxedIn ? "boxed in" : otherFloor ? "goal on another floor"
            : hop is not null ? "goal in sight" : "open ground";
        var situation = BotLessons.Of(inDungeon, goal?.Kind, shape, why);
        _lastSituation = situation;

        // Stuck on this step here before: what worked here first, else his local AI thinks it through while the usual fix runs
        // (UltimusDirector.Reason.cs, Niduus, 2026-11-14). Only unstuck moves; nothing is skipped.
        if (index == 0 && ReasonAboutStuck(member, goal, step, quest, where, why, skip)) {
            return;
        }

        var (fix, flag) = UltimusFixes.Next(member.Zone, member.X, member.Y, step, skip, situation);
        if (flag.Lesson.Length > 0) {
            Think(flag.Lesson, "lesson|" + situation.Key + "|" + fix);
        }

        _openFlag[index] = (flag, member.Zone ?? "", member.X, member.Y);
        _freeSince.Remove(index);
        var seen = flag.Times > 1 ? $" (flagged here {flag.Times} times; tried before: {string.Join(", ", flag.Tried.SkipLast(1).Distinct())})" : "";
        Note("Ultimus", $"{member.Name} {why} in {where} while {_doing}{seen}: {fix}.", UltimusLink.ABNORMAL);
        ThinkAboutStuck(member, why, fix, flag, step);
        if (flag.Times > 1) {
            Issue("team", quest, $"{member.Name} is stuck again in {where} while {_doing}: flagged here {flag.Times} times; now tries: {fix}.",
                key: $"unstick|{member.Zone}|{(int) (flag.X / 600)}|{(int) (flag.Y / 600)}|{flag.Times}", broken: false, level: UltimusLink.ABNORMAL);
        }

        switch (fix) {
            case UltimusFixes.OTHER_WAY:
                if (TryAnotherWay(index)) {
                    _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                    OverlayQuestPath.Forget(member.CharId);

                    return;
                }

                // No teleporter: the stuck spot is now walked round (RouteMemory), so the way is found fresh.
                Aside(index, member, 600f, 1600f, "takes a new route round the spot where it got stuck");
                return;
            case UltimusFixes.AWAY:
                Aside(index, member, 900f, 2500f, "walks well away and comes back another way");
                return;
            case UltimusFixes.START:
                Recall(index, member);
                return;
            case UltimusFixes.HOP:
            case UltimusFixes.GO:
                // Fixes from before Niduus's rule (2026-11-14: Ahziael never moves him), still named in old notes: he walks well
                // away and comes back another way instead.
                Aside(index, member, 900f, 2500f, "walks well away and comes back another way (he finds his own way)");
                return;
            case UltimusFixes.FLAG when flag.Tried.Count(t => t == UltimusFixes.FLAG) >= 2:
                // Every fix tried here twice over: set aside, other story work first, and a "needs help" report (UltimusDirector.Safety.cs).
                ParkStep(member, quest, goal?.Key, goal?.What ?? _doing ?? "his step", where);
                Aside(index, member, 150f, 600f, "steps aside while the step waits");
                return;
            case UltimusFixes.FLAG:
                // He quests as a human does (Niduus, 2026-11-14): no step is finished for him. Every move fix was tried here,
                // so the spot goes to Niduus as a broken one, and he starts the round of fixes again.
                Issue("quest", quest, $"{member.Name} tried every way to get free at {where} while {_doing} and is still stuck there. " +
                                      "He waits for a fix and keeps trying; nothing is skipped.",
                    key: $"unstick-flag|{member.Zone}|{(int) (flag.X / 600)}|{(int) (flag.Y / 600)}", level: UltimusLink.CRITICAL);
                Aside(index, member, 150f, 600f, "steps aside and starts the round of fixes again");
                return;
            default:
                Aside(index, member, 150f, 600f, "steps aside to get unstuck");
                return;
        }
    }

    // The situation of his last stuck alarm (BotLessons), which the stuck thinking's moves are learned under too.
    private BotLessons.Situation _lastSituation;

    // The flag each wizard is working off now, and where it was; free once it is far from there or in another area.
    private readonly Dictionary<int, (UltimusFixes.Flag Flag, string Zone, float X, float Y)> _openFlag = [];
    private readonly Dictionary<int, DateTime> _freeSince = [];

    private void NoteFreed(int index, UltimusMember.Status member) {
        if (!_openFlag.TryGetValue(index, out var open)) {
            return;
        }

        // While zoning (no area yet, or not in the world) nothing is known about the new spot.
        if (string.IsNullOrEmpty(member.Zone) || member.State != "playing") {
            return;
        }

        var otherZone = !string.Equals(open.Zone, member.Zone, StringComparison.OrdinalIgnoreCase);
        if (!otherZone && UltimusPlanner.Far(open.X, open.Y, member.X, member.Y) < 800) {
            _freeSince.Remove(index);
            return;
        }

        // Free only once it stays free: a go-back to the area's start moved it far, and it was stuck again at once, yet
        // that counted as "got free" (v0.10.851 log). Now it has to keep going for 45 seconds without getting stuck.
        if (!_freeSince.TryGetValue(index, out var since)) {
            _freeSince[index] = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - since < TimeSpan.FromSeconds(45)) {
            return;
        }

        _freeSince.Remove(index);
        _openFlag.Remove(index);
        UltimusFixes.Worked(open.Flag);
        ThinkFreed(member, open.Flag);
        var (flags, worked) = UltimusFixes.Count();
        Note("Ultimus", $"{member.Name} got free with \"{open.Flag.Worked}\" and remembers it for that spot ({flags} stuck spots known, {worked} with a fix that works).");
    }

    private void Aside(int index, UltimusMember.Status member, float near, float far, string why) {
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far)
                   ?? (member.X + Random.Shared.Next((int) -far, (int) far), member.Y + Random.Shared.Next((int) -far, (int) far));
        var aside = WalkOrStraight(member, spot.X, spot.Y);
        _actors[index].Tell(new UltimusMember.WalkTo(aside, why));
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(member.CharId);
    }

    // Where going back to an area's start put each wizard (by area), and a go-back that has not landed yet.
    private readonly Dictionary<string, (float X, float Y)> _startLanding = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (string Zone, DateTime At)> _recalling = [];

    private void Recall(int index, UltimusMember.Status member) {
        if (InDungeon(member)) {
            Aside(index, member, 900f, 2500f, "walks well away inside the dungeon (no Commons button in a dungeon)");

            return;
        }

        _actors[index].Tell(new UltimusMember.Recall());
        _recalling[index] = (member.Zone ?? "", DateTime.UtcNow);
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
    }

    // A few seconds after going back to the start: where it landed is the area's start.
    private void NoteStartLanding(int index, UltimusMember.Status member) {
        if (_recalling.TryGetValue(index, out var going) && DateTime.UtcNow - going.At > TimeSpan.FromSeconds(4)) {
            _recalling.Remove(index);
            if (string.Equals(going.Zone, member.Zone, StringComparison.OrdinalIgnoreCase)) {
                _startLanding[member.Zone] = (member.X, member.Y);
            }
        }
    }

    /// <summary>
    /// The way to a spot to get unstuck; when the map gives no way from here at all (a walk that goes nowhere), a straight
    /// line, as a player who can't see the way just walks off in one direction (Niduus's log, 2026-11-13: at The Oasis's
    /// start every step aside went nowhere, so it never moved at all).
    /// </summary>
    private List<(float X, float Y)> WalkOrStraight(UltimusMember.Status member, float toX, float toY) {
        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, toX, toY, body: true);
        if (points.Count > 0 && UltimusPlanner.Far(member.X, member.Y, points[^1].X, points[^1].Y) >= 60) {
            return points;
        }

        Note("Ultimus", $"{member.Name}: the map gives no way from ({member.X:0}, {member.Y:0}) in {UltimusText.Zone(member.Zone)}, so it walks straight toward ({toX:0}, {toY:0}).",
            UltimusLink.ABNORMAL);
        BotMistakes.Record("Ultimus", BotMistakes.NO_WAY, member.Zone, member.X, member.Y, _doing,
            $"The map gave no way from ({member.X:0}, {member.Y:0}) to ({toX:0}, {toY:0}).",
            "The way finder found nothing, so I walked straight instead of standing still.", "The area's walk data needs a look at this spot.");

        return [((member.X + toX) / 2, (member.Y + toY) / 2), (toX, toY)];
    }

    // A patch under a twentieth of the area's main ground with no teleporter on it.
    private const double BOXED_IN_SHARE = 0.05;

    /// <summary>In a dungeon (an instance): the Commons button would throw it away.</summary>
    private static bool InDungeon(UltimusMember.Status member)
        => member.Wizard?.ZoneIsInstance == true || OverlayQuestRoute.IsDungeon(member.Zone);

    private static bool BoxedIn(UltimusMember.Status member) {
        var share = OverlayQuestPath.IslandShare(member.Zone, member.X, member.Y);

        return share is >= 0 and < BOXED_IN_SHARE && UltimusPads.Nearest(member.Zone, member.X, member.Y, [], 100000f) is null;
    }

}
