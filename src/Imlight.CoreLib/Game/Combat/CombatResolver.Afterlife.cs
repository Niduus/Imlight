/*
 * Afterlife (Niduus, 2026-10-21): a kAfterlife effect waits on its target, and when the target is defeated its effects
 * bring them back. Malistaire the Undying's "Mega Taunt - Shadow" gives him one at the start of the battle (100 health),
 * and his "I touch Shadow" cheat then turns him into Haunted Malistaire.
 *
 * Wizards come back too (Niduus, 2026-10-22: a Guardian Spirit on his friend did not work, so the server had the friend
 * defeated while the game had him standing). A heal of a share of the most health (kHealPercent) counts as well as a
 * flat one, and an afterlife cast on a teammate who is already defeated brings them back at once.
 *
 * The game does the same on its own while it plays the round (CombatResolver::ProcessDeathPostAllEffects), so the server
 * only keeps its numbers in step: nothing is sent for it.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // Afterlife effects waiting on each participant, first given first.
    private readonly Dictionary<CombatDuelSubCircle, List<SpellEffect>> _afterlives = [];

    internal void AddAfterlife(IEnumerable<CombatDuelSubCircle> targets, SpellEffect effect) {
        // A defeated target is fine: the next ProcessAfterlives brings them back (a revive on a fallen teammate).
        foreach (var target in targets.Where(t => t is not null && t.Occupied)) {
            if (!_afterlives.TryGetValue(target, out var waiting)) {
                _afterlives[target] = waiting = [];
            }

            waiting.Add(effect);
        }
    }

    // Who an afterlife brought back this round. The game brings them back itself only once the round's effects are done,
    // so a lasting polymorph cast on them in the same round (Malistaire's "I touch Shadow", Afterlife - Malistaire 02)
    // finds them still defeated there and the look does not change (Niduus, 2026-10-22). The server takes the new form at
    // once (waiting a round let a last hit end the battle at 100 health, 0.10.150), and the game is sent the cast only
    // first thing next round, when it has them standing and swaps the model (CheatCast).
    private readonly HashSet<CombatDuelSubCircle> _cameBackThisRound = [];
    private readonly Dictionary<CombatDuelSubCircle, (Spell Spell, SpellTemplate Template)> _polymorphReplays = [];

    // At the start of a round: the polymorphs the game could not show last round, cast again on the same participant.
    private float ReplayPolymorphs(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        _cameBackThisRound.Clear();
        var cinematicTime = 0.0f;
        foreach (var (target, (spell, template)) in _polymorphReplays.ToList()) {
            _polymorphReplays.Remove(target);
            if (!target.Occupied || !target.IsAlive || !target.AddedToDuel || CombatPolymorph.FormOf(target) == 0) {
                continue;
            }

            try {
                target._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                var action = new QueuedCombatAction { SpellCaster = target, SelectedTarget = target, Spell = spell, SpellTemplate = template };
                cinematicTime += HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);
                Logger.Information("Duel {0} | Slot {1} | {2} sent now, so the game shows the new form.",
                    Logger.Args(_duel.m_duelID.Full, target.SlotIndex, (string) template.m_name));
            }
            catch (Exception e) {
                Logger.Error("Duel {0} | Slot {1} | Polymorph replay failed: {2}", Logger.Args(_duel.m_duelID.Full, target.SlotIndex, e));
            }
        }

        return cinematicTime;
    }

    // Brings back each defeated participant that has an afterlife waiting; each afterlife works once.
    private void ProcessAfterlives() {
        foreach (var (circle, waiting) in _afterlives.ToList()) {
            if (waiting.Count == 0 || !circle.Occupied || circle.IsAlive || circle.ParticipantGameStats is not { } stats) {
                continue;
            }

            var afterlife = waiting[0];
            waiting.RemoveAt(0);
            var effects = afterlife is EffectListSpellEffect list ? list.m_effectList ?? [] : [];
            // A wizard's server health already counts gear; a creature's most health is its base and bonus.
            var most = circle.IsHumanPlayer ? stats.m_baseHitpoints : stats.m_baseHitpoints + stats.m_bonusHitpoints;
            var heal = effects.Where(e => e is not null && e.m_effectType == kSpellEffects.kHeal).Sum(e => e.m_effectParam)
                       // Guardian Angel brings back with a share of the maximum health (kMaxHealthHeal), which was
                       // not counted, so it brought no one back.
                       + effects.Where(e => e is not null && e.m_effectType is kSpellEffects.kHealPercent or kSpellEffects.kMaxHealthHeal)
                           .Sum(e => (int) (most * (e.m_effectParam / 100.0)));
            if (heal <= 0) {
                continue;
            }

            circle.SetHealthTo(most > 0 ? Math.Min(heal, most) : heal);
            _cameBackThisRound.Add(circle);

            Logger.Information("Duel {0} | Slot {1} | Afterlife: back with {2} health.",
                Logger.Args(_duel.m_duelID.Full, circle.SlotIndex, stats.m_currentHitpoints));
        }
    }

}
