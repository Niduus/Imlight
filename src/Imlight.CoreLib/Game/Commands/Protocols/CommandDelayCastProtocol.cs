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
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.daybreaker Fire Cat` (`.db`) and `.nightbringer Fire Cat` (`.nb`): in battle, casts a spell for free this round,
/// at the start or the end of your turn, as well as your card. Type it several times to chain spells.
/// </summary>
internal class CommandDelayCastProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("daybreaker")]
    [Alias("db")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DaybreakerCommand([Remainder] string spell) => Queue(spell, atStart: true);

    [Command("nightbringer")]
    [Alias("nb")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void NightbringerCommand([Remainder] string spell) => Queue(spell, atStart: false);

    private void Queue(string spell, bool atStart) {
        var command = atStart ? ".daybreaker" : ".nightbringer";
        var name = (spell ?? "").Trim();

        // An optional target first: p1 (you) to p4 (teammates), e1 to e4 (enemies).
        string target = null;
        var words = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 2 && words[0].Length == 2 && words[0].ToLowerInvariant() is var code
            && code[0] is 'p' or 'e' && code[1] is >= '1' and <= '4') {
            target = code;
            name = words[1].Trim();
        }

        if (name.Length == 0) {
            InformSenderClient($"Use {command}, an optional target (p1 is you, p2-p4 teammates, e1-e4 enemies) and a spell's name or ID, such as {command} e2 Fire Cat.");

            return;
        }

        if (!CommandDrawProtocol.TryFindSpell(name, out var templateId, out var spellName)) {
            InformSenderClient($"No spell called \"{name}\" was found. Try .lookup spell {name} to see the names.");

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST {
            SpellTemplateId = templateId,
            SpellName = spellName,
            AtStart = atStart,
            Target = target,
        });
    }

}
