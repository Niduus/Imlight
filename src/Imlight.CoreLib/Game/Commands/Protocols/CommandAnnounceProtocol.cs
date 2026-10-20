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
using System.Text;
using Akka.Actor;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.ca gained power pip` (Justin, 2026-10-20): in battle, the game's own line with those words shows like a boss's
/// cheat line, in the "Gained Power Pip" spot, before the next round's first spell.
/// </summary>
internal class CommandAnnounceProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("ca")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AnnounceCommand([Remainder] string text) {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            InformSenderClient("Use .ca and words from one of the game's own lines, such as .ca gained power pip. .ca find <words> lists the lines that match.");

            return;
        }

        // The game shows only its own lines, by key (Justin, 2026-10-20: typed text showed nothing). A number is the
        // phrase's number in the app's Lookup tab.
        if (int.TryParse(text, out var number)) {
            var phrases = Locale.Phrases;
            if (number < 1 || number > phrases.Count) {
                InformSenderClient($"There are {phrases.Count} phrases. Find their numbers in the app's Lookup tab under Phrase.");

                return;
            }

            var (phraseKey, phrase) = phrases[number - 1];
            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE { Text = phraseKey, Shown = phrase });

            return;
        }

        var find = text.StartsWith("find ", StringComparison.OrdinalIgnoreCase);
        var query = find ? text[5..].Trim() : text;
        var matches = Locale.Search(query, find ? MAX_LISTED : 1);
        if (matches.Count == 0) {
            InformSenderClient($"The game has no line with \"{query}\". Try fewer words.");

            return;
        }

        if (find) {
            var list = new StringBuilder().AppendLine($"<center>Lines with \"{query}\":</center>\n");
            var numbers = Locale.Phrases.Select((p, i) => (p.Key, Number: i + 1)).ToDictionary(p => p.Key, p => p.Number);
            foreach (var (lineKey, line) in matches) {
                list.AppendLine(numbers.TryGetValue(lineKey, out var n) ? $"{n}  {line}" : line);
            }

            InformSenderClient(list.ToString(), true);

            return;
        }

        var (key, shown) = matches[0];
        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE { Text = key, Shown = shown });
    }

    private const int MAX_LISTED = 10;

}
