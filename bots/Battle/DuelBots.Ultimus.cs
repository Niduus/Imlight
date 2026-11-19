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
using Imlight.CoreLib.Shared.Resources;
using Imcodec.ObjectProperty.TypeCache;
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
internal sealed partial class DuelBots {

    private sealed record UltimusThink(int Round, bool Deadline);

    private const string ULTIMUS_THINK_KEY = "UltimusThink";
    private const string ULTIMUS_DEADLINE_KEY = "UltimusDeadline";
    private readonly Dictionary<int, string> _ultimusPicks = [];
    private readonly HashSet<(int Round, int Slot)> _ultimusDiscarded = [];
    private readonly HashSet<(int Round, int Slot)> _ultimusDrew = [];
    // Rounds he threw a card away to make room for a treasure card (once a round).
    private readonly HashSet<(int Round, int Slot)> _ultimusRoomMade = [];
    // Treasure card draws made to dig for an enchantment this fight, by slot (at most ULTIMUS_DIGS, so he does not
    // throw away too much of his hand for it).
    private readonly Dictionary<int, int> _ultimusDigs = [];
    private const int ULTIMUS_DIGS = 3;
    // The Ultimus circles that have read this battle's enemy decks (said once a battle).
    private readonly HashSet<int> _ultimusScouted = [];
    private int _ultimusDeadlineRound = -1;

    private static string UltimusRole(CombatDuelSubCircle circle)
        => circle?.ParticipantObject is { } participant ? UltimusDirector.RoleOf(participant.m_globalID.Full) : null;

    /// <summary>
    /// Works as the seat (Ultimus or a TCO7 member) of this circle's wizard until disposed, so its picks, records and memories
    /// are its own (UltimusSeat).
    /// </summary>
    private static UltimusSeat.Scope UltimusSeatOf(CombatDuelSubCircle circle)
        => UltimusSeat.Use(UltimusSeat.OfGlobal(circle?.ParticipantObject?.m_globalID.Full ?? 0) ?? UltimusSeat.Current);

    private IEnumerable<CombatDuelSubCircle> UltimusCircles
        => D.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true, IsAlive: true } && UltimusRole(c) is not null);

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

        var other = D.SubCircles.FirstOrDefault(c => c is { Occupied: true, IsSummonedMinion: false }
                                                   && (joinerIsUltimus ? QuestTeamRole(c) is not null : UltimusRole(c) is not null));
        if (other is null) {
            return false;
        }

        Logger.Debug("Duel {0} | {1} stays out: {2} is already in this battle (Ultimus and the Quest Team keep apart).",
            Logger.Args(D.Duel.m_duelID.Full, joiner.m_debugName, other.ParticipantObject?.m_debugName));

        return true;
    }

    /// <summary>
    /// Whether Ultimus may walk into the battle a creature (by object id) is in: a real player is in it (not the Quest
    /// Team), it is not PvP, and a players' circle is free. <paramref name="who"/> is a player in it, for the log.
    /// </summary>
    /// <summary>Whether Ultimus is in a battle with a real player beside him (his lessons then say "with players", not "solo").</summary>
    internal static bool UltimusWithPlayers(ulong ultimusGlobalId) {
        try {
            return CombatDuelComponent.s_playerBattles.TryGetValue(ultimusGlobalId, out var claim) && claim.Battle.HoldsPlayer(ultimusGlobalId)
                   && claim.Battle.SubCircles is { } circles
                   && circles.Any(c => c is { Occupied: true, IsHumanPlayer: true, IsSummonedMinion: false } && UltimusRole(c) is null && !IsBot(c));
        }
        catch (InvalidOperationException) {
            return false;
        }
    }

    /// <summary>
    /// The templates of the creatures fighting against <paramref name="globalId"/> in its battle (empty when it is in none),
    /// so a bot can see how strong a group mate's fight is before it walks in.
    /// </summary>
    internal static List<ulong> UltimusFoesOf(ulong globalId) {
        foreach (var battle in CombatDuelComponent.s_playerBattles.Values.Select(v => v.Battle).Distinct().ToList()) {
            try {
                if (!battle._isActive || battle.SubCircles is not { } circles) {
                    continue;
                }

                var mine = circles.FirstOrDefault(c => c is { Occupied: true } && c.ParticipantObject?.m_globalID.Full == globalId);
                if (mine is null) {
                    continue;
                }

                return [.. circles.Where(c => c is { Occupied: true, IsSummonedMinion: false } && c.SlotType != mine.SlotType && c.ParticipantObject is not null)
                    .Select(c => (ulong) c.ParticipantObject.m_templateID)];
            }
            catch (InvalidOperationException) {
                // The circles changed mid-read: asked again next time.
            }
        }

        return [];
    }

    internal static bool UltimusMayJoin(ulong creatureGlobalId, out string who) {
        who = "";
        foreach (var battle in CombatDuelComponent.s_playerBattles.Values.Select(v => v.Battle).Distinct().ToList()) {
            try {
                if (!battle._isActive || battle.IsPvPDuel || (battle.BotState is DuelBots kicked && kicked._botsKicked) || battle.SubCircles is not { } circles) {
                    continue;
                }

                if (!circles.Any(c => c is { Occupied: true } && c.ParticipantObject?.m_globalID.Full == creatureGlobalId)) {
                    continue;
                }

                if (circles.Any(c => c is { Occupied: true } && QuestTeamRole(c) is not null)) {
                    return false;
                }

                // A TCO7 member counts another TCO7 member as a player here: they join each other's fights as two players do
                // (Niduus, 2026-11-16: "I want them to quest either alone or with someone").
                var me = UltimusSeat.Current;
                var player = circles.FirstOrDefault(c => c is { Occupied: true, IsHumanPlayer: true, IsSummonedMinion: false }
                                                         && ((UltimusRole(c) is null && !IsBot(c))
                                                             || (me.IsTco7 && UltimusSeat.OfGlobal(c.ParticipantObject?.m_globalID.Full ?? 0) is { IsTco7: true } other && other != me)));
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
        if (!CombatDuelComponent.s_playerBattles.TryGetValue(ultimusGlobalId, out var claim)) {
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

    /// <summary>The players in Ultimus's battle, by character ID and name (for his memory of who he fought with).</summary>
    internal static List<(ulong CharId, string Name)> UltimusBattlePlayers(ulong ultimusGlobalId) {
        if (!CombatDuelComponent.s_playerBattles.TryGetValue(ultimusGlobalId, out var claim)) {
            return [];
        }

        try {
            return [.. claim.Battle.SubCircles.Where(c => c is { Occupied: true, IsSummonedMinion: false } && !c.IsAiCreature && c._wizard is not null
                                                          && c.ParticipantObject?.m_globalID.Full != ultimusGlobalId)
                .Select(c => (c._wizard.CharId, c._wizard.PlayerNameBehavior?.GetWizardName() ?? c.ParticipantObject?.m_debugName ?? "someone"))];
        }
        catch (InvalidOperationException) {
            return [];
        }
    }

    // A tough fight: a boss, three or more enemies, his health under half, or the enemies' habits say they take 40% of it this round.
    private bool UltimusToughFight(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles) {
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0) {
            return false;
        }

        // Nearly won (no boss, every enemy under a quarter of its health): a treasure card is kept for a fight that needs it
        // (Niduus, 2026-11-17: "what to use, when").
        if (!enemies.Any(RewrittenAI.IsBoss) && enemies.All(e => RewrittenAI.HealthShare(e) < 0.25f)) {
            return false;
        }

        return enemies.Any(RewrittenAI.IsBoss) || enemies.Count >= 3 || RewrittenAI.HealthShare(me) < 0.5
               || BotCore.Incoming(enemies, D.Duel.m_roundNum, c => "").Share >= 0.4;
    }

    /// <summary>
    /// Digging for an enchantment (Niduus, 2026-11-17: "If he has a enchant in his TC, he discard cards to dig for it, while
    /// also still planning his next stage plan, careful not to discard too much"): his treasure cards hold a damage
    /// enchantment, none is in his hand, he holds a hit to put it on, the fight is not nearly won, and he has dug fewer than
    /// ULTIMUS_DIGS times this fight. One draw a round; with a full hand only his least useful card goes, never a hit he
    /// plans to cast (UltimusRoomForTreasure).
    /// </summary>
    private bool UltimusDigForEnchant(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles) {
        var enemies = circles.Where(c => c.OccupiedTeam != me.OccupiedTeam && c.IsAlive).ToList();
        if (enemies.Count == 0 || _ultimusDigs.GetValueOrDefault(me.SlotIndex) >= ULTIMUS_DIGS || me.VaultRemainingCount <= 0
            || UltimusTactics.HoldsHitEnchant(me)
            || (!enemies.Any(RewrittenAI.IsBoss) && enemies.All(e => RewrittenAI.HealthShare(e) < 0.25f))
            || !UltimusTactics.Hand(me).Any(h => h.Card.Hit && !h.Card.Natural)) {
            return false;
        }

        return me._wizard?.SpellbookBehavior?.SpellList is { } deck
               && deck.Any(s => s is not null && CoreObjectFactory.GetCoreTemplate(s.m_templateID) is SpellTemplate t && t.m_Treasure
                                // One he can play at his level: he dug three times a fight for Tough and Giant (level 52 cards)
                                // at level 26, throwing away Balanceblade and Dragonblade (his log, 2026-11-17 19:36-19:52).
                                && BotEnchants.Playable(t, BotGear.LevelOf(me._wizard))
                                && BotEnchants.Of(t) is { Kind: BotEnchants.DAMAGE or BotEnchants.PIERCE });
    }

    // The card he throws away to make room for a treasure card: one his solo play would discard anyway, else the least
    // useful (no hit, no heal, then the lowest rank).
    private static (int Index, string Why)? UltimusRoomForTreasure(CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles) {
        var threat = UltimusScout.InBattle(circles.Where(c => c.OccupiedTeam != me.OccupiedTeam));
        var cards = new List<(int Index, RewrittenAI.Card Card)>();
        for (byte i = 0; i < 24; i++) {
            if (me.GetSpellFromLastHand(i) is { } spell && !spell.m_treasureCard && RewrittenAI.Read(spell) is { } card) {
                cards.Add((i, card));
            }
        }

        var useless = BotCards.Discards(me, circles, BotStyle.Backup, c => UltimusTactics.UselessShield(c, threat))
            .Where(i => cards.Any(c => c.Index == i)).ToList();
        var pick = useless.Count > 0
            ? cards.First(c => c.Index == useless[0])
            : cards.OrderBy(c => c.Card.Hit ? 1 : 0).ThenBy(c => c.Card.Heal ? 1 : 0).ThenBy(c => c.Card.Rank).FirstOrDefault();

        return pick.Card is null ? null : (pick.Index, $"discards {pick.Card.Shown}");
    }

    private void UltimusPlanningBegins() {
        _ultimusPicks.Clear();
        if (D.Duel.m_roundNum <= 1) {
            _ultimusScouted.Clear();
            _ultimusDrew.Clear();
            _ultimusRoomMade.Clear();
            _ultimusDownSince.Clear();
            _ultimusDigs.Clear();
        }

        UltimusDownedRound();
        foreach (var circle in UltimusCircles.ToList()) {
            using var seat = UltimusSeatOf(circle);
            UltimusOrders.NewRound();
        }

        if (D.IsPvPDuel || !D._awaitingCombatMoves || !UltimusCircles.Any()) {
            return;
        }

        // A person's moment to look at the hand before reaching for a card (2.5 to 5 s; the clicks take a few more).
        D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(2.5 + (Random.Shared.NextDouble() * 2.5)));
        D.BotTimer(ULTIMUS_DEADLINE_KEY, new UltimusThink(D.Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, D.PlanningSeconds - BOT_DEADLINE_BEFORE_END)));
    }

    // The round each downed Ultimus circle was first seen down in, by slot.
    private readonly Dictionary<int, int> _ultimusDownSince = [];
    private const int ULTIMUS_DOWN_ROUNDS = 4;

    /// <summary>
    /// Niduus, 2026-11-14: "if dead in battle, Ultimus will stay for 4 rounds. At the start of the 5th, he will leave. Also
    /// noting this in chat". Downed, he says so and waits 4 rounds for a revive; at the start of the 5th he presses Flee
    /// himself, through his own game (nobody takes him out). Revived, he thanks them.
    /// </summary>
    private void UltimusDownedRound() {
        if (D.IsPvPDuel) {
            return;
        }

        foreach (var circle in D.SubCircles.Where(c => c is { Occupied: true, AddedToDuel: true } && UltimusRole(c) is not null).ToList()) {
            using var seat = UltimusSeatOf(circle);
            var wizard = UltimusDirector.ActorOf(UltimusRole(circle));
            if (wizard is null) {
                continue;
            }

            if (circle.IsAlive) {
                if (_ultimusDownSince.Remove(circle.SlotIndex)) {
                    wizard.Tell(new UltimusMember.SayText("Thanks for the revive! Back in it."), Akka.Actor.ActorRefs.NoSender);
                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: was revived; fights on.");
                }

                continue;
            }

            if (!_ultimusDownSince.TryGetValue(circle.SlotIndex, out var since)) {
                _ultimusDownSince[circle.SlotIndex] = D.Duel.m_roundNum;
                wizard.Tell(new UltimusMember.SayText($"Sorry, I'm down! If nobody can revive me, I'll leave the battle in {ULTIMUS_DOWN_ROUNDS} rounds."),
                    Akka.Actor.ActorRefs.NoSender);
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: is down; waits {ULTIMUS_DOWN_ROUNDS} rounds for a revive, then leaves.");
                continue;
            }

            // In a dungeon or instance a flee loses the whole run, so he never presses it there: he stays down and waits for a
            // revive or the battle's end (Niduus, 2026-11-16: "see fleeing a Dungeon/Instance as a punishment").
            if (D.Duel.m_roundNum - since >= ULTIMUS_DOWN_ROUNDS && circle._wizard is { } downed && Imlight.CoreLib.Game.World.DungeonRecall.IsInDungeon(downed.CharId)) {
                if (D.Duel.m_roundNum - since == ULTIMUS_DOWN_ROUNDS) {
                    wizard.Tell(new UltimusMember.SayText("I won't flee: in here that would lose the whole dungeon. I'll wait for a revive or the end of the fight."),
                        Akka.Actor.ActorRefs.NoSender);
                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: down {ULTIMUS_DOWN_ROUNDS} rounds in a dungeon: does not flee (that loses the run); waits.");
                }

                continue;
            }

            if (D.Duel.m_roundNum - since >= ULTIMUS_DOWN_ROUNDS) {
                _ultimusDownSince.Remove(circle.SlotIndex);
                UltimusEffort.Note(UltimusEffort.Kind.Fled, "down 4 rounds with no revive, so he fled the battle");
                wizard.Tell(new UltimusMember.SayText("Nobody could revive me, so I'm leaving the battle. Good luck!"), Akka.Actor.ActorRefs.NoSender);
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: down for {ULTIMUS_DOWN_ROUNDS} rounds with no revive: presses Flee.");
                wizard.Tell(new UltimusMember.Flee(), Akka.Actor.ActorRefs.NoSender);
            }
        }
    }

    // Ultimus came into the battle mid-planning: its look and its last call, by the time left.
    private void UltimusJoinedPlanning(CombatDuelSubCircle circle, double secondsLeft) {
        if (UltimusRole(circle) is null || D.IsPvPDuel || !D.PlanningMayEnd) {
            return;
        }

        _ultimusPicks.Remove(circle.SlotIndex);
        D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false),
            TimeSpan.FromSeconds(Math.Min(2.5 + (Random.Shared.NextDouble() * 2.5), Math.Max(1, secondsLeft / 3))));
        D.BotTimer(ULTIMUS_DEADLINE_KEY, new UltimusThink(D.Duel.m_roundNum, true),
            TimeSpan.FromSeconds(Math.Max(3, secondsLeft - BOT_DEADLINE_BEFORE_END)));
    }

    // A real player picked or changed their mind: Ultimus looks again.
    private void UltimusNoticeMove(CombatDuelSubCircle caster) {
        // Only a real player's pick, in planning: creatures pick as the round opens, and a look then had Ultimus pick
        // before planning began, which ended the round early (Niduus, 2026-11-13).
        if (caster is null || !caster.IsHumanPlayer || IsBot(caster) || UltimusRole(caster) is not null || D.IsPvPDuel
            || !D.PlanningMayEnd || !UltimusCircles.Any()) {
            return;
        }

        D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(0.8));
    }

    // Comes through BotDoor (DuelMessage).
    private void ReceiveUltimusThink(UltimusThink message) {
        if (!D._isActive || !D._awaitingCombatMoves || D.IsPvPDuel || D.Duel.m_roundNum != message.Round || !D.PlanningMayEnd) {
            return;
        }

        if (message.Deadline) {
            _ultimusDeadlineRound = message.Round;
        }

        var circles = D.ActiveSubCircles.ToList();
        foreach (var member in UltimusCircles.ToList()) {
            using var seat = UltimusSeatOf(member);
            // A real player before it on its side (a lower circle) picks first, until the last call.
            var waiting = circles.Any(c => c.OccupiedTeam == member.OccupiedTeam && c.SlotIndex < member.SlotIndex && c.IsHumanPlayer
                                           && c.IsAlive && c.AddedToDuel && UltimusRole(c) is null && !IsBot(c)
                                           && D.CombatResolver.GetQueuedAction(c) is null);
            // His group leader's orders (CombatDuelComponent.UltimusOrders.cs): "you go first" picks without waiting; "let me go
            // first" waits for the leader's pick, until the last call.
            waiting = (waiting && !UltimusOrders.GoFirst) || UltimusLeaderStillPicking(member, circles);
            if (waiting && _ultimusDeadlineRound != message.Round) {
                continue;
            }

            // An ordered flee or treasure card draw comes before anything else.
            if (UltimusOrderedFleeOrDraw(member, message.Deadline)) {
                continue;
            }

            // The card thrown away to make room for a treasure card is not out of his hand yet: he picks once it is, on the
            // hand as it really is (Niduus, 2026-11-16: "Ultimus just hit himself": he looked again before the discard
            // landed, and his pick's place in the hand then held another card, cast on himself).
            if (!message.Deadline && _ultimusRoomMade.Contains((D.Duel.m_roundNum, member.SlotIndex)) && member.HandCount >= 7
                && !_ultimusDrew.Contains((D.Duel.m_roundNum, member.SlotIndex))) {
                D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(1));
                continue;
            }

            // A tough fight, treasure cards waiting and a full hand: the Draw button does nothing with seven cards in hand, so
            // he had never drawn one (Niduus, 2026-11-16: "if he's not using Treasure cards in battle, have him use them";
            // his logs: no draw in any fight). As a player does, he throws away his least useful card first, then draws.
            var digging = UltimusDigForEnchant(member, circles);
            if (!message.Deadline && ((UltimusToughFight(member, circles) && member.VaultRemainingCount > 0 && member.TreasureCardsInHand == 0) || digging)
                && member.HandCount >= 7 && !_ultimusDrew.Contains((D.Duel.m_roundNum, member.SlotIndex))
                && UltimusRoomForTreasure(member, circles) is { } room && _ultimusRoomMade.Add((D.Duel.m_roundNum, member.SlotIndex))
                && UltimusDirector.ActorOf(UltimusRole(member)) is { } maker) {
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: " + (digging
                    ? $"digging for an enchantment in my treasure cards with a full hand: {room.Why} to make room."
                    : $"a tough fight with a full hand: {room.Why} to make room for a treasure card."));
                var roomLeft = D.PlanningSeconds - (DateTime.UtcNow - D._planningStartedAt).TotalSeconds;
                maker.Tell(new UltimusMember.Clicks(BotBattle.Timed([BotBattle.Move.Discard(room.Index, room.Why)], 7, roomLeft)),
                    Akka.Actor.ActorRefs.NoSender);
                D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(4));
                continue;
            }

            // Niduus, 2026-11-15: "Let him buy a few treasure cards and play them in tough fights". In a tough fight, with
            // treasure cards in his deck, none in hand and room for one, he presses Draw (his own game's button, once a
            // round) and looks at the hand again a moment later, when the card is in it.
            if (!message.Deadline && ((UltimusToughFight(member, circles) && member.VaultRemainingCount > 0 && member.TreasureCardsInHand == 0) || digging)
                && member.HandCount < 7 && _ultimusDrew.Add((D.Duel.m_roundNum, member.SlotIndex))
                && UltimusDirector.ActorOf(UltimusRole(member)) is { } drawer) {
                if (digging) {
                    _ultimusDigs[member.SlotIndex] = _ultimusDigs.GetValueOrDefault(member.SlotIndex) + 1;
                }

                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: " + (digging
                    ? $"digs for an enchantment for my next big hit: draws a treasure card ({_ultimusDigs[member.SlotIndex]} of {ULTIMUS_DIGS} this fight)."
                    : "a tough fight: draws a treasure card."));
                drawer.Tell(new UltimusMember.DrawTreasure(), Akka.Actor.ActorRefs.NoSender);
                D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(1.5));
                continue;
            }

            var moves = new List<BotBattle.Move>();

            // First, once a round, the cards a solo wizard won't play go, as a player discards.
            if (_ultimusDiscarded.Add((D.Duel.m_roundNum, member.SlotIndex))) {
                var threat = UltimusScout.InBattle(circles.Where(c => c.OccupiedTeam != member.OccupiedTeam));
                // The schools he is hit with go into his training plan (a school shield against the one he meets most).
                BotTrainingPlan.Faced(member.ParticipantObject?.m_globalID.Full ?? 0, threat.Schools);
                // Who really stands in this battle, by creature id and real name, for his clean fight records (UltimusFights).
                UltimusFights.Seen(member.ParticipantObject?.m_globalID.Full ?? 0, D.Duel.m_roundNum,
                    circles.Where(c => c.OccupiedTeam != member.OccupiedTeam), circles.Where(c => c.OccupiedTeam == member.OccupiedTeam));
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
                choice = UltimusTactics.Decide(member, circles, D.CombatResolver, D.Duel.m_roundNum);
            }
            catch (Exception e) {
                Logger.Warning("Duel {0} | Slot {1} | " + UltimusSeat.Current.Name + " could not decide: {2}", Logger.Args(D.Duel.m_duelID.Full, member.SlotIndex, e.Message));
                choice = new(UltimusTactics.Move.Pass, 0, -1, "could not decide");
            }

            choice = UltimusOrdered(member, circles, choice, moves);
            var pick = $"{choice.Move}:{choice.Card}:{choice.Target}";
            // The same pick is not clicked again, unless at the last call its click never landed.
            if (_ultimusPicks.TryGetValue(member.SlotIndex, out var before) && before == pick
                && (!message.Deadline || D.CombatResolver.GetQueuedAction(member) is not null)) {
                continue;
            }

            _ultimusPicks[member.SlotIndex] = pick;
            UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {choice.Why}");
            // How likely he plays each card, learned from his own fights (UltimusCardUse; Niduus, 2026-11-18).
            UltimusCardUseRound(member, choice);
            // What the overlay's Ultimus Spectator shows of this round (read only).
            UltimusSpectate.NoteRound(D.Duel.m_duelID.Full, D.Duel.m_roundNum, member, circles, choice,
                [.. moves.Where(m => m.Type == CombatMoveType.Discard).Select(m => m.Why)]);
            // The best enchantment in his hand goes on the card first, as a player drags it on (the turn is not used up).
            if (choice.Move == UltimusTactics.Move.Cast && !moves.Any(m => m.Type == CombatMoveType.Discard && m.Card == choice.Card)
                && UltimusTactics.EnchantFor(member, choice.Card, circles.Count(c => c.OccupiedTeam != member.OccupiedTeam && c.IsAlive)) is { } enchant
                && !moves.Any(m => m.Type == CombatMoveType.Discard && m.Card == enchant.Index)) {
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {enchant.Why}.");
                moves.Add(BotBattle.Move.Enchant(enchant.Index, choice.Card, enchant.Why));
            }

            moves.Add(choice.Move == UltimusTactics.Move.Cast ? BotBattle.Move.Cast(choice.Card, choice.Target, choice.Why) : BotBattle.Move.Pass(choice.Why));

            // The clicks go through its own game, as a person's do, after a person's moment to read the hand.
            var left = D.PlanningSeconds - (DateTime.UtcNow - D._planningStartedAt).TotalSeconds;
            var clicks = message.Deadline ? BotBattle.Timed(moves, 0, Math.Min(left, 3)) : BotBattle.Timed(moves, 7, left);
            if (UltimusDirector.ActorOf(UltimusRole(member)) is { } wizard) {
                wizard.Tell(new UltimusMember.Clicks(clicks), Akka.Actor.ActorRefs.NoSender);
            }
        }
    }

    // The cards in his hand this round and the one he casts, for his card use counts (UltimusCardUse).
    private void UltimusCardUseRound(CombatDuelSubCircle member, UltimusTactics.Choice choice) {
        try {
            var hand = new List<uint>();
            for (var i = 0; i < member.HandCount; i++) {
                if (member.GetSpellFromLastHand((byte) i) is { } spell) {
                    hand.Add(spell.m_templateID);
                }
            }

            var cast = choice.Move == UltimusTactics.Move.Cast && member.GetSpellFromLastHand((byte) choice.Card) is { } picked ? (uint) picked.m_templateID : 0u;
            var level = (int) (member.ParticipantGameStats?.Level ?? 0);
            UltimusCardUse.Round(UltimusSeat.Current.Folder, D.Duel.m_duelID.Full, D.Duel.m_roundNum, level, hand, cast);
        }
        catch (Exception) {
            // Counts only: never in the way of the fight.
        }
    }

}
