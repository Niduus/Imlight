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
 *
 * ========================================================================
 * RECYCLING BIN SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Answers the game's Recycling Bin window (MSG_REQUESTRECYCLINGBIN): lists the items the wizard threw away and gives
 * one back for gold. See RecyclingBin for the message rules read from the client.
 *
 * USAGE EXAMPLE:
 * RecyclingBinService.SendBin(session, wizard); // opens the window from a chat command
 *
 * NOTE:
 * The game's own texts are used for every answer, so nothing here needs a game file change.
 */

using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.IO;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.ItemSafety;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class RecyclingBinService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new RecyclingBinService(parentActor));

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTRECYCLINGBIN))]
    private void ReceiveRequestRecyclingBin(WIZARD3_56_PROTOCOL.MSG_REQUESTRECYCLINGBIN message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            if (!RecyclingBin.Enabled) {
                InformGameClient("The Recycling Bin is turned off on this server.");

                return;
            }

            if (message.ItemGID == 0) {
                SendBin(SessionActor.ActorRef, wizard);
            }
            else {
                Reclaim(wizard, message.ItemGID);
            }
        }
        catch (Exception ex) {
            Logger.Error("Recycling Bin: MSG_REQUESTRECYCLINGBIN (item {0}) failed. {1}", Logger.Args(message.ItemGID, ex));
        }
    }

    /// <summary>
    /// Sends the wizard's bin to the game and opens its Recycling Bin window. An empty bin is said in chat, because the
    /// window needs at least one item to open safely.
    /// </summary>
    /// <returns>How many items were sent.</returns>
    internal static int SendBin(IActorRef session, Wizard wizard) {
        var items = RecyclingBin.ItemsFor(wizard.CharId);
        var blobs = new System.Collections.Generic.List<ByteString>(items.Count);
        foreach (var backup in items) {
            if (ItemSync.TrySerialize(backup.Item, out var blob)) {
                blobs.Add(blob);
            }
            else {
                Logger.Warning("Recycling Bin: could not show {0} ({1}) of {2}, its copy does not serialize.",
                    Logger.Args(backup.ItemName, backup.BackupId, wizard.CharId));
            }
        }

        if (blobs.Count == 0) {
            session.Tell(Chat(RecyclingBin.Days == 0
                ? "Your Recycling Bin is empty. Items you throw away wait there until you take them back."
                : $"Your Recycling Bin is empty. Items you throw away wait there for {RecyclingBin.Days} days."));

            return 0;
        }

        // The first empties the game's list, the last opens the window, the rest only add.
        for (var i = 0; i < blobs.Count; i++) {
            var status = i == blobs.Count - 1 ? RecyclingBin.STATUS_OPEN
                : i == 0 ? RecyclingBin.STATUS_FIRST
                : RecyclingBin.STATUS_ADD;
            session.Tell(new WIZARD3_56_PROTOCOL.MSG_REQUESTRECYCLINGBIN {
                SerializedItem = blobs[i],
                ItemGID = 0,
                Status = status,
            });
        }

        Logger.Information("Recycling Bin: showed {0} items to {1}.", Logger.Args(blobs.Count, wizard.CharId));

        return blobs.Count;
    }

    private void Reclaim(Wizard wizard, ulong itemId) {
        var backup = RecyclingBin.Find(wizard.CharId, itemId);
        if (backup is null) {
            InformGameClient("That item is no longer in your Recycling Bin.");
            Logger.Information("Recycling Bin: {0} asked for item {1}, which is not in their bin.", Logger.Args(wizard.CharId, itemId));

            return;
        }

        var cost = RecyclingBin.Cost;
        if (wizard.GameStats.m_currentGold < cost) {
            SendStatus(RecyclingBin.STATUS_NOT_ENOUGH_GOLD);

            return;
        }

        if (!ItemPlacement.FitsInBackpack(wizard, backup.Item)) {
            SendStatus(RecyclingBin.STATUS_NO_ROOM);

            return;
        }

        // The answer carries the item as the window listed it, so the game takes the right row out of its list.
        ItemSync.TrySerialize(backup.Item, out var listed);
        if (!ItemHistory.TryRestore(wizard, backup, SessionActor.ActorRef, out var result)) {
            InformGameClient($"{backup.ItemName} could not be taken out of the Recycling Bin: {result}.");
            Logger.Warning("Recycling Bin: {0} ({1}) of {2} was not given back: {3}.",
                Logger.Args(backup.ItemName, backup.BackupId, wizard.CharId, result));

            return;
        }

        if (cost > 0) {
            wizard.RemoveGold(cost);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch,
            });
        }

        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTRECYCLINGBIN {
            SerializedItem = listed,
            ItemGID = itemId,
            Status = RecyclingBin.STATUS_RECLAIMED,
        });
        Logger.Information("Recycling Bin: {0} ({1}) went back to {2} for {3} gold: {4}.",
            Logger.Args(backup.ItemName, backup.BackupId, wizard.CharId, cost, result));
    }

    private void SendStatus(uint status)
        => SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTRECYCLINGBIN { Status = status });

    private static EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE Chat(string text)
        => new() { Message = text, Modal = 0 };

}
