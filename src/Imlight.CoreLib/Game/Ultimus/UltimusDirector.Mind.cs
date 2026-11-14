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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus thinking out loud (Niduus, 2026-11-13: "Have his thoughts be in the logs but also make a new section on his page
/// for Ultimus Thoughts ... so he can think out loud in that section and we can understand his mindset"): why it picks what
/// it picks, what it remembers of a creature or a spot, and what it will do differently. Each thought is a THOUGHT line of
/// its log (brown in Ahziael). Its mistakes, with its reasoning, go in the bots' shared book (BotMistakes).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // The same thought is not said again for a while.
    private static readonly TimeSpan s_thoughtGap = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTime> _thoughts = new(StringComparer.Ordinal);

    // Its reasoning a moment ago, kept with a mistake when one is made.
    private string _lastReasoning = "";

    // His last thoughts, for "Pause and explain".
    private readonly List<string> _recentThoughts = [];

    private void Think(string thought, string key = null) {
        key ??= thought;
        if (_thoughts.TryGetValue(key, out var said) && DateTime.UtcNow - said < s_thoughtGap) {
            return;
        }

        _thoughts[key] = DateTime.UtcNow;
        if (_thoughts.Count > 500) {
            _thoughts.Clear();
        }

        _lastReasoning = thought;
        _recentThoughts.Add($"{DateTime.Now:HH:mm:ss} {thought}");
        if (_recentThoughts.Count > 30) {
            _recentThoughts.RemoveAt(0);
        }

        Note("Ultimus", thought, UltimusLink.THOUGHT);
    }

    // ---------------------------------------------------------------------
    // Creatures it fought
    // ---------------------------------------------------------------------

    // The fight going on: the creature, its name, the health share it went in with, and when.
    private (uint Template, string Name, double Health, DateTime Since)? _fight;

    // Going in at full health against a creature that beat it or takes most of its health.
    private bool _toughFoeNext;

    private static double HealthShare(UltimusMember.Status member) =>
        member.Wizard?.GameStats is { } stats ? stats.m_currentHitpoints / (double) Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints) : 1;

    /// <summary>A fight is its next step: what it remembers of the creature, and whether it goes in at full health.</summary>
    private void ThinkAboutFight(UltimusPlanner.Target target) {
        var foe = UltimusFoes.Of((uint) Math.Min(target.Template, uint.MaxValue));
        _toughFoeNext = foe?.Tough == true;
        if (foe is null) {
            Think($"I have not fought {FoeName(target)} before. I read its deck and make mine ready for it, and I watch how it goes.");

            return;
        }

        Think(foe.Tough
            ? $"I remember {foe.Describe()}. It is a tough one for me, so I heal up before I go in ({UltimusPlaybook.Cite("Quest.ToughFoeHealth")})."
            : $"I remember {foe.Describe()}. I can take it as I am.", "foe|" + foe.Template);
    }

    private void FightStarted(UltimusMember.Status me) {
        _fight = _target is { Kind: UltimusPlanner.FIGHT } fight
            ? ((uint) Math.Min(fight.Template, uint.MaxValue), FoeName(fight), HealthShare(me), DateTime.UtcNow)
            : (0, "", HealthShare(me), DateTime.UtcNow);
        if (_fight.Value.Template == 0) {
            Think("A battle started that was not my step. I walk round creatures I don't need to fight; this one still caught me.",
                "ambush|" + me.Zone);
        }
    }

    private void FightEnded(UltimusMember.Status me, bool won) {
        if (_fight is not { } fight) {
            return;
        }

        _fight = null;
        var lostShare = Math.Max(0, fight.Health - (won ? HealthShare(me) : 0));
        var seconds = (DateTime.UtcNow - fight.Since).TotalSeconds;
        NoteDayFight(fight.Name.Length > 0 ? fight.Name : "a creature", won, lostShare);
        if (fight.Template == 0) {
            return;
        }

        CheckFight(me, won, lostShare, fight.Name);
        if (BotKnowledge.Of(fight.Template) is { Casts: >= 3 } habit) {
            Think($"What the bots know of its habits now: {habit.Describe()}.", "habit|" + fight.Template);
        }

        var lostBefore = UltimusFoes.Of(fight.Template)?.Lost ?? 0;
        var foe = UltimusFoes.Fought(fight.Template, fight.Name, won, lostShare, seconds);
        // The fights worth remembering: a loss, a win over a creature that beat him, a close call (the journal).
        if (!won || lostBefore > 0 || lostShare >= 0.6) {
            UltimusJournal.Write(won ? UltimusJournal.WON : UltimusJournal.LOST,
                !won ? $"Lost to {foe.Name} in {UltimusText.Zone(me.Zone)}."
                : lostBefore > 0 ? $"Finally beat {foe.Name} in {UltimusText.Zone(me.Zone)}, after it beat me {lostBefore} time{(lostBefore == 1 ? "" : "s")}."
                : $"Beat {foe.Name} in {UltimusText.Zone(me.Zone)}, but it was close: it took {lostShare:P0} of my health.",
                WorldOf(me.Zone), UltimusText.Zone(me.Zone), foe.Name, won ? 1 : -1);
            if (won) {
                CheerWin(foe.Name);
            }
        }

        if (won) {
            Think(foe.Tough
                ? $"I beat {foe.Name}, but it took {lostShare:P0} of my health. Next time I go in at full health."
                : $"I beat {foe.Name} and it took {lostShare:P0} of my health. Now I know what to expect of it.", "won|" + fight.Template);

            return;
        }

        var level = me.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        var reasoning = _lastReasoning.Length > 0 ? _lastReasoning : $"I went in at {fight.Health:P0} health.";
        BotMistakes.Record("Ultimus", BotMistakes.LOST_FIGHT, me.Zone, me.X, me.Y, _target?.What ?? "",
            $"Lost to {foe.Name} alone at level {level} after {seconds / 60:0.#} minutes (went in at {fight.Health:P0} health).", reasoning,
            "Go in at full health and mana with a deck made for it; if it still wins, wait for a player or level up first.", foe: foe.Name);
        Think($"I lost to {foe.Name}. I went in at {fight.Health:P0} health. Next time I heal up to full first and make my deck for it.",
            "lost|" + fight.Template);
        // Why, and a different plan for the next try (UltimusDirector.Life.cs).
        LearnFromLoss(me, fight.Template, foe.Name, fight.Health, seconds);
    }

    private static string FoeName(UltimusPlanner.Target target) {
        var what = target.What ?? "";
        foreach (var lead in new[] { "Defeat and Collect ", "defeat and collect ", "Defeat ", "defeat ", "fight ", "Fight " }) {
            if (what.StartsWith(lead, StringComparison.Ordinal)) {
                return what[lead.Length..];
            }
        }

        return what.Length > 0 ? what : "this creature";
    }

    // ---------------------------------------------------------------------
    // Places it got stuck
    // ---------------------------------------------------------------------

    private void ThinkAboutStuck(UltimusMember.Status member, string why, string fix, UltimusFixes.Flag flag, string step) {
        // "has stood still" is said of him in the log; in his own words it is "I have stood still".
        var mine = why.StartsWith("has ", StringComparison.Ordinal) ? "have " + why[4..] : why;
        var tried = flag.Tried.Count > 1 ? string.Join(", ", flag.Tried.GetRange(0, flag.Tried.Count - 1)) : "";
        var thought = flag.Times <= 1
            ? $"I {mine} here. Nothing is written down for this spot, so I try the cheapest fix first: {fix}."
            : flag.Worked.Length > 0 && flag.Worked == fix
                ? $"I have been stuck here {flag.Times} times. Last time \"{flag.Worked}\" got me free, so I do that again."
                : $"I have been stuck here {flag.Times} times. I tried {tried} and none got me free, so this time I try something new: {fix}.";
        Think(thought, $"stuck|{flag.Zone}|{(int) flag.X}|{(int) flag.Y}|{flag.Times}");
        BotMistakes.Record("Ultimus", BotMistakes.STUCK, member.Zone, member.X, member.Y, step,
            $"Stuck ({why}) while {_doing}.", thought,
            flag.Worked.Length > 0 ? $"Here, \"{flag.Worked}\" works." : "Walk round this spot; the way finder bends round it now.", fix);
    }

    private void ThinkFreed(UltimusMember.Status member, UltimusFixes.Flag flag) {
        BotMistakes.Fixed(BotMistakes.STUCK, flag.Zone, flag.X, flag.Y, flag.Worked);
        Think($"I got free with \"{flag.Worked}\". I write that down for this spot, so I (and the other bots) try it first next time.",
            $"freed|{flag.Zone}|{(int) flag.X}|{(int) flag.Y}");
    }

    /// <summary>
    /// The line of sight circle (UltimusSight): a goal in the circle with nothing in the way is walked to straight; blocked,
    /// a short way round inside the circle. Null when the goal is out of sight or no such way is seen (the route map then).
    /// </summary>
    private List<(float X, float Y)> SightWalk(UltimusMember.Status member, float toX, float toY, string what, List<(float X, float Y)> creatures) {
        var far = UltimusPlanner.Far(member.X, member.Y, toX, toY);
        // A plan he trusts little goes the map's way, no shortcut by sight (Confidence).
        if (far < 40 || _carefulPlan || !UltimusSight.InSight(member.X, member.Y, toX, toY)) {
            return null;
        }

        // A creature it does not need to fight by the line: the route map walks round it instead.
        if (creatures?.Exists(c => NearLine(c.X, c.Y, member.X, member.Y, toX, toY) < 220) == true) {
            return null;
        }

        if (UltimusSight.Clear(member.Zone, member.X, member.Y, toX, toY)) {
            Think($"I can see \"{what}\" {far:0} away, with nothing in the way: I walk straight to it.", "sight|" + what);

            return [(toX, toY)];
        }

        if (UltimusSight.AroundTo(member.Zone, member.X, member.Y, toX, toY) is { } around) {
            Think($"\"{what}\" is {far:0} away in my circle of sight, but something is in the way: I go round it.", "around|" + what);

            return around;
        }

        return null;
    }

    private static float NearLine(float px, float py, float ax, float ay, float bx, float by) {
        var (dx, dy) = (bx - ax, by - ay);
        var t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / MathF.Max(1, (dx * dx) + (dy * dy)), 0, 1);

        return UltimusPlanner.Far(px, py, ax + (dx * t), ay + (dy * t));
    }

    // ---------------------------------------------------------------------
    // Gear, by a Balance solo wizard's goals (UltimusPlaybook [Gear])
    // ---------------------------------------------------------------------

    private static BotGear.Goals GearGoals(Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) {
        var level = wizard?.MagicSchoolBehavior?.Level ?? 1;
        double P(string name) => UltimusPlaybook.Value("Gear." + name);

        return new BotGear.Goals(
            Math.Min(P("AccuracyMax"), P("AccuracyFloor") + (P("AccuracyPerLevel") * level)),
            Math.Min(P("ResistMax"), P("ResistFloor") + (P("ResistPerLevel") * level)),
            P("HealthPerLevel") * level,
            P("PowerPipGoal"), P("DamageWeight"), P("PierceWeight"), P("FloorWeight"), P("OverFloorShare"));
    }

    private void ThinkAboutSwap(UltimusMember.Status member, BotGear.Swap swap, BotGear.Goals goals) {
        var worn = swap.Worn?.Name ?? "nothing";
        Note(member.Role, $"{member.Name} weighs {swap.Piece.Name} against {worn} for its whole outfit (gain {swap.Gain:0.#}).");
        Think($"I put on {swap.Piece.Name} instead of {worn}: {swap.Why}. As a Balance wizard alone I want enough resist, accuracy " +
              $"and health first, power pips toward {goals.PowerPips:0}%, then damage and pierce ({UltimusPlaybook.Cite("Gear.FloorWeight")}).",
            "swap|" + swap.Piece.Id);
    }

    // Once per level: what its outfit gives against what it wants, and what it is short of.
    private int _outfitReviewedAt = -1;

    private void ReviewOutfit(UltimusMember.Status member, Imlight.CoreLib.WizardData.Models.Player.Wizard wizard) {
        var level = wizard.MagicSchoolBehavior?.Level ?? 1;
        if (_outfitReviewedAt == level) {
            return;
        }

        _outfitReviewedAt = level;
        var goals = GearGoals(wizard);
        var totals = BotGear.TotalsOf(BotGear.Outfit(wizard).Values, BotGear.SchoolOf(wizard));
        var short_ = new List<string>();
        if (totals.Resist < goals.Resist) {
            short_.Add("resist");
        }

        if (totals.Accuracy < goals.Accuracy) {
            short_.Add("accuracy");
        }

        if (totals.Health < goals.Health) {
            short_.Add("health");
        }

        Think($"My outfit at level {level}: {totals.Describe(goals)}. " +
              (short_.Count == 0
                  ? "Every floor is met, so I look for damage and pierce next."
                  : $"I am short of {string.Join(" and ", short_)}, so pieces with those come first in shops and drops."),
            "outfit|" + level);

        // The best piece left in the backpack and why it stays there, so a piece he keeps off (a robe) is never a mystery.
        if (BotGear.BestSwap(wizard, goals, double.NegativeInfinity) is { Item: not null } kept) {
            Think($"I keep {kept.Piece.Name} in my backpack (instead of {kept.Worn?.Name ?? "nothing"}): wearing it would change my outfit by {kept.Gain:0.#} ({kept.Why}), and a swap " +
                  "has to gain more than 0.5 to be worth it.", "kept|" + level + "|" + kept.Piece.Id);
        }
    }

    // Full health first: a tough creature, a plan he trusts little (Confidence), or a fight while he is careful (UltimusMood).
    private bool ToughFoeNow(UltimusPlanner.Target target) =>
        !CombatDuelComponent.IsInBattle(_team[0].GlobalId)
        && (_carefulPlan || _riskPrepare && target?.Kind == UltimusPlanner.FIGHT || target?.Kind == UltimusPlanner.FIGHT && (_toughFoeNext || UltimusMood.Now == UltimusMood.Level.Careful && UltimusMood.LostRecently));

}
