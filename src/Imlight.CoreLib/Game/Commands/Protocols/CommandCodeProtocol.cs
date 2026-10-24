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
/// `.code` opens the game's promo code window in game (Niduus, 2026-10-23: "make a .code command to bring up the
/// code UI"). The game only opens it itself from the Promo Code button on the character select screen
/// (WizardCharacterSelect, btnPromoCode, promoCodeEntry.gui), so in game it is asked for by file with MSG_SHOWGUI.
/// `.code HYPER` redeems a code (the Manager's Codes card makes them). `.code gui 2` to `.code gui 4` try other
/// spellings and the friend code window; `.code gui &lt;file&gt;.gui` tries any window. `.code watch` logs what the
/// game sends for 2 minutes.
/// </summary>
internal class CommandCodeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // 1 is the promo code window as the game names it; the rest are guesses in case the game wants the folder or
    // the window is a different one (the FriendFinder "Enter Code" window).
    private static readonly string[] s_windows = [
        PromoCodeService.CODE_WINDOW,
        "GUI/promoCodeEntry.gui",
        "FriendCodeRequest.gui",
        "GUI/FriendCodeRequest.gui",
    ];

    // Anyone may open the window and redeem codes; the window tests are for quality assurance.
    [Command("code")]
    [Alias("codes", "redeem")]
    private void CodeCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            ShowWindow(s_windows[0]);

            return;
        }

        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words[0].Equals("watch", StringComparison.OrdinalIgnoreCase)) {
            // For finding what a button sends (Niduus, 2026-10-23: the Idle button opened nothing).
            if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command code watch")) {
                InformSenderClient("You do not have permission to use this command.");

                return;
            }

            Context.SessionActor.Tell(new WatchClientMessages { Seconds = 120 }, ActorRefs.NoSender);
            InformSenderClient("For the next 2 minutes the server log gets every message your game sends. Press the button now (such as Idle in the Options book).");

            return;
        }

        if (words[0].Equals("gui", StringComparison.OrdinalIgnoreCase)) {
            if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command code gui")) {
                InformSenderClient("You do not have permission to use this command.");

                return;
            }

            var which = words.Length > 1 ? words[1].Trim() : "1";
            if (int.TryParse(which, out var number) && number >= 1 && number <= s_windows.Length) {
                ShowWindow(s_windows[number - 1]);
            }
            else if (which.EndsWith(".gui", StringComparison.OrdinalIgnoreCase)) {
                ShowWindow(which);
            }
            else {
                InformSenderClient($"Use .code gui 1 to {s_windows.Length} to try another window, or .code gui <file>.gui.");
            }

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

    private void ShowWindow(string file) {
        try {
            Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_SHOWGUI { GUIFileName = file, StageIndex = 0 }, ActorRefs.NoSender);
            Logger.Information("Code: {0} asked the game to show {1}.", Logger.Args(Context.Character?.CharId, file));
            InformSenderClient($"Opening the code window. You can also type .code and the code, such as .code HYPER.");
        }
        catch (Exception ex) {
            Logger.Error("Code: showing {0} failed. {1}", Logger.Args(file, ex.Message));
            InformSenderClient("Could not open the code window.");
        }
    }

}
