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
 * HOUSE CREATURES
 * ========================================================================
 *
 * PURPOSE:
 * Pets and mounts placed in a house. Builds the data the client shows them from, and keeps the backpack item's own
 * stored document while the creature is in the house or the attic, so the same pet comes back with its level,
 * talents and name, and the same mount with its dyes.
 *
 * USAGE EXAMPLE:
 * if (HouseCreatures.IsKept(templateId)) {
 *     placed = placed with { Custom = PlacedItem.EncodeCustom(HouseCreatures.BuildCustom(item)) };
 * }
 * var item = HouseCreatures.TakeBack(wizard, placed.TemplateId, placed.ItemGid, out var kept);
 *
 * NOTE:
 * - The client builds a placed pet with a ClientPetItemBehavior and a ClientPetNameBehavior and loads the entry's
 *   m_customData into the pet behavior with SerializerBinary(versionable, no flags) and property mask 0x80: the level,
 *   XP, names, ratings, happiness, the m_housingPet* colors, the original GID and the jewel. It copies the colors onto
 *   the object. A mount's data is a MountItemBehavior loaded with mask 0x10000: the original GID and the three colors
 *   (ClientHousingManager builds the object, 0x140f32420).
 * - A pet or mount keeps its identity (Niduus, 2026-10-23): its stored document is not deleted when it is placed or
 *   put in the attic, and it is the item that comes back. Only an item whose document is gone is made anew.
 *
 * TODO:
 * - Pets wander in the real game (MSG_UPDATEHOUSINGPET, MSG_CHANGEBREADCRUMBREQUEST); here a placed pet stands still.
 * - Check with a capture whether the attic shows a pet from its custom data.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing.Families;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

internal static class HouseCreatures {

    private const string PetFamily = "Pet";
    private const string MountFamily = "Mount";
    private const uint PetMask = 0x80;
    private const uint MountMask = 0x10000;

    /// <summary>
    /// Checks whether a template is a pet or a mount, whose backpack item keeps its stored document in the house.
    /// </summary>
    /// <param name="templateId">The item's template id.</param>
    /// <returns><c>true</c> for a pet or a mount.</returns>
    internal static bool IsKept(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template && IsCreature(template);

    /// <summary>
    /// Checks whether a template is in the Pet or Mount family.
    /// </summary>
    /// <param name="template">An item template.</param>
    /// <returns><c>true</c> for a pet or a mount.</returns>
    internal static bool IsCreature(WizItemTemplate template)
        => HousingFamilies.Of(template).Any(family => family.Id is PetFamily or MountFamily);

    /// <summary>
    /// Builds the custom data of a pet's or mount's blob entry from its backpack item.
    /// </summary>
    /// <param name="item">The backpack item.</param>
    /// <returns>The bytes the client loads, or <c>null</c> when the item is neither or could not be serialized.</returns>
    internal static byte[] BuildCustom(WizClientObjectItem item) {
        try {
            if (item is null || CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template) {
                return null;
            }

            var families = HousingFamilies.Of(template);
            PropertyClass behavior;
            uint mask;
            if (families.Any(family => family.Id == PetFamily)) {
                behavior = PetData(item);
                mask = PetMask;
            } else if (families.Any(family => family.Id == MountFamily)) {
                behavior = new MountItemBehavior {
                    m_housingMountOriginalGID = item.m_globalID,
                    m_housingPetPrimaryColor = item.m_primaryColor,
                    m_housingPetPattern = item.m_pattern,
                    m_housingPetSecondaryColor = item.m_secondaryColor,
                };
                mask = MountMask;
            } else {
                return null;
            }

            // The client reads these with object and property sizes and no serializer flags (SerializerBinary(data, true, 0)).
            var serializer = new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None);

            return serializer.Serialize(behavior, mask, out var serialized) ? (byte[]) serialized : null;
        } catch (Exception exception) {
            Logger.Error("Housing: could not build the house data of item {ItemId}, template {TemplateId}: {Reason}",
                Logger.Args(item?.m_globalID.Full ?? 0, item?.m_templateID ?? 0, exception.Message));

            return null;
        }
    }

    /// <summary>
    /// Gives back the backpack item of a pet or mount that leaves a house or an attic: its own stored document when it
    /// has one, a new item otherwise. The item is not added to the backpack.
    /// </summary>
    /// <param name="wizard">The character who takes it.</param>
    /// <param name="templateId">The object's template id.</param>
    /// <param name="itemGid">The global id it had as a backpack item.</param>
    /// <param name="kept"><c>true</c> when the item is the stored document, which must not be stored again.</param>
    /// <returns>The item, or <c>null</c> when the template is not an item.</returns>
    internal static WizClientObjectItem TakeBack(Wizard wizard, uint templateId, ulong itemGid, out bool kept) {
        kept = false;
        if (itemGid != 0 && IsKept(templateId) && wizard.InventoryBehavior.GetItem(itemGid) is null) {
            try {
                var stored = WizardItemCollection.GetItem(wizard.CharId, itemGid);
                if (stored is not null && stored.m_templateID == templateId) {
                    kept = true;
                    Logger.Information("Housing: character {CharId} gets back item {ItemId}, template {TemplateId}, from its stored document.",
                        Logger.Args(wizard.CharId, itemGid, templateId));

                    return stored;
                }

                Logger.Warning("Housing: item {ItemId}, template {TemplateId}, of character {CharId} has no stored document, so a new one is made.",
                    Logger.Args(itemGid, templateId, wizard.CharId));
            } catch (Exception exception) {
                Logger.Error("Housing: could not load the stored item {ItemId} of character {CharId}: {Reason}",
                    Logger.Args(itemGid, wizard.CharId, exception.Message));
            }
        }

        return HousingItems.CreateItem(wizard, templateId, itemGid);
    }

    private static ClientPetItemBehavior PetData(WizClientObjectItem item) {
        var pet = PetProgress.GetBehavior(item);
        var jewel = JewelSockets.GetSocketedJewels(item).FirstOrDefault();

        // Only the properties of mask 0x80 are sent, so only those are copied.
        return new ClientPetItemBehavior {
            m_level = pet?.m_level ?? 1,
            m_XP = pet?.m_XP ?? 0,
            m_firstName = pet?.m_firstName ?? 0,
            m_middleName = pet?.m_middleName ?? 0,
            m_lastName = pet?.m_lastName ?? 0,
            m_hatchedTimeSecs = pet?.m_hatchedTimeSecs ?? 0,
            m_overallRating = pet?.m_overallRating ?? 0,
            m_activeRating = pet?.m_activeRating ?? 0,
            m_happiness = pet?.m_happiness ?? 0,
            m_housingPetPrimaryColor = item.m_primaryColor,
            m_housingPetPattern = item.m_pattern,
            m_housingPetSecondaryColor = item.m_secondaryColor,
            m_housingPetOriginalGID = item.m_globalID,
            m_housingPetJewelTemplateID = jewel?.m_templateID ?? 0,
        };
    }

}
