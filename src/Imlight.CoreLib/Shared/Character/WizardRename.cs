/*
 * Imlight
 * Copyright (C) 2026 Revive101
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

using System.Globalization;
using System.Linq;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Character;

/// <summary>
/// The game's own rename window (the plan's "real rename window", 2026-10-24): `.rename` marks the wizard, character
/// select then sends it with m_shouldRename so the game asks for a new name, and the game answers with
/// MSG_CHANGECHARACTERNAME. What the game puts in NewName is not known yet, so every likely form is read.
/// </summary>
internal static class WizardRename {

    // The packed custom name form holds 31 bytes (ServerWizPlayerNameBehavior.TryGetOverrideNameBytes).
    private const int MaxAsciiLength = 31;
    private const int MaxWideLength = 15;

    /// <summary>
    /// Reads a new wizard name: packed name keys as a number, a name from the game's name lists ("Walter Blanco"),
    /// or else a custom name.
    /// </summary>
    /// <param name="wizard">The wizard being renamed (its gender picks the first name list).</param>
    /// <param name="text">What the game or the player sent.</param>
    /// <param name="nameKeys">The new name keys (the old ones for a custom name).</param>
    /// <param name="customName">The custom name, or empty for a name from the lists.</param>
    /// <returns>False if the name is empty or too long.</returns>
    internal static bool TryReadName(Wizard wizard, string text, out uint nameKeys, out string customName) {
        var names = wizard.PlayerNameBehavior;
        nameKeys = names.NameIndices;
        customName = "";
        text = (text ?? "").Trim().Trim('\0');
        if (text.Length == 0) {
            return false;
        }

        // The name keys themselves, in decimal or hex. The top byte is not part of the name and is kept.
        var hex = text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase);
        var number = hex ? text[2..] : text;
        if (uint.TryParse(number, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var keys)
            && WizardNameBank.IsValidWizardName(keys, names.Gender)) {
            nameKeys = (names.NameIndices & 0xFF000000) | (keys & 0x00FFFFFF);

            return true;
        }

        if (WizardNameBank.TryFindNameKeys(text, names.Gender, out keys)) {
            nameKeys = (names.NameIndices & 0xFF000000) | keys;

            return true;
        }

        var ascii = text.All(c => c >= 0x20 && c < 0x7F);
        if (text.Length > (ascii ? MaxAsciiLength : MaxWideLength)) {
            return false;
        }

        customName = text;

        return true;
    }

    /// <summary>The longest custom name, for messages.</summary>
    internal static int MaxLength => MaxAsciiLength;

}
