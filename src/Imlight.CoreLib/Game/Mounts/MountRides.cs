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
 * MULTI-PERSON MOUNTS
 * ========================================================================
 *
 * PURPOSE:
 * Who drives a mount with more than one seat and who rides along on it, shared by every session. A driver's own
 * MountRideService keeps its entry up to date (zone, spot, battle); each rider's MountRideService keeps its own seat.
 *
 * NOTE:
 * The game's own rules (Help.lang, "Mounts"): riders must be in the driver's group; a rider who leaves the group, or
 * a driver who does, gets everyone off. Riders hop on with X near the mount. When the driver battles, teleports or
 * takes a sigil the riders go along. A rider gets off by moving or by putting on their own mount; the driver taking
 * the mount off gets everyone off. MountItemBehaviorTemplate.m_numSeats counts the driver's seat too.
 *
 * Messages: MSG_RIDEMOUNT (WIZARD 183) tells every game in the zone that a wizard sits on (Riding 1) or got off
 * (Riding 0) a driver's mount; the rider's own game sends it back with Riding 0 when the rider moves.
 * MSG_PARTYMULTIPLAYERMOUNTEQUIPUPDATE and ...JOINUPDATE (GAME 141, 143) only print the group chat lines
 * "NAME is riding a MOUNT. Hop on board!" and "NAME is now a passenger on OWNER's MOUNT".
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Mounts;

/// <summary>A wizard on a mount with room for passengers, as their own session last saw them.</summary>
internal sealed class MountDriver {

    public ulong CharId { get; init; }
    public ulong ObjectId { get; init; }
    public volatile IActorRef Session;

    // Written only by the driver's session, read by the riders' sessions.
    public volatile IActorRef ZoneActor;
    public volatile string ZonePath;
    public volatile string Name;
    public volatile bool InDuel;
    public volatile bool InInstance;
    /// <summary>The mount is stowed for a trip and comes back on in the next area that allows it.</summary>
    public volatile bool Stowed;
    public DateTime StowedSince;

    private long _lastSeenTicks = DateTime.UtcNow.Ticks;

    /// <summary>The driver's session checked in just now.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);

    /// <summary>No session of the driver has checked in for a while: logged out or lost.</summary>
    public bool IsGone => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSeenTicks) > MountRides.GoneAfter.Ticks;
    public ulong MountItemId;
    public ulong MountTemplateId;
    public int Seats;
    private Vector3 _location;
    private readonly object _locationLock = new();

    public Vector3 Location {
        get { lock (_locationLock) { return _location; } }
        set { lock (_locationLock) { _location = value; } }
    }

    /// <summary>Riders in seat order.</summary>
    public readonly List<ulong> Riders = [];

    /// <summary>Passenger seats (the driver takes one of the mount's seats).</summary>
    public int Passengers => Math.Max(0, Seats - 1);

    public bool HasFreeSeat {
        get { lock (Riders) { return Riders.Count < Passengers; } }
    }

    public ulong[] RiderIds {
        get { lock (Riders) { return [.. Riders]; } }
    }

}

internal static class MountRides {

    // [Mounts], read on every use so a change applies without a restart.
    internal static bool Enabled => ConfigurationManager.GetValue("Mounts.MultiPersonMounts", true);
    internal static bool RequireGroup => ConfigurationManager.GetValue("Mounts.RequireGroup", true);
    internal static float RideRange => Math.Clamp(ConfigurationManager.GetValue("Mounts.RideRange", 250f), 25f, 2000f);
    internal static bool FollowDriver => ConfigurationManager.GetValue("Mounts.RidersFollowDriver", true);
    internal static bool JoinDriverBattles => ConfigurationManager.GetValue("Mounts.RidersJoinDriverBattles", true);
    internal static bool GroupChatLines => ConfigurationManager.GetValue("Mounts.GroupChatLines", true);

    /// <summary>"Press X or click to Ride" (GUI.lang).</summary>
    internal const string RidePromptKey = "GUI_00000029";
    internal const string RideServiceName = "RideMount";

    private static readonly ConcurrentDictionary<ulong, MountDriver> s_drivers = new();
    private static readonly ConcurrentDictionary<ulong, ulong> s_driverOfRider = new();
    private static readonly ConcurrentDictionary<ulong, long> s_riderSeenTicks = new();

    /// <summary>
    /// How long a seat or a driver entry outlasts its wizard's last check-in. A zone change takes a few seconds with
    /// no session at all; a logout that was not seen (a lost connection) runs out after this.
    /// </summary>
    internal static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(25);

    internal static void TouchRider(ulong riderCharId) => s_riderSeenTicks[riderCharId] = DateTime.UtcNow.Ticks;

    /// <summary>Frees the seats of riders who have not checked in for a while.</summary>
    internal static void DropGoneRiders(MountDriver driver) {
        var now = DateTime.UtcNow.Ticks;
        foreach (var riderCharId in driver.RiderIds) {
            if (!s_riderSeenTicks.TryGetValue(riderCharId, out var seen) || now - seen > GoneAfter.Ticks) {
                Stand(riderCharId);
            }
        }
    }

    internal static IEnumerable<MountDriver> Drivers => s_drivers.Values;

    internal static MountDriver GetDriver(ulong charId)
        => s_drivers.TryGetValue(charId, out var driver) ? driver : null;

    internal static MountDriver GetDriverByObject(ulong objectId)
        => Wizard.TryGetCharacterId(objectId, out var charId) ? GetDriver(charId) : null;

    internal static bool IsDriverObject(ulong objectId)
        => GetDriverByObject(objectId) is not null;

    internal static MountDriver GetDriverOfRider(ulong riderCharId)
        => s_driverOfRider.TryGetValue(riderCharId, out var driverCharId) ? GetDriver(driverCharId) : null;

    internal static bool IsRiding(ulong charId) => s_driverOfRider.ContainsKey(charId);

    internal static void Publish(MountDriver driver) => s_drivers[driver.CharId] = driver;

    /// <summary>Takes the driver's entry away; the riders' sessions see it is gone and get off.</summary>
    internal static void Withdraw(ulong driverCharId) => s_drivers.TryRemove(driverCharId, out _);

    /// <summary>Gives the rider a free seat on the driver's mount. False when it is full.</summary>
    internal static bool TrySit(MountDriver driver, ulong riderCharId) {
        lock (driver.Riders) {
            if (driver.Riders.Contains(riderCharId)) {
                s_driverOfRider[riderCharId] = driver.CharId;

                return true;
            }

            if (driver.Riders.Count >= driver.Passengers) {
                return false;
            }

            driver.Riders.Add(riderCharId);
        }

        s_driverOfRider[riderCharId] = driver.CharId;
        TouchRider(riderCharId);

        return true;
    }

    internal static void Stand(ulong riderCharId) {
        if (!s_driverOfRider.TryRemove(riderCharId, out var driverCharId)) {
            return;
        }

        if (GetDriver(driverCharId) is { } driver) {
            lock (driver.Riders) {
                driver.Riders.Remove(riderCharId);
            }
        }
    }

    /// <summary>How many seats the mount has (driver included), 1 for a plain mount or no mount.</summary>
    internal static int SeatsOf(WizClientObjectItem mount) {
        if (mount is null) {
            return 1;
        }

        var template = ItemHelper.GetItemTemplate(mount);
        var behavior = template?.m_behaviors?.OfType<MountItemBehaviorTemplate>().FirstOrDefault();

        return Math.Max(1, behavior?.m_numSeats ?? 1);
    }

    internal static WIZARD_12_PROTOCOL.MSG_RIDEMOUNT RideMessage(ulong riderObjectId, ulong driverObjectId, bool riding, Vector3 location)
        => new() {
            GlobalID = riderObjectId,
            DriverID = driverObjectId,
            Riding = (byte) (riding ? 1 : 0),
            // Packed the way moves are: a quarter of the spot, as a signed short.
            LocationX = unchecked((ushort) (short) Math.Round(location.X / 4)),
            LocationY = unchecked((ushort) (short) Math.Round(location.Y / 4)),
            LocationZ = unchecked((ushort) (short) Math.Round(location.Z / 4)),
        };

    /// <summary>
    /// For a player who is shown this wizard after they sat down: their seat on the driver's mount, or the riders on
    /// this wizard's own mount. Sent right behind the wizard's object.
    /// </summary>
    internal static List<IMessage> SpawnMessages(Wizard wizard) {
        var messages = new List<IMessage>();
        if (!Enabled || wizard is null) {
            return messages;
        }

        if (GetDriverOfRider(wizard.CharId) is { Stowed: false } driver) {
            messages.Add(RideMessage(wizard.GameObjectID, driver.ObjectId, true, driver.Location));
        }

        if (GetDriver(wizard.CharId) is { Stowed: false } own) {
            foreach (var riderCharId in own.RiderIds) {
                messages.Add(RideMessage(Wizard.GetGameObjectId(riderCharId), own.ObjectId, true, own.Location));
            }
        }

        return messages;
    }

}
