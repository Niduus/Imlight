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
/// Codes in game: giving a wizard what codes redeemed at character select (before 2026-10-24) are still waiting to
/// give, and `.code watch`. The server never uses the game's promo code window (Niduus, 2026-10-24: "Completely remove
/// Promo Code UI usage on the server"); codes are redeemed in chat with `.code HYPER`.
/// </summary>
internal class PromoCodeService(SessionActor sessionActor) : MessageService(sessionActor) {

    private System.Threading.Timer _watchTimer;

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

    // `.code watch`: every message the game sends for a while is written to the log, to see what a button sends.
    [InternalMessageHandler(typeof(WatchClientMessages))]
    private void ReceiveWatch(WatchClientMessages message) {
        SessionActor.ClientMessageLogPrefix = "Code watch";
        _watchTimer?.Dispose();
        _watchTimer = new System.Threading.Timer(_ => {
            if (SessionActor.ClientMessageLogPrefix == "Code watch") {
                SessionActor.ClientMessageLogPrefix = null;
            }
        }, null, TimeSpan.FromSeconds(message.Seconds), System.Threading.Timeout.InfiniteTimeSpan);
        Logger.Information("Codes | Writing every message the game sends to the log for {0} seconds.", Logger.Args(message.Seconds));
    }

    protected override void OnDispose() {
        base.OnDispose();
        _watchTimer?.Dispose();
    }

}

/// <summary>Tells a session's PromoCodeService to log every message the game sends for a while (`.code watch`).</summary>
internal sealed class WatchClientMessages : IServerMessage {

    public byte MessageOrder { get; } = 1;
    public byte ServiceID { get; } = 104;

    public int Seconds { get; init; } = 120;

}
