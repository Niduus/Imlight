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
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.cast none`, `.cast reset` (Niduus, 2026-10-20): see <see cref="CastLook"/>.
/// </summary>
internal class CommandCastLookProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("cast")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CastCommand([Remainder] string school) {
        var id = Context.CharacterObject.m_globalID.Full;
        var name = (school ?? "").Trim().ToLowerInvariant();
        if (name == "reset") {
            CastLook.Reset(id);
            InformSenderClient("Your spells play their own casting animation again.");

            return;
        }

        // Another school's animation can't be shown: the game takes the casting sign from its own copy of the spell.
        if (name != "none") {
            InformSenderClient("Use .cast none to skip your casting animation, or .cast reset to undo. (The game always plays the spell's own school's animation, so other schools can't be picked.)");

            return;
        }

        CastLook.Set(id, MagicSchool.None);
        InformSenderClient("Your spells skip their casting animation. Use .cast reset to undo.");
    }

}
