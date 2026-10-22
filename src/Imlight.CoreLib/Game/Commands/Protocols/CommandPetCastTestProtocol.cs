/*
 * `.petcasttest <number>` picks how pet May Casts are sent, without restarting the server; `.petcasttest off` goes back
 * to the [PetCasting] switches. For finding the way the game plays a pet's cast (Niduus, 2026-10-20).
 */

using System.Linq;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandPetCastTestProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("petcasttest")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PetCastTestCommand(string value) {
        var text = value.Trim().ToLowerInvariant();
        if (text == "off") {
            PetMayCasts.TestMode = null;
            InformSenderClient("Pet cast test is off.");

            return;
        }

        if (int.TryParse(text, out var mode) && mode >= 0 && mode < PetMayCasts.TestModes.Length) {
            PetMayCasts.TestMode = mode;
            InformSenderClient($"Pet casts are now sent as: {PetMayCasts.TestModes[mode]}. Start a new battle to try it.");

            return;
        }

        InformSenderClient("Use .petcasttest 0 to " + (PetMayCasts.TestModes.Length - 1) + ", or off: "
            + string.Join("; ", PetMayCasts.TestModes.Select((m, i) => $"{i} = {m}")));
    }

}
