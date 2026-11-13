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
            if (_stuck.Look(i, member.Zone, member.X, member.Y, resting) is var tries and > 0) {
                Unstick(i, tries);
            }
        }
    }

    private void Unstick(int index, int tries) {
        var member = _team[index];
        var where = $"{UltimusText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0})";
        var seconds = (int) _stuck.Still.TotalSeconds;
        var quest = _target?.Quest ?? _currentQuest;
        var step = (_target ?? _leaderLastTarget) is { } goal ? $"{goal.Quest}|{goal.Goal}|{goal.What}" : _doing;

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

        if (string.IsNullOrEmpty(quest)) {
            skip.Add(UltimusFixes.SKIP);
        }

        var (fix, flag) = UltimusFixes.Next(member.Zone, member.X, member.Y, step, skip);
        _openFlag[index] = (flag, member.Zone ?? "", member.X, member.Y);
        var seen = flag.Times > 1 ? $" (flagged here {flag.Times} times; tried before: {string.Join(", ", flag.Tried.SkipLast(1).Distinct())})" : "";
        Note("Ultimus", $"{member.Name} has stood still over {seconds} seconds in {where} while {_doing}{seen}: {fix}.", UltimusLink.ABNORMAL);
        if (flag.Times > 1) {
            Issue("team", quest, $"{member.Name} is stuck again in {where} while {_doing}: flagged here {flag.Times} times; now tries: {fix}.",
                key: $"unstick|{member.Zone}|{(int) (flag.X / 600)}|{(int) (flag.Y / 600)}|{flag.Times}", skipStep: false, level: UltimusLink.ABNORMAL);
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
            case UltimusFixes.SKIP:
                Issue("quest", quest, $"{member.Name} tried every fix at {where} while {_doing} and is still stuck there: Ahziael finishes the step.",
                    key: $"unstick-skip|{member.Zone}|{(int) (flag.X / 600)}|{(int) (flag.Y / 600)}", skipStep: false, level: UltimusLink.ABNORMAL);
                if (!string.IsNullOrEmpty(quest)) {
                    _actors[index].Tell(new UltimusMember.SkipStep(quest));
                }

                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                return;
            default:
                Aside(index, member, 150f, 600f, "steps aside to get unstuck");
                return;
        }
    }

    // The flag each wizard is working off now, and where it was; free once it is far from there or in another area.
    private readonly Dictionary<int, (UltimusFixes.Flag Flag, string Zone, float X, float Y)> _openFlag = [];

    private void NoteFreed(int index, UltimusMember.Status member) {
        if (!_openFlag.TryGetValue(index, out var open)) {
            return;
        }

        var otherZone = !string.Equals(open.Zone, member.Zone ?? "", StringComparison.OrdinalIgnoreCase);
        if (!otherZone && UltimusPlanner.Far(open.X, open.Y, member.X, member.Y) < 800) {
            return;
        }

        _openFlag.Remove(index);
        UltimusFixes.Worked(open.Flag);
        var (flags, worked) = UltimusFixes.Count();
        Note("Ultimus", $"{member.Name} got free with \"{open.Flag.Worked}\" and remembers it for that spot ({flags} stuck spots known, {worked} with a fix that works).");
    }

    private void Aside(int index, UltimusMember.Status member, float near, float far, string why) {
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far)
                   ?? (member.X + Random.Shared.Next((int) -far, (int) far), member.Y + Random.Shared.Next((int) -far, (int) far));
        var aside = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.X, spot.Y, body: true);
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
    /// Stuck right where going back to the area's start puts it (Niduus's log, 2026-11-13: Ultimus went back to The Oasis's
    /// start every minute and a half for over an hour, and stood still there each time): going back again is no use, so it
    /// walks well away instead (over floor the file does not show, if that is the only way), and it is written up once.
    /// True when that was done.
    /// </summary>
    private bool StartIsHere(int index, UltimusMember.Status member, string where, string quest) {
        if (!_startLanding.TryGetValue(member.Zone ?? "", out var start) || UltimusPlanner.Far(start.X, start.Y, member.X, member.Y) > 400) {
            return false;
        }

        Issue("team", quest, $"{member.Name} is stuck at {where}, where {UltimusText.Zone(member.Zone)} starts, while {_doing}: going back to the start would put it here again.",
            key: $"start-stuck|{member.Zone}", skipStep: false, level: UltimusLink.ABNORMAL);
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, 900f, 2500f)
                   ?? (member.X + Random.Shared.Next(-900, 900), member.Y + Random.Shared.Next(-900, 900));
        Note("Ultimus", $"{member.Name} is stuck where {UltimusText.Zone(member.Zone)} starts, so going back there is no use: walks well away and finds the way from there.",
            UltimusLink.ABNORMAL);
        var away = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.X, spot.Y, body: true);
        _actors[index].Tell(new UltimusMember.WalkTo(away, "walks away from the area's start to get unstuck"));
        _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(member.CharId);

        return true;
    }

    // A patch under a twentieth of the area's main ground with no teleporter on it.
    private const double BOXED_IN_SHARE = 0.05;

    private static bool BoxedIn(UltimusMember.Status member) {
        var share = OverlayQuestPath.IslandShare(member.Zone, member.X, member.Y);

        return share is >= 0 and < BOXED_IN_SHARE && UltimusPads.Nearest(member.Zone, member.X, member.Y, [], 100000f) is null;
    }

}
