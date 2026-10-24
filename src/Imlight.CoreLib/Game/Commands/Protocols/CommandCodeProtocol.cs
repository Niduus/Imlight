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
 */

using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Codes;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.code HYPER` redeems a code (the Manager's Codes card makes them). The server never opens the game's promo code
/// window (Niduus, 2026-10-24: "Completely remove Promo Code UI usage on the server"), so chat is the only way to
/// redeem. `.code watch` logs what the game sends for 2 minutes.
/// </summary>
internal class CommandCodeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Anyone may redeem codes; `.code watch` is for quality assurance.
    [Command("code")]
    [Alias("codes", "redeem")]
    private void CodeCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            InformSenderClient("Type .code and the code to redeem it, such as .code HYPER.");

            return;
        }

        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words[0].Equals("watch", StringComparison.OrdinalIgnoreCase)) {
            // For finding what a button sends (Niduus, 2026-10-23).
            if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command code watch")) {
                InformSenderClient("You do not have permission to use this command.");

                return;
            }

            Context.SessionActor.Tell(new WatchClientMessages { Seconds = 120 }, ActorRefs.NoSender);
            InformSenderClient("For the next 2 minutes the server log gets every message your game sends. Press the button now.");

            return;
        }

        try {
            PromoCodes.Redeem(Context.Character?.Account ?? Context.Account, text, Context.Character, Context.SessionActor, out var answer);
            InformSenderClient(answer);
        }
        catch (Exception ex) {
            Logger.Error("Codes | Redeeming \"{0}\" with .code failed. {1}", Logger.Args(text, ex.Message));
            InformSenderClient("Something went wrong redeeming that code. Try again.");
        }
    }

}
