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

using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Linq;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandTeleport : CommandProtocol {

    internal override string Group { get; set; } = "";

    private static readonly string s_gmIslandZoneName = "WizardCity/QA_SpawnRate";
    private static readonly string[] s_gmIslandShortcutNames = [
        "gm", "gmisland", "gm_island", "gmis", "gm_is", "gm_isl", "gm_isla", "gm_islan", "gm_island"
    ];

    // .tp takes a zone's code name (WizardCity/WC_Streets/WC_Unicorn, or just WC_Unicorn) or the name players know it by
    // (Unicorn Way), with small typos forgiven (Niduus, 2026-11-04: ".tp able to read either"). Database > Locations
    // in the Manager lists both names of every zone.
    [Command("teleport")]
    [Alias("tp", "port")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TeleportCommand([Remainder] string zone) {
        zone = (zone ?? "").Trim();
        if (s_gmIslandShortcutNames.Any(x => x.Equals(zone, System.StringComparison.OrdinalIgnoreCase))) {
            // Check the account auth level to see if they can teleport to the GM Island.
            if (Context.Account.AuthLevel < AuthLevel.HallMonitor) {
                InformSenderClient($"Zone {zone} does not exist.");

                return;
            }

            SendTo(s_gmIslandZoneName);

            return;
        }

        if (ZoneNames.Find(zone) is not { } match) {
            Logger.Warning("Teleport command was given an invalid zone name {0}", Logger.Args(zone));
            InformSenderClient($"No place is called {zone}. Type its name (such as Unicorn Way) or its code name (such as WC_Unicorn). " +
                "Ahziael's Database > Locations lists every place with both names.");

            return;
        }

        var target = match.Zone;
        var shown = target.Name.Length > 0 ? $"{target.Name} ({target.Code})" : target.Code;
        var same = match.Others.Count(z => z.Name.Length > 0 && z.Name.Equals(target.Name, System.StringComparison.OrdinalIgnoreCase));
        if (match.How == "code name" || match.Others.Count == 0) {
            InformSenderClient($"Going to {shown}.");
        }
        else if (match.How == "name" && same > 0) {
            InformSenderClient($"Going to {shown}. {same} more place{(same == 1 ? " is" : "s are")} also called {target.Name}, " +
                "such as " + string.Join(", ", match.Others.Take(3).Select(z => z.Tail)) + ". To go to one of those, type .tp with its code name.");
        }
        else {
            InformSenderClient($"Going to {shown}, the closest match for {zone}. Others: " +
                string.Join(", ", match.Others.Take(3).Select(z => z.Name.Length > 0 ? $"{z.Name} ({z.Tail})" : z.Tail)) +
                (match.Others.Count > 3 ? $" and {match.Others.Count - 3} more." : "."));
        }

        Logger.Information("Teleport {0} -> {1} (by {2}).", Logger.Args(zone, target.Code, match.How));
        SendTo(target.Code);
    }

    // The Manager's quest step buttons: right to where a step of a quest happens (Game/Tools/QuestStepSpots), even when
    // the quests or doors before it are not done (Niduus, 2026-10-28).
    [Command("tpstep")]
    [Alias("stepto")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TeleportToStepCommand(string quest, string step) {
        var template = WizardData.Collections.QuestTemplateCollection.GetQuestByName(quest);
        if (template is null) {
            InformSenderClient($"There is no quest {quest}.");

            return;
        }

        if (!int.TryParse(step, out var number)
            || Tools.QuestStepSpots.Find(template, number) is not { } spot) {
            InformSenderClient($"Step {step} of {quest} has no place the server knows.");

            return;
        }

        Logger.Information("Teleport to step {0} of {1}: {2} at {3} ({4}).", Logger.Args(number, quest, spot.Zone, spot.Location, spot.How));
        InformSenderClient($"Going to step {number}: {spot.How}.");
        SendTo(spot.Zone, spot.Location);
    }

    private const string BAZAAR_ZONE = "WizardCity/WC_Streets/Interiors/WC_OldeTown_AuctionHouse";

    [Command("bazaar")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void BazaarCommand() => SendTo(BAZAAR_ZONE);

    private void SendTo(string actualZoneName, string location = "Start") {
        var teleportEffectsMsg = new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS();
        Context.SessionActor.Tell(teleportEffectsMsg);

        // Wait 2 seconds.
        Task.Delay(2000).Wait();

        var msg = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER() {
            DestinationZone = actualZoneName,
            DestinationLocation = location,
            SendToClient = true,
            OwnerCharId = Context.Character.CharId
        };
        Context.SessionActor.Tell(msg);
    }

}
