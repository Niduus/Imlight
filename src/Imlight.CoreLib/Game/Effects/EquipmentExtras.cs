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

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Effects;

/// <summary>
/// The stats a wizard gets from the worn pet's talents and from jewels in worn gear, on top of the gear itself.
/// </summary>
internal static class EquipmentExtras {

    /// <summary>
    /// Takes off the pet and jewel stats added before and adds them again for what is worn now.
    /// Call it after anything that changes the worn pet, a pet's stats or talents, or a socket.
    /// </summary>
    internal static void Refresh(Wizard wizard) {
        try {
            Remove(wizard);
            Apply(wizard);
        }
        catch (Exception e) {
            Logger.Error("Could not update pet and jewel stats for {0}: {1}", Logger.Args(wizard?.CharId, e));
        }
    }

    /// <summary>
    /// Forgets what was added without taking it off, for when the stats were just rebuilt from scratch.
    /// </summary>
    internal static void Forget(Wizard wizard) {
        wizard.AppliedPetEffects = [];
        wizard.AppliedJewelEffects = [];
    }

    private static void Remove(Wizard wizard) {
        foreach (var (name, effect) in wizard.AppliedPetEffects) {
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, name, effect);
        }

        foreach (var info in wizard.AppliedJewelEffects) {
            if (info is ProvideSpellEffectInfo spell) {
                for (var i = 0; i < Math.Max(1, spell.m_numSpells); i++) {
                    wizard.RemoveTemporarySpell(StringHash.Compute(spell.m_spellName));
                }

                continue;
            }

            CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, info);
        }

        Forget(wizard);
    }

    private static void Apply(Wizard wizard) {
        var equipped = wizard.EquipmentBehavior?.EquippedItems ?? [];
        foreach (var item in equipped.ToList()) {
            var template = ItemHelper.GetItemTemplate(item);
            if (template is null) {
                continue;
            }

            if (PetProgress.GetBehavior(item) is not null) {
                if (PetProgress.EnsureInitialized(item)) {
                    WizardItemCollection.SaveItemBehaviors(item);
                }
                foreach (var (name, effect) in PetProgress.ComputeEffects(item, JewelSockets.GetPetJewelTalents(item))) {
                    CharacterEffectHelper.AddStatisticEffectToStats(wizard.GameStats, name, effect);
                    wizard.AppliedPetEffects.Add((name, effect));
                }

                continue;
            }

            foreach (var jewel in JewelSockets.GetSocketedJewels(item)) {
                foreach (var info in jewel.m_equipEffects ?? []) {
                    switch (info) {
                        case ProvideSpellEffectInfo spell:
                            if (GameEffectFactory.CreateEffectFromInfo(spell, 0) is ProvideSpellEffect provide) {
                                foreach (var card in SpellFactory.CreateSpellsFromEffect(provide) ?? []) {
                                    wizard.AddTemporarySpell(card);
                                }

                                wizard.AppliedJewelEffects.Add(info);
                            }
                            break;
                        case StatisticEffectInfo or StartingPipEffectInfo:
                            if (CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info) is not null) {
                                wizard.AppliedJewelEffects.Add(info);
                            }
                            break;
                    }
                }
            }
        }
    }

}
