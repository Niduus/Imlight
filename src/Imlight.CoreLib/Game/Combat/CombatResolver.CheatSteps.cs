/*
 * More boss cheats (Niduus, 2026-10-22: "Get more expansive with cheats, think out of the box"). The When and Do kinds
 * added to the ones of CombatResolver.BossCheats.cs; each is listed at the top of BossCheats.cs.
 *
 * Whens: PlayerFlees (a player ran from the battle, answered at the start of the next round), PlayerDefeated (a player
 * falls) and PlayerHealthBelow (a player's health first drops under Percent of their most, once each player).
 * Dos: Reflect, StealPips, RemoveAuras, AddCharm, Boost, Immune, Stun, HealthTo, SwapHealth and PickOne.
 *
 * For players and creatures alike (Niduus, 2026-10-23: "ALL CHEATS are to be compatible for PLAYERS and CREATURES"):
 * Whens EnemyJoins (someone joins the other side after round Round) and AllyHealthBelow (someone on its own side first
 * drops under Percent, or Health); the If on the creature the When is about (Creature, NotCreature); the Do Wait (the
 * steps after it go off at the start of a round Rounds rounds later); the targets Summoner, LastAttacker, WeakestAlly
 * and StrongestAlly.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    /// <summary>A player fled the battle (CombatDuelComponent): PlayerFlees cheats answer it at the start of the next round.</summary>
    internal void NotePlayerFled(CombatDuelSubCircle player) {
        try {
            if (player is not null && player.IsHumanPlayer) {
                _fledPlayers.Add((player.SlotIndex, player.OccupiedTeam));
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Noting a flee for the boss cheats failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }
    }

    private float FleeCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (_fledPlayers.Count == 0) {
            return 0;
        }

        var fled = _fledPlayers.ToList();
        _fledPlayers.Clear();
        var cinematicTime = 0.0f;
        foreach (var (slot, team) in fled) {
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss.OccupiedTeam != team).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    if (cheats[i].Is("PlayerFlees") && InPhase(boss, cheats[i])) {
                        cinematicTime += TryBossCheat(boss, i, cheats[i], null, null, $"the player in slot {slot} fleeing",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }
        }

        return cinematicTime;
    }

    // PlayerHealthBelow (someone on the other side) and AllyHealthBelow (someone else on its own side): their health first
    // drops under Percent, or Health hit points, once for each of them. "The attacker" is that one. (A player falling,
    // PlayerDefeated, is answered with MinionDefeated, MinionDefeatCheats.)
    private float PlayerStateCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var standing = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.ParticipantObject is not null)
            .ToList();
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                var ally = cheat.Is("AllyHealthBelow");
                if ((!ally && !cheat.Is("PlayerHealthBelow")) || !InPhase(boss, cheat)) {
                    continue;
                }

                foreach (var other in standing.Where(c => c != boss && (ally ? c.OccupiedTeam == boss.OccupiedTeam : IsFoe(boss, c)))) {
                    var most = MostHealth(other);
                    var health = other.ParticipantGameStats.m_currentHitpoints;
                    var under = cheat.Health > 0 ? health < cheat.Health : most > 0 && health * 100.0 / most < cheat.Percent;
                    if (!under || !_playerHealthSeen.Add((boss, _bossCheatTemplates.GetValueOrDefault(boss), i, other.ParticipantObject.m_globalID.Full))) {
                        continue;
                    }

                    cinematicTime += TryBossCheat(boss, i, cheat, other, other,
                        $"the health of {(ally ? "its ally" : "the player")} in slot {other.SlotIndex} dropping to {health}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    // A free cast "first in the round" (FreeCast Timing First) of a cheat going off as the round starts, and one "before
    // my card" of a cheat on its own card (ICast): both are cast at once, as their queue has already been cast.
    private bool _cheatAtRoundStart;
    private bool _cheatOwnCard;

    // The last one on the other side whose spell hurt each participant (the target LastAttacker).
    private readonly Dictionary<CombatDuelSubCircle, CombatDuelSubCircle> _lastAttackers = [];

    // Each participant's first round in the battle, by its global id (EnemyJoins); the round the count began.
    private readonly Dictionary<ulong, int> _joinRounds = [];
    private int _joinFirstRound;

    /// <summary>The steps after a Wait step of a cheat, kept for the start of a later round.</summary>
    private sealed record WaitingCheat(CombatDuelSubCircle Owner, BossCheats.BossCheat Cheat, List<BossCheats.CheatAction> Steps, int Round,
                                       CombatDuelSubCircle Trigger, CombatDuelSubCircle Receiver, string Name, bool Custom);

    private readonly List<WaitingCheat> _waitingCheats = [];

    // The share of its most health a participant has (1 when it has no most).
    private static double HealthShare(CombatDuelSubCircle c)
        => MostHealth(c) > 0 ? c.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(c) : 1;

    // Who summoned a minion into the battle (its CombatParticipant's m_minionSubCircle), or null.
    private CombatDuelSubCircle Summoner(CombatDuelSubCircle minion)
        => minion.IsSummonedMinion && minion.CombatParticipant is { } participant
            ? _subCircles.FirstOrDefault(c => c is not null && c != minion && c.Occupied && c.IsAlive && c.SlotIndex == participant.m_minionSubCircle)
            : null;

    // The If "only if it is this creature": the one the When is about (the one that fell for MinionDefeated and
    // PlayerDefeated, else the one whose move, health or coming in set it off) is of the template Creature, or with
    // NotCreature is any other one.
    private static bool CreatureAllows(BossCheats.BossCheat cheat, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        if (cheat.Creature == 0) {
            return true;
        }

        var who = cheat.Is("MinionDefeated") || cheat.Is("PlayerDefeated") ? receiver : trigger ?? receiver;
        var match = who?.ParticipantObject is { } obj && obj.m_templateID == cheat.Creature;

        return match != cheat.NotCreature;
    }

    // Each spell of the mover that landed and hurt: the mover is now the last attacker of whoever it hit on the other side.
    private void NoteAttackers(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed) {
        foreach (var (action, spell) in landed.Where(m => Flatten(m.Spell).Any(e => s_damageEffects.Contains(e.m_effectType)))) {
            foreach (var slot in action.m_targetSubcircleList ?? []) {
                if (_subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.SlotIndex == slot) is { } hit
                    && hit.OccupiedTeam != mover.OccupiedTeam) {
                    _lastAttackers[hit] = mover;
                }
            }
        }
    }

    // The steps after a Wait step, at the start of the round they waited for, if whoever's cheat it is still stands.
    private float WaitedCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var round = (int) _duel.m_roundNum;
        var due = _waitingCheats.Where(w => w.Round <= round).ToList();
        if (due.Count == 0) {
            return 0;
        }

        _waitingCheats.RemoveAll(due.Contains);
        var cinematicTime = 0.0f;
        foreach (var waited in due) {
            if (!waited.Owner.IsAlive || !waited.Owner.AddedToDuel || BattleDecided()) {
                Logger.Information("Duel {0} | Slot {1} | {2}: the steps it waited for are dropped (it is no longer standing).",
                    Logger.Args(_duel.m_duelID.Full, waited.Owner.SlotIndex, waited.Name));

                continue;
            }

            Logger.Information("{0} | Duel {1} | {2} (slot {3}) goes on after waiting, round {4}: {5}.",
                Logger.Args(waited.Custom ? "Custom cheat" : "Boss cheat", _duel.m_duelID.Full, waited.Name, waited.Owner.SlotIndex, round,
                    string.Join(", ", waited.Steps.Select(StepText))));
            cinematicTime += RunCheatSteps(waited.Owner, waited.Cheat, waited.Steps, waited.Trigger, waited.Receiver, waited.Name, waited.Custom,
                combatActionList, cinematicTimeSoFar + cinematicTime);
        }

        return cinematicTime;
    }

    // When "EnemyJoins": someone came into the battle on the other side, and its first round is later than Round of the
    // battle (Round 2: from round 3 on). For a creature's cheats that is a wizard; Minions: a summoned minion counts too.
    // "The attacker" and "whoever that spell was aimed at" are the newcomer. Checked at the start of each round.
    private float JoinCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var round = (int) _duel.m_roundNum;
        var first = _joinRounds.Count == 0;
        if (first) {
            _joinFirstRound = round;
        }

        var newcomers = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.ParticipantObject is not null
                                               && _joinRounds.TryAdd(c.ParticipantObject.m_globalID.Full, round))
            .ToList();
        if (first || newcomers.Count == 0) {
            return 0;
        }

        var battleRound = round - _joinFirstRound + 1;
        var cinematicTime = 0.0f;
        foreach (var newcomer in newcomers) {
            Logger.Information("Duel {0} | Slot {1} | Came into the battle in round {2}.",
                Logger.Args(_duel.m_duelID.Full, newcomer.SlotIndex, battleRound));
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss != newcomer).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!cheat.Is("EnemyJoins") || !InPhase(boss, cheat) || battleRound <= cheat.Round
                        || newcomer.OccupiedTeam == boss.OccupiedTeam
                        || (!cheat.Minions && (newcomer.IsSummonedMinion || !IsFoe(boss, newcomer)))) {
                        continue;
                    }

                    cinematicTime += TryBossCheat(boss, i, cheat, newcomer, newcomer, $"slot {newcomer.SlotIndex} joining in round {battleRound}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// PickOne: of the actions after it (Count of them, or all the rest when 0), only one is kept, picked at random by
    /// Weights ("2, 1, 1": the first twice as likely as each other one; 1 each when left out).
    /// </summary>
    private static List<BossCheats.CheatAction> PickSteps(List<BossCheats.CheatAction> steps) {
        if (!steps.Any(s => s.Is("PickOne"))) {
            return steps;
        }

        var kept = new List<BossCheats.CheatAction>();
        for (var i = 0; i < steps.Count; i++) {
            if (!steps[i].Is("PickOne")) {
                kept.Add(steps[i]);

                continue;
            }

            var pick = steps[i];
            var count = pick.Count > 0 ? Math.Min(pick.Count, steps.Count - i - 1) : steps.Count - i - 1;
            var group = steps.Skip(i + 1).Take(count).ToList();
            i += count;
            if (group.Count == 0) {
                continue;
            }

            var weights = (pick.Weights ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 1)
                .ToList();
            var chances = group.Select((_, g) => g < weights.Count ? weights[g] : 1).ToList();
            var total = chances.Sum();
            if (total <= 0) {
                continue;
            }

            var roll = Random.Shared.Next(total);
            for (var g = 0; g < group.Count; g++) {
                if (roll < chances[g]) {
                    kept.Add(group[g]);
                    break;
                }

                roll -= chances[g];
            }
        }

        return kept;
    }

    private static string SchoolText(string school) => string.IsNullOrWhiteSpace(school) || school.Equals("All", StringComparison.OrdinalIgnoreCase)
        ? "" : $"{school} ";

    private static string RoundsText(int rounds) => rounds <= 0 ? "the rest of the battle" : rounds == 1 ? "1 round" : $"{rounds} rounds";

    // An aura-like effect with rounds lasts that long and is never used up; 0 rounds is the rest of the battle.
    private const int REST_OF_BATTLE = 999;

    private static SpellEffect CheatEffect(kSpellEffects type, int amount, string school, int rounds, bool good) => new() {
        m_effectType = type,
        m_effectParam = amount,
        m_sDamageType = string.IsNullOrWhiteSpace(school) ? "All" : school.Trim(),
        m_disposition = good ? kHangingDisposition.kBeneficial : kHangingDisposition.kHarmful,
        m_effectTarget = kEffectTarget.kSelf,
        m_numRounds = rounds,
        m_paramPerRound = type == kSpellEffects.kAbsorbDamage ? amount : 0,
    };

    /// <summary>The Dos of this file. False when the step is none of them.</summary>
    private bool TryMoreCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                   CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName, List<string> lines,
                                   CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);

        try {
            switch (step.Do.ToLowerInvariant()) {
                case "reflect": {
                    // The player's spell fizzles and the boss casts it back: a spell on the boss's side goes on the
                    // player who cast it, one a player cast on their own side goes on the boss's side.
                    _interruptNow = true;
                    if (_cheatSpellId == 0) {
                        Logger.Warning("Duel {0} | Boss cheat of {1}: Reflect has no spell to send back (it answers a player's spell).",
                            Logger.Args(_duel.m_duelID.Full, bossName));

                        return true;
                    }

                    var back = step with { Do = "Cast", Spell = "", SpellID = _cheatSpellId, Target = step.Target.Length > 0 ? step.Target : "" };
                    cinematicTime = CheatCast(boss, cheat, back, trigger, receiver, bossName, lines, combatActionList, cinematicTimeSoFar, custom);

                    return true;
                }
                case "stealpips": {
                    var taken = On("Caster").Sum(target => ChangePips(target, step.Kind, -Math.Max(1, Math.Abs(step.Amount))));
                    if (taken > 0) {
                        ChangePips(boss, step.Kind, taken);
                    }

                    return true;
                }
                case "removeauras":
                    foreach (var target in On("AllEnemies").Where(t => t?._hangingEffects is not null)) {
                        var gone = target._hangingEffects.Count(e => CombatRequirements.IsAura(e) && !e.m_protected);
                        target._hangingEffects.RemoveAll(e => CombatRequirements.IsAura(e) && !e.m_protected);
                        Logger.Information("Duel {0} | Slot {1} | Cheat takes off {2} auras.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, gone));
                    }

                    return true;
                case "addcharm": {
                    // Blade, Weakness, Shield, Trap or Absorb of Amount (percent; hit points for Absorb), used up as one.
                    var amount = Math.Abs(step.Amount);
                    var effect = (step.Kind ?? "").ToLowerInvariant() switch {
                        "weakness" => CheatEffect(kSpellEffects.kModifyOutgoingDamage, -amount, step.School, 0, good: false),
                        "shield" => CheatEffect(kSpellEffects.kModifyIncomingDamage, -amount, step.School, 0, good: true),
                        "trap" => CheatEffect(kSpellEffects.kModifyIncomingDamage, amount, step.School, 0, good: false),
                        "absorb" => CheatEffect(kSpellEffects.kAbsorbDamage, amount, step.School, 0, good: true),
                        _ => CheatEffect(kSpellEffects.kModifyOutgoingDamage, amount, step.School, 0, good: true),
                    };
                    var fallback = effect.m_disposition == kHangingDisposition.kHarmful ? "Caster" : "Self";
                    foreach (var target in On(fallback).Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(effect with { });
                    }

                    return true;
                }
                case "boost": {
                    // Kind Damage (more damage out), Resist (less damage in), Heal (more healing out) or Accuracy, by
                    // Amount percent (below 0 lowers it), for Rounds rounds (0: the rest of the battle). Never used up.
                    var amount = step.Amount;
                    var rounds = step.Rounds > 0 ? step.Rounds + 1 : REST_OF_BATTLE;
                    var effect = (step.Kind ?? "").ToLowerInvariant() switch {
                        "resist" => CheatEffect(kSpellEffects.kModifyIncomingDamage, -amount, step.School, rounds, good: amount >= 0),
                        "heal" => CheatEffect(kSpellEffects.kModifyOutgoingHeal, amount, step.School, rounds, good: amount >= 0),
                        "accuracy" => CheatEffect(kSpellEffects.kModifyAccuracy, amount, step.School, rounds, good: amount >= 0),
                        _ => CheatEffect(kSpellEffects.kModifyOutgoingDamage, amount, step.School, rounds, good: amount >= 0),
                    };
                    foreach (var target in On("Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(effect with { });
                    }

                    return true;
                }
                case "immune": {
                    // No damage of School (every school when empty) for Rounds rounds: a ward of -100% that is never used up.
                    var rounds = step.Rounds > 0 ? step.Rounds + 1 : REST_OF_BATTLE;
                    foreach (var target in On("Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                        target._hangingEffects.Add(CheatEffect(kSpellEffects.kModifyIncomingDamage, -100, step.School, rounds, good: true));
                    }

                    return true;
                }
                case "stun":
                    foreach (var target in On("Caster").Where(t => t?.CombatParticipant is not null && t.IsAlive)) {
                        if (!target.TryStun()) {
                            continue;
                        }

                        target.CombatParticipant.m_stunned = Math.Max(target.CombatParticipant.m_stunned, Math.Max(1, step.Rounds));
                        Logger.Information("Duel {0} | Slot {1} | Cheat stuns it for {2} rounds.",
                            Logger.Args(_duel.m_duelID.Full, target.SlotIndex, target.CombatParticipant.m_stunned));
                    }

                    return true;
                case "healthto":
                    // Its health (or Target's) becomes Amount percent of its most, never below 1.
                    foreach (var target in On("Self")) {
                        SetHealthTo(target, (int) Math.Round(MostHealth(target) * Math.Clamp(step.Amount, 1, 100) / 100.0));
                    }

                    return true;
                case "swaphealth": {
                    // The boss and Target (the player who set it off, else a random player) swap their share of health.
                    var other = On("Caster").FirstOrDefault(t => t is not null && t != boss && t.IsAlive);
                    if (other is null || MostHealth(boss) <= 0 || MostHealth(other) <= 0) {
                        return true;
                    }

                    var mine = boss.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(boss);
                    var theirs = other.ParticipantGameStats.m_currentHitpoints / (double) MostHealth(other);
                    SetHealthTo(boss, (int) Math.Round(MostHealth(boss) * theirs));
                    SetHealthTo(other, (int) Math.Round(MostHealth(other) * mine));

                    return true;
                }
                default:
                    return false;
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheat of {1}: {2} failed: {3}", Logger.Args(_duel.m_duelID.Full, bossName, step.Do, e));

            return true;
        }
    }

    // A participant's health becomes this (1 to its most), up or down.
    private void SetHealthTo(CombatDuelSubCircle target, int health) {
        if (target?.ParticipantGameStats is not { } stats || !target.IsAlive) {
            return;
        }

        var want = Math.Clamp(health, 1, Math.Max(1, MostHealth(target)));
        var now = stats.m_currentHitpoints;
        if (want > now) {
            HealFromCheat(target, want - now);
        }
        else if (want < now) {
            target.DamageParticipant(now - want);
            if (target._wizard is null && target.CombatParticipant is not null) {
                target.CombatParticipant.m_playerHealth = stats.m_currentHitpoints;
            }

            Logger.Information("Duel {0} | Slot {1} | Cheat sets its health to {2}.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, stats.m_currentHitpoints));
        }
    }

}
