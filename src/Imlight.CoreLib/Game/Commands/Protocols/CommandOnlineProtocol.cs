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
using System.Globalization;
using System.Linq;
using System.Text;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.online`: who is playing now and where, like the "Who's online" card on Ahziael's Home (Niduus, 2026-11-04, chat
/// commands for Ahziael's features). Every player sees each wizard's name and world; hall monitors and up also see the
/// account and the exact zone, as the card shows them. Also .who and .whosonline.
/// </summary>
internal class CommandOnlineProtocol : CommandProtocol {

    private const int PAGE_SIZE = 10;

    internal override string Group { get; set; } = "";

    [Command("online")]
    [Alias("who", "whosonline")]
    private void OnlineCommand(string pageText = "1") {
        var staff = (Context.Account?.AuthLevel ?? AuthLevel.None) >= AuthLevel.HallMonitor;
        var players = OnlinePlayerCollection.GetCachedOnlinePlayers();
        var inWorld = players.Where(p => p.CharacterId != 0 && !string.IsNullOrWhiteSpace(p.WizardName))
            .OrderBy(p => p.WizardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var choosing = players.Length - inWorld.Count;

        if (inWorld.Count == 0) {
            InformSenderClient("Nobody is in the world right now" + (choosing > 0 ? $" ({choosing} at character select)." : "."));

            return;
        }

        var pages = (inWorld.Count + PAGE_SIZE - 1) / PAGE_SIZE;
        var page = int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var asked) ? Math.Clamp(asked, 1, pages) : 1;
        var text = new StringBuilder()
            .AppendLine($"<center>Online now: {inWorld.Count}{(pages > 1 ? $" (page {page} of {pages})" : "")}</center>")
            .AppendLine();
        foreach (var player in inWorld.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE)) {
            var place = Place(player.CurrentZone, player.CurrentZoneDisplayName);
            text.AppendLine(staff
                ? $"{player.WizardName}  -  {place}  ({player.AccountName}, {player.CurrentZone})"
                : $"{player.WizardName}  -  {place}");
        }

        if (choosing > 0) {
            text.AppendLine().AppendLine($"{choosing} more at character select.");
        }

        if (page < pages) {
            text.AppendLine().Append($"More: type .online {page + 1}");
        }

        InformSenderClient(text.ToString().TrimEnd(), true);
    }

    /// <summary>The world a zone is in, in words: "WizardCity/WC_Ravenwood" is Wizard City.</summary>
    internal static string Place(string zone, string shown) {
        if (!string.IsNullOrWhiteSpace(shown) && !shown.Contains('/')) {
            return shown.Trim();
        }

        var world = (zone ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (world.Length == 0) {
            return "somewhere";
        }

        var words = new StringBuilder();
        for (var i = 0; i < world.Length; i++) {
            if (i > 0 && char.IsUpper(world[i]) && char.IsLower(world[i - 1])) {
                words.Append(' ');
            }

            words.Append(world[i] == '_' ? ' ' : world[i]);
        }

        return words.ToString();
    }

}
