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
    // with its restart timer 25% sooner if idle for too long"): about 56 seconds, not 75.
    internal const double SOONER = 0.75;
    private readonly BotStuck _stuck = new(BotStuck.STILL * SOONER);
    private readonly Dictionary<int, DateTime> _unstickUntil = [];

    // Stepping aside: the other orders wait a few seconds.
    private bool Unsticking(int index) => _unstickUntil.TryGetValue(index, out var until) && DateTime.UtcNow < until;

    private void LookForStuck() {
        var resting = _healWaiting;
        for (var i = 0; i < _team.Count; i++) {
            var member = _team[i];
            RouteMemory.Track("Ultimus", member.CharId, member.Zone, member.X, member.Y);
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

        // Written down (RouteMemory): the way finder bends round this spot from now on, for every bot, and a proven walk
        // through it no longer counts (Niduus, 2026-11-13: "adapt to new paths to get to his quest goal").
        RouteMemory.Stuck(member.CharId, member.Zone, member.X, member.Y);

        // A fresh look at the way: the old walk and the old order are forgotten.
        _sent.Remove(index);
        OverlayQuestPath.Forget(member.CharId);
        _target = null;

        // Boxed in (Niduus, 2026-11-13: "stuck", pressed on a house wall behind the low stone fence in Triton Avenue): a small
        // patch cut off by a fence or a ledge with no teleporter on it can't be walked off, so stepping aside is no use and
        // it goes back to the area's start at once rather than after three tries.
        var boxedIn = tries < 3 && BoxedIn(member);
        if (boxedIn) {
            if (StartIsHere(index, member, where, quest)) {
                return;
            }

            Note("Ultimus", $"{member.Name} is boxed in at {where} (a small patch cut off by a fence or a ledge, no teleporter on it): goes back to where {UltimusText.Zone(member.Zone)} starts.",
                UltimusLink.ABNORMAL);
            Recall(index, member);

            return;
        }

        if (tries >= 3) {
            Issue("team", quest,
                $"{member.Name} is stuck in {where} while {_doing}: it stood still over {seconds} seconds {tries} times and stepping aside did not free it.",
                key: $"unstick|{member.Zone}|{index}", skipStep: false, level: UltimusLink.ABNORMAL);
        }

        // The self-check: stepping aside twice did not free it, so the server takes it back to where the area starts (a
        // place cut off from the way on, such as the Kraken isle, is left that way), and the way is found again from there.
        if (tries >= 3) {
            if (StartIsHere(index, member, where, quest)) {
                return;
            }

            Note("Ultimus", $"{member.Name} is still stuck in {where}: goes back to where {UltimusText.Zone(member.Zone)} starts and finds the way from there.",
                UltimusLink.ABNORMAL);
            Recall(index, member);

            return;
        }

        if (tries >= 2) {
            _clicks.Clear();

            // Another way first, as a player who can't get through tries the teleporter (UltimusDirector.Routes.cs).
            if (TryAnotherWay(index)) {
                _unstickUntil[index] = DateTime.UtcNow + UNSTICK_FOR;
                OverlayQuestPath.Forget(member.CharId);

                return;
            }
        }

        var (near, far) = tries >= 2 ? (600f, 1600f) : (150f, 600f);
        var spot = BotStuck.StepAside(member.Zone, member.X, member.Y, member.Z, near, far)
                   ?? (member.X + Random.Shared.Next(-200, 200), member.Y + Random.Shared.Next(-200, 200));

        Note("Ultimus", $"{member.Name} has stood still over {seconds} seconds in {where} while {_doing}: steps aside and finds the way again.",
            UltimusLink.ABNORMAL);
        var aside = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, spot.X, spot.Y, body: true);
        _actors[index].Tell(new UltimusMember.WalkTo(aside, "steps aside to get unstuck"));
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
