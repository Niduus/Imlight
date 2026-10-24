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
/// `.ca gained power pip` (Niduus, 2026-10-20): in battle, the game's own line with those words shows like a boss's
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

        var find = text.StartsWith("find ", StringComparison.OrdinalIgnoreCase);
        if (find) {
            var query = text[5..].Trim();
            var matches = Locale.Search(query, MAX_LISTED);
            if (matches.Count == 0) {
                InformSenderClient($"The game has no line with \"{query}\". Try fewer words.");

                return;
            }

            var list = new StringBuilder().AppendLine($"<center>Lines with \"{query}\":</center>\n");
            var numbers = Locale.Phrases.Select((p, i) => (p.Key, Number: i + 1)).ToDictionary(p => p.Key, p => p.Number);
            foreach (var (lineKey, line) in matches) {
                list.AppendLine(numbers.TryGetValue(lineKey, out var n) ? $"{n}  {line}" : line);
            }

            InformSenderClient(list.ToString(), true);

            return;
        }

        // Several lines split by commas show one after another (Niduus, 2026-10-20: `.ca 1, 5`).
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            if (!TryResolve(part, out var key, out var shown, out var problem)) {
                InformSenderClient(problem);

                continue;
            }

            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE { Text = key, Shown = shown });
        }
    }

    /// <summary>
    /// The game line a .ca phrase means: a number is the phrase's number in the app's Lookup tab, words are the
    /// line that matches them best. The game shows only its own lines, by key (Niduus, 2026-10-20: typed text
    /// showed nothing).
    /// </summary>
    /// <param name="text">The number or words.</param>
    /// <param name="key">The line's locale key, which the game shows.</param>
    /// <param name="shown">The line's English text.</param>
    /// <param name="problem">Why nothing was found, in plain words.</param>
    internal static bool TryResolve(string text, out string key, out string shown, out string problem) {
        key = shown = problem = null;
        text = (text ?? "").Trim();
        if (int.TryParse(text, out var number)) {
            var phrases = Locale.Phrases;
            if (number < 1 || number > phrases.Count) {
                problem = $"There are {phrases.Count} phrases. Find their numbers in the app's Lookup tab under Phrase.";

                return false;
            }

            (key, shown) = phrases[number - 1];

            return true;
        }

        // A locale key itself (CombatMessages_00000676, as the boss cheat files and Wizard404 give the game's lines):
        // the words of a line never hold its key, so the search below did not find them (Niduus, 2026-10-24).
        if (text.Contains('_') && !text.Contains(' ') && Locale.GetEnglishName(text) is { Length: > 0 } english) {
            (key, shown) = (text, english);

            return true;
        }

        var matches = Locale.Search(text, 1);
        if (matches.Count == 0) {
            problem = $"The game has no line with \"{text}\". Try fewer words.";

            return false;
        }

        (key, shown) = matches[0];

        return true;
    }

    private const int MAX_LISTED = 10;

}
