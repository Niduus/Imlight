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
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.TeamUp;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The game's Friendly Players window (its own list, not the friends list). Niduus, 2026-11-21: "mark all bots as
/// friendly players, this way they show up in this listing". The window asks with MSG_REQUESTFRIENDLYPLAYERS and is
/// answered with the same message holding a FriendlyPlayerInfoList, the way the Recent tab is answered. Every bot wizard
/// that is on (OnlineList.IsBotWizard: Ultimus, the TCO7, True Form, the Quest Team and the Kitharas Family) is listed with
/// its level, school and where it is. Nothing is saved and the friends list is not touched.
/// </summary>
internal partial class FriendsService {

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERS))]
    private void ReceiveRequestFriendlyPlayers(WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERS message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        var list = new FriendlyPlayerInfoList { m_friendlyPlayerInfoList = [] };
        foreach (var player in OnlinePlayerCollection.GetCachedOnlinePlayers()) {
            var charId = player.CharacterId;
            if (charId == 0 || charId == wizard.CharId || !OnlineList.IsBotWizard(charId)) {
                continue;
            }

            var bot = WizardCollection.GetCharacterUnloaded(charId);
            var nameHex = bot?.PlayerNameBehavior?.GetWizardNameAsByteHexString();
            if (string.IsNullOrWhiteSpace(nameHex)) {
                continue;
            }

            var level = player.Level > 0 ? player.Level : (int) (bot.MagicSchoolBehavior?.Level ?? 0);
            list.m_friendlyPlayerInfoList.Add(new FriendlyPlayerInfo {
                m_characterGID = (GID) charId,
                m_nameBlob = new ByteString(DataManipulation.SpacedHexStringToBytes(nameHex)),
                m_schoolCode = bot.MagicSchoolBehavior is null ? 0 : MagicSchoolIndex.ToSocialIndex(bot.MagicSchoolBehavior.MagicSchool),
                m_zoneDisplayName = FriendlyZoneName(charId, player),
                m_level = (byte) Math.Clamp(level, 0, byte.MaxValue),
                m_groupStatus = 0,
                m_hasFreeChat = 1,
                m_isOnline = true,
                m_endorseScore = 0,
            });
        }

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );

        if (!serializer.Serialize(list, 1, out var listBytes)) {
            Logger.Error("Friendly players | {0} opened Friendly Players, but the list could not be made.",
                Logger.Args(wizard.CharId));

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERS {
            PlayerGID = message.PlayerGID != 0 ? message.PlayerGID : wizard.GameObjectID,
            Data = listBytes,
        });

        Logger.Information("Friendly players | Sent {0} the Friendly Players list with {1} bot(s).",
            Logger.Args(wizard.CharId, list.m_friendlyPlayerInfoList.Count));
    }

    /// <summary>
    /// Where a listed wizard is. The window looks the name up in the game's text (a key such as "WizardZone_00000698", the
    /// zone's own display name), so plain words showed as an empty box; plain words are only sent when the zone has no key.
    /// </summary>
    private static string FriendlyZoneName(ulong charId, Imlight.CoreLib.WizardData.Models.Misc.OnlinePlayer player) {
        var key = TeamUpSigils.DisplayKeyNow(player?.CurrentZone);

        return string.IsNullOrEmpty(key) ? FriendZoneText(charId, player) : key;
    }

    /// <summary>
    /// The window's "Current Quest" boxes (Niduus, 2026-11-21: "not showing their quests"): it asks with
    /// MSG_REQUESTFRIENDLYPLAYERQUEST for the wizards on the page and is answered with the same message holding a
    /// FriendlyPlayerQuestInfo (character ids and quest titles, as game text keys like the quest journal's). A bot's quest is
    /// read from its own session: its main story quest when it has one, else the first quest it holds.
    /// </summary>
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERQUEST))]
    private void ReceiveRequestFriendlyPlayerQuest(WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERQUEST message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        byte[] asked = message.Data;
        var askedIds = new List<ulong>();
        if (TryReadFriendlyQuestAsk(asked, out var askedInfo)) {
            askedIds.AddRange((askedInfo.m_characterIDList ?? []).Select(id => (ulong) id).Where(id => id != 0));
        }
        else {
            Logger.Information("Friendly players | {0} asked for quests with data the server could not read ({1} bytes: {2}).",
                Logger.Args(wizard.CharId, asked?.Length ?? 0, asked is null ? "" : Convert.ToHexString(asked.Take(64).ToArray())));
        }

        // Nothing readable asked: every bot that is on (the window keeps the ones on its page).
        if (askedIds.Count == 0) {
            askedIds.AddRange(OnlinePlayerCollection.GetCachedOnlinePlayers()
                .Select(p => p.CharacterId).Where(id => id != 0 && id != wizard.CharId && OnlineList.IsBotWizard(id)));
        }

        // Each bot's own session is asked at once, so a page of bots waits about as long as one.
        var asks = new List<(ulong CharId, Task<CHARACTER_103_PROTOCOL.MSG_CHARACTER> Ask)>();
        foreach (var charId in askedIds.Distinct()) {
            if (!OnlineList.IsBotWizard(charId) || !TryGetOnlinePlayer(charId, out var online)) {
                continue;
            }

            asks.Add((charId, Context.ActorSelection(online.ActorPath).Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(
                new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(), TimeSpan.FromSeconds(QUERY_TELEPORT_WIZARD_TIMEOUT_IN_SECONDS))));
        }

        try {
            Task.WaitAll([.. asks.Select(a => (Task) a.Ask)], TimeSpan.FromSeconds(QUERY_TELEPORT_WIZARD_TIMEOUT_IN_SECONDS + 1));
        }
        catch (AggregateException) {
            // A bot that did not answer in time just shows no quest.
        }

        var info = new FriendlyPlayerQuestInfo { m_characterIDList = [], m_questNameList = [] };
        foreach (var (charId, ask) in asks) {
            var bot = ask.IsCompletedSuccessfully ? ask.Result?.Wizard : null;
            var title = bot is null ? null : CurrentQuestTitle(bot);
            if (string.IsNullOrEmpty(title)) {
                continue;
            }

            info.m_characterIDList.Add((GID) charId);
            info.m_questNameList.Add(title);
        }

        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(info, 1, out var infoBytes)) {
            Logger.Error("Friendly players | The quests for {0} could not be made.", Logger.Args(wizard.CharId));

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTFRIENDLYPLAYERQUEST {
            PlayerGID = message.PlayerGID != 0 ? message.PlayerGID : wizard.GameObjectID,
            Data = infoBytes,
        });

        Logger.Information("Friendly players | Sent {0} the quests of {1} of {2} asked bot(s).",
            Logger.Args(wizard.CharId, info.m_characterIDList.Count, askedIds.Count));
    }

    private static bool TryReadFriendlyQuestAsk(byte[] data, out FriendlyPlayerQuestInfo info) {
        info = null;
        if (data is null || data.Length == 0) {
            return false;
        }

        foreach (var versionable in new[] { false, true }) {
            try {
                if (new ObjectSerializer(Versionable: versionable, Behaviors: SerializerFlags.None)
                        .Deserialize<FriendlyPlayerQuestInfo>(data, 1, out var read) && read is not null) {
                    info = read;

                    return true;
                }
            }
            catch {
                // Try the next way of reading it.
            }
        }

        return false;
    }

    /// <summary>The title key of the quest a wizard is on: a main story quest first, else the first quest it holds.</summary>
    private static string CurrentQuestTitle(Wizard bot) {
        string first = null;
        foreach (var quest in bot.QuestBehavior?.CurrentQuestInstances ?? []) {
            var template = string.IsNullOrEmpty(quest?.QuestName) ? null : SpiralDB.GetQuestByName(quest.QuestName);
            var title = (string) template?.m_questTitle;
            if (string.IsNullOrEmpty(title) || template.m_isHidden) {
                continue;
            }

            if (template.m_mainline) {
                return title;
            }

            first ??= title;
        }

        return first;
    }

}
