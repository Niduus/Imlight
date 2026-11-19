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
using Akka.Actor;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Back into the same dungeon copy after falling or being put out of it, by Dungeon Recall, as a player does. He lost to
/// Gearhead on Big Ben's ninth floor, filled his potions in Wizard City, and then walked into a new copy of Big Ben and
/// did floors one to five again (his log, 2026-11-16 04:57 to 05:21), though his Dungeon Recall still held the copy he
/// left. Once he is well enough to go on (or the recall is about to run out), he takes it before anything else.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // Health share he wants before he goes back in; under it his usual healing (a potion trip) comes first.
    private const double COMEBACK_HEALTH = 0.6;
    // With this little time left on the recall he goes back in anyway (above a quarter of his health).
    private const uint COMEBACK_LAST_SECONDS = 120;
    // A potion fill before going back in only with this much of the recall left (a Wizard City trip took about 7 minutes).
    private const uint COMEBACK_POTION_SECONDS = 420;

    // The dungeon area he fell in, and when: only a copy he fell in (not one he left when done) is gone back to.
    private (string Zone, DateTime At)? _fellIn;
    private static readonly TimeSpan s_comebackWithin = TimeSpan.FromMinutes(20);

    private DateTime _comebackSentAt = DateTime.MinValue;
    private int _comebackTries;

    private bool DungeonComeback(UltimusMember.Status leader) {
        // Back in: done.
        if (leader?.Wizard?.ZoneIsInstance == true && _comebackTries > 0) {
            _fellIn = null;
        }

        if (leader?.Wizard is not { } wizard || wizard.ZoneIsInstance || _partyMate != 0 || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || leader.Talking || _fellIn is not { } fell || DateTime.UtcNow - fell.At > s_comebackWithin
            || !World.DungeonRecall.TryGet(leader.CharId, out var ticket) || !OverlayQuestTracker.SameZone(ticket.Zone ?? "", fell.Zone)
            // Never back into a side dungeon his story step is not in (Counterweight East, 2026-11-16).
            || IsSideDungeon(fell.Zone)) {
            _comebackTries = 0;
            return false;
        }

        if (DateTime.UtcNow - _comebackSentAt < TimeSpan.FromSeconds(15)) {
            _doing = "going back into the dungeon by Dungeon Recall";
            return true;
        }

        if (_comebackTries >= 3) {
            return false;
        }

        var stats = wizard.GameStats;
        var share = stats is null ? 1.0 : stats.m_currentHitpoints / (double) Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
        var now = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var runningOut = ticket.ExpireTime > now && ticket.ExpireTime - now <= COMEBACK_LAST_SECONDS;
        // About to walk in through the door again: that makes a new copy at the same health, with every floor to do over
        // (his log, 2026-11-16 06:46: fell on Big Ben's first floor, then at about half health walked in again and started
        // at Travis Pawman). The recall is never worse than that, so he takes it now, once over a quarter of his health
        // (under it his healing comes first, as before).
        // No potions and the boss still ahead: filled first while the recall has time for it (Big Ben, 2026-11-17: he went
        // back in to Meowiarty with none, six times, and lost five). The recall running out still brings him back at once.
        var left = ticket.ExpireTime > now ? ticket.ExpireTime - now : 0;
        if (!runningOut && PotionsBeforeComeback(leader, fell.Zone, left)) {
            return false;
        }

        var walkingIn = _target is { } next && !string.IsNullOrEmpty(next.To) && OverlayQuestTracker.SameZone(next.To, fell.Zone);
        // Walking in at any health is never better than the recall (16:38, same day: under a quarter of his health he
        // walked in at floor one again), so then he always takes it.
        if (share < COMEBACK_HEALTH && !walkingIn && !(runningOut && share >= 0.25)) {
            return false;
        }

        _comebackTries++;
        _comebackSentAt = DateTime.UtcNow;
        _hubPressedAt = DateTime.UtcNow;
        _buttonFromZone = leader.Zone;
        // The trip's own Recall to its marked spot is not needed now: the dungeon is where he goes.
        _markedZone = null;
        Note("Ultimus", $"{leader.Name} goes back into {UltimusText.Zone(ticket.Zone)} by Dungeon Recall ({share:P0} health), " +
                        "to the same copy and the floors already done.", UltimusLink.NOTABLE);
        Think($"I left {UltimusText.Zone(ticket.Zone)} with its floors done and my Dungeon Recall still holds that copy. " +
              "I go back by it instead of walking in again and doing every floor over.", "comeback|" + ticket.Zone);
        _actors[0].Tell(new UltimusMember.UseDungeonRecall());

        return true;
    }

}
