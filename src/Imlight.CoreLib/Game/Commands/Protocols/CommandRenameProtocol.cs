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
using Imlight.Common;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.rename` asks for the game's own rename window: on the way back to character select the wizard is sent with
/// m_shouldRename, so the game asks for a new name from its name lists (WizardRename). `.rename cancel` takes it back,
/// and `.rename &lt;name&gt;` renames straight away (a name from the lists, or a custom one). Anyone may rename
/// their own wizard.
/// </summary>
internal class CommandRenameProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("rename")]
    private void RenameCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        text = (text ?? "").Trim();
        if (text.Length == 0) {
            Ask(wizard, true);
            InformSenderClient($"Log out to the character select screen and pick this wizard. The game will ask you for a new name. This stays on for {(int) Wizard.RenameWindowTime.TotalMinutes} minutes; type .rename cancel to stop it.");

            return;
        }

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase)) {
            Ask(wizard, false);
            InformSenderClient("The rename window is off again.");

            return;
        }

        if (!WizardRename.TryReadName(wizard, text, out var nameKeys, out var customName)) {
            InformSenderClient($"That name is too long. Keep it to {WizardRename.MaxLength} letters.");

            return;
        }

        var oldName = wizard.PlayerNameBehavior.GetWizardName();
        wizard.Rename(nameKeys, customName);
        CopyToAccount(wizard);
        var newName = wizard.PlayerNameBehavior.GetWizardName();
        Logger.Information("Rename | {0} renamed themselves to {1} with .rename.", Logger.Args(oldName, newName));
        InformSenderClient(customName.Length == 0
            ? $"Your name is now {newName}. Relog to see it."
            : $"Your name is now {newName} (a custom name, not one from the game's lists). Relog to see it.");
    }

    private static void Ask(Wizard wizard, bool ask) {
        wizard.AskForRename(ask);
        CopyToAccount(wizard);
    }

    // The account may hold its own copy of the wizard for character select.
    private static void CopyToAccount(Wizard wizard) {
        var copy = wizard.Account?.GetCharacter(wizard.CharId);
        if (copy is null || ReferenceEquals(copy, wizard)) {
            return;
        }

        copy.RenameAskedAt = wizard.RenameAskedAt;
        copy.PlayerNameBehavior.NameIndices = wizard.PlayerNameBehavior.NameIndices;
        copy.PlayerNameBehavior.NameOverride = wizard.PlayerNameBehavior.NameOverride;
    }

}
