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

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The pet talents players look for, best first (Niduus, 2026-11-19), for any bot's pet: its first five are the pet's
/// talents and the next one it lacks is its jewel's.
/// <list type="bullet">
/// <item>Damage: Dealer (school, flat 10), Pain-Giver (6 every school), Giver (6 school), Pain-Bringer (3 every school),
/// Boon (3 school).</item>
/// <item>Resist: Spell-Proof (10 every school), Spell-Defying (5 every school); the school wards are worse ("Uni, the
/// bridge ends here").</item>
/// <item>Pierce: Armor Breaker (3), Armor Piercer (2), every school only.</item>
/// <item>Accuracy: Sniper (9 school), Eye and Shot (less); pip talents; may-casts are nice to have.</item>
/// </list>
/// The names are the game's (its pet jewels carry the same names). The pet's stats are 260 each (PET_STAT).
/// </summary>
internal static class BotPetTalents {

    /// <summary>Every pet stat (Strength, Intellect, Agility, Will, Power) at this (Niduus: "260 across the board").</summary>
    internal const int PET_STAT = 260;

    internal static readonly string[] Stats = ["Strength", "Intellect", "Agility", "Will", "Power"];

    /// <summary>The talents for a school's hitter, best first.</summary>
    internal static string[] For(string school) => [
        $"{school}-Dealer", "Pain-Giver", "Spell-Proof", "Spell-Defying", $"{school}-Giver",
        "Armor Breaker", "Pain-Bringer", $"{school}-Sniper", "Armor Piercer", $"{school}-Boon",
        $"{school}-Eye", $"{school}-Shot", "Pip Boost", "Unicorn", "Fairy",
    ];

}
