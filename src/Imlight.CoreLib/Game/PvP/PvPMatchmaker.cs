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
 * Pairs the queued players of a match pool. Plain logic owned by the PvPDirector: it reads the queue and the
 * match templates, and decides, but changes nothing.
 * 
 * USAGE EXAMPLE:
 * var pairs = PvPMatchmaker.FindPairs(queue.Entries, DateTime.UtcNow, charId => isPresent(charId));
 * 
 * NOTE:
 * The rating of practice is the level modifier of the match table at the level of the player. A player is paired
 * with the closest opponent of the pool inside a range of m_initialQueueThreshold plus m_actorQueueGrowth for every
 * second waited, or with anyone after m_actorQueueTimeout seconds or when they asked to try all opponents. Either
 * player of a pair may be the one the range allows. These semantics are inferred from the field names.
 * 
 * TODO:
 * What are m_minQueueSize, m_maxQueueSize and m_queueWaitingTime for?
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.PvP;

internal static class PvPMatchmaker {

    /// <summary>
    /// Gets the rating of a level from a level modifier table, interpolating between the listed levels and
    /// clamping to the first and last one.
    /// </summary>
    public static float GetRating(TournamentLevelModifiers table, int level) {
        var points = table.m_levelToRankMod.OrderBy(point => point.X).ToList();
        if (points.Count == 0) {
            return 0;
        }

        if (level <= points[0].X) {
            return points[0].Y;
        }

        for (var i = 1; i < points.Count; i++) {
            if (level > points[i].X) {
                continue;
            }

            var span = points[i].X - points[i - 1].X;
            var fraction = span <= 0 ? 1 : (level - points[i - 1].X) / span;

            return points[i - 1].Y + ((points[i].Y - points[i - 1].Y) * fraction);
        }

        return points[^1].Y;
    }

    /// <summary>
    /// Finds the pairs to start now, longest waiting player first. A player is in at most one pair.
    /// </summary>
    public static List<(PvPQueueEntry First, PvPQueueEntry Second)> FindPairs(IEnumerable<PvPQueueEntry> entries, DateTime utcNow,
                                                                              Func<ulong, bool> isAvailable) {
        var pairs = new List<(PvPQueueEntry, PvPQueueEntry)>();
        var pools = entries
            .Where(entry => isAvailable(entry.CharId))
            .GroupBy(entry => (entry.RealmName, entry.TournamentNameId, entry.MatchNameId));

        foreach (var pool in pools) {
            if (!PvPMatchFactory.TryGetMatch(pool.Key.MatchNameId, out var match)) {
                Logger.Error("Queued match {MatchId} has no template.", Logger.Args(pool.Key.MatchNameId));

                continue;
            }

            var waiting = pool.OrderBy(entry => entry.EnteredAtUtc).ThenBy(entry => entry.CharId).ToList();
            var paired = new HashSet<ulong>();
            foreach (var first in waiting) {
                if (paired.Contains(first.CharId)) {
                    continue;
                }

                var second = waiting
                    .Where(candidate => candidate.CharId != first.CharId && !paired.Contains(candidate.CharId))
                    .Where(candidate => Accepts(match, first, candidate, utcNow) || Accepts(match, candidate, first, utcNow))
                    .OrderBy(candidate => Math.Abs(candidate.Rating - first.Rating))
                    .FirstOrDefault();
                if (second is null) {
                    continue;
                }

                paired.Add(first.CharId);
                paired.Add(second.CharId);
                pairs.Add((first, second));
            }
        }

        return pairs;
    }

    private static bool Accepts(MatchTemplate match, PvPQueueEntry player, PvPQueueEntry opponent, DateTime utcNow) {
        var secondsWaited = Math.Max(0, (utcNow - player.EnteredAtUtc).TotalSeconds);
        if (player.ExpandSearch || secondsWaited >= match.m_actorQueueTimeout) {
            return true;
        }

        var range = match.m_initialQueueThreshold + (match.m_actorQueueGrowth * secondsWaited);

        return Math.Abs(opponent.Rating - player.Rating) <= range;
    }

}
