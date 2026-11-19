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

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// <c>.levellock on</c>, <c>.levellock off</c> or <c>.levellock</c>: the game's own Level Lock, for anyone
/// (Niduus, 2026-11-19). While locked the level and XP stay as they were and XP earned is saved up; unlocking gives it
/// all (WizardService).
/// </summary>
internal class CommandLevelLockProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("levellock")]
    [Alias("locklevel", "xplock", "lockxp")]
    private void LevelLockCommand(string choice = "") {
        bool? lockIt;
        switch ((choice ?? "").Trim().ToLowerInvariant()) {
            case "" or "status":
                lockIt = null;
                break;
            case "on" or "lock" or "yes" or "1":
                lockIt = true;
                break;
            case "off" or "unlock" or "no" or "0":
                lockIt = false;
                break;
            default:
                InformSenderClient("Type .levellock on to lock your level, .levellock off to unlock it, or .levellock to see how it stands.");

                return;
        }

        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_SETLEVELLOCK { Lock = lockIt }, ActorRefs.NoSender);
    }

}
