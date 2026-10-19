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
/// `.hall`: gives one trainer tome (the Burrower Lectern), the same furniture Abner sells; `.hall 5` gives five. Each
/// one placed in a house brings a set trainer (see <see cref="HouseTrainerSets"/>). `.hall flip`, in the house, hands
/// the trainer parts out along the row from the other end. `.hall vendors` gives one piece of seller furniture per
/// reagent kind (spellements included) and per jewel shape and pin; each placed in a house brings its seller (see
/// <see cref="HouseVendors"/>).
/// </summary>
internal class CommandHallProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // The most tomes one .hall gives.
    private const int MAX_TOMES = 100;

    [Command("hall")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HallCommand([Remainder] string argument = "") {
        argument = (argument ?? "").Trim();
        if (argument.Equals("flip", System.StringComparison.OrdinalIgnoreCase)) {
            Flip();

            return;
        }

        if (argument.Equals("vendors", System.StringComparison.OrdinalIgnoreCase)
            || argument.Equals("sellers", System.StringComparison.OrdinalIgnoreCase)) {
            GiveSellers();

            return;
        }

        var count = 1;
        if (argument.Length > 0 && (!int.TryParse(argument, out count) || count < 1)) {
            InformSenderClient($"Use .hall for one trainer tome, .hall and a number (up to {MAX_TOMES}) for more, .hall vendors for the sellers, or .hall flip in your house.");

            return;
        }

        count = System.Math.Min(count, MAX_TOMES);
        var given = 0;
        var where = Placement.Backpack;
        for (var i = 0; i < count; i++) {
            if (!Context.Character.TryCreateItem(HouseTrainers.ItemTemplateId, out var item)) {
                break;
            }

            var placement = ItemPlacement.Place(Context.Character, item);
            ItemPlacement.Announce(Context.SessionActor, Context.Character, item, placement);
            if (placement is not (Placement.Backpack or Placement.Bank or Placement.SharedBank)) {
                break;
            }

            given++;
            where = placement;
        }

        if (given == 0) {
            InformSenderClient("Could not add a trainer tome, your backpack and bank are full.");

            return;
        }

        var tomes = given == 1 ? "a trainer tome" : $"{given} trainer tomes";
        InformSenderClient(where switch {
            Placement.Backpack => $"Added {tomes} to your backpack. Each one placed in your house brings the next set trainer.",
            _ => $"Added {tomes}; some went to your bank because your backpack is full.",
        } + (given < count ? $" Only {given} of {count} fit." : "") + $" All the set trainers need {HouseTrainerSets.Sets.Count} tomes.");
    }

    private void GiveSellers() {
        var given = new System.Collections.Generic.List<string>();
        foreach (var seller in HouseVendors.Sellers) {
            if (!Context.Character.TryCreateItem(seller.FurnitureId, out var item)) {
                continue;
            }

            var placement = ItemPlacement.Place(Context.Character, item);
            ItemPlacement.Announce(Context.SessionActor, Context.Character, item, placement);
            if (placement is not (Placement.Backpack or Placement.Bank or Placement.SharedBank)) {
                break;
            }

            given.Add(seller.Name);
        }

        InformSenderClient(given.Count == 0
            ? "Could not add the seller furniture, your backpack and bank are full."
            : $"Added {given.Count} pieces of seller furniture. Place one in your house and its seller stands on it: "
              + string.Join(", ", given) + ".");
    }

    private void Flip() {
        var lot = Context.Character.AttachedLotGid;
        if (lot == 0) {
            InformSenderClient("Go into the house with the trainer tomes first, then type .hall flip.");

            return;
        }

        HouseTrainerSets.Flip(lot);
        InformSenderClient("The trainer parts now go along the row from the other end. Leave the house and come back in to see the new names.");
    }

}
