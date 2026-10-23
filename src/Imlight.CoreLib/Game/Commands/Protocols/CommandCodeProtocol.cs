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
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.code` opens the game's promo code window in game (Niduus, 2026-10-23: "make a .code command to bring up the
/// code UI"). The game only opens it itself from the Promo Code button on the character select screen
/// (WizardCharacterSelect, btnPromoCode, promoCodeEntry.gui), so in game it is asked for by file with MSG_SHOWGUI.
/// `.code 2` to `.code 4` try the other spellings and the friend code window; `.code &lt;file&gt;.gui` tries any window.
/// </summary>
internal class CommandCodeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // 1 is the promo code window as the game names it; the rest are guesses in case the game wants the folder or
    // the window is a different one (the FriendFinder "Enter Code" window).
    private static readonly string[] s_windows = [
        "promoCodeEntry.gui",
        "GUI/promoCodeEntry.gui",
        "FriendCodeRequest.gui",
        "GUI/FriendCodeRequest.gui",
    ];

    [Command("code")]
    [Alias("codes")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CodeCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();

        string file;
        if (text.Length == 0) {
            file = s_windows[0];
        }
        else if (int.TryParse(text, out var number) && number >= 1 && number <= s_windows.Length) {
            file = s_windows[number - 1];
        }
        else if (text.EndsWith(".gui", StringComparison.OrdinalIgnoreCase)) {
            file = text;
        }
        else {
            InformSenderClient($"Use .code, or .code 1 to {s_windows.Length} to try another window, or .code <file>.gui.");

            return;
        }

        try {
            Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_SHOWGUI { GUIFileName = file, StageIndex = 0 }, ActorRefs.NoSender);
            Logger.Information("Code: {0} asked the game to show {1}.", Logger.Args(Context.Character?.CharId, file));
            InformSenderClient($"Asked the game to open {file}. If nothing shows, try .code 2, .code 3 or .code 4. "
                + "The promo code window also opens from the Promo Code button on the character select screen.");
        }
        catch (Exception ex) {
            Logger.Error("Code: showing {0} failed. {1}", Logger.Args(file, ex.Message));
            InformSenderClient("Could not open the code window.");
        }
    }

}
