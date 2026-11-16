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
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Every main-world area checked once per server run for spots like Big Ben's Counterweight East (Niduus, 2026-11-15:
/// "look for cases where this could be similar in any capacity (better or worse) then give it to Ultimus so he's better
/// equipped later"): where our rough floor map lost a ledge, a bridge, a ring walk or a doorway that the game's own walk
/// map has, and how many floors each area stacks. The way finder already puts those spots right as it reads an area
/// (OverlayQuestPath.Carve); this lists them in logs/ultimus/maps/_where-our-map-was-wrong.txt, worst first, and lays
/// out each such area in the maps folder.
/// </summary>
internal static class UltimusMapCheck {

    private static int s_started;

    // Wait after the server starts, and between areas, so the check never slows the game.
    private static readonly TimeSpan s_firstWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_between = TimeSpan.FromMilliseconds(1500);

    internal static void Start() {
        if (Interlocked.Exchange(ref s_started, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                await Task.Delay(s_firstWait);
                await Run();
            }
            catch (Exception ex) {
                Logger.Debug("Ultimus map check stopped: {0}", Logger.Args(ex.Message));
            }
        });
    }

    private static async Task Run() {
        var worlds = MainWorlds.WithHub();
        var areas = LocalWadCache.GetAllCachedFiles()
            .Select(f => (f.Filename ?? "").Replace('\\', '/'))
            .Select(n => n.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Select(n => n.Split('/')[^1].Replace('-', '/'))
            .Where(z => worlds.Contains(z.Split('/')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(z => z, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = new List<(string Area, int Walls, int Black, int Floors, int Climbs)>();
        var checkedCount = 0;
        foreach (var area in areas) {
            await Task.Delay(s_between);
            try {
                if (ZoneNav.Graph(area) is not { } graph) {
                    continue;
                }

                checkedCount++;
                var (walls, black) = OverlayQuestPath.Carved(area) ?? (0, 0);
                var (floors, climbs) = ZoneNav.FloorsOf(graph);
                if (walls + black > 0 || floors > 2) {
                    found.Add((area, walls, black, floors, climbs));
                }

                if (walls + black > 0) {
                    UltimusDirectorActor.SaveZoneLayout(area);
                }
            }
            catch (Exception) {
                // One area that can't be read; the rest are still checked.
            }
        }

        var text = new StringBuilder();
        text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm} Ultimus map check: {checkedCount} main-world areas with a walk map checked.");
        text.AppendLine("Squares our rough map had as wall or black where the game's own walk map has a walk (narrow ledges, bridges, ring walks,");
        text.AppendLine("doorways, lift tops). The way finder now walks them for Ultimus, the Quest Team, the Bot Players and the Minimap line.");
        text.AppendLine();
        foreach (var f in found.OrderByDescending(f => f.Walls + f.Black).ThenByDescending(f => f.Floors)) {
            text.AppendLine($"{f.Area}: {f.Walls} wall and {f.Black} black squares put right; {f.Floors} floors, {f.Climbs} stairs or ramps between them.");
        }

        var folder = Path.Combine(UltimusWire.Folder, "maps");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "_where-our-map-was-wrong.txt"), text.ToString());
        Logger.Information("Ultimus map check: {0} areas checked, {1} had spots our map had wrong.",
            Logger.Args(checkedCount, found.Count(f => f.Walls + f.Black > 0)));
    }

}
