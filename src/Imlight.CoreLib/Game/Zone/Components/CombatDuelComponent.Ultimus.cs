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
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Shared.Networking;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Ultimus, the Solo Quester (Game/Ultimus), in a battle. Forked from the Quest Team's (CombatDuelComponent.QuestTeam.cs):
/// a real wizard on a players' circle whose game is the server's own, so its picks are made here (UltimusTactics), after
/// any real player before it on its side has picked. Unlike the Quest Team it welcomes players into its battle (no
/// formation, nobody flees), and it joins a player's battle when its quest's creature is in it. It never shares a battle
/// with the Quest Team: neither joins the other's (Niduus, 2026-11-12: "He will never attempt to join QT and they will
/// always attempt to avoid colliding with Ultimus").
/// </summary>
internal sealed partial class CombatDuelComponent {

    private sealed record UltimusThink(int Round, bool Deadline);

    private const string ULTIMUS_THINK_KEY = "UltimusThink";
    private const string ULTIMUS_DEADLINE_KEY = "UltimusDeadline";
    private readonly Dictionary<int, string> _ultimusPicks = [];
    private readonly HashSet<(int Round, int Slot)> _ultimusDiscarded = [];
    // The Ultimus circles that have read this battle's enemy decks (said once a battle).
    private readonly HashSet<int> _ultimusScouted = [];
    private int _ultimusDeadlineRound = -1;

    private static string UltimusRole(CombatDuelSubCircle circle)
        => circle?.ParticipantObject is { } participant ? UltimusDirector.RoleOf(participant.m_globalID.Full) : null;

    private IEnumerable<CombatDuelSubCircle> UltimusCircles
        => SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && UltimusRole(c) is not null);

    /// <summary>
    /// Ultimus and the Quest Team never share a battle: an Ultimus wizard stays out of a battle a Quest Team wizard is in,
    /// and a Quest Team wizard stays out of a battle Ultimus is in. Anyone else may join Ultimus.
    /// </summary>
    private bool UltimusRefusesBattle(Imcodec.ObjectProperty.TypeCache.CoreObject joiner) {
        var id = joiner.m_globalID.Full;
        var joinerIsUltimus = UltimusDirector.RoleOf(id) is not null;
        var joinerIsTeam = QuestTeamDirector.RoleOf(id) is not null;
        if (!joinerIsUltimus && !joinerIsTeam) {
            return false;
        }

        var other = SubCircles.FirstOrDefault(c => c is { Occupied: true, IsSummonedMinion: false }
                                                   && (joinerIsUltimus ? QuestTeamRole(c) is not null : UltimusRole(c) is not null));
        if (other is null) {
            return false;
        }

        Logger.Debug("Duel {0} | {1} stays out: {2} is already in this battle (Ultimus and the Quest Team keep apart).",
            Logger.Args(Duel.m_duelID.Full, joiner.m_debugName, other.ParticipantObject?.m_debugName));

        return true;
    }

    /// <summary>
    /// Whether Ultimus may walk into the battle a creature (by object id) is in: a real player is in it (not the Quest
    /// Team), it is not PvP, and a players' circle is free. <paramref name="who"/> is a player in it, for the log.
    /// </summary>
    internal static bool UltimusMayJoin(ulong creatureGlobalId, out string who) {
        who = "";
        foreach (var battle in s_playerBattles.Values.Select(v => v.Battle).Distinct().ToList()) {
            try {
                if (!battle._isActive || battle.IsPvPDuel || battle._botsKicked || battle.SubCircles is not { } circles) {
                    continue;
                }

                if (!circles.Any(c => c is { Occupied: true } && c.ParticipantObject?.m_globalID.Full == creatureGlobalId)) {
                    continue;
                }

                if (circles.Any(c => c is { Occupied: true } && QuestTeamRole(c) is not null)) {
                    return false;
                }

                var player = circles.FirstOrDefault(c => c is { Occupied: true, IsHumanPlayer: true, IsSummonedMinion: false }
                                                         && UltimusRole(c) is null && !IsBot(c));
                var room = circles.Any(c => c is not null && !c.Occupied && c.SlotType == player?.SlotType);
                if (player is null || !room) {
                    return false;
                }

                who = player.ParticipantObject?.m_debugName ?? "a player";

                return true;
            }
            catch (InvalidOperationException) {
                // The circles changed mid-read: asked again next time.
            }
        }

        return false;
    }

    /// <summary>The names of the other wizards (players and street bots) in the battle an Ultimus wizard is in.</summary>
    internal static List<string> UltimusBattleCompany(ulong ultimusGlobalId) {
        if (!s_playerBattles.TryGetValue(ultimusGlobalId, out var claim)) {
            return [];
        }

        try {
            return [.. claim.Battle.SubCircles.Where(c => c is { Occupied: true, IsSummonedMinion: false } && !c.IsAiCreature
                                                          && c.ParticipantObject?.m_globalID.Full != ultimusGlobalId)
                .Select(c => c.ParticipantObject?.m_debugName ?? "someone")];
        }
        catch (InvalidOperationException) {
            return [];
        }
    }

    private void UltimusPlanningBegins() {
        _ultimusPicks.Clear();
        if (Duel.m_roundNum <= 1) {
            _ultimusScouted.Clear();
        }
        if (IsPvPDuel || !_awaitingCombatMoves || !UltimusCircles.Any()) {
            return;
        }

        // A person's moment to look at the hand before reaching for a card (2.5 to 5 s; the clicks take a few more).
        Timers.StartSingleTimer(ULTIMUS_THINK_KEY, new UltimusThink(Duel.m_roundNum, false), TimeSpan.FromSeconds(2.5 + (Random.Shared.NextDouble() * 2.5)));
        Timers.StartSingleTimer(ULTIMUS_DEADLINE_KEY, new UltimusThink(Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, PlanningSeconds - BOT_DEADLINE_BEFORE_END)));
    }

    // Ultimus came into the battle mid-planning: its look and its last call, by the time left.
    private void UltimusJoinedPlanning(CombatDuelSubCircle circle, double secondsLeft) {
        if (UltimusRole(circle) is null || IsPvPDuel || !PlanningMayEnd) {
            return;
        }

        _ultimusPicks.Remove(circle.SlotIndex);
        Timers.StartSingleTimer(ULTIMUS_THINK_KEY, new UltimusThink(Duel.m_roundNum, false),
            TimeSpan.FromSeconds(Math.Min(2.5 + (Random.Shared.NextDouble() * 2.5), Math.Max(1, secondsLeft / 3))));
        Timers.StartSingleTimer(ULTIMUS_DEADLINE_KEY, new UltimusThink(Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, secondsLeft - BOT_DEADLINE_BEFORE_END)));
    }

    // A real player picked or changed their mind: Ultimus looks again.
    private void UltimusNoticeMove(CombatDuelSubCircle caster) {
        // Only a real player's pick, in planning: creatures pick as the round opens, and a look then had Ultimus pick
        // before planning began, which ended the round early (Niduus, 2026-11-13).
        if (caster is null || !caster.IsHumanPlayer || IsBot(caster) || UltimusRole(caster) is not null || IsPvPDuel
            || !PlanningMayEnd || !UltimusCircles.Any()) {
            return;
        }

        Timers.StartSingleTimer(ULTIMUS_THINK_KEY, new UltimusThink(Duel.m_roundNum, false), TimeSpan.FromSeconds(0.8));
    }

    [MessageHandler(typeof(UltimusThink))]
    private void ReceiveUltimusThink(UltimusThink message) {
        if (!_isActive || !_awaitingCombatMoves || IsPvPDuel || Duel.m_roundNum != message.Round || !PlanningMayEnd) {
            return;
        }

        if (message.Deadline) {
            _ultimusDeadlineRound = message.Round;
        }

        var circles = ActiveSubCircles.ToList();
        foreach (var member in UltimusCircles.ToList()) {
            // A real player before it on its side (a lower circle) picks first, until the last call.
            var waiting = circles.Any(c => c.OccupiedTeam == member.OccupiedTeam && c.SlotIndex < member.SlotIndex && c.IsHumanPlayer
                                           && c.IsAlive && c.AddedToDuel && UltimusRole(c) is null && !IsBot(c)
                                           && CombatResolver.GetQueuedAction(c) is null);
            if (waiting && _ultimusDeadlineRound != message.Round) {
                continue;
            }

            var moves = new List<BotBattle.Move>();

            // First, once a round, the cards a solo wizard won't play go, as a player discards.
            if (_ultimusDiscarded.Add((Duel.m_roundNum, member.SlotIndex))) {
                var threat = UltimusScout.InBattle(circles.Where(c => c.OccupiedTeam != member.OccupiedTeam));
                if (_ultimusScouted.Add(member.SlotIndex)) {
                    UltimusLink.Note(UltimusRoster.NAME, $"reads the enemies' decks: {threat.Describe()}.");
                }

                foreach (var index in BotCards.Discards(member, circles, BotStyle.Backup, c => UltimusTactics.UselessShield(c, threat))) {
                    var thrown = member.GetSpellFromLastHand((byte) index);
                    moves.Add(BotBattle.Move.Discard(index, $"discards {(thrown is null ? "a card" : RewrittenAI.Read(thrown)?.Shown ?? "a card")}"));
                }
            }

            UltimusTactics.Choice choice;
            try {
                choice = UltimusTactics.Decide(member, circles, CombatResolver);
            }
            catch (Exception e) {
                Logger.Warning("Duel {0} | Slot {1} | Ultimus could not decide: {2}", Logger.Args(Duel.m_duelID.Full, member.SlotIndex, e.Message));
                choice = new(UltimusTactics.Move.Pass, 0, -1, "could not decide");
            }

            var pick = $"{choice.Move}:{choice.Card}:{choice.Target}";
            // The same pick is not clicked again, unless at the last call its click never landed.
            if (_ultimusPicks.TryGetValue(member.SlotIndex, out var before) && before == pick
                && (!message.Deadline || CombatResolver.GetQueuedAction(member) is not null)) {
                continue;
            }

            _ultimusPicks[member.SlotIndex] = pick;
            UltimusLink.Note(UltimusRoster.NAME, $"round {Duel.m_roundNum}: {choice.Why}");
            // What the overlay's Ultimus Spectator shows of this round (read only).
            UltimusSpectate.NoteRound(Duel.m_duelID.Full, Duel.m_roundNum, member, circles, choice,
                [.. moves.Where(m => m.Type == CombatMoveType.Discard).Select(m => m.Why)]);
            moves.Add(choice.Move == UltimusTactics.Move.Cast ? BotBattle.Move.Cast(choice.Card, choice.Target, choice.Why) : BotBattle.Move.Pass(choice.Why));

            // The clicks go through its own game, as a person's do, after a person's moment to read the hand.
            var left = PlanningSeconds - (DateTime.UtcNow - _planningStartedAt).TotalSeconds;
            var clicks = message.Deadline ? BotBattle.Timed(moves, 0, Math.Min(left, 3)) : BotBattle.Timed(moves, 7, left);
            if (UltimusDirector.ActorOf(UltimusRole(member)) is { } wizard) {
                wizard.Tell(new UltimusMember.Clicks(clicks), Akka.Actor.ActorRefs.NoSender);
            }
        }
    }

}
