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
 * PET ELIXIRS
 * ========================================================================
 *
 * PURPOSE:
 * Tells the pet elixirs apart from the other elixirs (ObjectData/Elixirs). No adjective marks them, so they are
 * known by what they do: the Energy Elixirs give pet training energy (ResAddEnergy), the Hatching Elixir and
 * Purreau's Potion #9 may only be bought with eggs waiting or a hatching slot in use (ReqHatchAllEggs,
 * ReqClearHatchingSlot), the Double Pet XP benefits name BenefitDoublePetXP, and the Next Hatch Free Elixir
 * (Elixir-042) is the one known by its name.
 *
 * USAGE EXAMPLE:
 * if (PetElixirs.IsPetElixir(template)) { ... }
 *
 * NOTE:
 * Group elixirs (GroupElixir) and test elixirs are left out.
 *
 * TODO:
 * - Purreau's Potion #9 and the Next Hatch Free Elixir have nothing to do: hatching has no slot timer and no cost.
 * - The Double Pet XP elixirs add nothing to the member pet XP bonus everyone already has (PetRules).
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// The pet elixirs: the elixirs that act on pets, pet energy or hatching.
/// </summary>
internal static class PetElixirs {

    private const string NEXT_HATCH_FREE = "Elixir-042";
    private const string DOUBLE_PET_XP = "BenefitDoublePetXP";

    /// <summary>
    /// Whether an item is a pet elixir.
    /// </summary>
    internal static bool IsPetElixir(WizItemTemplate template) {
        if (template?.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault() is not { } elixir) {
            return false;
        }

        var name = (string) template.m_objectName ?? "";
        if (name.Contains("TEST", StringComparison.OrdinalIgnoreCase)
            || (template.m_adjectiveList ?? []).Any(adjective => (string) adjective == "GroupElixir")) {
            return false;
        }

        return name == NEXT_HATCH_FREE
            || (elixir.m_equipActionList?.m_results ?? []).Any(result => result is ResAddEnergy)
            || (elixir.m_typeList ?? []).Any(type => (string) type == DOUBLE_PET_XP)
            || Requirements(template.m_purchaseRequirements).Concat(Requirements(template.m_equipRequirements))
                .Any(requirement => requirement is ReqHatchAllEggs or ReqClearHatchingSlot);
    }

    /// <summary>
    /// Whether an elixir hatches every egg in the hatching slots (the Hatching Elixir, "Hatch all eggs in inventory").
    /// </summary>
    internal static bool HatchesAllEggs(WizItemTemplate template)
        => Requirements(template?.m_purchaseRequirements).Concat(Requirements(template?.m_equipRequirements))
            .Any(requirement => requirement is ReqHatchAllEggs);

    private static IEnumerable<Requirement> Requirements(RequirementList list) {
        foreach (var requirement in list?.m_requirements ?? []) {
            if (requirement is RequirementList inner) {
                foreach (var nested in Requirements(inner)) {
                    yield return nested;
                }
            }
            else if (requirement is not null) {
                yield return requirement;
            }
        }
    }

}
