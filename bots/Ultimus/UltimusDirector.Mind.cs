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
using Akka.Actor;
using System.Collections.Generic;
using System.Linq;
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

    // What he went in with and why, for the clean fight record (UltimusFights).
    private (double Mana, int Level, string Quest, string Goal, string Step, string Source, string Zone, float X, float Y)? _fightStart;

    // Going in at full health against a creature that beat it or takes most of its health.
    private bool _toughFoeNext;

    private static double ManaShare(UltimusMember.Status member) =>
        member.Wizard?.GameStats is { } stats ? stats.m_currentMana / (double) Math.Max(1, stats.m_baseMana + stats.m_bonusMana) : 1;

    // One full, accurate line per fight (UltimusFights, logs/ultimus/fights.jsonl).
    private void SaveFight(UltimusMember.Status me, uint template, string name, bool won, double healthBefore, double lostShare, double seconds,
                           UltimusFights.Battle battle, bool picked, (string Kind, string Style) style = default) {
        var start = _fightStart;
        _fightStart = null;
        UltimusFights.Save(new Dictionary<string, object> {
            ["at"] = DateTime.UtcNow.ToString("o"),
            ["won"] = won,
            ["seconds"] = Math.Round(seconds),
            ["rounds"] = battle?.Round ?? 0,
            ["zone"] = start?.Zone ?? me.Zone ?? "",
            ["world"] = WorldOf(start?.Zone ?? me.Zone),
            ["x"] = Math.Round(start?.X ?? me.X),
            ["y"] = Math.Round(start?.Y ?? me.Y),
            ["level"] = start?.Level ?? me.Wizard?.MagicSchoolBehavior?.Level ?? 0,
            ["foeTemplate"] = template,
            ["foe"] = name,
            ["picked"] = picked,
            ["quest"] = start?.Quest ?? "",
            ["goal"] = start?.Goal ?? "",
            ["step"] = start?.Step ?? "",
            ["rule"] = start?.Source ?? "",
            ["healthBefore"] = Math.Round(healthBefore, 3),
            ["healthAfter"] = Math.Round(HealthShare(me), 3),
            ["healthLost"] = Math.Round(lostShare, 3),
            ["manaBefore"] = Math.Round(start?.Mana ?? 0, 3),
            ["manaAfter"] = Math.Round(ManaShare(me), 3),
            ["playersWithMe"] = battle?.Humans ?? 0,
            ["botsWithMe"] = battle?.Bots ?? 0,
            ["enemies"] = battle is null ? null : battle.Enemies.Select(e => new Dictionary<string, object> {
                ["template"] = e.Template, ["name"] = e.Name, ["maxHealth"] = e.MaxHealth, ["healthLeft"] = e.Health, ["minion"] = e.Minion,
            }).ToList(),
            ["enemiesKnown"] = battle is not null,
            ["fightKind"] = style.Kind ?? "",
            ["style"] = style.Style ?? "",
        });
    }

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

    private (string Quest, string Goal, int Count)? _dropBefore;

    private void FightStarted(UltimusMember.Status me) {
        _fight = _target is { Kind: UltimusPlanner.FIGHT } fight
            ? ((uint) Math.Min(fight.Template, uint.MaxValue), FoeName(fight), HealthShare(me), DateTime.UtcNow)
            : (0, "", HealthShare(me), DateTime.UtcNow);
        _fightStart = (ManaShare(me), me.Wizard?.MagicSchoolBehavior?.Level ?? 0, _target?.Quest ?? "", _target?.Goal ?? "",
            _target?.What ?? "", _pickSource, me.Zone ?? "", me.X, me.Y);
        // The step's count before the fight, to learn whether this creature counts for it (UltimusDrops).
        _dropBefore = _target is { Kind: UltimusPlanner.FIGHT } t && !string.IsNullOrEmpty(t.Quest)
            ? (t.Quest, t.Goal, UltimusDrops.StepCount(me.Wizard, t.Quest, t.Goal))
            : null;
        if (_fight.Value.Template == 0) {
            Think("A battle started that was not my step. I walk round creatures I don't need to fight; this one still caught me.",
                "ambush|" + me.Zone);
        }
    }

    private void FightEnded(UltimusMember.Status me, bool won) {
        if (_fight is not { } fight) {
            UltimusFightStyles.End(me.GlobalId, won, 0, 0, count: false);
            return;
        }

        _fight = null;
        // Zoom one-shots with dev spells: his fights are no player's, so none of them goes into what the bots learn of
        // creatures, styles or card mixes. Only which creature counted for a step is kept (that is the game's, not his).
        if (_seat.IsScout) {
            UltimusFights.Take(me.GlobalId);
            UltimusFightStyles.End(me.GlobalId, won, 0, 0, count: false);
            if (won && _dropBefore is { } zoomBefore && fight.Template != 0) {
                var counted = UltimusDrops.StepCount(me.Wizard, zoomBefore.Quest, zoomBefore.Goal) > zoomBefore.Count;
                UltimusDrops.Fought(zoomBefore.Quest, zoomBefore.Goal, fight.Template, fight.Name, me.Zone, counted);
            }

            _dropBefore = null;
            return;
        }

        var lostShare = Math.Max(0, fight.Health - (won ? HealthShare(me) : 0));
        var seconds = (DateTime.UtcNow - fight.Since).TotalSeconds;
        // What really stood there, read from the battle: a fight he did not pick gets its real foe (it was not written at
        // all), and a picked one its real name (Niduus, 2026-11-15: only accurate data).
        var battle = UltimusFights.Take(me.GlobalId);
        var style = UltimusFightStyles.End(me.GlobalId, won, battle?.Round ?? 0, lostShare);
        var picked = fight.Template != 0;
        if (fight.Template == 0 && UltimusFights.Main(battle) is { } main) {
            fight = (main.Template, main.Name, fight.Health, fight.Since);
        }
        else if (fight.Template != 0 && UltimusFights.RealName(fight.Template) is { Length: > 0 } real) {
            fight = (fight.Template, real, fight.Health, fight.Since);
        }

        SaveFight(me, fight.Template, fight.Name, won, fight.Health, lostShare, seconds, battle, picked, style);
        // The good and the bad of it, for his own awareness (UltimusEffort).
        UltimusEffort.Note(won ? UltimusEffort.Kind.FightWon : UltimusEffort.Kind.FightLost,
            $"{(won ? "beat" : "lost to")} {(fight.Name.Length > 0 ? fight.Name : "a creature")} in {battle?.Round ?? 0} round(s), {lostShare:P0} health lost", seconds);
        NoteDayFight(fight.Name.Length > 0 ? fight.Name : "a creature", won, lostShare);
        if (fight.Template == 0) {
            return;
        }

        // A fight he did not pick (it caught him on the way): what the creature did is still written down, under its real
        // name, but it is no step's fight, so no step lessons come from it.
        if (!picked) {
            UltimusFoes.Fought(fight.Template, fight.Name, won, lostShare, seconds);
            return;
        }

        CheckFight(me, won, lostShare, fight.Name);
        // His card mix learns from it: more hits in his main deck when he ran out of them, more support treasure cards when a
        // big hit or a close call says so (UltimusDeckMix; Niduus, 2026-11-21: "Have him learn, that is the whole point of him").
        if (UltimusDeckMix.Learn(fight.Name.Length > 0 ? fight.Name : "a creature", won, battle?.Round ?? 0, lostShare, seconds,
                BotKnowledge.Of(fight.Template)?.BiggestHitShare ?? 0) is { } mixMoved) {
            Think($"What that fight taught me about my cards: {mixMoved}", "mix|" + fight.Template + "|" + mixMoved);
        }

        if (won && _dropBefore is { } before) {
            var after = UltimusDrops.StepCount(me.Wizard, before.Quest, before.Goal);
            var counted = after > before.Count;
            UltimusDrops.Fought(before.Quest, before.Goal, fight.Template, fight.Name, me.Zone, counted);
            if (!counted && UltimusDrops.Score(before.Quest, before.Goal, fight.Template) < 0) {
                Think($"I have beaten {fight.Name} 3 times for \"{before.Goal}\" and it never counted. I try other creatures for it first.",
                    "nodrop|" + before.Quest + "|" + fight.Template);
            }
        }

        _dropBefore = null;
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
                Announce(SAY_MOMENT, lostBefore > 0 ? $"Finally beat {foe.Name}!" : $"Phew, beat {foe.Name}. That was close!");
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
        BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.LOST_FIGHT, me.Zone, me.X, me.Y, _target?.What ?? "",
            $"Lost to {foe.Name} alone at level {level} after {seconds / 60:0.#} minutes (went in at {fight.Health:P0} health).", reasoning,
            "Go in at full health and mana with a deck made for it; if it still wins, wait for a player or level up first.", foe: foe.Name);
        Think($"I lost to {foe.Name}. I went in at {fight.Health:P0} health. Next time I heal up to full first and make my deck for it.",
            "lost|" + fight.Template);
        // Why, and a different plan for the next try (UltimusDirector.Life.cs).
        LearnFromLoss(me, fight.Template, foe.Name, fight.Health, seconds);
    }

    private static string FoeName(UltimusPlanner.Target target) {
        // The creature's real name from the game data; the step's words ("1 creatures", "1 Golden Key", "a fight for XP
        // toward level 14") only when the data has none.
        if (target.Template != 0 && UltimusFights.RealName(target.Template) is { Length: > 0 } real) {
            return real;
        }

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
        BotMistakes.Record(UltimusSeat.Current.Key, BotMistakes.STUCK, member.Zone, member.X, member.Y, step,
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

        // A creature it does not need to fight, or a creature path, by the line: the route map walks round it instead.
        if (creatures?.Exists(c => NearLine(c.X, c.Y, member.X, member.Y, toX, toY) < SIGHT_CLEAR) == true) {
            return null;
        }

        if (UltimusSight.Clear(member.Zone, member.X, member.Y, toX, toY)) {
            Think($"I can see \"{what}\" {far:0} away, with nothing in the way: I walk straight to it.", "sight|" + what);

            return [(toX, toY)];
        }

        if (UltimusSight.AroundTo(member.Zone, member.X, member.Y, toX, toY) is { } around && !NearCreatures(member.X, member.Y, around, creatures)) {
            Think($"\"{what}\" is {far:0} away in my circle of sight, but something is in the way: I go round it.", "around|" + what);

            return around;
        }

        return null;
    }

    // How near a creature, or a creature path's spot, a shortcut by sight may pass (a creature starts a battle within
    // about 175, and patrols walk a little off their path).
    private const float SIGHT_CLEAR = 220f;

    // Whether a walk passes near any of the creatures or creature path spots.
    private static bool NearCreatures(float fromX, float fromY, List<(float X, float Y)> points, List<(float X, float Y)> creatures) {
        if (creatures is not { Count: > 0 } || points is not { Count: > 0 }) {
            return false;
        }

        var (ax, ay) = (fromX, fromY);
        foreach (var (bx, by) in points) {
            if (creatures.Exists(c => NearLine(c.X, c.Y, ax, ay, bx, by) < SIGHT_CLEAR)) {
                return true;
            }

            (ax, ay) = (bx, by);
        }

        return false;
    }

    // ---- Timing a pass between patrols ----

    // How far ahead on the walk a creature crossing it is waited for, how near the walk counts, and the longest wait.
    private const float GAP_AHEAD = 700f;
    private const float GAP_NEAR = 200f;
    private static readonly TimeSpan s_gapMost = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_gapRest = TimeSpan.FromSeconds(25);
    private (string Key, DateTime Since, DateTime Done)? _gapWait;

    // While he waits: a crossing creature this near is backed away from, not stood in front of. Standing still on its way he
    // was caught 3 to 6 seconds into the wait, four times on 2026-11-21 (a creature walking his way walks into him).
    private const float GAP_HOLD = 450f;
    private DateTime _gapBackAt;

    private void HoldForGap(UltimusMember.Status member, List<(float X, float Y)> crossing) {
        var nearest = crossing.Select(c => (c.X, c.Y, Far: UltimusPlanner.Far(member.X, member.Y, c.X, c.Y))).OrderBy(c => c.Far).FirstOrDefault();
        if (crossing.Count == 0 || nearest.Far >= GAP_HOLD || nearest.Far < 1) {
            StopWalking(0);
            return;
        }

        if (DateTime.UtcNow - _gapBackAt < TimeSpan.FromSeconds(1.2)) {
            return;
        }

        _gapBackAt = DateTime.UtcNow;
        var away = (X: member.X + ((member.X - nearest.X) / nearest.Far * MOB_STEP_BACK), Y: member.Y + ((member.Y - nearest.Y) / nearest.Far * MOB_STEP_BACK));
        var back = Imlight.CoreLib.Game.Tools.OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, away.X, away.Y, body: true, avoid: crossing);
        _sent.Remove(0);
        _actors[0].Tell(new UltimusMember.WalkTo(back, "backs away from a creature crossing its way while it waits"));
        Think($"The creature on my way is {nearest.Far:0} away and could walk into me: I back away while I wait, as a player keeps out of a patrol's reach.",
            "gap-back|" + (int) (nearest.X / 300) + "|" + (int) (nearest.Y / 300));
    }

    /// <summary>
    /// A creature he does not need is on the walk just ahead (the way finder found no way round it): true while he waits
    /// for it to walk on, at most a few seconds per step, then he goes (Niduus, 2026-11-15: "be more aware of his
    /// surroundings ... short cutting streets by walking through mob paths").
    /// </summary>
    private bool WaitForGap(UltimusMember.Status member, UltimusPlanner.Target target, List<(float X, float Y)> points,
        List<(float X, float Y)> creatures) {
        // On the way to a fight too (Niduus, 2026-11-19): the creatures in the list are the ones his quests don't need.
        if (creatures is not { Count: > 0 } || points is not { Count: > 0 }) {
            _gapWait = null;
            return false;
        }

        var key = target.Key + "|" + target.What;
        if (_gapWait is { } done && done.Key == key && DateTime.UtcNow < done.Done) {
            return false;
        }

        // The walk's first stretch only: what is far ahead will have walked on by the time he gets there.
        var ahead = new List<(float X, float Y)>();
        var (ax, ay) = (member.X, member.Y);
        var gone = 0f;
        foreach (var p in points) {
            var leg = UltimusPlanner.Far(ax, ay, p.X, p.Y);
            if (gone + leg > GAP_AHEAD && leg > 1) {
                var t = (GAP_AHEAD - gone) / leg;
                ahead.Add((ax + ((p.X - ax) * t), ay + ((p.Y - ay) * t)));
                break;
            }

            ahead.Add(p);
            gone += leg;
            (ax, ay) = p;
        }

        // Creatures already right by him are handled by stepping back; the ones by the step itself are not waited for.
        var crossing = creatures.Where(c => UltimusPlanner.Far(member.X, member.Y, c.X, c.Y) > MOB_TOO_CLOSE
                                            && UltimusPlanner.Far(target.X, target.Y, c.X, c.Y) > 250).ToList();
        if (!NearCreatures(member.X, member.Y, ahead, crossing)) {
            if (_gapWait is { } was && was.Key == key && was.Done == DateTime.MaxValue) {
                Think("The creature walked on: my way is clear, so I go.", "gap-clear|" + key);
            }

            _gapWait = null;
            return false;
        }

        if (_gapWait is not { } wait || wait.Key != key) {
            _gapWait = (key, DateTime.UtcNow, DateTime.MaxValue);
            Think($"A creature I don't need is walking across my way to \"{target.What}\" just ahead, and there is no way round it: " +
                  "I wait for it to walk on, out of its reach, instead of walking into its battle.", "gap|" + key);
            HoldForGap(member, crossing);

            return true;
        }

        if (DateTime.UtcNow - wait.Since < s_gapMost) {
            HoldForGap(member, crossing);
            return true;
        }

        // Waited long enough (it stands there, or walks back and forth on the way): he goes, and does not wait again for
        // this step for a short while. It was 2 minutes, and on a long walk (the Tower Archives, 2026-11-21) every creature
        // after the first then went unwatched: 16 battles he did not need in 3 hours.
        _gapWait = (key, wait.Since, DateTime.UtcNow + s_gapRest);
        Think("It is still in my way after a few seconds: I go on and take the battle if it comes.", "gap-go|" + key);

        return false;
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
        // Only a fight asks for it: a shop, a door or a talk is no fight, so the careful way never sends him to heal for
        // one (his log, 2026-11-15: "This one's tough, healing up first" at full health, on a trip to a jeweler).
        && target?.Kind == UltimusPlanner.FIGHT
        && (_carefulPlan || _riskPrepare || _toughFoeNext || UltimusMood.Now == UltimusMood.Level.Careful && UltimusMood.LostRecently);

}
