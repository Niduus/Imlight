/*
 * A wizard's minion leaving on purpose (Niduus, 2026-11-12: "dismiss button is not working. Also when attempting to cast
 * another minion to replace the player's existing, it just forces the player to pass instead of cast").
 *
 * Dismiss   The game's Dismiss button on the wizard's own minion sends MSG_DISMISS_SUMMON (WizCombatMessages.xml,
 *           "Client requesting the server to dismiss a minion or henchmen from the combat"). The minion leaves the battle
 *           at once in planning, or once the round has played when the spells are going off. It costs no turn.
 * Replace   With [Minions] OnePerWizard on, a wizard who summons a minion while one of theirs stands sends the old one
 *           away as the new one comes in, into the old one's circle. With it off, both stay (when a circle is free).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    // Minions dismissed while the spells were going off: they leave once the round has played.
    private readonly List<CombatDuelSubCircle> _dismissLater = [];

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_DISMISSMINION))]
    private void ReceiveDismissMinion(COMBAT_106_PROTOCOL.MSG_DISMISSMINION message) {
        if (!_isActive) {
            return;
        }

        var owner = SubCircles.FirstOrDefault(c => c is not null && c.ParticipantActor == message.Actor);
        if (owner is null) {
            return;
        }

        var minion = DismissTarget(owner, message.Subcircle);
        if (minion is null) {
            Logger.Information("Duel {0} | Slot {1} | Dismiss: no minion of theirs in circle {2}.",
                Logger.Args(Duel.m_duelID.Full, owner.SlotIndex, message.Subcircle));

            return;
        }

        // While the round's spells play out the minion's own spell may still be on screen: it goes once they are done.
        if (Duel.m_duelPhase == kDuelPhase.kPhase_Execution) {
            if (!_dismissLater.Contains(minion)) {
                _dismissLater.Add(minion);
            }

            Logger.Information("Duel {0} | Slot {1} | Dismiss: the minion in slot {2} leaves once this round has played.",
                Logger.Args(Duel.m_duelID.Full, owner.SlotIndex, minion.SlotIndex));

            return;
        }

        SendMinionAway(minion, "dismissed by its wizard");
        EndPlanningIfAllMoved();
    }

    /// <summary>
    /// The owner's minion the Dismiss button means: the one in the circle the game named (as a circle number or as the
    /// circle's bit), else their only minion. Null when they have none.
    /// </summary>
    private CombatDuelSubCircle DismissTarget(CombatDuelSubCircle owner, uint subcircle) {
        var own = PlayerMinions.OwnMinions(owner).ToList();
        if (own.Count == 0) {
            return null;
        }

        var bit = subcircle > 0 && (subcircle & (subcircle - 1)) == 0 ? (int) Math.Log2(subcircle) : -1;

        return own.FirstOrDefault(m => m.SlotIndex == subcircle)
            ?? own.FirstOrDefault(m => m.SlotIndex == bit)
            ?? (own.Count == 1 ? own[0] : null);
    }

    /// <summary>
    /// Takes a minion out of the battle without a death: it is gone from the game's battle at once and its circle is free.
    /// </summary>
    private void SendMinionAway(CombatDuelSubCircle minion, string why) {
        if (minion?.ParticipantObject is not { } obj || !minion.IsSummonedMinion) {
            return;
        }

        var actor = minion.ParticipantActor;
        Logger.Information("Duel {0} | Slot {1} | Minion leaves: {2}.", Logger.Args(Duel.m_duelID.Full, minion.SlotIndex, why));

        CombatResolver?.DropQueuedAction(minion);
        DuelBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
            DuelID = SigilId,
            ParticipantID = obj.m_globalID,
        });
        ForgetParticipant(obj.m_globalID);
        minion.RemoveParticipant();
        _dismissLater.Remove(minion);

        // Its entity goes too (CombatCreatureAIComponent: the circle no longer holds it, so it just deletes itself).
        actor?.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());
    }

    // The minions dismissed during the round's spells, once the round has played.
    private void SendDismissedMinionsAway() {
        foreach (var minion in _dismissLater.ToList()) {
            SendMinionAway(minion, "dismissed by its wizard");
        }

        _dismissLater.Clear();
    }

    // A minion that left in planning may have been the last one the round waited for.
    private void EndPlanningIfAllMoved() {
        if (!_awaitingCombatMoves || CombatResolver is null || !CombatResolver.HaveAllParticipantsEnqueuedActions()) {
            return;
        }

        _awaitingCombatMoves = false;
        Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A wizard's new minion replaces theirs (PlayerMinions.OnePerWizard): the old ones leave, and the circle the first
    /// of them stood in is where the new one goes. Null when they had none.
    /// </summary>
    private CombatDuelSubCircle ReplaceOwnMinions(CombatDuelSubCircle caster) {
        if (!PlayerMinions.OnePerWizard || caster is null || !caster.IsHumanPlayer) {
            return null;
        }

        var old = PlayerMinions.OwnMinions(caster, summonedOnly: true).ToList();
        foreach (var minion in old) {
            SendMinionAway(minion, "its wizard summoned a new one");
        }

        return old.FirstOrDefault();
    }
}
