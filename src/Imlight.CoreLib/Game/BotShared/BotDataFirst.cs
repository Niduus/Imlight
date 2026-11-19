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

using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Server data first (Niduus, 2026-11-19: "AI chats taking over primary of the Bots custom LM's we made for them. This should
/// always be in reverse. The bots have access to all data the server has, it should consult that instead of AI chat unless it
/// is at a road block"). For Ultimus, the TCO7 and the Quest Team alike:
/// chat: the real game's data (UltimusLookup), then the bot's own answer from its real facts (its keyword chat), then what it
/// learned from the AI before (UltimusAiLearned), and only when none of those knows anything, the AI.
/// stuck: what worked at the spot before (stuck solutions, the watchdog book, the bots' lessons and mistakes), then the bot's
/// own round of unstuck moves; the AI thinks only once every own move was tried at the spot and none worked (a roadblock).
/// [Ultimus] ServerDataFirst (default true) turns it off, back to the AI wording every answer. Applies at once.
/// </summary>
internal static class BotDataFirst {

    /// <summary>On unless [Ultimus] ServerDataFirst is false. Read live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Ultimus.ServerDataFirst", true);

    /// <summary>Stuck alarms at one spot on one step before the AI may think, when the bot's own data has nothing.</summary>
    internal const int ROADBLOCK_ALARMS = 3;

    /// <summary>
    /// Whether a stuck spot is a roadblock the AI may think about: the bot's own moves there are used up (what its records
    /// said to try included), or nothing it wrote down knows a way out (<paramref name="dataKnowsAFix"/> false) and it has
    /// been stuck there often.
    /// </summary>
    internal static bool Roadblock(bool dataKnowsAFix, bool ownMovesUsedUp, int alarms)
        => !On || ownMovesUsedUp || (!dataKnowsAFix && alarms >= ROADBLOCK_ALARMS);

}
