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
 * ITEM SYNC
 * ========================================================================
 *
 * PURPOSE:
 * Tells a player's client that an item entered or left its inventory behavior,
 * with the one property mask the client's inventory reader expects.
 *
 * USAGE EXAMPLE:
 * if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, item)) { ... }
 * ItemSync.SendRemoved(SessionActor.ActorRef, wizard, itemId);
 *
 * NOTE:
 * The session is a SessionActor reference: an IMessage told to it goes to the
 * client socket. The client reads the item blob at mask 0x18 (Prop_Transmit |
 * Prop_AuthorityTransmit) and a blob written at another mask is misaligned.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Shared.Items;

internal static class ItemSync {

    private const uint ItemSerializationFlags =
        (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit);
    private static readonly CoreObjectSerializer s_itemSerializer = new(behaviors: SerializerFlags.None);

    /// <summary>
    /// Serializes an item the way the client's inventory add message reads it.
    /// </summary>
    /// <param name="item">The item to serialize.</param>
    /// <param name="serialized">The item blob.</param>
    /// <returns>False if the item could not be serialized.</returns>
    internal static bool TrySerialize(WizClientObjectItem item, out ByteString serialized)
        => s_itemSerializer.Serialize(item, ItemSerializationFlags, out serialized);

    // The Pet Hatching Kiosk keeps each pet the inventory way (TrySerializeForKiosk), and its pet messages
    // (MSG_REQUESTHATCHMAKINGPETDATA, MSG_REQUESTHATCHMAKINGHATCHEDPET) carry only the pet's ClientPetItemBehavior,
    // written with the plain serializer and property mask 7. Read from WizardGraphicalClient.exe: HatchmakingManager
    // loads PetData with SerializerBinary (mask 7) and DynamicCasts it to ClientPetItemBehavior. A whole item sent the
    // inventory way read as nothing (the hatch window never opened), and a whole item sent the plain way failed that
    // cast and crashed the game (0.10.956, Niduus 2026-11-21).
    private const uint KIOSK_PET_FLAGS =
        (uint) (PropertyFlags.Prop_Save | PropertyFlags.Prop_Copy | PropertyFlags.Prop_Public);
    private const uint WIZ_CLIENT_OBJECT_ITEM_HASH = 1653772158;

    /// <summary>Serializes a pet the way the Pet Hatching Kiosk keeps it (the whole item, the inventory way).</summary>
    internal static bool TrySerializeForKiosk(WizClientObjectItem item, out ByteString serialized)
        => new CoreObjectSerializer(behaviors: SerializerFlags.None).Serialize(item, ItemSerializationFlags, out serialized);

    /// <summary>
    /// The pet data the kiosk's windows read: the pet's ClientPetItemBehavior, plain, mask 7. Null when the item has
    /// none or it cannot be written (the game is then sent nothing rather than something it cannot read).
    /// </summary>
    internal static byte[] KioskPetData(WizClientObjectItem item) {
        var pet = item?.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().FirstOrDefault();
        if (pet is null) {
            return null;
        }

        // The kiosk windows read the pet's template from the field at m_hatchedTimeSecs (offset 0xa0: the IsInPetTome
        // map and Browse Pets by Pedigree, which has no pet type of its own). A hatched pet does not use that time, so
        // the copy sent carries the template there; the pet itself keeps its own value.
        var hatched = pet.m_hatchedTimeSecs;
        ByteString data;
        bool written;
        try {
            pet.m_hatchedTimeSecs = (uint) item.m_templateID;
            written = new ObjectSerializer(false, SerializerFlags.None).Serialize(pet, KIOSK_PET_FLAGS, out data);
        }
        finally {
            pet.m_hatchedTimeSecs = hatched;
        }

        if (!written) {
            return null;
        }

        byte[] bytes = data;

        return bytes is { Length: > 4 } ? bytes : null;
    }

    /// <summary>
    /// The pet data for a pet the kiosk keeps (base64 of the whole item, the inventory way, or the plain way pets were
    /// kept in 0.10.956). Null when it cannot be read.
    /// </summary>
    /// <param name="itemData">The kept item.</param>
    /// <param name="shownTalents">When set, the copy sent has these as the pet's talents, with only its own revealed ones
    /// revealed (another player's kiosk pet, see PetHatching.KioskShownTalents). The kept item is not changed.</param>
    internal static byte[] KioskPetBytes(string itemData, System.Collections.Generic.List<uint> shownTalents = null) {
        if (string.IsNullOrEmpty(itemData)) {
            return null;
        }

        try {
            var bytes = System.Convert.FromBase64String(itemData);
            WizClientObjectItem item;
            var read = bytes.Length >= 4 && System.BitConverter.ToUInt32(bytes, 0) == WIZ_CLIENT_OBJECT_ITEM_HASH
                ? new ObjectSerializer(false, SerializerFlags.None).Deserialize(bytes, KIOSK_PET_FLAGS, out item)
                : new CoreObjectSerializer(behaviors: SerializerFlags.None).Deserialize(bytes, ItemSerializationFlags, out item);

            if (!read) {
                return null;
            }

            if (shownTalents is { Count: > 0 } && item.m_inactiveBehaviors?.OfType<ClientPetItemBehavior>().FirstOrDefault() is { } pet) {
                // Only the pet's own revealed talents stay revealed, five at most; the rest show as hidden slots.
                var revealed = pet.m_expressedTalents ?? [];
                pet.m_allTalents = [.. shownTalents];
                pet.m_expressedTalents = [.. shownTalents.Where(revealed.Contains).Take(Imlight.CoreLib.Game.Pet.PetHatching.MOST_REVEALED)];
                pet.m_allDerbyTalents ??= [];
                pet.m_expressedDerbyTalents = [.. (pet.m_expressedDerbyTalents ?? []).Take(Imlight.CoreLib.Game.Pet.PetHatching.MOST_REVEALED)];
                Imlight.CoreLib.Game.Pet.PetProgression.RefreshRatings(pet);
            }

            return KioskPetData(item);
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.IO.EndOfStreamException or System.ArgumentException
            or System.IndexOutOfRangeException or System.InvalidOperationException) {
            return null;
        }
    }

    /// <summary>
    /// Tells the client that an item entered the wizard's inventory.
    /// </summary>
    /// <param name="session">The SessionActor reference of the wizard's session.</param>
    /// <param name="wizard">The wizard that received the item.</param>
    /// <param name="item">The item that was added.</param>
    /// <returns>False if the item could not be serialized, in which case nothing was sent.</returns>
    internal static bool SendAdded(IActorRef session, Wizard wizard, WizClientObjectItem item) {
        if (!TrySerialize(item, out var serializedItem)) {
            Logger.Error("Failed to serialize item {0} for the inventory add message.",
                Logger.Args(item.m_globalID.Full));

            return false;
        }

        session.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = serializedItem,
        });

        // A pet of a new kind shows in the open Pet Tome at once.
        PetTome.Announce(session, wizard, item);

        return true;
    }

    /// <summary>
    /// Tells the client that an item left the wizard's inventory.
    /// </summary>
    /// <param name="session">The SessionActor reference of the wizard's session.</param>
    /// <param name="wizard">The wizard that lost the item.</param>
    /// <param name="itemId">The global id of the removed item.</param>
    internal static void SendRemoved(IActorRef session, Wizard wizard, ulong itemId)
        => session.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = itemId,
        });

}
