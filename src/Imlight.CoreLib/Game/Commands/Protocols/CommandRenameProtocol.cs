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
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.rename` opens the game's own Change Name or Style window (RenameService). The game only opens it at level 50 or
/// higher and with 100 crowns or more, so a wizard with fewer crowns is shown 100 just while it opens; nothing is
/// taken. `.rename &lt;name&gt;` renames straight away (a name from the lists, or a custom one). Anyone may rename
/// their own wizard.
/// </summary>
internal class CommandRenameProtocol : CommandProtocol {

    // What the game's window checks before it opens (GUI3_00000002 and GUI3_00000003).
    private const int WINDOW_LEVEL = 50;
    private const int WINDOW_CROWNS = 100;

    internal override string Group { get; set; } = "";

    [Command("rename")]
    private void RenameCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        text = (text ?? "").Trim();
        if (text.Length == 0) {
            OpenWindow(wizard);

            return;
        }

        if (!WizardRename.TryReadName(wizard, text, out var nameKeys, out var customName)) {
            InformSenderClient($"That name is too long. Keep it to {WizardRename.MaxLength} letters.");

            return;
        }

        var oldName = wizard.PlayerNameBehavior.GetWizardName();
        wizard.Rename(nameKeys, customName);
        var newName = wizard.PlayerNameBehavior.GetWizardName();
        Logger.Information("Rename | {0} renamed themselves to {1} with .rename.", Logger.Args(oldName, newName));
        InformSenderClient(customName.Length == 0
            ? $"Your name is now {newName}. Relog to see it."
            : $"Your name is now {newName} (a custom name, not one from the game's lists). Relog to see it.");
    }

    private void OpenWindow(Wizard wizard) {
        if (wizard.MagicSchoolBehavior.Level < WINDOW_LEVEL) {
            InformSenderClient($"The game's rename window only opens at level {WINDOW_LEVEL} or higher. Below that, type .rename and the new name, such as .rename Walter Blanco.");

            return;
        }

        var crowns = wizard.Account?.Crowns ?? 0;
        if (crowns < WINDOW_CROWNS) {
            SendCrowns(wizard, WINDOW_CROWNS);
        }

        Context.SessionActor.Tell(new WIZARD3_56_PROTOCOL.MSG_REQUESTRENAMECHARACTER { Status = RenameService.OPEN_WINDOW }, ActorRefs.NoSender);
        if (crowns < WINDOW_CROWNS) {
            SendCrowns(wizard, crowns);
        }

        Logger.Information("Rename | Opened the rename window for {0}.", Logger.Args(wizard.CharId));
    }

    private void SendCrowns(Wizard wizard, int crowns)
        => Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
            Failure = 0,
            TotalCrowns = crowns,
            CharacterID = wizard.CharId,
            CacheBalanceForCSSegmentation = 1
        }, ActorRefs.NoSender);

}
