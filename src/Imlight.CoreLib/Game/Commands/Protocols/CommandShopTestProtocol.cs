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

using System.Linq;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.shoptest &lt;number&gt;`: every shop opens with that shop type. `.shoptest &lt;equipment|reagent|snack|jewel|housing|recipe|mount&gt;`:
/// every shop offers that kind of menu option. `.shoptest off` puts both back. For finding what makes the game
/// open its reagent, snack and jewel shop windows.
/// </summary>
internal class CommandShopTestProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("shoptest")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ShopTestCommand(string value) {
        var text = value.Trim().ToLowerInvariant();
        if (text == "off") {
            InteractVendorComponent.TestShopType = null;
            InteractVendorComponent.TestOption = null;
            InformSenderClient("Shop test is off. Walk away from the shop and back before opening it again.");

            return;
        }

        if (int.TryParse(text, out var type)) {
            InteractVendorComponent.TestShopType = type;
            InformSenderClient($"Every shop now opens with shop type {type}. Walk away from the shop and back, then open it.");

            return;
        }

        if (InteractVendorComponent.TestOptions.Contains(text)) {
            InteractVendorComponent.TestOption = text;
            InformSenderClient($"Every shop now opens as a {text} shop. Walk away from the shop and back, then open it.");

            return;
        }

        InformSenderClient("Use .shoptest and a number (0, 1, 2 ...), or one of: " + string.Join(", ", InteractVendorComponent.TestOptions) + ", or off.");
    }

}
