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
using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Equipment;

/// <summary>
/// The backpack's Equipment Sets ("gear sets"): a set remembers the worn hat, robe, boots, wand, athame, amulet, ring,
/// deck and pet (mounts are left out, as in the real game) and puts them all on again with one click.
/// The client's EquipmentSet carries the set name (picked from the game's name list), whether it is worn, and an
/// m_serializedItems list of item ids the window draws its wizard picture from.
/// The server keeps its own item list, so equipping never depends on that blob.
/// </summary>
internal static class GearSets {

    internal static readonly EquipmentSlotType[] SavedSlots = [
        EquipmentSlotType.Hat, EquipmentSlotType.Robe, EquipmentSlotType.Shoes, EquipmentSlotType.Weapon,
        EquipmentSlotType.Athame, EquipmentSlotType.Amulet, EquipmentSlotType.Ring, EquipmentSlotType.Deck,
        EquipmentSlotType.Pet,
    ];

    private const PropertyFlags SERIALIZATION_FLAGS = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

    internal static int MaxSets => Math.Max(1, ConfigurationManager.Settings["Character.MaxGearSets"].AsInt(30));


    /// <summary>What is worn now in the slots a set saves.</summary>
    internal static List<GearSetItem> Snapshot(ServerWizEquipmentBehavior behavior) {
        var items = new List<GearSetItem>();
        foreach (var slot in behavior.SlotList ?? []) {
            if (SavedSlots.Contains(slot.SlotType) && slot.ItemId.Value != 0) {
                items.Add(new GearSetItem { ItemId = slot.ItemId.Value, SlotType = slot.SlotType });
            }
        }

        // In slot order, like the game's own list of the worn gear, so an unchanged outfit shows as already saved.
        return items.OrderBy(item => Array.IndexOf(SavedSlots, item.SlotType)).ToList();
    }

    /// <summary>True when every item of the set is worn.</summary>
    internal static bool IsWorn(GearSet set, ServerWizEquipmentBehavior behavior)
        => set.Items.Count > 0 && set.Items.All(item => behavior.HasItemEquipped(item.ItemId));

    internal static EquipmentSet ToClient(GearSet set, ServerWizEquipmentBehavior behavior) => new() {
        m_serializedItems = ItemsBlob(set),
        m_equipmentSetName = set.Name,
        m_isEquipped = IsWorn(set, behavior),
    };

    internal static List<EquipmentSet> ToClientList(ServerWizEquipmentBehavior behavior)
        => (behavior.GearSets ?? []).Select(set => ToClient(set, behavior)).ToList();

    internal static bool TrySerialize(PropertyClass value, out ByteString data) {
        data = default;
        try {
            return new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None)
                .Serialize(value, SERIALIZATION_FLAGS, out data);
        }
        catch (Exception e) {
            Logger.Error("Gear sets: could not serialize {0}: {1}", Logger.Args(value?.GetType().Name, e));

            return false;
        }
    }

    internal static bool TrySerializeList(ServerWizEquipmentBehavior behavior, out ByteString data)
        => TrySerialize(new EquipmentSetList { m_equipmentSetList = ToClientList(behavior) }, out data);

    /// <summary>
    /// Finds the set a client message means. The client sends the set back as a string, most likely the EquipmentSet
    /// it was given; a bare 4-byte name and the exact bytes the server sent are understood too.
    /// </summary>
    internal static int Find(ServerWizEquipmentBehavior behavior, byte[] data, out string how) {
        how = "nothing matched";
        var sets = behavior.GearSets ?? [];
        if (data is null || data.Length == 0 || sets.Count == 0) {
            return -1;
        }

        if (TryRead(data, new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None), out var name)
            || TryRead(data, new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None), out name)) {
            var index = sets.FindIndex(set => set.Name == name);
            if (index >= 0) {
                how = $"EquipmentSet named {name}";

                return index;
            }
        }

        if (data.Length == 4) {
            var bareName = BitConverter.ToUInt32(data, 0);
            var index = sets.FindIndex(set => set.Name == bareName);
            if (index >= 0) {
                how = $"bare name {bareName}";

                return index;
            }
        }

        for (var i = 0; i < sets.Count; i++) {
            if (TrySerialize(ToClient(sets[i], behavior), out var sent) && ((byte[]) sent).AsSpan().SequenceEqual(data)) {
                how = "same bytes as sent";

                return i;
            }
        }

        // Last resort: a set's 4-byte name anywhere in what the client sent.
        for (var i = 0; i < sets.Count; i++) {
            var nameBytes = BitConverter.GetBytes(sets[i].Name);
            if (sets[i].Name > 255 && data.AsSpan().IndexOf(nameBytes) >= 0) {
                how = $"name {sets[i].Name} found inside the bytes";

                return i;
            }
        }

        if (uint.TryParse(System.Text.Encoding.ASCII.GetString(data).Trim('\0', ' '), out var textName)) {
            var index = sets.FindIndex(set => set.Name == textName);
            if (index >= 0) {
                how = $"name as text {textName}";

                return index;
            }
        }

        return -1;
    }

    internal static string Hex(byte[] data)
        => data is null ? "(none)" : Convert.ToHexString(data.Length > 256 ? data[..256] : data);

    private static bool TryRead(byte[] data, ObjectSerializer serializer, out uint name) {
        name = 0;
        try {
            if (serializer.Deserialize<EquipmentSet>(data, SERIALIZATION_FLAGS, out var set) && set is not null) {
                name = set.m_equipmentSetName;

                return true;
            }
        }
        catch (Exception) {
            // Not this layout; the caller tries the next one.
        }

        return false;
    }

    /// <summary>
    /// The set's items as the game writes them for the worn gear (EquipmentManagerWindow 0x140873290): a 2-byte count,
    /// then each item's 8-byte global id. The window draws its picture from these ids and compares them with the worn
    /// gear to say a set is already saved.
    /// </summary>
    private static ByteString ItemsBlob(GearSet set) {
        var bytes = new byte[2 + set.Items.Count * 8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (ushort) set.Items.Count);
        for (var i = 0; i < set.Items.Count; i++) {
            BitConverter.TryWriteBytes(bytes.AsSpan(2 + i * 8), set.Items[i].ItemId);
        }

        return bytes;
    }

}
