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
            NoteStartLanding(i, member);
            NoteFreed(i, member);
            // Standing still while it works a walk-through exit is the exit work, not being stuck (it flagged itself stuck
            // over and over at The Oasis's exit, v0.10.851).
            if (_stuck.Look(i, member.Zone, member.X, member.Y, resting || (i == 0 && WorkingExit)) is var tries and > 0) {
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
        var where = $"{UltimusText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0})";
        why ??= $"has stood still over {(int) _stuck.Still.TotalSeconds} seconds";
        NoteDayStuck();
        var quest = _target?.Quest ?? _currentQuest;
        var goal = _target ?? _leaderLastTarget;
        var step = goal is not null ? $"{goal.Quest}|{goal.Goal}|{goal.What}" : _doing;

        // Written down (RouteMemory): the way finder bends round this spot from now on, for every bot, and a proven walk
        // through it no longer counts (Niduus, 2026-11-13: "adapt to new paths to get to his quest goal").
        RouteMemory.Stuck(member.CharId, member.Zone, member.X, member.Y);

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
        if (BoxedIn(member)) {
            skip.Add(UltimusFixes.ASIDE);
        }

        if (_startLanding.TryGetValue(member.Zone ?? "", out var start) && UltimusPlanner.Far(start.X, start.Y, member.X, member.Y) <= 400) {
            skip.Add(UltimusFixes.START);
        }

        // The line of sight circle's hop: only by a goal in sight with a clear spot next to it.
        var hop = goal is not null && UltimusSight.InSight(member.X, member.Y, goal.X, goal.Y)
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
            skip.Remove(UltimusFixes.START);
            Issue("quest", quest, $"The creature for \"{goal?.What ?? _doing}\" never showed up in {UltimusText.Zone(member.Zone)}: "
                                  + "he leaves the area and comes back in, as a player would, and keeps checking. Nothing is skipped.",
                key: $"absent|{member.Zone}|{step}", level: UltimusLink.ABNORMAL);
        }

        var (fix, flag) = UltimusFixes.Next(member.Zone, member.X, member.Y, step, skip);
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
            case UltimusFixes.HOP when hop is { } spot:
                Think($"\"{goal!.What}\" is right there, {UltimusPlanner.Far(member.X, member.Y, goal.X, goal.Y):0} away in my circle of sight, but I can't walk to it. " +
                      "I hop to a clear spot next to it; the route there needs fixing, so I write it down.");
                BotMistakes.Record("Ultimus", BotMistakes.NO_WAY, member.Zone, member.X, member.Y, step,
                    $"Could not walk to \"{goal.What}\" {UltimusPlanner.Far(member.X, member.Y, goal.X, goal.Y):0} away; hopped to ({spot.X:0}, {spot.Y:0}).",
                    _lastReasoning, "The walk data between these two spots needs fixing.", fix);
                _actors[index].Tell(new UltimusMember.Hop(spot.X, spot.Y, spot.Z, "next to " + goal.What));
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                return;
            case UltimusFixes.GO:
                // As a player types .stuck go: the server sends it to the area its step is in (nothing happens when the step
                // is in this area, and the next flag here goes on to the next fix).
                _actors[index].Tell(new UltimusMember.GoToStep());
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
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

    private static bool BoxedIn(UltimusMember.Status member) {
        var share = OverlayQuestPath.IslandShare(member.Zone, member.X, member.Y);

        return share is >= 0 and < BOXED_IN_SHARE && UltimusPads.Nearest(member.Zone, member.X, member.Y, [], 100000f) is null;
    }

}
