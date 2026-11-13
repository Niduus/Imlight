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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// What a server-played wizard (the Quest Team, Ultimus) already used for a quest step it collects for, one by one (Niduus,
/// 2026-11-13: "it will redeem one and then seek out other's to interact with. Not revisiting collected ones they have
/// collected independently for quests"): each wizard remembers the objects it used and the spots it finished for each
/// step, so it goes on to the next one and never back.
/// </summary>
internal static class BotCollect {

    /// <summary>Two spots this close are the same one (the quest arrow's spot and the object by it).</summary>
    private const float SAME_SPOT = 150f;

    private sealed class Step {
        public readonly HashSet<ulong> Objects = [];
        public readonly List<(float X, float Y)> Spots = [];
    }

    private static readonly ConcurrentDictionary<string, Step> s_steps = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(ulong charId, string quest, string goal) => $"{charId}|{quest}|{goal}";

    private static Step Of(ulong charId, string quest, string goal) => s_steps.GetOrAdd(Key(charId, quest, goal), _ => new Step());

    /// <summary>The wizard used <paramref name="objectId"/> at (x, y) for this step.</summary>
    internal static void Used(ulong charId, string quest, string goal, ulong objectId, float x, float y) {
        var step = Of(charId, quest, goal);
        lock (step) {
            step.Objects.Add(objectId);
            if (!step.Spots.Any(s => Far(s.X, s.Y, x, y) < SAME_SPOT)) {
                step.Spots.Add((x, y));
            }
        }
    }

    /// <summary>Nothing more for the wizard to use at (x, y) for this step: it goes on to the next spot.</summary>
    internal static void SpotDone(ulong charId, string quest, string goal, float x, float y) {
        var step = Of(charId, quest, goal);
        lock (step) {
            if (!step.Spots.Any(s => Far(s.X, s.Y, x, y) < SAME_SPOT)) {
                step.Spots.Add((x, y));
            }
        }
    }

    /// <summary>Whether the wizard already used this object for this step.</summary>
    internal static bool HasUsed(ulong charId, string quest, string goal, ulong objectId) {
        if (!s_steps.TryGetValue(Key(charId, quest, goal), out var step)) {
            return false;
        }

        lock (step) {
            return step.Objects.Contains(objectId);
        }
    }

    /// <summary>Whether the wizard is done with the spot at (x, y) for this step.</summary>
    internal static bool HasDone(ulong charId, string quest, string goal, float x, float y) {
        if (!s_steps.TryGetValue(Key(charId, quest, goal), out var step)) {
            return false;
        }

        lock (step) {
            return step.Spots.Any(s => Far(s.X, s.Y, x, y) < SAME_SPOT);
        }
    }

    /// <summary>
    /// The quest arrow's spots for a step, best first: ones some wizard in <paramref name="holders"/> still has to do,
    /// nearest first, with spots that have creatures close by after the free ones (no battle in the way when it can be
    /// helped, Niduus, 2026-11-13). When every spot is done by all, the nearest again (the step still wants more).
    /// </summary>
    internal static List<(float X, float Y)> Order(IReadOnlyList<(float X, float Y)> spots, IReadOnlyList<ulong> holders, string quest, string goal,
        float fromX, float fromY, IReadOnlyList<(float X, float Y)> creatures) {
        bool Guarded((float X, float Y) s) => creatures.Any(c => Far(c.X, c.Y, s.X, s.Y) < 450);
        var open = spots.Where(s => holders.Any(h => !HasDone(h, quest, goal, s.X, s.Y))).ToList();
        var pool = open.Count > 0 ? open : spots.ToList();

        return [.. pool.OrderBy(s => Far(fromX, fromY, s.X, s.Y) + (Guarded(s) ? 3000 : 0))];
    }

    /// <summary>Forgets a wizard's steps (a restart makes new wizards).</summary>
    internal static void Forget(ulong charId) {
        foreach (var key in s_steps.Keys.Where(k => k.StartsWith(charId + "|", StringComparison.Ordinal)).ToList()) {
            s_steps.TryRemove(key, out _);
        }
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

}
