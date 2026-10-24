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
 * TEAM UP WORLDS
 * ========================================================================
 *
 * PURPOSE:
 * The world tabs of the Join a Team window and which world a dungeon belongs to.
 *
 * USAGE EXAMPLE:
 * var worldId = TeamUpWorlds.WorldOf("WizardCity/Interiors/WC_Golem_Tower");
 *
 * NOTE:
 * The game keeps the tabs itself, in Root.wad TeamHelpWorldInfo.xml (a TeamHelpWorldInfoList, read 2026-10-24). It
 * knows a tab by the string hash of the tab's m_buttonName (TeamHelpWorldInfo +0x110, set from m_buttonName when the
 * file loads), so that hash is the world id of MSG_REQUESTTEAMHELPJOIN, MSG_CLIENTREQUESTTEAMHELPWORLDINFO and
 * TeamHelpUpdateInfo.m_worldList. A tab's m_worldPath lists the zone folders of its dungeons, split by commas.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Game.TeamUp;

internal static class TeamUpWorlds {

    internal sealed record World(string ButtonName, int DisplayIndex, string[] Paths) {
        public uint Id { get; } = StringHash.Compute(ButtonName);
    }

    // TeamHelpWorldInfo.xml, in its own order. Dungeons (G14 and the monthly events) and Skeleton Key come first by
    // path so their dungeons are not filed under the world they stand in.
    internal static readonly IReadOnlyList<World> All = [
        new("GUI/BadgeCategory/Badge_Category_Dungeons.dds", 13, ["G14_DM", "G14_HS", "G14_SB", "Marleybone/G14_Gauntlet", "MonthlyEvents"]),
        new("GUI/BadgeCategory/Badge_Category_SkeletonKey.dds", 14, ["SkeletonKey"]),
        new("GUI/Buttons/Button_World_Wizard_City.dds", 0, ["WizardCity"]),
        new("GUI/Buttons/Button_World_Aquila.dds", 1, ["Aquila"]),
        new("GUI/Buttons/Button_World_Krokotopia.dds", 2, ["Krokotopia"]),
        new("GUI/Buttons/Button_World_Marleybone.dds", 3, ["Marleybone"]),
        new("GUI/Buttons/Button_World_Mooshu.dds", 4, ["MooShu"]),
        new("GUI/Buttons/Button_World_Dragonspire.dds", 5, ["DragonSpire"]),
        new("GUI/Buttons/Button_World_Celestia.dds", 6, ["Celestia"]),
        new("GUI/Buttons/Button_World_Zafaria.dds", 7, ["Zafaria"]),
        new("GUI/Buttons/Button_World_Avalon.dds", 8, ["Avalon"]),
        new("GUI/Buttons/Button_World_Azteca.dds", 9, ["Azteca"]),
        new("GUI/Buttons/Button_World_Khrysalis.dds", 10, ["Khrysalis"]),
        new("GUI/Buttons/Button_World_Grizzleheim.dds", 11, ["Grizzleheim"]),
        new("GUI/Buttons/Button_World_Pigswick_Academy.dds", 12, ["Wysteria"]),
        new("GUI/Buttons/Button_World_Polaris.dds", 15, ["Polaris"]),
        new("GUI/Buttons/Button_World_Arcanum.dds", 16, ["Arcanum"]),
        new("GUI/Buttons/Button_World_Mirage.dds", 17, ["Mirage"]),
        new("GUI/Buttons/Button_World_Empyrea.dds", 18, ["Empyrea"]),
        new("GUI/Buttons/Button_World_Karamelle.dds", 19, ["Karamelle"]),
        new("GUI/Buttons/Button_World_Lemuria.dds", 20, ["Lemuria"]),
        new("GUI/Buttons/Button_World_Novus.dds", 21, ["Novus"]),
        new("GUI/Buttons/Button_World_Wallaru.dds", 22, ["Wallaru"]),
        new("GUI/Buttons/Button_World_Darkmoor.dds", 23, ["Darkmoor"]),
    ];

    /// <summary>The world tab first in the window (the lowest display index): Wizard City.</summary>
    internal static World First
        => All.OrderBy(w => w.DisplayIndex).First();

    internal static World ById(uint id)
        => All.FirstOrDefault(w => w.Id == id);

    /// <summary>The world tab a zone's dungeon is listed under: the first whose path the zone path starts with.</summary>
    internal static uint WorldOf(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return First.Id;
        }

        foreach (var world in All) {
            if (world.Paths.Any(p => zonePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                                     || zonePath.Contains("/" + p, StringComparison.OrdinalIgnoreCase))) {
                return world.Id;
            }
        }

        return First.Id;
    }

}
