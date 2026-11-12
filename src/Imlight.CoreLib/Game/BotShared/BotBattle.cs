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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// How the server's own wizards (the Quest Team and Bot Players) play a battle round the way a person does (Niduus,
/// 2026-11-12: "QT battles will play 1:1 as a human interaction will. ALL BOTS, not just QT will mimmick actual player
/// activity"): each pick is the same click a game sends (the battle window's card, discard, pass and flee), made after a
/// person's time to read the hand, one click after the other. What to pick stays each one's own brain (QuestTeamTactics,
/// BotTactics); this is how the pick reaches the battle.
/// </summary>
internal static class BotBattle {

    /// <summary>One click in the battle window: a card cast at a circle (-1: no target), a discard, a pass or a flee.</summary>
    internal readonly record struct Move(CombatMoveType Type, int Card, int Target, string Why) {

        internal static Move Cast(int card, int target, string why) => new(CombatMoveType.Attack, card, target, why);

        internal static Move Discard(int card, string why) => new(CombatMoveType.Discard, card, -1, why);

        internal static Move Pass(string why) => new(CombatMoveType.Pass, 0, -1, why);

        internal static Move Flee(string why) => new(CombatMoveType.Flee, 0, -1, why);

    }

    /// <summary>A click and when it is made, in seconds from now.</summary>
    internal readonly record struct Click(Move Move, double AfterSeconds);

    /// <summary>
    /// When each click is made, as a person: a moment to read the hand (longer for a fuller hand), about a second for each
    /// discard, and the pick after. Never later than <paramref name="secondsLeft"/> allows (the last call is quick).
    /// </summary>
    internal static List<Click> Timed(IReadOnlyList<Move> moves, int handSize, double secondsLeft, Random random = null) {
        random ??= Random.Shared;
        var clicks = new List<Click>();
        if (moves.Count == 0) {
            return clicks;
        }

        var budget = Math.Max(0.3, secondsLeft - 2);
        var at = Math.Min(budget * 0.6, 1.5 + (handSize * 0.3) + (random.NextDouble() * 1.5));
        foreach (var move in moves) {
            clicks.Add(new Click(move, Math.Min(at, budget)));
            at += move.Type == CombatMoveType.Discard ? 0.6 + (random.NextDouble() * 0.6) : 0.8 + (random.NextDouble() * 0.8);
        }

        return clicks;
    }

    /// <summary>The target as the game sends it: the circle's bit, or "no target".</summary>
    internal static uint RawTarget(int circle) => circle is < 0 or > 31 ? uint.MaxValue : 1u << circle;

    /// <summary>The click as a game sends it (for a wizard with a game session: the Quest Team's pretend game).</summary>
    internal static DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVE ToGameMessage(Move move) => new() {
        MoveType = (byte) move.Type,
        SpellSelection = (byte) Math.Clamp(move.Card, 0, 255),
        SpellTarget = RawTarget(move.Target),
        TimeLeft = 0,
        ShadowPactTarget = -1,
        SelectedTieredSpellID = 0,
    };

    /// <summary>
    /// The click as the battle gets it from a game (what CombatService makes of MSG_COMBATMOVE), for a wizard with no
    /// session (a Bot Player): told to its battle, it takes the same way a person's click does.
    /// </summary>
    internal static COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE ToBattleMessage(Move move, IActorRef wizardActor) => new() {
        Actor = wizardActor,
        MoveType = (byte) move.Type,
        SpellSelection = (byte) Math.Clamp(move.Card, 0, 255),
        SpellTarget = move.Target < 0 ? uint.MaxValue : (uint) move.Target,
        RawSpellTarget = RawTarget(move.Target),
        TimeLeft = 0,
        ShadowPactTarget = -1,
        SelectedTieredSpellID = 0,
    };

}
