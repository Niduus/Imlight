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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The trainer hall door: walking up to one spot sends the wizard to their own copy of the Open Space house, the test
/// area for spell trainers. The house zone can only be loaded as a house, so the wizard is given that house first.
/// </summary>
internal static class TrainerHallDoor {

    private static readonly bool s_enabled = ConfigurationManager.GetValue("TrainerHall.Enabled", true);
    private static readonly string s_doorZone = ConfigurationManager.GetValue("TrainerHall.DoorArea", "WizardCity/WC_Hub");
    private static readonly float s_doorX = ConfigurationManager.GetValue("TrainerHall.DoorX", 560f);
    private static readonly float s_doorY = ConfigurationManager.GetValue("TrainerHall.DoorY", 548f);
    private static readonly float s_doorZ = ConfigurationManager.GetValue("TrainerHall.DoorZ", 0f);
    private static readonly float s_radius = ConfigurationManager.GetValue("TrainerHall.Radius", 60f);
    private static readonly uint s_houseTemplateId = ConfigurationManager.GetValue("TrainerHall.HallHouseId", 1520349u);

    /// <summary>Whether the trainer hall is turned on.</summary>
    internal static bool Enabled => s_enabled;

    /// <summary>The house the trainer hall is a copy of.</summary>
    internal static uint HouseTemplateId => s_houseTemplateId;

    // How tall the door spot is: a wizard standing on the step above or below still counts.
    private const float HEIGHT_TOLERANCE = 120f;

    // Wizards who stepped off the spot since they arrived in the door's area. Someone who arrives on the spot (the
    // World Gate in the hall sends them there) has to step off before it sends them again.
    private static readonly ConcurrentDictionary<ulong, bool> s_armed = new();

    /// <summary>
    /// Finds where the World Gate in the trainer hall leads: back to the door, in the door's area.
    /// </summary>
    /// <returns><c>false</c> when the zone is not the trainer hall.</returns>
    internal static bool TryGetExit(string houseZone, out string zone, out string location) {
        zone = null;
        location = null;
        if (!s_enabled || !HouseTemplates.TryGet(s_houseTemplateId, out var hall)
            || !string.Equals(houseZone, hall.LotZone, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        zone = s_doorZone;
        location = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s_doorX},{s_doorY},{s_doorZ},0");

        return true;
    }

    /// <summary>
    /// Checks a move and sends the wizard to the trainer hall when they walked onto the door spot.
    /// </summary>
    /// <param name="session">The wizard's SessionActor.</param>
    /// <param name="wizard">The wizard that moved.</param>
    /// <param name="x">The new X.</param>
    /// <param name="y">The new Y.</param>
    /// <param name="z">The new Z.</param>
    internal static void OnMove(IActorRef session, Wizard wizard, float x, float y, float z) {
        if (!s_enabled || wizard is null) {
            return;
        }

        if (!string.Equals(wizard.Zone, s_doorZone, StringComparison.OrdinalIgnoreCase)) {
            s_armed.TryRemove(wizard.CharId, out _);

            return;
        }

        var dx = x - s_doorX;
        var dy = y - s_doorY;
        var onSpot = dx * dx + dy * dy <= s_radius * s_radius && Math.Abs(z - s_doorZ) <= HEIGHT_TOLERANCE;
        if (!onSpot) {
            s_armed[wizard.CharId] = true;

            return;
        }

        // Walking onto the spot sends them once; they have to step off before it sends them again.
        if (!s_armed.TryUpdate(wizard.CharId, false, true)) {
            return;
        }

        // The wizard needs the house as their own before the game can load its zone, and enters it like Home does.
        if (!HouseDeeds.TryPrepareHouse(wizard, s_houseTemplateId, out var entry, out var failure)) {
            Logger.Information("{0} could not be given the trainer hall house: {1}.",
                Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), failure));
            session.Tell(new GAME_5_PROTOCOL.MSG_CLIENTNOTIFYTEXT {
                NotifyText = failure == HouseGrantFailure.BackpackFull
                    ? "Make room in your backpack to enter the trainer hall."
                    : "The trainer hall could not be opened.",
                Type = 0,
            });

            return;
        }

        Logger.Information("{0} walked through the trainer hall door.", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName()));
        session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.Zone,
            DestinationLocation = HouseDeeds.EntryLocation,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = entry.OwnerCharId,
            HouseEntry = entry,
        });
    }

}
