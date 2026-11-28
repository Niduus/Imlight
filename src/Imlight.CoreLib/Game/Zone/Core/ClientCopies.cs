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

using System.Collections.Concurrent;
using System.Collections.Generic;
using Akka.Actor;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// Which of the objects the game built from its zone file each player's game still has.
/// </summary>
/// <remarks>
/// The server's copy of such an object carries the game's own id (<see cref="ClientBuiltObjects.TryGlobalId"/>), so the
/// game must not be sent it while it holds its own: it would be a second copy under the same id. Every game holds all of
/// them once it has loaded the zone (MSG_LOGINCOMPLETE); one is gone once the game is sent MSG_REMOVEOBJECT for it, and
/// back once it is sent the object again. The session records both as they go out (SessionActor), the render component
/// asks before sending.
/// </remarks>
internal static class ClientCopies {

    private sealed class State {
        public ushort Zone;
        public readonly HashSet<ulong> Gone = [];
    }

    private static readonly ConcurrentDictionary<IActorRef, State> s_bySession = new();

    /// <summary>The game was told to load the zone with this dynamic id: it builds all its own copies again.</summary>
    internal static void ZoneLoaded(IActorRef session, uint dynamicZoneId) {
        var state = new State { Zone = (ushort) (dynamicZoneId & 0xFFFF) };
        s_bySession[session] = state;
    }

    /// <summary>Whether the game has loaded the zone <paramref name="id"/> belongs to (so a removal of it reaches it).</summary>
    internal static bool ZoneReady(IActorRef session, ulong id)
        => s_bySession.TryGetValue(session, out var state) && state.Zone == ClientBuiltObjects.ZoneOf(id);

    internal static void Removed(IActorRef session, ulong id) {
        if (ClientBuiltObjects.IsClientBuiltId(id) && s_bySession.TryGetValue(session, out var state)) {
            lock (state) {
                state.Gone.Add(id);
            }
        }
    }

    internal static void Sent(IActorRef session, ulong id) {
        if (ClientBuiltObjects.IsClientBuiltId(id) && s_bySession.TryGetValue(session, out var state)) {
            lock (state) {
                state.Gone.Remove(id);
            }
        }
    }

    /// <summary>
    /// Whether the player's game holds its own copy of the object with <paramref name="id"/>: a client-built id the game has
    /// not been told to remove, or one of a zone it is still loading (it builds that copy as it loads).
    /// </summary>
    internal static bool Holds(IActorRef session, ulong id) {
        if (session is null || !ClientBuiltObjects.IsClientBuiltId(id)) {
            return false;
        }

        if (!s_bySession.TryGetValue(session, out var state) || state.Zone != ClientBuiltObjects.ZoneOf(id)) {
            return true;
        }

        lock (state) {
            return !state.Gone.Contains(id);
        }
    }

    internal static void Forget(IActorRef session) => s_bySession.TryRemove(session, out _);

}
