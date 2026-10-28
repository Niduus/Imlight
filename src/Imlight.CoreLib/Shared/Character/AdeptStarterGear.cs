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

using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Shared.Character;

/// <summary>
/// New wizards start wearing their school's Adept set (the Crown Shop set: hat, robe, shoes, wand, dirk, ring, ampul
/// and deck; "L30" in the file names, but the only thing needed to wear it is the school) and ride their school's
/// Ghulture (PERM) (Niduus, 2026-10-28). [Character] StartInAdeptGear turns it off; it is read when the starter kit is
/// handed out, so a change applies to the next new wizard.
/// </summary>
internal static class AdeptStarterGear {

    internal static bool Enabled => ConfigurationManager.GetValue("Character.StartInAdeptGear", true);

    // Template ids from the game files (Items and objects.csv). Each piece runs Balance, Death, Fire, Ice, Life,
    // Myth, Storm from its first id.
    private const ulong HAT = 1712550;
    private const ulong ROBE = 1712557;
    private const ulong SHOES = 1712564;
    private const ulong WAND = 1712571;
    private const ulong DIRK = 1712578;
    private const ulong RING = 1712585;
    private const ulong AMPUL = 1712592;
    private const ulong DECK = 1712599;
    private const ulong GHULTURE = 1392135;

    /// <summary>
    /// The Adept set and Ghulture for the wizard's school, or nothing when the setting is off. Schools without an
    /// Adept set of their own (astral, shadow) get the Balance set.
    /// </summary>
    internal static ulong[] TemplateIdsFor(MagicSchool school) {
        if (!Enabled) {
            return [];
        }

        ulong offset = school switch {
            MagicSchool.Death => 1,
            MagicSchool.Fire => 2,
            MagicSchool.Ice => 3,
            MagicSchool.Life => 4,
            MagicSchool.Myth => 5,
            MagicSchool.Storm => 6,
            _ => 0,
        };

        return [HAT + offset, ROBE + offset, SHOES + offset, WAND + offset, DIRK + offset, RING + offset,
                AMPUL + offset, DECK + offset, GHULTURE + offset];
    }

}
