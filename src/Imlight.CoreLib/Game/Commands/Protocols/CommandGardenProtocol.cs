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

using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.garden` shows the gardening rank and the garden of the house one is in. For testing: `.garden grow` grows every
/// plant one stage, `.garden ready` makes them ready to harvest, `.garden rank 5` sets the rank, `.garden spells` teaches
/// the garden spells of the rank and `.garden seeds` gives a few seeds (Game/Services/HousingService.Gardening).
/// </summary>
internal class CommandGardenProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("garden")]
    [Alias("gardening")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GardenCommand([Remainder] string what = "") {
        var parts = (what ?? "").Trim().ToLowerInvariant().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length > 0 ? parts[0] : "status";
        var value = 0;
        if (action is "level") {
            action = "rank";
        }

        if (action == "rank" && (parts.Length < 2 || !int.TryParse(parts[1], out value) || value < 1 || value > 20)) {
            InformSenderClient("Type .garden rank followed by a number from 1 to 20, such as .garden rank 5.");

            return;
        }

        if (action is not ("status" or "grow" or "ready" or "rank" or "spells" or "seeds")) {
            InformSenderClient("Type .garden, .garden grow, .garden ready, .garden rank <1-20>, .garden spells or .garden seeds.");

            return;
        }

        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_GARDENCOMMAND { Action = action, Value = value }, ActorRefs.NoSender);
    }

}
