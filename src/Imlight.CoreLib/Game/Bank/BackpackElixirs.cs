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
 * BACKPACK ELIXIRS
 * ========================================================================
 *
 * PURPOSE:
 * The Expand Backpack Elixir (Elixir-041) makes the backpack bigger for good. Its template has no result the server
 * could run, so the server adds the slots itself: [Character] ExpandBackpackElixirAmount per elixir, up to
 * [Character] MaxBackpackItems.
 *
 * USAGE EXAMPLE:
 * if (BackpackElixirs.Is(template)) { BackpackElixirs.TryUse(session, wizard, out var failure); }
 *
 * NOTE:
 * The client hears the new size with MSG_UPDATEEXTRAINVENTORY. The settings are read on every use, so they apply live.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/26/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Bank;

internal static class BackpackElixirs {

    private const string TemplatesKey = "Character.ExpandBackpackElixirs";
    private const string AmountKey = "Character.ExpandBackpackElixirAmount";
    private const string MaxKey = "Character.MaxBackpackItems";

    // The game's Expand Backpack Elixir.
    private const string DefaultTemplates = "1431039";
    private const int DefaultAmount = 10;

    /// <summary>
    /// Whether an item template is an Expand Backpack Elixir.
    /// </summary>
    /// <param name="template">The item's template.</param>
    internal static bool Is(WizItemTemplate template) {
        if (template is null) {
            return false;
        }

        var ids = ConfigurationManager.GetValue(TemplatesKey, DefaultTemplates);
        return ids.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Any(id => ulong.TryParse(id, out var templateId) && templateId == template.m_templateID);
    }

    /// <summary>
    /// Adds one elixir's slots to the backpack, saves it and tells the client.
    /// </summary>
    /// <param name="session">The SessionActor reference of the wizard's session.</param>
    /// <param name="wizard">The wizard that used the elixir.</param>
    /// <param name="failure">Why nothing changed. Empty on success.</param>
    /// <returns>False, with the backpack as it was, when it is at [Character] MaxBackpackItems or could not be saved.</returns>
    internal static bool TryUse(IActorRef session, Wizard wizard, out string failure) {
        var inventory = wizard.InventoryBehavior;
        var amount = Math.Max(0, ConfigurationManager.GetValue(AmountKey, DefaultAmount));
        var max = ConfigurationManager.GetValue(MaxKey, 0);
        var room = max > 0 ? Math.Max(0, max - inventory.Limit) : amount;
        var added = Math.Min(amount, room);
        if (added <= 0) {
            failure = $"Your backpack is already as big as it can get ({inventory.Limit}).";

            return false;
        }

        var before = inventory.Limit;
        inventory.ExtraSlots += added;
        if (!WizardCollection.UpdateCharacterItems(wizard)) {
            inventory.ExtraSlots -= added;
            failure = "The bigger backpack could not be saved.";

            return false;
        }

        Logger.Information("{0} used an Expand Backpack Elixir: backpack {1} -> {2}.", Logger.Args(wizard.CharId, before, inventory.Limit));
        Announce(session, wizard);
        failure = string.Empty;

        return true;
    }

    /// <summary>
    /// Tells the client the backpack's elixir slots and its whole size.
    /// </summary>
    internal static void Announce(IActorRef session, Wizard wizard)
        => session.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEEXTRAINVENTORY {
            ExtraInventorySpace = wizard.InventoryBehavior.ExtraSlots,
            CurrentInventorySpace = wizard.InventoryBehavior.Limit,
        });

}
