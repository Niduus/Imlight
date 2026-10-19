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

using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Text;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandLookupProtocol : CommandProtocol {

    private const int MAX_RESULTS = 10; // More makes the window taller than the screen, hiding its OK button.

    internal override string Group { get; set; } = "lookup";

    [Command("item")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LookupItemCommand([Remainder] string query)
        => InformResults(IdCatalogKind.Item, query);

    [Command("spell")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LookupSpellCommand([Remainder] string query)
        => InformResults(IdCatalogKind.Spell, query);

    [Command("mob")]
    [Alias("creature")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LookupCreatureCommand([Remainder] string query)
        => InformResults(IdCatalogKind.Creature, query);

    [Command("npc")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LookupNpcCommand([Remainder] string query)
        => InformResults(IdCatalogKind.Npc, query);

    [Command("all")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LookupAllCommand([Remainder] string query)
        => InformResults(null, query);

    private void InformResults(IdCatalogKind? kind, string query) {
        if (!IdCatalog.IsReady) {
            InformSenderClient("The ID lookup is still being built, or is turned off in the configuration. Try again shortly.");

            return;
        }

        var matches = IdCatalog.Search(kind, query, MAX_RESULTS + 1);
        if (matches.Count == 0) {
            InformSenderClient($"Nothing found for \"{query}\".");

            return;
        }

        var message = new StringBuilder()
            .AppendLine($"<center>Results for \"{query}\":</center>\n");
        for (var i = 0; i < matches.Count && i < MAX_RESULTS; i++) {
            var match = matches[i];
            var details = match.Kind switch {
                IdCatalogKind.Creature when match.Level > 0 => $" (level {match.Level} {match.School})",
                _ when match.School != "" => $" ({match.School})",
                _ => ""
            };

            message.AppendLine($"{match.Id}  {match.Kind}  {match.Name}{details}");
        }

        if (matches.Count > MAX_RESULTS) {
            message.AppendLine("\nMore found. Add words to narrow it, or use the app's Lookup tab.");
        }

        InformSenderClient(message.ToString(), true);
    }

}
