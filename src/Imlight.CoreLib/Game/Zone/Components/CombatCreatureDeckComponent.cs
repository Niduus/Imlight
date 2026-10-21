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
 * COMBAT CREATURE DECK
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spelldecks for combat-enabled creature entities, initializing 
 * their spellbooks from both client spell names and SpiralDB deck data.
 * 
 * USAGE EXAMPLE:
 * var deckComponent = new CombatCreatureDeckComponent(zoneEntity);
 * var availableSpells = deckComponent.Spells;
 * 
 * NOTE:
 * Requires prior initialization of CoreObjectFactory and SpellFactory.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/15/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class CombatCreatureDeckComponent : ZoneEntityComponent, IComponentFactory {

    public List<SpellData> Spells { get; } = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && gameObjectTemplate.m_behaviors.Any(x => x is DuelistBehaviorTemplate);

    // ctor
    public CombatCreatureDeckComponent(ZoneEntity entity) : base(entity) { 
        foreach (var spellId in GameSpellIds(entity.Template)) {
            AddSpell(spellId);
        }

        // A creature with an empty deck (no usable client names and no SpiralDB deck) falls
        // back to the default spellbook so it can cast instead of passing every round.
        if (Spells.Count == 0) {
            Logger.Warning(
                "{0} {1} has no usable spells from its client deck or SpiralDB deck, falling back to the default spellbook.",
                Logger.Args(nameof(ZoneEntity), entity.ActiveGameObject.m_debugName)
            );
            foreach (var spellId in CreatureSpellbookCollection.GetDefaultCreatureSpellbook().SpellTemplateIds) {
                AddSpell(spellId);
            }
        }
    }

    /// <summary>
    /// The spells of a creature's game deck, once each, before randomizer mode. The app's Bosses tab shows these
    /// (IdCatalog writes them to creatures.csv).
    /// </summary>
    internal static List<uint> GameSpellIds(CoreTemplate template) {
        var spellIds = new List<uint>();
        if (template?.m_behaviors is null) {
            return spellIds;
        }

        // Collect every behavior from the creature's equipment items.
        var equipmentItemBehaviors = template.m_behaviors
            .OfType<EquipmentBehaviorTemplate>()
            .SelectMany(x => x.m_itemList)
            .Select(x => CoreObjectFactory.GetCoreTemplate(x))
            .Where(x => x != null)
            .SelectMany(x => x.m_behaviors ?? []);

        // Combine equipment item behaviors with the entity's own behaviors.
        // MobDeckBehaviorTemplate can appear in either location.
        var allBehaviors = template.m_behaviors
            .Concat(equipmentItemBehaviors)
            .ToList();

        void Add(uint spellId) {
            // Both sources may list the same spell; creatures carry one entry per spell.
            // Unknown spell ids are skipped; SpellFactory logs the miss.
            if (!spellIds.Contains(spellId) && SpellFactory.GetSpell(spellId) is not null) {
                spellIds.Add(spellId);
            }
        }

        // MobDeckBehaviorTemplate stores spell names directly, if it exists.
        var mobDeck = allBehaviors.OfType<MobDeckBehaviorTemplate>().FirstOrDefault();
        if (mobDeck != null) {
            foreach (var spellName in mobDeck.m_spellList) {
                if (spellName.ToString().Length == 0) {
                    continue;
                }

                // Unknown names are skipped; SpellFactory logs the miss.
                var spell = SpellFactory.GetSpell(spellName.ToString());
                if (spell is not null) {
                    Add(spell.m_templateID);
                }
            }
        }

        // DeckBehaviorTemplate stores a deck name that maps to a SpiralDB spellbook.
        // The deck is the union of both sources: the client's own spell names and the
        // SpiralDB spellbook named by the deck behavior. Either source may be missing.
        var deckBehavior = allBehaviors.OfType<DeckBehaviorTemplate>().FirstOrDefault();
        if (deckBehavior is not null && !string.IsNullOrEmpty(deckBehavior.m_defaultDeck)) {
            var spellbook = CreatureSpellbookCollection.GetCreatureSpellbook(deckBehavior.m_defaultDeck);
            foreach (var spellId in spellbook?.SpellTemplateIds ?? []) {
                Add(spellId);
            }
        }

        return spellIds;
    }

    private void AddSpell(uint spellId) {
        // Both sources may list the same spell; creatures carry one entry per spell.
        if (Spells.Any(x => x.m_templateID == spellId)) {
            return;
        }

        // Unknown spell ids are skipped; SpellFactory logs the miss.
        if (SpellFactory.GetSpell(spellId) is null) {
            return;
        }

        // Randomizer mode may swap it for another spell of the same type and pip cost.
        var rolled = Randomization.Randomizer.RollCreatureSpell(spellId);
        if (rolled != spellId && SpellFactory.GetSpell(rolled) is not null && Spells.All(x => x.m_templateID != rolled)) {
            spellId = rolled;
        }

        // Creatures have infinite spells in their spellbook.
        Spells.Add(new SpellData {
            m_templateID = spellId,
            m_quantity = 9999,
        });
    }

}