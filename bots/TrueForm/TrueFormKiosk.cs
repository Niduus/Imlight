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
/// True Form's pets at the Pet Hatching Kiosk. He offered every pet he had (Niduus, 2026-11-21, to test the kiosk); that test is
/// over (Niduus, 2026-11-22: "Remove his pets from Kiosk as well"), so each time he is made ready any listing of his comes off.
/// </summary>
internal static class TrueFormKiosk {

    internal static void Withdraw(Wizard wizard) {
        if (wizard is null) {
            return;
        }

        try {
            var listed = HatchmakingListings.Of(wizard.CharId);
            foreach (var listing in listed) {
                HatchmakingListings.Remove(listing.Parent.ItemId);
            }

            if (listed.Count > 0) {
                Logger.Information("True Form: took his {0} pets off the hatching kiosk.", Logger.Args(listed.Count));
            }
        }
        catch (Exception e) {
            Logger.Warning("True Form: could not take his pets off the hatching kiosk: {0}", Logger.Args(e.Message));
        }
    }

}
