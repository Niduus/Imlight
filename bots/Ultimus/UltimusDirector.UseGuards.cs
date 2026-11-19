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
using System.Linq;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// A quest thing that won't work yet because its boss or creatures still stand (Niduus, 2026-11-14, the Frozen Forge in
/// Odji's Chamber): he tries it first, as some bosses can be skipped; when it does not work, he fights what stands by it,
/// then tries it again (BotUseGuards).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // The use did not work: true when creatures stand by it and he goes to fight them first.
    private bool UseNeedsFight(UltimusMember.Status member, UltimusPlanner.Target target, string why) {
        var on = BotUseGuards.On(member.CharId, target.Quest, target.Goal);
        if (!BotUseGuards.Failed(member.CharId, target.Quest, target.Goal, member.Zone, member.ZoneActor, target.X, target.Y)) {
            return false;
        }

        if (!on) {
            Note("Ultimus", $"{member.Name} tried {target.What} ({why}) with creatures still standing by it: fights them first, then tries again.",
                UltimusLink.NOTABLE);
            Think($"I tried {target.What}, but {why}. The creatures by it are still standing, so I beat them first and then try again, " +
                  "as a player does when a boss guards the thing.", "useguards|" + target.Key);
        }

        return true;
    }

    // While its creatures stand: the fight by the thing comes before the thing itself.
    private UltimusPlanner.Target GuardFight(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target?.Kind != UltimusPlanner.USE) {
            return null;
        }

        // Learned before: this thing needed its creatures beaten first, so he fights them first this time.
        if (BotUseGuards.FightFirst(leader.CharId, target.Quest, target.Goal, leader.Zone, leader.ZoneActor, target.X, target.Y)) {
            Note("Ultimus", $"{leader.Name} remembers that {target.What} needed the creatures by it beaten first: fights them first this time.",
                UltimusLink.NOTABLE);
        }

        if (!BotUseGuards.On(leader.CharId, target.Quest, target.Goal)) {
            return null;
        }

        var foe = BotUseGuards.Next(leader.CharId, target.Quest, target.Goal, leader.Zone, leader.ZoneActor, leader.X, leader.Y, out var cleared);
        if (cleared) {
            _collectSearch.Remove(target.Key);
            foreach (var key in _clicks.Keys.Where(k => k.StartsWith(target.Key + "|", StringComparison.Ordinal)).ToList()) {
                _clicks.Remove(key);
            }

            Note("Ultimus", $"{leader.Name} beat what stood by {target.What}: tries it again.", UltimusLink.NOTABLE);
            Think($"The creatures by {target.What} are beaten. I try it again now.", "useguards-done|" + target.Key);

            return null;
        }

        return foe is null
            ? null
            : new UltimusPlanner.Target(UltimusPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
                $"{foe.Name}, standing by {target.What} (beat it first, then try again)", target.Quest, Template: foe.Template,
                GlobalId: foe.Object.m_globalID.Full, Key: "useguards|" + target.Key);
    }

}
