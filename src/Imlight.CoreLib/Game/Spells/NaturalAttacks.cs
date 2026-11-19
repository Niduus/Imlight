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
 *
 * ========================================================================
 * NATURAL ATTACKS
 * ========================================================================
 *
 * PURPOSE:
 * Tells a creature's natural attack (the swing it makes with no card) from its other spells. The game names those
 * cards "NA <creature>" (a few "<creature> NA"), and the card the players see is called "Natural Attack". Both count.
 * Used by the creature AIs for the natural attack cooldown ([Combat] NaturalAttackCooldown, Niduus 2026-11-19:
 * "Have Enemies gain a 1 round cool down on Natural Attacks, this goes for SPELLS NAMED 'Natural Attack'").
 *
 * USAGE EXAMPLE:
 * if (NaturalAttacks.Is(spell)) { ... }
 *
 * Last Updated: 11/19/2026
 */

using System;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Spells;

internal static class NaturalAttacks {

    internal const string SHOWN_NAME = "Natural Attack";

    /// <summary>True when the card is a natural attack.</summary>
    internal static bool Is(Spell spell)
        => spell is not null && CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate template && Is(template);

    /// <summary>True when the spell is a natural attack: named "NA ..." / "... NA", or shown as "Natural Attack".</summary>
    internal static bool Is(SpellTemplate template) {
        if (template is null) {
            return false;
        }

        var name = ((string) template.m_name ?? "").Trim();
        if (name.StartsWith("NA ", StringComparison.Ordinal) || name.EndsWith(" NA", StringComparison.Ordinal)
            || name.Equals(SHOWN_NAME, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        string key = template.m_displayName;
        if (string.IsNullOrEmpty(key)) {
            return false;
        }

        try {
            return (Locale.GetEnglishName(key) ?? "").Trim().Equals(SHOWN_NAME, StringComparison.OrdinalIgnoreCase);
        }
        catch {
            return false;
        }
    }

}
