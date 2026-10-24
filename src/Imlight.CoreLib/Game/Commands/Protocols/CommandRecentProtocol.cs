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
using System.Globalization;
using System.Linq;
using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// The friends window's Recent tab from chat: `.recent` lists it, `.recent clear` empties it, and
/// `.recent add <wizard name|id>` (QA) puts a wizard in it, to try the tab without battling someone first.
/// </summary>
internal class CommandRecentProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("recent")]
    private void RecentCommand([Remainder] string what = "") {
        var me = Context.Character;
        var text = (what ?? "").Trim();
        if (!RecentWizards.Enabled) {
            InformSenderClient("The recent wizards list is off ([RecentWizards] Enabled = false in Imlight.ini).");

            return;
        }

        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
            var removed = RecentWizards.Clear(me.CharId);
            InformSenderClient(removed == 0 ? "Your recent wizards list was already empty." : $"Removed {removed} wizard(s) from your recent wizards list.");

            return;
        }

        if (text.StartsWith("add ", StringComparison.OrdinalIgnoreCase) || text.Equals("add", StringComparison.OrdinalIgnoreCase)) {
            if (Context.Account.AuthLevel < AuthLevel.QualityAssurance) {
                InformSenderClient("You do not have permission to use this command.");

                return;
            }

            var who = text.Length > 3 ? text[4..].Trim() : "";
            var other = Find(who);
            if (other is null || other.CharId == me.CharId) {
                InformSenderClient("Use .recent add and the name of a wizard who is online now (or a character id).");

                return;
            }

            RecentWizards.Met([me.CharId, other.CharId], RecentWizardKind.Combat);
            InformSenderClient($"{other.PlayerNameBehavior.GetWizardName()} is in your recent wizards list now (and you in theirs). Open the Friends window's Recent tab again to see it.");

            return;
        }

        var list = RecentWizards.For(me.CharId);
        if (list.Count == 0) {
            InformSenderClient("No recent wizards yet. Battle, group up, hatch or duel with someone and they show up here and in the Friends window's Recent tab.");

            return;
        }

        var lines = list.Take(10).Select(e => {
            var name = WizardCollection.GetCharacterUnloaded(e.CharId)?.PlayerNameBehavior?.GetWizardName() ?? $"character {e.CharId}";
            var when = DateTimeOffset.FromUnixTimeSeconds(e.LastSeen).LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
            return $"{name} ({when}): {e.Combat} battles, {e.Instance} groups, {e.Hatch} hatches, {e.PvP} duels";
        });
        InformSenderClient($"Recent wizards ({list.Count}):<br>" + string.Join("<br>", lines));
    }

    private static Wizard Find(string who) {
        if (who.Length == 0) {
            return null;
        }

        if (ulong.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
            return WizardCollection.GetCharacterUnloaded(id);
        }

        return OnlinePlayerCollection.GetOnlinePlayers()
            .Select(p => WizardCollection.GetCharacterUnloaded(p.CharacterId))
            .FirstOrDefault(w => string.Equals(w?.PlayerNameBehavior?.GetWizardName(), who, StringComparison.OrdinalIgnoreCase));
    }

}
