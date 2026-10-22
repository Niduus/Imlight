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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.hatch <pet 1> <pet 2>` hatches two of your own pets at once, for players alone (Niduus, 2026-10-20). A pet is
/// named by its number in `.hatch`'s list, its item id, or words of its name.
/// </summary>
internal class CommandHatchProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("hatch")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void HatchCommand([Remainder] string text = "") {
        var pets = OwnPets();
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0].Equals("list", StringComparison.OrdinalIgnoreCase)) {
            InformSenderClient(pets.Count == 0
                ? "You have no hatched pets. Wear a pet or keep one in your backpack."
                : "Type .hatch <pet 1> <pet 2> with numbers, ids or names from this list: "
                  + string.Join("; ", pets.Select((p, i) => $"{i + 1}) {Name(p)} ({PetProgress.LevelName(PetProgress.GetBehavior(p)?.m_level ?? 0)}, id {p.m_globalID.Full})")));

            return;
        }

        // Every way to cut the words in two, until both halves name different pets.
        for (var cut = 1; cut < words.Length; cut++) {
            var first = Find(pets, string.Join(' ', words[..cut]));
            var second = Find(pets, string.Join(' ', words[cut..]));
            var pair = first.SelectMany(a => second.Where(b => b != a).Select(b => (a, b))).FirstOrDefault();
            if (pair.a is null) {
                continue;
            }

            var parentA = PetHatching.Snapshot(pair.a, Context.Character.CharId);
            var parentB = PetHatching.Snapshot(pair.b, Context.Character.CharId);
            var problem = PetHatching.PairProblem(parentA, parentB);
            if (problem is not null) {
                InformSenderClient(problem);

                return;
            }

            Logger.Information("Hatching: .hatch \"{0}\" by {1} picked {2} and {3}.",
                Logger.Args(text, Context.Character.CharId, parentA.ItemId, parentB.ItemId));
            Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_BREEDPETS {
                ParentA = parentA,
                ParentB = parentB,
                Source = HatchSource.Command,
            }, ActorRefs.NoSender);

            return;
        }

        InformSenderClient("Could not find two different pets in \"" + text + "\". Type .hatch to list your pets.");
    }

    private List<WizClientObjectItem> OwnPets()
        => PetHatching.OwnPets(Context.Character);

    private static List<WizClientObjectItem> Find(List<WizClientObjectItem> pets, string text) {
        text = text.Trim();
        if (ulong.TryParse(text, out var number)) {
            // Small numbers are places in the list; big ones are item ids.
            if (number >= 1 && number <= (ulong) pets.Count) {
                return [pets[(int) number - 1]];
            }

            return [.. pets.Where(p => p.m_globalID.Full == number)];
        }

        var exact = pets.Where(p => string.Equals(Name(p), text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PetHatching.KindName((uint) p.m_templateID), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) {
            return exact;
        }

        return [.. pets.Where(p => Name(p).Contains(text, StringComparison.OrdinalIgnoreCase)
            || (PetHatching.KindName((uint) p.m_templateID) ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))];
    }

    private static string Name(WizClientObjectItem pet) => PetHatching.Label(pet);

}
