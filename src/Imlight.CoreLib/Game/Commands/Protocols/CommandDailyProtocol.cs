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
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.daily` shows today's Daily Assignment (and opens the game's window), `.daily new` swaps it for another one.
/// For testing (quality assurance): `.daily done` finishes it, `.daily day N` makes the next one day N of the streak,
/// `.daily reset` clears the assignment and the streak.
/// </summary>
internal class CommandDailyProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("daily")]
    [Alias("dailies", "assignment")]
    private void DailyCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var action = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        var number = words.Length > 1 && int.TryParse(words[1], out var n) ? n : 1;

        if (action is "done" or "day" or "reset"
            && !AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command daily " + action)) {
            InformSenderClient("You do not have permission to use this command.");

            return;
        }

        if (action is not ("" or "new" or "done" or "day" or "reset")) {
            InformSenderClient("Use .daily to see today's assignment or .daily new for another one.");

            return;
        }

        Context.SessionActor.Tell(new DailyAssignmentCommand { Action = action, Number = number }, ActorRefs.NoSender);
    }

}
