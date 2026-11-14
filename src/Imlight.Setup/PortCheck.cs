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
 */

using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Whether something listens on a port of this PC. A try that runs out of time is given up quietly: its unfinished
/// connect ends with "The I/O operation has been aborted" when the socket closes, and nothing waited for it, so it was
/// written in Ahziael's error log as "An error in the background" each time the server started or stopped (a tester's
/// log, 2026-11-13: six of them in one evening, all harmless).
/// </summary>
internal static class PortCheck {

    internal static bool IsListening(int port, TimeSpan wait) {
        try {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            // Read the outcome whenever it comes, so a late failure is never left for the error log.
            _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            return connect.Wait(wait) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException) {
            return false;
        }
    }

}
