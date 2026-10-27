/*
 * `.willcast` (Niduus, 2026-10-27): the pet's Will Casts from chat, for anyone, next to the game's own Will Cast button.
 *   .willcast              lists them: in a battle whether each is ready, out of one what each costs and does.
 *   .willcast <number>     in a battle, casts that one before your card this round (what the button does).
 *   .willcast unlock [n]   unlocks a locked one with its reagents (all locked ones without a number).
 * See Game/Pet/PetWillCasts.cs.
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandWillCastProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("willcast")]
    private void WillCastCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        if (!PetWillCasts.Enabled) {
            InformSenderClient("Will Casts are turned off on this server.");

            return;
        }

        var pet = PetWillCasts.WornPet(wizard);
        var willCasts = PetWillCasts.ForPet(pet);
        if (willCasts.Count == 0) {
            InformSenderClient(pet is null ? "Wear a pet to use its Will Casts." : "Your pet has no Will Casts.");

            return;
        }

        var parts = (text ?? "").Trim().ToLowerInvariant().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0] == "unlock") {
            Unlock(wizard, pet, willCasts, parts.Length > 1 ? parts[1] : null);

            return;
        }

        if (parts.Length > 0 && int.TryParse(parts[0], out var number)) {
            if (number < 1 || number > willCasts.Count) {
                InformSenderClient($"Your pet has {willCasts.Count} Will Cast{(willCasts.Count == 1 ? "" : "s")}; use .willcast 1 to {willCasts.Count}.");

                return;
            }

            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_PETWILLCASTREQUEST { Talent = willCasts[number - 1].TalentId });

            return;
        }

        if (wizard.IsInDuel) {
            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_PETWILLCASTREQUEST { List = true });

            return;
        }

        InformSenderClient("Your pet's Will Casts: " + string.Join("; ", willCasts.Select((w, i) => $"{i + 1}. {Describe(w)}"))
            + ". In a battle, press the pet's Will Cast button or type .willcast <number>.");
    }

    private static string Describe(PetWillCasts.WillCast willCast) {
        var parts = new List<string>();
        if (willCast.Locked) {
            parts.Add(PetWillCasts.FreeReagents ? "locked, works anyway (free on this server)"
                : "locked, unlock with " + string.Join(", ", PetWillCasts.UnlockReagents(willCast.Power)
                    .Select(need => $"{need.Amount} {PetWillCasts.ItemName(need.TemplateId)}")) + " (.willcast unlock)");
        }

        parts.Add(willCast.RampUp > 1 ? $"from round {willCast.RampUp}" : "from round 1");
        parts.Add($"wait {willCast.Cooldown} rounds after a use");
        parts.Add(willCast.UsesPerCombat > 0 ? $"{willCast.UsesPerCombat} uses a battle" : "no use limit");
        if (PetWillCasts.CostsHappiness && willCast.HappinessCost > 0) {
            parts.Add($"{willCast.HappinessCost} happiness a use");
        }

        if (willCast.NoPvP) {
            parts.Add("not in PvP");
        }

        return $"{willCast.ShownName} ({string.Join(", ", parts)})";
    }

    private void Unlock(WizardData.Models.Player.Wizard wizard, Imcodec.ObjectProperty.TypeCache.WizClientObjectItem pet,
                        List<PetWillCasts.WillCast> willCasts, string which) {
        var locked = willCasts.Where(w => w.Locked).ToList();
        if (which is not null) {
            if (!int.TryParse(which, out var number) || number < 1 || number > willCasts.Count) {
                InformSenderClient($"Use .willcast unlock 1 to {willCasts.Count}, or .willcast unlock for every locked one.");

                return;
            }

            locked = willCasts[number - 1].Locked ? [willCasts[number - 1]] : [];
        }

        if (locked.Count == 0) {
            InformSenderClient(which is null ? "Every Will Cast of your pet is already unlocked." : "That Will Cast is already unlocked.");

            return;
        }

        foreach (var willCast in locked) {
            var unlocked = PetWillCasts.Unlock(Context.SessionActor, wizard, pet, willCast.TalentId, out var problem);
            InformSenderClient(unlocked is null
                ? $"{willCast.ShownName}: {problem}"
                : $"Unlocked the {willCast.ShownName} Will Cast{(PetWillCasts.FreeReagents ? " (free on this server)" : "")}.");
        }
    }

}
