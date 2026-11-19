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
using Akka.Actor;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// Gear sets for every job, and the gear in them locked (Niduus, 2026-11-15: "Have him also make gear sets and lock gear he
/// may need for Support, Blading, Healing, anything in between"). Out of battle, every few minutes: each job's set from the
/// gear owned (BotGearSets), saved when it changed, then the backpack padlock on what the sets keep.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private readonly Dictionary<string, DateTime> _nextGearSetLook = new(StringComparer.Ordinal);

    private void KeepGearSets(int index) {
        var member = _team[index];
        if (member.Wizard is not { } wizard || CombatDuelComponent.IsInBattle(member.GlobalId)
            || DateTime.UtcNow < _nextGearSetLook.GetValueOrDefault(member.Role)) {
            return;
        }

        _nextGearSetLook[member.Role] = DateTime.UtcNow.AddMinutes(3);
        var saved = new List<string>();
        foreach (var role in BotGearSets.Roles) {
            if (BotGearSets.Save(wizard, role, BotGearSets.OutfitFor(wizard, role.Style))) {
                saved.Add(role.Name);
            }
        }

        if (saved.Count > 0) {
            Note(member.Role, $"{member.Name} saves its gear sets for {string.Join(", ", saved)} (one for every job: " +
                              string.Join("; ", BotGearSets.Roles.Select(r => $"{r.Name}, {r.What}")) + ").");
        }

        var clicks = BotGearSets.LockClicks(wizard);
        if (clicks.Count > 0) {
            _actors[index].Tell(new QuestTeamMember.LockItems(clicks));
        }
    }

}
