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
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.draw 2 Fire Cat`: puts cards into your hand in battle, even ones that are not in your deck.
/// </summary>
internal class CommandDrawProtocol : CommandProtocol {

    private const int MAX_AMOUNT = 7;
    private const int SEARCH_LIMIT = 50;

    internal override string Group { get; set; } = "";

    [Command("draw")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DrawCommand([Remainder] string text) {
        // The amount is optional: ".draw Fire Cat" draws one.
        var words = (text ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var amount = 1;
        var name = string.Join(' ', words);
        if (words.Length == 2 && int.TryParse(words[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) {
            amount = number;
            name = words[1];
        }

        if (amount < 1 || amount > MAX_AMOUNT || string.IsNullOrWhiteSpace(name)) {
            InformSenderClient($"Use .draw, how many (1 to {MAX_AMOUNT}) and the card's name, such as .draw 2 Fire Cat.");

            return;
        }

        if (!TryFindSpell(name.Trim(), out var templateId, out var spellName)) {
            InformSenderClient($"No card called \"{name.Trim()}\" was found. Try .lookup spell {name.Trim()} to see the names.");

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATDRAWCARDS {
            SpellTemplateId = templateId,
            SpellName = spellName,
            Amount = amount
        });
    }

    internal static bool TryFindSpell(string name, out uint templateId, out string spellName) {
        templateId = 0;
        spellName = name;

        // A spell ID works too.
        if (uint.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
            if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate byId) {
                templateId = id;
                spellName = EnglishName(byId);

                return true;
            }

            return false;
        }

        // Exact names first, and the normal card before a treasure card of the same name.
        var candidates = SpellFactory.AllSpellTemplates()
            .Where(t => t.Template is not null)
            .Select(t => (t.Path, t.Template, Name: EnglishName(t.Template)))
            .Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => !t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Template.m_Treasure)
            .ThenBy(t => t.Name.Length)
            .Take(SEARCH_LIMIT);

        foreach (var (path, _, english) in candidates) {
            if (CoreObjectFactory.TryGetCoreTemplateID(x => x.m_filename == path, out uint found) && found != 0) {
                templateId = found;
                spellName = english;

                return true;
            }
        }

        return false;
    }

    private static string EnglishName(SpellTemplate template) {
        string displayKey = template.m_displayName;
        string internalName = template.m_name;
        var english = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

        return string.IsNullOrEmpty(english) ? internalName ?? "" : english;
    }

}
