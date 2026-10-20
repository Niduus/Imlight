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
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.ca Prepare yourself!` (Justin, 2026-10-20): in battle, the text shows like a boss's cheat line, in the
/// "Gained Power Pip" spot, when the next round's spells play.
/// </summary>
internal class CommandAnnounceProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("ca")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AnnounceCommand([Remainder] string text) {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            InformSenderClient("Use .ca and the words to show, such as .ca Prepare yourself!");

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE { Text = text });
    }

}
