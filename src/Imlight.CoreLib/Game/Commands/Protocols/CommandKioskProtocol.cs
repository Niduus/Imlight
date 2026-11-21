/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 * KIOSK COMMAND
 * ========================================================================
 *
 * PURPOSE:
 * `.kiosk <pet>` puts one of your pets up at the Pet Hatching Kiosk. The game's own Offer Pets window only lists
 * pets that are Adult or older, so a younger pet ([HatchKiosk] MinAge set to Baby or Teen) goes up this way
 * (Niduus, 2026-11-21: "a new Kiosk setting for allowing pets at MINIMAL age into the kiosk").
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/21/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandKioskProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Anyone may put their own pets up, the same as at a kiosk.
    [Command("kiosk")]
    private void KioskCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        var pets = PetHatching.OwnPets(Context.Character);
        if (text.Length == 0 || text.Equals("list", StringComparison.OrdinalIgnoreCase)) {
            InformSenderClient(pets.Count == 0
                ? "You have no hatched pets. Wear a pet or keep one in your backpack."
                : $"Type .kiosk and a number, id or name from this list to put that pet up at the Pet Hatching Kiosk (at least {PetProgress.LevelName(KioskHousePets.MinAge)}): "
                  + string.Join("; ", pets.Select((p, i) => $"{i + 1}) {PetHatching.Label(p)} ({PetProgress.LevelName(PetProgress.GetBehavior(p)?.m_level ?? 0)}, id {p.m_globalID.Full})")));

            return;
        }

        if (!KioskLink.Enabled || !PetHatching.Enabled) {
            InformSenderClient("The Pet Hatching Kiosk is turned off right now.");

            return;
        }

        var found = Find(pets, text);
        if (found.Count != 1) {
            InformSenderClient(found.Count == 0
                ? $"Could not find a pet called \"{text}\". Type .kiosk to list your pets."
                : $"More than one pet fits \"{text}\". Use its number from .kiosk instead.");

            return;
        }

        var item = found[0];
        var charId = Context.Character.CharId;
        var pet = PetHatching.Snapshot(item, charId);
        var problem = PetHatching.ParentProblem(pet) ?? KioskHousePets.AgeProblem(pet);
        if (problem is not null) {
            InformSenderClient(problem);

            return;
        }

        var listed = HatchmakingListings.Of(charId);
        var most = Math.Clamp(ConfigurationManager.GetValue("HatchKiosk.MaxPets", 5), 1, 100);
        if (!OnlineList.IsBotWizard(charId) && listed.Count >= most && listed.All(l => l.Parent.ItemId != pet.ItemId)) {
            InformSenderClient($"You can't add any more pets to the Pet Hatching Kiosk ({most} at a time). Take one off first.");

            return;
        }

        HatchmakingListings.Add(HatchmakingListings.For(item, pet));
        Logger.Information("Hatching: {0} listed {1} at the kiosk with .kiosk.", Logger.Args(charId, PetHatching.Describe(pet)));
        InformSenderClient($"{pet.Label} is now at the hatchmaking kiosk.");
    }

    private static List<WizClientObjectItem> Find(List<WizClientObjectItem> pets, string text) {
        if (ulong.TryParse(text, out var number)) {
            if (number >= 1 && number <= (ulong) pets.Count) {
                return [pets[(int) number - 1]];
            }

            return [.. pets.Where(p => p.m_globalID.Full == number)];
        }

        var exact = pets.Where(p => string.Equals(PetHatching.Label(p), text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PetHatching.KindName((uint) p.m_templateID), text, StringComparison.OrdinalIgnoreCase)).ToList();

        return exact.Count > 0
            ? exact
            : [.. pets.Where(p => PetHatching.Label(p).Contains(text, StringComparison.OrdinalIgnoreCase)
                || (PetHatching.KindName((uint) p.m_templateID) ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))];
    }

}
