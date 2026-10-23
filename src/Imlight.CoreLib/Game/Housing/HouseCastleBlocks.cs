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
 * HOUSE CASTLE BLOCKS
 * ========================================================================
 *
 * PURPOSE:
 * The two templates of a castle block: the one its backpack item has and the one it is placed as on a castle floor.
 *
 * USAGE EXAMPLE:
 * var placedTemplate = message.SwitchCastleBlock != 0 ? HouseCastleBlocks.Switched(templateId) : templateId;
 * var backpackTemplate = HouseCastleBlocks.ItemTemplate(placed.TemplateId);
 *
 * NOTE:
 * - CastleBlocksBehaviorTemplate names the other template: m_placedOnFloorTemplateID on the backpack one (a door,
 *   1212043, is placed as its inside door, 1212042), m_pickedupFromFloorTemplateID on the placed one.
 * - The client sets SwitchCastleBlock on MSG_PLACEHOUSINGOBJECT when the block has another template and it is placed on
 *   a floor, or the reverse in its other mode (0x140f6cad0). Its move message always sends 0.
 * - A placed block goes back to the backpack and the attic as the template it was picked up from.
 *
 * TODO:
 * - Check with a capture what the client's mode flag at +0x404 means.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Housing;

internal static class HouseCastleBlocks {

    /// <summary>
    /// Finds the template a castle block is switched to.
    /// </summary>
    /// <param name="templateId">The block's template id.</param>
    /// <returns>The other template, or <paramref name="templateId"/> when the block has none.</returns>
    internal static uint Switched(uint templateId) {
        var block = BehaviorOf(templateId);
        if (block is null) {
            return templateId;
        }

        if (block.m_placedOnFloorTemplateID != 0) {
            return block.m_placedOnFloorTemplateID;
        }

        return block.m_pickedupFromFloorTemplateID != 0 ? block.m_pickedupFromFloorTemplateID : templateId;
    }

    /// <summary>
    /// Finds the template a placed object's backpack item has.
    /// </summary>
    /// <param name="templateId">The placed object's template id.</param>
    /// <returns>The template the block was picked up from, or <paramref name="templateId"/> for any other object.</returns>
    internal static uint ItemTemplate(uint templateId)
        => BehaviorOf(templateId) is { m_pickedupFromFloorTemplateID: not 0 } block ? block.m_pickedupFromFloorTemplateID : templateId;

    private static CastleBlocksBehaviorTemplate BehaviorOf(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template
            ? (template.m_behaviors ?? []).OfType<CastleBlocksBehaviorTemplate>().FirstOrDefault()
            : null;

}
