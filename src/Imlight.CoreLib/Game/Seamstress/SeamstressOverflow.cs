/*
 * SEAMSTRESS OVERFLOW
 *
 * An unstitch turns one item into two. When the backpack has no room for the second one (the appearance item, or the
 * look pet), it goes to the bank like a drop does (ItemPlacement), so a full backpack does not stop an unstitch
 * (Niduus, 2026-10-27: unstitching never worked). Only when the bank is off or full too is the unstitch refused.
 */

using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Seamstress;

internal static class SeamstressOverflow {

    /// <summary>
    /// Whether one more item of this kind fits in the backpack, counting worn items as the client does.
    /// </summary>
    internal static bool FitsBackpack(Wizard wizard, WizClientObjectItem item)
        => !BankRules.TakesBackpackSlot(item) || wizard.BackpackFreeSlots >= 1;

    /// <summary>
    /// Whether the bank is on and has room for the item.
    /// </summary>
    internal static bool FitsBank(Wizard wizard, WizClientObjectItem item)
        => BankConfig.IsEnabled && !BankRules.IsRental(item) && wizard.StorageBehavior.HasRoomFor(item);

    /// <summary>
    /// Stores a new item of an unstitch that did not fit in the backpack, and tells the client where it went.
    /// </summary>
    internal static void GiveToBank(IActorRef session, Wizard wizard, WizClientObjectItem item) {
        item.m_characterId = wizard.CharId;
        var placement = ItemPlacement.Give(session, wizard, item);
        Logger.Information("[Seamstress] The backpack of {CharId} was full, so item {ItemId} of the unstitch went to the {Place}.",
            Logger.Args(wizard.CharId, item.m_globalID.Full, placement));
    }

}
