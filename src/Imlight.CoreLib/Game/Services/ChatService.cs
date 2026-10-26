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
 * CHAT SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages in-game chat functionality, including message processing, 
 * command handling, and logging of player communications.
 * 
 * USAGE EXAMPLE:
 * Internal service handling chat-related messages within the game server's 
 * session management system.
 * 
 * NOTE:
 * - Implements chat command processing for authorized users
 * 
 * TODO:
 * - Review and enhance chat command authorization
 * - Improve message sanitization logic
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/26/2026
 */

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class ChatService(SessionActor sessionActor) : MessageService(sessionActor) {

    // Always make sure this command prefix is within the bounds of the Regex.
    private const string CommandPrefix = ".";
    private const string MessageRegex = "[^a-zA-Z0-9\\p{P} ]";
    private const uint TeamChatFlags = 0x50;

    private Wizard _selectedCharacter;
    private Account _selectedAccount;

    private readonly IActorRef _dispatcherRef = CommandDispatcher.Instance;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new ChatService(parentActor));

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALCHAT))]
    private void ReceiveRequestRadialChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALCHAT message) {
        var charObj = GetActiveGameObject();
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();

        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // Craft the wizard name.
        var byteName = wizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(byteName);

        var cleanedMessage = CleanMessageTrash(message.Message);

        // Parse in-game chat commands. Do not broadcast it to the zone.
        if (cleanedMessage.StartsWith(CommandPrefix) && account.AuthLevel > AuthLevel.None) {
            SendChatCommand(cleanedMessage, charObj, wizard);

            return;
        }

        if (!TryApplyChatLevel(message.Message, wizard, out var shown, out var asTyped)) {
            return;
        }

        LogChatMessage(wizard.PlayerNameBehavior.GetWizardName(), cleanedMessage, wizard.Zone);
        SaveChatLog(cleanedMessage, charObj, wizard);

        // Broadcast the message to the zone, skipping players who have ignored the sender.
        var msg = asTyped
            ? (IMessage) ShownAsTyped(sourceName, charObj.m_globalID, message.Message, ChatFilter.RadialChatType, filter: 2)
            : new GAME_5_PROTOCOL.MSG_RADIALCHAT {
                Message = shown is null ? message.Message : (ByteString) shown,
                SourceID = charObj.m_globalID,
                SourceName = sourceName,
                Filter = 2,
            };
        SendFilteredZoneMessage(msg, wizard.CharId, wizard.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHAT))]
    private void ReceiveRequestRadialQuickChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHAT message) {
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        var globalId = GetActiveGameObject().m_globalID;
        var character = GetActiveWizard();
        var byteName = character.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var src = DataManipulation.SpacedHexStringToBytes(byteName);

        var msg = new GAME_5_PROTOCOL.MSG_RADIALQUICKCHAT() {
            MessageID = message.MessageID,
            SourceID = globalId,
            SourceName = src,
            Filter = 0,
        };
        SendFilteredZoneMessage(msg, character.CharId, character.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHATEXT))]
    private void ReceiveRequestRadialQuickChatExt(GAME_5_PROTOCOL.MSG_REQUESTRADIALQUICKCHATEXT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var character = GetActiveWizard();
        LogQuickChat(character, message.Message.ToString());

        var msg = new GAME_5_PROTOCOL.MSG_RADIALQUICKCHATEXT {
            Message = message.Message,
            SourceID = GetActiveGameObject().m_globalID,
            SourceName = GetSourceName(character),
            Filter = 0,
        };
        SendFilteredZoneMessage(msg, character.CharId, character.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHAT))]
    private void ReceiveWhisperRadialQuickChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHAT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var myWizard = GetActiveWizard();
        var sourceName = GetSourceName(myWizard);
        LogQuickChat(myWizard, $"#{message.MessageID} to {message.TargetID}");

        SendDirectedQuickChat(message.TargetID, myWizard,
            whisper: new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHAT {
                SourceName = sourceName,
                SourceID = myWizard.CharId,
                MessageID = message.MessageID,
                Filter = 0
            },
            teamLine: new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHAT {
                SourceName = sourceName,
                SourceID = myWizard.CharId,
                MessageID = message.MessageID,
                Filter = 0,
                Flags = TeamChatFlags
            });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT))]
    private void ReceiveWhisperRadialChatExt(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDQUICKCHATEXT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var myWizard = GetActiveWizard();
        var sourceName = GetSourceName(myWizard);
        LogQuickChat(myWizard, $"{message.Message} (to {message.TargetID})");

        SendDirectedQuickChat(message.TargetID, myWizard,
            whisper: new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHATEXT {
                SourceName = sourceName,
                SourceID = myWizard.CharId,
                Message = message.Message,
                Filter = 0
            },
            teamLine: new GAME_5_PROTOCOL.MSG_DIRECTEDQUICKCHATEXT {
                SourceName = sourceName,
                SourceID = myWizard.CharId,
                Message = message.Message,
                Filter = 0,
                Flags = TeamChatFlags
            });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALFRIENDQUICKCHAT))]
    private void ReceiveFriendQuickChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALFRIENDQUICKCHAT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var character = GetActiveWizard();
        LogQuickChat(character, $"#{message.MessageID} (friends)");

        SendToNearbyFriends(message.PlayerGID, character, new GAME_5_PROTOCOL.MSG_RADIALQUICKCHAT {
            MessageID = message.MessageID,
            SourceID = GetActiveGameObject().m_globalID,
            SourceName = GetSourceName(character),
            Filter = 0,
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTRADIALFRIENDQUICKCHATEXT))]
    private void ReceiveFriendQuickChatExt(WIZARD2_53_PROTOCOL.MSG_REQUESTRADIALFRIENDQUICKCHATEXT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var character = GetActiveWizard();
        LogQuickChat(character, $"{message.Message} (friends)");

        SendToNearbyFriends(message.PlayerGID, character, new GAME_5_PROTOCOL.MSG_RADIALQUICKCHATEXT {
            Message = message.Message,
            SourceID = GetActiveGameObject().m_globalID,
            SourceName = GetSourceName(character),
            Filter = 0,
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTRADIALFRIENDCHAT))]
    private void ReceiveFriendChat(GAME_5_PROTOCOL.MSG_REQUESTRADIALFRIENDCHAT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var charObj = GetActiveGameObject();
        var wizard = GetActiveWizard();
        var cleanedMessage = CleanMessageTrash(message.Message);

        if (cleanedMessage.StartsWith(CommandPrefix) && GetActiveAccount().AuthLevel > AuthLevel.None) {
            SendChatCommand(cleanedMessage, charObj, wizard);

            return;
        }

        if (!TryApplyChatLevel(message.Message, wizard, out var shown, out var asTyped)) {
            return;
        }

        LogChatMessage(wizard.PlayerNameBehavior.GetWizardName(), $"{cleanedMessage} (friends)", wizard.Zone);
        SaveChatLog(cleanedMessage, charObj, wizard);

        SendToNearbyFriends(message.PlayerGID, wizard, asTyped
            ? (IMessage) ShownAsTyped(GetSourceName(wizard), charObj.m_globalID, message.Message, ChatFilter.RadialChatType, filter: 2)
            : new GAME_5_PROTOCOL.MSG_RADIALCHAT {
                Message = shown is null ? message.Message : (ByteString) shown,
                SourceID = charObj.m_globalID,
                SourceName = GetSourceName(wizard),
                Filter = 2,
            });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTZONECLUSTERQUICKCHAT))]
    private void ReceiveZoneClusterQuickChat(GAME_5_PROTOCOL.MSG_REQUESTZONECLUSTERQUICKCHAT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var character = GetActiveWizard();
        LogQuickChat(character, $"#{message.MessageID}");

        var msg = new GAME_5_PROTOCOL.MSG_RADIALZONECLUSTERQUICKCHAT {
            MessageID = message.MessageID,
            SourceID = GetActiveGameObject().m_globalID,
            SourceName = GetSourceName(character),
            Filter = 0,
            IsOwner = message.IsOwner,
        };
        SendFilteredZoneMessage(msg, character.CharId, character.Zone);
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTZONECLUSTERQUICKCHATEXT))]
    private void ReceiveZoneClusterQuickChatExt(WIZARD2_53_PROTOCOL.MSG_REQUESTZONECLUSTERQUICKCHATEXT message) {
        if (IsMutedAndTold()) {
            return;
        }

        var character = GetActiveWizard();
        LogQuickChat(character, message.Message.ToString());

        var msg = new WIZARD2_53_PROTOCOL.MSG_RADIALZONECLUSTERQUICKCHATEXT {
            Message = message.Message,
            SourceID = GetActiveGameObject().m_globalID,
            SourceName = GetSourceName(character),
            Filter = 0,
            IsOwner = message.IsOwner,
        };
        SendFilteredZoneMessage(msg, character.CharId, character.Zone);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT))]
    private void ReceiveWhisperDirectChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT message) {
        // A player is whispering to another player, directly.
        // This message uses a wide-character string (16 bits per character).
        var targetID = message.TargetID;

        // Check if the sender is muted.
        var account = GetActiveAccount();
        if (account.InfractionHistory.IsCurrentlyMuted) {
            InformGameClient("You are currently muted.");

            return;
        }

        // The client's /party line is a directed chat aimed at the group's channel ID.
        var myWizard = GetActiveWizard();
        if (!TryApplyChatLevel(message.Message, myWizard, out var shown, out var asTyped)) {
            return;
        }

        if (shown is not null) {
            message.Message = shown;
        }

        if (GroupRegistry.IsGroupChannel(targetID)) {
            SendGroupChat(message, myWizard, asTyped);

            return;
        }

        // Check if the target has ignored the sender.
        if (BuddyRelationshipCollection.HasBlocked(targetID, myWizard.CharId)) {
            return;
        }

        if (!TryGetOnlinePlayer(targetID, out var targetPlayer)) {
            // Inform the user of error if the target is offline.
            SendToSocket(new GAME_5_PROTOCOL.MSG_DIRECTEDCHATFAIL());

            return;
        }

        // Send the directed chat to the target player.
        var hexName = myWizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var sourceName = DataManipulation.SpacedHexStringToBytes(hexName);
        var msg = asTyped
            ? (IMessage) ShownAsTyped(sourceName, myWizard.CharId, message.Message, ChatFilter.DirectedChatType, filter: 0)
            : new GAME_5_PROTOCOL.MSG_DIRECTEDCHAT {
                SourceName = sourceName,
                SourceID = myWizard.CharId,
                Message = message.Message,
                Filter = 0
            };

        var targetActorPath = targetPlayer.ActorPath;
        Context.ActorSelection(targetActorPath).Tell(msg);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYSTATS))]
    private void ReceivePlayerSelect(GAME_5_PROTOCOL.MSG_BUDDYSTATS message) {
        // Regular players have no reason to be able to select other players
        // for command context.
        var localAccount = GetActiveAccount();
        if (localAccount.AuthLevel < AuthLevel.HallMonitor) {
            return;
        }

        if (message.BuddyID == 0) {
            _selectedCharacter = null;
            _selectedAccount = null;

            return;
        }
        
        if (!Wizard.TryGetCharacterId(message.BuddyID, out var characterId)) {
            return;
        }

        var persistentCharacter = WizardCollection.GetCharacter(characterId);
        if (persistentCharacter is null) {
            return;
        }

        var selectedAccount = AccountCollection.GetAccount(persistentCharacter.AccountId);
        if (selectedAccount is null) {
            return;
        }

        // Cache for our next command.
        _selectedCharacter = persistentCharacter;
        _selectedAccount = selectedAccount;
    }

    private bool IsMutedAndTold() {
        if (!GetActiveAccount().InfractionHistory.IsCurrentlyMuted) {
            return false;
        }

        InformGameClient("You are currently muted.");

        return true;
    }

    private static ByteString GetSourceName(Wizard wizard)
        => DataManipulation.SpacedHexStringToBytes(wizard.PlayerNameBehavior.GetWizardNameAsByteHexString());

    private static void LogQuickChat(Wizard wizard, string what)
        => LogChatMessage(wizard.PlayerNameBehavior.GetWizardName(), $"(quick chat) {what}", wizard.Zone);

    /// <summary>
    /// The client aims friend-list whispers at a character and team lines at the group (by
    /// channel or group ID). The target may come as a character ID or a game object ID.
    /// </summary>
    private void SendDirectedQuickChat(ulong targetId, Wizard sender, IMessage whisper, IMessage teamLine) {
        if (GroupRegistry.TryGetGroup(sender.CharId, out var group)
            && (group.ChannelId == targetId || group.GroupId == targetId)) {
            SendToTeam(group, sender.CharId, teamLine);

            return;
        }

        if (!TryFindOnlinePlayer(targetId, out var targetPlayer)) {
            Logger.Information("Quick chat: nobody online with ID {0}.", Logger.Args(targetId));
            SendToSocket(new GAME_5_PROTOCOL.MSG_DIRECTEDCHATFAIL());

            return;
        }

        if (BuddyRelationshipCollection.HasBlocked(targetPlayer.CharacterId, sender.CharId)) {
            return;
        }

        Context.ActorSelection(targetPlayer.ActorPath).Tell(whisper);
    }

    private void SendToTeam(GroupSnapshot group, ulong senderCharId, IMessage msg) {
        var blockedBy = BuddyRelationshipCollection.GetCharactersWhoBlocked(senderCharId);

        foreach (var member in group.Members) {
            if (member.CharId == senderCharId || blockedBy.Contains(member.CharId)) {
                continue;
            }

            if (TryGetOnlinePlayer(member.CharId, out var memberPlayer)) {
                Context.ActorSelection(memberPlayer.ActorPath).Tell(msg);
            }
        }
    }

    /// <summary>
    /// Friend chat goes to the one friend the client named, or, when it names nobody else,
    /// to every friend in the sender's zone.
    /// </summary>
    private void SendToNearbyFriends(ulong playerGid, Wizard sender, IMessage msg) {
        if (TryFindOnlinePlayer(playerGid, out var namedPlayer) && namedPlayer.CharacterId != sender.CharId) {
            if (!BuddyRelationshipCollection.HasBlocked(namedPlayer.CharacterId, sender.CharId)) {
                Context.ActorSelection(namedPlayer.ActorPath).Tell(msg);
            }

            return;
        }

        var blockedBy = BuddyRelationshipCollection.GetCharactersWhoBlocked(sender.CharId);
        foreach (var player in OnlinePlayerCollection.GetPlayersInZone(sender.Zone)) {
            if (player.CharacterId == sender.CharId
                || blockedBy.Contains(player.CharacterId)
                || !BuddyRelationshipCollection.AreFriends(sender.CharId, player.CharacterId)) {
                continue;
            }

            Context.ActorSelection(player.ActorPath).Tell(msg);
        }
    }

    private static bool TryFindOnlinePlayer(ulong id, out OnlinePlayer onlinePlayer) {
        if (TryGetOnlinePlayer(id, out onlinePlayer)) {
            return true;
        }

        return Wizard.TryGetCharacterId(id, out var charId) && TryGetOnlinePlayer(charId, out onlinePlayer);
    }

    private static string CleanMessageTrash(ByteString message) {
        if (message.IsEmpty) {
            return string.Empty;
        }

        // Remove the first byte, unless it's the command prefix.
        if (!message.ToString().StartsWith(CommandPrefix)) {
            message = ((byte[])message)[1..];
        }

        // Define a regular expression pattern to keep alphanumeric characters and punctuation
        string validCharactersPattern = MessageRegex; // \p{P} matches any punctuation character
        var cleanedMessage = Regex.Replace(message.ToString(), validCharactersPattern, "").Trim();

        return cleanedMessage;
    }

    private void SendGroupChat(GAME_5_PROTOCOL.MSG_REQUESTDIRECTEDCHAT message, Wizard wizard, bool asTyped) {
        var hexName = wizard.PlayerNameBehavior.GetWizardNameAsByteHexString();
        var text = message.Message.ToString() ?? string.Empty;

        GroupDirectory.Instance?.Tell(new GROUP_109_PROTOCOL.MSG_GROUPCHAT {
            SenderCharId = wizard.CharId,
            ChannelId = message.TargetID,
            SourceName = DataManipulation.SpacedHexStringToBytes(hexName),
            Message = message.Message,
            ShowAsTyped = asTyped,
            CharIdsIgnoringSender = BuddyRelationshipCollection.GetCharactersWhoBlocked(wizard.CharId)
        });

        Logger.Information("[Group {Channel}] {Name}: {Message}",
            Logger.Args(message.TargetID, wizard.PlayerNameBehavior.GetWizardName(), text));
        SaveChatLog(text, GetActiveGameObject(), wizard);
    }

    /// <summary>
    /// Holds typed chat to the sender's chat level (<see cref="ChatFilter"/>). False when nothing may be sent (menu chat
    /// only; the player is told). Otherwise <paramref name="shown"/> is null to send the message as typed, or the text
    /// the others get instead ("...").
    /// </summary>
    private bool TryApplyChatLevel(string typed, Wizard wizard, out string shown, out bool asTyped) {
        shown = null;
        asTyped = false;
        typed ??= string.Empty;
        var mode = ChatFilter.CurrentChatMode(GetActiveAccount());
        var forOthers = ChatFilter.ForOthers(typed, mode, out var why);
        if (forOthers is null) {
            Logger.Information("Chat: {0} cannot type in chat ({1}): {2}", Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), why, typed));
            InformGameClient("Your account has menu chat only, so typed chat is turned off.");

            return false;
        }

        if (!ReferenceEquals(forOthers, typed)) {
            Logger.Information("Chat: {0}'s message shows as \"{1}\" ({2}): {3}",
                Logger.Args(wizard.PlayerNameBehavior.GetWizardName(), forOthers, why, typed));
            shown = forOthers;
        }
        else if (ChatFilter.ShowsRedWords(typed, mode)) {
            asTyped = true;
        }

        return true;
    }

    /// <summary>
    /// A typed line the game must show as it is, red words and all (<see cref="ChatFilter.ShowAsTypedChatBit"/>): sent as
    /// channel chat, whose Flags the game takes as the line's chat type.
    /// </summary>
    private static GAME_5_PROTOCOL.MSG_CHANNELCHAT ShownAsTyped(ByteString sourceName, ulong sourceId, string text, uint chatType, byte filter)
        => new() {
            SourceName = sourceName,
            SourceID = sourceId,
            Message = text,
            TargetID = 0,
            Filter = filter,
            Flags = chatType | ChatFilter.ShowAsTypedChatBit,
        };

    private static void LogChatMessage(string name, string message, string zoneName)
        => Logger.Information("[{0}] {1}: {2}", Logger.Args(zoneName, name, message));

    private static void SaveChatLog(string message, CoreObject charObj, Wizard character) 
        => ChatLogCollection.AddChatLog(new ChatLog() {
            TimeStamp = DateTime.UtcNow,
            ZoneName = character.Zone,
            CharacterId = character.CharId,
            AccountId = character.AccountId,
            Message = message,
        });

    /// <summary>
    /// Sends a zone chat message to all online players in the zone, skipping those
    /// who have ignored the sender.
    /// </summary>
    private void SendFilteredZoneMessage(IMessage msg, ulong senderCharId, string zoneName) {
        var blockedBy = BuddyRelationshipCollection.GetCharactersWhoBlocked(senderCharId);
        var playersInZone = OnlinePlayerCollection.GetPlayersInZone(zoneName);

        foreach (var player in playersInZone) {
            // Skip the sender (they see their own message client-side).
            if (player.CharacterId == senderCharId) {
                continue;
            }

            // Skip players who have blocked the sender.
            if (blockedBy.Contains(player.CharacterId)) {
                continue;
            }

            Context.ActorSelection(player.ActorPath).Tell(msg);
        }
    }

    private void SendChatCommand(string input, CoreObject charObj, Wizard character) {
        var account = GetActiveAccount();

        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = input[1..], // Remove the command prefix
            ActorRef = SessionActor.ActorRef,
            CoreObject = charObj,
            Wizard = character,
            Account = account,
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedCharacter,
            SelectedAccount = _selectedAccount
        });
    }

}