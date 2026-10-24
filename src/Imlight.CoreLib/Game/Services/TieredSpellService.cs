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
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The game's spell upgrade window (the Spellements tab of the spellbook): which spell groups show, upgrading a
/// spell into its next tier, and refunding a retired upgrade. See <see cref="TieredSpells"/>.
/// </summary>
internal class TieredSpellService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TieredSpellService(parentActor));

    // The game asks which spell groups to show (the deck window, the HUD and the upgrade window all ask).
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CANSEETIEREDSPELLGROUPS))]
    private void ReceiveCanSeeTieredSpellGroups(WIZARD2_53_PROTOCOL.MSG_CANSEETIEREDSPELLGROUPS message) {
        try {
            if (!TieredSpells.Enabled) {
                return;
            }

            SendVisibility();
        }
        catch (Exception ex) {
            Logger.Error("Spell upgrades | Answering which spell groups show failed. {0}", Logger.Args(ex.Message));
        }
    }

    // The upgrade window's Upgrade button.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLUPGRADE))]
    private void ReceiveTieredSpellUpgrade(WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLUPGRADE message) {
        var success = false;
        try {
            var wizard = GetActiveWizard();
            Logger.Information("Spell upgrades | The game asks to upgrade spell {0} into {1} (group {2}).",
                Logger.Args(message.SpellID, message.SpellIDNew, message.TieredSpellGroup));
            success = TieredSpells.TryUpgrade(SessionActor.ActorRef, wizard, message.SpellID, message.SpellIDNew, out var text);
            if (!success) {
                Logger.Information("Spell upgrades | Not upgraded: {0}", Logger.Args(text));
                SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
            }
        }
        catch (Exception ex) {
            Logger.Error("Spell upgrades | Upgrading a spell failed. {0}", Logger.Args(ex.Message));
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLUPGRADERESP {
            Success = (byte) (success ? 1 : 0),
            FailedRequirementIndex = success ? -1 : 0,
        });
        SendVisibility();
    }

    // Refunding a retired upgrade.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLREFUND))]
    private void ReceiveTieredSpellRefund(WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLREFUND message) {
        var success = false;
        try {
            var wizard = GetActiveWizard();
            Logger.Information("Spell upgrades | The game asks to refund spell {0} (back to {1}).",
                Logger.Args(message.SpellID, message.SpellIDOld));
            success = TieredSpells.TryRefund(SessionActor.ActorRef, wizard, message.SpellID, message.SpellIDOld, out var text);
            SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
        catch (Exception ex) {
            Logger.Error("Spell upgrades | Refunding a spell failed. {0}", Logger.Args(ex.Message));
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_TIEREDSPELLREFUNDRESP {
            Success = (byte) (success ? 1 : 0),
            FailedRequirementIndex = success ? -1 : 0,
        });
        SendVisibility();
    }

    private void SendVisibility() {
        var answer = TieredSpells.VisibilityMessage(GetActiveWizard());
        if (answer is not null) {
            SendToSocket(answer);
        }
    }

}
