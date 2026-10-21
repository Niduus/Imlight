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

    // The round each boss first fought in, so a boss added with .joinme counts its own rounds.
    private readonly Dictionary<CombatDuelSubCircle, int> _bossFirstRound = [];

    // Once cheats that went off, by boss and the cheat's place in its file.
    private readonly HashSet<(CombatDuelSubCircle Boss, int Cheat)> _bossCheatsUsed = [];

    private IEnumerable<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> BossesWithCheats()
        => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAiCreature && c.IsAlive
                                  && c.ParticipantObject is not null)
            .Select(c => (c, BossCheats.For(c.ParticipantObject.m_templateID)))
            .Where(b => b.Item2.Count > 0);

    private float BossCheatsAtRoundStart(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!BossCheats.Enabled) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                if (!_bossFirstRound.TryGetValue(boss, out var first)) {
                    _bossFirstRound[boss] = first = (int) _duel.m_roundNum;
                }

                var round = (int) _duel.m_roundNum - first + 1;
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    var due = cheat.Is("BattleStart") ? round == 1
                        : cheat.Is("RoundStart") ? (cheat.Round > 0 ? round == cheat.Round : cheat.Every <= 0 || round % cheat.Every == 0)
                        : false;
                    if (due) {
                        cinematicTime += TryBossCheat(boss, i, cheat, null, $"round {round}", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += BossHealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats at round start failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
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
                .Select(a => CoreObjectFactory.GetCoreTemplate(a.m_spell.m_templateID) as SpellTemplate)
                .Where(t => t is not null)
                .ToList();

            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (cheat.Is("PlayerPasses") && passed) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, "a pass", combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                    else if (cheat.Is("PlayerCasts") && landed.FirstOrDefault(t => Matches(cheat, t)) is { } spell) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, $"{(string) spell.m_name}", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
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

    private float BossHealthCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            var most = boss.CombatParticipant?.m_maxPlayerHealth ?? 0;
            if (most <= 0) {
                most = boss.ParticipantGameStats.m_baseHitpoints + boss.ParticipantGameStats.m_bonusHitpoints;
            }

            if (most <= 0) {
                continue;
            }

            var percent = boss.ParticipantGameStats.m_currentHitpoints * 100.0 / most;
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                // Health cheats go off once each, as the boss's health first falls under the mark.
                if (cheat.Is("HealthBelow") && percent < cheat.Percent && !_bossCheatsUsed.Contains((boss, i))) {
                    cinematicTime += TryBossCheat(boss, i, cheat, null, $"health {percent:0}%",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                    _bossCheatsUsed.Add((boss, i));
                }
            }
        }

        return cinematicTime;
    }

    private static bool Matches(BossCheats.BossCheat cheat, SpellTemplate spell) {
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
    private float TryBossCheat(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                               string reason, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!boss.IsAlive || BattleDecided() || (cheat.Once && _bossCheatsUsed.Contains((boss, index)))
            || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        if (cheat.Once) {
            _bossCheatsUsed.Add((boss, index));
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
            foreach (var target in BossCheatTargets(boss, cheat, template, trigger)) {
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
                                                       CombatDuelSubCircle trigger) {
        var enemies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var target = cheat.Target.ToLowerInvariant();

        return target switch {
            "self" => [boss],
            "caster" when trigger is not null => [trigger],
            "enemy" when enemies.Count > 0 => [enemies[Random.Shared.Next(enemies.Count)]],
            "allenemies" => enemies,
            _ => [trigger is not null && trigger.OccupiedTeam != boss.OccupiedTeam
                  && (template.m_effects ?? []).Any(e => e is not null && s_enemyTargets.Contains(e.m_effectTarget))
                ? trigger
                : PickFreeTarget(boss, template, null)],
        };
    }

}
