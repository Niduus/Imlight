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

    // Everyone in the battle with cheats, each with its own: a creature by its cheat file, and the wizard played on the
    // server's own PC by the player cheats (PlayerCheats.cs). Every When, If and Do works the same for both (Niduus,
    // 2026-10-23: "ALL CHEATS are to be compatible for PLAYERS and CREATURES"): "it" is whoever the cheats are of, and
    // "a player" is someone on the other side (IsFoe).
    private IEnumerable<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> BossesWithCheats(bool defeatedToo = false)
        // Not IsAiCreature: a boss brought in with .joinme is a summoned minion, and it cheats too (Niduus's log, 2026-10-21).
        => _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && (defeatedToo || c.IsAlive) && c.ParticipantObject is not null)
            .Select(c => (c, c.IsHumanPlayer ? PlayerCheats.IsLocal(c) ? PlayerCheats.Current() : s_noCheats : BossCheats.For(CheatTemplate(c))))
            .Where(b => b.Item2.Count > 0);

    private static readonly List<BossCheats.BossCheat> s_noCheats = [];

    // "A player" of a cheat: someone on the other side. For a creature's cheats that is a wizard (their minions do not
    // count, as before); for a wizard's player cheats, anyone there.
    private static bool IsFoe(CombatDuelSubCircle owner, CombatDuelSubCircle other)
        => other is not null && other.OccupiedTeam != owner.OccupiedTeam && (owner.IsHumanPlayer || other.IsHumanPlayer);

    // Whose cheats they are, for the log: the boss's name, or "player cheat of" the wizard.
    private string CheaterName(CombatDuelSubCircle owner) {
        if (!owner.IsHumanPlayer) {
            return BossCheats.NameOf(_bossCheatTemplates.GetValueOrDefault(owner, Customs.KeyOf(owner.ParticipantObject)));
        }

        string wizard = owner._wizard?.PlayerNameBehavior?.GetWizardName() ?? "";

        return $"player cheat of {(wizard.Length > 0 ? wizard : "the local wizard")}";
    }

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

    // The pips a Pips When counts: with PipsKind "Shadow" only the shadow pips.
    private static int PipCount(CombatDuelSubCircle c, string kind) => kind.Equals("Shadow", StringComparison.OrdinalIgnoreCase)
        ? (c.CombatParticipant?.m_pipCount is { } pips ? pips.m_shadowPips : 0) : PipCount(c);

    // The standing players on the other side with at least the cheat's pips.
    private List<CombatDuelSubCircle> PlayersWithPips(CombatDuelSubCircle boss, BossCheats.BossCheat cheat)
        => cheat.Pips <= 0 ? [] : [.. _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive
                                                          && c.OccupiedTeam != boss.OccupiedTeam && PipCount(c, cheat.PipsKind ?? "") >= cheat.Pips)];

    // When "Pips": the boss (or with PipsOf Enemy, any player) has at least that many pips (PipsKind Shadow: shadow pips).
    private bool PipsReached(CombatDuelSubCircle boss, BossCheats.BossCheat cheat) {
        int Count(CombatDuelSubCircle c) => PipCount(c, cheat.PipsKind ?? "");
        if (cheat.Pips <= 0) {
            return false;
        }

        return cheat.PipsOf.Equals("Enemy", StringComparison.OrdinalIgnoreCase)
            ? _subCircles.Any(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && c.OccupiedTeam != boss.OccupiedTeam
                                   && Count(c) >= cheat.Pips)
            : Count(boss) >= cheat.Pips;
    }

    // The cheats of bosses and of the local player alike (BossesWithCheats).
    private float BossCheatsAtRoundStart(CombatActionListObj combatActionList, float cinematicTimeSoFar)
        => BossRoundCheats(atEnd: false, combatActionList, cinematicTimeSoFar);

    private float BossCheatsAtRoundEnd(CombatActionListObj combatActionList, float cinematicTimeSoFar)
        => BossRoundCheats(atEnd: true, combatActionList, cinematicTimeSoFar);

    private float BossRoundCheats(bool atEnd, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            // A free cast "first in the round" of a cheat that goes off as the round starts is cast at once.
            _cheatAtRoundStart = !atEnd;
            if (!atEnd) {
                // Everyone's health as the round starts, for the Ifs "lost / gained this round" (CreativeCheats).
                NoteRoundStart();
                cinematicTime += WaitedCheats(combatActionList, cinematicTimeSoFar);
                cinematicTime += JoinCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
            }

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

                    // A RoundEnd with Pips: only if, at the end of the round, it (PipsOf Enemy: any player) still has them
                    // (Voice of Death: Mana Burn when a wizard ends the round with pips left).
                    var due = atEnd ? cheat.Is("RoundEnd") && DueIn(cheat, round) && (cheat.Pips <= 0 || PipsReached(boss, cheat))
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
            // Extra turns go last in the round, as free casts (CreativeCheats).
            if (atEnd) {
                QueueExtraTurns();
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats at round {1} failed: {2}",
                Logger.Args(_duel.m_duelID.Full, atEnd ? "end" : "start", e));
        }
        finally {
            _cheatAtRoundStart = false;
        }

        return cinematicTime;
    }

    /// <summary>
    /// Cheats that interrupt (an Interrupt step) go off before the spell that sets them off; the spell then fizzles
    /// (QueuedCombatAction.Interrupted).
    /// </summary>
    // ICast (its own spell) and EnemyCasts (a spell from the other side) go off just before the spell lands, as do
    // cheats that interrupt.
    private float BossCheatsBefore(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        // A Silence step: its spells fizzle until the rounds are up (CreativeCheats).
        if (action?.SpellCaster is { } silenced && action.Spell is not null && IsSilenced(silenced)) {
            action.Interrupted = true;
            Logger.Information("Duel {0} | Slot {1} | A cheat's silence makes its spell fizzle.", Logger.Args(_duel.m_duelID.Full, silenced.SlotIndex));

            return 0;
        }

        if (action?.SpellCaster is not { } mover || action.Spell is null || action.SelectedTarget is null
            || !mover.IsAlive || mover.CombatParticipant is null || mover.CombatParticipant.m_stunned > 0) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            var spell = action.SpellTemplate ?? CoreObjectFactory.GetCoreTemplate(action.Spell.m_templateID) as SpellTemplate;
            if (spell is null) {
                return 0;
            }

            var targetStands = action.SelectedTarget.IsAlive && action.SelectedTarget.AddedToDuel;
            var aimed = AimedAt(mover, action.SelectedTarget, spell);
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!InPhase(boss, cheat)) {
                        continue;
                    }

                    var own = cheat.Is("ICast") && mover == boss;
                    var before = own || (cheat.Is("EnemyCasts") && IsFoe(boss, mover));
                    // A spell that won't go off (its target is gone) sets off no "before it lands" cheat.
                    if (before ? !targetStands && !CanGoOnFallen(spell) || !Matches(cheat, spell, aimed, boss, mover)
                            : !cheat.Interrupts || !targetStands || !SetOffBy(cheat, boss, mover, spell, aimed, crit: null)) {
                        continue;
                    }

                    _interruptNow = false;
                    _cheatSpellId = action.Spell.m_templateID;
                    _cheatCard = action.Spell;
                    // A free cast "before my card" of a cheat on its own card is cast at once.
                    _cheatOwnCard = own;
                    cinematicTime += TryBossCheat(boss, i, cheat, mover, action.SelectedTarget,
                        $"{(own ? "its own " : mover == boss ? "" : $"slot {mover.SlotIndex}'s ")}{(string) spell.m_name} (before it lands)",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                    _cheatOwnCard = false;
                    _cheatSpellId = 0;
                    _cheatCard = null;
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

            return IsFoe(boss, mover) && Matches(cheat, spell, aimed, boss, mover)
                   && (!s_castWhens.TryGetValue(cheat.When, out var kind) || CategoriesOf(spell).Contains(kind));
        }

        return cheat.Is("BossHit") && mover.OccupiedTeam != boss.OccupiedTeam && aimed.Contains(boss.SlotIndex)
               && Flatten(spell).Any(e => s_damageEffects.Contains(e.m_effectType)) && Matches(cheat, spell, aimed, boss, mover);
    }

    private float BossCheatsAfter(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var time = BossCheatsAfterMove(mover, before, combatActionList, cinematicTimeSoFar);
        // A player cheat's free casts (Incindiate after a pass) can defeat a boss after its own cheats were checked.
        if (mover is not null) {
            try {
                time += BossDefeatCheats(mover, [], combatActionList, cinematicTimeSoFar + time);
            }
            catch (Exception e) {
                Logger.Error("Duel {0} | Boss defeat cheats after slot {1}'s move failed: {2}", Logger.Args(_duel.m_duelID.Full, mover.SlotIndex, e));
            }
        }

        return time;
    }

    private float BossCheatsAfterMove(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (mover is null) {
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

            NoteAttackers(mover, landed);
            // Every spell cast, for the Ifs "a spell was cast" and "the last spell" (CreativeCheats).
            NoteCasts(mover, cast);
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!InPhase(boss, cheat) || cheat.Interrupts) {
                        continue;
                    }

                    if (cheat.Is("PlayerPasses") && passed && IsFoe(boss, mover)) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, null, "a pass", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                    // IPass and ICrit: its own pass, or its own spell landing as a critical hit.
                    else if (cheat.Is("IPass") && passed && mover == boss) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, null, "its own pass", combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                    else if (cheat.Is("ICrit") && mover == boss
                             && cast.FirstOrDefault(m => m.Action.m_spellHits == (char) 2
                                                         && Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover))
                                 is { Spell: not null } crit) {
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, ReceiverOf(crit.Action, mover),
                            $"its own critical {(string) crit.Spell.m_name}", combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                    // SideFizzles: a spell of its own or of someone on its side fizzles; "the attacker" and "the one hit" are
                    // the one whose spell fizzled (Adrok's Lightning Strike on the fizzler, Dream Jabberwock's Dark Empower).
                    else if (cheat.Is("SideFizzles") && mover.OccupiedTeam == boss.OccupiedTeam
                             && cast.FirstOrDefault(m => m.Action.m_spellHits == (char) 0
                                                         && Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover))
                                 is { Spell: not null } fizzled) {
                        _cheatSpellId = fizzled.Action.m_spell.m_templateID;
                        _cheatCard = fizzled.Action.m_spell;
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, mover,
                            $"{(mover == boss ? "its own" : $"slot {mover.SlotIndex}'s")} {(string) fizzled.Spell.m_name} (fizzled)",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                        _cheatSpellId = 0;
                        _cheatCard = null;
                    }
                    // AllyHit: a spell from the other side hurts someone else on its side; that one is "the one hit".
                    else if (cheat.Is("AllyHit") && mover.OccupiedTeam != boss.OccupiedTeam
                             && landed.Select(m => (m.Action, m.Spell, Hit: AllyHitBy(boss, m.Action, m.Spell)))
                                 .FirstOrDefault(m => m.Hit is not null && Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover))
                                 is { Hit: { } ally } allyMove) {
                        _cheatSpellId = allyMove.Action.m_spell.m_templateID;
                        _cheatCard = allyMove.Action.m_spell;
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, ally, $"{(string) allyMove.Spell.m_name} on its ally in slot {ally.SlotIndex}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                        _cheatSpellId = 0;
                        _cheatCard = null;
                    }
                    else if ((IsCastWhen(cheat) || cheat.Is("BossHit"))
                             && CountedFor(cheat).FirstOrDefault(m => SetOffBy(cheat, boss, mover, m.Spell, m.Action.m_targetSubcircleList ?? [],
                                     crit: m.Action.m_spellHits == (char) 2))
                                 is { Spell: not null } move) {
                        _cheatSpellId = move.Action.m_spell.m_templateID;
                        _cheatCard = move.Action.m_spell;
                        cinematicTime += TryBossCheat(boss, i, cheat, mover, ReceiverOf(move.Action, mover),
                            $"{(string) move.Spell.m_name}{(move.Action.m_spellHits == (char) 2 ? " (a critical hit)" : move.Action.m_spellHits == (char) 0 ? " (fizzled)" : "")}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                        _cheatSpellId = 0;
                        _cheatCard = null;
                    }
                }
            }

            cinematicTime += MoreWhenCheats(mover, landed, combatActionList, cinematicTimeSoFar + cinematicTime);
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

    // The first one on the boss's side, not the boss, that a damage spell from the other side was aimed at, or null.
    private CombatDuelSubCircle AllyHitBy(CombatDuelSubCircle boss, CombatAction action, SpellTemplate spell)
        => !Flatten(spell).Any(e => s_damageEffects.Contains(e.m_effectType)) ? null
            : (action.m_targetSubcircleList ?? []).Where(slot => slot != boss.SlotIndex)
                .Select(slot => _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.SlotIndex == slot))
                .FirstOrDefault(c => c is not null && c.OccupiedTeam == boss.OccupiedTeam);

    // Whoever a spell was aimed at; for a spell that went on many (Bladestorm), the caster, so a cheat on "whoever that
    // spell was aimed at" answers the one who cast it (Rattlebones Exalted's Fairy Queen, Central's page).
    private CombatDuelSubCircle ReceiverOf(CombatAction action, CombatDuelSubCircle caster = null) {
        var slots = action.m_targetSubcircleList ?? [];
        if (slots.Count > 1 && caster is not null) {
            return caster;
        }

        return slots.Select(slot => _subCircles.FirstOrDefault(c => c is not null && c.Occupied && c.SlotIndex == slot))
            .FirstOrDefault(c => c is not null);
    }

    // When "MinionDefeated" (someone on its side that was standing is now defeated) and "PlayerDefeated" (someone on the
    // other side). Both look at the same list of the newly fallen, so neither takes a defeat from the other.
    private float MinionDefeatCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var fallen = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && !c.IsAlive
                                            && c.ParticipantObject is not null
                                            && _seenDefeated.Add((c, c.ParticipantObject.m_globalID.Full)))
            .ToList();
        foreach (var gone in fallen) {
            foreach (var (boss, cheats) in BossesWithCheats().Where(b => b.Boss != gone).ToList()) {
                for (var i = 0; i < cheats.Count; i++) {
                    if (!InPhase(boss, cheats[i])) {
                        continue;
                    }

                    if (cheats[i].Is("MinionDefeated") && boss.OccupiedTeam == gone.OccupiedTeam) {
                        cinematicTime += TryBossCheat(boss, i, cheats[i], null, gone, $"the defeat of slot {gone.SlotIndex}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                    else if (cheats[i].Is("PlayerDefeated") && IsFoe(boss, gone)) {
                        cinematicTime += TryBossCheat(boss, i, cheats[i], null, gone, $"the defeat of the player in slot {gone.SlotIndex}",
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
                                  || cheat.Category is { Length: > 0 } || cheat.CostMin > 0 || cheat.CostMax > 0 || cheat.SingleTarget || cheat.OnBoss || cheat.NotOnCaster
                                  || cheat.NotCategory is { Length: > 0 };
                var by = mover is null ? default
                    : landed.FirstOrDefault(m => Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], boss, mover));
                var byWho = mover;
                // Else the last spell that landed on it this round: a free cast of a player cheat, .db or .cf, or a
                // move before the round's end (Niduus, 2026-10-22: Incindiate came from a player cheat after a pass).
                if (by.Spell is null && LastHitOn(boss, combatActionList) is { } last) {
                    byWho = _subCircles.FirstOrDefault(c => c is not null && c.SlotIndex == last.Action.m_spellCaster);
                    if (byWho is not null && Matches(cheat, last.Spell, last.Action.m_targetSubcircleList ?? [], boss, byWho)) {
                        by = last;
                    }
                    else {
                        byWho = mover;
                    }
                }

                if (spellBlocks && by.Spell is null) {
                    continue;
                }

                if (by.Spell is not null) {
                    _cheatSpellId = by.Action.m_spell.m_templateID;
                    _cheatCard = by.Action.m_spell;
                }

                cinematicTime += TryBossCheat(boss, i, cheat, byWho, byWho,
                    by.Spell is null ? "its defeat" : $"its defeat by {(string) by.Spell.m_name}",
                    combatActionList, cinematicTimeSoFar + cinematicTime);
                _cheatSpellId = 0;
                _cheatCard = null;
            }

            if (boss.IsAlive) {
                _bossesSeenDefeated.Remove(boss);
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// Which side acts first (.wegofirst, .theygofirst and the Turn order cheat block): who's side, or the other one.
    /// The order of a round is set when its spells start, so a cheat's change counts from the next round.
    /// </summary>
    internal void SetFirstSide(CombatDuelSubCircle who, bool weFirst) {
        if (_duel.m_bPVP || who is null) {
            return;
        }

        var team = weFirst ? who.OccupiedTeam : who.OccupiedTeam == CombatTeam.Player ? CombatTeam.Monster : CombatTeam.Player;
        _duel.m_firstTeamToAct = (int) team;
        // The sun moves at once, not at the next round.
        who._duelActor?.ShowFirstSide();
        Logger.Information("Duel {0} | Slot {1} | {2} side goes first now.",
            Logger.Args(_duel.m_duelID.Full, who.SlotIndex, team == CombatTeam.Player ? "The players'" : "The creatures'"));
    }

    // The last spell of this round's actions that landed on the boss, with its action.
    private static (CombatAction Action, SpellTemplate Spell) LastHitOn(CombatDuelSubCircle boss, CombatActionListObj combatActionList) {
        for (var i = combatActionList.m_actionList.Count - 1; i >= 0; i--) {
            var action = combatActionList.m_actionList[i];
            if (action?.m_spell is not null && action.m_spellHits is (char) 1 or (char) 2 && action.m_spellCaster != boss.SlotIndex
                && (action.m_targetSubcircleList?.Contains(boss.SlotIndex) ?? false)
                && CoreObjectFactory.GetCoreTemplate(action.m_spell.m_templateID) is SpellTemplate spell) {
                return (action, spell);
            }
        }

        return default;
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
                // MyHealthBelow is the player cheats' old name for it.
                if (!(cheat.Is("HealthBelow") || cheat.Is("MyHealthBelow")) || !under || !InPhase(boss, cheat) || CheatUsed(boss, i)
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

            // DoT and HoT: damage or healing over rounds (Dewey, MacDeath: "If a Wizard casts a :dot:").
            if (e.m_effectType is kSpellEffects.kDamageOverTime) {
                found.Add("DoT");
            }

            if (e.m_effectType is kSpellEffects.kHealOverTime) {
                found.Add("HoT");
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

        if (cheat.MultiTarget && !Flatten(spell).Any(e => s_manyTargets.Contains(e.m_effectTarget))) {
            return false;
        }

        if (cheat.NotOnCaster && mover is not null && aimed.Contains(mover.SlotIndex)) {
            return false;
        }

        if (cheat.NotCategory is { Length: > 0 } notCategory && SpellNameFamily.CategoryMatches(notCategory, CategoriesOf(spell))) {
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

        if ((cheat.CostMin > 0 || cheat.CostMax != 0) && !SpellNameFamily.CostMatches(CostOf(spell), cheat.CostMin, cheat.CostMax)) {
            return false;
        }

        if (cheat.CasterSchool is { Length: > 0 } casterSchool
            && !casterSchool.Equals(mover?.SchoolNow.ToString(), StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // What the spell does: "a different spell that also clears DoTs" (CombatResolver.MoreCheats.cs).
        if (!SpellDoesAllows(cheat, spell)) {
            return false;
        }

        if (cheat.Spells is { Count: > 0 }) {
            string name = spell.m_name;
            string displayKey = spell.m_displayName;
            var shown = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

            string spellBase = spell.m_spellBase;

            // AnyVersion: "Scald" is every Scald (its treasure card, item card, a mob's copy...).
            // A spell typed as its template ID ("857718733") is that very spell (Niduus, 2026-10-26).
            var id = SpellNameFamily.AnyIds(cheat.Spells) ? SpellFactory.TemplateIdOf(spell).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

            return SpellNameFamily.IsOneOf(cheat.Spells, cheat.AnyVersion, name, shown, cheat.AnyVersion ? spellBase : null, id);
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

        // NoAllies counts for every When, not only those of a round (Niduus, 2026-10-24: a minion's defeat that leaves
        // the boss alone, for the gauntlets' summon cycles).
        if (cheat.NoAllies && HasAllies(boss)) {
            return false;
        }

        if (!AlliesAllow(boss, cheat)) {
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
        if ((cheat.Once && CheatUsed(boss, index)) || !LimitsAllow(boss, index, cheat) || !CreatureAllows(cheat, trigger, receiver)
            || !HasAllows(boss, cheat, trigger, receiver) || !MarkAllows(boss, cheat, trigger, receiver)
            || !MoreAllows(boss, cheat, trigger, receiver) || CheatTurnedOff(boss, index)
            || !ConditionsAllow(boss, index, cheat, trigger, receiver)) {
            return 0;
        }

        // The one the If "has" found (AnyEnemy / AnyAlly), for the target "Matched".
        var matched = _hasMatch;

        // InTurn picks by how many times the cheat went off before.
        var steps = PickSteps(cheat.Steps(), _bossCheatCounts.GetValueOrDefault(CheatKey(boss, index)).Times);
        var template = _bossCheatTemplates.GetValueOrDefault(boss, Customs.KeyOf(boss.ParticipantObject));
        var bossName = CheaterName(boss);
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
            // A wizard's most health is their own (it would be saved with them): only their health changes.
            if (boss.IsHumanPlayer) {
                SetHealthTo(boss, health.Amount);
            }
            else {
                SetBossHealth(boss, health.Amount);
            }
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

        return RunCheatSteps(boss, cheat, steps, trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar, matched);
    }

    // A cheat's steps, with whose cheat they are (the target "Owner") and whom its If found (the target "Matched")
    // kept for them, and put back after, as a step's spell can set off another cheat.
    private float RunCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, List<BossCheats.CheatAction> steps,
                                CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName, bool custom,
                                CombatActionListObj combatActionList, float cinematicTimeSoFar, CombatDuelSubCircle matched = null) {
        var (owner, wasMatched) = (_cheatOwner, _cheatMatched);
        (_cheatOwner, _cheatMatched) = (boss, matched);
        var had = combatActionList.m_actionList.ToHashSet(ReferenceEqualityComparer.Instance);
        try {
            return RunCheatStepsInOrder(boss, cheat, steps, trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar);
        }
        finally {
            (_cheatOwner, _cheatMatched) = (owner, wasMatched);
            MarkCheatCasts(combatActionList, had);
        }
    }

    /// <summary>
    /// The actions a cheat added (its casts, their fizzles and lines) go to the game as interrupts, out of the turn
    /// order ([BossCheats] CastsAsInterrupt), so the game does not take them for the caster's turn and play its
    /// damage and heal over time ticks with them; the server ticks those once, on the caster's own turn.
    /// </summary>
    private static void MarkCheatCasts(CombatActionListObj combatActionList, IReadOnlySet<object> had) {
        if (!BossCheats.CastsAsInterrupt) {
            return;
        }

        foreach (var action in combatActionList.m_actionList) {
            if (action is not null && !had.Contains(action)) {
                action.m_interrupt = CastsAsInterrupt(action);
            }
        }
    }

    /// <summary>
    /// Whether a cheat's action goes to the game as an interrupt. A global (bubble) does not ([BossCheats]
    /// GlobalsInTurn): sent as an interrupt the game played its cast but never put the bubble up (Niduus, 2026-10-26:
    /// "no global still. if it helps, it's a shadow global").
    /// </summary>
    internal static bool CastsAsInterrupt(CombatAction action) {
        if (!BossCheats.CastsAsInterrupt) {
            return false;
        }

        if (!BossCheats.GlobalsInTurn || action?.m_spell is not { } spell) {
            return true;
        }

        return CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template
               || template.m_effects?.Any(e => e is not null && e.m_effectTarget == kEffectTarget.kGlobal) != true;
    }

    // A cheat's steps, in order. A Wait step keeps the ones after it for the start of a later round (WaitedCheats).
    private float RunCheatStepsInOrder(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, List<BossCheats.CheatAction> steps,
                                       CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName, bool custom,
                                       CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var lines = new List<string>();
        var defeat = false;
        _lastStepTargets = [];
        _clearedCount = 0;
        for (var s = 0; s < steps.Count; s++) {
            var step = steps[s];
            if (!boss.IsAlive && !step.Is("Say")) {
                break;
            }

            // The mark a step's "whoever has the mark" means: its own, else the cheat's.
            _stepMark = step.Mark.Length > 0 ? step.Mark : cheat.Mark;

            if (step.Is("Wait")) {
                var later = steps.Skip(s + 1).ToList();
                if (later.Count > 0) {
                    var rounds = Math.Max(1, step.Rounds);
                    _waitingCheats.Add(new WaitingCheat(boss, cheat, later, (int) _duel.m_roundNum + rounds, trigger, receiver, bossName, custom));
                    Logger.Information("Duel {0} | Slot {1} | {2} waits {3} rounds before: {4}.", Logger.Args(_duel.m_duelID.Full,
                        boss.SlotIndex, bossName, rounds, string.Join(", ", later.Select(StepText))));
                }

                break;
            }

            // IfCleared: the next Count steps (1 if 0) only if the removals before it took off at least Amount (1 if 0)
            // effects; with Each, once for each one taken off ("Clear a DoT for a +55% Storm blade", Reap the Whirlwind).
            if (step.Is("IfCleared")) {
                var next = steps.Skip(s + 1).Take(Math.Max(1, step.Count)).ToList();
                var cleared = _clearedCount;
                var times = step.Each ? cleared : cleared >= Math.Max(1, step.Amount) ? 1 : 0;
                Logger.Information("Duel {0} | Slot {1} | {2}: {3} effects were cleared, so the next {4} steps go {5} times.",
                    Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, cleared, next.Count, times));
                if (times != 1) {
                    for (var t = 0; t < times && boss.IsAlive; t++) {
                        cinematicTime += RunCheatStepsInOrder(boss, cheat, next, trigger, receiver, bossName, custom, combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }

                    _clearedCount = cleared;
                    s += next.Count;
                }

                continue;
            }

            // Repeat and IfVar: the next Count steps go Times times, or only if a variable says so (CreativeCheats).
            if (step.Is("Repeat") || step.Is("IfVar")) {
                var next = steps.Skip(s + 1).Take(Math.Max(1, step.Count)).ToList();
                var times = step.Is("Repeat") ? Math.Clamp(step.Times, 0, 20) : VarHolds(boss, step) ? 1 : 0;
                if (times != 1) {
                    for (var t = 0; t < times && boss.IsAlive; t++) {
                        cinematicTime += RunCheatStepsInOrder(boss, cheat, next, trigger, receiver, bossName, custom, combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }

                    s += next.Count;
                }

                continue;
            }

            switch (step.Do.ToLowerInvariant()) {
                case "form" or "sethealth":
                    break;
                case "firstturn":
                    // Swap: the side that goes first now goes second (Dimension Shift).
                    SetFirstSide(boss, step.Side.Equals("Swap", StringComparison.OrdinalIgnoreCase)
                        ? _duel.m_firstTeamToAct != (int) boss.OccupiedTeam
                        : !step.Side.Equals("Them", StringComparison.OrdinalIgnoreCase));
                    break;
                case "onlyschool": {
                    // The next Count steps (1 if 0) only if the target (the same as the step before, if none is
                    // picked) is a wizard or creature of School.
                    var who = StepTargets(boss, step.Target.Length > 0 ? step.Target : "Last", null, trigger, receiver);
                    var school = (step.School ?? "").Trim();
                    if (!who.Any(t => school.Equals(t?.SchoolNow.ToString(), StringComparison.OrdinalIgnoreCase))) {
                        s += Math.Max(1, step.Count);
                    }

                    break;
                }
                case "castby": {
                    // Someone else on its side casts it: one of Creature's template, or an ally as By says.
                    var by = CheatCaster(boss, step);
                    if (by is null) {
                        Logger.Information("Duel {0} | Slot {1} | {2}: no one on its side to cast {3} ({4}).", Logger.Args(_duel.m_duelID.Full,
                            boss.SlotIndex, bossName, step.Spell.Length > 0 ? step.Spell : step.SpellID.ToString(),
                            step.By.Length > 0 ? step.By : $"creature {step.Creature}"));
                        break;
                    }

                    var byLines = new List<string>();
                    if (lines.Count > 0 && by != boss) {
                        // The cheat's line shows over the boss before the other one's spell.
                        foreach (var line in lines) {
                            cinematicTime += ShowBossLine(line, boss, combatActionList);
                        }

                        lines.Clear();
                    }

                    cinematicTime += CheatCast(by, cheat, step with { Do = "Cast" }, trigger, receiver, bossName, by == boss ? lines : byLines,
                        combatActionList, cinematicTimeSoFar + cinematicTime, custom);
                    break;
                }
                case "setphase" when step.Phase > 0:
                    // Its health cheats of that phase can go off again (a cycle that comes back to a phase starts over).
                    RearmHealthCheats(boss, step.Phase);
                    // The new phase counts its rounds from the next round.
                    _bossPhases[boss] = (step.Phase, (int) _duel.m_roundNum + 1);
                    Logger.Information("Duel {0} | Slot {1} | Boss moves on to phase {2}.",
                        Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, step.Phase));
                    break;
                case "bubble":
                    // Any words in a chat bubble over the speaker (Niduus, 2026-10-26: "custom boss words that appear over
                    // me and in combat"). The game shows only its own lines as a boss line, so these go as local chat from
                    // the speaker, timed for when the round gets to this point.
                    SpeakBubble(boss, step, trigger, receiver, cinematicTimeSoFar + cinematicTime);
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
                        // {boss}, {player}, {round} and a cheat variable's name ({rage}) are filled in (Niduus, 2026-10-26).
                        var said = AnnounceText(step.Text, boss, trigger);
                        // A player cheat's words go to that wizard's own chat.
                        if (boss.IsHumanPlayer) {
                            boss.ParticipantActor?.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = Tools.ServerMessages.Own(said), Modal = 0 });
                        }
                        else {
                            TellPlayers($"{bossName}: {said}");
                        }
                    }

                    break;
                case "freecast":
                    cinematicTime += QueuePlayerCast(boss, step, trigger, receiver, combatActionList, cinematicTimeSoFar + cinematicTime);
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
                        _clearedCount += RemoveHanging(target, step.Is("RemoveCharms") ? s_charmEffects : s_wardEffects, step.Kind, step.Count, step.Spell);
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
        // The game's own name too, when the id is of a spell of another name (Niduus's log: "free cast Brace" was
        // Adv-Titan's Trident-Max Damage Aura, whose card is also called Brace).
        try {
            if (step.SpellID != 0 && step.Spell.Length > 0 && CoreObjectFactory.GetCoreTemplate(step.SpellID) is SpellTemplate { m_name: var gameName }
                && !string.Equals((string) gameName, step.Spell, StringComparison.OrdinalIgnoreCase)) {
                spell = $"{step.Spell} ({(string) gameName}, {step.SpellID})";
            }
        }
        catch (Exception) {
            // Only for the log.
        }

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
            "firstturn" => step.Side.Equals("Them", StringComparison.OrdinalIgnoreCase) ? "they go first" : "we go first",
            "sethealth" => $"health {step.Amount}",
            "reflect" => "reflect the spell",
            "stealpips" => $"steal {step.Amount} {step.Kind} pips{on}",
            "removeauras" => $"remove auras{on}",
            "announce" => $"announce \"{step.Text}\" to the battle ({(step.Style.Length > 0 ? step.Style : "Box")}{(step.Who.Length > 0 ? $", {step.Who}" : "")})",
            "removeeffects" => $"remove {(step.Kind.Length > 0 ? step.Kind : "all")} effects x{(step.Count > 0 ? step.Count : "all")}{(step.Bypass ? " (even protected)" : "")}{on}",
            "aura" => $"put an aura of {step.AuraStats}{on}",
            "addcharm" when CheatLookalikes.IsGlobal(step.Kind)
                => $"put up a {(step.Kind.Equals("GloomGlobal", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(step.Amount) : Math.Abs(step.Amount))}% {SchoolText(step.School)}{CheatLookalikes.KindWord(step.Kind)}{(step.Look.Length > 0 && step.Spell.Length == 0 ? $" ({step.Look} cast)" : "")}",
            "addcharm" => step.Kind.ToLowerInvariant() is "dot" or "hot"
                ? $"add a {step.Amount} {SchoolText(step.School)}{step.Kind} over {Math.Max(1, step.Rounds)} rounds{on}"
                : $"add a {step.Amount}{(step.Kind.Equals("Absorb", StringComparison.OrdinalIgnoreCase) ? "" : "%")} {SchoolText(step.School)}{step.Kind}{on}{(step.Look.Length > 0 && step.Spell.Length == 0 ? $" ({step.Look} cast)" : "")}",
            "ifcleared" => step.Each ? $"the next {Math.Max(1, step.Count)} steps once for each effect cleared"
                : $"the next {Math.Max(1, step.Count)} steps only if {Math.Max(1, step.Amount)}+ effects were cleared",
            "boost" => $"{step.Kind} {(step.Amount >= 0 ? "+" : "")}{step.Amount}% {SchoolText(step.School)}for {RoundsText(step.Rounds)}{on}",
            "immune" => $"immune to {SchoolText(step.School)}damage for {RoundsText(step.Rounds)}{on}",
            "stun" => $"stun for {Math.Max(1, step.Rounds)} rounds{on}",
            "healthto" => $"health to {step.Amount}%{on}",
            "damagecap" => step.Amount > 0 ? $"damage capped at {step.Amount} for {RoundsText(step.Rounds)}{on}" : $"damage caps off{on}",
            "swaphealth" => $"swap health{on}",
            "freecast" => $"free cast {spell}{on} x{Math.Max(1, step.Times)} ({(step.Timing.Length > 0 ? step.Timing : "Now")}"
                          + $"{(step.Silent ? ", silent" : "")}{(step.Enchant ? ", enchanted" : "")}{(step.OnTarget ? ", self spells on the target" : "")})",
            _ => step.Do.ToLowerInvariant(),
        };
    }

    // "Continue until cast" stops after this many tries, in case dispels keep coming.
    private const int MaxDispelTries = 20;

    // A cheat's spell: free, never fizzles, works through stun. A Dispel on the boss of its school stops it, as it
    // stops a player's spell; with DispelThen "Retry" (or the old DispelImmune) it is cast again.
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
        // A "select your targets" spell with a Pick: one cast, on the picked ones.
        var picks = CheatPicks(boss, step, template, trigger, receiver);
        if (picks is { Count: 0 }) {
            return 0;
        }

        foreach (var target in picks is not null ? [picks[0]] : StepTargets(boss, aim, template, trigger, receiver)) {
            if (!boss.IsAlive || target is null || !target.IsAlive || !target.AddedToDuel) {
                continue;
            }

            // A lasting polymorph on a boss an afterlife brought back this round: the game still has it defeated, so the
            // cast hid it (the spell is casterInvisible) and the model never came back (Niduus, 2026-10-22). It is sent
            // first thing next round instead, when the game has the boss standing. The server takes the form only then
            // too: told the boss was already changed, the game hid him for the cast and never showed the new model
            // (0.10.159). The cast goes before anyone else's, and its heal brings the new form to full health.
            if (lasting is not null && _cameBackThisRound.Contains(target)) {
                _polymorphReplays[target] = (spell, template);
                Logger.Information("Duel {0} | Slot {1} | {2} is sent first thing next round, after the game brings it back.",
                    Logger.Args(_duel.m_duelID.Full, target.SlotIndex, (string) template.m_name));

                continue;
            }

            boss._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
            var before = combatActionList.m_actionList.Count;
            var action = new QueuedCombatAction {
                SpellCaster = boss, SelectedTarget = target, Spell = spell, SpellTemplate = template, PickedTargets = picks, PicksChosen = picks is not null,
            };
            float time;
            // Dispel-proof is gone (Niduus, 2026-10-23): a spell that was dispel-proof (DispelImmune, the old default)
            // retries until cast instead, as the app's "if dispelled" block shows it.
            var retry = string.Equals(cheat.DispelThen, "Retry", StringComparison.OrdinalIgnoreCase);
            var legacyProof = !retry && string.IsNullOrEmpty(cheat.DispelThen) && step.DispelImmune;
            if (retry || legacyProof) {
                // If dispelled, retry: the fizzle plays, then the spell is cast again.
                var tries = cheat.RetryUntilCast || legacyProof ? MaxDispelTries : 1 + Math.Max(0, cheat.DispelRetries);
                time = 0;
                for (var attempt = 1; ; attempt++) {
                    if (!ConsumeDispell(boss, spell.m_magicSchoolID)) {
                        time += HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);

                        break;
                    }

                    time += HandleFizzleAction(action, combatActionList);
                    Logger.Information("Duel {0} | Slot {1} | {2} was dispelled (try {3} of {4}).",
                        Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, (string) template.m_name, attempt, tries));
                    if (attempt >= tries) {
                        break;
                    }

                    action = new QueuedCombatAction {
                        SpellCaster = boss, SelectedTarget = target, Spell = spell, SpellTemplate = template, PickedTargets = picks, PicksChosen = picks is not null,
                    };
                }
            }
            else {
                time = ConsumeDispell(boss, spell.m_magicSchoolID)
                    ? HandleFizzleAction(action, combatActionList)
                    : HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
            }

            ShowFree(spell, template, combatActionList, before);
            // A wizard's cheat cast is in no hand, and the game skips a wizard's spell it can't find there ("does not
            // spell in hand"), so the cast never played: Niduus's AddCharm Shadow blade (0.10.338) showed no cast and no
            // blade, only the round's wait. It goes into the hand the game is sent before the round, as a pet's May Cast.
            if (boss.IsHumanPlayer) {
                PutCastsInHand(boss, combatActionList, before);
            }

            // Silent: the game is told not to show the cast, like .c1s, so the wait is shorter too.
            if (step.Silent) {
                for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                    combatActionList.m_actionList[i].m_showCast = false;
                }

                time = Math.Min(time, SkipCastSeconds);
            }

            cinematicTime += time;
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

    // The game takes a card's pips from its rank, not from the cost the server sends, so a cheat's spell showed the
    // boss losing pips (Niduus, 2026-10-22). The copy the game plays has the rank taken off, as with a pet's May Cast.
    private void ShowFree(Spell spell, SpellTemplate template, CombatActionListObj combatActionList, int before) {
        var rank = template?.m_spellRank?.m_spellRank ?? 0;
        if (rank <= 0) {
            return;
        }

        var free = spell with { m_regularAdjust = spell.m_regularAdjust - rank };
        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
            if (ReferenceEquals(combatActionList.m_actionList[i].m_spell, spell)) {
                combatActionList.m_actionList[i].m_spell = free;
            }
        }
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
        // A Spell typed as its template ID ("857718733") is that spell (Niduus, 2026-10-26: "Do blocks accept spell ID's?").
        if (id == 0 && SpellNameFamily.IsSpellId(name, out var typed)) {
            id = typed;
        }

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
    // Several spells may be named, with commas between, and each by its name or its template ID (Niduus, 2026-10-26).
    private static bool FromSpell(SpellEffect effect, string family) {
        if (string.IsNullOrWhiteSpace(family)) {
            return true;
        }

        var wanted = family.Contains(',') ? family.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [family];
        if (wanted.Any(w => SpellNameFamily.IsSpellId(w, out var id) && id == effect.m_spellTemplateID)) {
            return true;
        }

        if (CoreObjectFactory.GetCoreTemplate(effect.m_spellTemplateID) is not SpellTemplate template) {
            return false;
        }

        string name = template.m_name;
        string key = template.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return wanted.Any(w => SpellNameFamily.AnyInFamily(w, name, shown));
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
    // Returns how many came off (for IfCleared).
    private int RemoveHanging(CombatDuelSubCircle target, HashSet<kSpellEffects> kinds, string kind, int count, string fromSpell = "") {
        if (target?._hangingEffects is not { } hanging) {
            return 0;
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

        return gone.Count;
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

        // A school's pips (Kind Fire...: Crash the Bandit Coot spends his two matching pips; CombatResolver.MoreCheats.cs).
        if (ChangeSchoolPips(target, kind, amount) is { } changed) {
            return changed;
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

    // Say's Text: the words go to every player's chat. They are the cheat's own words, so the Announcements tab leaves them.
    private void SpeakBubble(CombatDuelSubCircle boss, BossCheats.CheatAction step, CombatDuelSubCircle trigger, CombatDuelSubCircle receiver,
                             float afterSeconds) {
        var said = AnnounceText(step.Text, boss, trigger);
        if (said.Length == 0) {
            return;
        }

        var speakers = StepTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)
            .Where(c => c?.ParticipantObject is not null).ToList();
        var players = _subCircles.Where(c => c is not null && c.Occupied && c.IsHumanPlayer && c.ParticipantActor is not null)
            .Select(c => c.ParticipantActor).ToList();
        foreach (var speaker in speakers) {
            var message = new GAME_5_PROTOCOL.MSG_RADIALCHAT {
                Message = (Imcodec.IO.ByteString) said, SourceID = speaker.ParticipantObject.m_globalID, SourceName = (Imcodec.IO.ByteString) FighterName(speaker), Filter = 2,
            };
            var delay = TimeSpan.FromSeconds(Math.Max(0, afterSeconds));
            _ = System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => {
                foreach (var player in players) {
                    player.Tell(message);
                }
            });
            Logger.Information("Duel {0} | Slot {1} says \"{2}\" in a bubble, {3:0.0} s into the round's play.",
                Logger.Args(_duel.m_duelID.Full, speaker.SlotIndex, said, delay.TotalSeconds));
        }
    }

    private void TellPlayers(string text) {
        Tools.ServerMessages.Own(text);
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

    /// <summary>
    /// A Cast's or FreeCast's picks for a "select your targets" spell (Niduus, 2026-10-26): whom its Pick names, Count of
    /// them (0: all). WeakestEnemy, StrongestEnemy, WeakestAlly, StrongestAlly, MostPips and Enemy (at random) put that
    /// many in that order ("the 2 weakest", "2 at random"); any other Target (Marked, AllEnemies, EveryoneElse, e1...)
    /// names its own, the first Count of them. Only those on the side the spell picks from count. Null: no Pick, or not a
    /// spell that picks (it goes as before); empty: no one to pick.
    /// </summary>
    private List<CombatDuelSubCircle> CheatPicks(CombatDuelSubCircle boss, BossCheats.CheatAction step, SpellTemplate template,
                                                 CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var pick = (step.Pick ?? "").Trim();
        if (pick.Length == 0 || template is null || !MultiPicks.IsPickSpell(template)) {
            return null;
        }

        var friendly = !MultiPicks.PicksEnemies(template);
        var standing = boss._duelActor.ActiveSubCircles.Where(c => c is not null && c.IsAlive && c.AddedToDuel).ToList();
        var enemies = standing.Where(c => c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var allies = standing.Where(c => c.OccupiedTeam == boss.OccupiedTeam).ToList();
        var code = pick.ToLowerInvariant();
        var found = code switch {
            "weakestenemy" => [.. enemies.OrderBy(c => c.ParticipantGameStats.m_currentHitpoints)],
            "strongestenemy" => [.. enemies.OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints)],
            "weakestally" => [.. allies.OrderBy(HealthShare)],
            "strongestally" => [.. allies.OrderByDescending(MostHealth)],
            "mostpips" => [.. enemies.OrderByDescending(c => PipCount(c))],
            "enemy" or "random" => [.. enemies.OrderBy(_ => Random.Shared.Next())],
            "ally" => [.. allies.OrderBy(_ => Random.Shared.Next())],
            _ => StepTargets(boss, pick, template, trigger, receiver),
        };
        var side = found.Where(c => c is not null && c.IsAlive && c.AddedToDuel && (c.OccupiedTeam == boss.OccupiedTeam) == friendly).Distinct().ToList();
        var most = Math.Min(MultiPicks.MaxPicks(template), step.Count > 0 ? step.Count : int.MaxValue);
        var picks = side.Take(most).ToList();
        Logger.Information("Duel {0} | Slot {1} | Cheat {2} picks {3}: {4}.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, (string) template.m_name,
            pick + (step.Count > 0 ? $" x{step.Count}" : ""), picks.Count == 0 ? "no one" : string.Join(", ", picks.Select(c => c.SlotIndex))));

        return picks;
    }

    // Who a step works on. Empty: as the spell aims (or the player who set it off, for a spell on enemies).
    private List<CombatDuelSubCircle> StepTargets(CombatDuelSubCircle boss, string aim, SpellTemplate template,
                                                  CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var enemies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var allies = boss._duelActor.ActiveSubCircles
            .Where(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam == boss.OccupiedTeam).ToList();
        var others = allies.Where(c => c != boss).ToList();
        var code = (aim ?? "").Trim().ToLowerInvariant();
        // The same as the step before (Imogen Nightgrove: a random wizard, then the Brace of that wizard's school).
        if (code == "last") {
            return [.. _lastStepTargets.Where(c => c is not null && c.IsAlive && c.AddedToDuel)];
        }

        // The player cheats' circles (p1-p4: its own side's, e1-e4: the other side's, as .c1 numbers them).
        if (FreeTargetCircle(boss, code) is { } circle) {
            return _lastStepTargets = circle.IsAlive && circle.AddedToDuel ? [circle] : [];
        }

        // A step on itself does not count as "the block before": a Brace on itself after a spell on a wizard still
        // lets the next block look at that wizard.
        var found = TargetsFor(code, boss, template, trigger, receiver, enemies, allies, others);
        if (code is not ("self" or "me")) {
            _lastStepTargets = found;
        }

        return found;
    }

    private List<CombatDuelSubCircle> TargetsFor(string code, CombatDuelSubCircle boss, SpellTemplate template, CombatDuelSubCircle trigger,
                                                 CombatDuelSubCircle receiver, List<CombatDuelSubCircle> enemies,
                                                 List<CombatDuelSubCircle> allies, List<CombatDuelSubCircle> others) {
        return code switch {
            // Whoever that spell was aimed at, on either side (the ally hit, for AllyHit).
            "hit" when receiver is not null => receiver.IsAlive ? [receiver] : [],
            // Everyone standing who has the step's (or the cheat's) mark.
            "marked" => [.. enemies.Concat(allies).Where(c => HasMark(c, _stepMark))],
            "self" or "me" => [boss],
            // Whose cheat it is (for a CastBy, the one whose file it is, not the one casting).
            "owner" => _cheatOwner is { IsAlive: true } owner ? [owner] : [boss],
            // Whom the If "has" found (AnyEnemy / AnyAlly), else the player who set it off (the one with the pips).
            "matched" => _cheatMatched is { IsAlive: true } matched ? [matched]
                : trigger is { IsAlive: true } && trigger.OccupiedTeam != boss.OccupiedTeam ? [trigger] : [],
            // Everyone standing on the other side without the step's (or the cheat's) mark.
            "unmarked" => [.. enemies.Where(c => !HasMark(c, _stepMark))],
            // The one on the other side with the most pips.
            "mostpips" when enemies.Count > 0 => [enemies.OrderByDescending(c => PipCount(c)).First()],
            // The one who summoned it into the battle (a minion's summoner), while it stands; else no one.
            "summoner" => Summoner(boss) is { } summoner ? [summoner] : [],
            // The last one on the other side whose spell hurt it; no one when that one has fallen.
            "lastattacker" => _lastAttackers.TryGetValue(boss, out var last) && enemies.Contains(last) ? [last] : [],
            "weakestally" => [others.Count > 0 ? others.OrderBy(HealthShare).First() : boss],
            "strongestally" => [others.Count > 0 ? others.OrderByDescending(MostHealth).First() : boss],
            "caster" or "attacker" when trigger is not null => [trigger],
            // A spell on a teammate is taken from that teammate; one on many (or on an enemy) from the caster.
            "receiver" when receiver is not null && receiver.OccupiedTeam != boss.OccupiedTeam => [receiver],
            "receiver" when trigger is not null => [trigger],
            "hit" when trigger is not null => [trigger],
            "hit" => [],
            "firstenemy" when enemies.Count > 0 => [enemies.OrderBy(c => c.SlotIndex).First()],
            "weakestenemy" when enemies.Count > 0 => [enemies.OrderBy(c => c.ParticipantGameStats.m_currentHitpoints).First()],
            "enemy" or "caster" or "attacker" when enemies.Count > 0 => [enemies[Random.Shared.Next(enemies.Count)]],
            "allenemies" or "all" => enemies,
            "ally" => [others.Count > 0 ? others[Random.Shared.Next(others.Count)] : boss],
            "allallies" => allies,
            // Niduus, 2026-10-26: both sides standing, with or without itself.
            "everyone" => [.. allies.Concat(enemies).OrderBy(c => c.SlotIndex)],
            "everyoneelse" => [.. others.Concat(enemies).OrderBy(c => c.SlotIndex)],
            _ when template is null => [boss],
            _ => [trigger is not null && trigger.OccupiedTeam != boss.OccupiedTeam
                  && Flatten(template).Any(e => s_enemyTargets.Contains(e.m_effectTarget))
                ? trigger
                : PickFreeTarget(boss, template, null)],
        };
    }

}
