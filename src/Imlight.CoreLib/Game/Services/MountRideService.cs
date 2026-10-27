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
 * Lets group mates ride along on a mount with more than one seat (Niduus, 2026-10-27: "start work on multi person
 * mounts"). As the driver it keeps this wizard's entry in MountRides up to date; as a passenger it offers the
 * "Press X to Ride" prompt near a group mate's mount, sits this wizard down, keeps them with the driver (zone
 * changes, battles) and gets them off again.
 *
 * NOTE:
 * Everything is checked on a short timer, so a group change, a logout or a mount change on either side is noticed
 * without hooks in the other services. The rules are in MountRides.
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Mounts;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class MountRideService : MessageService {

    private const string TickTimerKey = "mount-ride";
    private static readonly TimeSpan s_tickInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_followTimeout = TimeSpan.FromSeconds(30);
    private const int SeatResendTicks = 10;

    // As the driver.
    private MountDriver _driver;

    // As a passenger.
    private MountDriver _ride;
    private ulong _promptDriverObject;
    private DateTime _followStarted;
    private bool _following;
    private int _seatResends;

    public MountRideService(SessionActor sessionActor) : base(sessionActor) {
        Timers.StartPeriodicTimer(TickTimerKey, new SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_TICK(), s_tickInterval, s_tickInterval);
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new MountRideService(parentActor));

    protected override void OnPreDispose() {
        Timers.Cancel(TickTimerKey);
        try {
            if (_ride is not null) {
                GetOff(tellOthers: true);
            }

            StopDriving();
        }
        catch (Exception ex) {
            Logger.Error("Error while leaving a multi-person mount at logout: {0}", Logger.Args(ex));
        }

        base.OnPreDispose();
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_TICK))]
    private void ReceiveTick(SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_TICK message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            if (!MountRides.Enabled) {
                if (_ride is not null) {
                    GetOff(tellOthers: true);
                }

                StopDriving();
                ClearPrompt();

                return;
            }

            KeepDriverEntry(wizard);
            if (_ride is not null) {
                KeepSeat(wizard);
            }
            else {
                KeepPrompt(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Error("Error while checking multi-person mounts: {0}", Logger.Args(ex));
        }
    }

    // ------------------------------------------------------------------
    // Driver
    // ------------------------------------------------------------------

    private void KeepDriverEntry(Wizard wizard) {
        // A battle takes the mount off for its length (CombatService); the entry stays so the riders go along.
        if (wizard.IsInDuel && _driver is not null) {
            _driver.InDuel = true;

            return;
        }

        var mount = wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount);
        var seats = MountRides.SeatsOf(mount);
        if (mount is null || seats < 2) {
            StopDriving();

            return;
        }

        if (_driver is null || _driver.MountItemId != mount.m_globalID) {
            StopDriving();
            _driver = new MountDriver {
                CharId = wizard.CharId,
                ObjectId = wizard.GameObjectID,
                Session = SessionActor.ActorRef,
                MountItemId = mount.m_globalID,
                MountTemplateId = mount.m_templateID.Full,
                Seats = seats,
            };
            Refresh(_driver, wizard);
            MountRides.Publish(_driver);
            Logger.Information("{0} is driving a mount with {1} seats ({2}).",
                Logger.Args(wizard.CharId, seats, mount.m_templateID));
            TellGroup(member => new GAME_5_PROTOCOL.MSG_PARTYMULTIPLAYERMOUNTEQUIPUPDATE {
                DestinationCharacterID = member,
                SourceCharacterID = wizard.CharId,
                MountTemplateID = mount.m_templateID.Full,
            });

            return;
        }

        Refresh(_driver, wizard);
    }

    private void Refresh(MountDriver driver, Wizard wizard) {
        driver.ZoneActor = SessionActor.GetZoneActor();
        driver.ZonePath = wizard.Zone;
        driver.Name = wizard.PlayerNameBehavior?.GetWizardName();
        driver.InDuel = wizard.IsInDuel;
        driver.InInstance = wizard.AttachedLotGid != 0 || World.InstancedPlayers.IsInInstance(wizard.CharId);
        driver.Location = wizard.Location;
    }

    private void StopDriving() {
        if (_driver is null) {
            return;
        }

        // Each rider's session sees the entry gone and gets its wizard off.
        MountRides.Withdraw(_driver.CharId);
        _driver = null;
    }

    // ------------------------------------------------------------------
    // Passenger: the prompt and getting on
    // ------------------------------------------------------------------

    private void KeepPrompt(Wizard wizard) {
        var driver = wizard.IsInDuel || HasOwnMount(wizard) ? null : NearestFreeMount(wizard);
        var objectId = driver?.ObjectId ?? 0;
        if (objectId == _promptDriverObject) {
            return;
        }

        ClearPrompt();
        if (driver is not null) {
            SendRidePrompt(driver);
            _promptDriverObject = objectId;
        }
    }

    private MountDriver NearestFreeMount(Wizard wizard) {
        var zone = SessionActor.GetZoneActor();
        if (zone is null) {
            return null;
        }

        var range = MountRides.RideRange;
        var here = wizard.Location;
        MountDriver best = null;
        var bestDistance = float.MaxValue;
        foreach (var driver in MountRides.Drivers) {
            if (!CanBoard(wizard, driver, zone)) {
                continue;
            }

            var spot = driver.Location;
            var distance = MathF.Sqrt(((spot.X - here.X) * (spot.X - here.X)) + ((spot.Y - here.Y) * (spot.Y - here.Y)));
            if (distance <= range && distance < bestDistance) {
                best = driver;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool CanBoard(Wizard wizard, MountDriver driver, IActorRef zone)
        => driver.CharId != wizard.CharId
           && driver.HasFreeSeat
           && !driver.InDuel
           && driver.ZoneActor is not null
           && driver.ZoneActor.Equals(zone)
           && (!MountRides.RequireGroup || GroupRegistry.AreGrouped(wizard.CharId, driver.CharId));

    private void SendRidePrompt(MountDriver driver) {
        var memento = new ServiceMementoBase {
            m_npcIcon = string.Empty,
            m_npcNameKey = "NPCFormats_Name",
            m_npcTextKey = MountRides.RidePromptKey,
            m_serviceOptions = [
                new InteractableOption {
                    m_serviceName = MountRides.RideServiceName,
                    m_displayKey = MountRides.RidePromptKey,
                    m_iconKey = "Interact",
                    m_serviceIndex = 0,
                }
            ],
            m_personaMadlibs = new MadlibBlock {
                m_blockToken = "NPC",
                m_madlibs = [
                    new MadlibArgT_WideByteString { m_madlibArgument = driver.Name ?? string.Empty, m_madlibToken = "NAME" }
                ]
            }
        };

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(memento, 4, out var data)) {
            Logger.Error("Failed to serialize the ride prompt for {0}'s mount.", Logger.Args(driver.CharId));

            return;
        }

        SendToSocket(new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
            MobileID = driver.ObjectId, // the object's global ID, whatever the name says
            Options = data,
            Reinteract = 0
        });
    }

    private void ClearPrompt() {
        if (_promptDriverObject == 0) {
            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE { MobileID = _promptDriverObject });
        _promptDriverObject = 0;
    }

    [MessageHandler(typeof(QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC))]
    private void ReceiveInteractNpc(QUEST_MESSAGES_52_PROTOCOL.MSG_INTERACTNPC message)
        => TryInteract(message.GlobalID);

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_INTERACTOPTION))]
    private void ReceiveInteractOption(GAME_5_PROTOCOL.MSG_INTERACTOPTION message)
        => TryInteract(message.ObjectID);

    private void TryInteract(ulong objectId) {
        if (!MountRides.Enabled || MountRides.GetDriverByObject(objectId) is not { } driver) {
            return;
        }

        Board(driver, fromCommand: false);
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_REQUEST))]
    private void ReceiveRideRequest(SERVICE_101_PROTOCOL.MSG_MOUNTRIDE_REQUEST message) {
        if (message.GetOff) {
            if (_ride is null) {
                InformGameClient("You are not riding anyone's mount.");

                return;
            }

            GetOff(tellOthers: true);

            return;
        }

        if (!MountRides.Enabled) {
            InformGameClient("Multi-person mounts are turned off on this server.");

            return;
        }

        var driver = MountRides.GetDriver(message.DriverCharId);
        if (driver is null) {
            InformGameClient("They are not on a mount with room for passengers.");

            return;
        }

        Board(driver, fromCommand: true);
    }

    private void Board(MountDriver driver, bool fromCommand) {
        var wizard = GetActiveWizard();
        if (wizard is null || _ride == driver) {
            return;
        }

        string refusal = null;
        if (wizard.IsInDuel) {
            refusal = "You can't get on a mount during a battle.";
        }
        else if (HasOwnMount(wizard)) {
            refusal = "Take off your own mount first.";
        }
        else if (MountRides.RequireGroup && !GroupRegistry.AreGrouped(wizard.CharId, driver.CharId)) {
            refusal = "You have to be in their group to ride their mount.";
        }
        else if (driver.ZoneActor is null || !driver.ZoneActor.Equals(SessionActor.GetZoneActor())) {
            refusal = "They are not in this area.";
        }
        else if (driver.InDuel) {
            refusal = "They are in a battle.";
        }
        else if (!fromCommand && Distance(wizard.Location, driver.Location) > MountRides.RideRange * 1.5f) {
            refusal = "You aren't close enough.";
        }

        if (refusal is null && _ride is not null) {
            GetOff(tellOthers: true);
        }

        if (refusal is null && !MountRides.TrySit(driver, wizard.CharId)) {
            refusal = "That mount is full.";
        }

        if (refusal is not null) {
            InformGameClient(refusal);

            return;
        }

        ClearPrompt();
        _ride = driver;
        _following = false;
        _seatResends = SeatResendTicks;
        wizard.Location = driver.Location;
        BroadcastSeat(wizard, riding: true);
        Logger.Information("{0} got on {1}'s mount.", Logger.Args(wizard.CharId, driver.CharId));

        TellGroup(member => new GAME_5_PROTOCOL.MSG_PARTYMULTIPLAYERMOUNTJOINUPDATE {
            DestinationCharacterID = member,
            SourceCharacterID = wizard.CharId,
            MountOwnerCharacterID = driver.CharId,
            MountTemplateID = driver.MountTemplateId,
        });
    }

    // ------------------------------------------------------------------
    // Passenger: staying on and getting off
    // ------------------------------------------------------------------

    private void KeepSeat(Wizard wizard) {
        var driver = MountRides.GetDriver(_ride.CharId);
        if (driver != _ride || !MountRides.IsRiding(wizard.CharId)) {
            GetOff(tellOthers: true); // the driver took the mount off or logged out

            return;
        }

        if (MountRides.RequireGroup && !GroupRegistry.AreGrouped(wizard.CharId, driver.CharId)) {
            GetOff(tellOthers: true);

            return;
        }

        if (HasOwnMount(wizard) || wizard.IsInDuel) {
            GetOff(tellOthers: true);

            return;
        }

        if (driver.InDuel) {
            // Off the mount where the driver stands, so the battle takes this wizard in as they walk up to it.
            wizard.Location = driver.Location;
            GetOff(tellOthers: true);
            if (MountRides.JoinDriverBattles) {
                ServerTeleportSelf(driver.Location);
            }

            return;
        }

        var zone = SessionActor.GetZoneActor();
        if (driver.ZoneActor is null || zone is null) {
            return; // one of them is between zones
        }

        if (!driver.ZoneActor.Equals(zone)) {
            FollowDriver(driver);

            return;
        }

        if (_following) {
            // Arrived in the driver's zone: sit down again for everyone here.
            _following = false;
            _seatResends = SeatResendTicks;
        }

        wizard.Location = driver.Location;

        // Told again about 1, 2 and 3 seconds after sitting down or arriving, in case a game had not drawn the driver
        // (or this wizard) yet when it was first told.
        if (_seatResends > 0) {
            _seatResends--;
            if (_seatResends % 3 == 0) {
                BroadcastSeat(wizard, riding: true);
            }
        }
    }

    private void FollowDriver(MountDriver driver) {
        if (!MountRides.FollowDriver || driver.InInstance || string.IsNullOrEmpty(driver.ZonePath)) {
            GetOff(tellOthers: true);

            return;
        }

        if (_following) {
            if (DateTime.UtcNow - _followStarted > s_followTimeout) {
                Logger.Information("{0} could not follow {1}'s mount to {2}.",
                    Logger.Args(GetActiveWizard()?.CharId, driver.CharId, driver.ZonePath));
                GetOff(tellOthers: false);
            }

            return;
        }

        _following = true;
        _followStarted = DateTime.UtcNow;
        Logger.Information("{0} follows {1}'s mount to {2}.",
            Logger.Args(GetActiveWizard()?.CharId, driver.CharId, driver.ZonePath));
        Teleport(
            destinationZone: driver.ZonePath,
            doTeleportEffects: false,
            ownerCharId: driver.CharId,
            destinationLocation: Util.GetCompactStringFromVector((Vector4) driver.Location));
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        // A new zone was loaded: the old prompt is gone with it, and a rider sits down again once the game is in.
        _promptDriverObject = 0;
        if (_ride is not null) {
            _seatResends = SeatResendTicks;
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_RIDEMOUNT))]
    private void ReceiveRideMount(WIZARD_12_PROTOCOL.MSG_RIDEMOUNT message) {
        // The rider's game sends this when the rider moves off the seat.
        Logger.Information("The game sent MSG_RIDEMOUNT: GlobalID {0}, DriverID {1}, Riding {2}, at {3} {4} {5}.",
            Logger.Args(message.GlobalID, message.DriverID, message.Riding, message.LocationX, message.LocationY, message.LocationZ));
        if (_ride is null) {
            return;
        }

        var wizard = GetActiveWizard();
        if (wizard is not null && message.Riding == 0) {
            wizard.Location = new Vector3(
                unchecked((short) message.LocationX * 4),
                unchecked((short) message.LocationY * 4),
                unchecked((short) message.LocationZ * 4));
        }

        GetOff(tellOthers: true);
    }

    private void GetOff(bool tellOthers) {
        var wizard = GetActiveWizard();
        var driver = _ride;
        _ride = null;
        _following = false;
        if (wizard is null || driver is null) {
            return;
        }

        MountRides.Stand(wizard.CharId);
        if (tellOthers) {
            BroadcastSeat(wizard, riding: false, driverObjectId: driver.ObjectId);
        }

        Logger.Information("{0} got off {1}'s mount.", Logger.Args(wizard.CharId, driver.CharId));
    }

    private void BroadcastSeat(Wizard wizard, bool riding, ulong driverObjectId = 0) {
        var message = MountRides.RideMessage(wizard.GameObjectID, riding ? _ride.ObjectId : driverObjectId, riding, wizard.Location);
        var zone = SessionActor.GetZoneActor();
        if (zone is null) {
            return;
        }

        zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = message,
            Selfless = false,
            Sender = SessionActor.ActorRef,
            Targets = ZoneBroadcastTarget.Players,
        }, Self);
    }

    private void ServerTeleportSelf(Vector3 location) {
        var wizard = GetActiveWizard();
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
            LocationX = unchecked((ushort) (short) Math.Round(location.X / 4)),
            LocationY = unchecked((ushort) (short) Math.Round(location.Y / 4)),
            LocationZ = unchecked((ushort) (short) Math.Round(location.Z / 4)),
            Direction = (byte) Math.Round(wizard.Orientation.Z / Math.PI / 2 * 250),
            MobileID = wizard.GameObject.m_nMobileID,
        }, isSelfless: false, ZoneBroadcastTarget.Players);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool HasOwnMount(Wizard wizard)
        => wizard.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is not null;

    private static float Distance(Vector3 a, Vector3 b)
        => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>A group chat line for every online group mate, this wizard included.</summary>
    private void TellGroup(Func<ulong, Imcodec.MessageLayer.IMessage> makeMessage) {
        var wizard = GetActiveWizard();
        if (!MountRides.GroupChatLines || wizard is null || !GroupRegistry.TryGetGroup(wizard.CharId, out var group)) {
            return;
        }

        foreach (var member in group.Members.Where(member => member.IsOnline)) {
            member.Session.Tell(makeMessage(member.CharId));
        }
    }

}
