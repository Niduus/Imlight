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
 * PIXIE POST SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Pixie Post: the messages wizards send each other, read in the Mail tab of the friends window.
 *
 * HOW THE GAME DOES IT (read from WizardGraphicalClient.exe, 2026-10-24):
 * - Sending: the friends window (a friend's "send mail" button, or Reply on a message) opens
 *   PixiePostSendMailWindow.gui, which sends MSG_PIXIEPOSTMESSAGE. ReceiverGID is the friend list entry's ID (the
 *   saved character ID), or on Reply the SenderGID of the message being read.
 * - Reading: the Mail tab shows one message at a time with "n/total" and back/next buttons. Each page sends
 *   MSG_REQUESTPIXIEPOSTMESSAGE with MessageNumber (from 0) and the server answers with the same message, filled in.
 *   TotalMessages 0 shows an empty mailbox. ExpireTime is the seconds left (shown as days and hours).
 * - Deleting: MSG_REQUESTDELETEMESSAGE (MailGID); the game then lowers its count and asks for the page again.
 * - MSG_PIXIEPOSTMAIL lights the new mail button on the HUD; opening the Mail tab turns it off again.
 * - MSG_PLAYPIXIEPOSTANIMATION plays the pixie flying in (IsSend 0) or out (IsSend 1) on PlayerGID.
 * - The game sends MSG_CHECK_MAIL when a wizard comes into the world.
 *
 * NOTE:
 * Gift mail from the Crown Shop (MSG_RETRIEVE_MAIL, MailWindow.gui) is separate, in GiftingService.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class PixiePostService(SessionActor sessionActor) : MessageService(sessionActor) {

    // [PixiePost] KeepDays: days a message stays in the mailbox. MaxMessages: the oldest go when the mailbox is full.
    private static int s_keepDays => Math.Max(ConfigurationManager.GetValue("PixiePost.KeepDays", 30), 1);
    private static int s_maxMessages => Math.Max(ConfigurationManager.GetValue("PixiePost.MaxMessages", 100), 1);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new PixiePostService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CHECK_MAIL))]
    private void ReceiveCheckMail(GAME_5_PROTOCOL.MSG_CHECK_MAIL message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            PruneExpired(wizard);
            if (wizard.PixiePosts.Any(p => !p.Read)) {
                SendToSocket(new WIZARD3_56_PROTOCOL.MSG_PIXIEPOSTMAIL());
            }
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | Checking the mailbox failed. {0}", Logger.Args(ex.Message));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTPIXIEPOSTMESSAGE))]
    private void ReceiveRequestMessage(WIZARD3_56_PROTOCOL.MSG_REQUESTPIXIEPOSTMESSAGE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            PruneExpired(wizard);
            SendPage(wizard, (int) message.MessageNumber);
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | Showing a message failed. {0}", Logger.Args(ex.Message));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_REQUESTDELETEMESSAGE))]
    private void ReceiveDeleteMessage(WIZARD3_56_PROTOCOL.MSG_REQUESTDELETEMESSAGE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            var mailId = message.MailGID;
            wizard.PixiePosts ??= new();
            var removed = wizard.PixiePosts.RemoveAll(p => p.MailId == mailId);
            Logger.Information("Pixie Post | {0} deleted message {1} ({2} removed).", Logger.Args(wizard.CharId, mailId, removed));
            if (removed > 0) {
                WizardCollection.UpdateCharacterPixiePosts(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | Deleting a message failed. {0}", Logger.Args(ex.Message));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_PIXIEPOSTMESSAGE))]
    private void ReceiveSendMessage(WIZARD3_56_PROTOCOL.MSG_PIXIEPOSTMESSAGE message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            if (!TryResolveRecipient(message.ReceiverGID, wizard, out var recipientCharId)) {
                Logger.Warning("Pixie Post | {0} sent mail to {1}, which is no wizard.", Logger.Args(wizard.CharId, message.ReceiverGID));
                InformGameClient("That wizard could not be found, so your Pixie Post was not sent.");

                return;
            }

            var post = new PixiePost {
                MailId = (ulong) RandomGen.GenerateGUID(),
                SenderCharId = wizard.CharId,
                SenderNameBase64 = ToBase64(DataManipulation.SpacedHexStringToBytes(wizard.PlayerNameBehavior.GetWizardNameAsByteHexString())),
                SubjectBase64 = ToBase64(message.Subject),
                MessageBase64 = ToBase64(message.Message),
                SentTime = Now(),
                TimeOffset = message.TimeOffset,
                ClientTime = message.ClientTime,
                PartyName = message.PartyName,
                PartyNameLocale = message.PartyNameLocale,
            };

            if (!Deliver(wizard, recipientCharId, post)) {
                InformGameClient("Your Pixie Post could not be delivered to that wizard.");

                return;
            }

            Logger.Information("Pixie Post | {0} sent \"{1}\" to {2}.", Logger.Args(wizard.CharId, (string) message.Subject, recipientCharId));
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_PLAYPIXIEPOSTANIMATION { PlayerGID = wizard.GameObjectID, IsSend = 1 });
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | Sending a message failed. {0}", Logger.Args(ex.Message));
        }
    }

    // A message for this session's wizard, from another session (or from the .mail command).
    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_PIXIEPOSTDELIVER))]
    private void ReceiveDeliver(CHARACTER_103_PROTOCOL.MSG_PIXIEPOSTDELIVER message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null || wizard.CharId != message.RecipientCharId) {
                // Logged out (or switched wizards) on the way: keep it in the database instead.
                WizardCollection.AddCharacterPixiePost(message.RecipientCharId, message.Post, s_maxMessages);

                return;
            }

            AddToOwnMailbox(wizard, message.Post);
        }
        catch (Exception ex) {
            Logger.Error("Pixie Post | Receiving a message failed. {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>
    /// Puts a post in a wizard's mailbox: the wizard's own session when online, otherwise the database.
    /// </summary>
    private bool Deliver(Wizard sender, ulong recipientCharId, PixiePost post) {
        if (recipientCharId == sender.CharId) {
            AddToOwnMailbox(sender, post);

            return true;
        }

        if (TryGetOnlinePlayer(recipientCharId, out var online) && !string.IsNullOrEmpty(online.ActorPath)) {
            Context.ActorSelection(online.ActorPath).Tell(new CHARACTER_103_PROTOCOL.MSG_PIXIEPOSTDELIVER {
                RecipientCharId = recipientCharId,
                Post = post,
            });

            return true;
        }

        var recipient = WizardCollection.GetCharacterUnloaded(recipientCharId);
        if (recipient is null) {
            return false;
        }

        if (recipient.FriendsBehavior?.HasPlayerBlocked(sender.CharId) == true) {
            Logger.Information("Pixie Post | {0} has {1} blocked; the message was dropped.", Logger.Args(recipientCharId, sender.CharId));

            // Blocked players are not told.
            return true;
        }

        return WizardCollection.AddCharacterPixiePost(recipientCharId, post, s_maxMessages);
    }

    private void AddToOwnMailbox(Wizard wizard, PixiePost post) {
        if (post.SenderCharId != wizard.CharId && wizard.FriendsBehavior?.HasPlayerBlocked(post.SenderCharId) == true) {
            Logger.Information("Pixie Post | {0} has {1} blocked; the message was dropped.", Logger.Args(wizard.CharId, post.SenderCharId));

            return;
        }

        wizard.PixiePosts ??= new();
        wizard.PixiePosts.Add(post);
        while (wizard.PixiePosts.Count > s_maxMessages) {
            wizard.PixiePosts.RemoveAt(0);
        }

        WizardCollection.UpdateCharacterPixiePosts(wizard);
        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_PIXIEPOSTMAIL());
        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_PLAYPIXIEPOSTANIMATION { PlayerGID = wizard.GameObjectID, IsSend = 0 });
    }

    private void SendPage(Wizard wizard, int number) {
        // Newest first.
        var posts = wizard.PixiePosts.OrderByDescending(p => p.SentTime).ThenByDescending(p => p.MailId).ToList();
        if (posts.Count == 0) {
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTPIXIEPOSTMESSAGE {
                MessageNumber = 0,
                TotalMessages = 0,
                Subject = ByteString.Empty,
                Message = ByteString.Empty,
                NameBlob = ByteString.Empty,
                Data = ByteString.Empty,
            });

            return;
        }

        number = Math.Clamp(number, 0, posts.Count - 1);
        var post = posts[number];
        var keepSeconds = (long) s_keepDays * 86400;
        var left = Math.Max(post.SentTime + keepSeconds - Now(), 60);

        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_REQUESTPIXIEPOSTMESSAGE {
            TimeOffset = post.TimeOffset,
            SenderGID = post.SenderCharId,
            Subject = FromBase64(post.SubjectBase64),
            Message = FromBase64(post.MessageBase64),
            NameBlob = FromBase64(post.SenderNameBase64),
            MessageNumber = (uint) number,
            TotalMessages = (uint) posts.Count,
            MailGID = post.MailId,
            PartyName = post.PartyName,
            PartyNameLocale = post.PartyNameLocale,
            SentTime = post.SentTime,
            ExpireTime = (uint) Math.Min(left, uint.MaxValue),
            Data = ByteString.Empty,
            AllowRecentWizards = 1,
        });

        if (!post.Read) {
            post.Read = true;
            WizardCollection.UpdateCharacterPixiePosts(wizard);
        }
    }

    private static void PruneExpired(Wizard wizard) {
        wizard.PixiePosts ??= new();
        var oldest = (long) Now() - (long) s_keepDays * 86400;
        if (wizard.PixiePosts.RemoveAll(p => p.SentTime < oldest) > 0) {
            WizardCollection.UpdateCharacterPixiePosts(wizard);
        }
    }

    // The friends list gives saved character IDs; accept a player object ID too.
    private static bool TryResolveRecipient(ulong id, Wizard sender, out ulong charId) {
        charId = 0;
        if (id == 0) {
            return false;
        }

        foreach (var candidate in new[] { id, Wizard.TryGetCharacterId(id, out var fromObject) ? fromObject : 0 }) {
            if (candidate == 0) {
                continue;
            }

            if (candidate == sender.CharId || TryGetOnlinePlayer(candidate, out _)
                || WizardCollection.GetCharacterUnloaded(candidate) is not null) {
                charId = candidate;

                return true;
            }
        }

        return false;
    }

    internal static PixiePost MakePost(Wizard sender, string subject, string text)
        => new() {
            MailId = (ulong) RandomGen.GenerateGUID(),
            SenderCharId = sender.CharId,
            SenderNameBase64 = ToBase64(DataManipulation.SpacedHexStringToBytes(sender.PlayerNameBehavior.GetWizardNameAsByteHexString())),
            SubjectBase64 = ToBase64((ByteString) subject),
            MessageBase64 = ToBase64((ByteString) text),
            SentTime = Now(),
        };

    private static uint Now() => (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string ToBase64(ByteString data) {
        byte[] bytes = data;

        return bytes is null || bytes.Length == 0 ? "" : Convert.ToBase64String(bytes);
    }

    private static ByteString FromBase64(string text) {
        if (string.IsNullOrEmpty(text)) {
            return ByteString.Empty;
        }

        try {
            return Convert.FromBase64String(text);
        } catch (FormatException) {
            return ByteString.Empty;
        }
    }

}
