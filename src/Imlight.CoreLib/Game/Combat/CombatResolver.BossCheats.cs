/*
 * Boss cheats in a round (Niduus, 2026-10-21). The cheats themselves are in BossCheats.cs and spiraldb-extra/BossCheats.
 *
 * Custom cheats (2026-10-22): the app's When -> If -> Do blocks. Each cheat's old fields (Cast, Line, SetPhase...) and
 * its Actions run as one list of steps (BossCheat.Steps). Cheats that interrupt go off before the player's spell
 * (BossCheatsBefore), the rest after it (BossCheatsAfter).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // Once cheats that went off, by boss, the cheat file's template and the cheat's place in that file.
    private readonly HashSet<(CombatDuelSubCircle Boss, ulong Template, int Cheat)> _bossCheatsUsed = [];

    // How often each cheat went off this battle and its last round (MaxTimes, Cooldown).
    private readonly Dictionary<(CombatDuelSubCircle Boss, ulong Template, int Cheat), (int Times, int Round)> _bossCheatCounts = [];

    // Creatures already seen defeated (MinionDefeated), by circle and the creature in it.
    private readonly HashSet<(CombatDuelSubCircle Circle, ulong Creature)> _seenDefeated = [];

    // Set by an Interrupt step while a cheat runs.
    private bool _interruptNow;

    // The player's spell that set the running cheat off (for Reflect), or 0.
    private uint _cheatSpellId;

    // Players who fled since the last round started (PlayerFlees), with the team they were on.
    private readonly List<(int Slot, CombatTeam Team)> _fledPlayers = [];

    // Players whose health a PlayerHealthBelow cheat already answered, by boss, template, cheat and player.
    private readonly HashSet<(CombatDuelSubCircle Boss, ulong Template, int Cheat, ulong Player)> _playerHealthSeen = [];

    // The template whose cheats each boss uses now (CheatTemplate).
    private readonly Dictionary<CombatDuelSubCircle, ulong> _bossCheatTemplates = [];

    // Each boss's phase and the round it began in (a boss starts in phase 1 in its first round, so a boss added with
    // .joinme counts its own rounds).
    private readonly Dictionary<CombatDuelSubCircle, (int Phase, int FirstRound)> _bossPhases = [];

    private IEnumerable<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> BossesWithCheats(bool defeatedToo = false)
        // Not IsAiCreature: a boss brought in with .joinme is a summoned minion, and it cheats too (Niduus's log, 2026-10-21).
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

    private (CombatDuelSubCircle, ulong, int) CheatKey(CombatDuelSubCircle boss, int index)
        => (boss, _bossCheatTemplates.GetValueOrDefault(boss), index);

    private bool CheatUsed(CombatDuelSubCircle boss, int index) => _bossCheatsUsed.Contains(CheatKey(boss, index));

    private void UseCheat(CombatDuelSubCircle boss, int index) => _bossCheatsUsed.Add(CheatKey(boss, index));

    private void CountCheat(CombatDuelSubCircle boss, int index) {
        var key = CheatKey(boss, index);
        _bossCheatCounts[key] = (_bossCheatCounts.GetValueOrDefault(key).Times + 1, (int) _duel.m_roundNum);
    }

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

    private static int PipCount(CombatDuelSubCircle c) => c.CombatParticipant?.m_pipCount is { } pips
        ? CombatDuelSubCircle.TotalPips(pips) + pips.m_shadowPips : 0;

    // The standing players on the other side with at least the cheat's pips.
    private List<CombatDuelSubCircle> PlayersWithPips(CombatDuelSubCircle boss, BossCheats.BossCheat cheat)
        => cheat.Pips <= 0 ? [] : [.. _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive
                                                          && c.OccupiedTeam != boss.OccupiedTeam && PipCount(c) >= cheat.Pips)];

    // When "Pips": the boss (or with PipsOf Enemy, any player) has at least that many pips.
    private bool PipsReached(CombatDuelSubCircle boss, BossCheats.BossCheat cheat) {
        static int Count(CombatDuelSubCircle c) => c.CombatParticipant?.m_pipCount is { } pips
            ? CombatDuelSubCircle.TotalPips(pips) + pips.m_shadowPips : 0;
        if (cheat.Pips <= 0) {
            return false;
        }

        return cheat.PipsOf.Equals("Enemy", StringComparison.OrdinalIgnoreCase)
            ? _subCircles.Any(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam
                                   && Count(c) >= cheat.Pips)
            : Count(boss) >= cheat.Pips;
    }

    // Boss cheats, then the local player's cheats (CombatResolver.PlayerCheats.cs).
    private float BossCheatsAtRoundStart(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var time = BossRoundCheats(atEnd: false, combatActionList, cinematicTimeSoFar);

        return time + PlayerCheatsAtRound(atEnd: false, combatActionList, cinematicTimeSoFar + time);
    }

    private float BossCheatsAtRoundEnd(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var time = BossRoundCheats(atEnd: true, combatActionList, cinematicTimeSoFar);

        return time + PlayerCheatsAtRound(atEnd: true, combatActionList, cinematicTimeSoFar + time);
    }

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

                    // "When a player has at least … pips": it goes off for each such player, who is "the attacker", so
                    // its spells can aim at the player who saved up (Niduus, 2026-10-22: Fire Dispel on a player with 6
                    // pips in round 1, against the Incindiate strat).
                    if (!atEnd && cheat.Is("Pips") && cheat.PipsOf.Equals("Enemy", StringComparison.OrdinalIgnoreCase)) {
                        foreach (var player in PlayersWithPips(boss, cheat)) {
                            cinematicTime += TryBossCheat(boss, i, cheat, player, player, $"round {round}, {cheat.Pips}+ pips on slot {player.SlotIndex}",
                                combatActionList, cinematicTimeSoFar + cinematicTime);
                        }

                        continue;
                    }

                    var due = atEnd ? cheat.Is("RoundEnd") && DueIn(cheat, round)
                        : cheat.Is("BattleStart") ? round == 1 && PhaseOf(boss).Phase == 1
                        : cheat.Is("Pips") ? PipsReached(boss, cheat)
                        // "FromRound": every round (or every Every rounds) from round Round of its phase on: an enrage.
                        : cheat.Is("FromRound") ? round >= Math.Max(1, cheat.Round)
                                                  && (cheat.Every <= 1 || (round - Math.Max(1, cheat.Round)) % cheat.Every == 0)
                        : cheat.Is("RoundStart") && DueIn(cheat, round);
                    if (due) {
                        cinematicTime += TryBossCheat(boss, i, cheat, null, null, $"{(atEnd ? "end of " : "")}round {round}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            if (!atEnd) {
                cinematicTime += FleeCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            }

            cinematicTime += MinionDefeatCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            cinematicTime += PlayerStateCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            cinematicTime += BossDefeatCheats(null, [], combatActionList, cinematicTimeSoFar + cinematicTime);
            cinematicTime += BossHealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats at round {1} failed: {2}",
                Logger.Args(_duel.m_duelID.Full, atEnd ? "end" : "start", e));
        }

        return cinematicTime;
    }

    /// <summary>
    /// Cheats that interrupt (an Interrupt step) go off before the spell that sets them off; the spell then fizzles
    /// (QueuedCombatAction.Interrupted).
    /// </summary>
    private float BossCheatsBefore(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var time = PlayerCheatsBefore(action, combatActionList, cinematicTimeSoFar);

        return time + BossCheatsBeforeMove(action, combatActionList, cinematicTimeSoFar + time);
    }

    private float BossCheatsBeforeMove(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!BossCheats.Enabled || action?.SpellCaster is not { } mover || action.Spell is null || action.SelectedTarget is null
            || !mover.IsAlive || mover.CombatParticipant is null || mover.CombatParticipant.m_stunned > 0
            || !action.SelectedTarget.IsAlive || !action.SelectedTarget.AddedToDuel) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            var spell = action.SpellTemplate ?? CoreObjectFactory.GetCoreTemplate(action.Spell.m_templateID) as SpellTemplate;
            if (spell is null) {
                return 0;
            }

            var aimed = AimedAt(mover, action.SelectedTarget, spell);
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!cheat.Interrupts || !InPhase(boss, cheat) || !SetOffBy(cheat, boss, mover, spell, aimed, crit: null)) {
                        continue;
                    }

                    _interruptNow = false;
                    _cheatSpellId = action.Spell.m_templateID;
                    cinematicTime += TryBossCheat(boss, i, cheat, mover, action.SelectedTarget, $"{(string) spell.m_name} (before it lands)",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                    _cheatSpellId = 0;
                    if (_interruptNow) {
                        action.Interrupted = true;
                        _interruptNow = false;

                        return cinematicTime;
                    }
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats before slot {1}'s move failed: {2}",
                Logger.Args(_duel.m_duelID.Full, mover.SlotIndex, e));
        }

        return cinematicTime;
    }

    // The slots a queued spell is aimed at: its target, or the target's whole side for a spell that hits many.
    private HashSet<int> AimedAt(CombatDuelSubCircle caster, CombatDuelSubCircle target, SpellTemplate spell) {
        var many = Flatten(spell).Any(e => s_manyTargets.Contains(e.m_effectTarget) && e.m_effectTarget != kEffectTarget.kGlobal);

        return many
            ? [.. _subCircles.Where(c => c is not null && c.Occupied && c.IsAlive && c.OccupiedTeam == target.OccupiedTeam).Select(c => c.SlotIndex)]
            : [target.SlotIndex];
    }

    // When "PlayerHeals", "PlayerCharms"...: When "PlayerCasts" for one kind of card (its Category on top of the cheat's).
    private static readonly Dictionary<string, string> s_castWhens = new(StringComparer.OrdinalIgnoreCase) {
        ["PlayerHeals"] = "Heal", ["PlayerCharms"] = "Charm", ["PlayerWards"] = "Ward", ["PlayerAuras"] = "Aura",
        ["PlayerGlobals"] = "Global", ["PlayerMinions"] = "Minion", ["PlayerShadows"] = "Shadow", ["PlayerDamages"] = "Damage",
    };

    // A cheat set off by a player's spell: PlayerCasts, the kinds above and PlayerCrits.
    private static bool IsCastWhen(BossCheats.BossCheat cheat)
        => cheat.Is("PlayerCasts") || cheat.Is("PlayerCrits") || s_castWhens.ContainsKey(cheat.When ?? "");

    // Whether a player's (or for BossHit, an enemy's) spell aimed at those slots sets the cheat off. Crit: whether that
    // spell was a critical hit, or null before it is cast (a PlayerCrits cheat is never set off then).
    private static bool SetOffBy(BossCheats.BossCheat cheat, CombatDuelSubCircle boss, CombatDuelSubCircle mover, SpellTemplate spell,
                                 ICollection<int> aimed, bool? crit) {
        if (IsCastWhen(cheat)) {
            if (cheat.Is("PlayerCrits") && crit != true) {
                return false;
            }

            return mover.IsHumanPlayer && Matches(cheat, spell, aimed, boss, mover)
                   && (!s_castWhens.TryGetValue(cheat.When, out var kind) || CategoriesOf(spell).Contains(kind));
        }

        return cheat.Is("BossHit") && mover.OccupiedTeam != boss.OccupiedTeam && aimed.Contains(boss.SlotIndex)
               && Flatten(spell).Any(e => s_damageEffects.Contains(e.m_effectType)) && Matches(cheat, spell, aimed, boss, mover);
    }

    private float BossCheatsAfter(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var time = BossCheatsAfterMove(mover, before, combatActionList, cinematicTimeSoFar);

        return time + PlayerCheatsAfter(mover, before, combatActionList, cinematicTimeSoFar + time);
    }

    private float BossCheatsAfterMove(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!BossCheats.Enabled || mover is null) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            var moves = combatActionList.m_actionList.Skip(before).Where(a => a.m_spellCaster == mover.SlotIndex).ToList();
            var passed = moves.Count > 0 && moves.All(a => a.m_spell is null);
            // A critical hit is m_spellHits 2 (CombatActionResolver): it landed too.
            var cast = moves.Where(a => a.m_spell is not null)
                .Select(a => (Action: a, Spell: CoreObjectFactory.GetCoreTemplate(a.m_spell.m_templateID) as SpellTemplate))
                .Where(m => m.Spell is not null)
                .ToList();
            var landed = cast.Where(m => m.Action.m_spellHits is (char) 1 or (char) 2).ToList();
            // A spell that fizzled is m_spellHits 0; a cheat's Fizzle says which of them set it off.
            List<(CombatAction Action, SpellTemplate Spell)> CountedFor(BossCheats.BossCheat cheat)
                => (cheat.Fizzle ?? "").ToLowerInvariant() switch {
                    "fizzled" => [.. cast.Where(m => m.Action.m_spellHits == (char) 0)],
                    "any" => cast,
                    _ => landed,
                };

            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!InPhase(boss, cheat) || cheat.Interrupts) {
                        continue;
                    }

                    if (cheat.Is("PlayerPasses") && passed && mover.IsHumanPlayer) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, null, "a pass", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                    else if ((IsCastWhen(cheat) || cheat.Is("BossHit"))
                             && CountedFor(cheat).FirstOrDefault(m => SetOffBy(cheat, boss, mover, m.Spell, m.Action.m_targetSubcircleList ?? [],
                                     crit: m.Action.m_spellHits == (char) 2))
                                 is { Spell: not null } move) {
                        _cheatSpellId = move.Action.m_spell.m_templateID;
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, ReceiverOf(move.Action),
                            $"{(string) move.Spell.m_name}{(move.Action.m_spellHits == (char) 2 ? " (a critical hit)" : move.Action.m_spellHits == (char) 0 ? " (fizzled)" : "")}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                        _cheatSpellId = 0;
                    }
                }
            }

            cinematicTime += MinionDefeatCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            cinematicTime += PlayerStateCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            cinematicTime += BossDefeatCheats(mover, landed, combatActionList, cinematicTimeSoFar + cinematicTime);
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

    // When "MinionDefeated": a creature on a boss's side that was standing is now defeated.
    private float MinionDefeatCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var fallen = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && !c.IsHumanPlayer && !c.IsAlive
                                            && c.ParticipantObject is not null
                                            && _seenDefeated.Add((c, c.ParticipantObject.m_globalID.Full)))
            .ToList();
        foreach (var minion in fallen) {
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss != minion && b.Boss.OccupiedTeam == minion.OccupiedTeam).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    if (cheats[i].Is("MinionDefeated") && InPhase(boss, cheats[i])) {
                        cinematicTime += TryBossCheat(boss, i, cheats[i], null, minion, $"the defeat of slot {minion.SlotIndex}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }
        }

        return cinematicTime;
    }

    // Bosses already seen defeated (BossDefeated); one that is brought back can set it off again.
    private readonly HashSet<CombatDuelSubCircle> _bossesSeenDefeated = [];

    // When "BossDefeated" (Niduus, 2026-10-22: "If the boss dies due to Incindiate, revive the boss"): a boss that was
    // standing is now defeated. After a player's move, the If blocks on spells (these spells, school, card type...) look
    // at that player's spells that landed; defeated any other way (an over time tick, a cheat), only a cheat with no
    // such blocks goes off. A Set health block first brings the boss back, as for its health dropping.
    private float BossDefeatCheats(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed,
                                   CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        foreach (var (boss, cheats) in BossesWithCheats(defeatedToo: true).ToList()) {
            if (boss.IsAlive) {
                _bossesSeenDefeated.Remove(boss);
                continue;
            }

            if (!_bossesSeenDefeated.Add(boss)) {
                continue;
            }

            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!cheat.Is("BossDefeated") || !InPhase(boss, cheat)) {
                    continue;
                }

                var spellBlocks = cheat.Spells is { Count: > 0 } || cheat.School.Length > 0 || cheat.Kind.Length > 0
                                  || cheat.Category is { Length: > 0 } || cheat.CostMin > 0 || cheat.CostMax > 0 || cheat.SingleTarget || cheat.OnBoss;
                var by = mover is null ? default
                    : landed.FirstOrDefault(m => Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover));
                if (spellBlocks && by.Spell is null) {
                    continue;
                }

                if (by.Spell is not null) {
                    _cheatSpellId = by.Action.m_spell.m_templateID;
                }

                cinematicTime += TryBossCheat(boss, i, cheat, mover, mover,
                    by.Spell is null ? "its defeat" : $"its defeat by {(string) by.Spell.m_name}",
                    combatActionList, cinematicTimeSoFar + cinematicTime);
                _cheatSpellId = 0;
            }

            if (boss.IsAlive) {
                _bossesSeenDefeated.Remove(boss);
            }
        }

        return cinematicTime;
    }

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
                    || (!boss.IsAlive && !cheat.Steps().Any(s => s.Is("SetHealth") || s.Is("Form")))) {
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

    // A spell's effects, with the effects inside its effect lists and random picks.
    private static IEnumerable<SpellEffect> Flatten(SpellTemplate spell) {
        static IEnumerable<SpellEffect> Walk(SpellEffect effect, int depth) {
            if (effect is null || depth > 4) {
                yield break;
            }

            yield return effect;
            var inner = effect switch {
                EffectListSpellEffect list => list.m_effectList,
                RandomSpellEffect random => random.m_effectList,
                _ => null,
            };
            foreach (var child in inner ?? []) {
                foreach (var e in Walk(child, depth + 1)) {
                    yield return e;
                }
            }
        }

        return (spell.m_effects ?? []).SelectMany(e => Walk(e, 0));
    }

    private static readonly HashSet<kSpellEffects> s_charmEffects = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyOutgoingDamageFlat, kSpellEffects.kModifyOutgoingHeal,
        kSpellEffects.kModifyOutgoingHealFlat, kSpellEffects.kModifyAccuracy, kSpellEffects.kCritBoost,
        kSpellEffects.kModifyOutgoingArmorPiercing, kSpellEffects.kDispel,
    ];

    private static readonly HashSet<kSpellEffects> s_wardEffects = [
        kSpellEffects.kModifyIncomingDamage, kSpellEffects.kModifyIncomingDamageFlat, kSpellEffects.kAbsorbDamage,
        kSpellEffects.kModifyIncomingHeal, kSpellEffects.kModifyIncomingHealFlat, kSpellEffects.kModifyIncomingDamageType,
        kSpellEffects.kCritBlock, kSpellEffects.kModifyIncomingArmorPiercing,
    ];

    // A charm or ward that helps whoever has it: by its disposition, else by its sign (less damage in is good).
    private static bool IsGood(SpellEffect effect) => effect.m_disposition switch {
        kHangingDisposition.kBeneficial => true,
        kHangingDisposition.kHarmful => false,
        _ => effect.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
            ? effect.m_effectParam < 0 : effect.m_effectParam > 0,
    };

    /// <summary>What a card does, from its type and effects (a cheat's Category): Damage, Heal, Shield, Trap...</summary>
    internal static HashSet<string> CategoriesOf(SpellTemplate spell) {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string type = spell.m_sTypeName;
        type ??= "";
        foreach (var name in new[] { "Aura", "Global", "Polymorph", "Steal", "Shadow", "Enchantment", "Manipulation", "Charm", "Ward" }) {
            if (type.Contains(name, StringComparison.OrdinalIgnoreCase)) {
                found.Add(name);
            }
        }

        foreach (var e in Flatten(spell)) {
            if (e.m_effectTarget == kEffectTarget.kGlobal) {
                found.Add("Global");
            }

            if (s_damageEffects.Contains(e.m_effectType)) {
                found.Add("Damage");
            }

            if (s_healEffects.Contains(e.m_effectType)) {
                found.Add("Heal");
            }

            if (e.m_effectType is kSpellEffects.kSummonCreature or kSpellEffects.kSpawnCreature) {
                found.Add("Minion");
            }

            if (e.m_effectType is kSpellEffects.kStun) {
                found.Add("Stun");
            }

            if (e.m_effectType is kSpellEffects.kDispel) {
                found.Add("Dispel");
            }

            if (e.m_effectType is kSpellEffects.kPolymorph) {
                found.Add("Polymorph");
            }

            if (e.m_effectType is kSpellEffects.kStealCharm or kSpellEffects.kStealWard or kSpellEffects.kStealHealth) {
                found.Add("Steal");
            }

            if (e.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingDamageFlat) {
                found.Add(IsGood(e) ? "Shield" : "Trap");
            }

            if (e.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat) {
                found.Add(IsGood(e) ? "Blade" : "Weakness");
            }
        }

        // An aura is a long-lasting charm on oneself: not a blade too.
        if (found.Contains("Aura")) {
            found.Remove("Blade");
        }

        // Charm: any blade, weakness or other charm (accuracy, critical...); Ward: any shield, trap or absorb.
        if (!found.Contains("Aura") && (found.Contains("Blade") || found.Contains("Weakness")
                                        || Flatten(spell).Any(e => e.m_effectType is kSpellEffects.kModifyAccuracy or kSpellEffects.kCritBoost
                                            or kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingArmorPiercing))) {
            found.Add("Charm");
        }

        if (found.Contains("Shield") || found.Contains("Trap") || Flatten(spell).Any(e => e.m_effectType == kSpellEffects.kAbsorbDamage)) {
            found.Add("Ward");
        }

        if (spell.m_Treasure) {
            found.Add("Treasure");
        }

        return found;
    }

    private static bool Matches(BossCheats.BossCheat cheat, SpellTemplate spell, ICollection<int> aimed, CombatDuelSubCircle boss,
                                CombatDuelSubCircle mover) {
        if (cheat.OnBoss && !aimed.Contains(boss.SlotIndex)) {
            return false;
        }

        if (cheat.SingleTarget && Flatten(spell).Any(e => s_manyTargets.Contains(e.m_effectTarget))) {
            return false;
        }

        if (cheat.School.Length > 0 && !cheat.School.Equals((string) spell.m_sMagicSchoolName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (cheat.Kind.Length > 0 && !cheat.Kind.Equals((string) spell.m_sTypeName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // Several kinds may be asked for, with commas between ("Charm, Ward").
        if (cheat.Category is { Length: > 0 } category && !SpellNameFamily.CategoryMatches(category, CategoriesOf(spell))) {
            return false;
        }

        if ((cheat.CostMin > 0 || cheat.CostMax > 0) && !SpellNameFamily.CostMatches(CostOf(spell), cheat.CostMin, cheat.CostMax)) {
            return false;
        }

        if (cheat.CasterSchool is { Length: > 0 } casterSchool
            && !casterSchool.Equals(mover?.ParticipantGameStats?.MagicSchool.ToString(), StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (cheat.Spells is { Count: > 0 }) {
            string name = spell.m_name;
            string displayKey = spell.m_displayName;
            var shown = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

            string spellBase = spell.m_spellBase;

            // AnyVersion: "Scald" is every Scald (its treasure card, item card, a mob's copy...).
            return SpellNameFamily.IsOneOf(cheat.Spells, cheat.AnyVersion, name, shown, cheat.AnyVersion ? spellBase : null);
        }

        return true;
    }

    // Whether the cheat's own limits let it go now: MaxTimes, Cooldown and the boss's health window.
    private bool LimitsAllow(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat) {
        var (times, last) = _bossCheatCounts.GetValueOrDefault(CheatKey(boss, index));
        if (cheat.MaxTimes > 0 && times >= cheat.MaxTimes) {
            return false;
        }

        if (cheat.Cooldown > 0 && times > 0 && (int) _duel.m_roundNum - last <= cheat.Cooldown) {
            return false;
        }

        // FromRound / ToRound count the battle's rounds; PlayersMin / PlayersMax the players standing.
        var battleRound = (int) _duel.m_roundNum;
        if ((cheat.FromRound > 0 && battleRound < cheat.FromRound) || (cheat.ToRound > 0 && battleRound > cheat.ToRound)) {
            return false;
        }

        if (cheat.PlayersMin > 0 || cheat.PlayersMax > 0) {
            var players = _subCircles.Count(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam);
            if (players < cheat.PlayersMin || (cheat.PlayersMax > 0 && players > cheat.PlayersMax)) {
                return false;
            }
        }

        if (cheat.HasAllies && !HasAllies(boss)) {
            return false;
        }

        var most = MostHealth(boss);
        var percent = most > 0 ? boss.ParticipantGameStats.m_currentHitpoints * 100.0 / most : 0;

        return (cheat.HealthAbove <= 0 || percent > cheat.HealthAbove) && (cheat.HealthUnder <= 0 || percent < cheat.HealthUnder);
    }

    /// <summary>
    /// Makes the boss use one cheat: its steps in order (a line shows over the boss with its first spell, spells cast
    /// for free, and the rest).
    /// </summary>
    /// <param name="trigger">The player who set it off, or null.</param>
    /// <param name="receiver">Whoever that player's spell was aimed at (or the defeated minion), or null.</param>
    /// <returns>The seconds it adds to the round.</returns>
    private float TryBossCheat(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                               CombatDuelSubCircle receiver, string reason, CombatActionListObj combatActionList,
                               float cinematicTimeSoFar) {
        if ((cheat.Once && CheatUsed(boss, index)) || !LimitsAllow(boss, index, cheat)) {
            return 0;
        }

        var steps = PickSteps(cheat.Steps());
        var template = _bossCheatTemplates.GetValueOrDefault(boss, Customs.KeyOf(boss.ParticipantObject));
        var bossName = BossCheats.NameOf(template);
        var custom = cheat.Custom || Customs.IsCustom(template);

        // A second form comes before anything else is checked, so the battle goes on: a new creature (Form) when a
        // circle is free, else the boss back up in its own circle (SetHealth).
        var formStep = steps.FirstOrDefault(s => s.Is("Form") && (s.Creature > 0 || s.Custom > 0));
        if (formStep is not null) {
            var form = SpawnCheatCreature(boss, formStep, asForm: true);
            if (form is not null) {
                LogCustomCheat(custom, boss, bossName, index, reason, steps);

                return BossFormArrives(boss, index, cheat, formStep, form, bossName, steps, combatActionList);
            }

            Logger.Warning("Duel {0} | Slot {1} | Boss form {2} found no free circle{3}.", Logger.Args(_duel.m_duelID.Full,
                boss.SlotIndex, formStep.Creature, steps.Any(s => s.Is("SetHealth")) ? "; the boss comes back in its own circle" : ""));
        }

        foreach (var health in steps.Where(s => s.Is("SetHealth") && s.Amount > 0)) {
            SetBossHealth(boss, health.Amount);
        }

        if (!boss.IsAlive || BattleDecided() || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        if (cheat.Once) {
            UseCheat(boss, index);
        }

        CountCheat(boss, index);
        var casts = steps.Where(s => s.IsCast).ToList();
        Logger.Information("Duel {0} | Slot {1} | {2} cheats after {3}: {4}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, reason,
                casts.Count == 0 ? "no spell" : string.Join(", ", casts.Select(c => c.Spell.Length > 0 ? c.Spell : c.SpellID.ToString()))));
        LogCustomCheat(custom, boss, bossName, index, reason, steps);

        var cinematicTime = 0.0f;
        var lines = new List<string>();
        var defeat = false;
        foreach (var step in steps) {
            if (!boss.IsAlive && !step.Is("Say")) {
                break;
            }

            switch (step.Do.ToLowerInvariant()) {
                case "form" or "sethealth":
                    break;
                case "setphase" when step.Phase > 0:
                    // The new phase counts its rounds from the next round.
                    _bossPhases[boss] = (step.Phase, (int) _duel.m_roundNum + 1);
                    Logger.Information("Duel {0} | Slot {1} | Boss moves on to phase {2}.",
                        Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, step.Phase));
                    break;
                case "say":
                    if (step.Line.Length > 0) {
                        if (CommandAnnounceProtocol.TryResolve(step.Line, out var line, out _, out var problem)) {
                            lines.Add(line);
                        }
                        else {
                            Logger.Warning("Duel {0} | Boss cheat line \"{1}\" not found: {2}", Logger.Args(_duel.m_duelID.Full, step.Line, problem));
                        }
                    }

                    if (step.Text.Length > 0) {
                        TellPlayers($"{bossName}: {step.Text}");
                    }

                    break;
                case "cast" or "polymorph":
                    cinematicTime += CheatCast(boss, cheat, step, trigger, receiver, bossName, lines, combatActionList,
                        cinematicTimeSoFar + cinematicTime, custom);
                    break;
                case "interrupt":
                    _interruptNow = true;
                    break;
                case "removecharms" or "removewards":
                    foreach (var target in StepTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        RemoveHanging(target, step.Is("RemoveCharms") ? s_charmEffects : s_wardEffects, step.Kind, step.Count, step.Spell);
                    }

                    break;
                case "addeffects":
                    AddHanging(boss, step, trigger, receiver, bossName);
                    break;
                case "heal":
                    foreach (var target in StepTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        HealFromCheat(target, step.Percent ? MostHealth(target) * step.Amount / 100 : step.Amount);
                    }

                    break;
                case "minion":
                    if (SpawnCheatCreature(boss, step, asForm: false) is null) {
                        Logger.Warning("Duel {0} | Slot {1} | Cheat minion {2} found no free circle.",
                            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, step.Custom > 0 ? $"custom {step.Custom}" : step.Creature));
                    }

                    break;
                case "pips":
                    foreach (var target in StepTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        ChangePips(target, step.Kind, step.Amount);
                    }

                    break;
                case "defeat":
                    defeat = true;
                    break;
                case "pickone":
                    // Handled by PickSteps before the steps run.
                    break;
                default:
                    if (TryMoreCheatSteps(boss, cheat, step, trigger, receiver, bossName, lines, combatActionList,
                            cinematicTimeSoFar + cinematicTime, custom, out var more)) {
                        cinematicTime += more;

                        break;
                    }


                    Logger.Warning("Duel {0} | Boss cheat of {1}: \"{2}\" is not something a cheat can do.",
                        Logger.Args(_duel.m_duelID.Full, bossName, step.Do));
                    break;
            }
        }

        // A form brought back in its own circle stays up; Defeat counts for the boss the form would have replaced.
        if (defeat && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
            Logger.Information("Duel {0} | Slot {1} | {2} defeats itself.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName));
        }

        foreach (var line in lines) {
            cinematicTime += ShowBossLine(line, boss, combatActionList);
        }

        return cinematicTime;
    }

    // "Custom cheat | ...": one line in the log each time a cheat of the app's (or a custom's) goes off.
    private void LogCustomCheat(bool custom, CombatDuelSubCircle boss, string bossName, int index, string reason,
                                List<BossCheats.CheatAction> steps) {
        if (!custom) {
            return;
        }

        Logger.Information("Custom cheat | Duel {0} | {1} (slot {2}) cheat {3} after {4}: {5}.",
            Logger.Args(_duel.m_duelID.Full, bossName, boss.SlotIndex, index + 1, reason,
                steps.Count == 0 ? "does nothing" : string.Join(", ", steps.Select(StepText))));
    }

    private static string StepText(BossCheats.CheatAction step) {
        var on = step.Target.Length > 0 ? $" on {step.Target}" : "";
        var spell = step.Spell.Length > 0 ? step.Spell : step.SpellID.ToString();

        return step.Do.ToLowerInvariant() switch {
            "cast" => $"cast {spell}{on}{(step.Silent ? " (silent)" : "")}",
            "polymorph" => $"polymorph with {spell}{on}",
            "say" => step.Line.Length > 0 ? $"say \"{step.Line}\"" : $"chat \"{step.Text}\"",
            "heal" => $"heal {step.Amount}{(step.Percent ? "%" : "")}{on}",
            "pips" => $"{(step.Amount < 0 ? "take" : "give")} {Math.Abs(step.Amount)} {step.Kind} pips{on}",
            "minion" or "form" => $"{step.Do.ToLowerInvariant()} {(step.Custom > 0 ? $"custom {step.Custom}" : step.Creature)}",
            "removecharms" or "removewards" => $"{step.Do} {step.Kind} x{(step.Count > 0 ? step.Count : "all")}{on}",
            "addeffects" => $"add the effects of {spell}{on}",
            "setphase" => $"phase {step.Phase}",
            "sethealth" => $"health {step.Amount}",
            "reflect" => "reflect the spell",
            "stealpips" => $"steal {step.Amount} {step.Kind} pips{on}",
            "removeauras" => $"remove auras{on}",
            "addcharm" => $"add a {step.Amount}% {SchoolText(step.School)}{step.Kind}{on}",
            "boost" => $"{step.Kind} {(step.Amount >= 0 ? "+" : "")}{step.Amount}% {SchoolText(step.School)}for {RoundsText(step.Rounds)}{on}",
            "immune" => $"immune to {SchoolText(step.School)}damage for {RoundsText(step.Rounds)}{on}",
            "stun" => $"stun for {Math.Max(1, step.Rounds)} rounds{on}",
            "healthto" => $"health to {step.Amount}%{on}",
            "swaphealth" => $"swap health{on}",
            "freecast" => $"free cast {spell}{on} x{Math.Max(1, step.Times)} ({(step.Timing.Length > 0 ? step.Timing : "Now")}"
                          + $"{(step.Silent ? ", silent" : "")}{(step.Enchant ? ", enchanted" : "")})",
            _ => step.Do.ToLowerInvariant(),
        };
    }

    // A cheat's spell: free, never fizzles, works through stun. A Dispel on the boss stops it only when it is not
    // DispelImmune, as it stops a player's spell of that school.
    private float CheatCast(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step, CombatDuelSubCircle trigger,
                            CombatDuelSubCircle receiver, string bossName, List<string> lines, CombatActionListObj combatActionList,
                            float cinematicTimeSoFar, bool custom) {
        var (spell, template) = FindSpell(step.Spell, step.SpellID);
        if (template is null) {
            Logger.Warning("Duel {0} | Boss cheat spell \"{1}\" of {2} not found.", Logger.Args(_duel.m_duelID.Full, step.Spell, bossName));

            return 0;
        }

        // Free: no pips are taken for it.
        spell = spell with { m_pipCost = new SpellRank() };
        // A polymorph that changes the boss (Afterlife - Malistaire 02) goes on itself unless told otherwise; one that
        // attacks in another shape (Polymorph - Dragon NA) aims as the cheat or the spell says.
        var lasting = Flatten(template).FirstOrDefault(e => e.m_effectType == kSpellEffects.kPolymorph);
        var aim = step.Target.Length > 0 ? step.Target : step.Is("Polymorph") && lasting is not null && cheat.Target.Length == 0 ? "Self" : cheat.Target;
        var cinematicTime = 0.0f;
        foreach (var target in StepTargets(boss, aim, template, trigger, receiver)) {
            if (!boss.IsAlive || target is null || !target.IsAlive || !target.AddedToDuel) {
                continue;
            }

            boss._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
            var before = combatActionList.m_actionList.Count;
            var action = new QueuedCombatAction { SpellCaster = boss, SelectedTarget = target, Spell = spell, SpellTemplate = template };
            var time = !step.DispelImmune && ConsumeDispell(boss, spell.m_magicSchoolID)
                ? HandleFizzleAction(action, combatActionList)
                : HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
            // Silent: the game is told not to show the cast, like .c1s, so the wait is shorter too.
            if (step.Silent) {
                for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                    combatActionList.m_actionList[i].m_showCast = false;
                }

                time = Math.Min(time, SkipCastSeconds);
            }

            cinematicTime += time;
            if (lasting is not null && before < combatActionList.m_actionList.Count) {
                NotePolymorphReplay(target, spell, template);
            }

            if (step.Is("Polymorph") || lasting is not null) {
                LogPolymorph(custom, boss, bossName, template, lasting, target, before < combatActionList.m_actionList.Count);
            }

            if (lines.Count > 0 && before < combatActionList.m_actionList.Count) {
                cinematicTime += InsertAnnouncements(combatActionList, before, combatActionList.m_actionList[before], [.. lines], boss.SlotIndex);
                lines.Clear();
            }
        }

        return cinematicTime;
    }

    // "Custom cheat | ... polymorph": what shape the boss takes and for how long, each time a polymorph cheat goes off.
    private void LogPolymorph(bool custom, CombatDuelSubCircle boss, string bossName, SpellTemplate template, SpellEffect lasting,
                              CombatDuelSubCircle target, bool sent) {
        string shape;
        if (lasting is not null) {
            var mob = CombatPolymorph.MobOf(lasting.m_effectParam);
            var name = CoreObjectFactory.GetCoreTemplate(mob) is GameObjectTemplate t ? (string) t.m_objectName : mob.ToString();
            shape = $"{(target == boss ? "it turns" : $"slot {target.SlotIndex} turns")} into {name} "
                    + (lasting.m_numRounds < 0 ? "for the rest of the battle" : $"for {lasting.m_numRounds} rounds");
        }
        else {
            // Polymorph - Dragon NA: its cinematic hides the boss, its other shape (the Black Dragon) comes in its place
            // and attacks, then goes, and the boss is back in its circle.
            shape = $"it takes its other shape for this attack on slot {target.SlotIndex}, then turns back";
        }

        Logger.Information("{0} | Duel {1} | {2} (slot {3}) polymorph with {4}: {5}{6}.",
            Logger.Args(custom ? "Custom cheat" : "Boss cheat", _duel.m_duelID.Full, bossName, boss.SlotIndex,
                (string) template.m_name, shape, sent ? "" : " (nothing was sent: the spell found no one)"));
    }

    private static (Spell Spell, SpellTemplate Template) FindSpell(string name, uint id) {
        var spell = id != 0 ? SpellFactory.GetSpell(id)
            : !string.IsNullOrWhiteSpace(name) && SpellFactory.Exists(name) ? SpellFactory.GetSpell(name)
            // A plain name ("Scald") that is no spell's own name: the plainest spell of that family.
            : !string.IsNullOrWhiteSpace(name) && FamilySpellName(name) is { } plain ? SpellFactory.GetSpell(plain) : null;

        return (spell, spell is null ? null : CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate);
    }

    private static readonly Lazy<List<(string Name, string Shown, bool Treasure)>> s_spellNames = new(() => {
        try {
            return [.. SpellFactory.AllSpellTemplates().Select(t => {
                string name = t.Template.m_name;
                string key = t.Template.m_displayName;

                return (name ?? "", string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key) ?? "", t.Template.m_Treasure);
            })];
        }
        catch (Exception e) {
            Logger.Error("Boss cheats | The spell names could not be listed: {0}", Logger.Args(e));

            return [];
        }
    });

    // The game name of the spell a family name stands for: not a treasure card, whose card shows that very name if
    // one does, else the shortest name.
    private static string FamilySpellName(string family) => s_spellNames.Value
        .Where(s => SpellNameFamily.AnyInFamily(family, s.Name, s.Shown))
        .OrderBy(s => s.Treasure)
        .ThenBy(s => !s.Shown.Equals(family.Trim(), StringComparison.OrdinalIgnoreCase))
        .ThenBy(s => s.Name.Length)
        .Select(s => s.Name)
        .FirstOrDefault();

    // The spell a hanging effect came from is in a cheat's Spell family (or is it, by name).
    private static bool FromSpell(SpellEffect effect, string family) {
        if (string.IsNullOrWhiteSpace(family)) {
            return true;
        }

        if (CoreObjectFactory.GetCoreTemplate(effect.m_spellTemplateID) is not SpellTemplate template) {
            return false;
        }

        string name = template.m_name;
        string key = template.m_displayName;

        return SpellNameFamily.AnyInFamily(family, name, string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key));
    }

    private static int CostOf(SpellTemplate spell) => spell.m_spellRank is { m_xPipSpell: false } rank ? rank.m_spellRank : 0;

    // AddEffects: a spell's charms and wards go on its targets with no casting, as if it had been cast.
    private void AddHanging(CombatDuelSubCircle boss, BossCheats.CheatAction step, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                            string bossName) {
        var (spell, template) = FindSpell(step.Spell, step.SpellID);
        if (template is null) {
            Logger.Warning("Duel {0} | Boss cheat spell \"{1}\" of {2} not found.", Logger.Args(_duel.m_duelID.Full, step.Spell, bossName));

            return;
        }

        var effects = Flatten(template).Where(e => s_charmEffects.Contains(e.m_effectType) || s_wardEffects.Contains(e.m_effectType)).ToList();
        foreach (var target in StepTargets(boss, step.Target.Length > 0 ? step.Target : "Self", template, trigger, receiver)) {
            foreach (var effect in effects) {
                var copy = effect with { m_spellTemplateID = spell.m_templateID };
                if (copy.m_effectType == kSpellEffects.kAbsorbDamage) {
                    copy = copy with { m_paramPerRound = copy.m_effectParam };
                }

                target._hangingEffects?.Add(copy);
            }
        }
    }

    // RemoveCharms / RemoveWards: Kind Good, Bad or All (empty), newest first, Count of them (0 = all); with a Spell, only
    // those that came from a spell of that name (any version).
    private void RemoveHanging(CombatDuelSubCircle target, HashSet<kSpellEffects> kinds, string kind, int count, string fromSpell = "") {
        if (target?._hangingEffects is not { } hanging) {
            return;
        }

        var good = kind.Equals("Good", StringComparison.OrdinalIgnoreCase);
        var bad = kind.Equals("Bad", StringComparison.OrdinalIgnoreCase);
        var gone = hanging.Where(e => kinds.Contains(e.m_effectType) && e.m_numRounds <= 0 && !e.m_protected
                                      && (!good || IsGood(e)) && (!bad || !IsGood(e)) && FromSpell(e, fromSpell))
            .Reverse()
            .Take(count > 0 ? count : int.MaxValue)
            .ToList();
        hanging.RemoveAll(gone.Contains);
        Logger.Information("Duel {0} | Slot {1} | Cheat takes off {2} of its charms or wards.",
            Logger.Args(_duel.m_duelID.Full, target.SlotIndex, gone.Count));
    }

    private void HealFromCheat(CombatDuelSubCircle target, int amount) {
        if (target is null || !target.IsAlive || amount <= 0) {
            return;
        }

        target.HealParticipant(amount);
        if (target._wizard is null && target.CombatParticipant is not null) {
            target.CombatParticipant.m_playerHealth = target.ParticipantGameStats.m_currentHitpoints;
        }

        Logger.Information("Duel {0} | Slot {1} | Cheat heals {2}, now {3}.",
            Logger.Args(_duel.m_duelID.Full, target.SlotIndex, amount, target.ParticipantGameStats.m_currentHitpoints));
    }

    // Pips: Amount above 0 gives (as .pip give), below 0 takes. Kind Normal (empty), Power or Shadow.
    // Returns how many it gave (or, for a take, took).
    private int ChangePips(CombatDuelSubCircle target, string kind, int amount) {
        if (target?.CombatParticipant?.m_pipCount is not { } pips || amount == 0) {
            return 0;
        }

        var name = (kind ?? "").ToLowerInvariant() switch { "power" => "power", "shadow" => "shadow", _ => "light" };
        if (amount > 0) {
            Logger.Information("Duel {0} | Slot {1} | Cheat pips: {2}", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, target.GivePips(name, amount)));

            return amount;
        }

        var take = -amount;
        for (; take > 0; take--) {
            if (name == "shadow" && pips.m_shadowPips > 0) {
                pips.m_shadowPips--;
            }
            else if (name == "power" && pips.m_powerPips > 0) {
                pips.m_powerPips--;
            }
            else if (name == "light" && pips.m_genericPips > 0) {
                pips.m_genericPips--;
            }
            else if (name == "light" && pips.m_powerPips > 0) {
                pips.m_powerPips--;
            }
            else {
                break;
            }
        }

        Logger.Information("Duel {0} | Slot {1} | Cheat takes {2} {3} pips.", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, -amount - take, name));

        return -amount - take;
    }

    // Say's Text: the words go to every player's chat.
    private void TellPlayers(string text) {
        foreach (var player in _subCircles.Where(c => c is not null && c.Occupied && c.IsHumanPlayer && c.ParticipantActor is not null)) {
            player.ParticipantActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
    }

    // Minion or Form: a creature, or a custom of the Customs tab, joins on the boss's side. A Form with no custom is the
    // custom the boss's Forms name for that creature, if any.
    private CombatDuelSubCircle SpawnCheatCreature(CombatDuelSubCircle boss, BossCheats.CheatAction step, bool asForm) {
        var creature = step.Creature;
        if (step.Custom > 0 && Customs.Get(step.Custom) is { } custom) {
            creature = (uint) custom.BaseTemplateID;
        }

        if (creature == 0) {
            return null;
        }

        return step.Custom > 0 ? boss._duelActor.SpawnBossMinion(creature, boss, step.Custom)
            : asForm ? boss._duelActor.SpawnBossForm(creature, boss)
            : boss._duelActor.SpawnBossMinion(creature, boss, 0);
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

    // A second form took a circle: the boss is defeated (ReplaceBoss) and its lines show over it. The new creature
    // fights from the next round with its own cheats.
    private float BossFormArrives(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, BossCheats.CheatAction formStep,
                                  CombatDuelSubCircle form, string bossName, List<BossCheats.CheatAction> steps,
                                  CombatActionListObj combatActionList) {
        if (cheat.Once) {
            UseCheat(boss, index);
        }

        CountCheat(boss, index);
        if (formStep.ReplaceBoss && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
        }

        Logger.Information("Duel {0} | Slot {1} | {2} comes back as {3} in slot {4}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName,
                BossCheats.NameOf(formStep.Custom > 0 ? Customs.Key(formStep.Custom) : formStep.Creature), form.SlotIndex));

        var cinematicTime = 0.0f;
        foreach (var say in steps.Where(s => s.Is("Say"))) {
            if (say.Text.Length > 0) {
                TellPlayers($"{bossName}: {say.Text}");
            }

            if (say.Line.Length == 0) {
                continue;
            }

            if (!CommandAnnounceProtocol.TryResolve(say.Line, out var line, out _, out var problem)) {
                Logger.Warning("Duel {0} | Boss cheat line \"{1}\" not found: {2}", Logger.Args(_duel.m_duelID.Full, say.Line, problem));

                continue;
            }

            cinematicTime += ShowBossLine(line, boss, combatActionList);
        }

        return cinematicTime;
    }

    // Who a step works on. Empty: as the spell aims (or the player who set it off, for a spell on enemies).
    private List<CombatDuelSubCircle> StepTargets(CombatDuelSubCircle boss, string aim, SpellTemplate template,
                                                  CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var enemies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var allies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam == boss.OccupiedTeam).ToList();
        var others = allies.Where(c => c != boss).ToList();

        return (aim ?? "").ToLowerInvariant() switch {
            "self" => [boss],
            "caster" or "attacker" when trigger is not null => [trigger],
            // A spell on a teammate is taken from that teammate; one on many (or on an enemy) from the caster.
            "receiver" when receiver is not null && receiver.OccupiedTeam != boss.OccupiedTeam => [receiver],
            "receiver" when trigger is not null => [trigger],
            "firstenemy" when enemies.Count > 0 => [enemies.OrderBy(c => c.SlotIndex).First()],
            "weakestenemy" when enemies.Count > 0 => [enemies.OrderBy(c => c.ParticipantGameStats.m_currentHitpoints).First()],
            "enemy" or "caster" or "attacker" when enemies.Count > 0 => [enemies[Random.Shared.Next(enemies.Count)]],
            "allenemies" => enemies,
            "ally" => [others.Count > 0 ? others[Random.Shared.Next(others.Count)] : boss],
            "allallies" => allies,
            _ when template is null => [boss],
            _ => [trigger is not null && trigger.OccupiedTeam != boss.OccupiedTeam
                  && Flatten(template).Any(e => s_enemyTargets.Contains(e.m_effectTarget))
                ? trigger
                : PickFreeTarget(boss, template, null)],
        };
    }

}
