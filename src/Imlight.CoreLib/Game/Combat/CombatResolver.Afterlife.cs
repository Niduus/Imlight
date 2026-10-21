/*
 * Afterlife (Justin, 2026-10-21): a kAfterlife effect waits on its target, and when the target is defeated its effects
 * bring them back. Malistaire the Undying's "Mega Taunt - Shadow" gives him one at the start of the battle (100 health),
 * and his "I touch Shadow" cheat then turns him into Haunted Malistaire.
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
        foreach (var target in targets.Where(t => t is not null && t.IsAlive)) {
            if (!_afterlives.TryGetValue(target, out var waiting)) {
                _afterlives[target] = waiting = [];
            }

            waiting.Add(effect);
        }
    }

    // Brings back each defeated participant that has an afterlife waiting; each afterlife works once.
    private void ProcessAfterlives() {
        foreach (var (circle, waiting) in _afterlives.ToList()) {
            // A wizard's health is saved with the wizard; only creatures come back for now.
            if (waiting.Count == 0 || !circle.Occupied || circle.IsHumanPlayer || circle.IsAlive
                || circle.ParticipantGameStats is not { } stats) {
                continue;
            }

            var afterlife = waiting[0];
            waiting.RemoveAt(0);
            var effects = afterlife is EffectListSpellEffect list ? list.m_effectList ?? [] : [];
            var heal = effects.Where(e => e is not null && e.m_effectType == kSpellEffects.kHeal).Sum(e => e.m_effectParam);
            if (heal <= 0) {
                continue;
            }

            var most = stats.m_baseHitpoints + stats.m_bonusHitpoints;
            stats.m_currentHitpoints = most > 0 ? Math.Min(heal, most) : heal;
            if (circle.CombatParticipant is { } participant) {
                participant.m_playerHealth = stats.m_currentHitpoints;
            }

            Logger.Information("Duel {0} | Slot {1} | Afterlife: back with {2} health.",
                Logger.Args(_duel.m_duelID.Full, circle.SlotIndex, stats.m_currentHitpoints));
        }
    }

}
