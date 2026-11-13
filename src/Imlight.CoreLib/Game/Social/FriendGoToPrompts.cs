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
 * FRIEND GO TO PROMPTS
 * ========================================================================
 *
 * PURPOSE:
 * Remembers who was asked "Do you want to go to your friend?" after picking a friend to teleport to (Niduus,
 * 2026-11-13: one click on a friend sometimes teleported). The game answers that box with MSG_GotoPlayerConfirm, the
 * same message a sigil's group question gets, so FriendsService takes the answers it asked for and GroupService
 * leaves them alone.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/13/2026
 */

using System;
using System.Collections.Concurrent;

namespace Imlight.CoreLib.Game.Social;

internal static class FriendGoToPrompts {

    // How long a Yes still counts after the question, and how long a taken answer is still known as a friend answer
    // (the two services see the same message one after the other).
    private static readonly TimeSpan s_answerTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_takenTime = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<ulong, (ulong Target, DateTime At)> s_asked = new();
    private static readonly ConcurrentDictionary<ulong, (ulong Target, DateTime At)> s_taken = new();

    /// <summary>Notes that this wizard was asked about going to this friend; a newer question replaces an older one.</summary>
    internal static void Asked(ulong charId, ulong targetCharId) {
        s_asked[charId] = (targetCharId, DateTime.UtcNow);
        s_taken.TryRemove(charId, out _);
    }

    /// <summary>True once, for a Yes to the last question asked, in time.</summary>
    internal static bool TryTake(ulong charId, ulong targetCharId) {
        if (!s_asked.TryGetValue(charId, out var asked) || asked.Target != targetCharId) {
            return false;
        }

        if (!s_asked.TryRemove(new(charId, asked))) {
            return false;
        }

        if (DateTime.UtcNow - asked.At > s_answerTime) {
            return false;
        }

        s_taken[charId] = (targetCharId, DateTime.UtcNow);

        return true;
    }

    /// <summary>Whether a MSG_GotoPlayerConfirm about this friend answers the friend question (asked or just taken).</summary>
    internal static bool IsFriendAnswer(ulong charId, ulong targetCharId) {
        var now = DateTime.UtcNow;

        return (s_asked.TryGetValue(charId, out var asked) && asked.Target == targetCharId && now - asked.At <= s_answerTime)
            || (s_taken.TryGetValue(charId, out var taken) && taken.Target == targetCharId && now - taken.At <= s_takenTime);
    }

}
