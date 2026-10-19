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

using System.Globalization;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Storage space commands without a group, so they are typed as `.backpack 200` and `.bank max`.
/// </summary>
internal class CommandSpaceProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // What ".backpack max" and ".bank max" give. Very large lists can be too big for the game to receive in one message.
    private static readonly int s_backpackCommandMax = ConfigurationManager.GetValue("Character.BackpackCommandMax", 500);
    private static readonly int s_bankCommandMax = ConfigurationManager.GetValue("Character.BankCommandMax", 1000);

    [Command("backpack")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetBackpackCommand(string amount) {
        if (!TryReadSpace(amount, s_backpackCommandMax, out var limit)) {
            InformSenderClient($"Use .backpack and a number of items (1 to {s_backpackCommandMax}), or .backpack max.");

            return;
        }

        var inventory = Context.Character.InventoryBehavior;
        if (limit < inventory.Items.Count) {
            InformSenderClient($"Your backpack holds {inventory.Items.Count} items, so it cannot be made smaller than that.");

            return;
        }

        var previous = inventory.ItemLimit;
        inventory.ItemLimit = limit;
        if (!WizardCollection.UpdateCharacterItems(Context.Character)) {
            inventory.ItemLimit = previous;
            InformSenderClient("Could not save the backpack size.");

            return;
        }

        InformSenderClient($"Your backpack now holds {limit} items. Change area or log out and back in to see it in the backpack window.");
    }

    [Command("bank")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetBankCommand(string amount) {
        if (!TryReadSpace(amount, s_bankCommandMax, out var limit)) {
            InformSenderClient($"Use .bank and a number of items (1 to {s_bankCommandMax}), or .bank max.");

            return;
        }

        if (limit < Context.Character.StorageBehavior.Items.Count) {
            InformSenderClient($"Your bank holds {Context.Character.StorageBehavior.Items.Count} items, so it cannot be made smaller than that.");

            return;
        }

        if (!Context.Character.SetBankLimit(limit)) {
            InformSenderClient("Could not save the bank size.");

            return;
        }

        Context.SessionActor.Tell(BankSync.UpdateBankLimit(Context.Character), null);
        InformSenderClient($"Your bank now holds {limit} items.");
    }

    private static bool TryReadSpace(string amount, int max, out int limit) {
        if (string.Equals(amount?.Trim(), "max", System.StringComparison.OrdinalIgnoreCase)) {
            limit = max;

            return true;
        }

        return int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= max;
    }

}
