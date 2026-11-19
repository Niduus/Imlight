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

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The bots' own work in one battle (Bot Players, the Quest Team, Ultimus and the TCO7): their picks, orders, seats and
/// timers. It was part of the battle itself (CombatDuelComponent.Bots/QuestTeam/Ultimus/UltimusOrders.cs) and moved to the
/// bots folder so a bot update changes it too (BotDoor). The battle keeps one of these in its BotState and hands on what
/// happens; <see cref="D"/> is the battle.
/// </summary>
internal sealed partial class DuelBots {

    /// <summary>The battle.</summary>
    internal readonly CombatDuelComponent D;

    private DuelBots(CombatDuelComponent duel) {
        D = duel;
    }

    /// <summary>The bots' state for a battle, made the first time it is asked for (and again after a new battle starts).</summary>
    internal static DuelBots Of(CombatDuelComponent duel) {
        if (duel.BotState is DuelBots mine) {
            return mine;
        }

        var made = new DuelBots(duel);
        duel.BotState = made;

        return made;
    }

    // ---------------------------------------------------------------- the battle's hooks (BotSide)

    internal void PlanningBegins() {
        // How many enemies the server's own wizards face, for the crowd share their decks plan for.
        BotFightSizes();
        // Bot Players think once the real players have picked.
        BotsPlanningBegins();
        // The Quest Team's picks are made here too.
        QuestTeamPlanningBegins();
        // Ultimus's picks too.
        UltimusPlanningBegins();
    }

    internal void NoticeMove(CombatDuelSubCircle caster) {
        BotsNoticeMove(caster);
        QuestTeamNoticeMove(caster);
        UltimusNoticeMove(caster);
    }

    internal void JoinedPlanning(CombatDuelSubCircle circle, double secondsLeft) => UltimusJoinedPlanning(circle, secondsLeft);

    // The Quest Team never joins a battle with someone else's wizard in it, and Ultimus and the Quest Team never share one.
    internal bool RefusesJoiner(CoreObject joiner) => QuestTeamRefusesBattle(joiner) || UltimusRefusesBattle(joiner);

    internal CombatDuelSubCircle OwnCircle(CoreObject joiner) => QuestTeamOwnCircle(joiner);

    /// <summary>A message only the bots understand, handed on by the battle (BotDoorMessage).</summary>
    internal void Receive(object payload) {
        switch (payload) {
            case BotPlayers.BotJoinBattle m: ReceiveBotJoinBattle(m); break;
            case BotPlayers.BotKickFromBattle m: ReceiveBotKickFromBattle(m); break;
            case BotThink m: ReceiveBotThink(m); break;
            case BotClick m: ReceiveBotClick(m); break;
            case QuestTeamThink m: ReceiveQuestTeamThink(m); break;
            case UltimusThink m: ReceiveUltimusThink(m); break;
        }
    }

}
