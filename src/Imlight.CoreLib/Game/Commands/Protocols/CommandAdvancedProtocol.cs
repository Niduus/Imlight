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
using Imlight.CoreLib.Game.World;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.advanced on` makes every dungeon sigil with an Advanced Mode copy (Game/World/AdvancedMode) take you to the harder
/// copy, and its circle glows red; `.advanced off` goes back to normal; `.advanced list` names the dungeons that have
/// one. The sigil's own Advanced Mode button works either way. Every player can use it.
/// </summary>
internal class CommandAdvancedProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("advanced")]
    [Alias("advancedmode", "challenge")]
    private void AdvancedCommand([Remainder] string argument = "") {
        if (!AdvancedMode.Enabled) {
            InformSenderClient("Advanced Mode is off on this server (Ahziael, Battle settings).");

            return;
        }

        var charId = Context.Character.CharId;
        switch ((argument ?? "").Trim().ToLowerInvariant()) {
            case "on" or "yes" or "1":
                AdvancedMode.SetWanted(charId, true);
                InformSenderClient("Advanced Mode on: dungeon sigils with an Advanced copy take you there (their circle glows red). Type .advanced off to stop.");
                break;
            case "off" or "no" or "0":
                AdvancedMode.SetWanted(charId, false);
                InformSenderClient("Advanced Mode off: sigils take you to the normal dungeon.");
                break;
            case "list" or "dungeons":
                var names = AdvancedMode.Zones.Select(z => z.Name).Distinct().OrderBy(n => n);
                InformSenderClient($"Dungeons with an Advanced Mode copy ({AdvancedMode.Zones.Count}): {string.Join(", ", names)}.");
                break;
            default:
                InformSenderClient($"Advanced Mode is {(AdvancedMode.Wants(charId) ? "on" : "off")} for you. "
                    + "Type .advanced on, .advanced off, or .advanced list for the dungeons that have it.");
                break;
        }
    }

}
