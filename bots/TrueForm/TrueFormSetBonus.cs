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
using System.Runtime.CompilerServices;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Set bonuses for True Form only (Niduus, 2026-11-19, picked "Only True Form": "Even making use of Set Bonus combos if he
/// wants to truly go all in on something like the fossil pet and mount for the + damage for having 2 equipped"). The server
/// gives nobody set bonuses, so his are put on here: every set he wears enough pieces of (the game's ItemSetBonus tiers)
/// adds its effects to his stats, and again each time his stats are worked out anew (a zone change or log in clears them).
/// Players are left as they are.
/// </summary>
internal static class TrueFormSetBonus {

    // His set effects carry this slot, so they can be found and taken off again.
    private static readonly uint s_slot = StringHash.Compute("TrueFormSetBonus");

    private sealed class Applied {
        internal string Key = "";
        internal readonly List<GameEffectInfo> Infos = [];
    }

    // Keyed on his effect list: a new list means his stats were worked out anew, with no set bonus on them.
    private static readonly ConditionalWeakTable<List<GameEffectBase>, Applied> s_applied = new();

    /// <summary>The set bonus tiers a wizard's worn pieces reach: (set, tier) with the tier's effects.</summary>
    internal static List<(uint Set, int Count, ItemSetBonusData Tier)> Reached(IEnumerable<WizItemTemplate> worn) {
        var reached = new List<(uint, int, ItemSetBonusData)>();
        foreach (var group in worn.Where(t => t is not null && t.m_itemSetBonusTemplateID != 0).GroupBy(t => (uint) t.m_itemSetBonusTemplateID)) {
            if (CoreObjectFactory.GetCoreTemplate(group.Key) is not ItemSetBonusTemplate set) {
                continue;
            }

            var count = group.Count();
            foreach (var tier in (set.m_itemSetBonusDataList ?? []).Where(d => d is not null && d.m_numItemsToEquip <= count)) {
                reached.Add((group.Key, count, tier));
            }
        }

        return reached;
    }

    /// <summary>Puts his set bonuses on his stats when they are not on yet (or his worn sets changed).</summary>
    internal static void Ensure(Wizard wizard) {
        try {
            if (wizard?.GameStats is null || wizard.GameEffects is not { } effects || wizard.EquipmentBehavior is null) {
                return;
            }

            var reached = Reached(wizard.EquipmentBehavior.EquippedItems.Select(ItemHelper.GetItemTemplate).ToList());
            var key = string.Join(",", reached.Select(r => $"{r.Set}:{r.Tier.m_numItemsToEquip}"));
            var applied = s_applied.GetOrCreateValue(effects);
            if (applied.Key == key) {
                return;
            }

            // Off with the old ones first (his sets changed without his stats being worked out anew).
            foreach (var info in applied.Infos) {
                CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, info);
            }

            effects.RemoveAll(e => e.m_itemSlotID == s_slot);
            applied.Infos.Clear();
            var names = new List<string>();
            foreach (var (set, _, tier) in reached) {
                foreach (var info in tier.m_equipEffectsGranted ?? []) {
                    if (info is null || GameEffectFactory.CreateEffectFromInfo(info, s_slot) is not { } effect) {
                        continue;
                    }

                    effect.m_internalID = wizard.NextEffectId();
                    CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info);
                    if (effect is ProvideSpellEffect cards && SpellFactory.CreateSpellsFromEffect(cards) is { } spells) {
                        foreach (var spell in spells) {
                            wizard.AddTemporarySpell(spell);
                        }
                    }

                    effects.Add(effect);
                    applied.Infos.Add(info);
                }

                var shown = Locale.GetEnglishName((string) (CoreObjectFactory.GetCoreTemplate(set) as ItemSetBonusTemplate)?.m_displayName ?? "");
                names.Add($"{(string.IsNullOrWhiteSpace(shown) ? set.ToString() : shown)} ({tier.m_numItemsToEquip} pieces)");
            }

            applied.Key = key;
            if (names.Count > 0) {
                Logger.Information("True Form: set bonus on: {0}.", Logger.Args(string.Join(", ", names)));
            }
        }
        catch (Exception e) {
            Logger.Debug("True Form: could not put his set bonus on: {0}", Logger.Args(e.Message));
        }
    }

}
