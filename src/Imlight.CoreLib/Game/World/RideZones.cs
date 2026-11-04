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
 *
 * ========================================================================
 * RIDE ZONES
 * ========================================================================
 *
 * PURPOSE:
 * A ride is a small zone the game plays a trip in: Krokotopia's flying ship between the Oasis and Krokosphinx, the
 * Dragonspyre flight path, Marleybone's airship flights. Stepping on board starts the trip (the zone's own trigger moves
 * the ship into its flying state, waits out the flight and then teleports the player off).
 *
 * The Krokotopia ships and the Dragonspyre flight allow 100 players, so they loaded as one shared zone that stayed up.
 * The ship stayed where the first trip left it: the next wizard to board stood on a ship already parked at the far
 * dock, nothing moved, and the trip's own clock still ran (Niduus, 2026-11-03: "i got stuck on the boat when getting
 * on it"). Now every boarding gets the player's own fresh copy of the ride, so the ship always starts at its first
 * dock and flies, and the copy goes away once they are off.
 *
 * [Instance] RideCopies (default true, live) turns this off again.
 *
 * Skipping a ride (Niduus, 2026-11-04: "the boat trip in krok doesn't have a skip"): the ship's own skip sign
 * (KT_TeleportToOasis) works on both ships and takes the player straight to where the trip ends, and `.skip` does the
 * same on any ride (the Dragonspyre flight has no sign).
 *
 * Nothing in the game files is changed; the rides are recognised from their own trigger script.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.World;

internal static class RideZones {

    private static readonly ConcurrentDictionary<string, bool> s_known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (string Zone, string Location)> s_trips = new(StringComparer.OrdinalIgnoreCase);

    public static bool Enabled => ConfigurationManager.GetValue("Instance.RideCopies", true);

    /// <summary>Whether a zone already loaded once turned out to be a ride.</summary>
    public static bool IsKnownRide(string zonePath)
        => Enabled && zonePath is not null && s_known.TryGetValue(zonePath, out var ride) && ride;

    /// <summary>
    /// Whether a zone's trigger script is a ride: a small script (a ride has one to three triggers, a dungeon many) where
    /// stepping in (an Enter event) has a trigger put an object into a state, wait, and then teleport the player away,
    /// itself or through the event it posts. The answer is remembered.
    /// </summary>
    public static bool IsRide(string zonePath, WizZoneTriggers triggers) {
        if (zonePath is null) {
            return false;
        }

        var ride = s_known.GetOrAdd(zonePath, _ => IsRideScript(triggers));
        if (ride && TripEnd(zonePath, triggers) is { } trip) {
            s_trips[zonePath] = trip;
        }
        if (ride && Enabled) {
            Logger.Debug("Zone {0} is a ride; each boarding gets its own fresh copy.", Logger.Args(zonePath));
        }

        return ride && Enabled;
    }

    /// <summary>Where a ride's trip ends: where its own teleport at the end of the trip takes the player.</summary>
    public static bool TryGetTripEnd(string zonePath, out string zone, out string location) {
        zone = location = null;
        if (zonePath is null || !s_known.TryGetValue(zonePath, out var ride) || !ride) {
            return false;
        }

        if (!s_trips.TryGetValue(zonePath, out var trip)) {
            if (Recorded(zonePath) is not { } recorded) {
                return false;
            }

            trip = s_trips[zonePath] = recorded;
        }

        (zone, location) = trip;

        return true;
    }

    // The game's own teleport in the ride's script, or the one SpiralDB recorded for it when the script names none.
    private static (string Zone, string Location)? TripEnd(string zonePath, WizZoneTriggers triggers) {
        var own = triggers?.m_triggers?
            .Where(t => t?.m_results?.m_results is not null)
            .SelectMany(t => t.m_results.m_results)
            .OfType<ResTeleport>()
            .FirstOrDefault(r => !string.IsNullOrEmpty(r.m_destinationZone));
        if (own is not null) {
            return (own.m_destinationZone, own.m_destinationLoc);
        }

        return Recorded(zonePath);
    }

    private static (string Zone, string Location)? Recorded(string zonePath) {
        var recorded = ZoneDataCollection.GetZoneData(zonePath)?.Teleports?
            .Select(t => t.Teleport)
            .FirstOrDefault(r => r is not null && !string.IsNullOrEmpty(r.m_destinationZone));

        return recorded is null ? null : (recorded.m_destinationZone, recorded.m_destinationLoc);
    }

    private const int MAX_RIDE_TRIGGERS = 4;

    private static bool IsRideScript(WizZoneTriggers triggers) {
        var all = triggers?.m_triggers?.Where(t => t is not null).ToList();
        if (all is null || all.Count == 0 || all.Count > MAX_RIDE_TRIGGERS) {
            return false;
        }

        // The events that set off a teleport somewhere in the zone.
        var teleportEvents = all
            .Where(t => t.m_results?.m_results?.Any(r => r is ResTeleport) == true && t.m_fireEvents is not null)
            .SelectMany(t => t.m_fireEvents.Select(e => e.ToString()))
            .ToHashSet(StringComparer.Ordinal);

        return all.Any(t => IsRideTrigger(t, teleportEvents));
    }

    private static bool IsRideTrigger(Trigger trigger, ISet<string> teleportEvents) {
        if (trigger.m_fireEvents is null || trigger.m_results?.m_results is null
            || !trigger.m_fireEvents.Any(e => e.ToString()?.StartsWith("Enter", StringComparison.Ordinal) == true)) {
            return false;
        }

        var results = trigger.m_results.m_results;
        var moves = results.FindIndex(r => r is ResModifyTriggerObject);
        if (moves < 0) {
            return false;
        }

        var waits = results.FindIndex(moves + 1, r => r is ResWait);

        return waits >= 0 && results.FindIndex(waits + 1, r => r is ResTeleport
            || r is ResPostEvent post && teleportEvents.Contains(post.m_eventName.ToString())) >= 0;
    }

}
