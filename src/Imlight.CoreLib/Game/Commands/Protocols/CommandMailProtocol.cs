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
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.mail` sends yourself a Pixie Post, so the Mail tab of the friends window and the new mail button can be tried
/// without a second wizard. `.mail Hello there` uses your own words.
/// </summary>
internal class CommandMailProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("mail")]
    [Alias("pixiepost")]
    private void MailCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        text = (text ?? "").Trim();
        if (text.Length == 0) {
            text = "This is a test Pixie Post. Open your friends list and the Mail tab to read it, then try Reply and Delete.";
        }

        try {
            var post = PixiePostService.MakePost(wizard, "Test Pixie Post", text);
            Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_PIXIEPOSTDELIVER {
                RecipientCharId = wizard.CharId,
                Post = post,
            }, ActorRefs.NoSender);
            InformSenderClient("A Pixie Post is on its way to you. Open your friends list and its Mail tab to read it.");
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | .mail failed. {0}", Logger.Args(ex.Message));
            InformSenderClient("Could not send the test Pixie Post.");
        }
    }

}
