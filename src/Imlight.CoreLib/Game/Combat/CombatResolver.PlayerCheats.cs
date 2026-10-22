/*
 * Player cheats in a round (Niduus, 2026-10-22): the cheats of spiraldb-extra/PlayerCheats.json (see PlayerCheats.cs)
 * for the wizard played on the server's own PC. Their spells are the .c1 / .c2 free casts (AddFreeDelayedCast), put in
 * the queue the cheat's Timing names; "Now" casts at once through its own queue ('x').
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // The queue of a player cheat's "Now" spells: cast as soon as they are queued.
    private const char NOW_QUEUE = 'x';

    private readonly Dictionary<(CombatDuelSubCircle Me, BossCheats.BossCheat Cheat), (int Times, int Round)> _playerCheatCounts = [];
    private readonly HashSet<CombatDuelSubCircle> _movedThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, int> _playerFirstRound = [];

    private List<CombatDuelSubCircle> LocalPlayers()
        => [.. _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive && PlayerCheats.IsLocal(c))];

    // The player's own round, from 1 (one who joins later counts from their first round).
    private int PlayerRound(CombatDuelSubCircle me) {
        if (!_playerFirstRound.TryGetValue(me, out var first)) {
            _playerFirstRound[me] = first = (int) _duel.m_roundNum;
        }

        return (int) _duel.m_roundNum - first + 1;
    }

    private static int PipsOf(CombatDuelSubCircle c) => c.CombatParticipant?.m_pipCount is { } pips
        ? CombatDuelSubCircle.TotalPips(pips) + pips.m_shadowPips : 0;

    private float PlayerCheatsAtRound(bool atEnd, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            if (!atEnd) {
                _movedThisRound.Clear();
            }

            var cheats = PlayerCheats.Current();
            if (cheats.Count == 0) {
                return 0;
            }

            foreach (var me in LocalPlayers()) {
                var round = PlayerRound(me);
                foreach (var cheat in cheats) {
                    var due = atEnd ? cheat.Is("RoundEnd") && DueIn(cheat, round)
                        : cheat.Is("BattleStart") ? round == 1
                        : cheat.Is("Pips") ? cheat.Pips > 0 && PipsOf(me) >= cheat.Pips
                        : cheat.Is("FromRound") ? round >= Math.Max(1, cheat.Round)
                                                  && (cheat.Every <= 1 || (round - Math.Max(1, cheat.Round)) % cheat.Every == 0)
                        : cheat.Is("RoundStart") && DueIn(cheat, round);
                    if (due) {
                        cinematicTime += RunPlayerCheat(me, cheat, $"{(atEnd ? "end of " : "")}round {round}", ownCard: false,
                            roundStart: !atEnd, combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += MyHealthCheats(cheats, combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Player cheats at round {1} failed: {2}", Logger.Args(_duel.m_duelID.Full, atEnd ? "end" : "start", e));
        }

        return cinematicTime;
    }

    // ICast (my spell) and EnemyCasts (an enemy's spell on my battle), before the spell lands.
    private float PlayerCheatsBefore(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            if (action?.SpellCaster is not { } mover || action.Spell is null || action.SelectedTarget is null || !mover.IsAlive
                || mover.CombatParticipant is null || mover.CombatParticipant.m_stunned > 0) {
                return 0;
            }

            var cheats = PlayerCheats.Current();
            if (cheats.Count == 0) {
                return 0;
            }

            var spell = action.SpellTemplate ?? CoreObjectFactory.GetCoreTemplate(action.Spell.m_templateID) as SpellTemplate;
            if (spell is null) {
                return 0;
            }

            var aimed = AimedAt(mover, action.SelectedTarget, spell);
            foreach (var me in LocalPlayers()) {
                var mine = me == mover;
                if (!mine && mover.OccupiedTeam == me.OccupiedTeam) {
                    continue;
                }

                foreach (var cheat in cheats.Where(c => mine ? c.Is("ICast") : c.Is("EnemyCasts"))) {
                    if (Matches(cheat, spell, aimed, me, mover)) {
                        cinematicTime += RunPlayerCheat(me, cheat, $"{(mine ? "my" : $"slot {mover.SlotIndex}'s")} {(string) spell.m_name}",
                            ownCard: mine, roundStart: false, combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Player cheats before a move failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }

        return cinematicTime;
    }

    // IPass and ICrit, after my move; MyHealthBelow after anyone's.
    private float PlayerCheatsAfter(CombatDuelSubCircle mover, int before, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            if (mover is null) {
                return 0;
            }

            _movedThisRound.Add(mover);
            var cheats = PlayerCheats.Current();
            if (cheats.Count == 0) {
                return 0;
            }

            if (PlayerCheats.IsLocal(mover) && mover.IsAlive) {
                var moves = combatActionList.m_actionList.Skip(before).Where(a => a.m_spellCaster == mover.SlotIndex).ToList();
                var passed = moves.Count > 0 && moves.All(a => a.m_spell is null);
                var crits = moves.Where(a => a.m_spell is not null && a.m_spellHits == (char) 2)
                    .Select(a => (Action: a, Spell: CoreObjectFactory.GetCoreTemplate(a.m_spell.m_templateID) as SpellTemplate))
                    .Where(m => m.Spell is not null)
                    .ToList();
                foreach (var cheat in cheats) {
                    if (cheat.Is("IPass") && passed) {
                        cinematicTime += RunPlayerCheat(mover, cheat, "my pass", ownCard: false, roundStart: false, combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                    else if (cheat.Is("ICrit") && crits.FirstOrDefault(m => Matches(cheat, m.Spell, m.Action.m_targetSubcircleList ?? [], mover, mover))
                                 is { Spell: not null } crit) {
                        cinematicTime += RunPlayerCheat(mover, cheat, $"my critical {(string) crit.Spell.m_name}", ownCard: false,
                            roundStart: false, combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += MyHealthCheats(cheats, combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Player cheats after slot {1}'s move failed: {2}", Logger.Args(_duel.m_duelID.Full, mover?.SlotIndex, e));
        }

        return cinematicTime;
    }

    // MyHealthBelow: once a battle, as my health first drops under the mark.
    private float MyHealthCheats(List<BossCheats.BossCheat> cheats, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        foreach (var me in LocalPlayers()) {
            var most = MostHealth(me);
            var health = me.ParticipantGameStats.m_currentHitpoints;
            foreach (var cheat in cheats.Where(c => c.Is("MyHealthBelow"))) {
                var under = cheat.Health > 0 ? health < cheat.Health : most > 0 && health * 100.0 / most < cheat.Percent;
                if (under && _playerCheatCounts.GetValueOrDefault((me, cheat)).Times == 0) {
                    cinematicTime += RunPlayerCheat(me, cheat, $"my health dropping to {health}", ownCard: false, roundStart: false,
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    /// <summary>One player cheat: its limits, then its steps.</summary>
    /// <param name="ownCard">It answers my own card, about to be cast (ICast): Before then casts at once.</param>
    /// <param name="roundStart">It goes off as the round starts, before anyone's move: First then casts at once.</param>
    private float RunPlayerCheat(CombatDuelSubCircle me, BossCheats.BossCheat cheat, string reason, bool ownCard, bool roundStart,
                                 CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var key = (me, cheat);
        var (times, last) = _playerCheatCounts.GetValueOrDefault(key);
        var round = (int) _duel.m_roundNum;
        var most = MostHealth(me);
        var percent = most > 0 ? me.ParticipantGameStats.m_currentHitpoints * 100.0 / most : 0;
        if ((cheat.Once && times > 0) || (cheat.MaxTimes > 0 && times >= cheat.MaxTimes)
            || (cheat.Cooldown > 0 && times > 0 && round - last <= cheat.Cooldown)
            || (cheat.FromRound > 0 && round < cheat.FromRound) || (cheat.ToRound > 0 && round > cheat.ToRound)
            || (cheat.HealthAbove > 0 && percent <= cheat.HealthAbove) || (cheat.HealthUnder > 0 && percent >= cheat.HealthUnder)
            || BattleDecided() || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        _playerCheatCounts[key] = (times + 1, round);
        var steps = PickSteps(cheat.Steps());
        string wizard = me._wizard?.PlayerNameBehavior?.GetWizardName() ?? "";
        Logger.Information("Custom cheat | Duel {0} | player cheat of {1} (slot {2}) after {3}: {4}.",
            Logger.Args(_duel.m_duelID.Full, wizard.Length > 0 ? wizard : "the local wizard", me.SlotIndex, reason,
                steps.Count == 0 ? "does nothing" : string.Join(", ", steps.Select(StepText))));

        var cinematicTime = 0.0f;
        foreach (var step in steps) {
            try {
                switch (step.Do.ToLowerInvariant()) {
                    case "freecast" or "cast":
                        cinematicTime += QueuePlayerCast(me, step, ownCard, roundStart, combatActionList, cinematicTimeSoFar + cinematicTime);
                        break;
                    case "pips":
                        ChangePips(me, step.Kind, step.Amount);
                        break;
                    case "heal":
                        HealFromCheat(me, step.Percent ? MostHealth(me) * step.Amount / 100 : step.Amount);
                        break;
                    case "say" or "banner":
                        if (step.Line.Length > 0) {
                            if (CommandAnnounceProtocol.TryResolve(step.Line, out var line, out _, out var problem)) {
                                cinematicTime += ShowBossLine(line, me, combatActionList);
                            }
                            else {
                                Logger.Warning("Duel {0} | Player cheat line \"{1}\" not found: {2}", Logger.Args(_duel.m_duelID.Full, step.Line, problem));
                            }
                        }

                        if (step.Text.Length > 0) {
                            me.ParticipantActor?.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = step.Text, Modal = 0 });
                        }

                        break;
                    case "pickone":
                        break;
                    default:
                        Logger.Warning("Duel {0} | Player cheat: \"{1}\" is not something a player cheat can do.",
                            Logger.Args(_duel.m_duelID.Full, step.Do));
                        break;
                }
            }
            catch (Exception e) {
                Logger.Error("Duel {0} | Player cheat step {1} failed: {2}", Logger.Args(_duel.m_duelID.Full, step.Do, e));
            }
        }

        return cinematicTime;
    }

    // A boss-style target ("Self", "AllEnemies"...) as a .c1 target, so a cheat copied from a boss still aims sensibly.
    private static string FreeCastTarget(string target) => (target ?? "").Trim().ToLowerInvariant() switch {
        "" => null,
        "self" or "me" => "me",
        "firstenemy" => "e1",
        "allenemies" or "all" => "all",
        var code when code.Length == 2 && code[0] is 'p' or 'e' && code[1] is >= '1' and <= '4' => code,
        _ => null,
    };

    // FreeCast: Times free casts of the spell in the queue Timing names (see PlayerCheats.cs).
    private float QueuePlayerCast(CombatDuelSubCircle me, BossCheats.CheatAction step, bool ownCard, bool roundStart,
                                  CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var (spell, _) = FindSpell(step.Spell, step.SpellID);
        if (spell is null) {
            Logger.Warning("Duel {0} | Player cheat spell \"{1}\" not found.", Logger.Args(_duel.m_duelID.Full, step.Spell.Length > 0 ? step.Spell : step.SpellID));

            return 0;
        }

        var timing = (step.Timing ?? "").Trim().ToLowerInvariant();
        var (atStart, when) = timing switch {
            "first" when !roundStart => (true, 'f'),
            "last" => (true, 'l'),
            "before" when !ownCard => (true, 'n'),
            "after" => (false, 'n'),
            _ => (true, NOW_QUEUE),
        };
        var target = FreeCastTarget(step.Target);
        var count = Math.Clamp(step.Times, 1, 10);
        for (var i = 0; i < count; i++) {
            if (AddFreeDelayedCast(me, spell.m_templateID, atStart, out _, target, step.Silent, when, step.Enchant, step.Enchants) == 0) {
                return 0;
            }
        }

        if (when != NOW_QUEUE) {
            return 0;
        }

        var time = CastDelayed(me, atStart: true, combatActionList, cinematicTimeSoFar, NOW_QUEUE);
        // None of them waits for later (one that found no target is dropped).
        _delayedCasts.RemoveAll(d => d.When == NOW_QUEUE && d.Action.SpellCaster == me);

        return time;
    }

}
