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
 * ITEM PLACEMENT
 * ========================================================================
 *
 * PURPOSE:
 * Finds a place for a new item a player is given (a drop, a reward, a purchase, a gift, an admin command): the
 * backpack, and when that is full the bank, and for a jewel the shared bank. An item with no room is lost. It
 * also tells the client where the item went.
 *
 * USAGE EXAMPLE:
 * var placement = ItemPlacement.Place(wizard, item);
 * ItemPlacement.Announce(session, wizard, item, placement);
 *
 * NOTE:
 * The backpack is full when its items and the worn ones fill its limit, as the client counts it (Wizard.
 * BackpackFreeSlots). A jewel takes no backpack slot: it goes to the backpack until the wizard holds [Character]
 * MaxJewelsAllowed of them, then to the shared bank while it is under its jewel cap, then to the bank. The bank is only used while it is on, see
 * BankConfig.IsEnabled. A rental never goes into the bank. The client hears about an overflow item before the
 * loot popup, which shows it with the bank wording.
 *
 * TODO:
 * Is a full jewel pouch the jewel count alone, or do worn (socketed) jewels count too?
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Bank;

internal static class ItemPlacement {

    private const string MaxJewelsAllowedKey = "Character.MaxJewelsAllowed";

    /// <summary>
    /// Stores a new item where there is room for it.
    /// </summary>
    /// <param name="wizard">The wizard that is given the item.</param>
    /// <param name="item">The new item, with its behaviors initialized. It is stored nowhere yet.</param>
    /// <returns>Where the item is now. Lost means it was stored nowhere.</returns>
    internal static Placement Place(Wizard wizard, WizClientObjectItem item) {
        // A pet snack or reagent is a stack of its bag, never an item of the backpack's list.
        if (Wizard.IsBagStack(item)) {
            return wizard.AddToBag(item.m_templateID, out _) ? Placement.Backpack : Placement.Lost;
        }

        // A new pet keeps its genes and stats from the first save, wherever it goes.
        PetProgression.EnsureInitialized(item);

        var isJewel = BankRules.IsJewel(item);
        var fits = isJewel ? !IsJewelPouchFull(wizard) : !BankRules.TakesBackpackSlot(item) || wizard.BackpackFreeSlots >= 1;
        if (fits && wizard.AddInitializedItemToInventory(item)) {
            return Placement.Backpack;
        }

        if (BankRules.IsRental(item) || !BankConfig.IsEnabled) {
            return Placement.Lost;
        }

        // The shared bank is asked only when its stored state may agree, so a full bank does not log a refusal for every item.
        if (isJewel && BankRules.CanMove(BankContainer.Backpack, BankContainer.SharedBank, item, 1, wizard, wizard.Account) == BankDenial.None
            && BankCollection.TryAddShared(wizard, item).Success) {
            return Placement.SharedBank;
        }

        if (wizard.StorageBehavior.HasRoomFor(item) && wizard.AddItemToBank(item)) {
            return Placement.Bank;
        }

        return Placement.Lost;
    }

    /// <summary>
    /// Stores a new item where there is room for it and tells the client where it went, see Place and Announce. An
    /// item that fits nowhere is not announced, so a purchase can refuse it instead.
    /// </summary>
    /// <param name="session">The SessionActor reference of the wizard's session.</param>
    /// <param name="wizard">The wizard that is given the item.</param>
    /// <param name="item">The new item, with its behaviors initialized. It is stored nowhere yet.</param>
    /// <returns>Where the item is now. Lost means it was stored nowhere.</returns>
    internal static Placement Give(IActorRef session, Wizard wizard, WizClientObjectItem item) {
        var placement = Place(wizard, item);
        if (placement != Placement.Lost) {
            Announce(session, wizard, item, placement);
        }

        return placement;
    }

    /// <summary>
    /// Tells the client where a new item went: the inventory add, the storage add with the overflow notice, or
    /// the notice that the item is gone.
    /// </summary>
    /// <param name="session">The SessionActor reference of the wizard's session.</param>
    /// <param name="wizard">The wizard that was given the item.</param>
    /// <param name="item">The item that was placed.</param>
    /// <param name="placement">What Place returned for it.</param>
    internal static void Announce(IActorRef session, Wizard wizard, WizClientObjectItem item, Placement placement) {
        switch (placement) {
            case Placement.Backpack when Wizard.IsBagStack(item):
                var stack = (WizClientObjectItem) wizard.PetSnackBehavior?.GetSnack(item.m_templateID)
                    ?? wizard.AlchemyBehavior?.GetReagent(item.m_templateID);
                if (stack is not null) {
                    BankSync.SendBackpackStackAdded(session, wizard, stack);
                }

                break;
            case Placement.Backpack:
                ItemSync.SendAdded(session, wizard, item);

                break;
            case Placement.Bank:
                SendOverflow(session, wizard, item, shared: false, wizard.StorageBehavior.Items.Count);

                break;
            case Placement.SharedBank:
                SendOverflow(session, wizard, item, shared: true, wizard.Account.SharedBank.Items.Count);

                break;
            default:
                Logger.Warning("There was no room for item {ItemId} (template {TemplateId}) of {CharId}, so it is lost.",
                    Logger.Args(item.m_globalID.Full, item.m_templateID.Full, wizard.CharId));
                session.Tell(BankSync.ItemDrop(item.m_templateID.Full));

                break;
        }
    }

    private static void SendOverflow(IActorRef session, Wizard wizard, WizClientObjectItem item, bool shared, int countInBank) {
        var add = BankSync.StorageAdd(wizard, item, shared);
        if (add is null) {
            return;
        }

        session.Tell(add);
        session.Tell(BankSync.ItemOverflowToBank(item.m_globalID, countInBank));
        PetTome.Announce(session, wizard, item);
    }

    private static bool IsJewelPouchFull(Wizard wizard) {
        var allowed = ConfigurationManager.GetValue(MaxJewelsAllowedKey, 0);

        return allowed > 0 && wizard.InventoryBehavior.Items.Count(BankRules.IsJewel) >= allowed;
    }

}
