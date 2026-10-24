/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.mirror`: the Magic Mirror. Alone it takes you to the Wizard City Magic Mirror Shop. `.mirror reset` drops the
/// mirror look, `.mirror girl` / `.mirror boy` change the wizard's gender (relog), `.mirror look 1 2 3 ...` sets the
/// look numbers by hand (quality assurance, for finding the right numbers) and `.mirror data` writes
/// magic-mirror-data.txt next to the server.
/// </summary>
internal class CommandMirrorProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("mirror")]
    [Alias("magicmirror")]
    private void MirrorCommand([Remainder] string text = "") {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        switch (first) {
            case "":
                GoToShop();

                break;

            case "reset":
                wizard.MirrorLook = null;
                WizardCollection.UpdateCharacterMirrorLook(wizard);
                InformSenderClient("Your Magic Mirror look is gone. Change zones or relog to see your old look.");

                break;

            case "girl":
            case "boy":
            case "gender":
                ChangeGender(words.Length > 1 && first == "gender" ? words[1].ToLowerInvariant() : first);

                break;

            case "look":
                if (!IsQa("Command mirror look")) {
                    return;
                }

                var numbers = words.Skip(1).Select(w => int.TryParse(w, out var n) ? n : (int?) null).ToList();
                if (numbers.Count == 0 || numbers.Any(n => n is null)) {
                    InformSenderClient($"Type up to {MagicMirror.Parts.Length} numbers in this order: {string.Join(", ", MagicMirror.Parts)}. Use -1 to leave a part as it is.");

                    return;
                }

                var look = MagicMirror.Normalize(wizard.MirrorLook);
                for (var i = 0; i < numbers.Count && i < look.Length; i++) {
                    look[i] = numbers[i].Value;
                }

                Context.SessionActor.Tell(new ApplyMirrorLook { Look = look }, ActorRefs.NoSender);
                InformSenderClient("Mirror look: " + MagicMirror.Describe(look));

                break;

            case "data":
                if (!IsQa("Command mirror data")) {
                    return;
                }

                var path = MagicMirror.WriteData();
                InformSenderClient(path is null
                    ? "Could not write the Magic Mirror data, see the server log."
                    : $"Wrote {MagicMirror.DATA_FILE} next to the server. Open a Magic Mirror first so it has the mirror's options.");

                break;

            default:
                InformSenderClient("Use .mirror (go to the Magic Mirror Shop), .mirror reset, .mirror girl or .mirror boy.");

                break;
        }
    }

    private void ChangeGender(string which) {
        var wizard = Context.Character;
        if (wizard.WizardAvatar is null || which is not ("girl" or "boy")) {
            InformSenderClient("Use .mirror girl or .mirror boy.");

            return;
        }

        var gender = which == "girl" ? eGender.Female : eGender.Male;
        if (wizard.WizardAvatar.m_eGender == gender) {
            InformSenderClient($"Your wizard is already a {which}.");

            return;
        }

        // The mirror look numbers belong to the old gender's lists.
        wizard.WizardAvatar.m_eGender = gender;
        wizard.MirrorLook = null;
        WizardCollection.UpdateCharacterMirrorLook(wizard);
        Logger.Information("Magic Mirror | {0} is now a {1}.", Logger.Args(wizard.CharId, which));
        InformSenderClient($"Your wizard is now a {which}. Log out to character select and back in to see it. Then visit the Magic Mirror to pick a new look.");
    }

    private void GoToShop() {
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        Task.Delay(2000).Wait();
        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = MagicMirror.SHOP_ZONE,
            DestinationLocation = "Start",
            SendToClient = true,
            OwnerCharId = Context.Character.CharId
        });
    }

    private bool IsQa(string what) {
        if (AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, what)) {
            return true;
        }

        InformSenderClient("You do not have permission to use this command.");

        return false;
    }

}
