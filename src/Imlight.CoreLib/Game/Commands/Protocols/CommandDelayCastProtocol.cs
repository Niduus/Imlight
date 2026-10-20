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
/// `.c1 Fire Cat` and `.c2 Fire Cat` (were .daybreaker/.db and .nightbringer/.nb; Justin renamed them 2026-10-20): in
/// battle, casts a spell for free this round, before (c1) or after (c2) your own card. Type it several times to chain spells.
/// </summary>
internal class CommandDelayCastProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("c1")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DaybreakerCommand([Remainder] string spell) => QueueAll(spell, atStart: true);

    [Command("c2")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void NightbringerCommand([Remainder] string spell) => QueueAll(spell, atStart: false);

    /// <summary>
    /// Several spells in one line, split by commas; each part can start with c1 or c2 to pick its queue, else it
    /// goes in the typed command's queue (Justin, 2026-10-20: `.c1 Tower Shields, c2 Pyrodynamics`).
    /// </summary>
    private void QueueAll(string spells, bool atStart) {
        foreach (var part in (spells ?? "").Split(',', StringSplitOptions.TrimEntries)) {
            var words = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].TrimStart('.').ToLowerInvariant() : "";
            if (first is "c1" or "c2") {
                atStart = first == "c1";
                Queue(words.Length > 1 ? words[1] : "", atStart);
            } else {
                Queue(part, atStart);
            }
        }
    }

    private void Queue(string spell, bool atStart) {
        var command = atStart ? ".c1" : ".c2";
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
