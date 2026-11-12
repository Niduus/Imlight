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

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Bot Players (Game/BotPlayers) in a battle. A bot stands on a players' circle as a wizard and counts as a player for
/// everything (battle size included); its body answers the battle's questions like a player's session would. A bot picks
/// its card right after the real players before it (lower circles) on its side have picked theirs, and picks again when
/// any player on its side picks or changes their mind (BotTactics). A player can kick the bots out of their battle; after that no bot joins it again (Niduus, 2026-11-12).
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
                ScheduleBotThink(TimeSpan.FromSeconds(1.5));

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

    // ---------------------------------------------------------------- the bots' turn

    private sealed record BotThink(int Round, bool Deadline);

    private const string BOT_THINK_KEY = "BotThink";
    private const string BOT_DEADLINE_KEY = "BotDeadline";

    // How long the bots wait for slow players before they pick anyway: this much before the planning ends.
    private const double BOT_DEADLINE_BEFORE_END = 6;

    // The enemy each bot hit last (bot slot -> enemy slot): it stays on it.
    private readonly Dictionary<int, int> _botTargets = [];

    // What each bot picked this round (slot -> card and target), so the same pick is not sent twice.
    private readonly Dictionary<int, string> _botPicks = [];
    private int _botDeadlineRound = -1;

    private IEnumerable<CombatDuelSubCircle> BotCircles
        => SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && IsBot(c));

    private void BotsPlanningBegins() {
        _botPicks.Clear();
        if (IsPvPDuel || !_awaitingCombatMoves || !BotCircles.Any()) {
            return;
        }

        foreach (var bot in BotCircles) {
            // A bot's deck never runs out, like a creature's: it is shuffled back together when it gets low.
            if (bot.AvailableSpells < 8) {
                bot.Reshuffle();
            }

            // A fresh hand each round, as a player who discards what they can't use.
            bot.DrawFreshHand();
        }

        // A first look a moment in (when no real player is on their side, that is when they pick), and a last call before
        // the planning ends for a player who never picks.
        ScheduleBotThink(TimeSpan.FromSeconds(1.5 + Random.Shared.NextDouble() * 2));
        Timers.StartSingleTimer(BOT_DEADLINE_KEY, new BotThink(Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, PlanningSeconds - BOT_DEADLINE_BEFORE_END)));
    }

    // A real player picked a card (or changed their mind): the bots on their side look again shortly.
    private void BotsNoticeMove(CombatDuelSubCircle caster) {
        if (caster is null || !caster.IsHumanPlayer || IsBot(caster) || IsPvPDuel
            || !BotCircles.Any(b => b.OccupiedTeam == caster.OccupiedTeam)) {
            return;
        }

        ScheduleBotThink(TimeSpan.FromSeconds(0.5 + Random.Shared.NextDouble() * 0.6));
    }

    private void ScheduleBotThink(TimeSpan after) {
        if (_isActive && Duel is not null) {
            Timers.StartSingleTimer(BOT_THINK_KEY, new BotThink(Duel.m_roundNum, false), after);
        }
    }

    [MessageHandler(typeof(BotThink))]
    private void ReceiveBotThink(BotThink message) {
        if (!_isActive || !_awaitingCombatMoves || IsPvPDuel || Duel.m_roundNum != message.Round) {
            return;
        }

        if (message.Deadline) {
            _botDeadlineRound = message.Round;
        }

        var circles = ActiveSubCircles.ToList();
        var played = false;
        foreach (var bot in BotCircles.OrderBy(c => c.SlotIndex).ToList()) {
            // It waits for the real players before it on its side (lower circles), until the last call; players after it
            // it adapts to when they pick (Niduus, 2026-11-12: "if it's in slot 2, it will wait for player 1's pick, then
            // adapt to 3 and 4's selection").
            var waiting = circles.Any(c => c.OccupiedTeam == bot.OccupiedTeam && c.SlotIndex < bot.SlotIndex && c.IsHumanPlayer
                                           && c.IsAlive && c.AddedToDuel && !IsBot(c) && CombatResolver.GetQueuedAction(c) is null);
            if (waiting && _botDeadlineRound != message.Round) {
                continue;
            }

            BotTactics.Choice choice;
            try {
                choice = BotTactics.Decide(bot, circles, CombatResolver, _botTargets.GetValueOrDefault(bot.SlotIndex, -1), IsBot);
            }
            catch (Exception e) {
                Logger.Warning("Duel {0} | Slot {1} | Bot could not decide: {2}", Logger.Args(Duel.m_duelID.Full, bot.SlotIndex, e.Message));
                choice = new(BotTactics.Move.Pass, 0, -1, "could not decide");
            }

            var pick = $"{choice.Move}:{choice.Card}:{choice.Target}";
            if (_botPicks.TryGetValue(bot.SlotIndex, out var before) && before == pick && CombatResolver.GetQueuedAction(bot) is not null) {
                continue;
            }

            var changed = before is not null;
            _botPicks[bot.SlotIndex] = pick;
            if (choice.TargetSlotKept >= 0) {
                _botTargets[bot.SlotIndex] = choice.TargetSlotKept;
            }

            PlayBotChoice(bot, choice, changed);
            played = true;
        }

        if (played && _isActive && _awaitingCombatMoves && CombatResolver.HaveAllParticipantsEnqueuedActions()) {
            _awaitingCombatMoves = false;
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), TimeSpan.FromSeconds(1));
        }
    }

    private void PlayBotChoice(CombatDuelSubCircle bot, BotTactics.Choice choice, bool changed) {
        var who = BotBodies.All.FirstOrDefault(o => o.GlobalId == bot.ParticipantObject.m_globalID.Full);
        var name = who?.Name ?? $"slot {bot.SlotIndex}";
        Logger.Information("Duel {0} | Round {1} | Slot {2} | Bot {3} {4}{5}.",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, bot.SlotIndex, name, changed ? "changes its pick: " : "", choice.Why));
        if (who is not null) {
            BotLink.Note(who.Id, name, changed ? "changes its pick" : "round " + Duel.m_roundNum, choice.Why);
        }

        switch (choice.Move) {
            case BotTactics.Move.Cast:
                var target = choice.Target < 0 ? uint.MaxValue : (uint) choice.Target;
                HandleAttackMove(bot, choice.Card, target, choice.Target < 0 ? uint.MaxValue : 1u << choice.Target);
                break;
            case BotTactics.Move.Flee:
                HandleFleeAction(bot, drainMana: false);
                break;
            default:
                HandlePassMove(bot);
                break;
        }
    }

}
