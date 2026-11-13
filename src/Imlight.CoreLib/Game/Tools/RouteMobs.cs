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
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The street's creature areas (<see cref="MobSpawnAreas"/>, read from the area's creature paths, so known even while the
/// creatures are dead) for every way finder: the Minimap's quest line, the Quest Team, Ultimus and the Bot Players (Niduus,
/// 2026-11-13: go round the mobs unless the step needs them, and head into their area when it does).
/// </summary>
internal static class RouteMobs {

    // Creature walks this near the step are not kept away from (the step itself may stand among them).
    private const float STEP_CLEAR = 450f;

    /// <summary>
    /// Spots to keep away from on the way to (<paramref name="toX"/>, <paramref name="toY"/>), for
    /// <see cref="OverlayQuestPath.Walk"/>'s avoid list: every creature walk of the area, except the ones of the creatures
    /// to fight (<paramref name="wanted"/>) and the ones by the step, plus <paramref name="more"/> (creatures standing
    /// there now). Null when there are none.
    /// </summary>
    internal static List<(float X, float Y)> Avoid(IActorRef zone, float toX, float toY, IReadOnlyCollection<ulong> wanted = null,
        IEnumerable<(float X, float Y)> more = null) {
        var spots = new List<(float X, float Y)>();
        try {
            spots.AddRange(MobSpawnAreas.AvoidSpots(zone, wanted)
                .Where(s => MathF.Sqrt(((s.X - toX) * (s.X - toX)) + ((s.Y - toY) * (s.Y - toY))) > STEP_CLEAR));
        }
        catch (Exception) {
            // Only the creatures standing there now.
        }

        if (more is not null) {
            spots.AddRange(more);
        }

        return spots.Count > 0 ? spots : null;
    }

    /// <summary>
    /// The nearest creature area where a creature a bounty step wants (by its adjectives, or a stand-in the quest names)
    /// spawns: where to go when none is up right now. Null when none spawns in this area.
    /// </summary>
    internal static MobSpawnAreas.Area SeekFor(IActorRef zone, float x, float y, string[] adjectives) {
        if (adjectives is not { Length: > 0 }) {
            return null;
        }

        try {
            return MobSpawnAreas.In(zone).Where(a => a.Templates.Any(t => Wanted(t, adjectives)))
                .OrderBy(a => a.DistanceTo(x, y)).FirstOrDefault();
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>The spot of an area's walk nearest (x, y): where to stand to meet its creatures.</summary>
    internal static (float X, float Y) Nearest(MobSpawnAreas.Area area, float x, float y)
        => area.Points.OrderBy(p => ((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y))).First();

    private static bool Wanted(ulong template, string[] adjectives) {
        if (Quests.BountyStandIns.Counts(adjectives, template)) {
            return true;
        }

        var own = (CoreObjectFactory.GetCoreTemplate((uint) template) as GameObjectTemplate)?.m_adjectiveList;

        return own?.Any(a => adjectives.Contains((string) a, StringComparer.Ordinal)) == true;
    }

}
