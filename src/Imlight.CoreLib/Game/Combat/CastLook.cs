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
using Imlight.CoreLib.Shared.Behaviors;
using System;
using System.Collections.Concurrent;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// `.cast sun` (Niduus, 2026-10-20): a wizard's spells play another school's casting animation, or none with
/// `.cast none`, until `.cast reset` or the server restarts. Only what the game is shown changes; the spell itself
/// (pips, damage, school bonuses) stays as it is.
/// </summary>
internal static class CastLook {

    // Character global ID to the school ID to show, or 0 for no casting animation.
    private static readonly ConcurrentDictionary<ulong, uint> s_looks = new();

    internal static void Set(ulong characterId, MagicSchool school) => s_looks[characterId] = (uint) school;

    internal static void Reset(ulong characterId) => s_looks.TryRemove(characterId, out _);

    internal static bool TryGet(ulong characterId, out uint schoolId) => s_looks.TryGetValue(characterId, out schoolId);

    internal static bool TryParse(string name, out MagicSchool school) {
        school = MagicSchool.None;

        return !string.IsNullOrWhiteSpace(name) && Enum.TryParse(name.Trim(), true, out school)
            && Enum.IsDefined(school);
    }

}
