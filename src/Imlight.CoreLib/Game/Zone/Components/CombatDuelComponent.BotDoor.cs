/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 */

using System;
using Akka.Actor;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The battle's side of the doorway to the bots (BotDoor). The bots' own battle work (their picks, orders and seats) lives in
/// the bots folder (bots/Battle) and keeps its state for this battle in <see cref="BotState"/>; the battle only hands on what
/// happens and the messages that are the bots' own.
/// </summary>
internal sealed partial class CombatDuelComponent {

    // This battle's own address, for bots sent to it from outside (BattleOf).
    private IActorRef _ref;

    /// <summary>What the bots keep about this battle (theirs alone; dropped when a new battle starts or the bots are swapped).</summary>
    internal object BotState { get; set; }

    /// <summary>The battle's own address.</summary>
    internal IActorRef Ref => _ref;

    /// <summary>The battle a wizard (by global id) stands in, or null.</summary>
    internal static IActorRef BattleOf(ulong globalId)
        => s_playerBattles.TryGetValue(globalId, out var claim) && claim.Battle.HoldsPlayer(globalId) ? claim.Battle._ref : null;

    /// <summary>Whether a wizard (by global id) stands in a battle right now.</summary>
    internal static bool IsInBattle(ulong globalId) => BattleOf(globalId) is not null;

    /// <summary>The battle (its component) a wizard (by global id) stands in, or null. For the bots' looks at a battle.</summary>
    internal static CombatDuelComponent DuelOf(ulong globalId)
        => s_playerBattles.TryGetValue(globalId, out var claim) && claim.Battle.HoldsPlayer(globalId) ? claim.Battle : null;

    /// <summary>A timer whose message only the bots understand (it comes back to them through <see cref="BotDoor"/>).</summary>
    internal void BotTimer(string key, object payload, TimeSpan after)
        => Timers.StartSingleTimer(key, new BotDoorMessage(payload, BotDoor.Generation), after);

    /// <summary>Stops a timer the bots started.</summary>
    internal void CancelBotTimer(string key) => Timers.Cancel(key);

    [MessageHandler(typeof(BotDoorMessage))]
    private void ReceiveBotDoorMessage(BotDoorMessage message) {
        // A message from bots that were swapped out since is dropped: the new bots plan again on their own.
        if (message.Generation == BotDoor.Generation) {
            BotDoor.Side?.DuelMessage(this, message.Payload);
        }
    }

}
