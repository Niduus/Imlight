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
/// `.c1 Fire Cat` and `.c2 Fire Cat` (were .daybreaker/.db and .nightbringer/.nb; Niduus renamed them 2026-10-20): in
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
    /// goes in the typed command's queue (Niduus, 2026-10-20: `.c1 Tower Shields, c2 Pyrodynamics`).
    /// </summary>
    private void QueueAll(string spells, bool atStart, bool skip) {
        // A ca part (Niduus, 2026-10-20: `.c1 Fire Cat, ca 1, Fire Cat`) goes in the queue of the part before it,
        // and shows just before the spell after it.
        var lastAtStart = atStart;
        var lastWhen = 'n';
        foreach (var part in (spells ?? "").Split(',', StringSplitOptions.TrimEntries)) {
            var words = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].TrimStart('.').ToLowerInvariant() : "";
            if (first is "c1" or "c2" or "c1s" or "c2s") {
                lastAtStart = first.StartsWith("c1");
                lastWhen = Queue(words.Length > 1 ? words[1] : "", lastAtStart, first.EndsWith('s'));
            } else if (first == "ca") {
                QueueAnnouncement(words.Length > 1 ? words[1] : "", lastAtStart, lastWhen);
            } else {
                lastAtStart = atStart;
                lastWhen = Queue(part, atStart, skip);
            }
        }
    }

    private void QueueAnnouncement(string phrase, bool atStart, char when) {
        if (!CommandAnnounceProtocol.TryResolve(phrase, out var key, out var shown, out var problem)) {
            InformSenderClient(phrase.Trim().Length == 0 ? "Put a phrase number or words after ca, such as .c1 Fire Cat, ca 1." : problem);

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST {
            AtStart = atStart,
            When = when,
            Announcement = key,
            AnnouncementShown = shown,
        });
    }

    /// <summary>
    /// `.cX [s/f/l/e] [target] spell` (Niduus, 2026-10-20): s skips the casting animation, f casts before anyone's
    /// turn this round, l after everyone's, e adds the best enchantment that fits (Epic, Sharpened Blade...); they
    /// mix, like sfe. The target is p1-p4, e1-e4 or all (a single-target
    /// spell then casts once on each enemy, or each teammate). A number after the spell repeats it.
    /// </summary>
    /// <returns>The queue's f, l or n, for a ca part after it.</returns>
    private char Queue(string spell, bool atStart, bool skip) {
        var words = (spell ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var when = 'n';
        var enchant = false;

        if (words.Count > 1 && words[0].ToLowerInvariant() is var options && options.Length <= 4
            && options.Distinct().Count() == options.Length
            && options.All(c => c is 's' or 'f' or 'l' or 'e') && !(options.Contains('f') && options.Contains('l'))) {
            skip |= options.Contains('s');
            enchant = options.Contains('e');
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
            InformSenderClient($"Use {command}, optional s (no casting animation), e (best enchantment), f (first, before anyone) or l (last, after everyone), an optional target (p1 is you, p2-p4 teammates, e1-e4 enemies, all for everyone) a spell's name or ID and an optional number of times, such as {command} sf all Fire Cat 3.");

            return when;
        }

        // A number at the end repeats the spell (Niduus, 2026-10-20: `.c1 Fire Cat 3`), unless it is part of the
        // spell's name ("Detonate All 200").
        var repeat = 1;
        if (!CommandDrawProtocol.TryFindSpell(name, out var templateId, out var spellName)) {
            if (words.Count > 1 && int.TryParse(words[^1], out var times) && times > 0
                && CommandDrawProtocol.TryFindSpell(string.Join(' ', words[..^1]), out templateId, out spellName)) {
                repeat = Math.Min(times, MaxRepeat);
            }
            else {
                InformSenderClient($"No spell called \"{name}\" was found. Try .lookup spell {name} to see the names.");

                return when;
            }
        }

        for (var i = 0; i < repeat; i++) {
            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST {
                SpellTemplateId = templateId,
                SpellName = spellName,
                AtStart = atStart,
                Target = target,
                SkipCast = skip,
                When = when,
                Enchant = enchant,
            });
        }

        return when;
    }

    // The most times one spell repeats.
    private const int MaxRepeat = 20;

}
