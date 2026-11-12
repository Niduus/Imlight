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
 */

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.QuestTeam;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// .questteam tp Ice (or a name): goes right next to a Quest Team wizard, wherever it is, a dungeon's own copy included, to
/// watch it play (Niduus, 2026-11-12: "be able to TP to the bots, regardless of the area they are in to see them and their
/// progress"). Ahziael's Quest Team page has a button for each wizard.
/// </summary>
internal class CommandQuestTeam : CommandProtocol {

    internal override string Group { get; set; } = "questteam";

    [Command("tp")]
    [Alias("goto", "go")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TeleportCommand([Remainder] string who) {
        var members = QuestTeamDirector.Members;
        if (members.Count == 0) {
            InformSenderClient("The Quest Team is not playing right now. Turn it on in Ahziael (Quest Team > Quest Team) with the server running.");

            return;
        }

        who = (who ?? "").Trim();
        var member = who.Length == 0 ? members[0]
            : members.FirstOrDefault(m => m.Role.Equals(who, StringComparison.OrdinalIgnoreCase))
              ?? members.FirstOrDefault(m => (m.Name ?? "").Contains(who, StringComparison.OrdinalIgnoreCase));
        if (member is null) {
            InformSenderClient($"No Quest Team wizard is called {who}. Use Ice, Life, Death or Storm.");

            return;
        }

        var zone = member.Zone;
        if (member.State != "playing" || string.IsNullOrEmpty(zone)) {
            InformSenderClient($"{member.Name} ({member.Role}) is not in the game right now ({member.State}).");

            return;
        }

        var me = Context.Character;
        var inInstance = member.Wizard?.ZoneIsInstance == true;
        var owner = inInstance ? ZoneInstanceOwners.TryGetOwner(member.CharId, zone, out var assigned) ? assigned : member.CharId : me.CharId;
        if (inInstance) {
            // The attach after the transfer lands in the same copy of the dungeon as the wizard.
            ZoneInstanceOwners.Assign(me.CharId, zone, owner);
        }

        // A step beside it, facing the same way.
        var location = string.Create(CultureInfo.InvariantCulture, $"{member.X + 40},{member.Y},{member.Z + 5},0");
        Logger.Information("Quest Team: {0} goes to {1} ({2}) in {3}{4}.",
            Logger.Args(me.CharId, member.Name, member.Role, zone, inInstance ? $" (copy of {owner})" : ""));
        InformSenderClient($"Going to {member.Name} ({member.Role}) in {ZoneNames.Shown(zone)}.");

        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        Task.Delay(1500).Wait();
        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = zone,
            DestinationLocation = location,
            SendToClient = true,
            OwnerCharId = owner,
            IsPrivate = inInstance,
            // Another copy of the same zone the player stands in loads anew; the same copy just moves them.
            ForceReload = inInstance && string.Equals(me.Zone, zone, StringComparison.OrdinalIgnoreCase),
        });
    }

    [Command("where")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WhereCommand() {
        var members = QuestTeamDirector.Members;
        if (members.Count == 0) {
            InformSenderClient("The Quest Team is not playing right now.");

            return;
        }

        foreach (var member in members) {
            InformSenderClient($"{member.Role}: {member.Name}, level {member.Wizard?.MagicSchoolBehavior?.Level ?? 0}, " +
                               $"{(string.IsNullOrEmpty(member.Zone) ? member.State : ZoneNames.Shown(member.Zone))}. .questteam tp {member.Role} goes there.");
        }
    }

}
