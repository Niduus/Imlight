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
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The trainer hall door: walking up to one spot sends the wizard to their own copy of the Avalon Castle Plot, the test
/// area for spell trainers. The house zone can only be loaded as a house, so the wizard is given that house first.
/// </summary>
internal static class TrainerHallDoor {

    private static readonly bool s_enabled = ConfigurationManager.GetValue("TrainerHall.Enabled", true);
    private static readonly string s_doorZone = ConfigurationManager.GetValue("TrainerHall.DoorZone", "WizardCity/WC_Ravenwood");
    private static readonly float s_doorX = ConfigurationManager.GetValue("TrainerHall.DoorX", 560f);
    private static readonly float s_doorY = ConfigurationManager.GetValue("TrainerHall.DoorY", 548f);
    private static readonly float s_doorZ = ConfigurationManager.GetValue("TrainerHall.DoorZ", 0f);
    private static readonly float s_radius = ConfigurationManager.GetValue("TrainerHall.Radius", 60f);
    private static readonly uint s_houseTemplateId = ConfigurationManager.GetValue("TrainerHall.HouseTemplateId", 1217734u);

    // How tall the door spot is: a wizard standing on the step above or below still counts.
    private const float HEIGHT_TOLERANCE = 120f;
    private static readonly TimeSpan s_cooldown = TimeSpan.FromSeconds(10);

    // Wizards standing on the spot. Walking onto it sends them once; they have to step off before it sends them again.
    private static readonly ConcurrentDictionary<ulong, DateTime> s_onSpot = new();

    /// <summary>
    /// Checks a move and sends the wizard to the trainer hall when they walked onto the door spot.
    /// </summary>
    /// <param name="session">The wizard's SessionActor.</param>
    /// <param name="wizard">The wizard that moved.</param>
    /// <param name="x">The new X.</param>
    /// <param name="y">The new Y.</param>
    /// <param name="z">The new Z.</param>
    internal static void OnMove(IActorRef session, Wizard wizard, float x, float y, float z) {
        if (!s_enabled || wizard is null || !string.Equals(wizard.Zone, s_doorZone, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        var dx = x - s_doorX;
        var dy = y - s_doorY;
        var onSpot = dx * dx + dy * dy <= s_radius * s_radius && Math.Abs(z - s_doorZ) <= HEIGHT_TOLERANCE;
        if (!onSpot) {
            s_onSpot.TryRemove(wizard.CharId, out _);

            return;
        }

        var now = DateTime.UtcNow;
        if (s_onSpot.TryGetValue(wizard.CharId, out var since) && now - since < s_cooldown) {
            return;
        }

        if (s_onSpot.ContainsKey(wizard.CharId)) {
            // Still standing there after a send that did not happen; try again after the cooldown.
            s_onSpot[wizard.CharId] = now;
        }
        else if (!s_onSpot.TryAdd(wizard.CharId, now)) {
            return;
        }

        if (!HouseTemplates.TryGet(s_houseTemplateId, out var house)) {
            Logger.Error("The trainer hall house {TemplateId} is not a house.", Logger.Args(s_houseTemplateId));

            return;
        }

        // The wizard needs the house as their own before the game can load its zone.
        var grant = HouseDeeds.Issue(wizard, s_houseTemplateId);
        if (!grant.Succeeded) {
            Logger.Information("{0} could not be given the trainer hall house: {1}.",
                Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), grant.Failure));
            session.Tell(new GAME_5_PROTOCOL.MSG_CLIENTNOTIFYTEXT {
                NotifyText = grant.Failure == HouseGrantFailure.BackpackFull
                    ? "Make room in your backpack to enter the trainer hall."
                    : "The trainer hall could not be opened.",
                Type = 0,
            });

            return;
        }

        foreach (var item in grant.Created) {
            ItemSync.SendAdded(session, wizard, item);
        }

        Logger.Information("{0} walked through the trainer hall door.", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName()));
        session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = house.LotZone,
            DestinationLocation = HouseDeeds.EntryLocation,
            SendToClient = true,
            OwnerCharId = wizard.CharId,
        });
    }

}
