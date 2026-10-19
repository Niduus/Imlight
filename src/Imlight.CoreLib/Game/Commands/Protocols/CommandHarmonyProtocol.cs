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
/// `.lightnshadow on` / `.lightnshadow off`: every round refills power pips, shadow pips and mana.
/// </summary>
internal class CommandHarmonyProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("harmony")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HarmonyCommand(string state) {
        if (!TryReadState(state, ".harmony", out var on)) {
            return;
        }

        BattleModes.Harmony.Set(Context.Character.CharId, on);
        InformSenderClient(on
            ? "Harmony mode is on: your battles start with 7 power pips and full shadow pips. It starts with your next battle."
            : "Harmony mode is off: your battles start with normal pips again.");
    }

    [Command("lightnshadow")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LightAndShadowCommand(string state) {
        if (!TryReadState(state, ".lightnshadow", out var on)) {
            return;
        }

        BattleModes.LightAndShadow.Set(Context.Character.CharId, on);
        InformSenderClient(on
            ? "Light and Shadow is on: each round your power pips, shadow pips and mana fill back up. School pips stay as they are."
            : "Light and Shadow is off: pips and mana come back normally again.");
    }

    private bool TryReadState(string state, string command, out bool on) {
        on = state.Trim().ToLowerInvariant() == "on";
        if (on || state.Trim().ToLowerInvariant() == "off") {
            return true;
        }

        InformSenderClient($"Use {command} on or {command} off.");

        return false;
    }

}
