/*
 * PET STITCH
 *
 * Niduus (2026-10-20): Prospector Zeke stitches pets like Eloise stitches gear. One pet keeps its stats,
 * level and talents and takes the look of the other, which is used up. Unstitching gives the look back as
 * a new baby pet. The pet is changed in place (a fresh pet from its template would lose its talents).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Seamstress;

internal sealed class PetStitch(Wizard wizard, IActorRef session, Func<ulong, bool> unequip) {

    internal static bool IsPet(WizClientObjectItem item)
        => item is not null && PetFactory.IsPetTemplate((uint) item.m_templateID);

    internal bool TryStitch(WizClientObjectItem look, WizClientObjectItem stats, int crowns, out string failure) {
        if (!IsPet(look) || !IsPet(stats)) {
            failure = "only two pets can be stitched together";

            return false;
        }

        if (!TryPrepare([look, stats], 0, crowns, out failure)) {
            return false;
        }

        var before = (stats.m_displayID, stats.m_primaryColor, stats.m_secondaryColor, stats.m_pattern);
        stats.m_displayID = ItemHelper.GetDisplayTemplateId(look);
        stats.m_primaryColor = look.m_primaryColor;
        stats.m_secondaryColor = look.m_secondaryColor;
        stats.m_pattern = look.m_pattern;
        if (!wizard.InventoryBehavior.RemoveItem(look)) {
            (stats.m_displayID, stats.m_primaryColor, stats.m_secondaryColor, stats.m_pattern) = before;
            failure = $"pet {look.m_globalID.Full} is not in the backpack";

            return false;
        }

        if (!Save(crowns, [stats], [look.m_globalID])) {
            (stats.m_displayID, stats.m_primaryColor, stats.m_secondaryColor, stats.m_pattern) = before;
            wizard.InventoryBehavior.AddItem(look);
            failure = "the pets could not be stored";

            return false;
        }

        Logger.Information("[Seamstress] Pet {0} of {1} now looks like template {2} (pet {3} used up).",
            Logger.Args(stats.m_globalID.Full, wizard.CharId, (uint) stats.m_displayID, look.m_globalID.Full));
        Refresh(stats);
        ItemSync.SendRemoved(session, wizard, look.m_globalID);
        SendCrowns(crowns);

        return true;
    }

    internal bool TryUnstitch(WizClientObjectItem pet, int color1, int color2, int crowns, out string failure) {
        if (!IsPet(pet) || !ItemHelper.IsStitched(pet)) {
            failure = $"pet {pet?.m_globalID.Full} is not stitched";

            return false;
        }

        var lookId = (uint) pet.m_displayID;
        var look = PetFactory.CreateHatchedPet(wizard.CharId, lookId);
        if (look is null) {
            failure = $"pet template {lookId} could not be built";

            return false;
        }

        if (!TryPrepare([pet], 1, crowns, out failure)) {
            return false;
        }

        look.m_primaryColor = pet.m_primaryColor;
        look.m_secondaryColor = pet.m_secondaryColor;
        look.m_pattern = pet.m_pattern;
        var before = (pet.m_displayID, pet.m_primaryColor, pet.m_secondaryColor, pet.m_pattern);
        pet.m_displayID = 0;
        pet.m_primaryColor = color1;
        pet.m_secondaryColor = color2;
        pet.m_pattern = 0;
        if (!ItemSync.TrySerialize(look, out _) || !wizard.InventoryBehavior.AddItem(look)) {
            (pet.m_displayID, pet.m_primaryColor, pet.m_secondaryColor, pet.m_pattern) = before;
            failure = "the look pet could not be added";

            return false;
        }

        if (!Save(crowns, [pet, look], [])) {
            (pet.m_displayID, pet.m_primaryColor, pet.m_secondaryColor, pet.m_pattern) = before;
            wizard.InventoryBehavior.RemoveItem(look);
            failure = "the pets could not be stored";

            return false;
        }

        Logger.Information("[Seamstress] Pet {0} of {1} unstitched; its look came back as pet {2} (template {3}).",
            Logger.Args(pet.m_globalID.Full, wizard.CharId, look.m_globalID.Full, lookId));
        Refresh(pet);
        ItemSync.SendAdded(session, wizard, look);
        SendCrowns(crowns);

        return true;
    }

    // Worn pets come off first: the client is told about the changed pet as a new backpack item.
    private bool TryPrepare(IReadOnlyList<WizClientObjectItem> pets, int newPets, int crowns, out string failure) {
        var worn = pets.Where(pet => wizard.EquipmentBehavior.HasItemEquipped(pet.m_globalID)).ToList();
        if (wizard.InventoryBehavior.FreeSlots < worn.Count + newPets) {
            failure = $"{worn.Count + newPets} free backpack slots are needed";

            return false;
        }

        if (wizard.Account.Crowns < crowns) {
            failure = $"{crowns} Crowns are needed";

            return false;
        }

        foreach (var pet in worn) {
            if (!unequip(pet.m_globalID)) {
                failure = $"pet {pet.m_globalID.Full} could not be taken off";

                return false;
            }
        }

        failure = string.Empty;

        return true;
    }

    private bool Save(int crowns, IReadOnlyCollection<WizClientObjectItem> changed, IReadOnlyCollection<ulong> removed) {
        var account = wizard.Account;
        account.Crowns -= crowns;
        if (AccountCollection.SaveCrownsWithBackpack(account, wizard, changed, removed)) {
            return true;
        }

        account.Crowns += crowns;

        return false;
    }

    private void Refresh(WizClientObjectItem pet) {
        ItemSync.SendRemoved(session, wizard, pet.m_globalID);
        if (!ItemSync.SendAdded(session, wizard, pet)) {
            Logger.Error("[Seamstress] Could not tell the client about pet {0}; a relog shows it.", Logger.Args(pet.m_globalID.Full));
        }
    }

    private void SendCrowns(int crowns) {
        if (crowns > 0) {
            session.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                Failure = 0,
                TotalCrowns = wizard.Account.Crowns,
                CharacterID = wizard.CharId,
                CacheBalanceForCSSegmentation = 1
            });
        }
    }

}
