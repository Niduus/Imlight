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

    // A Quest Team wizard stays out of a battle any other wizard (a player or a street bot) is already in.
    private bool QuestTeamRefusesBattle(Imcodec.ObjectProperty.TypeCache.CoreObject joiner) {
        if (QuestTeamDirector.RoleOf(joiner.m_globalID.Full) is null) {
            return false;
        }

        var outsider = SubCircles.FirstOrDefault(c => c is { Occupied: true, IsSummonedMinion: false } && QuestTeamRole(c) is null
                                                      && (c.IsHumanPlayer || IsBot(c)));
        if (outsider is null) {
            return false;
        }

        Logger.Debug("Duel {0} | Quest Team wizard {1} stays out: {2} is already in this battle.",
            Logger.Args(Duel.m_duelID.Full, joiner.m_debugName, outsider.ParticipantObject.m_debugName));

        return true;
    }

    private static readonly string[] s_questTeamOrder = ["Ice", "Life", "Death", "Storm"];

    /// <summary>
    /// Why the Quest Team's formation is broken in this battle, or null when it holds (Niduus, 2026-11-12: "1st: Ice, 2nd:
    /// Life, 3rd: Death, 4th: Storm. They will never break this formation, if they do, they are punished by fleeing").
    /// Broken when anyone else (a player or a bot, not a minion) stands on their side, or a team wizard is not on its own
    /// circle of the four.
    /// </summary>
    private string QuestTeamFormationBroken() {
        var team = SubCircles.Where(c => c is { Occupied: true } && QuestTeamRole(c) is not null).ToList();
        if (team.Count == 0) {
            return null;
        }

        var side = SubCircles.Where(c => c is not null && c.SlotType == team[0].SlotType).OrderBy(c => c.SlotIndex).ToList();
        if (side.FirstOrDefault(c => c.Occupied && !c.IsSummonedMinion && QuestTeamRole(c) is null && (c.IsHumanPlayer || IsBot(c))) is { } outsider) {
            return $"{outsider.ParticipantObject?.m_debugName ?? "someone else"} took a place in their battle";
        }

        foreach (var member in team) {
            var role = QuestTeamRole(member);
            var place = side.IndexOf(member);
            var wanted = Array.IndexOf(s_questTeamOrder, role);
            if (wanted >= 0 && place != wanted) {
                return $"{role} stood on circle {place + 1} instead of {wanted + 1}";
            }
        }

        return null;
    }

    // The formation broke: every team wizard in this battle flees, as a player would, and the team regroups outside.
    private bool QuestTeamKeepFormation() {
        if (IsPvPDuel || !_isActive || QuestTeamFormationBroken() is not { } why) {
            return false;
        }

        QuestTeamLink.Note("Team", $"leaves the battle to keep its formation: {why}.");
        QuestTeamDirector.FormationBroke(why);
        // Each clicks Flee, as a player does.
        foreach (var member in SubCircles.Where(c => c is { Occupied: true } && QuestTeamRole(c) is not null).ToList()) {
            if (QuestTeamDirector.ActorOf(QuestTeamRole(member)) is { } wizard) {
                wizard.Tell(new QuestTeamMember.Clicks([new BotBattle.Click(BotBattle.Move.Flee("keeps the formation"), 0.3 + Random.Shared.NextDouble())]), Akka.Actor.ActorRefs.NoSender);
            }
            else {
                HandleFleeAction(member);
            }
        }

        return true;
    }

    // The first rounds, while their side's creatures' circles are not full and more of what they came for is near.
    private bool QuestTeamHoldsForMobs() {
        if (Duel.m_roundNum > 4 || !QuestTeamDirector.MobsComing) {
            return false;
        }

        var creatures = SubCircles.Count(c => c is { Occupied: true, IsAiCreature: true });
        var team = QuestTeamCircles.ToList();

        return creatures < 4 && team.All(c => RewrittenAI.HealthShare(c) > 0.5f);
    }

    private void QuestTeamPlanningBegins() {
        _questTeamPicks.Clear();
        if (IsPvPDuel || !_awaitingCombatMoves || !QuestTeamCircles.Any()) {
            return;
        }

        if (QuestTeamKeepFormation()) {
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

        if (QuestTeamKeepFormation()) {
            return;
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
            var moves = new List<BotBattle.Move>();

            // First, once a round, the cards its job won't play go (Game/BotShared/BotCards), as a player discards.
            if (_questTeamDiscarded.Add((Duel.m_roundNum, member.SlotIndex))) {
                foreach (var index in BotCards.Discards(member, circles, BotGear.StyleOfRole(role))) {
                    var thrown = member.GetSpellFromLastHand((byte) index);
                    moves.Add(BotBattle.Move.Discard(index, $"discards {(thrown is null ? "a card" : RewrittenAI.Read(thrown)?.Shown ?? "a card")}"));
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

            // More of the creatures they came for may still walk in: no hit yet, so the battle is not won before they join
            // (Niduus, 2026-11-12: "QT will wait for mobs to join the battle as much as possible to get all the mobs they desire").
            if (choice.Move == QuestTeamTactics.Move.Cast && QuestTeamHoldsForMobs()
                && member.GetSpellFromLastHand((byte) choice.Card) is { } card && RewrittenAI.Read(card) is { Hit: true }) {
                choice = new(QuestTeamTactics.Move.Pass, 0, -1, "waits for more creatures to join before hitting");
            }

            var pick = $"{choice.Move}:{choice.Card}:{choice.Target}";
            // The same pick is not clicked again, unless at the last call its click never landed.
            if (_questTeamPicks.TryGetValue(member.SlotIndex, out var before) && before == pick
                && (!message.Deadline || CombatResolver.GetQueuedAction(member) is not null)) {
                continue;
            }

            _questTeamPicks[member.SlotIndex] = pick;
            QuestTeamLink.Note(role, $"round {Duel.m_roundNum}: {choice.Why}");
            moves.Add(choice.Move == QuestTeamTactics.Move.Cast ? BotBattle.Move.Cast(choice.Card, choice.Target, choice.Why) : BotBattle.Move.Pass(choice.Why));

            // The clicks go through its own game, as a person's do, after a person's moment to read the hand.
            var left = PlanningSeconds - (DateTime.UtcNow - _planningStartedAt).TotalSeconds;
            var clicks = message.Deadline ? BotBattle.Timed(moves, 0, Math.Min(left, 3)) : BotBattle.Timed(moves, 7, left);
            if (QuestTeamDirector.ActorOf(role) is { } wizard) {
                wizard.Tell(new QuestTeamMember.Clicks(clicks), Akka.Actor.ActorRefs.NoSender);
            }

            played = true;
        }

        _ = played;
    }

}
