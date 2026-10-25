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
*/

using System;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Equipment;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The backpack's Equipment Sets window: make a set from what is worn, put a set on, delete one and move one up or down.
/// </summary>
internal partial class EquipmentService {

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CREATENEWEQUIPMENTSET))]
    private void ReceiveCreateNewEquipmentSet(WIZARD2_53_PROTOCOL.MSG_CREATENEWEQUIPMENTSET message) {
        try {
            var wizard = GetActiveWizard();
            var behavior = wizard.EquipmentBehavior;
            behavior.GearSets ??= [];

            var items = GearSets.Snapshot(behavior);
            var existing = behavior.GearSets.FindIndex(set => set.Name == message.SetName);
            Logger.Information("Gear sets: {0} makes set {1} from {2} worn items ({3}).",
                Logger.Args(wizard.CharId, message.SetName, items.Count, existing >= 0 ? "replaces the set of that name" : "new"));

            if (items.Count == 0 || (existing < 0 && behavior.GearSets.Count >= GearSets.MaxSets)) {
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_CREATESETFAILED());
                InformGameClient(items.Count == 0
                    ? "Put some gear on first: a set saves what you are wearing."
                    : $"You already have {GearSets.MaxSets} equipment sets. Delete one to make room.");

                return;
            }

            // Saving under a name that is taken updates that set, so a set can be changed without deleting it.
            if (existing >= 0) {
                behavior.GearSets[existing].Items = items;
                WizardCollection.UpdateCharacterItems(wizard);
                SendGearSetList();

                return;
            }

            var newSet = new GearSet { Name = message.SetName, Items = items };
            behavior.GearSets.Add(newSet);
            WizardCollection.UpdateCharacterItems(wizard);

            if (GearSets.TrySerialize(GearSets.ToClient(newSet, behavior), out var data)) {
                Logger.Information("Gear sets: sent set {0} as {1}.", Logger.Args(newSet.Name, GearSets.Hex(data)));
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPMENTSETCREATED {
                    NewSet = data,
                    GlobalID = wizard.GameObjectID,
                });
            }
            else {
                SendGearSetList();
            }
        }
        catch (Exception e) {
            Logger.Error("Gear sets: making set {0} failed: {1}", Logger.Args(message.SetName, e));
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_CREATESETFAILED());
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_EQUIPSET))]
    private void ReceiveEquipSet(WIZARD2_53_PROTOCOL.MSG_EQUIPSET message) {
        try {
            var wizard = GetActiveWizard();
            var behavior = wizard.EquipmentBehavior;
            var index = GearSets.Find(behavior, message.Set, out var how);
            Logger.Information("Gear sets: {0} puts on a set, client sent {1} ({2}, set {3}).",
                Logger.Args(wizard.CharId, GearSets.Hex(message.Set), how, index));
            if (index < 0) {
                InformGameClient("That equipment set could not be found. Try making it again.");

                return;
            }

            var set = behavior.GearSets[index];
            var missing = 0;
            var putOn = 0;
            foreach (var item in set.Items.ToList()) {
                if (behavior.HasItemEquipped(item.ItemId)) {
                    continue;
                }

                if (!wizard.InventoryBehavior.HasItem(item.ItemId)) {
                    missing++;

                    continue;
                }

                try {
                    EquipItem(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
                        IsEquip = 1,
                        ItemID = item.ItemId,
                        SlotName = item.SlotType.ToString(),
                    });
                    putOn++;
                }
                catch (Exception e) {
                    Logger.Error("Gear sets: putting on item {0} of set {1} failed: {2}", Logger.Args(item.ItemId, set.Name, e));
                }
            }

            Logger.Information("Gear sets: set {0} has {1} items: {2} put on, {3} already worn, {4} not in the backpack.",
                Logger.Args(set.Name, set.Items.Count, putOn, set.Items.Count - putOn - missing, missing));

            if (missing > 0) {
                InformGameClient(missing == 1
                    ? "1 item of this set is not in your backpack anymore, so it was left out."
                    : $"{missing} items of this set are not in your backpack anymore, so they were left out.");
            }

            // The game marks the set it is sent back as the worn one (ClientWizEquipmentBehavior::MSG_EquipSet).
            if (GearSets.TrySerialize(GearSets.ToClient(set, behavior), out var worn)) {
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPSET { Set = worn, GlobalID = wizard.GameObjectID });
            }

            SendGearSetList();
        }
        catch (Exception e) {
            Logger.Error("Gear sets: putting on a set failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_DELETEEQUIPMENTSET))]
    private void ReceiveDeleteEquipmentSet(WIZARD2_53_PROTOCOL.MSG_DELETEEQUIPMENTSET message) {
        try {
            var wizard = GetActiveWizard();
            var behavior = wizard.EquipmentBehavior;
            var index = GearSets.Find(behavior, message.Set, out var how);
            Logger.Information("Gear sets: {0} deletes a set, client sent {1} ({2}, set {3}).",
                Logger.Args(wizard.CharId, GearSets.Hex(message.Set), how, index));
            if (index >= 0) {
                behavior.GearSets.RemoveAt(index);
                WizardCollection.UpdateCharacterItems(wizard);
            }

            SendGearSetList();
        }
        catch (Exception e) {
            Logger.Error("Gear sets: deleting a set failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_MOVEEQUIPMENTSET))]
    private void ReceiveMoveEquipmentSet(WIZARD2_53_PROTOCOL.MSG_MOVEEQUIPMENTSET message) {
        try {
            var wizard = GetActiveWizard();
            var sets = wizard.EquipmentBehavior.GearSets ?? [];
            var from = (int) message.SetNumber;
            var to = message.MoveDown != 0 ? from + 1 : from - 1;
            Logger.Information("Gear sets: {0} moves set {1} {2}.",
                Logger.Args(wizard.CharId, from, message.MoveDown != 0 ? "down" : "up"));

            if (from >= 0 && from < sets.Count && to >= 0 && to < sets.Count) {
                (sets[from], sets[to]) = (sets[to], sets[from]);
                WizardCollection.UpdateCharacterItems(wizard);
            }

            SendGearSetList();
        }
        catch (Exception e) {
            Logger.Error("Gear sets: moving a set failed: {0}", Logger.Args(e));
        }
    }

    private void SendGearSetList() {
        var wizard = GetActiveWizard();
        if (!GearSets.TrySerializeList(wizard.EquipmentBehavior, out var data)) {
            return;
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEEQUIPMENTSETS {
            GlobalID = wizard.GameObjectID,
            Buffer = data,
        });
    }

}
