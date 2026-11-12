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
using Imlight.CoreLib.Game.BotShared;
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
            // Its deck made ready for these enemies before it sits down (Niduus, 2026-11-12: "adjust decks on the fly to
            // prepare for the next fight").
            BotCards.PrepareBot(message.BotObject.m_globalID.Full,
                BotCards.SituationOf(SubCircles.Where(c => c is { Occupied: true } && c.IsAiCreature)));
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
    private readonly HashSet<(int Round, int Slot)> _botDiscarded = [];
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
            if (_botPicks.TryGetValue(bot.SlotIndex, out var before) && before == pick) {
                // The same pick: at the last call, a click that never landed is made again.
                if (message.Deadline && CombatResolver.GetQueuedAction(bot) is null) {
                    PlayBotClicks(bot, [ToBattleMove(choice)], choice, false, quick: true);
                }

                continue;
            }

            var changed = before is not null;
            _botPicks[bot.SlotIndex] = pick;
            if (choice.TargetSlotKept >= 0) {
                _botTargets[bot.SlotIndex] = choice.TargetSlotKept;
            }

            // As a person clicks (Niduus, 2026-11-12: "ALL BOTS ... will mimic actual player activity"): once a round the
            // cards it won't play are discarded first, one click each, then the pick, each after a person's moment
            // (Game/BotShared/BotBattle).
            var moves = new List<BotBattle.Move>();
            if (_botDiscarded.Add((Duel.m_roundNum, bot.SlotIndex))) {
                var style = BotGear.StyleOf(RewrittenAI.SchoolOf(bot) ?? "");
                foreach (var index in BotCards.Discards(bot, circles, style)) {
                    if (!(choice.Move == BotTactics.Move.Cast && index == choice.Card)) {
                        moves.Add(BotBattle.Move.Discard(index, "won't play it"));
                    }
                }
            }

            moves.Add(ToBattleMove(choice));
            PlayBotClicks(bot, moves, choice, changed, quick: message.Deadline);
        }
    }

    // A bot's click, made later: dropped when the round moved on or the bot picked again since.
    private sealed record BotClick(int Round, int Slot, int Generation, BotBattle.Move Move);

    private readonly Dictionary<int, int> _botClickGeneration = [];

    private static BotBattle.Move ToBattleMove(BotTactics.Choice choice) => choice.Move switch {
        BotTactics.Move.Cast => BotBattle.Move.Cast(choice.Card, choice.Target, choice.Why),
        BotTactics.Move.Flee => BotBattle.Move.Flee(choice.Why),
        _ => BotBattle.Move.Pass(choice.Why),
    };

    private void PlayBotClicks(CombatDuelSubCircle bot, List<BotBattle.Move> moves, BotTactics.Choice choice, bool changed, bool quick) {
        PlayBotChoice(bot, choice, changed);
        var generation = _botClickGeneration.GetValueOrDefault(bot.SlotIndex) + 1;
        _botClickGeneration[bot.SlotIndex] = generation;
        var left = PlanningSeconds - (DateTime.UtcNow - _planningStartedAt).TotalSeconds;
        // A change of mind is quicker than the first look: the hand is already read.
        var clicks = quick || changed ? BotBattle.Timed(moves, 0, Math.Min(left, 3)) : BotBattle.Timed(moves, bot.GetCurrentHand().m_spellList.Count, left);
        for (var i = 0; i < clicks.Count; i++) {
            Timers.StartSingleTimer($"BotClick:{bot.SlotIndex}:{generation}:{i}",
                new BotClick(Duel.m_roundNum, bot.SlotIndex, generation, clicks[i].Move), TimeSpan.FromSeconds(Math.Max(0.1, clicks[i].AfterSeconds)));
        }
    }

    [MessageHandler(typeof(BotClick))]
    private void ReceiveBotClick(BotClick message) {
        if (!_isActive || !_awaitingCombatMoves || Duel is null || Duel.m_roundNum != message.Round
            || _botClickGeneration.GetValueOrDefault(message.Slot) != message.Generation) {
            return;
        }

        var bot = BotCircles.FirstOrDefault(c => c.SlotIndex == message.Slot);
        if (bot?.ParticipantActor is not { } actor) {
            return;
        }

        // The same message the battle gets from a person's game; it also ends the planning once everyone has picked.
        ReceiveCombatMove(BotBattle.ToBattleMessage(message.Move, actor));
    }

    private void PlayBotChoice(CombatDuelSubCircle bot, BotTactics.Choice choice, bool changed) {
        var who = BotBodies.All.FirstOrDefault(o => o.GlobalId == bot.ParticipantObject.m_globalID.Full);
        var name = who?.Name ?? $"slot {bot.SlotIndex}";
        Logger.Information("Duel {0} | Round {1} | Slot {2} | Bot {3} {4}{5}.",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, bot.SlotIndex, name, changed ? "changes its pick: " : "", choice.Why));
        if (who is not null) {
            BotLink.Note(who.Id, name, changed ? "changes its pick" : "round " + Duel.m_roundNum, choice.Why);
        }

    }

}
