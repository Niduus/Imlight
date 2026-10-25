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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Fishing;

/// <summary>A fish swimming in a pond right now.</summary>
internal sealed record LiveFish(uint Id, uint TemplateId, float Size, int Water);

/// <summary>
/// The fish swimming in each zone. Everyone in a zone fishes the same ponds: a caught or escaped fish is gone for
/// everyone and a new one swims in after the pond's respawn time.
/// </summary>
internal sealed class FishPond {

    private readonly object _lock = new();
    private readonly Dictionary<uint, LiveFish> _fish = [];
    private readonly List<(DateTime When, int Water)> _respawns = [];
    private readonly Random _random = new();
    private uint _nextId = 1;

    internal FishPond(string zonePath, List<FishingWater> waters) {
        ZonePath = zonePath;
        Waters = waters;
        foreach (var water in waters) {
            for (var i = 0; i < Math.Max(water.FishCount, 0); i++) {
                Spawn(water);
            }
        }
    }

    internal string ZonePath { get; }
    internal List<FishingWater> Waters { get; }

    /// <summary>Counts up whenever a fish swims in or leaves, so a player can tell their list is out of date.</summary>
    internal int Version { get; private set; }

    /// <summary>The fish swimming now, after any respawns that are due.</summary>
    internal List<LiveFish> Snapshot() {
        lock (_lock) {
            RespawnDue();

            return [.. _fish.Values.OrderBy(f => f.Id)];
        }
    }

    internal bool TryGet(uint fishId, out LiveFish fish) {
        lock (_lock) {
            return _fish.TryGetValue(fishId, out fish);
        }
    }

    /// <summary>Takes a fish out of its pond (caught or escaped); a new one swims in after the respawn time.</summary>
    internal bool Remove(uint fishId, out LiveFish fish) {
        lock (_lock) {
            if (!_fish.Remove(fishId, out fish)) {
                return false;
            }

            var water = Waters.ElementAtOrDefault(fish.Water);
            var seconds = Math.Max(water?.SecondsToRespawn ?? 0, (uint) FishingSettings.MinRespawnSeconds);
            _respawns.Add((DateTime.UtcNow.AddSeconds(seconds), fish.Water));
            Version++;

            return true;
        }
    }

    /// <summary>Empties the ponds and fills them again with new fish.</summary>
    internal void Reset() {
        lock (_lock) {
            _fish.Clear();
            _respawns.Clear();
            foreach (var water in Waters) {
                for (var i = 0; i < Math.Max(water.FishCount, 0); i++) {
                    Spawn(water);
                }
            }

            Version++;
        }
    }

    /// <summary>
    /// A random fish this pond can have, used when the client names a fish the server never sent (the client may make its
    /// own fish): one of the lure's school when the pond has one, otherwise any.
    /// </summary>
    internal LiveFish StandIn(string school) {
        lock (_lock) {
            var kinds = Waters.SelectMany(w => w.Fish.Select(f => (Water: w.Index, Fish: f)))
                .Select(k => (k.Water, k.Fish, Info: FishingData.Fish(k.Fish.m_fishTemplateID)))
                .Where(k => k.Info is not null && !k.Info.IsTest)
                .ToList();
            if (kinds.Count == 0) {
                return null;
            }

            var ofSchool = kinds.Where(k => string.Equals(k.Info.School, school, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = (ofSchool.Count > 0 ? ofSchool : kinds)[_random.Next(ofSchool.Count > 0 ? ofSchool.Count : kinds.Count)];

            return new LiveFish(0, pick.Info.TemplateId, RandomSize(pick.Info), pick.Water);
        }
    }

    private void RespawnDue() {
        var now = DateTime.UtcNow;
        for (var i = _respawns.Count - 1; i >= 0; i--) {
            if (_respawns[i].When > now) {
                continue;
            }

            var water = Waters.ElementAtOrDefault(_respawns[i].Water);
            _respawns.RemoveAt(i);
            if (water is not null) {
                Spawn(water);
            }
        }
    }

    private void Spawn(FishingWater water) {
        var kinds = water.Fish
            .Select(f => (Zone: f, Info: FishingData.Fish(f.m_fishTemplateID)))
            .Where(k => k.Info is not null && !k.Info.IsTest)
            .ToList();
        if (kinds.Count == 0) {
            return;
        }

        // Each kind's creation chance is its weight; kinds with none set count as 1.
        var total = kinds.Sum(k => k.Zone.m_creationChance > 0 ? k.Zone.m_creationChance : 1f);
        var roll = _random.NextDouble() * total;
        var pick = kinds[^1];
        foreach (var kind in kinds) {
            roll -= kind.Zone.m_creationChance > 0 ? kind.Zone.m_creationChance : 1f;
            if (roll <= 0) {
                pick = kind;

                break;
            }
        }

        // The game puts a fish in the pond numbered by the low 4 bits of its id (FishingManager), so the pond goes there.
        var fish = new LiveFish((_nextId++ << 4) | (uint) (water.Index & 0xF), pick.Info.TemplateId, RandomSize(pick.Info), water.Index);
        _fish[fish.Id] = fish;
        Version++;
    }

    private float RandomSize(FishInfo info)
        => (float) (info.MinSize + _random.NextDouble() * (info.MaxSize - info.MinSize));

}

/// <summary>The ponds of every zone that has been fished in since the server started.</summary>
internal static class FishPonds {

    private static readonly ConcurrentDictionary<string, FishPond> s_ponds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pond of a zone, or null while its ponds are still being read (<paramref name="ready"/> false) or when the zone
    /// has none.
    /// </summary>
    internal static FishPond For(string zonePath, out bool ready) {
        var waters = FishingData.Waters(zonePath, out ready);
        if (!ready || waters is null || waters.Count == 0) {
            return null;
        }

        return s_ponds.GetOrAdd(zonePath, path => {
            var pond = new FishPond(path, waters);
            Logger.Information("Fishing: {0} fish swim in zone {1}.", Logger.Args(pond.Snapshot().Count, path));

            return pond;
        });
    }

}
