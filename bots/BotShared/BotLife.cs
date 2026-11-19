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

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The life of this copy of the bots: the server can swap the bots for a newer build without a restart (BotDoor), so every
/// loop and timer of theirs ends when <see cref="Ended"/> is set. A copy that was swapped out must never keep playing.
/// </summary>
internal static class BotLife {

    private static readonly CancellationTokenSource s_end = new();

    /// <summary>Set when these bots are being swapped out.</summary>
    internal static CancellationToken Ended => s_end.Token;

    /// <summary>Whether these bots are being swapped out.</summary>
    internal static bool Over => s_end.IsCancellationRequested;

    /// <summary>Ends every loop and timer of these bots.</summary>
    internal static void End() {
        try {
            s_end.Cancel();
        }
        catch (ObjectDisposedException) {
            // Already ended.
        }
    }

    /// <summary>Waits, or returns early (false) once these bots are being swapped out.</summary>
    internal static async Task<bool> Wait(TimeSpan time) {
        try {
            await Task.Delay(time, s_end.Token);

            return true;
        }
        catch (OperationCanceledException) {
            return false;
        }
    }

}
