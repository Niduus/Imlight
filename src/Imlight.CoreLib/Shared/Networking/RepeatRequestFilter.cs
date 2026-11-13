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
 *
 * ========================================================================
 * REPEAT REQUEST FILTER
 * ========================================================================
 *
 * PURPOSE:
 * Swallows the copies of one "where can I get this item" question (MSG_GETITEMINFO) that the game sends in a burst.
 *
 * USAGE EXAMPLE:
 * if (filter.IsRepeat(message.ItemID, DateTime.UtcNow)) { continue; }
 *
 * NOTE:
 * Niduus, 2026-11-13 (v0.10.848): spam-clicking "Find where to get this item" disconnected him, then put him back.
 * His server log shows why: every click made the game send one more copy of the question than the click before
 * (1, 2, 3 ... 160 copies of item 1553369 a click, a millisecond apart), and the copies stayed after he reconnected.
 * Over 600 a second emptied the rate limit for 10 s, and the flood guard closed the session (SocketListener). The
 * game's own window is like that; the server cannot change it, so it answers the first copy of a burst and lets the
 * rest go without counting them against the rate limit. One answer reaches every copy of the window.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/13/2026
 */

using System;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>
/// Tells the copies of a repeated request apart from a new one, for one session.
/// </summary>
internal sealed class RepeatRequestFilter {

    /// <summary>A copy that comes this soon after the one before it belongs to the same burst.</summary>
    internal static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(100);

    /// <summary>Even a burst that never stops gets one answer this often, so a newly opened window still fills in.</summary>
    internal static readonly TimeSpan AnswerAtLeastEvery = TimeSpan.FromMilliseconds(250);

    private bool _any;
    private uint _lastKey;
    private DateTime _lastSeenUtc;
    private DateTime _lastAnsweredUtc;

    /// <summary>How many copies were let go since the last <see cref="TakeSwallowed"/>.</summary>
    internal int Swallowed { get; private set; }

    /// <summary>
    /// Whether this request only repeats the one just before it.
    /// </summary>
    /// <param name="key">What is asked about (the item's template id).</param>
    /// <param name="nowUtc">When it came.</param>
    /// <returns>True to let it go unanswered.</returns>
    internal bool IsRepeat(uint key, DateTime nowUtc) {
        var repeat = _any && key == _lastKey && nowUtc - _lastSeenUtc < BurstGap && nowUtc - _lastAnsweredUtc < AnswerAtLeastEvery;

        _any = true;
        _lastKey = key;
        _lastSeenUtc = nowUtc;
        if (repeat) {
            Swallowed++;
        } else {
            _lastAnsweredUtc = nowUtc;
        }

        return repeat;
    }

    /// <summary>Returns <see cref="Swallowed"/> and starts counting again.</summary>
    internal int TakeSwallowed() {
        var count = Swallowed;
        Swallowed = 0;

        return count;
    }
}
