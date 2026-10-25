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
 * EQUIPMENT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player equipment interactions, including item equipping, 
 * unequipping, and associated effect handling.
 * 
 * USAGE EXAMPLE:
 * Internal service handling equipment-related messages within the 
 * game server session.
 * 
 * NOTE:
 * - Supports item equip and unequip operations
 * - Manages equipment-related effects and broadcasts
 * - Implements security checks for suspicious equipment actions
 * - A house is equipped in the Islands and Structure slots, which only house items fit (HouseEquipment)
 * 
 * TODO:
 * - Enhance error handling for equipment transfers
 * - Review and improve effect serialization mechanisms
 * - Implement additional validation for equipment actions
 * 
 * Created by: Joji
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Rentals;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class EquipmentService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string ELIXIR_TICK_TIMER_KEY = "elixir-tick";
    private const int ELIXIR_SAVE_INTERVAL_SECONDS = 30;

    private readonly CoreObjectSerializer _itemSerializer = new(
        versionable: false,
        behaviors: SerializerFlags.None
    );
    private readonly CoreObjectSerializer _effectSerializer = new(
        versionable: false,
        behaviors: SerializerFlags.None
    );
    private static bool s_hidePetWhileMounted => ConfigurationManager.Settings["Character.HidePetWhileMounted"].AsBool();
    private const long EXPIRED_RENTAL_GRACE_SECONDS = 60;

    private readonly Dictionary<ulong, long> _expiredRentals = [];
    private ulong _summonedPetId;
    private int _secondsSinceLastElixirSave;
    private bool _isElixirTickerStarted;

    internal static bool HidePetWhileMounted => s_hidePetWhileMounted;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new EquipmentService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_EQUIPITEM))]
    private void ReceiveEquipItem(GAME_5_PROTOCOL.MSG_EQUIPITEM message) {
        try {
            if (message.IsEquip == 1) {
                EquipItem(message);
            }
            else {
                UnEquipItem(message);
            }
        }
        catch (Exception ex) {
            Logger.Error("Error while equipping item: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var playerCharacter = GetActiveWizard();
            var effects = playerCharacter.GameEffects;

            // Every effect goes out now, the pet's and jewels' cards included.
            EquipmentExtras.TakeCardEffectChanges(playerCharacter);
            SendAddEffects([.. effects]);
            ShowPetFormToZone(playerCharacter);

            // Spawn the equipped pet as a zone entity if one is equipped.
            if (playerCharacter.PetOwnerBehavior.EquippedPetTemplateId != 0 && !ReWearIfNamed(null)) {
                SpawnPetEntity();
            }

            // Restore active elixirs HUD countdown and start the playtime ticker.
            RestoreActiveElixirs(playerCharacter);
            StartElixirPlaytimeTicker();
        }
        catch (Exception ex) {
            Logger.Error("Error while attaching effects: {0} {1}",
                Logger.Args(ex.Message, ex.StackTrace));

            throw new ServiceRetryException("Error while attaching effects.", ex);
        }
    }

    private void EquipItem(GAME_5_PROTOCOL.MSG_EQUIPITEM message) {
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();
        var itemId = message.ItemID;

        var item = wizard.InventoryBehavior.GetItem(itemId);
        if (item is null) {
            // The client may have clicked in the moment its rental ran out.
            if (WasRentalJustExpired(itemId)) {
                return;
            }

            var infractionText = $"Player tried to equip item {itemId} that they do not have in their inventory!";
            account.AddInfraction(InfractionType.SuspiciousBehavior, infractionText);

            Logger.Warning("Player tried to equip item {0} that they do not have in their inventory."
                        + " This has been logged as suspicious behavior.",
                Logger.Args(itemId));

            return;
        }

        // Equipping a house picks where Home goes (Niduus, 2026-10-22: equipping a house did nothing), so every try is logged.
        var isHouse = HouseTemplates.TryGet((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue), out _);
        if (isHouse) {
            Logger.Information("{0} asks to equip house item {1} (template {2}) in slot {3}.",
                Logger.Args(wizard.CharId, itemId, item.m_templateID, message.SlotName));
        }

        // A house goes in a house slot and nothing else does.
        if (!HouseEquipment.Allows(item, message.SlotName)) {
            var infractionText = $"Player tried to equip item {itemId} in slot {message.SlotName}, which does not take it!";
            account.AddInfraction(InfractionType.SuspiciousBehavior, infractionText);

            Logger.Warning("Player tried to equip item {0} in slot {1}, which does not take it."
                        + " This has been logged as suspicious behavior.",
                Logger.Args(itemId, message.SlotName));

            return;
        }

        if (RentalItems.TryGetBehavior(item, out var rental) && RentalItems.IsDue(rental, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) {
            Logger.Debug("Player tried to equip rental {0}, which has run out.", Logger.Args(itemId));
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_CHECKRENTALS());

            return;
        }

        // Check if the item is an elixir being used from inventory.
        var itemTemplate = ItemHelper.GetItemTemplate(item);
        var elixirTemplate = itemTemplate?.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault();
        if (elixirTemplate is not null) {
            EquipElixirFromInventory(wizard, item, itemTemplate, elixirTemplate);
            return;
        }

        // Check to see if the player already has this item equipped. If they do, broadcast the removal of it.
        // We don't have to remove it here because the InventoryToEquipmentTransfer method will do that for us.
        // A Victory Dance joins the worn ones while fewer than five are worn, so nothing comes off.
        var danceHasRoom = string.Equals(message.SlotName, nameof(EquipmentSlotType.PIIVictoryDance), StringComparison.OrdinalIgnoreCase)
                           && wizard.EquipmentBehavior.GetVictoryDances().Count < Imlight.CoreLib.Shared.Behaviors.ServerWizEquipmentBehavior.MaxVictoryDances;
        if (!danceHasRoom && wizard.EquipmentBehavior.SlotInUse(message.SlotName, out var index)) {
            // Debug log.
            Logger.Debug("{0} tried to equip item {1} in slot {2} that is already in use. Unequipping from index {3}",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), itemId, message.SlotName, index));

            // Get the item that is currently equipped in this slot.
            var equippedItemId = wizard.EquipmentBehavior.GetItemInSlot(message.SlotName).m_globalID;
            SendUnequipItem(message.SlotName, index, equippedItemId);
        }

        if (!wizard.InventoryToEquipmentTransfer(itemId, out var effects, out var removedEffects)) {
            Logger.Warning("Equip failed on item {0}",
                Logger.Args(itemId));

            return;
        }

        if (isHouse) {
            Logger.Information("{0} equipped house item {1} (template {2}) in slot {3}; Home now goes to house template {4} (0 = the dorm).",
                Logger.Args(wizard.CharId, itemId, item.m_templateID, message.SlotName, HouseDeeds.ResolveEquippedHouse(wizard)));
        }

        SendEquipItem(item, message.SlotName);
        SendAddEffects(effects);
        SendCardEffectChanges(wizard);
        CapEnergy(wizard);

        if (RentalItems.TryGetBehavior(item, out _)) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_STARTRENTAL { ItemId = itemId });
        }

        // A mount equipped indoors is dismounted right away; the reconcile no-ops in the open world.
        if (Enum.TryParse<EquipmentSlotType>(message.SlotName, true, out var equippedSlot)
            && equippedSlot == EquipmentSlotType.Mount) {
            SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT());
            if (s_hidePetWhileMounted) {
                DismissPetEntity();
            }
        }
        else if (equippedSlot == EquipmentSlotType.PIIVictoryDance) {
            ZoneBroadcast(new WIZARD2_53_PROTOCOL.MSG_UPDATEAFTERCOMBATDANCE {
                AfterCombatDance = wizard.WizardAvatar?.m_afterCombatVictoryDance ?? (uint) item.m_templateID,
                PlayerGID = wizard.GameObjectID
            }, false);
        }
        else if (equippedSlot == EquipmentSlotType.PIITeleportEffect) {
            BroadcastTeleportEffect(wizard);
        }

        // If removedEffects is not null, the Wizard replaced an item with another item that has different effects.
        // We need to remove the old effects from the client.
        if (removedEffects is not null) {
            SendRemoveEffects(removedEffects);
        }

        // If this is a pet, spawn it as a zone entity so it appears in the world.
        if (message.SlotName == "Pet") {
            SpawnPetEntity(item);
        }
    }

    private void UnEquipItem(GAME_5_PROTOCOL.MSG_EQUIPITEM message) {
        var wizEquipmentBehavior = GetActiveWizard().EquipmentBehavior;
        var account = GetActiveAccount();
        var itemId = message.ItemID;

        // Check to see if the player has this item equipped. If they don't, log an infraction.
        if (!wizEquipmentBehavior.HasItemEquipped(itemId)) {
            if (WasRentalJustExpired(itemId)) {
                return;
            }

            var infractionText = $"Player tried to unequip item {itemId} that they do not have in their inventory!";
            account.AddInfraction(InfractionType.SuspiciousBehavior, infractionText);

            Logger.Warning("Player tried to unequip item {0} that they do not have in their inventory."
                        + " This has been logged as a suspicious behavior infraction.",
                Logger.Args(itemId));

            return;
        }

        UnequipById(itemId, message.SlotName);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_UNEQUIPITEM))]
    private void ReceiveUnequipItem(CHARACTER_103_PROTOCOL.MSG_UNEQUIPITEM message) {
        var wizEquipmentBehavior = GetActiveWizard().EquipmentBehavior;

        try {
            var slot = wizEquipmentBehavior.SlotList.Find(s => s.ItemId == message.ItemId);
            if (slot is not null) {
                UnequipById(message.ItemId, slot.SlotType.ToString());
            }
        }
        catch (Exception ex) {
            Logger.Error("Error while unequipping item {0}: {1} {2}",
                Logger.Args(message.ItemId, ex.Message, ex.StackTrace));
        }

        Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_UNEQUIPITEMRSP {
            Success = !wizEquipmentBehavior.HasItemEquipped(message.ItemId)
        });
    }

    private void UnequipById(ulong itemId, ByteString slotName) {
        var wizard = GetActiveWizard();
        var wizEquipmentBehavior = wizard.EquipmentBehavior;

        // This needs to be done before we unequip the item, because we need to know what slot it was in.
        var slot = wizEquipmentBehavior.GetSlotOfItem(itemId);

        // Active elixirs cannot be transferred back to backpack inventory.
        var slotObj = wizEquipmentBehavior.SlotList?.Find(s => s.ItemId == itemId);
        if (slotObj?.SlotType == EquipmentSlotType.Elixir) {
            Logger.Warning("Player {0} tried to unequip elixir {1} to backpack inventory.",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), itemId));
            return;
        }

        // The client's unequip request carries no slot name, so the pet is recognized by its slot.
        var isPet = wizEquipmentBehavior.GetEquippedPetId() == itemId;

        if (!wizard.EquipmentToInventoryTransfer(itemId, out var removedEffects)) {
            // If this fails, there is perhaps desync between the server and the client.
            // Send a message to the client to assure them that the server does not have the item equipped.
            SendUnequipItem(slotName, slot, itemId);

            Logger.Warning("Unequip failed on item {0}",
                Logger.Args(itemId));

            return;
        }

        SendUnequipItem(slotName, slot, itemId);
        SendRemoveEffects(removedEffects);
        SendCardEffectChanges(wizard);
        CapEnergy(wizard);

        if (slotObj is not null && HouseEquipment.IsHouseSlot(slotObj.SlotType)) {
            Logger.Information("{0} unequipped house item {1} from slot {2}; Home now goes to house template {3} (0 = the dorm).",
                Logger.Args(wizard.CharId, itemId, slotObj.SlotType, HouseDeeds.ResolveEquippedHouse(wizard)));
        }

        // A manual unequip abandons any pending auto-remount.
        if (Enum.TryParse<EquipmentSlotType>(slotName, true, out var unequippedSlot)
            && unequippedSlot == EquipmentSlotType.Mount
            && wizard.InteriorStowedMountId != 0) {
            wizard.InteriorStowedMountId = 0;
            WizardCollection.UpdateCharacterInteriorStowedMount(wizard);
        }

        if (s_hidePetWhileMounted && (EquipmentSlotType) slot == EquipmentSlotType.Mount) {
            SpawnPetEntity();
        }
        else if (slotObj?.SlotType == EquipmentSlotType.Mount) {
            RespawnNamedPetUnmounted();
        }

        if (isPet) {
            DismissPetEntity();
        }

        var isVictoryDance = slotObj?.SlotType == EquipmentSlotType.PIIVictoryDance;
        if (isVictoryDance) {
            ZoneBroadcast(new WIZARD2_53_PROTOCOL.MSG_UPDATEAFTERCOMBATDANCE {
                // Another worn dance (picked at random) takes over, or 0 when this was the last.
                AfterCombatDance = wizard.WizardAvatar?.m_afterCombatVictoryDance ?? 0,
                PlayerGID = wizard.GameObjectID
            }, false);
        }

        // Taking the teleport effect off brings the normal sparkle back.
        if (slotObj?.SlotType == EquipmentSlotType.PIITeleportEffect) {
            BroadcastTeleportEffect(wizard);
        }
    }

    /// <summary>
    /// Tells everyone in the zone, the wizard included, which teleport effect the wizard's teleports play: the worn
    /// one's template id, or 0 for the normal sparkle. The game keeps it on the wizard's magic school behavior (and on
    /// its own stats for the local player) and plays it from there (client MSG_UpdateTeleportEffect 0x1421f0a00).
    /// </summary>
    private void BroadcastTeleportEffect(Wizard wizard) {
        var effect = wizard.WearTeleportEffect();
        Logger.Information("{0} now teleports with effect {1} (0 = the normal sparkle).", Logger.Args(wizard.CharId, effect));
        ZoneBroadcast(new WIZARD2_53_PROTOCOL.MSG_UPDATETELEPORTEFFECT {
            TeleportEffect = effect,
            PlayerGID = wizard.GameObjectID
        }, false);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_EXPIRERENTAL))]
    private void ReceiveExpireRental(CHARACTER_103_PROTOCOL.MSG_EXPIRERENTAL message) {
        try {
            var wizard = GetActiveWizard();
            var item = wizard.EquipmentBehavior.GetItem(message.ItemId) ?? wizard.InventoryBehavior.GetItem(message.ItemId);

            // A message that outlived its item, or one sent before the timer ran out, changes nothing.
            if (item is null || !RentalItems.TryGetBehavior(item, out var behavior)) {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!RentalItems.IsDue(behavior, now)) {
                return;
            }

            if (!wizard.RemoveRentalItem(message.ItemId, out var removal)) {
                return;
            }

            RememberExpiredRental(message.ItemId);

            // The client moves a worn item to its backpack with the unequip, and the remove only touches the backpack.
            if (removal.WasEquipped) {
                SendUnequipItem(removal.SlotName, removal.SlotIndex, message.ItemId);
                SendRemoveEffects(removal.RemovedEffects);
                CapEnergy(wizard);

                if (s_hidePetWhileMounted && removal.SlotType == EquipmentSlotType.Mount) {
                    SpawnPetEntity();
                }
                if (removal.SlotType == EquipmentSlotType.Pet) {
                    DismissPetEntity();
                }
            }

            ItemSync.SendRemoved(SessionActor.ActorRef, wizard, message.ItemId);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_RENTALUPDATE { TemplateID = removal.TemplateId, Reason = 0 });
        } catch (Exception ex) {
            Logger.Error("Error while expiring rental {0}: {1} {2}", Logger.Args(message.ItemId, ex.Message, ex.StackTrace));
        }
    }

    private void RememberExpiredRental(ulong itemId) {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var staleIds = _expiredRentals
            .Where(expired => now - expired.Value > EXPIRED_RENTAL_GRACE_SECONDS)
            .Select(expired => expired.Key)
            .ToList();
        foreach (var staleId in staleIds) {
            _expiredRentals.Remove(staleId);
        }

        _expiredRentals[itemId] = now;
    }

    private bool WasRentalJustExpired(ulong itemId)
        => _expiredRentals.TryGetValue(itemId, out var expiredAt)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - expiredAt <= EXPIRED_RENTAL_GRACE_SECONDS;

    private void SpawnPetEntity(WizClientObjectItem petItem = null) {
        var zoneActor = SessionActor.GetZoneActor();
        if (zoneActor is null) {
            return;
        }

        if (s_hidePetWhileMounted && GetActiveWizard()?.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is not null) {
            DismissPetEntity();

            return;
        }

        // A wizard who looks like their pet (.petform or the game's Play as Pet button) has no second one beside them.
        if (Commands.Protocols.CommandPetFormProtocol.IsInPetForm(GetActiveWizard())) {
            DismissPetEntity();

            return;
        }

        // If petItem is null, we will use the equipped pet from the player's character.
        if (petItem is null) {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            var wizEquipmentBehavior = wizard.EquipmentBehavior;
            if (wizEquipmentBehavior is null) {
                return;
            }

            var equippedPetId = wizEquipmentBehavior.GetEquippedPetId();
            if (equippedPetId == 0) {
                return;
            }

            petItem = wizEquipmentBehavior.GetItem(equippedPetId);
        }

        var playerObj = GetActiveGameObject();
        if (playerObj is null) {
            return;
        }

        // The client reads the pet's level, XP and talents from this behavior, so every summon sends it.
        GetActiveWizard().InitializePet(petItem);
        if (PetBehaviorUpdate.TryCreate(petItem, out var behaviorUpdate)) {
            SendToSocket(behaviorUpdate);
        }

        // The generic "PetObject" template (ID 2) is used for all pet zone entities.
        // The specific breed appearance comes from behaviors on the template.
        // A pet with a .name pet name summoned while the wizard rides can't find its Idle and Run in the game, even with
        // the mount briefly off in the player's game (Niduus, 2026-10-25; unnamed pets are fine). So on a mount it comes
        // out under its normal name, and gets its own name back when the mount comes off (RespawnNamedPetUnmounted).
        var coreObj = PetFactory.CreatePetGameObject(petItem, playerObj.m_globalID, showNameOverride: !IsMounted());
        if (coreObj is null) {
            return;
        }

        // Place the pet at the player's location.
        coreObj.m_location = playerObj.m_location;
        coreObj.m_orientation = playerObj.m_orientation;

        // Only one pet follows its owner: a swap or a repeated attach replaces the previous summon.
        DismissPetEntity();

        var spawnMsg = new ZONE_102_PROTOCOL.MSG_SPAWNENTITY {
            CoreObject = coreObj,
            Template = CoreObjectFactory.GetCoreTemplate(PetFactory.GENERIC_PET_TEMPLATE_ID),
            Requester = SessionActor.ActorRef
        };

        zoneActor.Tell(spawnMsg, Self);
        _summonedPetId = coreObj.m_globalID;
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_RESUMMONPET))]
    private void ReceiveResummonPet(CHARACTER_103_PROTOCOL.MSG_RESUMMONPET message) {
        // A pet that is not out picks the change up on its next summon.
        if (_summonedPetId == 0) {
            return;
        }

        var equipment = GetActiveWizard()?.EquipmentBehavior;
        if (equipment is null || equipment.GetEquippedPetId() != message.PetItemId) {
            return;
        }

        var pet = equipment.GetItem(message.PetItemId);
        if (!ReWearIfNamed(pet)) {
            SpawnPetEntity(pet);
        }
    }

    /// <summary>Asks this service to take a named pet off and put it back on, as <see cref="ReWearIfNamed"/> says.</summary>
    internal sealed record ReWearNamedPet(ulong PetItemId);

    private static readonly TimeSpan ReWearDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A pet named with .name pet glided (no walk or idle), showed far too big and wisps weren't picked up after logging
    /// in or renaming, until it was taken off and put back on, which keeps its name (Niduus, 2026-10-22). Sending only
    /// MSG_EQUIPITEM, or only the public equip notice, before the summon didn't help, so the server now does the whole
    /// take-off and put-on itself a moment later, when the game has loaded the wizard. False when the pet has no name.
    /// </summary>
    private bool ReWearIfNamed(WizClientObjectItem pet) {
        var equipment = GetActiveWizard()?.EquipmentBehavior;
        pet ??= equipment?.GetEquippedPetId() is { } id and not 0 ? equipment.GetItem(id) : null;
        if (pet is null || !CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out var petName)
                        || petName.m_wsNameOverride.ToString() is not { Length: > 0 }) {
            return false;
        }

        DismissPetEntity();
        Context.System.Scheduler.ScheduleTellOnce(ReWearDelay, Self, new ReWearNamedPet(pet.m_globalID), Self);

        return true;
    }

    [InternalMessageHandler(typeof(ReWearNamedPet))]
    private void ReceiveReWearNamedPet(ReWearNamedPet message) {
        try {
            var equipment = GetActiveWizard()?.EquipmentBehavior;
            if (equipment is null || equipment.GetEquippedPetId() != message.PetItemId || GetActiveGameObject() is null) {
                return;
            }

            // The game is told the pet came off, and a moment later that it went back on, like the player's own two
            // clicks. The server keeps the pet worn throughout, so a zone change in between loses nothing.
            var slot = equipment.GetSlotOfItem(message.PetItemId);
            SendUnequipItem("Pet", slot, message.PetItemId);
            DismissPetEntity();
            Context.System.Scheduler.ScheduleTellOnce(PutBackOnDelay, Self, new PutNamedPetBackOn(message.PetItemId), Self);
            Logger.Information("Took named pet {0} off; it goes back on in a moment so the game animates it.", Logger.Args(message.PetItemId));
        }
        catch (Exception ex) {
            Logger.Error("Error while taking the named pet off: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    /// <summary>Puts a named pet that <see cref="ReceiveReWearNamedPet"/> took off back on.</summary>
    internal sealed record PutNamedPetBackOn(ulong PetItemId);

    private static readonly TimeSpan PutBackOnDelay = TimeSpan.FromSeconds(1);

    [InternalMessageHandler(typeof(PutNamedPetBackOn))]
    private void ReceivePutNamedPetBackOn(PutNamedPetBackOn message) {
        try {
            var equipment = GetActiveWizard()?.EquipmentBehavior;
            if (equipment is null || equipment.GetEquippedPetId() != message.PetItemId || GetActiveGameObject() is null) {
                return;
            }

            var pet = equipment.GetItem(message.PetItemId);
            equipment.MoveSlotToEnd(message.PetItemId);
            SendEquipItem(pet, "Pet");
            SpawnPetEntity(pet);
            Logger.Information("Put named pet {0} back on.", Logger.Args(message.PetItemId));
        }
        catch (Exception ex) {
            Logger.Error("Error while putting the named pet back on: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    /// <summary>
    /// After the mount comes off, a pet that came out under its normal name while the wizard rode (SpawnPetEntity) is
    /// taken off and put back on, so it shows its .name pet name again.
    /// </summary>
    private void RespawnNamedPetUnmounted() {
        if (_summonedPetId != 0 && !IsMounted()) {
            ReWearIfNamed(null);
        }
    }

    private bool IsMounted()
        => GetActiveWizard()?.EquipmentBehavior?.GetItemInSlot(EquipmentSlotType.Mount) is not null;

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_SYNC_PET_FOR_MOUNT))]
    private void ReceiveSyncPetForMount()
        => SpawnPetEntity();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_PETFORMCHANGED))]
    private void ReceivePetFormChanged(ZONE_102_PROTOCOL.MSG_PETFORMCHANGED message) {
        if (message.On) {
            DismissPetEntity();
        }
        else {
            SpawnPetEntity();
        }
    }

    // The game's Play as Pet button (beside the pet's picture) sends MSG_SWITCHTOPET. It does what `.petform` does,
    // the transformation that is known to work; what the real server answers with is not known yet.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_SWITCHTOPET))]
    private void ReceiveSwitchToPet(WIZARD2_53_PROTOCOL.MSG_SWITCHTOPET message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        Logger.Information("Play as Pet: {0} pressed the button (GlobalID {1}, Status {2}, PetTemplate {3}).",
            Logger.Args(wizard.CharId, message.GlobalID, message.Status, message.PetTemplate));
        Commands.Protocols.CommandPetFormProtocol.Toggle(wizard, SessionActor.ActorRef, SessionActor.GetZoneActor(),
            text => InformGameClient(text));
    }

    // A wizard in their pet's look (.petform) keeps it in the new zone, for the players there too.
    private void ShowPetFormToZone(Wizard wizard) {
        var form = wizard.GameEffects.Find(e => e is TransformationEffect && e.m_itemSlotID == Commands.Protocols.CommandPetFormProtocol.SlotId);
        var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (form is null || !_effectSerializer.Serialize(form, flags, out var data)) {
            return;
        }

        SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = wizard.GameObjectID, EffectData = data },
            Selfless = true,
            Sender = SessionActor.ActorRef,
            Targets = ZoneBroadcastTarget.Players,
        }, Self);
    }

    private void DismissPetEntity() {
        if (_summonedPetId == 0) {
            return;
        }

        // Sent straight to the zone, like the spawn, so a dismiss never overtakes the spawn it targets.
        SessionActor.GetZoneActor()?.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_DISMISSPET { PetGlobalId = _summonedPetId }],
            Targets = ZoneBroadcastTarget.Objects,
        }, Self);

        _summonedPetId = 0;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT))]
    private void ReceiveEnforceInteriorMount(ZONE_102_PROTOCOL.MSG_ENFORCEINTERIORMOUNT message) {
        // Zone data flags disallow mounts (m_noMounts): really unequip on entry (model and speed effect) and
        // re-equip on leaving. The stowed GID is persisted so it survives the zone transfer. Force (a
        // dungeon-sigil pad on a street) stows the mount regardless of the zone's no-mounts flag.
        try {
            var wizard = GetActiveWizard();
            var equip = wizard.EquipmentBehavior;

            if (message.Force || ZoneDisallowsMounts()) {
                var mount = equip.GetItemInSlot(EquipmentSlotType.Mount);
                if (mount is null) {
                    return; // not mounted, nothing to dismount
                }

                var slot = equip.GetSlotOfItem(mount.m_globalID);
                if (!wizard.EquipmentToInventoryTransfer(mount.m_globalID, out var removedEffects)) {
                    Logger.Warning("Interior dismount transfer failed for mount {0}", Logger.Args(mount.m_globalID));

                    return;
                }

                wizard.InteriorStowedMountId = mount.m_globalID;
                WizardCollection.UpdateCharacterInteriorStowedMount(wizard);
                SendUnequipItem("Mount", slot, mount.m_globalID); // drops the model client-side
                SendRemoveEffects(removedEffects);                // drops the mount speed effect
                if (s_hidePetWhileMounted) {
                    SpawnPetEntity();
                }
                else {
                    RespawnNamedPetUnmounted();
                }
            }
            else if (wizard.InteriorStowedMountId != 0) {
                var gid = wizard.InteriorStowedMountId;
                wizard.InteriorStowedMountId = 0;
                WizardCollection.UpdateCharacterInteriorStowedMount(wizard);

                if (!wizard.InventoryToEquipmentTransfer(gid, out var addedEffects, out _)) {
                    Logger.Warning("Outdoor remount transfer failed for mount {0}", Logger.Args(gid));

                    return;
                }

                var item = equip.GetItemInSlot(EquipmentSlotType.Mount);
                if (item is not null) {
                    SendEquipItem(item, "Mount");
                }
                SendAddEffects(addedEffects); // restores the mount speed
                if (s_hidePetWhileMounted) {
                    DismissPetEntity();
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Error while reconciling the mount for the zone: {0} {1}",
                Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    private bool ZoneDisallowsMounts() {
        var zoneActor = SessionActor.GetZoneActor();
        if (zoneActor is null) {
            return false;
        }

        var rsp = zoneActor.Ask<ZONE_102_PROTOCOL.MSG_QUERYZONEDATARSP>(
            new ZONE_102_PROTOCOL.MSG_QUERYZONEDATA(), TimeSpan.FromSeconds(5)).Result;

        return rsp?.ZoneData?.m_noMounts ?? false;
    }

    private void SendEquipItem(WizClientObjectItem item, string slotName) {
        // Confirm to the player that we've equipped their item server side.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM() {
            ItemID = item.m_globalID,
            SlotName = slotName,
            IsEquip = 1
        });

        // Serialize item and broadcast equip action to other players.
        var pubItem = ItemHelper.GetPublicItem(item);

        if (!_itemSerializer.Serialize(pubItem, 1, out var data)) {
            Logger.Error("Failed to serialize item {0} for equip broadcast.",
                Logger.Args(item.m_globalID));

            return;
        }

        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICEQUIPITEM() {
            GlobalID = GetActiveGameObject().m_globalID,
            SerializedInfo = data
        }, false);
    }

    private void SendUnequipItem(ByteString slotName, byte slot, ulong itemId) {
        // This one goes to the client.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM() {
            ItemID = itemId,
            SlotName = slotName,
            IsEquip = 0
        });

        // This one goes to the zone.
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_PUBLICUNEQUIPITEM() {
            GlobalID = GetActiveGameObject().m_globalID,
            IndexToRemove = slot
        }, false);
    }

    private void SendAddEffects(List<GameEffectBase> effects) {
        if (effects is null || effects.Count == 0) {
            return;
        }

        // This may fail since it is accessed immediately after attach. This means the CharacterService
        // hasn't had enough time to set its Wizard reference yet.
        var wizardObj = GetActiveGameObject();
        if (wizardObj is null) {
            wizardObj = GetActiveWizard()?.GetInitializedGameObject();

            if (wizardObj is null) {
                throw new ServiceRetryException("Failed to get active game object for add effects broadcast.");
            }
        }

        var charObjId = wizardObj.m_globalID;

        foreach (var effect in effects) {
            var flags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
            if (!_effectSerializer.Serialize(effect, flags, out var effectSerializedData)) {
                Logger.Error("Failed to serialize effect {0} for add effects broadcast.",
                    Logger.Args(effect.m_effectNameID));

                continue;
            }

            SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT() {
                GameObjectID = charObjId,
                EffectData = effectSerializedData
            });
        }
    }

    // The worn pet's and jewels' cards, which EquipmentExtras puts on and takes off with the gear.
    private void SendCardEffectChanges(Wizard wizard) {
        var (removed, added) = EquipmentExtras.TakeCardEffectChanges(wizard);
        SendRemoveEffects(removed);
        SendAddEffects(added);
    }

    // Energy above the new maximum (gear that gave energy came off) drops to it.
    private void CapEnergy(Wizard wizard) {
        if (!wizard.CapEnergyAtMax()) {
            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
            GlobalID = wizard.GameObjectID,
            Energy = wizard.PetOwnerBehavior.Energy,
            MaxEnergy = wizard.GameStats.GetClientTypeAlternative().m_energyMax,
            TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch
        });
    }

    private void SendRemoveEffects(List<GameEffectBase> effects) {
        var charObjId = GetActiveGameObject().m_globalID;

        foreach (var effect in effects) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT() {
                GameObjectID = charObjId,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }
    }

    private void EquipElixirFromInventory(Wizard wizard,
                                          WizClientObjectItem item,
                                          WizItemTemplate itemTemplate,
                                          ElixirBehaviorTemplate elixirTemplate) {
        uint durationSeconds = 0;
        if (!string.IsNullOrEmpty(elixirTemplate.m_expireTime)) {
            _ = uint.TryParse((string) elixirTemplate.m_expireTime, out durationSeconds);
        }

        // Case 1: Instant refill (duration == 0).
        if (durationSeconds == 0) {
            if (!wizard.InventoryBehavior.RemoveItem(item.m_globalID, out _)) {
                return;
            }

            WizardItemCollection.RemoveItem(wizard.CharId, item.m_globalID);
            WizardCollection.UpdateCharacterItems(wizard);

            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                GlobalID = wizard.GameObjectID,
                ItemID = item.m_globalID
            });

            // A result the server types do not know reads as null (the Hatching Elixir's), so only the known ones run.
            var results = elixirTemplate.m_equipActionList?.m_results?.Where(result => result is not null).ToList();
            if (results?.Count > 0) {
                ResultDispatcher.ExecuteResults(
                    Context,
                    new ResultList { m_results = results },
                    SessionActor.ActorRef,
                    wizard.GetInitializedGameObject());
            }

            // The Hatching Elixir hatches every egg waiting in the hatching slots.
            if (PetElixirs.HatchesAllEggs(itemTemplate)) {
                var eggs = wizard.PetOwnerBehavior?.MorphingSlots?.ToList() ?? [];
                Logger.Information("{0} used the Hatching Elixir {1}: hatching {2} eggs.",
                    Logger.Args(wizard.CharId, item.m_templateID, eggs.Count));
                foreach (var egg in eggs) {
                    TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_DOEGGHATCH { EggGlobalId = egg.m_globalID });
                }
            }

            PlaySound(elixirTemplate.m_equipSound);
            SyncHealthAndMana(wizard);
            return;
        }

        // Case 2: Timed elixir.
        var equipBehavior = wizard.EquipmentBehavior;
        if (!equipBehavior.CanEquipElixir(elixirTemplate)) {
            Logger.Warning("Player tried to equip elixir {0} but cannot equip it.", Logger.Args(item.m_globalID));
            return;
        }

        if (!wizard.InventoryBehavior.RemoveItem(item.m_globalID, out _)) {
            return;
        }

        if (CoreObjectFactory.FindBehaviorInstance<ClientElixirBehavior>(item, out var clientElixir)) {
            clientElixir.m_expireTime = durationSeconds;
            clientElixir.m_statsApplied = true;
        }

        if (!equipBehavior.EquipElixir(item, durationSeconds)) {
            wizard.InventoryBehavior.AddItem(item);
            return;
        }

        var addedEffects = CharacterEffectHelper.AddEffectsToWizard(wizard, itemTemplate);

        WizardCollection.UpdateCharacterItems(wizard);

        // Transfer the item from ClientInventoryBehavior to ClientEquipmentBehavior on the client.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = item.m_globalID,
            SlotName = "Elixir",
            IsEquip = 1
        });

        SendAddEffects(addedEffects);

        // Start HUD countdown timer (which also dispatches EQUIPMENT_EquipItem on the client) and enable HUD icon.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
            GlobalID = item.m_globalID,
            TimerTime = durationSeconds
        });

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
            parentID = item.m_globalID,
            EffectEnabled = 1
        });

        PlaySound(elixirTemplate.m_equipSound);
        SyncHealthAndMana(wizard);
        StartElixirPlaytimeTicker();
    }

    private void RestoreActiveElixirs(Wizard wizard) {
        var equipment = wizard.EquipmentBehavior;
        if (equipment?.SlotList is null) {
            return;
        }

        equipment.SyncActiveElixirTimes();

        var expiredSlots = new List<EquipmentSlot>();
        foreach (var (slot, _) in equipment.GetActiveElixirs()) {
            if (slot.RemainingSeconds > 0) {
                // Resume client HUD timer and icon.
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
                    GlobalID = slot.ItemId.Value,
                    TimerTime = slot.RemainingSeconds
                });

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
                    parentID = slot.ItemId.Value,
                    EffectEnabled = 1
                });
            }
            else {
                expiredSlots.Add(slot);
            }
        }

        foreach (var expiredSlot in expiredSlots) {
            ExpireElixir(wizard, expiredSlot);
        }
    }

    private void StartElixirPlaytimeTicker() {
        if (_isElixirTickerStarted) {
            return;
        }

        Timers.StartPeriodicTimer(
            ELIXIR_TICK_TIMER_KEY,
            new CHARACTER_103_PROTOCOL.MSG_DOELIXIRTICK(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        _isElixirTickerStarted = true;
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_DOELIXIRTICK))]
    private void ReceiveElixirPlaytimeTick(CHARACTER_103_PROTOCOL.MSG_DOELIXIRTICK message) {
        var wizard = GetActiveWizard();
        var equipment = wizard?.EquipmentBehavior;
        if (equipment?.SlotList is null) {
            return;
        }

        var activeElixirs = equipment.GetActiveElixirs().ToList();
        if (activeElixirs.Count == 0) {
            return;
        }

        var stateChanged = false;
        var expiredSlots = new List<EquipmentSlot>();

        foreach (var (slot, item) in activeElixirs) {
            if (slot.RemainingSeconds > 0) {
                slot.RemainingSeconds--;

                if (CoreObjectFactory.FindBehaviorInstance<ClientElixirBehavior>(item, out var clientElixir)) {
                    clientElixir.m_expireTime = slot.RemainingSeconds;
                }

                stateChanged = true;

                if (slot.RemainingSeconds == 0) {
                    expiredSlots.Add(slot);
                }
            }
            else {
                expiredSlots.Add(slot);
            }
        }

        foreach (var expiredSlot in expiredSlots) {
            ExpireElixir(wizard, expiredSlot);
        }

        if (stateChanged) {
            _secondsSinceLastElixirSave++;
            if (_secondsSinceLastElixirSave >= ELIXIR_SAVE_INTERVAL_SECONDS) {
                _secondsSinceLastElixirSave = 0;
                WizardCollection.UpdateCharacterItems(wizard);
            }
        }
    }

    private void ExpireElixir(Wizard wizard, EquipmentSlot slot) {
        var itemId = slot.ItemId.Value;
        Logger.Information("Elixir {0} ({1}) expired for character {2}",
            Logger.Args(slot.ItemName, itemId, wizard.CharId));

        // Destroy client HUD button and unequip item in client.
        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = itemId
        });

        // Remove effects from wizard and client.
        var item = wizard.EquipmentBehavior.GetItem(itemId);
        if (item is not null) {
            var template = ItemHelper.GetItemTemplate(item);
            if (template is not null) {
                var removedEffects = CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template);
                if (removedEffects is { Count: > 0 }) {
                    SendRemoveEffects(removedEffects);
                }

                if (template.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault() is { } elixirTemplate) {
                    PlaySound(elixirTemplate.m_unequipSound);
                }
            }
        }

        // Remove from server equipment behavior and database.
        wizard.EquipmentBehavior.UnequipItem(itemId);
        WizardItemCollection.RemoveItem(wizard.CharId, itemId);
        WizardCollection.UpdateCharacterItems(wizard);

        SyncHealthAndMana(wizard, clampToMax: true);
    }

    private void SyncHealthAndMana(Wizard wizard, bool clampToMax = false) {
        var clientStats = wizard.GameStats.GetClientTypeAlternative();
        if (clampToMax) {
            CapEnergy(wizard);
            if (wizard.GameStats.m_currentHitpoints > clientStats.m_baseHitpoints) {
                wizard.GameStats.m_currentHitpoints = clientStats.m_baseHitpoints;
            }

            if (wizard.GameStats.m_currentMana > clientStats.m_baseMana) {
                wizard.GameStats.m_currentMana = clientStats.m_baseMana;
            }
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = wizard.GameObjectID,
            NewHealth = wizard.GameStats.m_currentHitpoints,
            NewHealthMax = clientStats.m_baseHitpoints,
            DisplayDiff = 1
        });
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
            Mana = wizard.GameStats.m_currentMana,
            MaxMana = clientStats.m_baseMana,
            DisplayDiff = 1
        });
    }

    protected override void OnPreDispose() {
        Timers.Cancel(ELIXIR_TICK_TIMER_KEY);

        try {
            var wizard = GetActiveWizard();
            if (wizard?.EquipmentBehavior?.SlotList is not null
                && wizard.EquipmentBehavior.SlotList.Any(s => s.SlotType == EquipmentSlotType.Elixir)) {
                WizardCollection.UpdateCharacterItems(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Failed to persist elixirs during pre-dispose: {0}", Logger.Args(ex.Message));
        }

        base.OnPreDispose();
    }

}

