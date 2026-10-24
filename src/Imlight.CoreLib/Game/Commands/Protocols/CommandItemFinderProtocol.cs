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

using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.itemfinder` opens the game's Item Finder window anywhere, the same as a placed Backpack Butler or a bank: it shows
/// where every item is (backpack, bank, shared bank, attic and houses). Anyone may use it.
/// </summary>
internal class CommandItemFinderProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("itemfinder")]
    [Alias("finditem", "finder")]
    private void ItemFinderCommand()
        => Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_BEGINITEMFINDER(), ActorRefs.NoSender);

}
