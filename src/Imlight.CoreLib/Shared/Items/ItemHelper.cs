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
 * ITEM HELPER
 * ========================================================================
 * 
 * PURPOSE:
 * Provides utility methods for item template and equipment slot operations, 
 * including retrieving public item representations and determining 
 * equipment slot types.
 * 
 * USAGE EXAMPLE:
 * // Get the item template for a specific item
 * var itemTemplate = ItemHelper.GetItemTemplate(wizClientItem);
 * 
 * // Get the equipment slot for an item template
 * var equipmentSlot = ItemHelper.GetItemSlot(itemTemplate);
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.Bit;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Items;

internal static class ItemHelper {

    private const string RentalAdjective = "Rental";

    /// <summary>
    /// The item lock is bit 31 of m_pattern. The client sends MSG_REQUESTTOGGLELOCKITEM from the padlock of the
    /// backpack and the bank window and sets the item's m_pattern to the IsLocked of the answer
    /// (ClientWizStorageBehavior::MSG_RequestToggleLockItem). The pattern is saved with the item, so a lock survives a relog.
    /// </summary>
    internal const int LockBit = unchecked((int) 0x80000000);

    /// <summary>
    /// Whether the player locked the item. A locked item may not be sold, deleted, sold to the bazaar or stitched.
    /// </summary>
    /// <param name="item">The item in question, or null.</param>
    /// <returns>True when bit 31 of the pattern is set.</returns>
    internal static bool IsLocked(WizClientObjectItem item) => item is not null && (item.m_pattern & LockBit) != 0;

    /// <summary>
    /// A name for an item in the log: its template's object name and its id.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>Such as "Hat_Fire_L10 (123456)".</returns>
    internal static string DescribeForLog(WizClientObjectItem item) {
        if (item is null) {
            return "an unknown item";
        }

        string name = null;
        try {
            name = CoreObjectFactory.GetCoreTemplate(item.m_templateID) is WizItemTemplate template ? (string) template.m_objectName : null;
        }
        catch {
            // The id is still logged.
        }

        return $"{(string.IsNullOrWhiteSpace(name) ? $"template {item.m_templateID}" : name)} ({item.m_globalID})";
    }

    /// <summary>
    /// Gets the <see cref="EquippedItemInfoList"/> for a <see cref="Wizard"/>. This is a lightweight version of the
    /// actual equipment that is used to publicly display the character's equipment.
    /// </summary>
    /// <param name="item">The Wizard item.</param>
    /// <returns>The public, lightweight version of the given item.</returns>
    internal static WizardEquippedItemInfo GetPublicItem(WizClientObjectItem item) {
        var publicItem = new WizardEquippedItemInfo {
            m_itemID = (uint) item.m_templateID,
            m_pattern = (Bui5) item.m_pattern,
            m_baseColor = (Bui5) item.m_primaryColor,
            m_trimColor = (Bui5) item.m_secondaryColor,
            m_displayID = (uint) item.m_displayID,
        };

        return publicItem;
    }

    /// <summary>
    /// Gets the item template for a given <see cref="WizClientObjectItem"/>.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>The template for the given item. Null, if it wasn't found.</returns>
    internal static WizItemTemplate GetItemTemplate(WizClientObjectItem item) {
        var template = CoreObjectFactory.GetCoreTemplate(item.m_templateID);

        return (WizItemTemplate) template;
    }

    /// <summary>
    /// Whether the item was stitched, meaning it shows the look of a different item template.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>True when the item carries an appearance template.</returns>
    internal static bool IsStitched(WizClientObjectItem item) => (uint) item.m_displayID != 0;

    /// <summary>
    /// Whether an item template is a rental: it has the Rental adjective or a rental behavior.
    /// </summary>
    /// <param name="template">The item template, or null when the item has none.</param>
    /// <returns>True for a rental.</returns>
    internal static bool IsRental(WizItemTemplate template)
        => template?.m_adjectiveList?.Any(adjective => string.Equals(adjective, RentalAdjective, StringComparison.OrdinalIgnoreCase)) == true
           || template?.m_behaviors?.OfType<RentalBehaviorTemplate>().Any() == true;

    /// <summary>
    /// Gets the id of the template that decides how an item looks: the appearance template of a stitched
    /// item, otherwise the item's own template.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>The template id used for the item's look and dye layers.</returns>
    internal static uint GetDisplayTemplateId(WizClientObjectItem item)
        => IsStitched(item) ? (uint) item.m_displayID : (uint) item.m_templateID;

    /// <summary>
    /// Gets the template that decides how an item looks. See <see cref="GetDisplayTemplateId"/>.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>The appearance template. Null, if it wasn't found.</returns>
    internal static WizItemTemplate GetDisplayTemplate(WizClientObjectItem item)
        => CoreObjectFactory.GetCoreTemplate(GetDisplayTemplateId(item)) as WizItemTemplate;

    /// <summary>
    /// Gets the slot of a WizClientObjectItem from the template adjectives.
    /// </summary>
    /// <param name="itemTemplate"></param>
    /// <returns>The EquipmentSlot of the item; null if a matching adjective is not found.</returns>
    internal static EquipmentSlot GetItemSlot(WizItemTemplate itemTemplate) {
        if (itemTemplate?.m_adjectiveList is null) {
            throw new InvalidOperationException("Item template does not have an adjective list.");
        }

        // Iterate through the EquipmentSlot enum and return the first slot that matches the item's slot.
        foreach (var slot in Enum.GetValues(typeof(EquipmentSlotType)).Cast<EquipmentSlotType>()) {
            // Sanitize the slot name.
            var slotName = slot.ToString().Split('.')[^1];

            // Check if any of the items adjectives match the slot name.
            if (itemTemplate.m_adjectiveList.Any(adj => string.Equals(adj, slotName, StringComparison.OrdinalIgnoreCase))) {
                return new EquipmentSlot {
                    SlotType = slot,
                };
            }
        }

        // The client dynamically adds "PIIVictoryDance" to m_adjectiveList in CustomEmoteBehaviorTemplate::OnPostLoad
        // for emotes, so it is not present in the raw Root.wad template data. Dances are Victory Dances too: in a packet
        // capture of the real game (2026-10-22) Bee Dance (1633019, CE_Dance) was worn and played as one.
        if (itemTemplate.m_behaviors?.OfType<CustomEmoteBehaviorTemplate>()
                .Any(b => b.m_emoteType is CustomEmoteType.CE_Emote or CustomEmoteType.CE_Dance) == true) {
            return new EquipmentSlot {
                SlotType = EquipmentSlotType.PIIVictoryDance,
            };
        }

        return null;
    }

    /// <summary>
    /// Returns the slot hash of a given item template.
    /// </summary>
    /// <param name="template">The item template.</param>
    /// <returns>The hash value of the item slot.</returns>
    internal static uint GetItemSlotHash(WizItemTemplate template) {
        var slotType = GetItemSlot(template).SlotType;
        var slotName = slotType.ToString().Split('.')[^1];
        
        return StringHash.Compute(slotName);
    }
    
}
