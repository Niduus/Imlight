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
 * PVP
 * ========================================================================
 * 
 * PURPOSE:
 * Builds the serialized objects the PvP messages carry as byte strings.
 * 
 * USAGE EXAMPLE:
 * var eloList = PvPBlobs.EmptyEloEntryList();
 * 
 * NOTE:
 * Without a PvP packet capture the class project and tournament blobs follow the decompiled client loaders;
 * the empty Elo list equals the capture of a player with no records byte for byte.
 * 
 * TODO:
 * Do the class project blobs need more than the empty lists?
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.PvP;

internal static class PvPBlobs {

    private const uint ClientPropertyMask = 4;

    public static ByteString EmptyProjectProgress()
        => Serialize(new ClassProjectPlayerProgressList { m_playerProgressList = [] });

    public static ByteString EmptyProjectActive()
        => Serialize(new ClassProjectActiveList { m_activeProjectList = [] });

    /// <summary>
    /// The Elo records of a player with none: the list object behind the serializer compress marker bit,
    /// which is clear because the buffer is stored as is.
    /// </summary>
    public static ByteString EmptyEloEntryList() {
        var list = Serialize(new AdvPvPEloEntryListWrapper { m_entries = [] });

        return new ByteString([0, .. (byte[]) list]);
    }

    /// <summary>
    /// The tournament list of the PvP window: one new list for the tournament, without matches. It is what gives
    /// the client tournament info its id, so the window starts asking for updates.
    /// </summary>
    public static ByteString TournamentList(string tournamentName, uint tournamentNameId) {
        var update = new NewListUpdate {
            m_tournamentName = tournamentName,
            m_tournamentNameID = tournamentNameId,
            m_clearData = true,
            m_matches = [],
            m_teams = [],
            m_brackets = [],
            m_totalTeams = 0
        };

        return Serialize(new TournamentUpdateList { m_updates = [update] });
    }

    private static ByteString Serialize(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(value, ClientPropertyMask, out var data)) {
            Logger.Error("Failed to serialize PvP blob {Type}.", Logger.Args(value.GetType().Name));

            return default;
        }

        return data;
    }

}
