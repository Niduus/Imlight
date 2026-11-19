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
using Imlight.Common;

namespace Imlight.CoreLib.Game.Zone.Supervisors;

/// <summary>
/// Keeps a zone's script from feeding on itself.
/// </summary>
/// <remarks>
/// Katzenstein's Lab, 2026-11-19: the plaque puzzle (Sun, Bird, Tree, Bug) has four reset triggers, one per plaque, that
/// fire on "&lt;plaque&gt;.Off.EnterState" and set the other three plaques to Off. The zone raised EnterState even for a
/// plaque that was already Off, so each reset set off three more, and those nine more, without end: the server went from
/// 2.6 GB to 19 GB in two minutes, every player in the lab got a flood of state changes (1 FPS, then cut off), and the
/// whole server stayed slow until it was restarted. An object told to go into the state it is already in now stays put
/// and raises nothing, as in the live game; a second guard drops any one event that comes round far too often.
/// </remarks>
internal sealed partial class ZoneTriggerSupervisor {

    // An object set to the state it already has within this time is left alone (no state message, no EnterState).
    private static readonly TimeSpan SAME_STATE_QUIET = TimeSpan.FromSeconds(2);

    // Above this many posts of one event within a second, the rest of that second is dropped.
    private const int EVENT_POSTS_PER_SECOND = 100;

    private readonly Dictionary<string, DateTime> _lastStateSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Second, int Count)> _eventRate = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loopLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// True when the object is already in this state and was put there a moment ago, so setting it again would only echo.
    /// </summary>
    private bool IsSameStateEcho(string objectName, string previousState, string stateName) {
        var key = objectName + "\n" + stateName;
        var now = DateTime.UtcNow;
        var same = string.Equals(previousState, stateName, StringComparison.OrdinalIgnoreCase);
        if (same && _lastStateSet.TryGetValue(key, out var last) && now - last < SAME_STATE_QUIET) {
            return true;
        }

        if (same && previousState is not null && !_lastStateSet.ContainsKey(key)) {
            // Already in that state since the zone loaded (a plaque that starts Off): nothing to raise either.
            _lastStateSet[key] = now;
            return true;
        }

        _lastStateSet[key] = now;
        return false;
    }

    /// <summary>
    /// True when this event came round so often this second that it can only be a loop; the post is dropped.
    /// </summary>
    private bool IsRunawayEvent(string eventName) {
        if (string.IsNullOrEmpty(eventName)) {
            return false;
        }

        var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        var count = _eventRate.TryGetValue(eventName, out var seen) && seen.Second == second ? seen.Count + 1 : 1;
        _eventRate[eventName] = (second, count);
        if (_eventRate.Count > 4096) {
            _eventRate.Clear();
        }

        if (count <= EVENT_POSTS_PER_SECOND) {
            return false;
        }

        if (_loopLogged.Add(eventName)) {
            Logger.Warning("Zone {0}: event {1} came round more than {2} times in a second, so it is a loop; the extra posts are dropped.",
                Logger.Args(Zone.ZonePath, eventName, EVENT_POSTS_PER_SECOND));
        }

        return true;
    }
}
