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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// A new approach after ten stuck alarms at one spot on one step (Niduus, 2026-11-14, approved): the creatures around him
/// are fought (a boss or a guard in the way is often what holds a step back), or, with none, the step is set aside for a
/// while (other story work first) instead of the same four moves again.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // Ten was too many: in his logs (2026-11-13/14, 143 escapes) 9 in 10 got free within 4 tries, so a fifth alarm at the same
    // spot already means the usual moves don't work there (Niduus, 2026-11-15: get unstuck faster).
    private const int ALARMS_TO_CHANGE = 5;
    private const float CLEAR_RANGE = 1500f;

    // Alarms per spot and step this session ("zone|cell|step").
    private readonly Dictionary<string, int> _alarms = new(StringComparer.OrdinalIgnoreCase);

    // Clearing the creatures around a stuck spot: where, and until when at most.
    private (string Zone, float X, float Y, DateTime Until)? _clearAround;

    private bool TenAlarms(UltimusMember.Status member, UltimusPlanner.Target goal, string step, string quest, string where) {
        var key = $"{member.Zone}|{(int) (member.X / 500)}|{(int) (member.Y / 500)}|{step}";
        var count = _alarms[key] = _alarms.GetValueOrDefault(key) + 1;
        if (count % ALARMS_TO_CHANGE != 0) {
            return false;
        }

        var foes = BotUseGuards.Near(member.ZoneActor, member.X, member.Y)
            .Where(m => !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full)).ToList();
        if (foes.Count > 0) {
            _clearAround = (member.Zone ?? "", member.X, member.Y, DateTime.UtcNow.AddMinutes(10));
            Note("Ultimus", $"NEW APPROACH: stuck {count} times in {where} on \"{goal?.What ?? _doing}\": the same moves are no use, so he fights " +
                            $"the {foes.Count} creature(s) around him first ({string.Join(", ", foes.Select(f => f.Name).Distinct().Take(3))}), then tries again.",
                UltimusLink.NOTABLE);
            Think($"I got stuck here {count} times doing the same things. Something here holds me back, so I change my approach: I beat the " +
                  "creatures around me first, as a player clears a room, and then try my step again.", "approach-fight|" + key + "|" + count);

            return true;
        }

        Note("Ultimus", $"NEW APPROACH: stuck {count} times in {where} on \"{goal?.What ?? _doing}\" with no creatures around: the step is set aside " +
                        "for a while and he does other story work first.", UltimusLink.NOTABLE);
        Think($"I got stuck here {count} times doing the same things and there is nothing to fight. I set this step aside for a while and come back to it fresh.",
            "approach-park|" + key + "|" + count);
        ParkStep(member, quest, goal?.Key, goal?.What ?? _doing ?? "his step", where);

        return true;
    }

    // While clearing around a stuck spot: the nearest free creature there comes first.
    private UltimusPlanner.Target ClearAroundFight(UltimusMember.Status leader) {
        if (_clearAround is not { } clear) {
            return null;
        }

        if (DateTime.UtcNow > clear.Until || !string.Equals(clear.Zone, leader.Zone ?? "", StringComparison.OrdinalIgnoreCase)) {
            _clearAround = null;

            return null;
        }

        var foe = BotUseGuards.Near(leader.ZoneActor, clear.X, clear.Y)
            .Where(m => !CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full))
            .OrderBy(m => UltimusPlanner.Far(leader.X, leader.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        if (foe is null) {
            _clearAround = null;
            Note("Ultimus", $"{leader.Name} cleared the creatures around the spot where he was stuck: tries his step again.", UltimusLink.NOTABLE);

            return null;
        }

        return new UltimusPlanner.Target(UltimusPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
            $"{foe.Name}, by the spot where he was stuck (clears it, then tries again)", _currentQuest ?? "", Template: foe.Template,
            GlobalId: foe.Object.m_globalID.Full, Key: "clear|" + foe.Object.m_globalID.Full);
    }

}
