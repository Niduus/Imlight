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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.c1 Fire Cat` and `.c2 Fire Cat` (were .daybreaker/.db and .nightbringer/.nb; Justin renamed them 2026-10-20): in
/// battle, casts a spell for free this round, before (c1) or after (c2) your own card, or with f/l before anyone's or
/// after everyone's turn. Type it several times to chain spells.
/// </summary>
internal class CommandDelayCastProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("c1")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DaybreakerCommand([Remainder] string spell) => QueueAll(spell, atStart: true, skip: false);

    [Command("c1s")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void FirstSkipCommand([Remainder] string spell) => QueueAll(spell, atStart: true, skip: true);

    [Command("c2s")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AfterSkipCommand([Remainder] string spell) => QueueAll(spell, atStart: false, skip: true);

    [Command("c2")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void NightbringerCommand([Remainder] string spell) => QueueAll(spell, atStart: false, skip: false);

    /// <summary>
    /// Several spells in one line, split by commas; each part can start with c1 or c2 to pick its queue, else it
    /// goes in the typed command's queue (Justin, 2026-10-20: `.c1 Tower Shields, c2 Pyrodynamics`).
    /// </summary>
    private void QueueAll(string spells, bool atStart, bool skip) {
        foreach (var part in (spells ?? "").Split(',', StringSplitOptions.TrimEntries)) {
            var words = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].TrimStart('.').ToLowerInvariant() : "";
            if (first is "c1" or "c2" or "c1s" or "c2s") {
                Queue(words.Length > 1 ? words[1] : "", first.StartsWith("c1"), first.EndsWith('s'));
            } else {
                Queue(part, atStart, skip);
            }
        }
    }

    /// <summary>
    /// `.cX [s/f/l] [target] spell` (Justin, 2026-10-20): s skips the casting animation, f casts before anyone's
    /// turn this round, l after everyone's; they mix, like sf. The target is p1-p4, e1-e4 or all (a single-target
    /// spell then casts once on each enemy, or each teammate).
    /// </summary>
    private void Queue(string spell, bool atStart, bool skip) {
        var words = (spell ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var when = 'n';

        if (words.Count > 1 && words[0].ToLowerInvariant() is var options && options.Length <= 3
            && options.All(c => c is 's' or 'f' or 'l') && !(options.Contains('f') && options.Contains('l'))) {
            skip |= options.Contains('s');
            when = options.Contains('f') ? 'f' : options.Contains('l') ? 'l' : 'n';
            words.RemoveAt(0);
        }

        string target = null;
        if (words.Count > 1 && words[0].ToLowerInvariant() is var code
            && (code == "all" || code.Length == 2 && code[0] is 'p' or 'e' && code[1] is >= '1' and <= '4')) {
            target = code;
            words.RemoveAt(0);
        }

        var command = (atStart ? ".c1" : ".c2");
        var name = string.Join(' ', words);
        if (name.Length == 0) {
            InformSenderClient($"Use {command}, optional s (no casting animation), f (first, before anyone) or l (last, after everyone), an optional target (p1 is you, p2-p4 teammates, e1-e4 enemies, all for everyone) and a spell's name or ID, such as {command} sf all Fire Cat.");

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
            SkipCast = skip,
            When = when,
        });
    }

}
