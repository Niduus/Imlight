/*
 * The game's unlock of a locked pet Will Cast (MSG_UNLOCKPETTALENT; Niduus, 2026-10-27), see Game/Pet/PetWillCasts.cs.
 */

using System;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Services;

internal partial class PetService {

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UNLOCKPETTALENT))]
    private void ReceiveUnlockPetTalent(WIZARD2_53_PROTOCOL.MSG_UNLOCKPETTALENT message) {
        Logger.Information("Session {0} | Unlock pet talent: pet {1}, derby {2}, talent {3}, new talent {4}.",
            Logger.Args(SessionActor.SessionID, message.PetGID, message.DerbyTalent, message.TalentID, message.NewTalentID));
        try {
            var wizard = GetActiveWizard();
            var pet = message.PetGID != 0 ? wizard?.FindPetItem(message.PetGID) : PetWillCasts.WornPet(wizard);
            if (wizard is null || pet is null) {
                InformGameClient("That pet was not found.");

                return;
            }

            var unlocked = PetWillCasts.Unlock(SessionActor.ActorRef, wizard, pet, message.TalentID, out var problem);
            if (unlocked is null) {
                InformGameClient(problem);

                return;
            }

            // Sent back as the answer, so the game's unlock window knows it worked.
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UNLOCKPETTALENT {
                PetGID = message.PetGID,
                DerbyTalent = message.DerbyTalent,
                TalentID = message.TalentID,
                NewTalentID = PetData.TalentId(unlocked),
            });
            var name = PetData.GetTalent(unlocked) is Imcodec.ObjectProperty.TypeCache.PetTalentTemplate talent
                && PetWillCasts.Resolve(talent) is { } willCast ? willCast.ShownName : unlocked;
            InformGameClient($"Unlocked the {name} Will Cast{(PetWillCasts.FreeReagents ? " (free on this server)" : "")}.");
        }
        catch (Exception e) {
            Logger.Error("Unlocking a pet talent failed: {0}", Logger.Args(e));
        }
    }

}
