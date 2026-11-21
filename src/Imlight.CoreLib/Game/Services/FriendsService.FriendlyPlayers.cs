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
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Utilities;
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
                m_zoneDisplayName = FriendZoneText(charId, player),
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

}
