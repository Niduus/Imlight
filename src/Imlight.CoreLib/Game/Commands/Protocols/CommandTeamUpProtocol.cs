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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.TeamUp;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.teamup` opens the Join a Team window anywhere (Niduus, 2026-10-24: "Team Up kiosk: join a team for a dungeon with
/// one click"); the game places no Join A Team kiosk in its zones. `.teamup ask` (or `.teamup ask 2`) asks for a team
/// at the dungeon sigil the player stands at, the same as the sigil window's Team Up! button. `.teamup list` says
/// which teams are waiting.
/// </summary>
internal class CommandTeamUpProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // How far from a sigil's centre `.teamup ask` still finds it.
    private const float ASK_DISTANCE = 400f;

    [Command("teamup")]
    [Alias("team", "jointeam")]
    private void TeamUpCommand([Remainder] string text = "") {
        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!TeamUpBoard.IsEnabled) {
            InformSenderClient("Team Up is turned off on this server ([TeamUp] Enabled in Imlight.ini).");

            return;
        }

        if (words.Length == 0) {
            Context.SessionActor.Tell(new OpenTeamHelp(), ActorRefs.NoSender);
            InformSenderClient("Opening Join a Team. Pick a world, then a team, to join it.");

            return;
        }

        if (words[0].Equals("list", StringComparison.OrdinalIgnoreCase)) {
            var waiting = TeamUpBoard.Waiting();
            InformSenderClient(waiting.Count == 0
                ? "No teams are waiting for players right now."
                : string.Join(" | ", waiting.Select(t => $"{ShortName(t.Zone)}: {t.Count} of {t.Size}, {t.SecondsLeft} s left")));

            return;
        }

        if (words[0].Equals("ask", StringComparison.OrdinalIgnoreCase)) {
            byte size = 4;
            if (words.Length > 1 && (!byte.TryParse(words[1], out size) || size is < 2 or > 4)) {
                InformSenderClient("Use .teamup ask or .teamup ask 2 to 4 (how many players the team is for).");

                return;
            }

            var at = Context.CharacterObject?.m_location;
            var sigil = at is { } here
                ? TeamUpSigils.Nearest(Context.Character?.Zone, here.X, here.Y, ASK_DISTANCE)
                : null;
            if (sigil is null) {
                InformSenderClient("Stand on a dungeon sigil first (walk onto it once so the server knows it), then type .teamup ask.");

                return;
            }

            Context.SessionActor.Tell(new AskTeamUp { Sigil = sigil, Size = size }, ActorRefs.NoSender);
            InformSenderClient($"Asking for a team of {size} for {ShortName(sigil.DestinationZone)}. Others can join with .teamup or a Join A Team kiosk.");

            return;
        }

        InformSenderClient("Use .teamup to open Join a Team, .teamup ask at a dungeon sigil to ask for a team, or .teamup list.");
    }

    private static string ShortName(string zone)
        => string.IsNullOrEmpty(zone) ? "?" : zone[(zone.LastIndexOf('/') + 1)..];

}
