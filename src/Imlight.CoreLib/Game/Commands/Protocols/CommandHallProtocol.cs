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
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.hall`: gives one trainer tome (the Burrower Lectern), the same furniture Abner sells. Each one placed in a
/// house brings a spell trainer, so it marks where a tome goes.
/// </summary>
internal class CommandHallProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("hall")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HallCommand() {
        if (!Context.Character.TryCreateItem(HouseTrainers.ItemTemplateId, out var item)) {
            InformSenderClient("Could not create the trainer tome.");

            return;
        }

        var placement = ItemPlacement.Place(Context.Character, item);
        ItemPlacement.Announce(Context.SessionActor, Context.Character, item, placement);
        InformSenderClient(placement switch {
            Placement.Backpack => "Added a trainer tome to your backpack. Place it in your house where a tome should stand; type .hall again for another.",
            Placement.Bank => "Added a trainer tome to your bank, your backpack is full.",
            Placement.SharedBank => "Added a trainer tome to your shared bank, your backpack is full.",
            _ => "Could not add the trainer tome, your backpack and bank are full.",
        });
    }

}
