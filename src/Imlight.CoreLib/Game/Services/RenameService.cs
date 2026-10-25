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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The game's own Change Name or Style window (RenameCharacterWindow.gui, read from the game client 2026-10-25):
/// the server sends MSG_REQUESTRENAMECHARACTER with Status 0 to open it (`.rename`). The game itself refuses below
/// level 50 or under 100 crowns. Its OK button sends Status 2 with the picked name keys and gender (style), which the
/// server takes. Status 1 is the game's "already changed once" box, so it is never sent; renaming is free.
/// </summary>
internal class RenameService(SessionActor sessionActor) : MessageService(sessionActor) {

    internal const uint OPEN_WINDOW = 0;
    private const uint PICKED = 2;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new RenameService(parentActor));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTRENAMECHARACTER))]
    private void ReceiveRequestRenameCharacter(WIZARD3_56_PROTOCOL.MSG_REQUESTRENAMECHARACTER message) {
        try {
            var wizard = GetActiveWizard();
            Logger.Information("Rename | {0} sent the rename window: status {1}, name keys {2:X8}, gender {3}.",
                Logger.Args(wizard?.CharId, message.Status, message.NewName, message.NewGender));
            if (wizard is null || message.Status != PICKED) {
                return;
            }

            var names = wizard.PlayerNameBehavior;
            var oldName = names.GetWizardName();
            var gender = message.NewGender is (uint) eGender.Male or (uint) eGender.Female
                ? (eGender) message.NewGender
                : names.Gender;
            if (!WizardNameBank.IsValidWizardName(message.NewName, gender)) {
                SendNote("That name could not be read. Nothing was changed.");

                return;
            }

            var styleChanged = wizard.WizardAvatar is not null && wizard.WizardAvatar.m_eGender != gender;
            if (styleChanged) {
                // As the Magic Mirror's .mirror girl/boy: the look numbers belong to the old style's lists.
                wizard.WizardAvatar.m_eGender = gender;
                wizard.MirrorLook = null;
                WizardCollection.UpdateCharacterMirrorLook(wizard);
            }

            names.Gender = gender;
            wizard.Rename((names.NameIndices & 0xFF000000) | (message.NewName & 0x00FFFFFF), "");
            Logger.Information("Rename | {0} is now {1}{2}.",
                Logger.Args(oldName, names.GetWizardName(), styleChanged ? $" ({gender})" : ""));
            SendNote(styleChanged
                ? $"Your name is now {names.GetWizardName()} and your style changed. Log out to character select and back in to see it."
                : $"Your name is now {names.GetWizardName()}. Log out to character select and back in to see it everywhere.");
        }
        catch (Exception ex) {
            Logger.Error("Rename | The rename window answer failed. {0}", Logger.Args(ex.Message));
        }
    }

    private void SendNote(string text)
        => SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

}
