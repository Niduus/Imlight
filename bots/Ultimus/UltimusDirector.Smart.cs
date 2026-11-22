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
    private const float WISP_AFTER_FIGHT_BERTH = 120f;

    /// <summary>A battle he won ended here: the wisps it left are picked up (Niduus, 2026-11-14: "have him nab up the wisps").</summary>
    private void NoteFightSpot(UltimusMember.Status leader) {
        _wispSweep = (leader.Zone ?? "", leader.X, leader.Y, DateTime.UtcNow.AddSeconds(75));
        // The fight's wisps fall in the middle of its circle (Niduus, 2026-11-17: "measure the distance from him and the first
        // enemy slot, after a battle, he charges to the half way point. When a battle ends, the center is given wisps"). His
        // own slot is where he stands now, the first enemy's is where the creature he went for stood: halfway between.
        _wispCenter = _target is { Kind: UltimusPlanner.FIGHT } fought && UltimusPlanner.Far(leader.X, leader.Y, fought.X, fought.Y) is > 60 and <= 900
            ? (leader.Zone ?? "", (leader.X + fought.X) / 2, (leader.Y + fought.Y) / 2, DateTime.UtcNow.AddSeconds(WISP_CENTER_SECONDS))
            : null;
    }

    private (string Zone, float X, float Y, DateTime Until)? _wispCenter;
    // How long after a win he goes to the circle's middle for its wisps (they last [Battle] BattleWispSeconds, 7 by default).
    private const int WISP_CENTER_SECONDS = 9;
    internal const string WISP_CENTER_KEY = "wispcenter|";

    /// <summary>Not full right after a win: to the middle of the circle, where its wisps fall, walking through them.</summary>
    private UltimusPlanner.Target WispCenter(UltimusMember.Status leader, bool health, bool mana) {
        if (_wispCenter is not { } c || DateTime.UtcNow > c.Until || !string.Equals(c.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)
            || (!health && !mana)) {
            return null;
        }

        // Reached once is enough: the wisps there are walked through. Before, stepping off toward the next step made the
        // middle "not reached" again, so he turned back each second until the wisps' time ran out (3,435 such turns in the
        // log of 2026-11-21, Cyndr and Mythis flipping every second).
        if (UltimusPlanner.Far(leader.X, leader.Y, c.X, c.Y) <= 40) {
            _wispCenter = null;

            return null;
        }

        Think("The fight is over and I am not full. Its wisps fall in the middle of the circle, halfway between my spot and the " +
              "first enemy's, so I go there and walk through them.", "wisp-center|" + (int) c.X + "," + (int) c.Y);
        return new UltimusPlanner.Target(UltimusPlanner.SPOT, c.X, c.Y, "to the middle of the battle circle for its wisps",
            Key: WISP_CENTER_KEY + (int) c.X + "," + (int) c.Y);
    }

    private UltimusPlanner.Target WispAfterFight(UltimusMember.Status leader) {
        if (_wispSweep is not { } sweep || DateTime.UtcNow > sweep.Until || !string.Equals(sweep.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)
            || leader.Wizard?.GameStats is not { } stats) {
            _wispSweep = null;
            return null;
        }

        var health = stats.m_currentHitpoints < stats.m_baseHitpoints + stats.m_bonusHitpoints;
        var mana = stats.m_currentMana < stats.m_baseMana + stats.m_bonusMana;
        // The creatures just beaten still stand where the fight was for a moment, and more walk by: a fight's own wisps lie
        // right among them, so only one standing right on a wisp keeps him off it (Niduus, 2026-11-14, v0.10.855: "He did
        // not collect the after battle wisps"; the 500-unit berth for the area's wisps ruled every one of them out).
        if ((!health && !mana) || Wisp(leader, health, mana, WISP_AFTER_FIGHT_BERTH) is not { } wisp
            || UltimusPlanner.Far(sweep.X, sweep.Y, wisp.X, wisp.Y) > WISP_SWEEP_REACH) {
            return WispCenter(leader, health, mana);
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
        // The creatures just beaten still stand where the fight was for a moment, and more walk by: a fight's own wisps lie
        // right among them, so only one standing right on a wisp keeps him off it (Niduus, 2026-11-14, v0.10.855: "He did
        // not collect the after battle wisps"; the 500-unit berth for the area's wisps ruled every one of them out).
        if ((!health && !mana) || Wisp(leader, health, mana, WISP_AFTER_FIGHT_BERTH) is not { } wisp
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
    /// <summary>
    /// In a dungeon, the dungeon's own quest first (the one the game grants on the way in): it is what opens the dungeon's
    /// doors and teleporters on the way to the story's boss (Niduus, 2026-11-15: "I think our boy is stuck again"; in the
    /// Temple of Storms he went straight for Krokopatra, and the teleporter there stays shut until "Test of Strength" and the
    /// other tests are done). Null when there is none to do here.
    /// </summary>
    // The dungeon's own quest being done first, kept while in the dungeon (any of its rooms).
    private string _dungeonFirstQuest;

    private UltimusPlanner.Target DungeonQuestFirst(List<UltimusMember.Status> group, UltimusPlanner.Target main) {
        var leader = group[0];
        if (leader.Wizard is not { ZoneIsInstance: true } wizard || string.IsNullOrEmpty(leader.Zone)) {
            _dungeonFirstQuest = null;
            return null;
        }

        var own = DungeonQuestIndex.GetQuestsForZones([leader.Zone]).Select(q => (string) q.m_questName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The dungeon quest already being done goes on in the dungeon's next rooms too: its next step may be in a room
        // whose own quests don't list it (his log, 2026-11-15: "Gate Crashers" sent him from The Ironworks into Nail's
        // Hideout, where the story step pulled him back to The Ironworks, 47 times back and forth).
        if (_dungeonFirstQuest is { Length: > 0 } going) {
            own.Add(going);
        }
        var held = wizard.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null && own.Contains(q.QuestName)
            && !string.Equals(q.QuestName, main?.Quest ?? _currentQuest, StringComparison.OrdinalIgnoreCase)).Select(q => q.QuestName).ToList() ?? [];
        foreach (var name in held) {
            var step = UltimusPlanner.ForQuest(group, name);
            if (step is null || step.Problem.Length > 0 || step.Kind is UltimusPlanner.WAIT or UltimusPlanner.NONE) {
                // It sent him into this room, and here it has nothing he can do: that way is a dead end for this step.
                NoteDungeonDeadEnd(leader, name);
                continue;
            }

            // Its creature here was stood on and started no battle: left alone, as everywhere else (BotShared.DudCreatures).
            // Idunn, Shirataki Temple (2026-11-22 02:17-02:27): this path picked Shoji again right after she set it aside,
            // flip-flopping with the way out 700 times in ten minutes; ResetCopy now starts the copy over for it (QuickReset).
            if (step.Kind == UltimusPlanner.FIGHT && (step.GlobalId != 0 && BotShared.DudCreatures.IsDud(step.GlobalId) || DudStep(step))) {
                continue;
            }

            if (step.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL && step.To is { Length: > 0 } to
                && _dungeonDeadEnds.TryGetValue(DeadEndKey(name, step.Goal, to), out var until) && DateTime.UtcNow < until) {
                Think($"\"{UltimusText.Title(name)}\" points me to {UltimusText.Zone(to)}, but I was just there and its step had " +
                      $"nothing for me to do, so I go on to {main?.What ?? "my story step"} from here instead.", "dungeon-deadend|" + name + "|" + to);
                continue;
            }

            Think($"I am in a dungeon, and its own quest \"{UltimusText.Title(name)}\" opens the way through it: I do \"{step.What}\" " +
                  $"first, then go on to {main?.What ?? "my story step"}, as a player does.", "dungeonfirst|" + step.Key);

            _dungeonFirstQuest = name;
            _dungeonFirstLead = step.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL && step.To is { Length: > 0 }
                ? (name, step.Goal ?? "", step.To)
                : null;
            return step;
        }

        return null;
    }

    // The way the dungeon's own quest last sent him (its quest, step and the room it led to), and the ways that led to a room
    // where that step had nothing to do. In Shirataki Temple, "Summoning the Spirit" sent Tulkar from the Plague Oni's palace
    // out to the cliffside, where its shrines could not be placed, so the story step took him back in: 43 times in 20 minutes
    // (2026-11-21 23:10-23:30). A dead end is skipped for 20 minutes, so the story step (the Oni, right in that palace) goes on.
    private (string Quest, string Goal, string To)? _dungeonFirstLead;
    private readonly Dictionary<string, DateTime> _dungeonDeadEnds = new(StringComparer.OrdinalIgnoreCase);

    private static string DeadEndKey(string quest, string goal, string to) => quest + "|" + (goal ?? "") + "|" + to;

    private void NoteDungeonDeadEnd(UltimusMember.Status leader, string quest) {
        if (_dungeonFirstLead is not { } lead || !string.Equals(lead.Quest, quest, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lead.To, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        _dungeonDeadEnds[DeadEndKey(lead.Quest, lead.Goal, lead.To)] = DateTime.UtcNow.AddMinutes(20);
        _dungeonFirstLead = null;
    }

    /// <summary>
    /// Anywhere (not only in a dungeon): a way here that won't take him, or that leads where his step is, opens with a quest
    /// he holds, so that quest's step comes first (Niduus, 2026-11-15: "make sure all situations like this are also
    /// future-proof for later worlds/quests/scenarios"). The general lesson of the Temple of Storms' tests and the Tomb of
    /// Storms' obelisks, read from the game's own door needs (BotGates), so it works in every world. Null when there is none.
    /// </summary>
    private UltimusPlanner.Target GateQuestFirst(List<UltimusMember.Status> group, UltimusPlanner.Target main) {
        var leader = group[0];
        var mainTo = main?.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL ? main.To : "";
        if (BotShared.BotGates.OpensTheWay(leader.Wizard, leader.Zone, main?.Quest ?? _currentQuest, mainTo, out var why) is not { } opener) {
            return null;
        }

        var step = UltimusPlanner.ForQuest(group, opener);
        if (step is null || step.Problem.Length > 0 || step.Kind is UltimusPlanner.WAIT or UltimusPlanner.NONE) {
            return null;
        }

        Think($"\"{UltimusText.Title(opener)}\" opens my way on: {why}. I do \"{step.What}\" first, then go on to " +
              $"{main?.What ?? "my story step"}, as a player does.", "gatefirst|" + step.Key);

        return step;
    }

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
                    or UltimusPlanner.WAIT or UltimusPlanner.NONE
                || other.Kind == UltimusPlanner.FIGHT && (other.GlobalId != 0 && BotShared.DudCreatures.IsDud(other.GlobalId) || DudStep(other))) {
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
    /// Whether he wants the fights on his way: never. Niduus, 2026-11-14: no farming creatures for XP, the main story comes
    /// first; 2026-11-19: "avoid ones that he doesn't need for his quest. Making sure to only fight mobs named after the
    /// targets of his quests". Creatures his step doesn't need are always walked round, whatever the playbook says; his XP
    /// comes from quest fights and rewards (and a side quest of his level when he is short).
    /// </summary>
    private static bool SeekFights(UltimusMember.Status member) => false;

}
