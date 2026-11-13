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
using System.Collections.Concurrent;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.States;

/// <summary>
/// The animation an object plays on its way from one state to another (Niduus 2026-11-13: "some world animations don't
/// work when you complete the quest for it"). A quest or a zone script names the state an object ends in: the Dark Cave
/// bubbles go to Bubble_Off_END, a gate to Idle_Open. The object's state set says how it gets there: the state it is in
/// lists a transition (target Bubble_Off_END, through Bubble_Xplod_PASS), and that in-between state plays the animation
/// and moves on by itself after its transition time. The server sent only the end state, so the object jumped there and
/// the bubble vanished without popping. Now the in-between state is sent first and the end state after it.
/// </summary>
internal static class StateTransitions {

    // [World] PlayStateAnimations: objects a quest or a zone script changes play the animation on the way (default on).
    private static bool s_enabled {
        get {
            try {
                var value = ConfigurationManager.GetSetting("World.PlayStateAnimations");
                return string.IsNullOrWhiteSpace(value) || ConfigurationManager.Settings["World.PlayStateAnimations"].AsBool();
            }
            catch {
                return true;
            }
        }
    }

    // Used when the in-between state does not say how long it plays.
    private static readonly TimeSpan DEFAULT_WAIT = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LONGEST_WAIT = TimeSpan.FromSeconds(15);

    // The category creatures and the generic toggle use for "someone is using me"; never a world animation.
    private const string INTERACTING_CATEGORY = "Interacting";

    // State set of each placed object, by "<zone path>|<zone tag>" and by the tag alone (dynamods sometimes name the
    // zone a little differently from the zone file).
    private static readonly ConcurrentDictionary<string, string> s_setByZoneTag = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_setByTag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers which state set the object placed with this tag uses.</summary>
    internal static void Note(string zonePath, string zoneTag, CoreTemplate template) {
        try {
            if (string.IsNullOrEmpty(zoneTag)) {
                return;
            }

            var setName = SetNameOf(template);
            if (string.IsNullOrEmpty(setName)) {
                return;
            }

            s_setByTag[zoneTag] = setName;
            if (!string.IsNullOrEmpty(zonePath)) {
                s_setByZoneTag[$"{zonePath}|{zoneTag}"] = setName;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Could not note the state set of {0}: {1}", Logger.Args(zoneTag, ex.Message));
        }
    }

    internal static string SetNameOf(CoreTemplate template)
        => (template as GameObjectTemplate)?.m_behaviors?.OfType<ObjectStateBehaviorTemplate>()
            .Select(b => (string) b.m_stateSetName)
            .FirstOrDefault(n => !string.IsNullOrEmpty(n));

    /// <summary>
    /// The state the object with this tag plays on its way from <paramref name="fromState"/> (null = the state it starts
    /// in) to <paramref name="toState"/>, and how long it plays. False when it goes straight there.
    /// </summary>
    internal static bool TryFind(string zonePath, string zoneTag, string fromState, string toState,
                                 out string passState, out TimeSpan wait) {
        passState = null;
        wait = TimeSpan.Zero;
        if (!s_enabled || string.IsNullOrEmpty(zoneTag) || string.IsNullOrEmpty(toState)) {
            return false;
        }

        string setName = null;
        if (!string.IsNullOrEmpty(zonePath)) {
            s_setByZoneTag.TryGetValue($"{zonePath}|{zoneTag}", out setName);
        }

        if (setName is null && !s_setByTag.TryGetValue(zoneTag, out setName)) {
            return false;
        }

        return TryFindInSet(setName, fromState, toState, out passState, out wait);
    }

    /// <summary>The same, for a state set by name.</summary>
    internal static bool TryFindInSet(string setName, string fromState, string toState, out string passState, out TimeSpan wait) {
        passState = null;
        wait = TimeSpan.Zero;
        if (string.IsNullOrEmpty(setName) || string.IsNullOrEmpty(toState)) {
            return false;
        }

        try {
            var set = StateFactory.GetStateSet(setName);
            foreach (var category in set?.m_categories ?? []) {
                if (category?.m_states is not { Count: > 0 } states
                    || string.Equals(category.m_categoryName, INTERACTING_CATEGORY, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var target = states.FirstOrDefault(s => s is not null && Same(s.m_stateName, toState));
                if (target is null) {
                    continue;
                }

                // The state the object is in now; an unknown one is the state it starts in.
                var from = string.IsNullOrEmpty(fromState) ? null : states.FirstOrDefault(s => s is not null && Same(s.m_stateName, fromState));
                if (from is null) {
                    string start = category.m_startState;
                    from = states.FirstOrDefault(s => s is not null && Same(s.m_stateName, start));
                }

                if (from is null || Same(from.m_stateName, toState)) {
                    return false;
                }

                var way = from.m_transitions?.FirstOrDefault(t => t is not null && Same(t.m_targetState, toState));
                string through = way?.m_transitionState;
                if (string.IsNullOrEmpty(through) || Same(through, toState) || Same(through, from.m_stateName)) {
                    return false;
                }

                var pass = states.FirstOrDefault(s => s is not null && Same(s.m_stateName, through));
                if (pass is null) {
                    return false;
                }

                passState = pass.m_stateName;
                wait = pass.m_autoTransition && pass.m_transitionTime > 0
                    ? TimeSpan.FromSeconds(Math.Min(pass.m_transitionTime, LONGEST_WAIT.TotalSeconds))
                    : DEFAULT_WAIT;

                return true;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Could not read the transitions of state set {0}: {1}", Logger.Args(setName, ex.Message));
        }

        return false;
    }

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

}
