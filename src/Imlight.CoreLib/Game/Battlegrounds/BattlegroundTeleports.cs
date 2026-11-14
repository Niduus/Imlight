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
using System.Collections.Generic;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * Where the Beastmoon arenas' teleporters go. Their ResTeleport names no destination: the record holds only three
 * empty fields, no zone or location (as in the guild raids, Raids.TeleportTarget). The pads were matched the way the
 * raids' were, by where each pad's volume (volumes.xml) and each location (gamedata.bin m_locationList) stand:
 * every Target_ location is the spot a wizard lands on beside the pad at the OTHER end, a little outside that pad's
 * sphere, so a pad goes to the target beside its partner:
 * - Teleport_2_Red01 / Teleport_2_Blue01 stand in a team's base below the map and go out to Target_From_Red01 /
 *   Target_From_Blue01, which stand beside the field's way back, Teleport_RedBase (1) / Teleport_BlueBase (1). The way
 *   back, the side pads of BMH_Zone_05 and the victory pads of BMH_Zone_04 have no target beside anything: they go to
 *   the team's start (m_teamStartA Red_Start, m_teamStartB Blue_Start), and each asks for its team's effect.
 * - the paired pads cross over: CrossMap_01 lands at Target_CrossMap_02, the Sun pad at the Moon (BMH_Zone_02), Pink
 *   Upper at Target_Spiral_Red_Lower (03, 04), Spiral_Teleport_Upper01 at Target_Spiral1_02 (04), Yellow_Redside at
 *   Target_Yellow_Blueside (05).
 * Mirage_CTF_01 (Beastmoon_PVP_01, 06, ...) is not mapped yet.
 */
internal static class BattlegroundTeleports {

    private const string RED = "Red_Start";
    private const string BLUE = "Blue_Start";

    private static Dictionary<string, string> Pads(params (string Trigger, string Location)[] pads) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (trigger, location) in pads) {
            map[trigger] = location;
        }

        return map;
    }

    // The ways in and out of the bases, the same in every arena.
    private static readonly (string, string)[] s_bases = [
        ("Trigger_Teleport_2_Red01", "Target_From_Red01"),
        ("Trigger_Teleport_2_Blue01 (1)", "Target_From_Blue01"),
        ("Trigger_Teleport_RedBase (1)", RED),
        ("Trigger_Teleport_BlueBase (1)", BLUE),
    ];

    private static readonly (string, string)[] s_spiralPairs = [
        ("Trigger_Pink_Teleport_Upper", "Target_Spiral_Red_Lower"),
        ("Trigger_Pink_Teleport_Lower", "Target_Spiral_Red_Upper"),
        ("Trigger_Green_Teleport_Upper", "Target_Spiral_Blue_Lower"),
        ("Trigger_Green_Teleport_Lower", "Target_Spiral_Blue_Upper"),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> s_zones = new(StringComparer.OrdinalIgnoreCase) {
        ["Battlegrounds/BMH/BMH_Zone_02"] = Pads([.. s_bases,
            ("Trigger_Teleport_CrossMap_01", "Target_CrossMap_02"),
            ("Trigger_Teleport_CrossMap_02", "Target_CrossMap_01"),
            // The point teleporters: a team that holds the Sun (point 1) or the Moon (point 2) may cross between them.
            ("Trigger_Teleport_RedSun", "Target_Moon_Teleport"),
            ("Trigger_Teleport_BlueSun", "Target_Moon_Teleport"),
            ("Trigger_Teleport_RedMoon", "Target_Sun_Teleport"),
            ("Trigger_Teleport_BlueMoon", "Target_Sun_Teleport"),
        ]),
        ["Battlegrounds/BMH/BMH_Zone_03"] = Pads([.. s_bases, .. s_spiralPairs]),
        ["Battlegrounds/BMH/BMH_Zone_04"] = Pads([.. s_bases, .. s_spiralPairs,
            ("Trigger_Spiral_Teleport_Upper01", "Target_Spiral1_02"),
            ("Trigger_Spiral_Teleport_Lower01", "Target_Spiral1_01"),
            ("Trigger_Spiral_Teleport_Upper02", "Target_Spiral2_02"),
            ("Trigger_Spiral_Teleport_Lower02", "Target_Spiral2_01"),
            // After the match, the victory pads at the center go home.
            ("Trigger_Teleport_RedBase", RED),
            ("Trigger_Teleport_BlueBase", BLUE),
        ]),
        ["Battlegrounds/BMH/BMH_Zone_05"] = Pads([.. s_bases,
            ("Teleport_Yellow_Redside", "Target_Yellow_Blueside"),
            ("Teleport_Yellow_Blueside", "Target_Yellow_Redside"),
            // The pads by the side camps take a team that holds the camp home.
            ("Trigger_Teleport_Red_Side1", RED),
            ("Trigger_Teleport_Blue_Side1", BLUE),
            ("Trigger_Teleport_RedBase_Side2", RED),
            ("Trigger_Teleport_BlueBase_Side2", BLUE),
            ("Trigger_Teleport_Red_Side3", RED),
            ("Trigger_Teleport_Blue_Side3", BLUE),
            ("Trigger_Teleport_Red_Side4", RED),
            ("Trigger_Teleport_Blue_Side4", BLUE),
        ]),
    };

    // The PvE arenas (one team, red): two ways out of the base (the second, Teleport_2_Red05 on AV_BlueSide, lands on the
    // far side, Target_BlueSide), the way back, and a teleporter home at each point the team took
    // (Teleport_RedBase_Sun on AV_Teleport_Sun, shown by Trigger_BaseTeleport_Sun when EVENT_CTF_Red_01 comes).
    private static readonly (string, string)[] s_pveBases = [
        ("Trigger_Teleport_2_Red01", "Target_From_Red01"),
        ("Trigger_Teleport_2_Red05", "Target_BlueSide"),
        ("Trigger_Teleport_RedBase (1)", RED),
        ("Trigger_RedBaseTele_2", RED),
        ("Trigger_Teleport_RedBase_Sun", RED),
        ("Trigger_Teleport_RedBase_Moon", RED),
        ("Trigger_Teleport_RedBase_Eye", RED),
        ("Trigger_Teleport_RedBase_Star", RED),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> s_pveZones = new(StringComparer.OrdinalIgnoreCase) {
        ["Battlegrounds/BG_PVE/BG_PVE_Zone_01"] = Pads([.. s_pveBases,
            ("Trigger_Spiral_Teleport_Upper01", "Target_Spiral1_02"),
            ("Trigger_Spiral_Teleport_Lower01", "Target_Spiral1_01"),
        ]),
        ["Battlegrounds/BG_PVE/BG_PVE_Zone_02"] = Pads([.. s_pveBases,
            ("Trigger_Teleport_Crossmap_StarSide", "Target_Crossmap_EyeSide"),
            ("Trigger_Teleport_Crossmap_EyeSide", "Target_Crossmap_StarSide"),
            ("Trigger_Teleport_RedSun", "Target_Moon_Teleport"),
            ("Trigger_Teleport_RedMoon", "Target_Sun_Teleport"),
        ]),
        ["Battlegrounds/BG_PVE/BG_PVE_Zone_04"] = Pads([.. s_pveBases, .. s_spiralPairs,
            ("Trigger_Spiral_Teleport_Upper01", "Target_Spiral1_02"),
            ("Trigger_Spiral_Teleport_Lower01", "Target_Spiral1_01"),
            ("Trigger_Spiral_Teleport_Upper02", "Target_Spiral2_02"),
            ("Trigger_Spiral_Teleport_Lower02", "Target_Spiral2_01"),
        ]),
    };

    /// <summary>The location a battleground pad goes to, or null when the zone or pad is not mapped.</summary>
    internal static string Target(string zonePath, string triggerName)
        => zonePath is not null && triggerName is not null
           && (s_zones.TryGetValue(zonePath, out var pads) || s_pveZones.TryGetValue(zonePath, out pads))
           && pads.TryGetValue(triggerName, out var location)
            ? location
            : null;

}
