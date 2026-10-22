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
 * STITCHED ITEM FACTORY
 * ========================================================================
 *
 * PURPOSE:
 * Builds the new items a stitch or an unstitch hands to the player from the plans of StitchRules.
 *
 * USAGE EXAMPLE:
 * if (StitchedItemFactory.TryCreateStitched(wizard, plan, out var item)) { wizard.AddItemToInventory(item); }
 *
 * NOTE:
 * The items are only built here: nothing is added to the inventory or saved.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Seamstress;

internal static class StitchedItemFactory {

    /// <summary>
    /// Builds the item a stitch creates: the stats template with the look of the appearance template.
    /// </summary>
    /// <param name="wizard">The wizard that owns the item.</param>
    /// <param name="plan">The planned stitch.</param>
    /// <param name="item">The new item.</param>
    /// <returns>False if the stats template is not an item template.</returns>
    internal static bool TryCreateStitched(Wizard wizard, StitchPlan plan, out WizClientObjectItem item) {
        if (!TryCreate(wizard, plan.StatsTemplateId, plan.PrimaryColor, plan.SecondaryColor, plan.Pattern, out item)) {
            return false;
        }

        item.m_displayID = plan.DisplayTemplateId;

        return true;
    }

    /// <summary>
    /// Builds one of the plain items an unstitch gives back.
    /// </summary>
    /// <param name="wizard">The wizard that owns the item.</param>
    /// <param name="plan">The planned item.</param>
    /// <param name="item">The new item.</param>
    /// <returns>False if the template is not an item template.</returns>
    internal static bool TryCreate(Wizard wizard, UnstitchedItem plan, out WizClientObjectItem item)
        => TryCreate(wizard, plan.TemplateId, plan.PrimaryColor, plan.SecondaryColor, plan.Pattern, out item);

    /// <summary>
    /// Whether an item has jewel sockets, which a stitch would destroy or leave locked.
    /// </summary>
    /// <param name="item">The item in question.</param>
    /// <returns>True when the item carries a jewel socket behavior.</returns>
    internal static bool HasJewelSockets(WizClientObjectItem item)
        => item.m_inactiveBehaviors?.OfType<ClientJewelSocketBehavior>().Any() == true;

    /// <summary>
    /// Gives an item the jewel sockets of another: unlocked sockets stay unlocked and socketed jewels stay
    /// (Niduus, 2026-10-20: stitched wands came back with their sockets locked again).
    /// </summary>
    /// <param name="from">The item whose sockets are kept.</param>
    /// <param name="to">The new item.</param>
    internal static void CopySockets(WizClientObjectItem from, WizClientObjectItem to) {
        var sockets = from?.m_inactiveBehaviors?.OfType<ClientJewelSocketBehavior>().FirstOrDefault();
        if (sockets is not null && SetSockets(to, sockets)) {
            Logger.Information("[Seamstress] Item {0} keeps the sockets of item {1} ({2} jewels).",
                Logger.Args(to.m_globalID.Full, from.m_globalID.Full,
                    sockets.m_publicJewelInfo?.m_infoPairs?.Count(p => p is not null && p.m_jewelTemplateID != 0) ?? 0));
        }
    }

    /// <summary>
    /// Puts a socket behavior in the item's own socket slot. False if the item has none.
    /// </summary>
    internal static bool SetSockets(WizClientObjectItem item, ClientJewelSocketBehavior sockets) {
        // A new item has no socket state yet (its template slot is empty); GetState makes one in the right slot.
        var slot = JewelSockets.GetState(item, create: true);
        var index = slot is null ? -1 : item.m_inactiveBehaviors.IndexOf(slot);
        if (index < 0) {
            Logger.Warning("[Seamstress] Item {0} (template {1}) has no jewel socket to keep.",
                Logger.Args(item.m_globalID.Full, item.m_templateID.Full));

            return false;
        }

        item.m_inactiveBehaviors[index] = sockets;

        return true;
    }

    private static bool TryCreate(Wizard wizard, uint templateId, int primaryColor, int secondaryColor, int pattern,
                                  out WizClientObjectItem item) {
        item = CoreObjectFactory.FinalizeCoreObject(templateId) as WizClientObjectItem;
        if (item is null) {
            Logger.Warning("Cannot create a seamstress item from template {TemplateId} because it is not an item.",
                Logger.Args(templateId));

            return false;
        }

        item.m_characterId = wizard.CharId;
        item.m_primaryColor = primaryColor;
        item.m_secondaryColor = secondaryColor;
        item.m_pattern = pattern;

        return true;
    }

}
