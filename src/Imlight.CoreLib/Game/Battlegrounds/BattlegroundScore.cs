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
using Imlight.CoreLib.Shared.Packets;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * How a match is scored. The game's help (Locale Battlegrounds.lang) says points come from defeating opponents and
 * capturing Battle Rings, and that a team earns more the more rings it holds. Its score events
 * (MSG_BATTLEGROUNDUPDATEPOINTS PointEvent, read from the match window, BattlegroundWindow 0x140298700): 2 "Defeated"
 * (PE_KILL_PLAYER, with the name in Tag), 4 and 5 "Captured" (PE_COMBAT_CAP), 6 "Dominating!" (PE_DOMINATION), 7
 * "Mercy..." (PE_MERCY); other numbers show no word. The amounts are not in the game files; in a recorded PvE match the
 * wizards (Λ) ended on 387 and the monsters (Ω) on 348, which these [Beastmoon] settings come near:
 *   CapturePoints        a team takes a ring                                 10
 *   DominationPoints     each ring a team holds, every DominationSeconds (10)   1
 *     (in PvE the monsters hold every ring the wizards do not, the Spiral too)
 *   NormalPoints / ElitePoints / BossPoints  the wizards beat a monster      5 / 10 / 25
 *   DefeatPoints         the monsters beat a wizard's beast                    20
 * The arenas' own script adds 100 for a PvE match's last boss (ResUnknown4591AF80).
 */
internal static class BattlegroundScore {

    internal const int PE_NONE = 0;
    internal const int PE_KILL_PLAYER = 2;
    internal const int PE_COMBAT_CAP = 4;
    internal const int PE_DOMINATION = 6;
    internal const int PE_MERCY = 7;

    private static int Setting(string key, int fallback) => Math.Max(0, ConfigurationManager.GetValue("Beastmoon." + key, fallback));

    internal static int CapturePoints => Setting("CapturePoints", 10);
    internal static int DominationPoints => Setting("DominationPoints", 1);
    internal static float DominationSeconds => Math.Max(1, ConfigurationManager.GetValue("Beastmoon.DominationSeconds", 10f));
    internal static int DefeatPoints => Setting("DefeatPoints", 20);

    internal static int MonsterPoints(BeastRewards.Rank rank) => rank switch {
        BeastRewards.Rank.Boss => Setting("BossPoints", 25),
        BeastRewards.Rank.Elite => Setting("ElitePoints", 10),
        _ => Setting("NormalPoints", 5),
    };

    /// <summary>Tells the arena to give points to a team (the zone adds them up and shows them).</summary>
    internal static void Give(IActorRef zone, Battlegrounds.Team team, int points, int pointEvent = PE_NONE) {
        if (zone is null || points <= 0) {
            return;
        }

        zone.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL {
            Kind = BattlegroundControlKind.Points,
            Team = (byte) team,
            Points = points,
            PointEvent = pointEvent,
        });
    }

}
