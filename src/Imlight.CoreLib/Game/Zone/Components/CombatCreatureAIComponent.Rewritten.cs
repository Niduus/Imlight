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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Role = Imlight.CoreLib.Game.Combat.RewrittenAI.Role;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The Rewritten AI's choices (RewrittenAI.cs has the settings, the battle's memory and how a card is read). With the switch
/// on, every round a creature:
///   1. scores every enemy by threat (stage 1) and picks a target, staying on it unless another is clearly ahead;
///   2. goes through what makes sense, in order (stage 2): finish someone, survive (heal, or a shield of a school an
///      enemy really has), answer a big blade stack (weakness, charm removal, stun), set up (a blade or trap matching the
///      hit it plans, with the pips to hit after), hit (the best hit for the matchup, around shields), or hold back;
///   3. does it as a team (stage 3): the creatures of a side decide one after another and see each other's plans, so
///      setup already on a target counts for all, nobody hits a wizard a teammate is already finishing, blades go to a
///      teammate who hits with that school, and a feed (several wizards blading one) is read and answered.
/// Intellect decides how often it thinks this through (else it picks loosely among the sensible moves); Aggression how
/// soon it stops setting up. Anything that goes wrong falls back to the old AI for that round.
/// </summary>
internal sealed partial class CombatCreatureAIComponent {

    // Taunt and Pacify, in threat points, kept for the rewritten AI (the old hate table keeps its own).
    private readonly Dictionary<int, float> _tauntPoints = [];
    private int _rewrittenTarget = -1;
    private int _setupStreak;
    private bool _heldBackLastRound;
    // The cards this creature has shown, to guess its role from its deck.
    private readonly Dictionary<uint, Card> _seenCards = [];

    private void NoteTaunt(int slot, float points) => _tauntPoints[slot] = _tauntPoints.GetValueOrDefault(slot) + points;

    private sealed class Turn {
        internal CombatDuelSubCircle Me;
        internal RewrittenAI.Tuning Tuning;
        internal RewrittenAI.Ledger Ledger;
        internal int Round;
        internal Role Role;
        internal string MySchool = "";
        internal int Pips;
        internal bool BossFight;
        internal List<CombatDuelSubCircle> Enemies = [];
        internal List<CombatDuelSubCircle> Friends = [];
        internal List<Card> Hand = [];
        internal List<Card> Castable = [];
        internal Dictionary<int, float> Threat = [];
        internal Dictionary<int, string> ThreatWhy = [];
        internal CombatDuelSubCircle Target;
        internal string TargetWhy = "";
        internal List<RewrittenAI.PlannedMove> Plan = [];
        internal List<int> Fed = [];
        internal CombatDuelSubCircle Leader;
    }

    private sealed record Choice(Card Card, CombatDuelSubCircle Target, string Kind, string Reason, int Damage = 0, bool Setup = false,
                                 bool HitsAll = false);

    /// <summary>The rewritten AI's move this round, or null to let the old AI decide.</summary>
    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE TryRewrittenTurn() {
        if (_currentSubCircle?.CombatParticipant is null || _currentDuelComponent is null || _roundHand?.m_spellList is null) {
            return null;
        }

        BossOverrides.BossOverride own = null;
        try {
            own = BossOverrides.For(Customs.KeyOf(Entity.ActiveGameObject));
        }
        catch (Exception) {
            // No changes for it.
        }

        if (!RewrittenAI.IsOnFor(own, _currentSubCircle.OccupiedTeam == CombatTeam.Player)) {
            return null;
        }

        // Stunned: the old AI passes and counts the stun down.
        if (_currentSubCircle.CombatParticipant.m_stunned > 0) {
            return null;
        }

        try {
            return RewrittenTurn(own);
        }
        catch (Exception e) {
            Logger.Warning("Rewritten AI | Duel {0} | Slot {1} | failed, the old AI decides this round: {2}",
                Logger.Args(_currentDuelComponent.SigilId, _currentSubCircle.SlotIndex, e));

            return null;
        }
    }

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE RewrittenTurn(BossOverrides.BossOverride own) {
        var ledger = RewrittenAI.For(_currentDuelComponent);
        var round = _currentDuelComponent.Duel?.m_roundNum ?? 0;

        // The creatures of a side decide one after another, each seeing what the ones before it planned.
        lock (ledger.Lock) {
            ledger.BeginRound(round);
            var turn = Look(own, ledger, round);
            ScoreThreat(turn);
            DecideGangUp(turn);
            PickTarget(turn);

            var options = Options(turn);
            Choice choice;
            if (options.Count == 0) {
                choice = new Choice(null, null, "pass", "nothing in its hand makes sense right now");
            }
            else if (_determinedSmartThisTurn || options.Count == 1) {
                choice = options[0];
            }
            else {
                // Not thinking it through this round (Intellect): any of the sensible moves near the top.
                choice = options[_random.Next(Math.Min(3, options.Count))];
            }

            return Commit(turn, choice);
        }
    }

    // ---------------------------------------------------------------- looking at the fight

    private Turn Look(BossOverrides.BossOverride own, RewrittenAI.Ledger ledger, int round) {
        var me = _currentSubCircle;
        var turn = new Turn {
            Me = me,
            Tuning = RewrittenAI.TuningFor(own),
            Ledger = ledger,
            Round = round,
            MySchool = RewrittenAI.SchoolOf(me),
            Pips = RewrittenAI.PipsOf(me),
        };
        var circles = _currentDuelComponent.ActiveSubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true }).ToList();
        turn.Enemies = [.. circles.Where(c => c.OccupiedTeam != me.OccupiedTeam)];
        turn.Friends = [.. circles.Where(c => c.OccupiedTeam == me.OccupiedTeam)];
        if (!turn.Friends.Contains(me)) {
            turn.Friends.Add(me);
        }

        turn.BossFight = circles.Any(c => c.OccupiedTeam == CombatTeam.Monster && RewrittenAI.IsBoss(c));
        turn.Plan = ledger.PlanOf(me.OccupiedTeam, round);
        turn.Fed = ledger.Fed(round).Where(slot => turn.Enemies.Any(e => e.SlotIndex == slot)).ToList();

        foreach (var spell in _roundHand.m_spellList) {
            if (RewrittenAI.Read(spell) is not { } card) {
                continue;
            }

            turn.Hand.Add(card);
            _seenCards.TryAdd(card.Id, card);
            if (turn.Tuning.NeverCast.Contains(card.Name) || turn.Tuning.NeverCast.Contains(card.Shown)) {
                continue;
            }

            // Max Same Spell In A Row still counts; the old "avoid repeat" roll is replaced by the reasoning.
            if (_mustSkipRepeatThisTurn && card.Id == _lastCastSpellId) {
                continue;
            }

            if (me.HasPipsForSpell(spell)) {
                turn.Castable.Add(card);
            }
        }

        turn.Role = RoleOf(me, own, turn);
        turn.Leader = turn.Friends.FirstOrDefault(f => f != me && RoleOf(f, FriendOverride(f), turn) == Role.Leader);

        return turn;
    }

    private static BossOverrides.BossOverride FriendOverride(CombatDuelSubCircle circle) {
        try {
            return circle?.ParticipantObject is { } obj && !circle.IsHumanPlayer ? BossOverrides.For(Customs.KeyOf(obj)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Its role: the Bosses tab's, else a guess. A creature's own minion helps it (Support); the one boss among ordinary
    /// creatures leads; a deck full of heals heals, one full of shields protects; the rest are Free.
    /// </summary>
    private Role RoleOf(CombatDuelSubCircle circle, BossOverrides.BossOverride own, Turn turn) {
        if (Enum.TryParse<Role>(own?.AIRole ?? "", true, out var set) && set != Role.Automatic) {
            return set;
        }

        if (circle.IsHumanPlayer) {
            return Role.Free;
        }

        if (circle.IsSummonedMinion && circle.OccupiedTeam == CombatTeam.Monster) {
            return Role.Support;
        }

        var side = turn.Friends.Where(f => !f.IsHumanPlayer).ToList();
        var bosses = side.Where(RewrittenAI.IsBoss).ToList();
        if (bosses.Count == 1 && bosses[0] == circle && side.Count > 1) {
            return Role.Leader;
        }

        if (circle != _currentSubCircle || _seenCards.Count < 3) {
            return Role.Free;
        }

        var cards = _seenCards.Values.ToList();
        if (cards.Count(c => c.Heal && !c.Hit) * 10 >= cards.Count * 3) {
            return Role.Healer;
        }

        if (cards.Count(c => c.Shield) * 10 >= cards.Count * 3) {
            return Role.Protector;
        }

        return Role.Free;
    }

    // ---------------------------------------------------------------- stage 1: threat

    private void ScoreThreat(Turn turn) {
        var me = turn.Me;
        var t = turn.Tuning;
        var across = (me.SlotIndex + 4) % _currentDuelComponent.SubCircles.Length;
        foreach (var enemy in turn.Enemies) {
            var slot = enemy.SlotIndex;
            var school = RewrittenAI.SchoolOf(enemy);
            // Danger now: their blades (their own school's and all-school ones count fully), pips, and traps they laid on me.
            var blades = RewrittenAI.BladesOn(enemy, school.Length > 0 ? school : null);
            var otherBlades = RewrittenAI.BladesOn(enemy) - blades;
            var pips = enemy.CombatParticipant?.m_pipCount;
            var pipPoints = pips is null ? 0 : (pips.m_genericPips * 6f) + (pips.m_powerPips * 12f);
            var traps = turn.Ledger.Get(turn.Ledger.TrapsLaid, me.SlotIndex, slot) * 0.5f;
            var danger = (blades + (otherBlades * 0.5f) + pipPoints + traps) * t.Danger;

            var damage = turn.Ledger.Get(turn.Ledger.DamageTo, me.SlotIndex, slot) * t.Damage;
            // What they did to the rest of my side counts a little too.
            damage += turn.Friends.Where(f => f != me).Sum(f => turn.Ledger.Get(turn.Ledger.DamageTo, f.SlotIndex, slot)) * 0.25f * t.Damage;
            var spent = turn.Ledger.SpentBlades.GetValueOrDefault(slot) * t.Danger;
            var healing = turn.Ledger.Healing.GetValueOrDefault(slot) * 0.5f * t.Healing;
            var low = (1f - RewrittenAI.HealthShare(enemy)) * 40f * t.LowHealth;
            var matchup = t.Excludes(RewrittenAI.NO_MATCHUP) ? 0 : Matchup(turn.MySchool, enemy, school) * t.Matchup;
            var taunt = _tauntPoints.GetValueOrDefault(slot);
            var fed = turn.Fed.Contains(slot) ? 60f : 0f;
            var score = danger + damage + spent + healing + low + matchup + taunt + fed + (slot == across ? 1f : 0f);
            turn.Threat[slot] = Math.Max(0.1f, score);

            var parts = new List<string>();
            void Part(string what, float value) {
                if (Math.Abs(value) >= 1f) {
                    parts.Add($"{what} {value:+0;-0}");
                }
            }

            Part("blades+pips", danger);
            Part("damage", damage);
            Part("spent blades", spent);
            Part("healing", healing);
            Part("low health", low);
            Part("matchup", matchup);
            Part("taunt", taunt);
            Part("fed", fed);
            turn.ThreatWhy[slot] = parts.Count == 0 ? "nothing yet" : string.Join(", ", parts);
        }
    }

    // A fire creature always wants the ice wizard, and takes on a fire wizard only reluctantly; their gear's resist (or
    // boost) against its school counts too.
    private static float Matchup(string mySchool, CombatDuelSubCircle enemy, string enemySchool) {
        if (string.IsNullOrEmpty(mySchool)) {
            return 0;
        }

        var score = 0f;
        if (RewrittenAI.Same(RewrittenAI.Opposite(mySchool), enemySchool)) {
            score += 30;
        }
        else if (RewrittenAI.Same(mySchool, enemySchool)) {
            score -= 30;
        }

        score -= RewrittenAI.ResistOf(enemy, mySchool) * 60f;

        return score;
    }

    /// <summary>
    /// Whether this side gangs up on one wizard: decided once, from round 2, when the wizards' first moves are known. A
    /// clear standout (one wizard far ahead on threat) or a feed makes it likelier.
    /// </summary>
    private void DecideGangUp(Turn turn) {
        var side = turn.Me.OccupiedTeam;
        if (turn.Ledger.GangUp.ContainsKey(side) || turn.Round < 2 || turn.Enemies.Count < 2) {
            return;
        }

        var chance = RewrittenAI.GangUpChance;
        var ordered = turn.Threat.Values.OrderByDescending(v => v).ToList();
        if (ordered.Count >= 2 && ordered[0] >= ordered[1] * 1.5f) {
            chance += 0.25f;
        }

        if (turn.Fed.Count > 0) {
            chance += 0.25f;
        }

        var gang = _random.NextDouble() < Math.Clamp(chance, 0f, 1f);
        turn.Ledger.GangUp[side] = gang;
        Say(turn, $"the side decides to {(gang ? "gang up on one wizard" : "each follow its own threat")} ({RewrittenAI.Percent(chance)}% chance)");
    }

    private void PickTarget(Turn turn) {
        if (turn.Enemies.Count == 0) {
            return;
        }

        CombatDuelSubCircle Of(int slot) => turn.Enemies.FirstOrDefault(e => e.SlotIndex == slot);
        var best = turn.Enemies.OrderByDescending(e => turn.Threat.GetValueOrDefault(e.SlotIndex)).First();
        var why = "most threat";

        // A feed: take the fed wizard out before they can hit.
        if (turn.Fed.Select(Of).Where(e => e is not null).OrderByDescending(e => RewrittenAI.BladesOn(e)).FirstOrDefault() is { } fed) {
            best = fed;
            why = "several wizards bladed them (a feed)";
        }
        else if (turn.Ledger.GangUp.GetValueOrDefault(turn.Me.OccupiedTeam) && !turn.Tuning.Excludes(RewrittenAI.NO_GANG_UP)
                 && turn.Plan.FirstOrDefault(m => m.Target >= 0 && Of(m.Target) is not null && m.Kind is "attack" or "finish" or "trap")
                     is { } lead) {
            best = Of(lead.Target);
            why = "ganging up with its side";
        }
        else if (!_determinedSmartThisTurn) {
            // Not thinking it through: a weighted pick, likelier the more threat.
            var total = turn.Enemies.Sum(e => turn.Threat.GetValueOrDefault(e.SlotIndex));
            var roll = _random.NextDouble() * total;
            foreach (var e in turn.Enemies) {
                roll -= turn.Threat.GetValueOrDefault(e.SlotIndex);
                if (roll <= 0) {
                    best = e;
                    why = "a loose pick by threat";
                    break;
                }
            }
        }

        // Stubborn: it stays on its target unless the new one is clearly ahead.
        if (why == "most threat" && Of(_rewrittenTarget) is { } current && current != best
            && turn.Threat.GetValueOrDefault(current.SlotIndex) * (1f + turn.Tuning.Stubborn) >= turn.Threat.GetValueOrDefault(best.SlotIndex)) {
            best = current;
            why = "stays on its target (nobody is clearly ahead)";
        }

        turn.Target = best;
        turn.TargetWhy = why;
    }

    // ---------------------------------------------------------------- stage 2 and 3: what makes sense, in order

    private List<Choice> Options(Turn turn) {
        var options = new List<Choice>();
        if (turn.Target is null) {
            return options;
        }

        var lastResort = new List<Choice>();
        Finish(turn, options);
        Survive(turn, options);
        AnswerDanger(turn, options);
        SetUp(turn, options, lastResort);
        Hit(turn, options);

        // A hit it could not size up (no damage number it can read) is still better than passing.
        if (!options.Any(o => o.Card?.Hit == true) && turn.Castable.Where(c => c.Hit).OrderByDescending(c => c.Rank).FirstOrDefault() is { } guess) {
            options.Add(new Choice(guess, turn.Target, "attack", $"its biggest hit on {Name(turn.Target)}"));
        }

        options.AddRange(lastResort);

        // Cards it has no special rule for (a summon, a bubble, a pip card): fine when nothing else is left to do. Blades,
        // traps, shields and heals that make no sense are never cast: it passes instead.
        foreach (var card in turn.Castable.Where(c => c.Kind == "other")) {
            options.Add(new Choice(card, card.Spell is not null && SpellEffectFilter.TargetsEnemy(card.Spell) ? turn.Target : turn.Me,
                "other", "nothing else makes sense, and this card has no rule against it"));
        }

        return options;
    }

    private static int PlannedOn(Turn turn, CombatDuelSubCircle target)
        => turn.Plan.Where(m => m.Target == target.SlotIndex).Sum(m => m.Damage);

    private static bool Planned(Turn turn, string kind, CombatDuelSubCircle target)
        => turn.Plan.Any(m => m.Kind == kind && m.Target == target.SlotIndex);

    // 1. Finish someone: a hit that knocks a wizard out, with what teammates already plan on them. A wizard a teammate is
    //    already finishing is left alone (no wasted kills).
    private void Finish(Turn turn, List<Choice> options) {
        Choice best = null;
        var bestScore = float.MinValue;
        foreach (var card in turn.Castable.Where(c => c.Hit)) {
            foreach (var enemy in turn.Enemies) {
                var planned = PlannedOn(turn, enemy);
                var health = RewrittenAI.Health(enemy);
                if (planned >= health) {
                    continue;
                }

                var damage = RewrittenAI.Estimate(card, turn.Me, enemy);
                if (damage + planned < health) {
                    continue;
                }

                var score = (turn.Threat.GetValueOrDefault(enemy.SlotIndex) * 10f) - card.Rank;
                if (score > bestScore) {
                    bestScore = score;
                    best = new Choice(card, enemy, "finish", planned > 0
                        ? $"with its side's planned hits ({planned}) this knocks {Name(enemy)} out"
                        : $"it can knock {Name(enemy)} out ({damage} vs {health} health)", damage, HitsAll: card.HitsAll);
                }
            }
        }

        if (best is not null) {
            options.Add(best);
        }
    }

    // 2. Survive: heal when low (itself, or a teammate when it heals for the team), and shield against a wizard who is
    //    ready to hit hard, only with a shield of a school that wizard has (a team of ice and storm never sees a myth shield).
    private void Survive(Turn turn, List<Choice> options) {
        var me = turn.Me;
        var t = turn.Tuning;
        var myShare = RewrittenAI.HealthShare(me);
        var ready = turn.Enemies.Where(e => turn.Fed.Contains(e.SlotIndex) || (RewrittenAI.BladesOn(e) >= 50 && RewrittenAI.PipsOf(e) >= 4))
            .OrderByDescending(e => RewrittenAI.BladesOn(e)).ToList();
        var feedAlert = turn.Fed.Count > 0;

        var heals = turn.Castable.Where(c => c.Heal && !c.Hit).ToList();
        if (heals.Count > 0) {
            if (myShare < 0.4f && !Planned(turn, "heal", me)) {
                var card = heals.OrderByDescending(c => c.HealAmount).First();
                options.Add(new Choice(card, me, "heal", $"its own health is low ({RewrittenAI.Percent(myShare)}%)"));
            }

            if (!t.Excludes(RewrittenAI.NO_TEAM_HEALS)) {
                var line = turn.Role == Role.Healer ? (feedAlert ? 0.85f : 0.65f) : 0.35f;
                var hurt = turn.Friends.Where(f => f != me && RewrittenAI.HealthShare(f) < line && !Planned(turn, "heal", f))
                    .OrderBy(RewrittenAI.HealthShare).FirstOrDefault();
                var card = heals.Where(c => c.HealForTeammate || c.HealWholeTeam).OrderByDescending(c => c.HealAmount).FirstOrDefault();
                if (hurt is not null && card is not null) {
                    options.Add(new Choice(card, hurt, "heal", feedAlert && turn.Role == Role.Healer
                        ? $"a feed is coming, so it heals {Name(hurt)} ahead"
                        : $"{Name(hurt)} is low ({RewrittenAI.Percent(RewrittenAI.HealthShare(hurt))}%)"));
                }
            }
        }

        if (ready.Count == 0 && myShare >= 0.5f) {
            return;
        }

        var danger = ready.FirstOrDefault() ?? turn.Target;
        var dangerSchool = RewrittenAI.SchoolOf(danger);
        var enemySchools = turn.Enemies.Select(RewrittenAI.SchoolOf).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shields = turn.Castable.Where(c => c.Shield && ShieldMakesSense(c, enemySchools)).ToList();
        if (shields.Count == 0) {
            return;
        }

        // Whom to shield: a Protector shields the teammate most likely hit (its side's leader, else the lowest), others themselves.
        var who = me;
        if (turn.Role == Role.Protector && !t.Excludes(RewrittenAI.NO_TEAM_SHIELDS)) {
            who = turn.Leader ?? turn.Friends.OrderBy(RewrittenAI.HealthShare).First();
        }

        // Against a wizard who is ready, only a shield of that wizard's school (or all schools) makes sense; when it is
        // just hurt, any shield of a school an enemy has.
        var fits = shields.Where(c => RewrittenAI.Fits(c.ShieldSchool, dangerSchool)).ToList();
        var usable = ready.Count > 0 ? fits : fits.Concat(shields).ToList();
        var card2 = usable.FirstOrDefault(c => who == me || c.ShieldForTeammate || c.ShieldWholeTeam);
        if (card2 is null && who != me) {
            who = me;
            card2 = usable.FirstOrDefault();
        }

        if (card2 is null || Planned(turn, "shield", who) || AlreadyShielded(who, string.IsNullOrEmpty(card2.ShieldSchool) ? dangerSchool : card2.ShieldSchool)) {
            return;
        }

        options.Add(new Choice(card2, who, "shield", ready.Count > 0
            ? $"{Name(danger)} has {RewrittenAI.BladesOn(danger):0}% blades and {RewrittenAI.PipsOf(danger)} pips ready ({dangerSchool})"
            : $"its health is down to {RewrittenAI.Percent(myShare)}%"));
    }

    private static bool ShieldMakesSense(Card card, HashSet<string> enemySchools)
        => string.IsNullOrEmpty(card.ShieldSchool) || card.ShieldSchool.Equals("All", StringComparison.OrdinalIgnoreCase)
           || enemySchools.Contains(card.ShieldSchool);

    private static bool AlreadyShielded(CombatDuelSubCircle who, string school)
        => RewrittenAI.WardsOn(who, school).Any(w => w.m_effectType == kSpellEffects.kAbsorbDamage || w.m_effectParam < 0);

    // 3. Answer the danger: a weakness, charm removal or (after a feed) a stun on the wizard with the big blades.
    private void AnswerDanger(Turn turn, List<Choice> options) {
        var stack = turn.Enemies.OrderByDescending(e => RewrittenAI.BladesOn(e)).FirstOrDefault();
        if (stack is null) {
            return;
        }

        var blades = RewrittenAI.BladesOn(stack);
        var fed = turn.Fed.Contains(stack.SlotIndex);
        if (blades < 40 && !fed) {
            return;
        }

        foreach (var card in turn.Castable.Where(c => (c.Weakness || c.RemovesCharms || (c.Stun && fed)) && !c.Hit)) {
            if (Planned(turn, card.Kind, stack)) {
                continue;
            }

            options.Add(new Choice(card, stack, card.Kind, $"{Name(stack)} carries {blades:0}% in blades{(fed ? " from a feed" : "")}"));

            return;
        }
    }

    // 4. Set up: a blade or trap of the school of the hit it plans, only while the setup in play is not yet enough and it
    //    keeps the pips to hit. Setup the side already put on the target counts for everyone; a creature adds more traps
    //    only when it has nothing better to do. Teammates feed their leader.
    private void SetUp(Turn turn, List<Choice> options, List<Choice> lastResort) {
        var t = turn.Tuning;
        if (t.Excludes(RewrittenAI.NO_SETUP)) {
            return;
        }

        var maxRounds = turn.Role == Role.Striker ? Math.Min(2, t.MaxSetupRounds) : t.MaxSetupRounds;
        if (_setupStreak >= maxRounds || (_determinedAggressiveThisTurn && _setupStreak >= 2)) {
            return;
        }

        var me = turn.Me;
        var target = turn.Target;
        // The hit it plans: its best hit in hand that it can pay for now or next round.
        var plannedHit = turn.Hand.Where(c => c.Hit && c.Rank <= turn.Pips + 1)
            .OrderByDescending(c => RewrittenAI.Estimate(c, me, target, ignoreWards: true)).FirstOrDefault();

        var selfBlades = new List<Choice>();
        var traps = new List<Choice>();
        var teamBlades = new List<Choice>();
        if (plannedHit is not null) {
            var school = plannedHit.DamageSchool;
            var now = RewrittenAI.Estimate(plannedHit, me, target) + PlannedOn(turn, target);
            var enough = now >= RewrittenAI.Health(target) || now >= RewrittenAI.BigHitShare * RewrittenAI.MostHealth(target);
            if (!enough) {
                bool PipsLeft(Card c) => turn.Pips - c.Rank + 1 >= plannedHit.Rank;
                foreach (var card in turn.Castable.Where(c => c.Blade && !c.BladeOnlyTeammate && RewrittenAI.Fits(c.BladeSchool, school)
                                                              && !RewrittenAI.HasEffectOfSpell(me, c.Id) && PipsLeft(c))) {
                    selfBlades.Add(new Choice(card, me, "blade", $"setting up its {school} hit ({plannedHit.Shown}) on {Name(target)}", Setup: true));
                }

                if (!t.Excludes(RewrittenAI.NO_TRAPS)) {
                    var trapsThere = RewrittenAI.WardsOn(target, school).Count(w => w.m_effectType == kSpellEffects.kModifyIncomingDamage && w.m_effectParam > 0)
                                     + turn.Plan.Count(m => m.Kind == "trap" && m.Target == target.SlotIndex);
                    foreach (var card in turn.Castable.Where(c => c.Trap && RewrittenAI.Fits(c.TrapSchool, school)
                                                                  && !RewrittenAI.HasEffectOfSpell(target, c.Id) && PipsLeft(c))) {
                        var choice = new Choice(card, target, "trap", trapsThere == 0
                            ? $"trapping {Name(target)} for its {school} hit"
                            : $"adding to the {trapsThere} trap(s) already on {Name(target)}", Setup: true);
                        // The side's traps are already there: more only when nothing else makes sense.
                        (trapsThere >= 2 ? lastResort : traps).Add(choice);
                    }
                }
            }
        }

        // Blades for a teammate: only one whose hits match that blade's school, with the pips to hit soon. A fire creature
        // never gives a myth teammate a fire blade. The leader is fed first.
        if (!t.Excludes(RewrittenAI.NO_TEAM_BLADES)) {
            foreach (var card in turn.Castable.Where(c => c.Blade && (c.BladeForTeammate || c.BladeWholeTeam))) {
                var mate = turn.Friends.Where(f => f != me && RewrittenAI.PipsOf(f) >= 2 && !RewrittenAI.HasEffectOfSpell(f, card.Id)
                                                   && HitsWith(turn, f).Any(s => RewrittenAI.Fits(card.BladeSchool, s)))
                    .OrderByDescending(f => f == turn.Leader)
                    .ThenByDescending(RewrittenAI.PipsOf)
                    .FirstOrDefault();
                if (mate is not null) {
                    teamBlades.Add(new Choice(card, mate, "blade", mate == turn.Leader
                        ? $"feeding its leader {Name(mate)} a {Blank(card.BladeSchool)} blade"
                        : $"{Name(mate)} hits with {Blank(card.BladeSchool)}", Setup: true));
                }
            }
        }

        // The order depends on its part in the team.
        var feedLeader = turn.Leader is not null && turn.Role != Role.Leader;
        switch (turn.Role) {
            case Role.Leader:
            case Role.Striker:
                options.AddRange(selfBlades);
                options.AddRange(traps);
                options.AddRange(teamBlades);
                break;
            case Role.Support:
                options.AddRange(teamBlades);
                options.AddRange(traps);
                options.AddRange(selfBlades);
                break;
            default:
                if (feedLeader || (!_determinedSelfishThisTurn && teamBlades.Count > 0 && selfBlades.Count == 0)) {
                    options.AddRange(teamBlades);
                    options.AddRange(selfBlades);
                }
                else {
                    options.AddRange(selfBlades);
                    options.AddRange(teamBlades);
                }

                options.AddRange(traps);
                break;
        }
    }

    /// <summary>
    /// The schools a teammate hits with, as far as this creature knows: what it planned this round, in a boss fight its
    /// hand ([RewrittenAI] BossesKnowHands), and its own school.
    /// </summary>
    private static IEnumerable<string> HitsWith(Turn turn, CombatDuelSubCircle mate) {
        var schools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in turn.Plan.Where(m => m.Slot == mate.SlotIndex && m.School.Length > 0)) {
            schools.Add(move.School);
        }

        if (turn.BossFight && RewrittenAI.BossesKnowHands && !mate.IsHumanPlayer) {
            try {
                foreach (var spell in mate.GetCurrentHand()?.m_spellList ?? []) {
                    if (RewrittenAI.Read(spell) is { Hit: true } card && card.DamageSchool.Length > 0) {
                        schools.Add(card.DamageSchool);
                    }
                }
            }
            catch (Exception) {
                // Its hand is not there yet.
            }
        }

        if (RewrittenAI.SchoolOf(mate) is { Length: > 0 } own) {
            schools.Add(own);
        }

        return schools;
    }

    // 5. Hit: the best hit for the matchup, its estimate counting shields, so it goes around a shield (another school, an
    //    all-enemies spell or another target) rather than into it. A smart creature may hold back for a much bigger hit.
    private void Hit(Turn turn, List<Choice> options) {
        var me = turn.Me;
        Choice best = null;
        var bestScore = 0f;
        var maxThreat = Math.Max(0.1f, turn.Threat.Values.DefaultIfEmpty(0.1f).Max());
        foreach (var card in turn.Castable.Where(c => c.Hit)) {
            if (card.HitsAll) {
                var total = turn.Enemies.Sum(e => Math.Min(RewrittenAI.Estimate(card, me, e),
                    Math.Max(0, RewrittenAI.Health(e) - PlannedOn(turn, e))));
                if (total > bestScore) {
                    bestScore = total;
                    best = new Choice(card, turn.Target, "attack", $"hits every enemy for about {total} in all", total, HitsAll: true);
                }

                continue;
            }

            foreach (var enemy in turn.Enemies) {
                var left = Math.Max(0, RewrittenAI.Health(enemy) - PlannedOn(turn, enemy));
                if (left == 0) {
                    continue;
                }

                var damage = Math.Min(RewrittenAI.Estimate(card, me, enemy), left);
                var weight = (enemy == turn.Target ? 1.3f : 1f) * (0.5f + (turn.Threat.GetValueOrDefault(enemy.SlotIndex) / maxThreat));
                if (damage * weight > bestScore) {
                    bestScore = damage * weight;
                    var shielded = RewrittenAI.Estimate(card, me, enemy, ignoreWards: true);
                    best = new Choice(card, enemy, "attack", enemy == turn.Target
                        ? $"its best hit on {Name(enemy)} (about {damage})"
                        : $"{Name(turn.Target)} is shielded against it, so it hits {Name(enemy)} instead (about {damage})", damage);
                    if (enemy == turn.Target && shielded > damage * 2) {
                        best = best with { Reason = $"{best.Reason}, through shields" };
                    }
                }
            }
        }

        if (best is null) {
            return;
        }

        // Hold back (smart only, never two rounds in a row): a much bigger hit is affordable next round.
        if (_determinedSmartThisTurn && !_heldBackLastRound && !turn.Tuning.Excludes(RewrittenAI.NO_HOLD_BACK) && best.Damage > 0) {
            var bigger = turn.Hand.Where(c => c.Hit && !turn.Castable.Contains(c) && c.Rank <= turn.Pips + 1)
                .Select(c => (Card: c, Damage: RewrittenAI.Estimate(c, me, best.Target)))
                .OrderByDescending(x => x.Damage).FirstOrDefault();
            if (bigger.Card is not null && bigger.Damage >= best.Damage * 2.5f && RewrittenAI.Health(best.Target) > best.Damage) {
                options.Add(new Choice(null, best.Target, "hold", $"saving its pips for {bigger.Card.Shown} next round (about {bigger.Damage} vs {best.Damage})"));
            }
        }

        options.Add(best);
    }

    // ---------------------------------------------------------------- the move

    private COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE Commit(Turn turn, Choice choice) {
        var me = turn.Me;
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE {
            Actor = Entity.SelfRef,
            MoveType = (byte) CombatMoveType.Pass,
            SpellSelection = 0,
            SpellTarget = 0,
        };

        var index = choice.Card is null ? -1 : _roundHand.m_spellList.IndexOf(choice.Card.Spell);
        if (index >= 0) {
            msg.MoveType = (byte) CombatMoveType.Attack;
            msg.SpellSelection = (byte) index;
            msg.SpellTarget = (byte) (choice.Target ?? me).SlotIndex;
        }

        _heldBackLastRound = choice.Kind == "hold";
        _setupStreak = index >= 0 && choice.Setup ? _setupStreak + 1 : index >= 0 && choice.Card.Hit ? 0 : _setupStreak;
        if (turn.Target is not null) {
            _rewrittenTarget = turn.Target.SlotIndex;
        }

        // Its plan, for the creatures deciding after it.
        if (index >= 0) {
            var school = choice.Card.Hit ? choice.Card.DamageSchool : choice.Card.Blade ? choice.Card.BladeSchool
                : choice.Card.Trap ? choice.Card.TrapSchool : choice.Card.School;
            if (choice.HitsAll) {
                foreach (var enemy in turn.Enemies) {
                    turn.Plan.Add(new RewrittenAI.PlannedMove(me.SlotIndex, choice.Kind, enemy.SlotIndex, school ?? "",
                        RewrittenAI.Estimate(choice.Card, me, enemy), choice.Card.Id));
                }
            }
            else {
                turn.Plan.Add(new RewrittenAI.PlannedMove(me.SlotIndex, choice.Kind, (choice.Target ?? me).SlotIndex, school ?? "",
                    choice.Card.Hit ? choice.Damage : 0, choice.Card.Id));
            }
        }

        if (RewrittenAI.LogReasons) {
            var threat = string.Join("; ", turn.Enemies.OrderByDescending(e => turn.Threat.GetValueOrDefault(e.SlotIndex)).Take(4)
                .Select(e => $"{Name(e)} {turn.Threat.GetValueOrDefault(e.SlotIndex):0} ({turn.ThreatWhy.GetValueOrDefault(e.SlotIndex)})"));
            var what = index >= 0 ? $"{choice.Kind} {choice.Card.Shown} on {Name(choice.Target ?? me)}" : choice.Kind == "hold" ? "holds back" : "passes";
            Logger.Information("Rewritten AI | Duel {0} round {1} | {2} (slot {3}, {4}{5}) {6}: {7}. Target {8}: {9}. Threat: {10}",
                Logger.Args(_currentDuelComponent.SigilId, turn.Round, Name(me), me.SlotIndex, turn.Role,
                    _determinedSmartThisTurn ? ", thinking it through" : ", playing loose", what, choice.Reason,
                    turn.Target is null ? "none" : Name(turn.Target), turn.TargetWhy, threat));
        }

        return msg;
    }

    private void Say(Turn turn, string what) {
        if (RewrittenAI.LogReasons) {
            Logger.Information("Rewritten AI | Duel {0} round {1} | {2}: {3}.",
                Logger.Args(_currentDuelComponent.SigilId, turn.Round, Name(turn.Me), what));
        }
    }

    private static string Name(CombatDuelSubCircle circle) {
        if (circle is null) {
            return "nobody";
        }

        var name = CombatEffectApplicator.TargetName(circle);
        var school = RewrittenAI.SchoolOf(circle);

        return circle.IsHumanPlayer && school.Length > 0 ? $"{name} ({school})" : name;
    }

    private static string Blank(string school) => string.IsNullOrEmpty(school) ? "any-school" : school;

}
