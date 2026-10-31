using System;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

/// <summary>Asks the session to put on a quest's pet (ResEquipItem with category Pet).</summary>
internal sealed class WearQuestPet : IServerMessage {
    public byte MessageOrder { get; } = 5;
    public byte ServiceID { get; } = 105;
    public ulong TemplateId { get; init; }
}

/// <summary>Asks the session to turn the wizard into a quest's pet (ResForcePlayerAsPet).</summary>
internal sealed class BecomeQuestPet : IServerMessage {
    public byte MessageOrder { get; } = 6;
    public byte ServiceID { get; } = 105;
    public ulong TemplateId { get; init; }
}

/// <summary>
/// Secret Strife of Pets (WC-OLDE-MAIN-001, Niduus 2026-10-31: "a talk to pets trigger that is not active"): Olde Town's
/// triggers put the school's pet on (ResEquipItem) and turn the wizard into it (ResForcePlayerAsPet), so the wizard can
/// talk to the pets there. The triggers name the quest's "_Gen1" pets; the wizard's own pet of the same name counts, and
/// one is given when the wizard has none.
/// </summary>
internal partial class EquipmentService {

    [InternalMessageHandler(typeof(WearQuestPet))]
    private void ReceiveWearQuestPet(WearQuestPet message) {
        try {
            WearQuestPetNow(message.TemplateId);
        }
        catch (Exception ex) {
            Logger.Error("Error while putting the quest pet on: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    [InternalMessageHandler(typeof(BecomeQuestPet))]
    private void ReceiveBecomeQuestPet(BecomeQuestPet message) {
        try {
            if (!WearQuestPetNow(message.TemplateId)) {
                return;
            }

            var wizard = GetActiveWizard();
            if (!Commands.Protocols.CommandPetFormProtocol.IsInPetForm(wizard)) {
                Commands.Protocols.CommandPetFormProtocol.Toggle(wizard, SessionActor.ActorRef, SessionActor.GetZoneActor(),
                    text => InformGameClient(text));
            }
        }
        catch (Exception ex) {
            Logger.Error("Error while turning the wizard into the quest pet: {0} {1}", Logger.Args(ex.Message, ex.StackTrace));
        }
    }

    // Puts the pet on unless it is worn already. False when the wizard ends up without it.
    private bool WearQuestPetNow(ulong templateId) {
        var wizard = GetActiveWizard();
        if (wizard is null || templateId == 0) {
            return false;
        }

        var equipment = wizard.EquipmentBehavior;
        var wornId = equipment.GetEquippedPetId();
        if (wornId != 0 && equipment.GetItem(wornId) is { } worn && IsSamePet(worn.m_templateID, templateId)) {
            return true;
        }

        var pet = wizard.InventoryBehavior.Items.FirstOrDefault(i => i.m_templateID == templateId)
                  ?? wizard.InventoryBehavior.Items.FirstOrDefault(i => IsSamePet(i.m_templateID, templateId));
        if (pet is null) {
            if (!wizard.TryCreateItem(templateId, out var created)) {
                Logger.Warning("Could not make quest pet {0} for {1}.", Logger.Args(templateId, wizard.CharId));

                return false;
            }

            var placement = ItemPlacement.Place(wizard, created);
            ItemPlacement.Announce(SessionActor.ActorRef, wizard, created, placement);
            if (placement != Placement.Backpack) {
                return false;
            }

            pet = created;
            Logger.Information("Gave {0} quest pet {1}.", Logger.Args(wizard.CharId, templateId));
        }

        EquipItem(new GAME_5_PROTOCOL.MSG_EQUIPITEM { ItemID = pet.m_globalID, SlotName = "Pet", IsEquip = 1 });

        return equipment.GetEquippedPetId() == pet.m_globalID;
    }

    // The quest's "_Gen1" pet and the one the quest gives share their name (Cinder Serpent, Clever Cogs and so on).
    private static bool IsSamePet(ulong a, ulong b) {
        if (a == b) {
            return true;
        }

        if (CoreObjectFactory.GetCoreTemplate(a) is not WizItemTemplate ta || CoreObjectFactory.GetCoreTemplate(b) is not WizItemTemplate tb
            || ta.m_behaviors?.OfType<PetItemBehaviorTemplate>().Any() != true) {
            return false;
        }

        var name = $"{ta.m_displayName}";

        return name.Length > 0 && name == $"{tb.m_displayName}";
    }

}
