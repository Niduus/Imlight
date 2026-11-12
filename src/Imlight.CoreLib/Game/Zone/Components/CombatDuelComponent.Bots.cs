/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 */

using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Bot Players (Game/BotPlayers) in a battle. A bot stands on a players' circle as a wizard and counts as a player for
/// everything (battle size included); its body answers the battle's questions like a player's session would. In the test
/// stage a bot passes every round, at the start of the planning. A player can kick the bots out of their battle; after
/// that no bot joins it again (Niduus, 2026-11-12).
/// </summary>
internal sealed partial class CombatDuelComponent {

    // This battle's own address, for bots sent to it from outside (BattleOf).
    private IActorRef _ref;

    // A player kicked a bot out of this battle: no bot joins it again.
    private bool _botsKicked;

    /// <summary>The battle a wizard (by global id) stands in, or null.</summary>
    internal static IActorRef BattleOf(ulong globalId)
        => s_playerBattles.TryGetValue(globalId, out var claim) && claim.Battle.HoldsPlayer(globalId) ? claim.Battle._ref : null;

    /// <summary>Whether a wizard (by global id) stands in a battle right now.</summary>
    internal static bool IsInBattle(ulong globalId) => BattleOf(globalId) is not null;

    private static bool IsBot(CombatDuelSubCircle circle)
        => circle?.ParticipantObject is { } participant && BotIds.IsBotObject(participant.m_globalID.Full);

    [MessageHandler(typeof(BotJoinBattle))]
    private void ReceiveBotJoinBattle(BotJoinBattle message) {
        string refusal = null;
        if (!_isActive) {
            refusal = "the battle was over";
        }
        else if (IsPvPDuel) {
            refusal = "bots don't join PvP";
        }
        else if (_botsKicked) {
            refusal = "a player kicked a bot out of this battle";
        }

        if (refusal is null) {
            AddParticipant(message.BotObject, message.BotActor);
            var circle = SubCircles.FirstOrDefault(c => c?.ParticipantObject?.m_globalID == message.BotObject.m_globalID);
            if (circle is null) {
                refusal = "no free circle";
            }
            else {
                // On its circle for every game in the area, as a real player who walked in.
                ZoneBroadcast(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
                    LocationX = CompressTeleportCoordinate(circle.WorldPosition.X),
                    LocationY = CompressTeleportCoordinate(circle.WorldPosition.Y),
                    LocationZ = CompressTeleportCoordinate(circle.WorldPosition.Z),
                    Direction = (byte) System.Math.Round(circle.WorldRotation / (2 * System.Math.PI) * 250),
                    MobileID = message.BotObject.m_nMobileID,
                });
                Logger.Information("Duel {0} | Slot {1} | Bot {2} joined the battle.",
                    Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, message.Name));
                message.BotActor.Tell(new BotSeated(circle.WorldPosition, circle.WorldRotation));
                PassForAutoPassing();

                return;
            }
        }

        Logger.Information("Duel {0} | Bot {1} did not join: {2}.", Logger.Args(Duel?.m_duelID.Full ?? SigilId, message.Name, refusal));
        message.BotActor.Tell(new BotNotSeated(refusal));
    }

    [MessageHandler(typeof(BotKickFromBattle))]
    private void ReceiveBotKickFromBattle(BotKickFromBattle message) {
        if (!_isActive) {
            return;
        }

        _botsKicked = true;
        var kicked = SubCircles.Where(c => c is { Occupied: true } && IsBot(c)
                                           && (message.BotGlobalId == 0 || c.ParticipantObject.m_globalID.Full == message.BotGlobalId)).ToList();
        foreach (var circle in kicked) {
            Logger.Information("Duel {0} | Slot {1} | {2} kicked bot {3} out of the battle; no bot joins it again.",
                Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, message.By, circle.ParticipantObject.m_debugName));
            HandleFleeAction(circle, drainMana: false);
        }
    }

}
