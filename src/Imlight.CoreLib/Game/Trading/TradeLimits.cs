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
 * ========================================================================
 * TRADE SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * The numeric limits of a treasure card trade.
 * 
 * USAGE EXAMPLE:
 * if (offer.Count >= TradeLimits.MaxDistinctCardsPerSide) { ... }
 * 
 * NOTE:
 * The two constants are client rules, not game data. The invite timeout is a server policy value
 * read from the Game Server section of Imlight.ini; the escrow step timeout is a fixed server value.
 * 
 * TODO:
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Trading;

internal static class TradeLimits {

    private const ushort DefaultInviteTimeoutSeconds = 60;
    private const ushort ApplyStepTimeoutSeconds = 15;

    // Client rule: TradeWindow::Handle_LocalPlayerSpellVaultList refuses a fifth distinct card.
    internal const int MaxDistinctCardsPerSide = 4;

    // Client rule: has_room_for_items allows this many non-CastleMagic cards in the treasure book.
    internal const int TreasureBookCapacity = 999;

    // Server policy: how long the directory waits for a session to finish one escrow step.
    internal static TimeSpan ApplyStepTimeout { get; } = TimeSpan.FromSeconds(ApplyStepTimeoutSeconds);

    internal static TimeSpan InviteTimeout { get; } = TimeSpan.FromSeconds(ReadInviteTimeoutSeconds());

    private static ushort ReadInviteTimeoutSeconds() {
        var seconds = ConfigurationManager.Settings["Game Server.TradeInviteTimeoutSeconds"].AsUShort(DefaultInviteTimeoutSeconds);

        // A missing or blank key reads as 0, and an invite that expires at once is never wanted.
        return seconds == 0 ? DefaultInviteTimeoutSeconds : seconds;
    }

}
