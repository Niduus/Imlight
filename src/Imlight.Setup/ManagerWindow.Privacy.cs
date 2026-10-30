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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Imlight.Setup;

/// <summary>
/// The Privacy card on the Ahziael tab (Niduus, 2026-10-30: users must not be traceable for playing on a private
/// server). It says plainly what Ahziael sends and keeps, and its Clean up button removes Ahziael's leftovers
/// (<see cref="PrivacyCleanup"/>) and undoes "Let friends connect". It never touches Wizard101's own files.
/// </summary>
internal sealed partial class ManagerWindow {

    private Control PrivacyCard() {
        return Fold("lock", "Tools", "Privacy",
            "What Ahziael sends and keeps, and a button that cleans up after it.",
            Sub("Ahziael has no tracking and sends nothing to KingsIsle. It only goes online to GitHub (Niduus/Imlight), " +
                "to check for updates and to read What's new. Your internet address is only looked up when you click " +
                "\"Let friends connect\" or \"Copy address\" on the Play tab. Ahziael and its server never change any Wizard101 file."),
            WithHint(Row(Action(Primary("Clean up"), CleanUpAsync), Action(Plain("Stop letting friends connect"), StopFriendsAsync)),
                "Clean up deletes the server's logs, Ahziael's setup log and list of previous commands, log copies Ahziael " +
                "saved on your desktop, and its downloads in the Windows temp folder. It asks first and shows what it found. " +
                "Your accounts, characters, customs, settings and backups are kept. Stop the server first to clear today's log too. " +
                "Stop letting friends connect closes the ports \"Let friends connect\" opened and removes its Windows Firewall rule."),
            Sub("Wizard101 keeps its own log of the server it last logged in to (WizardClient.log in its Bin folder). " +
                "Ahziael never touches the game's files; the game writes over that log itself the next time you start it. " +
                "If Wizard101's crash reporter appears after playing on a private server, you can click Don't Send."));
    }

    private async Task CleanUpAsync() {
        var manager = _manager;
        var found = await Task.Run(() => PrivacyCleanup.Find(manager));
        if (found.Count == 0) {
            Say("There is nothing to clean up.");

            return;
        }

        var list = string.Join("\n", found.Select(g => $"• {g.Files.Count} {g.What}"));
        if (!await ConfirmAsync($"Delete these?\n\n{list}\n\nAccounts, characters, customs, settings and backups are kept.")) {
            return;
        }

        var (removed, skipped) = await Task.Run(() => PrivacyCleanup.Remove(found));
        Say(skipped == 0
            ? $"Cleaned up: {removed} file{(removed == 1 ? "" : "s")} deleted."
            : $"Cleaned up: {removed} deleted. {skipped} still in use (today's server log while the server runs); stop the server and click Clean up again for those.");
    }

    private async Task StopFriendsAsync() {
        if (!FriendAccess.WasTurnedOn(_manager.InstallFolder) && !await Task.Run(() => FriendAccess.HasFirewallRule())
            && !await ConfirmAsync("\"Let friends connect\" does not look turned on. Ask your router to close the server's ports anyway?")) {
            return;
        }

        Say("Closing the way in for friends. Click Yes if Windows asks…");
        var done = await FriendAccess.TurnOffAsync(_manager.InstallFolder);
        _publicIp = null;
        _friendResult.IsVisible = false;
        ShowFriendAddress();
        Say(done.Count == 0 ? "Nothing was open for friends." : string.Join(" ", done));
    }

}
