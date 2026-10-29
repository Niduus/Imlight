/*
 * Minions' own cheats and the battle's end (Niduus, 2026-10-29: "expand more on blocks again"). Each works in a player's
 * cheats and a creature's alike.
 *
 *   GiveCheat   Target (MyMinions: its minions) gets one of its cheats (Cheat: 1 is the first in its list) for the rest of
 *               the battle, as if that cheat were in the target's own list: a minion that heals its summoner when hit, a
 *               teammate who blades up at each round's start. Given twice, it is kept once.
 *   Target MyMinions   the minions it summoned that stand (a wizard's too).
 *   If Minions         Who (itself) has Op Value minions standing.
 *   When BattleEnd     the battle is over: Result Won or Lost for its own side ("" either). Its Dos that make sense
 *                      then go off (a reward, a chat line, a sound, a saved variable); anything that needs another round
 *                      does nothing.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // Cheats given to someone for the rest of the battle (GiveCheat), after their own.
    private readonly Dictionary<CombatDuelSubCircle, List<BossCheats.BossCheat>> _givenCheats = [];

    // Its own cheats with the ones it was given after them, so the given ones count from the end of its list.
    private List<BossCheats.BossCheat> WithGiven(CombatDuelSubCircle who, List<BossCheats.BossCheat> own)
        => _givenCheats.TryGetValue(who, out var given) && given.Count > 0 ? [.. own, .. given] : own;

    // The minions it summoned that stand.
    private List<CombatDuelSubCircle> MinionsOf(CombatDuelSubCircle owner)
        => owner is null ? []
            : [.. _subCircles.Where(c => c is not null && c != owner && c.Occupied && c.AddedToDuel && c.IsAlive && c.IsSummonedMinion
                                        && c.CombatParticipant?.m_minionSubCircle == owner.SlotIndex)];

    private bool GiveCheat(CombatDuelSubCircle boss, BossCheats.CheatAction step, List<CombatDuelSubCircle> targets, string bossName) {
        var own = BossesWithCheats(defeatedToo: true).FirstOrDefault(b => b.Boss == boss).Cheats ?? [];
        var at = step.Cheat - 1;
        if (at < 0 || at >= own.Count) {
            Logger.Information("Duel {0} | {1}: GiveCheat {2} is not one of its cheats.", Logger.Args(_duel.m_duelID.Full, bossName, step.Cheat));

            return true;
        }

        var cheat = own[at];
        foreach (var target in targets.Where(t => t is not null && t != boss && t.Occupied)) {
            if (!_givenCheats.TryGetValue(target, out var given)) {
                _givenCheats[target] = given = [];
            }

            if (!given.Contains(cheat)) {
                given.Add(cheat);
                Logger.Information("Duel {0} | Slot {1} | {2}: gives slot {3} its cheat {4}.",
                    Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName, target.SlotIndex, step.Cheat));
            }
        }

        return true;
    }

    /// <summary>
    /// The battle is over (CombatDuelComponent.EndDuel, before anyone leaves their circle): the BattleEnd cheats of
    /// everyone in it, standing or not. Winners: the side that won, or null when neither did.
    /// </summary>
    internal void BattleEndCheats(CombatTeam? winners) {
        if (winners is null) {
            return;
        }

        try {
            var list = new CombatActionListObj { m_actionList = [] };
            foreach (var (boss, cheats) in BossesWithCheats(defeatedToo: true).ToList()) {
                var won = boss.OccupiedTeam == winners.Value;
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    var result = (cheat.Result ?? "").Trim();
                    if (!cheat.Is("BattleEnd") || !InPhase(boss, cheat)
                        || (result.Equals("Won", StringComparison.OrdinalIgnoreCase) && !won)
                        || (result.Equals("Lost", StringComparison.OrdinalIgnoreCase) && won)) {
                        continue;
                    }

                    TryBossCheat(boss, i, cheat, boss, boss, won ? "the battle was won" : "the battle was lost", list, 0);
                }
            }
        }
        catch (Exception e) {
            Logger.Warning("Duel {0} | The battle's end cheats failed: {1}", Logger.Args(_duel.m_duelID.Full, e.Message));
        }
    }
}
