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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.QuestTeam;
using Imlight.CoreLib.Shared.Networking;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The Quest Team (Game/QuestTeam) in a battle: real wizards on players' circles whose game is the server's own, so their
/// picks are made here, each by its job (QuestTeamTactics), from its own hand and pips. They pick in the team's order (Ice,
/// Life, Death, Storm), each seeing the picks before it, after any real player before them on their side has picked.
/// </summary>
internal sealed partial class CombatDuelComponent {

    private sealed record QuestTeamThink(int Round, bool Deadline);

    private const string QUEST_TEAM_THINK_KEY = "QuestTeamThink";
    private const string QUEST_TEAM_DEADLINE_KEY = "QuestTeamDeadline";
    private readonly Dictionary<int, string> _questTeamPicks = [];
    private readonly HashSet<(int Round, int Slot)> _questTeamDiscarded = [];
    private int _questTeamDeadlineRound = -1;

    private static string QuestTeamRole(CombatDuelSubCircle circle)
        => circle?.ParticipantObject is { } participant ? QuestTeamDirector.RoleOf(participant.m_globalID.Full) : null;

    private IEnumerable<CombatDuelSubCircle> QuestTeamCircles
        => SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && QuestTeamRole(c) is not null);

    private void QuestTeamPlanningBegins() {
        _questTeamPicks.Clear();
        if (IsPvPDuel || !_awaitingCombatMoves || !QuestTeamCircles.Any()) {
            return;
        }

        Timers.StartSingleTimer(QUEST_TEAM_THINK_KEY, new QuestTeamThink(Duel.m_roundNum, false), TimeSpan.FromSeconds(2 + Random.Shared.NextDouble()));
        Timers.StartSingleTimer(QUEST_TEAM_DEADLINE_KEY, new QuestTeamThink(Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, PlanningSeconds - BOT_DEADLINE_BEFORE_END)));
    }

    // A real player picked: the team looks again.
    private void QuestTeamNoticeMove(CombatDuelSubCircle caster) {
        if (caster is null || QuestTeamRole(caster) is not null || IsPvPDuel || !QuestTeamCircles.Any()) {
            return;
        }

        Timers.StartSingleTimer(QUEST_TEAM_THINK_KEY, new QuestTeamThink(Duel.m_roundNum, false), TimeSpan.FromSeconds(0.8));
    }

    [MessageHandler(typeof(QuestTeamThink))]
    private void ReceiveQuestTeamThink(QuestTeamThink message) {
        if (!_isActive || !_awaitingCombatMoves || IsPvPDuel || Duel.m_roundNum != message.Round) {
            return;
        }

        if (message.Deadline) {
            _questTeamDeadlineRound = message.Round;
        }

        var circles = ActiveSubCircles.ToList();
        var played = false;
        var order = new[] { "Ice", "Life", "Death", "Storm" };
        foreach (var member in QuestTeamCircles.OrderBy(c => Array.IndexOf(order, QuestTeamRole(c))).ToList()) {
            // A real player before it on its side (a lower circle) picks first, until the last call.
            var waiting = circles.Any(c => c.OccupiedTeam == member.OccupiedTeam && c.SlotIndex < member.SlotIndex && c.IsHumanPlayer
                                           && c.IsAlive && c.AddedToDuel && QuestTeamRole(c) is null && !IsBot(c)
                                           && CombatResolver.GetQueuedAction(c) is null);
            if (waiting && _questTeamDeadlineRound != message.Round) {
                continue;
            }

            var role = QuestTeamRole(member);

            // First, once a round, the cards its job won't play go (Game/BotShared/BotCards), as a player discards.
            if (_questTeamDiscarded.Add((Duel.m_roundNum, member.SlotIndex))) {
                foreach (var index in BotCards.Discards(member, circles, BotGear.StyleOfRole(role))) {
                    var thrown = member.GetSpellFromLastHand((byte) index);
                    QuestTeamLink.Note(role, $"round {Duel.m_roundNum}: discards {(thrown is null ? "a card" : RewrittenAI.Read(thrown)?.Shown ?? "a card")}");
                    HandleDiscardMove(member, index);
                }
            }

            QuestTeamTactics.Choice choice;
            try {
                choice = QuestTeamTactics.Decide(role, member, circles, CombatResolver);
            }
            catch (Exception e) {
                Logger.Warning("Duel {0} | Slot {1} | Quest Team {2} could not decide: {3}", Logger.Args(Duel.m_duelID.Full, member.SlotIndex, role, e.Message));
                choice = new(QuestTeamTactics.Move.Pass, 0, -1, "could not decide");
            }

            var pick = $"{choice.Move}:{choice.Card}:{choice.Target}";
            if (_questTeamPicks.TryGetValue(member.SlotIndex, out var before) && before == pick && CombatResolver.GetQueuedAction(member) is not null) {
                continue;
            }

            _questTeamPicks[member.SlotIndex] = pick;
            QuestTeamLink.Note(role, $"round {Duel.m_roundNum}: {choice.Why}");
            if (choice.Move == QuestTeamTactics.Move.Cast) {
                var target = choice.Target < 0 ? uint.MaxValue : (uint) choice.Target;
                HandleAttackMove(member, choice.Card, target, choice.Target < 0 ? uint.MaxValue : 1u << choice.Target);
            }
            else {
                HandlePassMove(member);
            }

            played = true;

            // A real player or bot after it may adapt to its pick.
            BotsNoticeMove(member);
        }

        if (played && _isActive && _awaitingCombatMoves && CombatResolver.HaveAllParticipantsEnqueuedActions()) {
            _awaitingCombatMoves = false;
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), TimeSpan.FromSeconds(1));
        }
    }

}
