/*
 * Boss cheats in a round (Justin, 2026-10-21). The cheats themselves are in BossCheats.cs and spiraldb-extra/BossCheats.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // Once cheats that went off, by boss and the cheat's place in its file.
    private readonly HashSet<(CombatDuelSubCircle Boss, int Cheat)> _bossCheatsUsed = [];

    // Each boss's phase and the round it began in (a boss starts in phase 1 in its first round, so a boss added with
    // .joinme counts its own rounds).
    private readonly Dictionary<CombatDuelSubCircle, (int Phase, int FirstRound)> _bossPhases = [];

    private IEnumerable<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> BossesWithCheats(bool defeatedToo = false)
        // Not IsAiCreature: a boss brought in with .joinme is a summoned minion, and it cheats too (Justin's log, 2026-10-21).
        => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && !c.IsHumanPlayer && (defeatedToo || c.IsAlive)
                                  && c.ParticipantObject is not null)
            .Select(c => (c, BossCheats.For(c.ParticipantObject.m_templateID)))
            .Where(b => b.Item2.Count > 0);

    private (int Phase, int FirstRound) PhaseOf(CombatDuelSubCircle boss) {
        if (!_bossPhases.TryGetValue(boss, out var phase)) {
            _bossPhases[boss] = phase = (1, (int) _duel.m_roundNum);
        }

        return phase;
    }

    private bool InPhase(CombatDuelSubCircle boss, BossCheats.BossCheat cheat)
        => cheat.Phase <= 0 || cheat.Phase == PhaseOf(boss).Phase;

    // The boss's round in its phase, from 1.
    private int BossRound(CombatDuelSubCircle boss) => (int) _duel.m_roundNum - PhaseOf(boss).FirstRound + 1;

    private static bool DueIn(BossCheats.BossCheat cheat, int round)
        => cheat.Round > 0 && cheat.Every > 0 ? round >= cheat.Round && (round - cheat.Round) % cheat.Every == 0
            : cheat.Round > 0 ? round == cheat.Round
            : cheat.Every <= 0 || round % cheat.Every == 0;

    private bool HasAllies(CombatDuelSubCircle boss)
        => _subCircles.Any(c => c is not null && c != boss && c.Occupied && c.AddedToDuel && c.IsAlive
                                && c.OccupiedTeam == boss.OccupiedTeam);

    private float BossCheatsAtRoundStart(CombatActionListObj combatActionList, float cinematicTimeSoFar)
        => BossRoundCheats(atEnd: false, combatActionList, cinematicTimeSoFar);

    private float BossCheatsAtRoundEnd(CombatActionListObj combatActionList, float cinematicTimeSoFar)
        => BossRoundCheats(atEnd: true, combatActionList, cinematicTimeSoFar);

    private float BossRoundCheats(bool atEnd, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!BossCheats.Enabled) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                var round = BossRound(boss);
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!InPhase(boss, cheat) || (cheat.NoAllies && HasAllies(boss))) {
                        continue;
                    }

                    var due = atEnd ? cheat.Is("RoundEnd") && DueIn(cheat, round)
                        : cheat.Is("BattleStart") ? round == 1 && PhaseOf(boss).Phase == 1
                        : cheat.Is("RoundStart") && DueIn(cheat, round);
                    if (due) {
                        cinematicTime += TryBossCheat(boss, i, cheat, null, null, $"{(atEnd ? "end of " : "")}round {round}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += BossHealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats at round {1} failed: {2}",
                Logger.Args(_duel.m_duelID.Full, atEnd ? "end" : "start", e));
        }

        return cinematicTime;
    }

    private float BossCheatsAfter(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!BossCheats.Enabled || mover is null || !mover.IsHumanPlayer) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            var moves = combatActionList.m_actionList.Skip(before).Where(a => a.m_spellCaster == mover.SlotIndex).ToList();
            var passed = moves.Count > 0 && moves.All(a => a.m_spell is null);
            var landed = moves.Where(a => a.m_spell is not null && a.m_spellHits == (char) 1)
                .Select(a => (Action: a, Spell: CoreObjectFactory.GetCoreTemplate(a.m_spell.m_templateID) as SpellTemplate))
                .Where(m => m.Spell is not null)
                .ToList();

            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!InPhase(boss, cheat)) {
                        continue;
                    }

                    if (cheat.Is("PlayerPasses") && passed) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, null, "a pass", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                    else if (cheat.Is("PlayerCasts") && landed.FirstOrDefault(m => Matches(cheat, m.Spell, m.Action, boss)) is
                             { Spell: not null } move) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, ReceiverOf(move.Action), $"{(string) move.Spell.m_name}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += BossHealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats after slot {1}'s move failed: {2}",
                Logger.Args(_duel.m_duelID.Full, mover.SlotIndex, e));
        }

        return cinematicTime;
    }

    private CombatDuelSubCircle ReceiverOf(CombatAction action)
        => (action.m_targetSubcircleList ?? []).Select(slot => _subCircles.FirstOrDefault(c => c is not null && c.Occupied
                                                                                                   && c.SlotIndex == slot))
            .FirstOrDefault(c => c is not null);

    private float BossHealthCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        // A defeated boss can still have a second form to come back as (SetHealth).
        foreach (var (boss, cheats) in BossesWithCheats(defeatedToo: true).ToList()) {
            var most = MostHealth(boss);
            if (most <= 0) {
                continue;
            }

            var health = boss.ParticipantGameStats.m_currentHitpoints;
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                var under = cheat.Health > 0 ? health < cheat.Health : health * 100.0 / most < cheat.Percent;
                // Health cheats go off once each, as the boss's health first falls under the mark.
                if (!cheat.Is("HealthBelow") || !under || !InPhase(boss, cheat) || _bossCheatsUsed.Contains((boss, i))
                    || (!boss.IsAlive && cheat.SetHealth <= 0)) {
                    continue;
                }

                cinematicTime += TryBossCheat(boss, i, cheat, null, null, $"health {health}",
                    combatActionList, cinematicTimeSoFar + cinematicTime);
                _bossCheatsUsed.Add((boss, i));
            }
        }

        return cinematicTime;
    }

    private static int MostHealth(CombatDuelSubCircle boss) {
        var most = boss.CombatParticipant?.m_maxPlayerHealth ?? 0;
        if (most <= 0) {
            most = boss.ParticipantGameStats.m_baseHitpoints + boss.ParticipantGameStats.m_bonusHitpoints;
        }

        return most;
    }

    // A second form: the boss's most and current health become this, even after it was defeated.
    private void SetBossHealth(CombatDuelSubCircle boss, int health) {
        var stats = boss.ParticipantGameStats;
        stats.m_baseHitpoints = Math.Max(1, health - stats.m_bonusHitpoints);
        stats.m_currentHitpoints = health;
        if (boss.CombatParticipant is not null) {
            boss.CombatParticipant.m_maxPlayerHealth = health;
            boss.CombatParticipant.m_playerHealth = health;
        }

        Logger.Information("Duel {0} | Slot {1} | Boss health set to {2}.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, health));
    }

    private static readonly HashSet<kEffectTarget> s_manyTargets = [
        kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce, kEffectTarget.kFriendlyTeam,
        kEffectTarget.kFriendlyTeamAllAtOnce, kEffectTarget.kAtLeastOneEnemy, kEffectTarget.kMultiTargetEnemy,
        kEffectTarget.kMultiTargetFriendly, kEffectTarget.kGlobal,
    ];

    private static bool Matches(BossCheats.BossCheat cheat, SpellTemplate spell, CombatAction action, CombatDuelSubCircle boss) {
        if (cheat.OnBoss && !(action.m_targetSubcircleList ?? []).Contains(boss.SlotIndex)) {
            return false;
        }

        if (cheat.SingleTarget && (spell.m_effects ?? []).Any(e => e is not null && s_manyTargets.Contains(e.m_effectTarget))) {
            return false;
        }

        if (cheat.School.Length > 0 && !cheat.School.Equals((string) spell.m_sMagicSchoolName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (cheat.Kind.Length > 0 && !cheat.Kind.Equals((string) spell.m_sTypeName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (cheat.Spells is { Count: > 0 }) {
            string name = spell.m_name;
            string displayKey = spell.m_displayName;
            var shown = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

            return cheat.Spells.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)
                                         || s.Equals(shown, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    /// <summary>
    /// Makes the boss use one cheat: its line shows over the boss, then its spell casts for free.
    /// </summary>
    /// <param name="trigger">The player who set it off, or null.</param>
    /// <returns>The seconds it adds to the round.</returns>
    /// <param name="receiver">Whoever that player's spell was aimed at, or null.</param>
    private float TryBossCheat(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                               CombatDuelSubCircle receiver, string reason, CombatActionListObj combatActionList,
                               float cinematicTimeSoFar) {
        // A second form comes back before anything else is checked: the boss is up again, so the battle goes on.
        if (cheat.SetHealth > 0 && !(cheat.Once && _bossCheatsUsed.Contains((boss, index)))) {
            SetBossHealth(boss, cheat.SetHealth);
        }

        if (!boss.IsAlive || BattleDecided() || (cheat.Once && _bossCheatsUsed.Contains((boss, index)))
            || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        if (cheat.Once) {
            _bossCheatsUsed.Add((boss, index));
        }

        if (cheat.SetPhase > 0) {
            // The new phase counts its rounds from the next round.
            _bossPhases[boss] = (cheat.SetPhase, (int) _duel.m_roundNum + 1);
            Logger.Information("Duel {0} | Slot {1} | Boss moves on to phase {2}.",
                Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, cheat.SetPhase));
        }

        string line = null;
        if (cheat.Line.Length > 0 && !CommandAnnounceProtocol.TryResolve(cheat.Line, out line, out _, out var problem)) {
            Logger.Warning("Duel {0} | Boss cheat line \"{1}\" not found: {2}", Logger.Args(_duel.m_duelID.Full, cheat.Line, problem));
        }

        var bossName = BossCheats.NameOf(boss.ParticipantObject.m_templateID);
        var spell = cheat.Cast.Length > 0 && SpellFactory.Exists(cheat.Cast) ? SpellFactory.GetSpell(cheat.Cast) : null;
        var template = spell is null ? null : CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;
        if (cheat.Cast.Length > 0 && template is null) {
            Logger.Warning("Duel {0} | Boss cheat spell \"{1}\" of {2} not found.", Logger.Args(_duel.m_duelID.Full, cheat.Cast, bossName));
        }

        Logger.Information("Duel {0} | Slot {1} | {2} cheats after {3}: {4}{5}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, reason,
                template is null ? "no spell" : cheat.Cast, line is null ? "" : $", line {line}"));

        var cinematicTime = 0.0f;
        var lines = line is null ? new List<string>() : [line];
        if (template is not null) {
            // Free: no pips are taken for it.
            spell = spell with { m_pipCost = new SpellRank() };
            foreach (var target in BossCheatTargets(boss, cheat, template, trigger, receiver)) {
                if (!boss.IsAlive || target is null || !target.IsAlive || !target.AddedToDuel) {
                    continue;
                }

                boss._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                var before = combatActionList.m_actionList.Count;
                var action = new QueuedCombatAction { SpellCaster = boss, SelectedTarget = target, Spell = spell, SpellTemplate = template };
                cinematicTime += HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
                if (lines.Count > 0 && before < combatActionList.m_actionList.Count) {
                    cinematicTime += InsertAnnouncements(combatActionList, before, combatActionList.m_actionList[before], lines, boss.SlotIndex);
                    lines.Clear();
                }
            }
        }

        if (cheat.Defeat && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
            Logger.Information("Duel {0} | Slot {1} | {2} defeats itself.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName));
        }

        // A line with no spell shows after the round's last spell so far, or before the round's first spell.
        if (lines.Count > 0) {
            var last = combatActionList.m_actionList.FindLastIndex(a => a.m_spell is not null);
            if (last >= 0) {
                cinematicTime += InsertAnnouncements(combatActionList, last + 1, combatActionList.m_actionList[last], lines, boss.SlotIndex);
            }
            else {
                AddAnnouncement(line, boss.SlotIndex);
            }
        }

        return cinematicTime;
    }

    private List<CombatDuelSubCircle> BossCheatTargets(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, SpellTemplate template,
                                                       CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var enemies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var target = cheat.Target.ToLowerInvariant();

        return target switch {
            "self" => [boss],
            "caster" when trigger is not null => [trigger],
            // A spell on a teammate is taken from that teammate; one on many (or on an enemy) from the caster.
            "receiver" when receiver is not null && receiver.OccupiedTeam != boss.OccupiedTeam => [receiver],
            "receiver" when trigger is not null => [trigger],
            "firstenemy" when enemies.Count > 0 => [enemies.OrderBy(c => c.SlotIndex).First()],
            "enemy" when enemies.Count > 0 => [enemies[Random.Shared.Next(enemies.Count)]],
            "allenemies" => enemies,
            _ => [trigger is not null && trigger.OccupiedTeam != boss.OccupiedTeam
                  && (template.m_effects ?? []).Any(e => e is not null && s_enemyTargets.Contains(e.m_effectTarget))
                ? trigger
                : PickFreeTarget(boss, template, null)],
        };
    }

}
