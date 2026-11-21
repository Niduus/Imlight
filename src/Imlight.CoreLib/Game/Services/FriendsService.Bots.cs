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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The server's own bots in the game's friends list ("Friendly Players"). Niduus, 2026-11-21: "mark all bots as friendly
/// players, this way they show up in this listing". Every bot wizard that is on (Ultimus, the TCO7, True Form, the Quest
/// Team and the Kitharas Family: OnlineList.IsBotWizard) is listed for every real player, with its level, school and where
/// it is, like a friend. Nothing is saved: a bot comes off the list when it logs off, and a player who removes one from the
/// list does not see it again until they log in again. Players can open a bot's page and go to it like a friend.
/// </summary>
internal partial class FriendsService {

    private const string BOT_FRIENDS_TIMER = "bot-friends";
    private static readonly TimeSpan s_botFriendsEvery = TimeSpan.FromSeconds(10);

    /// <summary>Asks for the bot friends to be looked at again (the list follows bots logging on, off and moving).</summary>
    internal sealed record BotFriendsCheck;

    // The bots on this player's list now, with what the list last showed for them.
    private readonly Dictionary<ulong, ShownBot> _botFriends = [];
    // Bots this player took off the list (until the next login), and the real friends (never listed twice).
    private readonly HashSet<ulong> _hiddenBots = [];
    private readonly HashSet<ulong> _realFriends = [];

    private sealed record ShownBot(ulong GameObjectId, string Zone, string Realm, int Level, uint Permissions, uint School);

    /// <summary>Whether a character is a bot this player's list shows as a friendly player.</summary>
    private bool IsShownBotFriend(ulong charId) => _botFriends.ContainsKey(charId);

    /// <summary>A player took a bot off their list: it stays off until they log in again.</summary>
    private void HideBotFriend(ulong charId) {
        if (_botFriends.Remove(charId) || OnlineList.IsBotWizard(charId)) {
            _hiddenBots.Add(charId);
        }
    }

    /// <summary>The friends list was asked for (at login): the bots that are on now go on it, then it keeps up.</summary>
    private void ShowBotFriends(Wizard owner, IEnumerable<ulong> realFriends) {
        if (owner is null || OnlineList.IsBotWizard(owner.CharId)) {
            return;
        }

        _botFriends.Clear();
        _hiddenBots.Clear();
        _realFriends.Clear();
        _realFriends.UnionWith(realFriends);
        RefreshBotFriends(owner);

        Timers.StartPeriodicTimer(BOT_FRIENDS_TIMER, new BotFriendsCheck(), s_botFriendsEvery, s_botFriendsEvery);
    }

    [InternalMessageHandler(typeof(BotFriendsCheck))]
    private void ReceiveBotFriendsCheck(BotFriendsCheck _) {
        var owner = GetActiveWizard();
        if (owner is null) {
            Timers.Cancel(BOT_FRIENDS_TIMER);

            return;
        }

        RefreshBotFriends(owner);
    }

    private void RefreshBotFriends(Wizard owner) {
        try {
            var online = new Dictionary<ulong, Imlight.CoreLib.WizardData.Models.Misc.OnlinePlayer>();
            foreach (var player in OnlinePlayerCollection.GetCachedOnlinePlayers()) {
                var charId = player.CharacterId;
                if (charId == 0 || charId == owner.CharId || _hiddenBots.Contains(charId) || _realFriends.Contains(charId)
                    || !OnlineList.IsBotWizard(charId)) {
                    continue;
                }

                online[charId] = player;
            }

            // Bots that logged off come off the list.
            foreach (var gone in _botFriends.Keys.Where(id => !online.ContainsKey(id)).ToList()) {
                _botFriends.Remove(gone);
                SendToSocket(new GAME_5_PROTOCOL.MSG_BUDDYDROP {
                    ListOwnerGID = owner.GameObjectID,
                    EntryGID = gone
                });
            }

            foreach (var (charId, player) in online) {
                var zone = FriendZoneText(charId, player);
                var realm = player.CurrentRealm ?? string.Empty;
                if (_botFriends.TryGetValue(charId, out var shown)) {
                    // Already listed: only told again when it moved or levelled.
                    if (shown.Zone == zone && shown.Realm == realm && shown.Level == player.Level) {
                        continue;
                    }

                    shown = shown with { Zone = zone, Realm = realm, Level = player.Level };
                    _botFriends[charId] = shown;
                    SendToSocket(new GAME_5_PROTOCOL.MSG_BUDDYSTATUSUPDATE {
                        ListOwnerGID = owner.GameObjectID,
                        EntryGID = charId,
                        Status = ONLINE_STATUS_CODE,
                        Permissions = shown.Permissions,
                        ZoneName = zone,
                        RealmName = realm,
                        FriendInfo = BotFriendInfo(shown.Level, shown.School),
                        FriendDate = 0,
                        FriendStatusDate = 0,
                        PreviousName = string.Empty
                    });

                    continue;
                }

                AddBotFriend(owner, charId, player, zone, realm);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Friendly players | Showing the bots on the friends list failed: {0}", Logger.Args(ex.Message));
        }
    }

    private void AddBotFriend(Wizard owner, ulong charId, Imlight.CoreLib.WizardData.Models.Misc.OnlinePlayer player,
                              string zone, string realm) {
        // A real friend (befriended before this) is already on the list from the saved friends.
        if (BuddyRelationshipCollection.AreFriends(owner.CharId, charId)) {
            _realFriends.Add(charId);

            return;
        }

        var bot = WizardCollection.GetCharacter(charId);
        var hexName = bot?.PlayerNameBehavior?.GetWizardNameAsByteHexString();
        if (string.IsNullOrWhiteSpace(hexName)) {
            return;
        }

        var level = player.Level > 0 ? player.Level : (int) bot.MagicSchoolBehavior.Level;
        var school = MagicSchoolIndex.ToSocialIndex(bot.MagicSchoolBehavior.MagicSchool);
        var permissions = (uint) (bot.Account?.GetAccountFlags() ?? 0);
        var gameObjectId = bot.GameObjectID;
        var now = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _botFriends[charId] = new ShownBot(gameObjectId, zone, realm, level, permissions, school);
        SendToSocket(new GAME_5_PROTOCOL.MSG_BUDDYENTRY {
            ListOwnerGID = owner.GameObjectID,
            EntryGID = charId,
            GameObjectID = gameObjectId,
            Name = DataManipulation.SpacedHexStringToBytes(hexName),
            Status = ONLINE_STATUS_CODE,
            FriendInfo = BotFriendInfo(level, school),
            PasswordChat = 0,
            Permissions = permissions,
            ZoneName = zone,
            RealmName = realm,
            Locale = 0,
            FriendDate = now,
            FriendStatusDate = now,
            PreviousName = string.Empty
        });

        Logger.Debug("Friendly players | {0} sees bot {1} (ID {2}) in {3}.",
            Logger.Args(owner.CharId, bot.PlayerNameBehavior.GetWizardName(), charId, zone));
    }

    // Same packing as BuildFriendInfo: level (high 16 bits), school (next byte), no friend symbol.
    private static uint BotFriendInfo(int level, uint school)
        => ((uint) Math.Clamp(level, 0, ushort.MaxValue) << 16) | (school << 8);

}
