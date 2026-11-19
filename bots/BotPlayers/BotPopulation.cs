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
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Core;
using NVector3 = System.Numerics.Vector3;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// How many bots an area gets (Niduus, 2026-11-12): each real player in it brings 3 to 6, an area (one street) has at
/// most 18 and the Commons at most 12. Only open areas of the world get them: not dungeons and other copies for one group,
/// houses, guild areas or the arena. [BotPlayers] in Imlight.ini, read live.
/// </summary>
internal static class BotPopulation {

    internal const double TICK_SECONDS = 4;

    internal static bool On => ConfigurationManager.GetValue("BotPlayers.Populate", true);
    internal static int MinPerPlayer => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.MinPerPlayer", 3), 0, 18);
    internal static int MaxPerPlayer => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.MaxPerPlayer", 6), MinPerPlayer, 18);
    internal static int MaxPerStreet => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.MaxPerStreet", 18), 0, 40);
    internal static int MaxInCommons => Math.Clamp(ConfigurationManager.GetValue("BotPlayers.MaxInCommons", 12), 0, 40);

    // The Commons of Wizard City.
    private const string COMMONS = "WC_Hub";

    private static readonly string[] s_never = ["Housing", "House", "Guild", "PvP", "Arena", "Battleground", "Tutorial", "Interior"];

    internal static bool IsCommons(string zonePath) => zonePath.Contains(COMMONS, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether bots may fill this area.</summary>
    internal static bool Allowed(string zonePath, bool instance)
        => !instance && !string.IsNullOrEmpty(zonePath) && !s_never.Any(n => zonePath.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many bots the area should have for the players in it (each player's own 3 to 6).</summary>
    internal static int Wanted(string zonePath, IEnumerable<int> perPlayer) {
        var cap = IsCommons(zonePath) ? MaxInCommons : MaxPerStreet;

        return Math.Min(cap, perPlayer.Sum(n => Math.Clamp(n, MinPerPlayer, MaxPerPlayer)));
    }

    /// <summary>
    /// A place in the area for a bot to come in (Niduus, 2026-11-12: "bot placements are very random and don't seem
    /// organic"): a point of the walk grid that is on a path and away from water, in sight of the player more often than
    /// not, and among those the open places (squares, wide streets) where players gather. Without a walk grid, somewhere
    /// around the player.
    /// </summary>
    internal static Imcodec.Math.Vector3 SpotIn(string zonePath, Imcodec.Math.Vector3 player) {
        var points = ZoneNav.Points(zonePath);
        var me = new NVector3(player.X, player.Y, player.Z);
        if (points.Length > 0) {
            // Two of three come in where the player can see them; the rest further along the street.
            var (near, far) = Random.Shared.Next(3) < 2 ? (400f, 2500f) : (2500f, 6000f);
            var best = new List<(NVector3 Point, int Score)>();
            for (var tries = 0; tries < 300 && best.Count < 24; tries++) {
                var point = points[Random.Shared.Next(points.Length)];
                var distance = NVector3.Distance(point, me);
                if (distance > near && distance < far && MathF.Abs(point.Z - me.Z) < 600
                    && BotGround.GoodSpot(zonePath, point) && ZoneCollision.Clearance(zonePath, point) >= 40
                    // Floor the player can walk to (not an island past a wall, water or black), and not a boxed-in patch.
                    && !OverlayQuestPath.Apart(zonePath, me.X, me.Y, point.X, point.Y)
                    && OverlayQuestPath.IslandShare(zonePath, point.X, point.Y) is < 0 or >= 0.05) {
                    best.Add((point, BotGround.Openness(zonePath, point)));
                }
            }

            if (best.Count > 0) {
                var pick = best.OrderByDescending(b => b.Score).Take(5).ToList()[Random.Shared.Next(Math.Min(5, best.Count))].Point;

                return new Imcodec.Math.Vector3(pick.X, pick.Y, pick.Z);
            }
        }

        // Nothing found on a path: near the player, where the player can surely stand.
        for (var tries = 0; tries < 20; tries++) {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var away = 200 + (Random.Shared.NextSingle() * 400);
            var wanted = new NVector3(me.X + (MathF.Cos(angle) * away), me.Y + (MathF.Sin(angle) * away), me.Z);
            var spot = points.Length > 0 ? ZoneNav.Nearest(zonePath, wanted.X, wanted.Y, me.Z, 400, 200) : wanted;
            if (spot is { } s && BotGround.CanStep(zonePath, s) && ZoneCollision.Clearance(zonePath, s) >= 40) {
                return new Imcodec.Math.Vector3(s.X, s.Y, s.Z);
            }
        }

        return player;
    }

    // Each world's levels (Niduus, 2026-11-12: "what is a 177 and 181 doing in Azteca. It's level max is 90"): the level
    // players reach it at and its level cap (each world raises the cap by about 10). Selenopolis lives in Krokotopia's folder.
    private static readonly (string Folder, int From, int Cap)[] s_worldLevels = [
        ("KT_Selenopolis", 180, 190), ("WizardCity", 1, 15), ("Krokotopia", 10, 25), ("Marleybone", 20, 30), ("MooShu", 25, 35),
        ("Dragonspyre", 35, 50), ("Grizzleheim", 20, 50), ("Wysteria", 20, 50), ("Celestia", 50, 60), ("Zafaria", 60, 70),
        ("Avalon", 70, 80), ("Azteca", 80, 90), ("Aquila", 75, 90), ("Khrysalis", 90, 100), ("Darkmoor", 95, 100),
        ("Polaris", 100, 110), ("Mirage", 110, 120), ("Empyrea", 120, 140), ("Karamelle", 140, 150), ("Lemuria", 150, 160),
        ("Novus", 160, 170), ("Wallaru", 170, 180), ("Selenopolis", 180, 190),
    ];

    // How far over a world's cap a wizard is still believable there.
    private const int OVER_CAP = 3;

    /// <summary>
    /// A level that fits the world: near the player's own when the player belongs there, otherwise one of the levels
    /// wizards questing that world have (a few slightly over its cap). Streets of unknown worlds go by the player.
    /// </summary>
    internal static int LevelFor(string zonePath, int playerLevel, Random random) {
        var nearPlayer = Math.Clamp(playerLevel + random.Next(-4, 3), 1, 200);
        if (World(zonePath) is not var (from, cap)) {
            return nearPlayer;
        }

        var top = cap + OVER_CAP;
        if (playerLevel >= from && playerLevel <= top) {
            return Math.Clamp(nearPlayer, from, top);
        }

        // Most are still questing the world; one in six has finished it and stayed a little over the cap.
        return random.Next(6) == 0 ? random.Next(cap, top + 1) : random.Next(from, cap + 1);
    }

    private static (int From, int Cap)? World(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        foreach (var (folder, from, cap) in s_worldLevels) {
            if (folder.Contains('_') ? zonePath.Contains(folder, StringComparison.OrdinalIgnoreCase)
                    : zonePath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) {
                return (from, cap);
            }
        }

        return null;
    }

}
