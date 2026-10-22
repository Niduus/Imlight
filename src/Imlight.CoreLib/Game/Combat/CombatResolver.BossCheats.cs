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

    // Once cheats that went off, by boss, the cheat file's template and the cheat's place in that file.
    private readonly HashSet<(CombatDuelSubCircle Boss, ulong Template, int Cheat)> _bossCheatsUsed = [];

    // The template whose cheats each boss uses now (CheatTemplate).
    private readonly Dictionary<CombatDuelSubCircle, ulong> _bossCheatTemplates = [];

    // Each boss's phase and the round it began in (a boss starts in phase 1 in its first round, so a boss added with
    // .joinme counts its own rounds).
    private readonly Dictionary<CombatDuelSubCircle, (int Phase, int FirstRound)> _bossPhases = [];

    private IEnumerable<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> BossesWithCheats(bool defeatedToo = false)
        // Not IsAiCreature: a boss brought in with .joinme is a summoned minion, and it cheats too (Justin's log, 2026-10-21).
        => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && !c.IsHumanPlayer && (defeatedToo || c.IsAlive)
                                  && c.ParticipantObject is not null)
            .Select(c => (c, BossCheats.For(CheatTemplate(c))))
            .Where(b => b.Item2.Count > 0);

    // The template whose cheat file a boss uses: the creature it was polymorphed into (Haunted Malistaire, 1336037)
    // when that has a file, else its own. A new form starts in phase 1 from the next round, counting its own rounds.
    private ulong CheatTemplate(CombatDuelSubCircle boss) {
        // A custom (Customs.cs) cheats as its custom, and its form as the custom its Forms names, if any.
        var form = Customs.FormKey(boss.ParticipantObject, CombatPolymorph.FormOf(boss));
        var template = form != 0 && BossCheats.For(form).Count > 0 ? form : Customs.KeyOf(boss.ParticipantObject);
        if (_bossCheatTemplates.TryGetValue(boss, out var before) && before != template) {
            _bossPhases[boss] = (1, (int) _duel.m_roundNum + 1);
            Logger.Information("Duel {0} | Slot {1} | Boss now cheats as {2}.",
                Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, BossCheats.NameOf(template)));
        }

        _bossCheatTemplates[boss] = template;

        return template;
    }

    private bool CheatUsed(CombatDuelSubCircle boss, int index)
        => _bossCheatsUsed.Contains((boss, _bossCheatTemplates.GetValueOrDefault(boss), index));

    private void UseCheat(CombatDuelSubCircle boss, int index)
        => _bossCheatsUsed.Add((boss, _bossCheatTemplates.GetValueOrDefault(boss), index));

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
        // A boss defeated by a cheat's spell comes back first if an afterlife waits on it.
        ProcessAfterlives();
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
                if (!cheat.Is("HealthBelow") || !under || !InPhase(boss, cheat) || CheatUsed(boss, i)
                    || (!boss.IsAlive && cheat.SetHealth <= 0 && cheat.Spawn == 0)) {
                    continue;
                }

                cinematicTime += TryBossCheat(boss, i, cheat, null, null, $"health {health}",
                    combatActionList, cinematicTimeSoFar + cinematicTime);
                UseCheat(boss, i);
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
        if (cheat.Once && CheatUsed(boss, index)) {
            return 0;
        }

        // A second form comes before anything else is checked, so the battle goes on: a new creature (Spawn) when a
        // circle is free, else the boss back up in its own circle (SetHealth).
        var form = cheat.Spawn > 0 ? boss._duelActor.SpawnBossForm(cheat.Spawn, boss) : null;
        if (form is not null) {
            return BossFormArrives(boss, index, cheat, form, combatActionList);
        }

        if (cheat.Spawn > 0) {
            Logger.Warning("Duel {0} | Slot {1} | Boss form {2} found no free circle{3}.", Logger.Args(_duel.m_duelID.Full,
                boss.SlotIndex, cheat.Spawn, cheat.SetHealth > 0 ? "; the boss comes back in its own circle" : ""));
        }

        if (cheat.SetHealth > 0) {
            SetBossHealth(boss, cheat.SetHealth);
        }

        if (!boss.IsAlive || BattleDecided() || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        if (cheat.Once) {
            UseCheat(boss, index);
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

        var bossName = BossCheats.NameOf(_bossCheatTemplates.GetValueOrDefault(boss, Customs.KeyOf(boss.ParticipantObject)));
        var casts = cheat.CastList();
        Logger.Information("Duel {0} | Slot {1} | {2} cheats after {3}: {4}{5}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, reason,
                casts.Count == 0 ? "no spell" : string.Join(", ", casts.Select(c => c.Spell)), line is null ? "" : $", line {line}"));

        var cinematicTime = 0.0f;
        var lines = line is null ? new List<string>() : [line];
        // Each spell in turn (or one picked at random with Random); a file's single Cast is a list of one.
        foreach (var cast in casts) {
            var spell = cast.SpellID != 0 ? SpellFactory.GetSpell(cast.SpellID)
                : SpellFactory.Exists(cast.Spell) ? SpellFactory.GetSpell(cast.Spell) : null;
            var template = spell is null ? null : CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;
            if (template is null) {
                Logger.Warning("Duel {0} | Boss cheat spell \"{1}\" of {2} not found.", Logger.Args(_duel.m_duelID.Full, cast.Spell, bossName));

                continue;
            }

            // Free: no pips are taken for it.
            spell = spell with { m_pipCost = new SpellRank() };
            foreach (var target in BossCheatTargets(boss, cheat, template, trigger, receiver)) {
                if (!boss.IsAlive || target is null || !target.IsAlive || !target.AddedToDuel) {
                    continue;
                }

                boss._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                var before = combatActionList.m_actionList.Count;
                var action = new QueuedCombatAction { SpellCaster = boss, SelectedTarget = target, Spell = spell, SpellTemplate = template };
                // A cheat spell never fizzles and works through stun. A Dispel on the boss stops it only when it is
                // not DispelImmune, as it stops a player's spell of that school.
                var time = !cast.DispelImmune && ConsumeDispell(boss, spell.m_magicSchoolID)
                    ? HandleFizzleAction(action, combatActionList)
                    : HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
                // Silent: the game is told not to show the cast, like .c1s, so the wait is shorter too.
                if (cast.Silent) {
                    for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                        combatActionList.m_actionList[i].m_showCast = false;
                    }

                    time = Math.Min(time, SkipCastSeconds);
                }

                cinematicTime += time;
                if (lines.Count > 0 && before < combatActionList.m_actionList.Count) {
                    cinematicTime += InsertAnnouncements(combatActionList, before, combatActionList.m_actionList[before], lines, boss.SlotIndex);
                    lines.Clear();
                }
            }
        }

        // With Spawn, Defeat is for the boss the new form replaces; one brought back in its own circle stays up.
        if (cheat.Defeat && cheat.Spawn == 0 && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
            Logger.Information("Duel {0} | Slot {1} | {2} defeats itself.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName));
        }

        if (lines.Count > 0) {
            cinematicTime += ShowBossLine(line, boss, combatActionList);
        }

        return cinematicTime;
    }

    // A line with no spell shows after the round's last spell so far, or before the round's first spell.
    private float ShowBossLine(string line, CombatDuelSubCircle boss, CombatActionListObj combatActionList) {
        var last = combatActionList.m_actionList.FindLastIndex(a => a.m_spell is not null);
        if (last >= 0) {
            return InsertAnnouncements(combatActionList, last + 1, combatActionList.m_actionList[last], [line], boss.SlotIndex);
        }

        AddAnnouncement(line, boss.SlotIndex);

        return 0;
    }

    // A second form (Spawn) took a circle: the boss is defeated (Defeat) and its line shows over it. The new creature
    // fights from the next round with its own cheats.
    private float BossFormArrives(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle form,
                                  CombatActionListObj combatActionList) {
        if (cheat.Once) {
            UseCheat(boss, index);
        }

        var bossName = BossCheats.NameOf(_bossCheatTemplates.GetValueOrDefault(boss, Customs.KeyOf(boss.ParticipantObject)));
        if (cheat.Defeat && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
        }

        Logger.Information("Duel {0} | Slot {1} | {2} comes back as {3} in slot {4}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, BossCheats.NameOf(cheat.Spawn), form.SlotIndex));

        if (cheat.Line.Length == 0) {
            return 0;
        }

        if (!CommandAnnounceProtocol.TryResolve(cheat.Line, out var line, out _, out var problem)) {
            Logger.Warning("Duel {0} | Boss cheat line \"{1}\" not found: {2}", Logger.Args(_duel.m_duelID.Full, cheat.Line, problem));

            return 0;
        }

        return ShowBossLine(line, boss, combatActionList);
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
