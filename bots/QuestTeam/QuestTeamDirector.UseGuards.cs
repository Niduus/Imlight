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

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// A quest thing that won't work yet because its boss or creatures still stand (Niduus, 2026-11-14, the Frozen Forge in
/// Odji's Chamber): the team tries it first, as some bosses can be skipped; when it does not work, they fight what stands
/// by it, then try it again (BotUseGuards).
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    // The use step the leader came within reach of, and since when (UseHere: 10 seconds by it with nothing offered).
    private (string Key, DateTime Since) _useNear = ("", DateTime.MaxValue);

    // The use did not work: true when creatures stand by it and the team goes to fight them first.
    private bool UseNeedsFight(QuestTeamMember.Status member, QuestTeamPlanner.Target target, string why) {
        var on = BotUseGuards.On(member.CharId, target.Quest, target.Goal);
        if (!BotUseGuards.Failed(member.CharId, target.Quest, target.Goal, member.Zone, member.ZoneActor, target.X, target.Y)) {
            return false;
        }

        if (!on) {
            Note("Team", $"{member.Name} tried {target.What} ({why}) with creatures still standing by it: the team fights them first, then tries again.",
                QuestTeamLink.NOTABLE);
        }

        return true;
    }

    // While its creatures stand: the fight by the thing comes before the thing itself.
    private QuestTeamPlanner.Target GuardFight(QuestTeamMember.Status leader, QuestTeamPlanner.Target target) {
        if (target?.Kind != QuestTeamPlanner.USE) {
            return null;
        }

        // Learned before: this thing needed its creatures beaten first, so he fights them first this time.
        if (BotUseGuards.FightFirst(leader.CharId, target.Quest, target.Goal, leader.Zone, leader.ZoneActor, target.X, target.Y)) {
            Note("Team", $"{leader.Name} remembers that {target.What} needed the creatures by it beaten first: fights them first this time.",
                QuestTeamLink.NOTABLE);
        }

        if (!BotUseGuards.On(leader.CharId, target.Quest, target.Goal)) {
            return null;
        }

        var foe = BotUseGuards.Next(leader.CharId, target.Quest, target.Goal, leader.Zone, leader.ZoneActor, leader.X, leader.Y, out var cleared);
        if (cleared) {
            foreach (var member in _team) {
                BotCollect.ForgetStep(member.CharId, target.Quest, target.Goal);
            }

            foreach (var key in _clicks.Keys.Where(k => k.StartsWith(target.Key + "|", StringComparison.Ordinal)).ToList()) {
                _clicks.Remove(key);
            }

            Note("Team", $"{leader.Name} and the team beat what stood by {target.What}: they try it again.", QuestTeamLink.NOTABLE);

            return null;
        }

        return foe is null
            ? null
            : new QuestTeamPlanner.Target(QuestTeamPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
                $"{foe.Name}, standing by {target.What} (beat it first, then try again)", target.Quest, Template: foe.Template,
                GlobalId: foe.Object.m_globalID.Full, Key: "useguards|" + target.Key);
    }

}
