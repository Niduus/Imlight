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

using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.garden` shows the gardening rank and the garden of the house one is in. For testing: `.garden grow` grows every
/// plant one stage, `.garden ready` makes them ready to harvest, `.garden rank 5` sets the rank, `.garden spells` teaches
/// the garden spells of the rank and `.garden seeds` gives a few seeds (Game/Services/HousingService.Gardening).
/// Plant growth (Niduus, 2026-10-25: "Implement testing commands for this"): `.garden speed 60` makes every plant grow
/// 60 times faster, `.garden speed young 10` only the young stage, `.garden speed reset` the game's own times,
/// `.garden pause` and `.garden resume` stop and start every garden, `.garden stage mature` puts every plant of one's
/// garden in a stage, `.garden age 2h` lets two hours of plant time pass and `.garden times` lists each plant's times.
/// The speeds are the Manager's Plant growth card's (Game/Gardening/GardenTiming).
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

        if (action is "unpause") {
            action = "resume";
        }

        if (action is not ("status" or "grow" or "ready" or "rank" or "spells" or "seeds" or "speed" or "pause" or "resume"
            or "stage" or "age" or "times")) {
            InformSenderClient("Type .garden, .garden grow, .garden ready, .garden rank <1-20>, .garden spells, .garden seeds, "
                + ".garden speed <number|reset>, .garden speed <seedling|young|mature|harvest|needs> <number>, .garden pause, "
                + ".garden resume, .garden stage <seedling|young|mature|elder|wilting|dead>, .garden age <30m|2h|1d> or .garden times.");

            return;
        }

        var text = string.Join(' ', parts.Skip(1));
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_GARDENCOMMAND { Action = action, Value = value, Text = text }, ActorRefs.NoSender);
    }

}
