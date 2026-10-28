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
 *
 * ========================================================================
 * DUNGEON RECALL
 * ========================================================================
 *
 * PURPOSE:
 * The live game's Dungeon Recall: a wizard who leaves a dungeon any way but its own exits (fleeing, losing a battle,
 * the Commons or Home button, a world gate, logging out) gets the HUD button that takes them back into the same copy
 * of the dungeon, with what was beaten still beaten. Niduus, 2026-10-28: "Fleeing from a instance does not give me
 * dungeon recall. Also if a player is in there with me and they remain, the recall does NOT count down".
 *
 * NOTE:
 * - The game's HUD (MSG_SETRESUMEINSTANCE) shows the button with a countdown to ExpireTime, a Unix time in seconds; at
 *   ExpireTime it hides the button by itself. MSG_UPDATERESUMEINSTANCETIME2 moves ExpireTime, MSG_CLEARRESUMEINSTANCE
 *   hides it. Clicking it sends MSG_RESUMEINSTANCETELEPORTPLAYER (ZoneService).
 * - While anyone is still inside the dungeon (any room of the same copy), the timer stands still at the full length
 *   ([Instance] RecallMinutes): the client is sent a fresh ExpireTime every second. It starts counting down once the
 *   copy is empty.
 * - A copy with a recall waiting is kept until the recall runs out, whatever [Instance] ResetWhenEmpty says (Zone).
 * - In memory only: a server restart forgets every recall (the copies are gone too).
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The Dungeon Recall of each wizard, and who is inside each instance container.
/// </summary>
internal static class DungeonRecall {

    private const int DEFAULT_RECALL_MINUTES = 10;

    /// <summary>A wizard's way back into a dungeon copy.</summary>
    internal sealed class Ticket {
        public ulong CharId { get; init; }
        public ulong OwnerCharId { get; init; }
        public string Zone { get; init; }
        public string Location { get; init; }
        public string DisplayName { get; init; }
        public IActorRef Session { get; set; }
        public uint ExpireTime { get; set; }
    }

    private static readonly ConcurrentDictionary<ulong, Ticket> s_tickets = new();

    // Container owner -> the characters in any of its zones.
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, byte>> s_inside = new();

    // The instance zones that exist, by container owner and zone path.
    private static readonly ConcurrentDictionary<(ulong Owner, string Zone), int> s_openZones = new(new OwnerZoneComparer());

    // The characters whose instance is a real dungeon, raid or private copy (one they went into by a sigil, a raid gate,
    // the button or [Instance] PrivateZones, and the rooms they walked on to from it). Small zones load as instances too
    // (shop and house interiors of the open world: a hard limit of 12 or less), and those give no recall.
    private static readonly ConcurrentDictionary<ulong, byte> s_inDungeon = new();

    private static readonly Lock s_timerLock = new();
    private static Timer s_timer;

    /// <summary>Whether leaving a dungeon gives the Dungeon Recall button ([Instance] DungeonRecall).</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Instance.DungeonRecall", true);

    /// <summary>How long the button lasts once nobody is left inside ([Instance] RecallMinutes).</summary>
    internal static int Minutes => Math.Max(1, ConfigurationManager.GetValue("Instance.RecallMinutes", DEFAULT_RECALL_MINUTES));

    private static uint Now => (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static uint FullExpireTime => Now + (uint) (Minutes * 60);

    /// <summary>Gives the wizard the button for the copy of <paramref name="zone"/> owned by <paramref name="ownerCharId"/>.</summary>
    /// <param name="session">The wizard's session, or null when they are logging out (the button comes at their next login).</param>
    internal static void Offer(IActorRef session, ulong charId, ulong ownerCharId, string zone, string location, string displayName) {
        if (!Enabled || charId == 0 || string.IsNullOrEmpty(zone) || !IsOpen(ownerCharId, zone)) {
            return;
        }

        var ticket = new Ticket {
            CharId = charId,
            OwnerCharId = ownerCharId,
            Zone = zone,
            Location = location ?? "",
            DisplayName = displayName ?? zone,
            Session = session,
            ExpireTime = FullExpireTime,
        };
        s_tickets[charId] = ticket;
        Logger.Information("Dungeon Recall: {0} can go back to {1} (copy of {2}) for {3} minutes once it is empty.",
            Logger.Args(charId, zone, ownerCharId, Minutes));

        Send(ticket);
        EnsureTimer();
    }

    /// <summary>The wizard's recall, if they have one.</summary>
    internal static bool TryGet(ulong charId, out Ticket ticket) {
        if (s_tickets.TryGetValue(charId, out ticket) && IsOpen(ticket.OwnerCharId, ticket.Zone)) {
            return true;
        }

        ticket = null;

        return false;
    }

    /// <summary>A wizard finished loading into a zone: the button is shown again there (the HUD starts empty).</summary>
    internal static void OnAttach(IActorRef session, ulong charId) {
        if (!s_tickets.TryGetValue(charId, out var ticket)) {
            return;
        }

        ticket.Session = session;
        if (!IsOpen(ticket.OwnerCharId, ticket.Zone) || (Now >= ticket.ExpireTime && !IsOccupied(ticket))) {
            Remove(ticket, "its dungeon closed");

            return;
        }

        Send(ticket);
    }

    /// <summary>Takes the wizard's button away.</summary>
    internal static void Clear(ulong charId, string reason) {
        if (s_tickets.TryGetValue(charId, out var ticket)) {
            Remove(ticket, reason);
        }
    }

    /// <summary>The latest time a recall keeps this owner's dungeon copy, or 0 when none does.</summary>
    internal static uint HeldUntil(ulong ownerCharId) {
        uint held = 0;
        foreach (var ticket in s_tickets.Values) {
            if (ticket.OwnerCharId == ownerCharId && ticket.ExpireTime > held) {
                held = ticket.ExpireTime;
            }
        }

        return held;
    }

    // The zones tell these.

    internal static void ZoneOpened(ulong ownerCharId, string zone) {
        if (ownerCharId != 0 && zone is not null) {
            s_openZones.AddOrUpdate((ownerCharId, zone), 1, (_, n) => n + 1);
        }
    }

    internal static void ZoneClosed(ulong ownerCharId, string zone) {
        if (ownerCharId == 0 || zone is null) {
            return;
        }

        var key = (ownerCharId, zone);
        if (s_openZones.AddOrUpdate(key, 0, (_, n) => n - 1) <= 0) {
            s_openZones.TryRemove(key, out _);
        }
    }

    /// <summary>Whether the character is in a dungeon, raid or private copy, not an open world interior.</summary>
    internal static bool IsInDungeon(ulong charId) => s_inDungeon.ContainsKey(charId);

    /// <summary>Records whether the zone the character is going to is a dungeon, raid or private copy.</summary>
    internal static void SetInDungeon(ulong charId, bool inDungeon) {
        if (inDungeon) {
            s_inDungeon[charId] = 0;
        }
        else {
            s_inDungeon.TryRemove(charId, out _);
        }
    }

    internal static void Entered(ulong ownerCharId, ulong charId, string zone) {
        // A house is in its owner's container too, but going home is not going back into the dungeon; nor is an open
        // world interior that loads as an instance.
        if (ownerCharId == 0 || charId == 0 || Housing.HouseZoneIndex.IsHouseZone(zone) || !IsInDungeon(charId)) {
            return;
        }

        s_inside.GetOrAdd(ownerCharId, _ => new ConcurrentDictionary<ulong, byte>())[charId] = 0;

        // Back inside the same copy, by the button or any other way: the button is used up.
        if (s_tickets.TryGetValue(charId, out var ticket) && ticket.OwnerCharId == ownerCharId) {
            Remove(ticket, "they are back inside");
        }
    }

    internal static void Left(ulong ownerCharId, ulong charId) {
        if (s_inside.TryGetValue(ownerCharId, out var chars)) {
            chars.TryRemove(charId, out _);
        }
    }

    private static bool IsOpen(ulong ownerCharId, string zone)
        => zone is not null && s_openZones.ContainsKey((ownerCharId, zone));

    // Anyone else in any room of the ticket's copy.
    private static bool IsOccupied(Ticket ticket)
        => s_inside.TryGetValue(ticket.OwnerCharId, out var chars) && chars.Keys.Any(c => c != ticket.CharId);

    private static void Remove(Ticket ticket, string reason) {
        if (!s_tickets.TryRemove(new System.Collections.Generic.KeyValuePair<ulong, Ticket>(ticket.CharId, ticket))) {
            return;
        }

        Logger.Information("Dungeon Recall: {0} no longer has the way back to {1} ({2}).",
            Logger.Args(ticket.CharId, ticket.Zone, reason));
        ticket.Session?.Tell(new WIZARD2_53_PROTOCOL.MSG_CLEARRESUMEINSTANCE());
    }

    private static void Send(Ticket ticket) => ticket.Session?.Tell(new WIZARD2_53_PROTOCOL.MSG_SETRESUMEINSTANCE {
        CharacterGID = ticket.CharId,
        ClusterGID = ticket.OwnerCharId,
        ZoneDisplayNameID = ticket.DisplayName,
        ExpireTime = ticket.ExpireTime,
        Source = 1,
        StartingZoneName = ticket.Zone,
        StartingZoneLocation = ticket.Location,
        ClusterTemplate = ticket.Zone,
    });

    private static void EnsureTimer() {
        lock (s_timerLock) {
            s_timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private static void Tick() {
        try {
            var now = Now;
            foreach (var ticket in s_tickets.Values) {
                if (!IsOpen(ticket.OwnerCharId, ticket.Zone)) {
                    Remove(ticket, "its dungeon closed");
                }
                else if (IsOccupied(ticket)) {
                    // Someone is still inside: the timer stands still at the full length.
                    var full = FullExpireTime;
                    if (full != ticket.ExpireTime) {
                        ticket.ExpireTime = full;
                        ticket.Session?.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATERESUMEINSTANCETIME2 {
                            ExpireTime = full,
                            ClusterGID = ticket.OwnerCharId,
                            PlayerGID = ticket.CharId,
                        });
                    }
                }
                else if (now >= ticket.ExpireTime) {
                    Remove(ticket, "it ran out");
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Dungeon Recall: the timer failed: {0}", Logger.Args(e));
        }
    }

    private sealed class OwnerZoneComparer : System.Collections.Generic.IEqualityComparer<(ulong Owner, string Zone)> {

        public bool Equals((ulong Owner, string Zone) x, (ulong Owner, string Zone) y)
            => x.Owner == y.Owner && string.Equals(x.Zone, y.Zone, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ulong Owner, string Zone) key)
            => HashCode.Combine(key.Owner, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Zone));

    }

}
