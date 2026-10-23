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
using Imlight.CoreLib.Game.Codes;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Promo codes in game: the promo code window's Submit button, the Idle button of the Options book (it opens the promo
/// code window, Niduus 2026-10-23), and giving a wizard what codes redeemed at character select are waiting to give.
/// </summary>
internal class PromoCodeService(SessionActor sessionActor) : MessageService(sessionActor) {

    /// <summary>The promo code window, as the game's own character select opens it (PromoCodeEntryWindow).</summary>
    internal const string CODE_WINDOW = "promoCodeEntry.gui";

    // [Codes] IdleOpensCodeWindow: the Options book's Idle button opens the promo code window.
    private static readonly bool s_idleOpensWindow = ConfigurationManager.GetValue("Codes.IdleOpensCodeWindow", true);

    private DateTime _lastNotAfk = DateTime.MinValue;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PromoCodeService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            var wizard = GetActiveWizard();
            foreach (var note in PromoCodes.DeliverWaiting(wizard?.Account, wizard, SessionActor.ActorRef)) {
                SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = note, Modal = 0 });
            }
        }
        catch (Exception ex) {
            Logger.Error("Codes | Giving the waiting code rewards failed. {0}", Logger.Args(ex.Message));
        }
    }

    // The promo code window's Submit button.
    [MessageHandler(typeof(LOGIN_7_PROTOCOL.MSG_FULFILLPROMOCODE))]
    private void ReceiveFulfillPromoCode(LOGIN_7_PROTOCOL.MSG_FULFILLPROMOCODE message) {
        try {
            string code = message.Code;
            var wizard = GetActiveWizard();
            Logger.Information("Codes | {0} entered \"{1}\" in the code window in game.", Logger.Args(wizard?.CharId, code));
            var success = PromoCodes.Redeem(wizard?.Account, code, wizard, SessionActor.ActorRef, out var answer);
            SendToSocket(new LOGIN_7_PROTOCOL.MSG_FULFILLPROMOCODE {
                Code = code,
                Success = success ? 1u : 0u,
                ItemList = success ? answer : "",
                ErrorDesc = success ? "" : answer
            });
            SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = answer, Modal = 0 });
        }
        catch (Exception ex) {
            Logger.Error("Codes | Redeeming a code in game failed. {0}", Logger.Args(ex.Message));
        }
    }

    // The Options book's Idle button (SettingsWindow::OnIdleButtonClick) sends MSG_NOT_AFK and then again every 5
    // minutes while idle. The game's patcher sends it too, at most every 5 minutes, while it downloads files.
    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_NOT_AFK))]
    private void ReceiveNotAfk(GAME_5_PROTOCOL.MSG_NOT_AFK message) {
        try {
            var now = DateTime.UtcNow;
            var gap = (now - _lastNotAfk).TotalSeconds;
            _lastNotAfk = now;

            // About 5 minutes after the last one is the idle heartbeat, not a new press.
            var heartbeat = gap is >= 280 and <= 320;
            Logger.Information("Codes | The game sent MSG_NOT_AFK ({0:0} s after the last one){1}.", Logger.Args(
                gap > 1e6 ? 0 : gap, !s_idleOpensWindow ? ", [Codes] IdleOpensCodeWindow is off" : heartbeat ? ", the idle heartbeat" : ": opening the code window"));
            if (s_idleOpensWindow && !heartbeat) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_SHOWGUI { GUIFileName = CODE_WINDOW, StageIndex = 0 });
            }
        }
        catch (Exception ex) {
            Logger.Error("Codes | Answering the Idle button failed. {0}", Logger.Args(ex.Message));
        }
    }

}
