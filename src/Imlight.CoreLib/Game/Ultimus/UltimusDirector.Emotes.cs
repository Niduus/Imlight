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
using Akka.Actor;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's emotes (Niduus, 2026-11-14, approved; [Ultimus] Emotes): a wave for a player who greets him near him, a cheer
/// after a tough fight won, and a sit when he has had nothing to do for a minute. Each from the emote wheel, as a player
/// plays it (UltimusEmotes picks the animation), and never in a battle.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private readonly Dictionary<string, DateTime> _emotedAt = [];
    private DateTime _idleSince = DateTime.MaxValue;

    private void Emote(string kind, string key, string what, TimeSpan every, double delaySeconds = 0) {
        if (!UltimusWire.Emotes || _actors.Count == 0 || _team.Count == 0
            || delaySeconds <= 0 && CombatDuelComponent.IsInBattle(_team[0].GlobalId)) {
            return;
        }

        if (_emotedAt.TryGetValue(key, out var at) && DateTime.UtcNow - at < every) {
            return;
        }

        _emotedAt[key] = DateTime.UtcNow;
        var emote = new UltimusMember.Emote(UltimusEmotes.Animation(kind), what);
        if (delaySeconds > 0) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(delaySeconds), _actors[0], emote, Self);
        }
        else {
            _actors[0].Tell(emote);
        }
    }

    /// <summary>A player near him said hello: a wave back (once in 10 minutes for each player).</summary>
    private void WaveAt(ulong charId, string name)
        => Emote(UltimusEmotes.WAVE, "wave|" + charId, $"waves at {name}", TimeSpan.FromMinutes(10));

    /// <summary>A tough fight won: a cheer, once the battle's end has played.</summary>
    private void CheerWin(string foe) => Emote(UltimusEmotes.CHEER, "cheer", $"cheers after beating {foe}", TimeSpan.FromMinutes(2), delaySeconds: 6);

    /// <summary>Called each plan: nothing to do for a minute, and he sits down (again only after 5 minutes).</summary>
    private void Idle(bool idle) {
        if (!idle) {
            _idleSince = DateTime.MaxValue;
            return;
        }

        if (_idleSince == DateTime.MaxValue) {
            _idleSince = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - _idleSince > TimeSpan.FromMinutes(1) && _team.Count > 0 && !_team[0].Walking) {
            Emote(UltimusEmotes.SIT, "sit", "sits down while he waits", TimeSpan.FromMinutes(5));
        }
    }

}
