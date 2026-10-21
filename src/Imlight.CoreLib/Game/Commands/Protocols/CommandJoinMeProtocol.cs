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
using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.joinme Gobbler` (Justin, 2026-10-20): in battle, a creature joins on the enemy side. A number at the end adds
/// that many, while the enemy side has room.
/// Some bosses share one name across worlds; <see cref="s_versions"/> picks the one Justin means (2026-10-21:
/// `.joinme Malistaire the Undying` is the Castle Darkmoor boss, `.joinme Malistaire the Undying Azteca` the Azteca one).
/// </summary>
internal class CommandJoinMeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Name (with an optional world word) to the template ID of that version.
    private static readonly Dictionary<string, ulong> s_versions = new(StringComparer.OrdinalIgnoreCase) {
        ["Malistaire the Undying"] = 1336035,                 // G14-DM-Malistaire_Boss, Castle Darkmoor
        ["Malistaire the Undying Darkmoor"] = 1336035,
        ["Malistaire the Undying Castle Darkmoor"] = 1336035,
        ["Malistaire the Undying Azteca"] = 684531,           // AZ-Human-LichMalistaire-A
    };

    [Command("joinme")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void JoinMeCommand([Remainder] string creature) {
        if (!IdCatalog.IsReady) {
            InformSenderClient("The ID lookup is still being built, or is turned off in the configuration. Try again shortly.");

            return;
        }

        var name = (creature ?? "").Trim();
        var match = Find(name);
        var times = 1;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (match is null && words.Length > 1 && int.TryParse(words[^1], out var count) && count > 0) {
            match = Find(string.Join(' ', words[..^1]));
            times = Math.Min(count, 4);
        }

        if (match is null) {
            InformSenderClient(name.Length == 0
                ? "Use .joinme and a creature's name or ID, such as .joinme Gobbler."
                : $"No creature called \"{name}\" was found. Try .lookup mob {name} to see the names.");

            return;
        }

        for (var i = 0; i < times; i++) {
            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATJOINME {
                CreatureTemplateId = (uint) match.Id,
                CreatureName = match.Name,
            });
        }
    }

    private static IdCatalogEntry Find(string name) {
        if (name.Length == 0) {
            return null;
        }

        var query = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s_versions.TryGetValue(query, out var id)) {
            query = id.ToString();
        }

        return IdCatalog.Search(IdCatalogKind.Creature, query, 1).FirstOrDefault();
    }

}
