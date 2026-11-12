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
    /// A place in the area for a bot to come in: a random point of the area's walk grid, not right next to the player
    /// (bots walk in from somewhere else, as players do); without a walk grid, somewhere around the player.
    /// </summary>
    internal static Imcodec.Math.Vector3 SpotIn(string zonePath, Imcodec.Math.Vector3 player) {
        var points = ZoneNav.Points(zonePath);
        var me = new NVector3(player.X, player.Y, player.Z);
        if (points.Length > 0) {
            for (var tries = 0; tries < 30; tries++) {
                var point = points[Random.Shared.Next(points.Length)];
                var distance = NVector3.Distance(point, me);
                if (distance is > 400 and < 6000 && ZoneCollision.Clearance(zonePath, point) >= 40) {
                    return new Imcodec.Math.Vector3(point.X, point.Y, point.Z);
                }
            }
        }

        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var away = 300 + (Random.Shared.NextSingle() * 600);
        var wanted = new NVector3(me.X + (MathF.Cos(angle) * away), me.Y + (MathF.Sin(angle) * away), me.Z);
        var snapped = points.Length > 0 ? ZoneNav.Nearest(zonePath, wanted.X, wanted.Y, me.Z, 400, 200) : null;
        var spot = snapped ?? wanted;

        return new Imcodec.Math.Vector3(spot.X, spot.Y, spot.Z);
    }

}
