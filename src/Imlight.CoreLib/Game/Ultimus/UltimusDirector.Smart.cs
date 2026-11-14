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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Thinking smarter on the way (Niduus, 2026-11-14, approved round): wisps picked up after a fight, a top-up from a wisp
/// right before a fight, steps of his other quests done while he is right by them (before he walks off or turns in), and
/// creatures on the way fought instead of walked round when he is short of XP.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Wisps after a fight
    // ---------------------------------------------------------------------

    private (string Zone, float X, float Y, DateTime Until)? _wispSweep;
    private const float WISP_SWEEP_REACH = 900f;

    /// <summary>A battle he won ended here: the wisps it left are picked up (Niduus, 2026-11-14: "have him nab up the wisps").</summary>
    private void NoteFightSpot(UltimusMember.Status leader) => _wispSweep = (leader.Zone ?? "", leader.X, leader.Y, DateTime.UtcNow.AddSeconds(40));

    private UltimusPlanner.Target WispAfterFight(UltimusMember.Status leader) {
        if (_wispSweep is not { } sweep || DateTime.UtcNow > sweep.Until || !string.Equals(sweep.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)
            || leader.Wizard?.GameStats is not { } stats) {
            _wispSweep = null;
            return null;
        }

        var health = stats.m_currentHitpoints < stats.m_baseHitpoints + stats.m_bonusHitpoints;
        var mana = stats.m_currentMana < stats.m_baseMana + stats.m_bonusMana;
        if ((!health && !mana) || Wisp(leader, health, mana) is not { } wisp
            || UltimusPlanner.Far(sweep.X, sweep.Y, wisp.X, wisp.Y) > WISP_SWEEP_REACH) {
            return null;
        }

        Think($"The fight left {wisp.What} right here, and I am not full: I pick it up before I go on, as any player does.", "wisp-after|" + wisp.Key);

        return wisp with { What = $"picks up {wisp.What} after the fight" };
    }

    /// <summary>
    /// A fight is next and he is not full: a wisp close by first (approved by Niduus, 2026-11-14: "heal right before fights").
    /// </summary>
    private UltimusPlanner.Target TopUpBeforeFight(UltimusMember.Status leader, UltimusPlanner.Target fight) {
        if (fight?.Kind != UltimusPlanner.FIGHT || leader.Wizard?.GameStats is not { } stats) {
            return null;
        }

        var health = stats.m_currentHitpoints < (stats.m_baseHitpoints + stats.m_bonusHitpoints) * 0.9;
        var mana = stats.m_currentMana < (stats.m_baseMana + stats.m_bonusMana) * 0.6;
        if ((!health && !mana) || Wisp(leader, health, mana) is not { } wisp
            || UltimusPlanner.Far(leader.X, leader.Y, wisp.X, wisp.Y) > 900) {
            return null;
        }

        Think($"A fight is next and I am not at full strength; {wisp.What} is close by, so I take it first.", "wisp-before|" + wisp.Key);

        return wisp with { What = $"picks up {wisp.What} before the fight" };
    }

    // ---------------------------------------------------------------------
    // Steps of his other quests while he is right by them
    // ---------------------------------------------------------------------

    private const float WHILE_HERE = 900f;

    /// <summary>
    /// Another quest he holds has a step right by him (a person to talk to, a creature, a thing to use) while his main step
    /// is a walk away or out of the area (approved by Niduus, 2026-11-14: "grab nearby next-quest targets", "finish nearby
    /// goals before turning in"): that one first, as a player does what is in front of them. Null when there is none.
    /// </summary>
    private UltimusPlanner.Target WhileHere(List<UltimusMember.Status> group, UltimusPlanner.Target main) {
        if (main is null || main.Kind is UltimusPlanner.FIGHT && main.Key.EndsWith("|join", StringComparison.Ordinal)) {
            return null;
        }

        var leader = group[0];
        var travel = main.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL;
        var mainWalk = UltimusPlanner.WalkLength(leader, main.X, main.Y);
        if (!travel && mainWalk < WHILE_HERE + 600) {
            return null;
        }

        var held = leader.Wizard?.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null
            && !string.Equals(q.QuestName, main.Quest, StringComparison.OrdinalIgnoreCase)).Select(q => q.QuestName).ToList() ?? [];
        UltimusPlanner.Target best = null;
        var bestWalk = float.MaxValue;
        foreach (var name in held) {
            if (QuestTemplateCollection.GetQuestByName(name) is not { } template || !UltimusPlanner.IsMainStoryWorld(template)) {
                continue;
            }

            var other = UltimusPlanner.ForQuest(group, name);
            if (other is null || other.Problem.Length > 0 || other.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL
                    or UltimusPlanner.WAIT or UltimusPlanner.NONE) {
                continue;
            }

            var walk = UltimusPlanner.WalkLength(leader, other.X, other.Y);
            if (walk < WHILE_HERE && walk < bestWalk) {
                (best, bestWalk) = (other, walk);
            }
        }

        if (best is not null) {
            Think($"\"{best.What}\" for another quest of mine is right here ({bestWalk:0} away), and my main step is " +
                  (travel ? "in another area" : $"{mainWalk:0} away") + ": I do it now, so I don't have to come back.", "whilehere|" + best.Key);
        }

        return best;
    }

    // ---------------------------------------------------------------------
    // Seek or avoid fights on the way
    // ---------------------------------------------------------------------

    /// <summary>
    /// Whether he wants the fights on his way (approved by Niduus, 2026-11-14: "avoid or seek fights depending on the step"):
    /// short of the level his quest needs and healthy, a creature on the way is XP, not something to walk round.
    /// </summary>
    private bool SeekFights(UltimusMember.Status member) {
        if (member.Wizard is not { } wizard || wizard.GameStats is not { } stats || wizard.ZoneIsInstance) {
            return false;
        }

        if (stats.m_currentHitpoints < (stats.m_baseHitpoints + stats.m_bonusHitpoints) * 0.75) {
            return false;
        }

        var quest = _currentQuest is null ? null : QuestTemplateCollection.GetQuestByName(_currentQuest);
        var level = wizard.MagicSchoolBehavior?.Level ?? 1;
        var needs = quest?.m_questLevel ?? 0;
        if (UltimusPlanner.LevelBlocked(wizard) is var (_, blocked)) {
            needs = Math.Max(needs, blocked);
        }

        var seek = needs > level;
        if (seek) {
            Think($"I am level {level} and my story needs {needs}: creatures on my way are XP, so I don't walk round them.", "seek|" + level);
        }

        return seek;
    }

}
