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

using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Shadow pip commands: `.sp slots 2`.
/// </summary>
internal class CommandShadowPipProtocol : CommandProtocol {

    private const int MAX_SLOTS = 8;

    internal override string Group { get; set; } = "sp";

    [Command("slots")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SetShadowPipSlotsCommand(string number) {
        if (!int.TryParse(number, out var slots) || slots < 1 || slots > MAX_SLOTS) {
            InformSenderClient($"Type a number from 1 to {MAX_SLOTS}, such as .sp slots 2.");

            return;
        }

        // Also unlocks shadow magic, so the slots show up even with the Shadow pips switch off.
        Context.Character.UnlockShadowMagic(slots);

        InformSenderClient($"You now have {slots} shadow pip slot{(slots == 1 ? "" : "s")}. Relog to see it.");
    }

}
