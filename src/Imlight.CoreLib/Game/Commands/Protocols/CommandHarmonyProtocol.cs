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
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.harmony on` / `.harmony off`: Harmony mode, every battle starts with full power and shadow pips.
/// </summary>
internal class CommandHarmonyProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("harmony")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HarmonyCommand(string state) {
        bool on;
        switch (state.Trim().ToLowerInvariant()) {
            case "on":
                on = true;
                break;
            case "off":
                on = false;
                break;
            default:
                InformSenderClient("Use .harmony on or .harmony off.");

                return;
        }

        HarmonyMode.Set(Context.Character.CharId, on);
        InformSenderClient(on
            ? "Harmony mode is on: your battles start with 7 power pips and full shadow pips. It starts with your next battle."
            : "Harmony mode is off: your battles start with normal pips again.");
    }

}
