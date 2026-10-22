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
/// `.wegofirst` and `.theygofirst` (Niduus, 2026-10-22): in battle, the sender's side (or the other side) acts first,
/// from this round's spells on. The Turn order cheat block does the same (CombatResolver.SetFirstSide).
/// </summary>
internal class CommandTurnOrderProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("wegofirst")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void WeGoFirstCommand()
        => Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATTURNORDER { WeFirst = true });

    [Command("theygofirst")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TheyGoFirstCommand()
        => Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATTURNORDER { WeFirst = false });

}
