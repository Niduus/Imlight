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

using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.arrowlog` makes the game write every step of its quest arrow search (QHLP lines) to WizardClient.log, for
/// finding why the arrow does not show (Niduus, 2026-10-25). The server sends MSG_COMMAND "DebugQH", which the game
/// runs as its own command (WizardClientModules::CommandDebugQH). Nothing in the game files changes.
/// </summary>
internal class CommandArrowLogProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // DebugQH flips the game's switch each time, so the server remembers which way it is for each character. The game
    // starts with it off, so this is forgotten when the character is picked at character select.
    private static readonly ConcurrentDictionary<ulong, bool> s_arrowLogOn = new();

    /// <summary>The character was picked at character select: its game starts with the arrow log off.</summary>
    internal static void Forget(ulong charId) => s_arrowLogOn.TryRemove(charId, out _);

    [Command("arrowlog")]
    private void ArrowLogCommand() {
        var charId = Context.Character.CharId;
        var on = !(s_arrowLogOn.TryGetValue(charId, out var wasOn) && wasOn);
        s_arrowLogOn[charId] = on;

        Context.SessionActor.Tell(new GAME_5_PROTOCOL.MSG_COMMAND { Command = "DebugQH" }, ActorRefs.NoSender);
        InformSenderClient(on
            ? "Quest arrow log is ON. Follow a quest, walk around for a minute, then type .arrowlog again."
            : "Quest arrow log is OFF.");
    }

}
