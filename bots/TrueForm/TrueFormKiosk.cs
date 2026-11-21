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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form offers every pet he has at the Pet Hatching Kiosk, so players can hatch with them (Niduus, 2026-11-21: "to
/// test Pet Hatching Kiosk, have True Form offer all his pets to the Kiosk for others to hatch with"). Each time he is
/// made ready his listings are brought up to date (his pets' talents change with his setup), and a pet he no longer has
/// comes off the kiosk.
/// </summary>
internal static class TrueFormKiosk {

    internal static void Offer(Wizard wizard) {
        if (wizard is null || !PetHatching.Enabled) {
            return;
        }

        try {
            var pets = PetHatching.OwnPets(wizard);
            var ids = pets.Select(p => p.m_globalID.Full).ToHashSet();
            foreach (var old in HatchmakingListings.Of(wizard.CharId).Where(l => !ids.Contains(l.Parent.ItemId))) {
                HatchmakingListings.Remove(old.Parent.ItemId);
            }

            var listed = 0;
            foreach (var pet in pets) {
                var parent = PetHatching.Snapshot(pet, wizard.CharId);
                if (PetHatching.ParentProblem(parent) is { } problem) {
                    Logger.Debug("True Form: {0} stays off the hatching kiosk: {1}", Logger.Args(parent?.Label, problem));
                    continue;
                }

                HatchmakingListings.Add(new HatchmakingListing {
                    Parent = parent,
                    NameKeys = CoreObjectFactory.FindBehaviorInstance<ClientPetNameBehavior>(pet, out var name) ? name.m_nameKeys : 0,
                    ItemData = ItemSync.TrySerializeForKiosk(pet, out var data) ? Convert.ToBase64String(data) : null,
                });
                listed++;
            }

            Logger.Information("True Form: offered {0} of his {1} pets at the hatching kiosk ({2}).",
                Logger.Args(listed, pets.Count, string.Join(", ", pets.Select(PetHatching.Label))));
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not offer his pets at the hatching kiosk: {0}", Logger.Args(e.Message));
        }
    }

}
