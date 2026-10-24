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
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The game's in-game rename request (MSG_REQUESTRENAMECHARACTER: new name keys, gender and text). What opens it and
/// what the game wants back are not known, so the server takes the name and logs it all.
/// The rename window at character select is in CharacterService.
/// </summary>
internal class RenameService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new RenameService(parentActor));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTRENAMECHARACTER))]
    private void ReceiveRequestRenameCharacter(WIZARD3_56_PROTOCOL.MSG_REQUESTRENAMECHARACTER message) {
        try {
            var wizard = GetActiveWizard();
            string text = message.Text.ToString();
            Logger.Information("Rename | {0} sent a rename request: status {1}, name keys {2:X8}, gender {3}, text \"{4}\".",
                Logger.Args(wizard?.CharId, message.Status, message.NewName, message.NewGender, text));
            if (wizard is null) {
                return;
            }

            var names = wizard.PlayerNameBehavior;
            var renamed = false;
            if (message.NewName != 0 && WizardNameBank.IsValidWizardName(message.NewName, names.Gender)) {
                wizard.Rename((names.NameIndices & 0xFF000000) | (message.NewName & 0x00FFFFFF), "");
                renamed = true;
            }
            else if (WizardRename.TryReadName(wizard, text, out var nameKeys, out var customName)) {
                wizard.Rename(nameKeys, customName);
                renamed = true;
            }

            if (renamed) {
                SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                    Message = $"Your name is now {names.GetWizardName()}. Relog to see it everywhere.",
                    Modal = 0
                });
            }
        }
        catch (Exception ex) {
            Logger.Error("Rename | The in-game rename request failed. {0}", Logger.Args(ex.Message));
        }
    }

}
