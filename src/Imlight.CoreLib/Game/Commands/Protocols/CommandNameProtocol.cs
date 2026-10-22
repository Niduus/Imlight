/*
 * Renames your wizard or the pet you are wearing (Niduus, 2026-10-20):
 *   .name self Walter Blanco    .name pet Sir Barksalot    .name self reset    .name pet reset
 */

using System;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandNameProtocol : CommandProtocol {

    private const int MAX_LENGTH = 40;

    internal override string Group { get; set; } = "";

    [Command("name")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void NameCommand([Remainder] string text) {
        var words = (text ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var who = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        if (who is not ("self" or "me" or "pet") || words.Length < 2) {
            InformSenderClient("Type .name self <new name> or .name pet <new name>. Use reset as the name to go back to the old one.");

            return;
        }

        var name = words[1].Trim();
        var reset = name.Equals("reset", StringComparison.OrdinalIgnoreCase);
        if (name.Length > MAX_LENGTH) {
            InformSenderClient($"That name is too long. Keep it to {MAX_LENGTH} letters.");

            return;
        }

        if (who == "pet") {
            RenamePet(reset ? "" : name);

            return;
        }

        Context.Character.SetNameOverride(reset ? "" : name);
        InformSenderClient(reset ? "Your name is back to normal. Relog to see it." : $"Your name is now {name}. Relog to see it.");
    }

    private void RenamePet(string name) {
        var pet = PetCare.GetWornPet(Context.Character);
        if (pet is null || !CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out var petName)) {
            InformSenderClient("Wear a pet first.");

            return;
        }

        petName.m_wsNameOverride = name;
        WizardItemCollection.SaveItemBehaviors(pet);

        // Summoned again so the new name shows over its head.
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_RESUMMONPET { PetItemId = pet.m_globalID }, ActorRefs.NoSender);
        InformSenderClient(name.Length == 0 ? "Your pet's name is back to normal." : $"Your pet is now named {name}.");
    }

}
